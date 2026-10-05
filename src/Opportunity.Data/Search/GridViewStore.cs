using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Search.GridViews;
using Opportunity.Contracts.Search;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Search;

/// <summary>
/// PostgreSQL <see cref="IGridViewStore"/> over <c>grid_view</c> and <c>grid_layout</c> (V0034, tenant tables under
/// forced RLS). Visibility is one predicate (shared, owned, or everything for Workspace Admins). Writes lock the row,
/// check the version, and insert their audit event (shared views) in the same transaction; a name taken by another view
/// of the same scope is reported, not raised.
/// </summary>
public sealed class GridViewStore(NpgsqlDataSource dataSource) : IGridViewStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Select =
        """
        SELECT v.view_id, v.name, v.owner_id, coalesce(u.display_name, u.email, v.owner_id::text), v.shared, v.columns::text, v.sort::text,
               v.created_at, v.modified_at, v.version
          FROM opportunity.grid_view v
          LEFT JOIN opportunity.app_user u ON u.user_id = v.owner_id
        """;

    private const string Visible = "(@all OR v.shared OR v.owner_id = @user)";

    public async Task<IReadOnlyList<GridViewRecord>> ListAsync(Guid workspaceId, GridViewViewer viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var views = await ReadAsync(tx, workspaceId, $"{Select} WHERE v.workspace_id = @ws AND {Visible} ORDER BY v.shared DESC, lower(v.name), v.view_id",
            viewer, _ => { }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return views;
    }

    public async Task<GridViewRecord?> GetAsync(Guid workspaceId, Guid viewId, GridViewViewer viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var views = await ReadAsync(tx, workspaceId, $"{Select} WHERE v.workspace_id = @ws AND v.view_id = @id AND {Visible}", viewer,
            p => p.AddWithValue("id", viewId), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return views.FirstOrDefault();
    }

    public async Task<GridViewWriteResult> CreateAsync(
        Guid workspaceId, Guid viewId, Guid ownerId, GridViewDefinition definition, AuditEvent? audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var count = tx.Command(
            "SELECT count(*) FROM opportunity.grid_view WHERE workspace_id = @ws AND shared = @shared AND (@shared OR owner_id = @owner)"))
        {
            count.Parameters.AddWithValue("ws", workspaceId);
            count.Parameters.AddWithValue("shared", definition.Shared);
            count.Parameters.AddWithValue("owner", ownerId);
            if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= GridViewLimits.MaxViews)
            {
                return new GridViewWriteResult(GridViewWriteStatus.LimitReached);
            }
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.grid_view (workspace_id, view_id, name, owner_id, shared, columns, sort, modified_by)
            VALUES (@ws, @id, @name, @owner, @shared, @columns::jsonb, @sort::jsonb, @owner)
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("id", viewId);
            insert.Parameters.AddWithValue("owner", ownerId);
            BindDefinition(insert.Parameters, definition);
            if (!await TryExecuteAsync(insert, cancellationToken).ConfigureAwait(false))
            {
                return new GridViewWriteResult(GridViewWriteStatus.DuplicateName);
            }
        }

        return await FinishAsync(tx, workspaceId, viewId, audit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GridViewWriteResult> UpdateAsync(
        Guid workspaceId, Guid viewId, long? expectedVersion, Guid modifiedBy, GridViewDefinition definition, AuditEvent? audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await LockAsync(tx, workspaceId, viewId, expectedVersion, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        await using (var update = tx.Command(
            """
            UPDATE opportunity.grid_view
               SET name = @name, shared = @shared, columns = @columns::jsonb, sort = @sort::jsonb, modified_by = @by,
                   version = version + 1, modified_at = now()
             WHERE workspace_id = @ws AND view_id = @id
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", viewId);
            update.Parameters.AddWithValue("by", modifiedBy);
            BindDefinition(update.Parameters, definition);
            if (!await TryExecuteAsync(update, cancellationToken).ConfigureAwait(false))
            {
                return new GridViewWriteResult(GridViewWriteStatus.DuplicateName);
            }
        }

        return await FinishAsync(tx, workspaceId, viewId, audit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GridViewWriteResult> DeleteAsync(
        Guid workspaceId, Guid viewId, long? expectedVersion, AuditEvent? audit, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await LockAsync(tx, workspaceId, viewId, expectedVersion, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        await using (var delete = tx.Command("DELETE FROM opportunity.grid_view WHERE workspace_id = @ws AND view_id = @id"))
        {
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("id", viewId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (audit is not null)
        {
            await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GridViewWriteResult(GridViewWriteStatus.Ok);
    }

    public async Task<GridLayoutRecord?> GetLayoutAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        GridLayoutRecord? layout = null;
        await using (var command = tx.Command(
            "SELECT view_id, columns::text, sort::text, modified_at FROM opportunity.grid_layout WHERE workspace_id = @ws AND user_id = @user"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("user", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                layout = new GridLayoutRecord(
                    reader.IsDBNull(0) ? null : reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : ReadColumns(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : ReadSort(reader.GetString(2)),
                    reader.GetFieldValue<DateTimeOffset>(3));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return layout;
    }

    public async Task<GridLayoutRecord> SaveLayoutAsync(
        Guid workspaceId, Guid userId, Guid? viewId, IReadOnlyList<GridViewColumn>? columns, IReadOnlyList<SearchSortKey>? sort,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        DateTimeOffset modifiedAt = default;
        await using (var upsert = tx.Command(
            """
            INSERT INTO opportunity.grid_layout (workspace_id, user_id, view_id, columns, sort, modified_at)
            VALUES (@ws, @user, @view, @columns::jsonb, @sort::jsonb, now())
            ON CONFLICT (workspace_id, user_id) DO UPDATE
               SET view_id = excluded.view_id, columns = excluded.columns, sort = excluded.sort, modified_at = excluded.modified_at
            RETURNING modified_at
            """))
        {
            upsert.Parameters.AddWithValue("ws", workspaceId);
            upsert.Parameters.AddWithValue("user", userId);
            upsert.Parameters.Add(new NpgsqlParameter("view", NpgsqlDbType.Uuid) { Value = (object?)viewId ?? DBNull.Value });
            upsert.Parameters.Add(new NpgsqlParameter("columns", NpgsqlDbType.Text)
            {
                Value = columns is null ? DBNull.Value : JsonSerializer.Serialize(columns.Select(StoredColumn.From), Json),
            });
            upsert.Parameters.Add(new NpgsqlParameter("sort", NpgsqlDbType.Text)
            {
                Value = sort is null ? DBNull.Value : JsonSerializer.Serialize(sort.Select(StoredSort.From), Json),
            });
            await using var reader = await upsert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                modifiedAt = reader.GetFieldValue<DateTimeOffset>(0);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GridLayoutRecord(viewId, columns, sort, modifiedAt);
    }

    private static async Task<GridViewWriteResult?> LockAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid viewId, long? expectedVersion, CancellationToken cancellationToken)
    {
        await using var command = tx.Command("SELECT version FROM opportunity.grid_view WHERE workspace_id = @ws AND view_id = @id FOR UPDATE");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", viewId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            long version when expectedVersion is null || version == expectedVersion => null,
            long => new GridViewWriteResult(GridViewWriteStatus.VersionConflict),
            _ => new GridViewWriteResult(GridViewWriteStatus.NotFound),
        };
    }

    private static async Task<GridViewWriteResult> FinishAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid viewId, AuditEvent? audit, CancellationToken cancellationToken)
    {
        if (audit is not null)
        {
            await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        }

        var views = await ReadAsync(tx, workspaceId, $"{Select} WHERE v.workspace_id = @ws AND v.view_id = @id", null,
            p => p.AddWithValue("id", viewId), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GridViewWriteResult(GridViewWriteStatus.Ok, views.Single());
    }

    /// <summary>Runs a statement inside a savepoint; false when it violated a unique constraint (a taken name).</summary>
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

    private static async Task<List<GridViewRecord>> ReadAsync(
        WorkspaceTransaction tx, Guid workspaceId, string sql, GridViewViewer? viewer, Action<NpgsqlParameterCollection> bind,
        CancellationToken cancellationToken)
    {
        var views = new List<GridViewRecord>();
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("all", viewer is null || viewer.SeesAll);
        command.Parameters.AddWithValue("user", viewer?.UserId ?? Guid.Empty);
        bind(command.Parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            views.Add(new GridViewRecord
            {
                WorkspaceId = workspaceId,
                ViewId = reader.GetGuid(0),
                Name = reader.GetString(1),
                OwnerId = reader.GetGuid(2),
                OwnerDisplayName = reader.GetString(3),
                Shared = reader.GetBoolean(4),
                Columns = ReadColumns(reader.GetString(5)),
                Sort = ReadSort(reader.GetString(6)),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
                ModifiedAt = reader.GetFieldValue<DateTimeOffset>(8),
                Version = reader.GetInt64(9),
            });
        }

        return views;
    }

    private static void BindDefinition(NpgsqlParameterCollection parameters, GridViewDefinition definition)
    {
        parameters.AddWithValue("name", definition.Name);
        parameters.AddWithValue("shared", definition.Shared);
        parameters.AddWithValue("columns", JsonSerializer.Serialize(definition.Columns.Select(StoredColumn.From), Json));
        parameters.AddWithValue("sort", JsonSerializer.Serialize(definition.Sort.Select(StoredSort.From), Json));
    }

    private static List<GridViewColumn> ReadColumns(string json) =>
        JsonSerializer.Deserialize<StoredColumn[]>(json, Json)?.Select(c => c.ToColumn()).ToList() ?? [];

    private static List<SearchSortKey> ReadSort(string json) =>
        JsonSerializer.Deserialize<StoredSort[]>(json, Json)?.Select(s => s.ToKey()).ToList() ?? [];

    /// <summary>Stored column: <c>{"field": "custodian", "width": 160, "pinned": false}</c>.</summary>
    private sealed record StoredColumn(string Field, int? Width, bool Pinned)
    {
        public static StoredColumn From(GridViewColumn column) => new(column.Field, column.Width, column.Pinned);

        public GridViewColumn ToColumn() => new(Field, Width, Pinned);
    }

    /// <summary>Stored sort key: <c>{"field": "controlNumber", "direction": "asc"}</c>.</summary>
    private sealed record StoredSort(string Field, string Direction)
    {
        public static StoredSort From(SearchSortKey key) => new(key.Field, key.Direction == SearchSortDirection.Desc ? "desc" : "asc");

        public SearchSortKey ToKey() =>
            new(Field, string.Equals(Direction, "desc", StringComparison.OrdinalIgnoreCase) ? SearchSortDirection.Desc : SearchSortDirection.Asc);
    }
}

public static class GridViewStoreRegistration
{
    /// <summary>Registers the PostgreSQL <see cref="IGridViewStore"/> (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresGridViewStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IGridViewStore, GridViewStore>();
        return services;
    }
}
