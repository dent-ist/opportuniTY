using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Search.SavedSearches;
using Opportunity.Contracts.Search;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Search;

/// <summary>
/// PostgreSQL <see cref="ISavedSearchStore"/> over <c>saved_search</c>, <c>saved_search_share</c> and
/// <c>saved_search_folder</c> (V0028, tenant tables under forced RLS). Visibility is one SQL predicate (owner, a share
/// naming the user or one of their groups, or everything for Workspace Admins); lists and reads load shares for the
/// whole page in one statement. Writes lock the row, check the version and insert their audit event in the same
/// transaction.
/// </summary>
public sealed class SavedSearchStore(NpgsqlDataSource dataSource) : ISavedSearchStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Select =
        """
        SELECT s.saved_search_id, s.name, s.folder_id, s.owner_id, coalesce(u.display_name, u.email, s.owner_id::text), s.query_text,
               s.ast_version, s.columns::text, s.sort::text, s.include_family, s.referenced_ids, s.created_at, s.modified_at, s.version,
               s.last_run_at, s.last_hit_count, s.last_hit_exact, s.last_run_current, s.last_run_generation
          FROM opportunity.saved_search s
          LEFT JOIN opportunity.app_user u ON u.user_id = s.owner_id
        """;

    /// <summary>The visibility predicate over alias <c>s</c>; parameters <c>@all</c>, <c>@user</c>, <c>@groups</c>.</summary>
    private const string Visible =
        """
        (@all OR s.owner_id = @user OR EXISTS (
            SELECT FROM opportunity.saved_search_share sh
             WHERE sh.workspace_id = s.workspace_id AND sh.saved_search_id = s.saved_search_id
               AND (sh.user_id = @user OR sh.group_name = ANY (@groups))))
        """;

    public async Task<SavedSearchRecord?> GetAsync(
        Guid workspaceId, Guid savedSearchId, SavedSearchViewer? viewer, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var found = await ReadAsync(tx, workspaceId, $"{Select} WHERE s.workspace_id = @ws AND s.saved_search_id = @id AND {Visible}", viewer,
            p => p.AddWithValue("id", savedSearchId), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found.FirstOrDefault();
    }

    public async Task<SavedSearchPage> ListAsync(
        Guid workspaceId, SavedSearchViewer viewer, SavedSearchListFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(filter.Limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var sql =
            $"""
            {Select}
             WHERE s.workspace_id = @ws AND {Visible}
               AND (NOT @byFolder OR s.folder_id IS NOT DISTINCT FROM @folder)
               AND (@name::text IS NULL OR s.name ILIKE '%' || @name || '%' ESCAPE '\')
               AND (@afterName::text IS NULL OR (s.name, s.saved_search_id) > (@afterName, @afterId))
             ORDER BY s.name, s.saved_search_id
             LIMIT @take
            """;
        var items = await ReadAsync(tx, workspaceId, sql, viewer, p =>
        {
            p.AddWithValue("byFolder", filter.FolderId is not null);
            p.Add(new NpgsqlParameter("folder", NpgsqlDbType.Uuid) { Value = filter.FolderId is { } f && f != Guid.Empty ? f : DBNull.Value });
            p.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = (object?)EscapeLike(filter.NameContains) ?? DBNull.Value });
            p.Add(new NpgsqlParameter("afterName", NpgsqlDbType.Text) { Value = (object?)filter.After?.Name ?? DBNull.Value });
            p.Add(new NpgsqlParameter("afterId", NpgsqlDbType.Uuid) { Value = (object?)filter.After?.SavedSearchId ?? DBNull.Value });
            p.AddWithValue("take", filter.Limit + 1);
        }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SavedSearchPage([.. items.Take(filter.Limit)], items.Count > filter.Limit);
    }

    public async Task<IReadOnlyDictionary<Guid, SavedSearchCriteria>> GetCriteriaAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> savedSearchIds, SavedSearchViewer? viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(savedSearchIds);
        var result = new Dictionary<Guid, SavedSearchCriteria>();
        if (savedSearchIds.Count == 0)
        {
            return result;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            $"""
            SELECT s.saved_search_id, s.name, s.query_text, s.referenced_ids FROM opportunity.saved_search s
             WHERE s.workspace_id = @ws AND s.saved_search_id = ANY (@ids) AND {Visible}
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", savedSearchIds.ToArray());
            BindViewer(command.Parameters, viewer);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                result[id] = new SavedSearchCriteria(id, reader.GetString(1), reader.GetString(2), reader.GetFieldValue<Guid[]>(3));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<SavedSearchWriteResult> CreateAsync(
        Guid workspaceId, Guid savedSearchId, Guid ownerId, SavedSearchDefinition definition, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (!await FolderExistsAsync(tx, workspaceId, definition.FolderId, cancellationToken).ConfigureAwait(false))
        {
            return new SavedSearchWriteResult(SavedSearchWriteStatus.FolderNotFound);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.saved_search (workspace_id, saved_search_id, name, folder_id, owner_id, query_text, ast_version, columns,
                                                  sort, include_family, referenced_ids)
            VALUES (@ws, @id, @name, @folder, @owner, @query, @astVersion, @columns::jsonb, @sort::jsonb, @family, @refs)
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("id", savedSearchId);
            insert.Parameters.AddWithValue("owner", ownerId);
            BindDefinition(insert.Parameters, definition);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await FinishAsync(tx, workspaceId, savedSearchId, audit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedSearchWriteResult> UpdateAsync(
        Guid workspaceId, Guid savedSearchId, long? expectedVersion, SavedSearchDefinition definition, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await LockAsync(tx, workspaceId, savedSearchId, expectedVersion, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        if (!await FolderExistsAsync(tx, workspaceId, definition.FolderId, cancellationToken).ConfigureAwait(false))
        {
            return new SavedSearchWriteResult(SavedSearchWriteStatus.FolderNotFound);
        }

        await using (var update = tx.Command(
            """
            UPDATE opportunity.saved_search
               SET name = @name, folder_id = @folder, query_text = @query, ast_version = @astVersion, columns = @columns::jsonb,
                   sort = @sort::jsonb, include_family = @family, referenced_ids = @refs, version = version + 1, modified_at = now()
             WHERE workspace_id = @ws AND saved_search_id = @id
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", savedSearchId);
            BindDefinition(update.Parameters, definition);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await FinishAsync(tx, workspaceId, savedSearchId, audit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedSearchWriteResult> DeleteAsync(
        Guid workspaceId, Guid savedSearchId, long? expectedVersion, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await LockAsync(tx, workspaceId, savedSearchId, expectedVersion, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        await using (var delete = tx.Command("DELETE FROM opportunity.saved_search WHERE workspace_id = @ws AND saved_search_id = @id"))
        {
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("id", savedSearchId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SavedSearchWriteResult(SavedSearchWriteStatus.Ok);
    }

    public async Task<SavedSearchWriteResult> SetSharingAsync(
        Guid workspaceId, Guid savedSearchId, long? expectedVersion, IReadOnlyList<SavedSearchShareTarget> shares, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(audit);
        var users = shares.Where(s => s.Kind == SavedSearchShareKind.User).Select(s => Guid.Parse(s.Id)).Distinct().ToArray();
        var groups = shares.Where(s => s.Kind == SavedSearchShareKind.Group).Select(s => s.Id).Distinct(StringComparer.Ordinal).ToArray();
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await LockAsync(tx, workspaceId, savedSearchId, expectedVersion, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        if (users.Length > 0)
        {
            await using var known = tx.Command("SELECT count(*) FROM opportunity.app_user WHERE user_id = ANY (@users)");
            known.Parameters.AddWithValue("users", users);
            if ((long)(await known.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != users.Length)
            {
                return new SavedSearchWriteResult(SavedSearchWriteStatus.UnknownUser);
            }
        }

        await using (var replace = tx.Command(
            """
            DELETE FROM opportunity.saved_search_share WHERE workspace_id = @ws AND saved_search_id = @id;

            INSERT INTO opportunity.saved_search_share (workspace_id, saved_search_id, share_id, user_id, group_name)
            SELECT @ws, @id, gen_random_uuid(), u, NULL FROM unnest(@users::uuid[]) AS u
            UNION ALL
            SELECT @ws, @id, gen_random_uuid(), NULL, g FROM unnest(@groups::text[]) AS g;

            UPDATE opportunity.saved_search SET version = version + 1, modified_at = now()
             WHERE workspace_id = @ws AND saved_search_id = @id;
            """))
        {
            replace.Parameters.AddWithValue("ws", workspaceId);
            replace.Parameters.AddWithValue("id", savedSearchId);
            replace.Parameters.AddWithValue("users", users);
            replace.Parameters.AddWithValue("groups", groups);
            await replace.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await FinishAsync(tx, workspaceId, savedSearchId, audit, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordRunAsync(Guid workspaceId, Guid savedSearchId, SavedSearchLastRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var update = tx.Command(
            """
            UPDATE opportunity.saved_search
               SET last_run_at = @at, last_hit_count = @hits, last_hit_exact = @exact, last_run_current = @current, last_run_generation = @generation
             WHERE workspace_id = @ws AND saved_search_id = @id AND (last_run_at IS NULL OR last_run_at <= @at)
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", savedSearchId);
            update.Parameters.AddWithValue("at", run.At);
            update.Parameters.AddWithValue("hits", run.HitCount);
            update.Parameters.AddWithValue("exact", run.Exact);
            update.Parameters.Add(new NpgsqlParameter("current", NpgsqlDbType.Boolean) { Value = (object?)run.Current ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("generation", NpgsqlDbType.Bigint) { Value = (object?)run.ServedGeneration ?? DBNull.Value });
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SavedSearchShareResource>> ListShareCandidatesAsync(
        Guid workspaceId, string? nameContains, Guid excludeUserId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            WITH assigned AS (
                SELECT user_id, group_name FROM opportunity.workspace_role_assignment WHERE workspace_id = @ws),
            groups AS (
                SELECT DISTINCT group_name FROM assigned WHERE group_name IS NOT NULL),
            users AS (
                SELECT u.user_id, coalesce(u.display_name, u.email, u.user_id::text) AS name, u.email
                  FROM opportunity.app_user u
                 WHERE u.user_id <> @me
                   AND (u.user_id IN (SELECT user_id FROM assigned WHERE user_id IS NOT NULL)
                        OR u.groups && ARRAY(SELECT group_name FROM groups)))
            SELECT kind, id, name FROM (
                SELECT 0 AS kind, group_name AS id, group_name AS name FROM groups
                 WHERE @q::text IS NULL OR group_name ILIKE '%' || @q || '%' ESCAPE '\'
                UNION ALL
                SELECT 1, user_id::text, name FROM users
                 WHERE @q::text IS NULL OR name ILIKE '%' || @q || '%' ESCAPE '\' OR email ILIKE '%' || @q || '%' ESCAPE '\') c
             ORDER BY lower(name), kind, id
             LIMIT @take
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("me", excludeUserId);
        command.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = (object?)EscapeLike(nameContains) ?? DBNull.Value });
        command.Parameters.AddWithValue("take", limit);
        var items = new List<SavedSearchShareResource>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new SavedSearchShareResource(
                    reader.GetInt32(0) == 0 ? SavedSearchShareKind.Group : SavedSearchShareKind.User, reader.GetString(1), reader.GetString(2)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return items;
    }

    public async Task<IReadOnlyList<SavedSearchFolderRecord>> ListFoldersAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var folders = await ReadFoldersAsync(tx, workspaceId, null, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return folders;
    }

    public async Task<SavedSearchFolderRecord?> GetFolderAsync(Guid workspaceId, Guid folderId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var folders = await ReadFoldersAsync(tx, workspaceId, folderId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return folders.FirstOrDefault();
    }

    public async Task<SavedSearchWriteResult> CreateFolderAsync(
        Guid workspaceId, Guid folderId, string name, Guid? parentFolderId, Guid createdBy, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (!await FolderExistsAsync(tx, workspaceId, parentFolderId, cancellationToken).ConfigureAwait(false))
        {
            return new SavedSearchWriteResult(SavedSearchWriteStatus.FolderNotFound);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.saved_search_folder (workspace_id, folder_id, name, parent_folder_id, created_by)
            VALUES (@ws, @id, @name, @parent, @by)
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("id", folderId);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parentFolderId ?? DBNull.Value });
            insert.Parameters.AddWithValue("by", createdBy);
            if (!await TryExecuteAsync(insert, cancellationToken).ConfigureAwait(false))
            {
                return new SavedSearchWriteResult(SavedSearchWriteStatus.DuplicateName);
            }
        }

        var folder = (await ReadFoldersAsync(tx, workspaceId, folderId, cancellationToken).ConfigureAwait(false))[0];
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SavedSearchWriteResult(SavedSearchWriteStatus.Ok, Folder: folder);
    }

    public async Task<SavedSearchWriteResult> UpdateFolderAsync(
        Guid workspaceId, Guid folderId, long? expectedVersion, string name, Guid? parentFolderId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await LockFolderAsync(tx, workspaceId, folderId, expectedVersion, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        if (!await FolderExistsAsync(tx, workspaceId, parentFolderId, cancellationToken).ConfigureAwait(false))
        {
            return new SavedSearchWriteResult(SavedSearchWriteStatus.FolderNotFound);
        }

        if (parentFolderId is { } parent)
        {
            // The new parent must not be the folder itself or one of its descendants.
            await using var cycle = tx.Command(
                """
                WITH RECURSIVE below AS (
                    SELECT folder_id FROM opportunity.saved_search_folder WHERE workspace_id = @ws AND folder_id = @id
                    UNION
                    SELECT f.folder_id FROM opportunity.saved_search_folder f JOIN below b ON f.parent_folder_id = b.folder_id
                     WHERE f.workspace_id = @ws)
                SELECT EXISTS (SELECT FROM below WHERE folder_id = @parent)
                """);
            cycle.Parameters.AddWithValue("ws", workspaceId);
            cycle.Parameters.AddWithValue("id", folderId);
            cycle.Parameters.AddWithValue("parent", parent);
            if (await cycle.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                return new SavedSearchWriteResult(SavedSearchWriteStatus.FolderCycle);
            }
        }

        await using (var update = tx.Command(
            """
            UPDATE opportunity.saved_search_folder SET name = @name, parent_folder_id = @parent, version = version + 1, modified_at = now()
             WHERE workspace_id = @ws AND folder_id = @id
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", folderId);
            update.Parameters.AddWithValue("name", name);
            update.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parentFolderId ?? DBNull.Value });
            if (!await TryExecuteAsync(update, cancellationToken).ConfigureAwait(false))
            {
                return new SavedSearchWriteResult(SavedSearchWriteStatus.DuplicateName);
            }
        }

        var folder = (await ReadFoldersAsync(tx, workspaceId, folderId, cancellationToken).ConfigureAwait(false))[0];
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SavedSearchWriteResult(SavedSearchWriteStatus.Ok, Folder: folder);
    }

    public async Task<SavedSearchWriteResult> DeleteFolderAsync(Guid workspaceId, Guid folderId, long? expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await LockFolderAsync(tx, workspaceId, folderId, expectedVersion, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        await using (var used = tx.Command(
            """
            SELECT EXISTS (SELECT FROM opportunity.saved_search_folder WHERE workspace_id = @ws AND parent_folder_id = @id)
                OR EXISTS (SELECT FROM opportunity.saved_search WHERE workspace_id = @ws AND folder_id = @id)
            """))
        {
            used.Parameters.AddWithValue("ws", workspaceId);
            used.Parameters.AddWithValue("id", folderId);
            if (await used.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                return new SavedSearchWriteResult(SavedSearchWriteStatus.FolderNotEmpty);
            }
        }

        await using (var delete = tx.Command("DELETE FROM opportunity.saved_search_folder WHERE workspace_id = @ws AND folder_id = @id"))
        {
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("id", folderId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SavedSearchWriteResult(SavedSearchWriteStatus.Ok);
    }

    private static async Task<SavedSearchWriteResult> FinishAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid savedSearchId, AuditEvent audit, CancellationToken cancellationToken)
    {
        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = await ReadAsync(tx, workspaceId, $"{Select} WHERE s.workspace_id = @ws AND s.saved_search_id = @id AND {Visible}", null,
            p => p.AddWithValue("id", savedSearchId), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SavedSearchWriteResult(SavedSearchWriteStatus.Ok, saved[0]);
    }

    /// <summary>Locks the row; a missing row or a stale version is the failed result.</summary>
    private static async Task<SavedSearchWriteResult?> LockAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid savedSearchId, long? expectedVersion, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            "SELECT version FROM opportunity.saved_search WHERE workspace_id = @ws AND saved_search_id = @id FOR UPDATE");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", savedSearchId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            long version when expectedVersion is null || version == expectedVersion => null,
            long => new SavedSearchWriteResult(SavedSearchWriteStatus.VersionConflict),
            _ => new SavedSearchWriteResult(SavedSearchWriteStatus.NotFound),
        };
    }

    private static async Task<SavedSearchWriteResult?> LockFolderAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid folderId, long? expectedVersion, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            "SELECT version FROM opportunity.saved_search_folder WHERE workspace_id = @ws AND folder_id = @id FOR UPDATE");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", folderId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            long version when expectedVersion is null || version == expectedVersion => null,
            long => new SavedSearchWriteResult(SavedSearchWriteStatus.VersionConflict),
            _ => new SavedSearchWriteResult(SavedSearchWriteStatus.NotFound),
        };
    }

    private static async Task<bool> FolderExistsAsync(WorkspaceTransaction tx, Guid workspaceId, Guid? folderId, CancellationToken cancellationToken)
    {
        if (folderId is not { } id)
        {
            return true;
        }

        await using var command = tx.Command(
            "SELECT EXISTS (SELECT FROM opportunity.saved_search_folder WHERE workspace_id = @ws AND folder_id = @id)");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>Runs a statement inside a savepoint; false when it violated a unique constraint.</summary>
    private static async Task<bool> TryExecuteAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await command.Transaction!.SaveAsync("unique_name", cancellationToken).ConfigureAwait(false);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await command.Transaction.ReleaseAsync("unique_name", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await command.Transaction.RollbackAsync("unique_name", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<List<SavedSearchFolderRecord>> ReadFoldersAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid? folderId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            SELECT folder_id, name, parent_folder_id, created_by, version FROM opportunity.saved_search_folder
             WHERE workspace_id = @ws AND (@id::uuid IS NULL OR folder_id = @id)
             ORDER BY lower(name), folder_id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = (object?)folderId ?? DBNull.Value });
        var folders = new List<SavedSearchFolderRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            folders.Add(new SavedSearchFolderRecord(
                workspaceId, reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetGuid(3), reader.GetInt64(4)));
        }

        return folders;
    }

    /// <summary>Reads searches with <paramref name="sql"/> (parameters @ws, the viewer's, and <paramref name="bind"/>), then their shares in one statement.</summary>
    private static async Task<List<SavedSearchRecord>> ReadAsync(
        WorkspaceTransaction tx, Guid workspaceId, string sql, SavedSearchViewer? viewer, Action<NpgsqlParameterCollection> bind,
        CancellationToken cancellationToken)
    {
        var searches = new List<SavedSearchRecord>();
        await using (var command = tx.Command(sql))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            BindViewer(command.Parameters, viewer);
            bind(command.Parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                searches.Add(new SavedSearchRecord
                {
                    WorkspaceId = workspaceId,
                    SavedSearchId = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    FolderId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    OwnerId = reader.GetGuid(3),
                    OwnerDisplayName = reader.GetString(4),
                    QueryText = reader.GetString(5),
                    AstVersion = reader.GetInt32(6),
                    Columns = JsonSerializer.Deserialize<string[]>(reader.GetString(7), Json) ?? [],
                    Sort = JsonSerializer.Deserialize<StoredSort[]>(reader.GetString(8), Json)?.Select(s => s.ToKey()).ToList() ?? [],
                    IncludeFamily = reader.GetBoolean(9),
                    References = reader.GetFieldValue<Guid[]>(10),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
                    ModifiedAt = reader.GetFieldValue<DateTimeOffset>(12),
                    Version = reader.GetInt64(13),
                    LastRun = reader.IsDBNull(14)
                        ? null
                        : new SavedSearchLastRun(
                            reader.GetFieldValue<DateTimeOffset>(14),
                            reader.GetInt64(15),
                            reader.GetBoolean(16),
                            reader.IsDBNull(17) ? null : reader.GetBoolean(17),
                            reader.IsDBNull(18) ? null : reader.GetInt64(18)),
                });
            }
        }

        if (searches.Count == 0)
        {
            return searches;
        }

        var shares = new Dictionary<Guid, List<SavedSearchShareResource>>();
        await using (var command = tx.Command(
            """
            SELECT sh.saved_search_id, sh.user_id, sh.group_name, coalesce(u.display_name, u.email, sh.user_id::text, sh.group_name)
              FROM opportunity.saved_search_share sh
              LEFT JOIN opportunity.app_user u ON u.user_id = sh.user_id
             WHERE sh.workspace_id = @ws AND sh.saved_search_id = ANY (@ids)
             ORDER BY sh.saved_search_id, sh.group_name NULLS FIRST, 4, sh.share_id
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", searches.Select(s => s.SavedSearchId).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                if (!shares.TryGetValue(id, out var list))
                {
                    shares[id] = list = [];
                }

                list.Add(reader.IsDBNull(1)
                    ? new SavedSearchShareResource(SavedSearchShareKind.Group, reader.GetString(2), reader.GetString(3))
                    : new SavedSearchShareResource(SavedSearchShareKind.User, reader.GetGuid(1).ToString(), reader.GetString(3)));
            }
        }

        return [.. searches.Select(s => shares.TryGetValue(s.SavedSearchId, out var list) ? s with { Shares = list } : s)];
    }

    private static void BindViewer(NpgsqlParameterCollection parameters, SavedSearchViewer? viewer)
    {
        parameters.AddWithValue("all", viewer is null || viewer.SeesAll);
        parameters.AddWithValue("user", viewer?.UserId ?? Guid.Empty);
        parameters.AddWithValue("groups", viewer?.Groups.ToArray() ?? []);
    }

    private static void BindDefinition(NpgsqlParameterCollection parameters, SavedSearchDefinition definition)
    {
        parameters.AddWithValue("name", definition.Name);
        parameters.Add(new NpgsqlParameter("folder", NpgsqlDbType.Uuid) { Value = (object?)definition.FolderId ?? DBNull.Value });
        parameters.AddWithValue("query", definition.QueryText);
        parameters.AddWithValue("astVersion", definition.AstVersion);
        parameters.AddWithValue("columns", JsonSerializer.Serialize(definition.Columns, Json));
        parameters.AddWithValue("sort", JsonSerializer.Serialize(definition.Sort.Select(StoredSort.From), Json));
        parameters.AddWithValue("family", definition.IncludeFamily);
        parameters.AddWithValue("refs", definition.References.Distinct().ToArray());
    }

    private static string? EscapeLike(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Stored sort key: <c>{"field": "controlNumber", "direction": "asc"}</c>.</summary>
    private sealed record StoredSort(string Field, string Direction)
    {
        public static StoredSort From(SearchSortKey key) => new(key.Field, key.Direction == SearchSortDirection.Desc ? "desc" : "asc");

        public SearchSortKey ToKey() =>
            new(Field, string.Equals(Direction, "desc", StringComparison.OrdinalIgnoreCase) ? SearchSortDirection.Desc : SearchSortDirection.Asc);
    }
}

public static class SavedSearchStoreRegistration
{
    /// <summary>Registers the PostgreSQL <see cref="ISavedSearchStore"/> (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresSavedSearchStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISavedSearchStore, SavedSearchStore>();
        return services;
    }
}
