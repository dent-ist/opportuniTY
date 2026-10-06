using System.Globalization;
using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Documents.Dedupe;
using Opportunity.Application.Jobs;
using Opportunity.Application.SearchWork;
using Opportunity.Core.Documents;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Data.Audit;
using Opportunity.Data.Jobs;
using Opportunity.Data.SearchWork;

namespace Opportunity.Data.Relationships;

/// <summary>
/// PostgreSQL implementation of <see cref="IDedupeStore"/> (E09-T04, V0040): the <c>dedupe_policy</c> row and the run,
/// which applies <see cref="DuplicateGroupingWriter"/> in the leased chunk's transaction.
/// </summary>
public sealed class DedupeStore(NpgsqlDataSource dataSource) : IDedupeStore
{
    /// <summary>The resource type of the policy's audit events.</summary>
    public const string ResourceType = "DedupePolicy";

    public async Task<DedupePolicyRecord> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var record = await ReadAsync(tx, forUpdate: false, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<(DedupePolicyWriteOutcome Outcome, DedupePolicyRecord Record)> SaveAsync(
        Guid workspaceId, long expectedVersion, DedupePolicy policy, SecurityPrincipal actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);

        // The workspace row serializes the first save (no policy row to lock yet).
        await using (var lockWorkspace = tx.Command("SELECT 1 FROM opportunity.workspace WHERE workspace_id = @ws FOR NO KEY UPDATE"))
        {
            lockWorkspace.Parameters.AddWithValue("ws", workspaceId);
            await lockWorkspace.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = await ReadAsync(tx, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (current.Version != expectedVersion)
        {
            return (DedupePolicyWriteOutcome.VersionConflict, current);
        }

        if (current.Version > 0 && current.Policy == policy)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (DedupePolicyWriteOutcome.Saved, current);
        }

        await using (var upsert = tx.Command(
            """
            INSERT INTO opportunity.dedupe_policy AS p (workspace_id, enabled, hash_source, scope, custodian_field_id, modified_by)
            VALUES (@ws, @enabled, @source, @scope, @field, @by)
            ON CONFLICT (workspace_id) DO UPDATE
            SET enabled = excluded.enabled, hash_source = excluded.hash_source, scope = excluded.scope,
                custodian_field_id = excluded.custodian_field_id, modified_by = excluded.modified_by, modified_at = now(),
                version = p.version + 1
            """))
        {
            upsert.Parameters.AddWithValue("ws", workspaceId);
            upsert.Parameters.AddWithValue("enabled", policy.Enabled);
            upsert.Parameters.AddWithValue("source", (short)policy.HashSource);
            upsert.Parameters.AddWithValue("scope", (short)policy.Scope);
            upsert.Parameters.Add(new NpgsqlParameter("field", NpgsqlDbType.Integer) { Value = (object?)policy.CustodianFieldId ?? DBNull.Value });
            upsert.Parameters.AddWithValue("by", actor.UserId);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var saved = await ReadAsync(tx, forUpdate: false, cancellationToken).ConfigureAwait(false);
        await AuditSql.InsertAsync(tx, new AuditEvent
        {
            WorkspaceId = workspaceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditTaxonomy.Workspace.Category,
            Action = AuditTaxonomy.Workspace.SettingsChanged,
            ActorType = AuditActorType.User,
            ActorId = actor.UserId.ToString(),
            ActorDisplay = actor.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? actor.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : actor.DisplayName,
            ClientIp = actor.ClientIp,
            UserAgent = actor.UserAgent,
            ResourceType = ResourceType,
            ResourceId = workspaceId.ToString(),
            Outcome = AuditOutcome.Success,
            CorrelationId = actor.CorrelationId,
            Details = new Dictionary<string, string?>
            {
                ["changed"] = "dedupePolicy",
                ["dedupePolicy.old"] = current.Version == 0 ? null : current.Policy.ToString(),
                ["dedupePolicy.new"] = policy.ToString(),
                ["version"] = saved.Version.ToString(CultureInfo.InvariantCulture),
            },
        }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (DedupePolicyWriteOutcome.Saved, saved);
    }

    public async Task<long> CountDocumentsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            "SELECT count(*) FROM opportunity.document_projection_state WHERE workspace_id = @ws AND NOT is_deleted");
        command.Parameters.AddWithValue("ws", workspaceId);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public async Task<DedupeRunResult> RunAsync(ClaimedChunk chunk, DedupePolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(policy);
        var lease = chunk.Lease;
        ChunkCommitResult commit;
        await using (var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false))
        {
            // Serialized with import chunks and family resolution: the run sees every family committed before it.
            await FamilyWriter.LockAsync(tx, cancellationToken).ConfigureAwait(false);
            var run = await DuplicateGroupingWriter.ApplyAsync(tx, policy, cancellationToken).ConfigureAwait(false);
            var summary = new DedupeRunSummary(
                lease.JobId, DateTimeOffset.UtcNow, policy, run.FamiliesCompared, run.FamiliesWithoutHash, run.FamiliesWithoutCustodian,
                run.FamiliesWithUpstreamGroup, run.Groups, run.DocumentsGrouped, run.Changed.Count);
            await RecordRunAsync(tx, chunk, summary, cancellationToken).ConfigureAwait(false);

            // Fence F3 after the run's writes; then its search work as the transaction's last statements (ADR-001 §7.1).
            var tasks = IndexTasks(chunk, run.Changed).ToList();
            commit = await JobChunkRepository.CommitInTransactionAsync(tx, lease, new ChunkCompletion
            {
                ItemsApplied = run.Changed.Count,
                ItemsUnchanged = Math.Max(0, chunk.ItemCount - run.Changed.Count),
                IndexTasks = tasks.Count,
            }, cancellationToken).ConfigureAwait(false);
            if (commit.Committed)
            {
                foreach (var task in tasks)
                {
                    await SearchWorkSql.AddIndexChunkTaskAsync(tx, task, cancellationToken).ConfigureAwait(false);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new DedupeRunResult(commit, summary);
            }
        }

        // Fence F3 refused: everything rolled back with the transaction; record the fence outcome on the chunk.
        if (commit.Outcome != ChunkCommitOutcome.LeaseLost)
        {
            var release = await new JobChunkRepository(dataSource).ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);
            commit = release switch
            {
                ChunkReleaseOutcome.Cancelled => commit with { Outcome = ChunkCommitOutcome.Cancelled },
                ChunkReleaseOutcome.ReturnedToPending => commit with { Outcome = ChunkCommitOutcome.JobNotRunning },
                _ => commit with { Outcome = ChunkCommitOutcome.LeaseLost },
            };
        }

        return new DedupeRunResult(commit, null);
    }

    /// <summary>Relationship IndexChunkTasks over explicit ids (at most 1,000 each) for the documents the run changed.</summary>
    private static IEnumerable<NewIndexChunkTask> IndexTasks(ClaimedChunk chunk, IReadOnlyList<Guid> documents)
    {
        var lease = chunk.Lease;
        var baseKey = ChunkIdempotencyKey.ForChunk(lease.WorkspaceId, lease.JobId, chunk.Sequence, ChunkOperationKind.IndexChunk, 0);
        var batches = documents.Distinct().Order().Chunk(ChunkMembership.MaxExplicitIds).ToList();
        for (var i = 0; i < batches.Count; i++)
        {
            yield return new NewIndexChunkTask
            {
                JobId = lease.JobId,
                ChunkId = lease.ChunkId,
                Kind = IndexTaskKind.Relationship,
                Membership = ChunkMembership.ExplicitIds(batches[i]),
                ChangeMask = SearchChangeMask.Relationships,
                IdempotencyKey = i == 0
                    ? baseKey
                    : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                        string.Create(CultureInfo.InvariantCulture, $"{baseKey}|{i}")))),
            };
        }
    }

    private static async Task RecordRunAsync(WorkspaceTransaction tx, ClaimedChunk chunk, DedupeRunSummary summary, CancellationToken cancellationToken)
    {
        // A run of the default policy (none saved) creates the row with that default, so the run can be read back.
        await using var command = tx.Command(
            """
            INSERT INTO opportunity.dedupe_policy AS p (workspace_id, modified_by, last_run_job_id, last_run_at, last_run_summary)
            VALUES (@ws, @by, @job, now(), @summary)
            ON CONFLICT (workspace_id) DO UPDATE
            SET last_run_job_id = excluded.last_run_job_id, last_run_at = excluded.last_run_at, last_run_summary = excluded.last_run_summary
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("by", chunk.InitiatedBy);
        command.Parameters.AddWithValue("job", summary.JobId);
        command.Parameters.Add(new NpgsqlParameter("summary", NpgsqlDbType.Jsonb) { Value = SummaryJson(summary).ToJsonString() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject SummaryJson(DedupeRunSummary s) => new()
    {
        ["policy"] = s.Policy.ToJson(),
        ["familiesCompared"] = s.FamiliesCompared,
        ["familiesWithoutHash"] = s.FamiliesWithoutHash,
        ["familiesWithoutCustodian"] = s.FamiliesWithoutCustodian,
        ["familiesWithUpstreamGroup"] = s.FamiliesWithUpstreamGroup,
        ["groups"] = s.Groups,
        ["documentsGrouped"] = s.DocumentsGrouped,
        ["documentsChanged"] = s.DocumentsChanged,
    };

    private static async Task<DedupePolicyRecord> ReadAsync(WorkspaceTransaction tx, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            SELECT enabled, hash_source, scope, custodian_field_id, version, modified_by, modified_at, last_run_job_id, last_run_at,
                   last_run_summary::text
            FROM opportunity.dedupe_policy WHERE workspace_id = @ws {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new DedupePolicyRecord(DedupePolicy.Default, 0, null, null, null);
        }

        var policy = new DedupePolicy(
            reader.GetBoolean(0), (DedupeHashSource)reader.GetInt16(1), (DuplicateGroupScope)reader.GetInt16(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3));
        DedupeRunSummary? lastRun = null;
        if (!reader.IsDBNull(7) && JsonNode.Parse(reader.GetString(9)) is JsonObject s)
        {
            lastRun = new DedupeRunSummary(
                reader.GetGuid(7), reader.GetFieldValue<DateTimeOffset>(8), DedupePolicy.Parse(s["policy"] as JsonObject),
                Count(s, "familiesCompared"), Count(s, "familiesWithoutHash"), Count(s, "familiesWithoutCustodian"),
                Count(s, "familiesWithUpstreamGroup"), Count(s, "groups"), Count(s, "documentsGrouped"), Count(s, "documentsChanged"));
        }

        return new DedupePolicyRecord(policy, reader.GetInt64(4), reader.GetGuid(5), reader.GetFieldValue<DateTimeOffset>(6), lastRun);
    }

    private static long Count(JsonObject summary, string name) => summary[name]?.GetValue<long>() ?? 0;
}
