using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.SearchWork;
using Opportunity.Core.SearchWork;

namespace Opportunity.Data.SearchWork;

/// <summary>PostgreSQL implementation of <see cref="ISearchOutboxRepository"/> over <c>opportunity.search_outbox</c> (V0008).</summary>
public sealed class SearchOutboxRepository(NpgsqlDataSource dataSource) : ISearchOutboxRepository
{
    private const int MaxOwnerLength = 200;
    private const int MaxErrorLength = 2_000;

    private const string ClaimedColumns =
        "o.workspace_id, o.outbox_id, o.created_at, o.document_id, o.document_version, o.change_mask, o.lane, " +
        "o.search_generation, o.committed_at, o.attempt_count";

    public async Task<IReadOnlyList<ClaimedOutboxRow>> ClaimAsync(
        Guid workspaceId, string owner, int limit, TimeSpan claimDuration, CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(claimDuration, TimeSpan.Zero);

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);

        // Rows whose attempts are used up stop here instead of cycling (ADR-001 §6.4).
        await using (var exhausted = tx.Command(
            """
            UPDATE opportunity.search_outbox SET status = 5, claim_owner = NULL, claim_expires_at = NULL, updated_at = now(),
                last_error = coalesce(last_error, 'Publish attempts exhausted.')
            WHERE workspace_id = @ws AND attempt_count >= @max
              AND (status = 1 OR (status = 2 AND claim_expires_at < now()))
            """))
        {
            exhausted.Parameters.AddWithValue("ws", workspaceId);
            exhausted.Parameters.AddWithValue("max", SearchOutboxRetryPolicy.MaxAttempts);
            await exhausted.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = tx.Command(
            $"""
            WITH candidate AS (
                SELECT workspace_id, created_at, outbox_id
                FROM opportunity.search_outbox
                WHERE workspace_id = @ws AND (status = 1 OR (status = 2 AND claim_expires_at < now()))
                  AND available_at <= now() AND attempt_count < @max
                ORDER BY lane, search_generation
                LIMIT @limit
                FOR UPDATE SKIP LOCKED)
            UPDATE opportunity.search_outbox o
            SET status = 2, claim_owner = @owner, claim_expires_at = clock_timestamp() + @claim,
                attempt_count = o.attempt_count + 1, updated_at = now()
            FROM candidate c
            WHERE o.workspace_id = c.workspace_id AND o.created_at = c.created_at AND o.outbox_id = c.outbox_id
            RETURNING {ClaimedColumns}
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("max", SearchOutboxRetryPolicy.MaxAttempts);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("claim", claimDuration);
        var rows = new List<ClaimedOutboxRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ClaimedOutboxRow(
                    reader.GetGuid(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetGuid(3),
                    reader.GetInt64(4), (SearchChangeMask)reader.GetInt16(5), SearchWorkSql.Lane(reader.GetInt16(6)),
                    reader.GetInt64(7), reader.GetFieldValue<DateTimeOffset>(8), reader.GetInt32(9)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.OrderBy(r => r.Lane).ThenBy(r => r.SearchGeneration).ThenBy(r => r.OutboxId)];
    }

    public async Task<int> MarkDispatchedAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<ClaimedOutboxRow> rows, CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(rows);
        return await UpdateClaimedAsync(workspaceId, owner, rows,
            "status = 3, dispatched_at = now(), claim_owner = NULL, claim_expires_at = NULL, last_error = NULL, updated_at = now()",
            null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ReleaseAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<ClaimedOutboxRow> rows, string reason, CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // attempt_count already counts the failed publish; backoff 0.5 s × 2^(n-1), capped (ADR-001 §6.4).
        return await UpdateClaimedAsync(workspaceId, owner, rows,
            """
            status = CASE WHEN o.attempt_count >= @max THEN 5 ELSE 1 END,
            available_at = now() + least(@base * power(2, least(o.attempt_count - 1, 16)), @cap),
            claim_owner = NULL, claim_expires_at = NULL, last_error = @error, updated_at = now()
            """,
            c =>
            {
                c.Parameters.AddWithValue("max", SearchOutboxRetryPolicy.MaxAttempts);
                c.Parameters.AddWithValue("base", SearchOutboxRetryPolicy.BaseDelay);
                c.Parameters.AddWithValue("cap", SearchOutboxRetryPolicy.MaxDelay);
                c.Parameters.AddWithValue("error", Truncate(reason));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> MarkAppliedThroughAsync(
        Guid workspaceId, Guid documentId, long documentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(documentVersion);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            UPDATE opportunity.search_outbox
            SET status = 4, applied_at = now(), claim_owner = NULL, claim_expires_at = NULL, updated_at = now()
            WHERE workspace_id = @ws AND document_id = @doc AND document_version <= @version AND status <> 4
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("doc", documentId);
        command.Parameters.AddWithValue("version", documentVersion);
        var applied = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return applied;
    }

    public async Task<SearchOutboxRow?> GetAsync(Guid workspaceId, long outboxId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT workspace_id, outbox_id, created_at, document_id, document_version, change_mask, lane, search_generation,
                   committed_at, status, attempt_count, dispatched_at, applied_at, last_error
            FROM opportunity.search_outbox
            WHERE workspace_id = @ws AND outbox_id = @id
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", outboxId);
        SearchOutboxRow? row = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                row = new SearchOutboxRow(
                    reader.GetGuid(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetGuid(3),
                    reader.GetInt64(4), (SearchChangeMask)reader.GetInt16(5), SearchWorkSql.Lane(reader.GetInt16(6)),
                    reader.GetInt64(7), reader.GetFieldValue<DateTimeOffset>(8), (SearchOutboxStatus)reader.GetInt16(9),
                    reader.GetInt32(10),
                    reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                    reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private async Task<int> UpdateClaimedAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<ClaimedOutboxRow> rows, string set, Action<NpgsqlCommand>? parameters,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        if (rows.Any(r => r.WorkspaceId != workspaceId))
        {
            throw new ArgumentException("Every row must belong to the workspace.", nameof(rows));
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.search_outbox o SET {set}
            FROM unnest(@created, @ids) AS k(created_at, outbox_id)
            WHERE o.workspace_id = @ws AND o.created_at = k.created_at AND o.outbox_id = k.outbox_id
              AND o.status = 2 AND o.claim_owner = @owner
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.Add(new NpgsqlParameter("created", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = rows.Select(r => r.CreatedAt).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = rows.Select(r => r.OutboxId).ToArray() });
        parameters?.Invoke(command);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static void ValidateOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(owner.Length, MaxOwnerLength, nameof(owner));
    }

    internal static string Truncate(string error) => error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
}
