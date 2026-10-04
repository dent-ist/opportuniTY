using Npgsql;

using Opportunity.Application.SearchWork;
using Opportunity.Core.Jobs;

namespace Opportunity.Data.SearchWork;

/// <summary>
/// PostgreSQL implementation of <see cref="IIndexTaskMembershipReader"/> (ADR-010 §4 resolution column):
/// <list type="bullet">
/// <item><c>ImportRows</c>: the <c>import_batch_member</c> rows the import chunk's own transaction wrote, except rows
/// recorded as Skipped (an overlay without changes bumped nothing, so there is nothing to project);</item>
/// <item><c>ExplicitIds</c>: the listed ids as they are; a missing row later becomes an unconditional delete;</item>
/// <item><c>DocumentKeyRange</c>: every <c>document_projection_state</c> row in the key range at execution, tombstones
/// included (they become deletes);</item>
/// <item><c>SnapshotRange</c>: not resolvable until materialized snapshots exist (E10-T02).</item>
/// </list>
/// </summary>
public sealed class IndexTaskMembershipReader(NpgsqlDataSource dataSource) : IIndexTaskMembershipReader
{
    public const int MaxPageSize = 5_000;

    public async Task<IReadOnlyList<Guid>> ReadPageAsync(
        Guid workspaceId, ChunkMembership membership, Guid? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);

        switch (membership.Kind)
        {
            case ChunkMembershipKind.ExplicitIds:
                return [.. membership.DocumentIds!.Where(id => after is null || id.CompareTo(after.Value) > 0).Order().Take(limit)];
            case ChunkMembershipKind.ImportRows:
                return await QueryAsync(
                    workspaceId,
                    """
                    SELECT DISTINCT m.document_id FROM opportunity.import_batch_member m
                    WHERE m.workspace_id = @ws AND m.import_batch_id = @batch AND m.row_no BETWEEN @from AND @to AND m.action <> 3
                      AND (@after::uuid IS NULL OR m.document_id > @after)
                    ORDER BY m.document_id
                    LIMIT @limit
                    """,
                    command =>
                    {
                        command.Parameters.AddWithValue("batch", membership.ImportBatchId!.Value);
                        command.Parameters.AddWithValue("from", membership.RangeFrom!.Value);
                        command.Parameters.AddWithValue("to", membership.RangeTo!.Value);
                    },
                    after, limit, cancellationToken).ConfigureAwait(false);
            case ChunkMembershipKind.DocumentKeyRange:
                return await QueryAsync(
                    workspaceId,
                    """
                    SELECT s.document_id FROM opportunity.document_projection_state s
                    WHERE s.workspace_id = @ws AND s.document_id BETWEEN @from AND @to
                      AND (@after::uuid IS NULL OR s.document_id > @after)
                    ORDER BY s.document_id
                    LIMIT @limit
                    """,
                    command =>
                    {
                        command.Parameters.AddWithValue("from", membership.DocumentIdFrom!.Value);
                        command.Parameters.AddWithValue("to", membership.DocumentIdTo!.Value);
                    },
                    after, limit, cancellationToken).ConfigureAwait(false);
            default:
                throw new NotSupportedException(
                    $"{membership.Kind} membership cannot be resolved yet: materialized snapshots arrive with E10-T02. Replay the task once they exist.");
        }
    }

    private async Task<IReadOnlyList<Guid>> QueryAsync(
        Guid workspaceId, string sql, Action<NpgsqlCommand> parameters, Guid? after, int limit, CancellationToken cancellationToken)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)after ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        parameters(command);
        var ids = new List<Guid>(limit);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }
}
