using System.Data;

using Npgsql;

using Opportunity.Application.Search;

namespace Opportunity.Data.Search;

/// <summary>
/// PostgreSQL <see cref="ISearchWatermarkStore"/> (V0011 counter and work records, V0030 visible watermark). A read is
/// one REPEATABLE READ snapshot of the counter, the visible watermark, the applied watermark and the oldest unreflected
/// work record, so the numbers are mutually consistent.
/// </summary>
public sealed class SearchWatermarkStore(NpgsqlDataSource dataSource) : ISearchWatermarkStore
{
    // Applied watermark (ADR-001 §7.2): the oldest non-Applied generation - 1, or the counter. Failed rows hold it back.
    // Oldest unreflected work: generations are commit-ordered, so the lowest generation above the visible watermark is
    // the oldest commit search does not reflect, whatever its status.
    private const string ReadSql =
        """
        WITH s AS (
            SELECT coalesce((SELECT g.value FROM opportunity.workspace_search_generation g WHERE g.workspace_id = @ws), 0) AS latest,
                   coalesce((SELECT w.indexed_through_generation FROM opportunity.workspace_search_watermark w WHERE w.workspace_id = @ws), 0) AS indexed)
        SELECT s.latest,
               s.indexed,
               (SELECT min(o.search_generation) FROM opportunity.search_outbox o WHERE o.workspace_id = @ws AND o.status <> 4),
               (SELECT min(t.search_generation) FROM opportunity.index_chunk_task t
                 WHERE t.workspace_id = @ws AND t.status <> 5 AND t.search_generation IS NOT NULL),
               CASE WHEN s.latest > s.indexed THEN least(
                   (SELECT o.committed_at FROM opportunity.search_outbox o
                     WHERE o.workspace_id = @ws AND o.search_generation > s.indexed ORDER BY o.search_generation LIMIT 1),
                   (SELECT t.committed_at FROM opportunity.index_chunk_task t
                     WHERE t.workspace_id = @ws AND t.search_generation > s.indexed ORDER BY t.search_generation LIMIT 1)) END,
               clock_timestamp()
        FROM s
        """;

    public async Task<SearchFreshnessReading> ReadAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        var reading = await ReadAsync(tx, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reading;
    }

    public async Task<SearchFreshnessReading> AdvanceAsync(Guid workspaceId, long indexedThroughGeneration, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(indexedThroughGeneration);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            INSERT INTO opportunity.workspace_search_watermark AS w (workspace_id, indexed_through_generation, refreshed_at)
            SELECT @ws, LEAST(@through, coalesce((SELECT g.value FROM opportunity.workspace_search_generation g WHERE g.workspace_id = @ws), 0)),
                   clock_timestamp()
            ON CONFLICT (workspace_id) DO UPDATE
                SET indexed_through_generation = GREATEST(w.indexed_through_generation, EXCLUDED.indexed_through_generation),
                    refreshed_at = EXCLUDED.refreshed_at,
                    updated_at = now()
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("through", indexedThroughGeneration);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var reading = await ReadAsync(tx, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reading;
    }

    /// <summary>The visible watermark alone, in the caller's transaction (job monitor pages).</summary>
    internal static async Task<long> ReadIndexedThroughAsync(WorkspaceTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            "SELECT coalesce((SELECT indexed_through_generation FROM opportunity.workspace_search_watermark WHERE workspace_id = @ws), 0)");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<SearchFreshnessReading> ReadAsync(WorkspaceTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(ReadSql);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var latest = reader.GetInt64(0);
        var indexed = Math.Min(reader.GetInt64(1), latest);
        var oldestPending = Math.Min(
            reader.IsDBNull(2) ? long.MaxValue : reader.GetInt64(2),
            reader.IsDBNull(3) ? long.MaxValue : reader.GetInt64(3));
        var applied = oldestPending == long.MaxValue ? latest : Math.Min(latest, oldestPending - 1);
        DateTimeOffset? oldest = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4);
        return new SearchFreshnessReading(latest, indexed, Math.Max(applied, 0), oldest, reader.GetFieldValue<DateTimeOffset>(5));
    }
}
