using System.Data;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Jobs;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Search.Reindex;
using Opportunity.Application.SearchWork;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Data.Jobs;
using Opportunity.Data.SearchWork;

namespace Opportunity.Data.Search;

/// <summary>
/// PostgreSQL implementation of <see cref="IReindexStore"/> over <c>search_reindex</c> (tenant, RLS) and the
/// installation-level <c>search_reindex_active</c> registry (V0041), the job tables and <c>document_projection_state</c>.
/// </summary>
public sealed class ReindexStore(NpgsqlDataSource dataSource) : IReindexStore
{
    /// <summary>A reindex task refreshes every projection input of the documents in its range.</summary>
    public const SearchChangeMask ReindexChangeMask =
        SearchChangeMask.Content | SearchChangeMask.Metadata | SearchChangeMask.Coding | SearchChangeMask.Relationships;

    private const string Columns =
        """
        workspace_id, job_id, phase, requested_kind, requested_generation, requested_shards,
        source_kind, source_pool, source_generation, source_revision, target_kind, target_pool, target_generation, target_revision,
        next_step_at, retain_until, documents_planned, validation::text, error, created_at, updated_at, switched_at, finished_at
        """;

    private const string Counter =
        "coalesce((SELECT g.value FROM opportunity.workspace_search_generation g WHERE g.workspace_id = @ws), 0)";

    public async Task<(ReindexCreateOutcome Outcome, ReindexRun Run)> CreateAsync(
        Guid workspaceId, Guid jobId, ReindexRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        ReindexRun? run;
        await using (var insert = tx.Command(
            $"""
            INSERT INTO opportunity.search_reindex (workspace_id, job_id, requested_kind, requested_generation, requested_shards)
            VALUES (@ws, @job, @kind, @generation, @shards)
            ON CONFLICT DO NOTHING
            RETURNING {Columns}
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("job", jobId);
            insert.Parameters.Add(Nullable("kind", NpgsqlDbType.Smallint, request.Kind is { } k ? (short?)k : null));
            insert.Parameters.Add(Nullable("generation", NpgsqlDbType.Integer, request.Generation));
            insert.Parameters.Add(Nullable("shards", NpgsqlDbType.Integer, request.PrimaryShards));
            run = await ReadOneAsync(insert, cancellationToken).ConfigureAwait(false);
        }

        var outcome = ReindexCreateOutcome.Created;
        if (run is null)
        {
            run = await SelectAsync(tx, "job_id = @job", c => c.Parameters.AddWithValue("job", jobId), cancellationToken).ConfigureAwait(false);
            outcome = ReindexCreateOutcome.Existing;
            if (run is null)
            {
                outcome = ReindexCreateOutcome.Conflict;
                run = await SelectAsync(tx, "phase BETWEEN 1 AND 5", _ => { }, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The reindex insert conflicted with a run that no longer exists.");
            }
        }
        else
        {
            await using var register = tx.Command(
                """
                INSERT INTO opportunity.search_reindex_active (reindex_workspace_id, job_id) VALUES (@ws, @job) ON CONFLICT DO NOTHING
                """);
            register.Parameters.AddWithValue("ws", workspaceId);
            register.Parameters.AddWithValue("job", jobId);
            await register.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (outcome, run);
    }

    public async Task<ReindexRun?> GetAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var run = await SelectAsync(tx, "job_id = @job", c => c.Parameters.AddWithValue("job", jobId), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async Task<IReadOnlyList<ReindexRun>> ListAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns} FROM opportunity.search_reindex WHERE workspace_id = @ws
            ORDER BY created_at DESC, job_id DESC LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("limit", limit);
        var runs = await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return runs;
    }

    public async Task<IReadOnlyList<(Guid WorkspaceId, Guid JobId)>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            "SELECT reindex_workspace_id, job_id FROM opportunity.search_reindex_active ORDER BY created_at, reindex_workspace_id, job_id LIMIT 1000");
        var active = new List<(Guid, Guid)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                active.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return active;
    }

    public async Task<ReindexRun?> TryLeaseAsync(
        Guid workspaceId, Guid jobId, string owner, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.search_reindex SET lease_owner = @owner, lease_expires_at = now() + @duration
            WHERE workspace_id = @ws AND job_id = @job AND phase NOT IN (8, 10)
              AND (lease_owner IS NULL OR lease_owner = @owner OR lease_expires_at < now())
            RETURNING {Columns}
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("duration", duration);
        var run = await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async Task<ReindexRun?> SaveAsync(ReindexRun run, string owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var finished = ReindexPhases.IsFinished(run.Phase);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        ReindexRun? stored;
        await using (var command = tx.Command(
            $"""
            UPDATE opportunity.search_reindex SET
                phase = @phase, source_kind = @source_kind, source_pool = @source_pool, source_generation = @source_generation,
                source_revision = @source_revision, target_kind = @target_kind, target_pool = @target_pool,
                target_generation = @target_generation, target_revision = @target_revision, next_step_at = @next_step_at,
                retain_until = @retain_until, documents_planned = @documents_planned, validation = @validation::jsonb, error = @error,
                switched_at = @switched_at, finished_at = CASE WHEN @finished THEN coalesce(finished_at, now()) END,
                lease_owner = CASE WHEN @finished THEN NULL ELSE lease_owner END,
                lease_expires_at = CASE WHEN @finished THEN NULL ELSE lease_expires_at END,
                updated_at = now()
            WHERE workspace_id = @ws AND job_id = @job AND lease_owner = @owner
            RETURNING {Columns}
            """))
        {
            command.Parameters.AddWithValue("ws", run.WorkspaceId);
            command.Parameters.AddWithValue("job", run.JobId);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("phase", (short)run.Phase);
            BindLocation(command, "source", run.Source);
            BindLocation(command, "target", run.Target);
            command.Parameters.Add(Nullable("next_step_at", NpgsqlDbType.TimestampTz, run.NextStepAt));
            command.Parameters.Add(Nullable("retain_until", NpgsqlDbType.TimestampTz, run.RetainUntil));
            command.Parameters.Add(Nullable("documents_planned", NpgsqlDbType.Bigint, run.DocumentsPlanned));
            command.Parameters.Add(Nullable("validation", NpgsqlDbType.Text, run.Validation?.ToJson().ToJsonString()));
            command.Parameters.Add(Nullable("error", NpgsqlDbType.Text, Truncate(run.Error, 2_000)));
            command.Parameters.Add(Nullable("switched_at", NpgsqlDbType.TimestampTz, run.SwitchedAt));
            command.Parameters.AddWithValue("finished", finished);
            stored = await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
        }

        if (stored is null)
        {
            return null;
        }

        await using (var reason = tx.Command(
            "UPDATE opportunity.job SET status_reason = @reason, updated_at = now() WHERE workspace_id = @ws AND job_id = @job"))
        {
            reason.Parameters.AddWithValue("ws", run.WorkspaceId);
            reason.Parameters.AddWithValue("job", run.JobId);
            reason.Parameters.AddWithValue("reason", Truncate(stored.StatusReason, 2_000)!);
            await reason.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (finished)
        {
            await using var unregister = tx.Command("DELETE FROM opportunity.search_reindex_active WHERE reindex_workspace_id = @ws AND job_id = @job");
            unregister.Parameters.AddWithValue("ws", run.WorkspaceId);
            unregister.Parameters.AddWithValue("job", run.JobId);
            await unregister.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task<(IReadOnlyList<ChunkPlan> Chunks, long Documents)> PlanKeyRangesAsync(
        Guid workspaceId, long projectionGeneration, int maxDocuments, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(projectionGeneration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDocuments);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        long documents;
        await using (var count = tx.Command("SELECT count(*) FROM opportunity.document_projection_state WHERE workspace_id = @ws"))
        {
            count.Parameters.AddWithValue("ws", workspaceId);
            documents = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        // Every maxDocuments-th key closes a range; only the boundaries leave the database.
        var chunks = new List<ChunkPlan>();
        var from = Guid.Empty;
        await using (var boundaries = tx.Command(
            """
            SELECT document_id FROM (
                SELECT document_id, row_number() OVER (ORDER BY document_id) AS n
                FROM opportunity.document_projection_state WHERE workspace_id = @ws) s
            WHERE n % @max = 0
            ORDER BY document_id
            """))
        {
            boundaries.Parameters.AddWithValue("ws", workspaceId);
            boundaries.Parameters.AddWithValue("max", (long)maxDocuments);
            await using var reader = await boundaries.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var to = reader.GetGuid(0);
                chunks.Add(new ChunkPlan(ChunkMembership.DocumentKeyRange(projectionGeneration, from, to), maxDocuments));
                if (to == Guid.AllBitsSet)
                {
                    from = to;
                    break;
                }

                from = DocumentKeyRanges.Successor(to);
            }
        }

        if (chunks.Count == 0 || from != chunks[^1].Membership.DocumentIdTo)
        {
            chunks.Add(new ChunkPlan(ChunkMembership.DocumentKeyRange(projectionGeneration, from, Guid.AllBitsSet),
                (int)(documents - ((long)chunks.Count * maxDocuments))));
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (chunks, documents);
    }

    public async Task<ChunkCommitResult> CommitChunkAsync(ClaimedChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.JobType != JobType.Reindex || chunk.OperationKind != ChunkOperationKind.ReindexChunk
            || chunk.Membership.Kind != ChunkMembershipKind.DocumentKeyRange)
        {
            throw new ArgumentException("Not a reindex chunk.", nameof(chunk));
        }

        var lease = chunk.Lease;
        ChunkCommitResult commit;
        await using (var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false))
        {
            (commit, _) = await SearchWorkSql.CommitChunkAsync(tx, lease, new ChunkCompletion { ItemsApplied = chunk.ItemCount }, new NewIndexChunkTask
            {
                JobId = lease.JobId,
                ChunkId = lease.ChunkId,
                Kind = IndexTaskKind.Reindex,
                Membership = chunk.Membership,
                ChangeMask = ReindexChangeMask,
                IdempotencyKey = ChunkIdempotencyKey.ForChunk(lease.WorkspaceId, lease.JobId, chunk.Sequence, ChunkOperationKind.ReindexChunk,
                    chunk.Membership.ProjectionGeneration ?? 0),
            }, cancellationToken).ConfigureAwait(false);
            if (commit.Committed)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return commit;
            }
        }

        if (commit.Outcome == ChunkCommitOutcome.LeaseLost)
        {
            return commit;
        }

        var release = await new JobChunkRepository(dataSource).ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);
        return release switch
        {
            ChunkReleaseOutcome.Cancelled => commit with { Outcome = ChunkCommitOutcome.Cancelled },
            ChunkReleaseOutcome.ReturnedToPending => commit with { Outcome = ChunkCommitOutcome.JobNotRunning },
            _ => commit with { Outcome = ChunkCommitOutcome.LeaseLost },
        };
    }

    public async Task<ReindexTaskProgress> GetTaskProgressAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT count(*), count(*) FILTER (WHERE status = 5), count(*) FILTER (WHERE status = 6)
            FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND job_id = @job AND task_kind = @kind
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("kind", (short)IndexTaskKind.Reindex);
        ReindexTaskProgress progress;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            progress = new ReindexTaskProgress(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return progress;
    }

    public Task<DocumentVersionPage> ReadVersionsAsync(Guid workspaceId, Guid? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadVersionPageAsync(workspaceId,
            """
            SELECT document_id, document_version, is_deleted FROM opportunity.document_projection_state
            WHERE workspace_id = @ws AND (@after::uuid IS NULL OR document_id > @after)
            ORDER BY document_id LIMIT @limit
            """,
            c =>
            {
                c.Parameters.Add(Nullable("after", NpgsqlDbType.Uuid, after));
                c.Parameters.AddWithValue("limit", limit);
            },
            cancellationToken);
    }

    public Task<DocumentVersionPage> ReadVersionsAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        return ReadVersionPageAsync(workspaceId,
            """
            SELECT document_id, document_version, is_deleted FROM opportunity.document_projection_state
            WHERE workspace_id = @ws AND document_id = ANY(@ids) ORDER BY document_id
            """,
            c => c.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = documentIds.ToArray() }),
            cancellationToken);
    }

    public async Task<(long Documents, long Generation)> CountDocumentsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT (SELECT count(*) FROM opportunity.document_projection_state WHERE workspace_id = @ws AND NOT is_deleted), {Counter}
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        (long, long) result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            result = (reader.GetInt64(0), reader.GetInt64(1));
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<DocumentVersionPage> ReadVersionPageAsync(
        Guid workspaceId, string sql, Action<NpgsqlCommand> parameters, CancellationToken cancellationToken)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        long generation;
        await using (var counter = tx.Command($"SELECT {Counter}"))
        {
            counter.Parameters.AddWithValue("ws", workspaceId);
            generation = (long)(await counter.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var documents = new List<DocumentVersionState>();
        await using (var command = tx.Command(sql))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            parameters(command);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                documents.Add(new DocumentVersionState(reader.GetGuid(0), reader.GetInt64(1), reader.GetBoolean(2)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentVersionPage(documents, generation);
    }

    private static async Task<ReindexRun?> SelectAsync(
        WorkspaceTransaction tx, string where, Action<NpgsqlCommand> parameters, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"SELECT {Columns} FROM opportunity.search_reindex WHERE workspace_id = @ws AND {where} ORDER BY created_at DESC LIMIT 1");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        parameters(command);
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ReindexRun?> ReadOneAsync(NpgsqlCommand command, CancellationToken cancellationToken) =>
        (await ReadAllAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    private static async Task<IReadOnlyList<ReindexRun>> ReadAllAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var runs = new List<ReindexRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(new ReindexRun
            {
                WorkspaceId = reader.GetGuid(0),
                JobId = reader.GetGuid(1),
                Phase = (ReindexPhase)reader.GetInt16(2),
                Request = new ReindexRequest(
                    reader.IsDBNull(3) ? null : (IndexPlacementKind)reader.GetInt16(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5)),
                Source = ReadLocation(reader, 6),
                Target = ReadLocation(reader, 10),
                NextStepAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
                RetainUntil = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
                DocumentsPlanned = reader.IsDBNull(16) ? null : reader.GetInt64(16),
                Validation = reader.IsDBNull(17) ? null : ReindexValidation.FromJson(JsonNode.Parse(reader.GetString(17)) as JsonObject),
                Error = reader.IsDBNull(18) ? null : reader.GetString(18),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(19),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(20),
                SwitchedAt = reader.IsDBNull(21) ? null : reader.GetFieldValue<DateTimeOffset>(21),
                FinishedAt = reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22),
            });
        }

        return runs;
    }

    private static IndexLocation? ReadLocation(NpgsqlDataReader reader, int first) => reader.IsDBNull(first)
        ? null
        : new IndexLocation(
            (IndexPlacementKind)reader.GetInt16(first),
            reader.IsDBNull(first + 1) ? null : reader.GetInt32(first + 1),
            reader.GetInt32(first + 2),
            reader.IsDBNull(first + 3) ? 0 : reader.GetInt32(first + 3));

    private static void BindLocation(NpgsqlCommand command, string prefix, IndexLocation? location)
    {
        command.Parameters.Add(Nullable($"{prefix}_kind", NpgsqlDbType.Smallint, location is null ? null : (short?)location.Kind));
        command.Parameters.Add(Nullable($"{prefix}_pool", NpgsqlDbType.Integer, location?.SharedPool));
        command.Parameters.Add(Nullable($"{prefix}_generation", NpgsqlDbType.Integer, location?.Generation));
        command.Parameters.Add(Nullable($"{prefix}_revision", NpgsqlDbType.Integer, location?.Revision));
    }

    private static NpgsqlParameter Nullable<T>(string name, NpgsqlDbType type, T? value) =>
        new(name, type) { Value = (object?)value ?? DBNull.Value };

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

public static class ReindexStoreRegistration
{
    /// <summary>Registers the PostgreSQL <see cref="IReindexStore"/> (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresReindexStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IReindexStore, ReindexStore>();
        return services;
    }
}
