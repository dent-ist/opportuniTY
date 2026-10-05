using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Search.HighlightSets;
using Opportunity.Contracts.Search;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Search;

/// <summary>
/// PostgreSQL <see cref="IHighlightSetStore"/> over <c>highlight_set</c> and <c>highlight_set_selection</c> (V0034,
/// tenant tables under forced RLS). Writes lock the row, check the version and insert their audit event in the same
/// transaction; a duplicate name is reported, not thrown.
/// </summary>
public sealed class HighlightSetStore(NpgsqlDataSource dataSource) : IHighlightSetStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Select =
        """
        SELECT h.highlight_set_id, h.name, h.description, h.color, h.terms::text, h.modified_by,
               coalesce(u.display_name, u.email, h.modified_by::text), h.modified_at, h.version
          FROM opportunity.highlight_set h
          LEFT JOIN opportunity.app_user u ON u.user_id = h.modified_by
        """;

    public async Task<IReadOnlyList<HighlightSetRecord>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var sets = await ReadAsync(tx, workspaceId, $"{Select} WHERE h.workspace_id = @ws ORDER BY lower(h.name), h.highlight_set_id", _ => { },
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return sets;
    }

    public async Task<IReadOnlyList<HighlightSetRecord>> GetManyAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> highlightSetIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(highlightSetIds);
        if (highlightSetIds.Count == 0)
        {
            return [];
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var sets = await ReadAsync(tx, workspaceId, $"{Select} WHERE h.workspace_id = @ws AND h.highlight_set_id = ANY (@ids)",
            p => p.AddWithValue("ids", highlightSetIds.ToArray()), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return sets;
    }

    public async Task<HighlightSetWriteResult> CreateAsync(
        Guid workspaceId, Guid highlightSetId, Guid actorId, HighlightSetDefinition definition, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.highlight_set (workspace_id, highlight_set_id, name, description, color, terms, created_by, modified_by)
            VALUES (@ws, @id, @name, @description, @color, @terms::jsonb, @actor, @actor)
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("id", highlightSetId);
            insert.Parameters.AddWithValue("actor", actorId);
            Bind(insert.Parameters, definition);
            if (!await TryExecuteAsync(insert, cancellationToken).ConfigureAwait(false))
            {
                return new HighlightSetWriteResult(HighlightSetWriteStatus.NameTaken);
            }
        }

        return await FinishAsync(tx, workspaceId, highlightSetId, audit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HighlightSetWriteResult> UpdateAsync(
        Guid workspaceId, Guid highlightSetId, long expectedVersion, Guid actorId, HighlightSetDefinition definition, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await LockAsync(tx, workspaceId, highlightSetId, expectedVersion, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        await using (var update = tx.Command(
            """
            UPDATE opportunity.highlight_set
               SET name = @name, description = @description, color = @color, terms = @terms::jsonb, modified_by = @actor,
                   modified_at = now(), version = version + 1
             WHERE workspace_id = @ws AND highlight_set_id = @id
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", highlightSetId);
            update.Parameters.AddWithValue("actor", actorId);
            Bind(update.Parameters, definition);
            if (!await TryExecuteAsync(update, cancellationToken).ConfigureAwait(false))
            {
                return new HighlightSetWriteResult(HighlightSetWriteStatus.NameTaken);
            }
        }

        return await FinishAsync(tx, workspaceId, highlightSetId, audit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HighlightSetWriteResult> DeleteAsync(
        Guid workspaceId, Guid highlightSetId, long expectedVersion, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await LockAsync(tx, workspaceId, highlightSetId, expectedVersion, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        await using (var delete = tx.Command("DELETE FROM opportunity.highlight_set WHERE workspace_id = @ws AND highlight_set_id = @id"))
        {
            delete.Parameters.AddWithValue("ws", workspaceId);
            delete.Parameters.AddWithValue("id", highlightSetId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new HighlightSetWriteResult(HighlightSetWriteStatus.Ok);
    }

    public async Task<HighlightSetSelection> GetSelectionAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        HighlightSetSelection selection = new([], true);
        await using (var command = tx.Command(
            "SELECT disabled_set_ids, search_hits FROM opportunity.highlight_set_selection WHERE workspace_id = @ws AND user_id = @user"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("user", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                selection = new HighlightSetSelection(reader.GetFieldValue<Guid[]>(0), reader.GetBoolean(1));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return selection;
    }

    public async Task SetSelectionAsync(Guid workspaceId, Guid userId, HighlightSetSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var upsert = tx.Command(
            """
            INSERT INTO opportunity.highlight_set_selection (workspace_id, user_id, disabled_set_ids, search_hits)
            VALUES (@ws, @user, @disabled, @hits)
            ON CONFLICT (workspace_id, user_id)
            DO UPDATE SET disabled_set_ids = excluded.disabled_set_ids, search_hits = excluded.search_hits, modified_at = now()
            """))
        {
            upsert.Parameters.AddWithValue("ws", workspaceId);
            upsert.Parameters.AddWithValue("user", userId);
            upsert.Parameters.AddWithValue("disabled", selection.DisabledSetIds.ToArray());
            upsert.Parameters.AddWithValue("hits", selection.SearchHits);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(NpgsqlParameterCollection parameters, HighlightSetDefinition definition)
    {
        parameters.AddWithValue("name", definition.Name);
        parameters.Add(new NpgsqlParameter("description", NpgsqlDbType.Text) { Value = (object?)definition.Description ?? DBNull.Value });
        parameters.AddWithValue("color", definition.Color);
        parameters.AddWithValue("terms", JsonSerializer.Serialize(definition.Terms.Select(t => new StoredTerm(t.TermId, t.Expression, t.Color)), Json));
    }

    private static async Task<HighlightSetWriteResult> FinishAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid highlightSetId, AuditEvent audit, CancellationToken cancellationToken)
    {
        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = await ReadAsync(tx, workspaceId, $"{Select} WHERE h.workspace_id = @ws AND h.highlight_set_id = @id",
            p => p.AddWithValue("id", highlightSetId), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new HighlightSetWriteResult(HighlightSetWriteStatus.Ok, saved[0]);
    }

    /// <summary>Locks the row; a missing row or a stale version is the failed result.</summary>
    private static async Task<HighlightSetWriteResult?> LockAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid highlightSetId, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            "SELECT version FROM opportunity.highlight_set WHERE workspace_id = @ws AND highlight_set_id = @id FOR UPDATE");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", highlightSetId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            long version when version == expectedVersion => null,
            long => new HighlightSetWriteResult(HighlightSetWriteStatus.VersionConflict),
            _ => new HighlightSetWriteResult(HighlightSetWriteStatus.NotFound),
        };
    }

    /// <summary>Runs a statement inside a savepoint; false when it violated a unique constraint (the name).</summary>
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

    private static async Task<List<HighlightSetRecord>> ReadAsync(
        WorkspaceTransaction tx, Guid workspaceId, string sql, Action<NpgsqlParameterCollection> bind, CancellationToken cancellationToken)
    {
        var sets = new List<HighlightSetRecord>();
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", workspaceId);
        bind(command.Parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var terms = JsonSerializer.Deserialize<List<StoredTerm>>(reader.GetString(4), Json) ?? [];
            sets.Add(new HighlightSetRecord
            {
                WorkspaceId = workspaceId,
                HighlightSetId = reader.GetGuid(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Color = reader.GetString(3),
                Terms = [.. terms.Select(t => new HighlightTerm(t.TermId, t.Expression, t.Color))],
                ModifiedBy = reader.GetGuid(5),
                ModifiedByDisplayName = reader.GetString(6),
                ModifiedAt = reader.GetFieldValue<DateTimeOffset>(7),
                Version = reader.GetInt64(8),
            });
        }

        return sets;
    }

    private sealed record StoredTerm(Guid TermId, string Expression, string? Color);
}

public static class HighlightSetStoreRegistration
{
    /// <summary>Registers the PostgreSQL <see cref="IHighlightSetStore"/> (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresHighlightSetStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IHighlightSetStore, HighlightSetStore>();
        return services;
    }
}
