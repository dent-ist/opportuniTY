using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Backup;
using Opportunity.Core.Workspaces;

namespace Opportunity.Data.Backup;

/// <summary>
/// PostgreSQL side of the restore tool (ADR-016 §6–§8, design §4.3–§4.5). The reconcile statements run per workspace in
/// one transaction through <see cref="WorkspaceTransaction"/>, while every writer is stopped; they only move rows back
/// to Pending and lower the visible watermark, so the existing dispatcher and index workers re-drive the work with the
/// same external versions.
/// </summary>
public sealed class RestoreStore(NpgsqlDataSource dataSource) : IRestoreStore
{
    private const string RunColumns =
        "restore_id, path, target_time, backup_label, snapshot_name, operator_name, phase, started_at, record_key, record_sha256, report";

    public async Task<RestoreRun?> GetRunAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var run = await ReadRunAsync(tx, restoreId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async Task<RestoreRun> BeginRunAsync(RestoreRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.restore_run (restore_id, path, target_time, backup_label, snapshot_name, operator_name)
            VALUES (@id, @path, @target, @label, @snapshot, @operator)
            ON CONFLICT (restore_id) DO NOTHING
            """))
        {
            insert.Parameters.AddWithValue("id", run.RestoreId);
            insert.Parameters.AddWithValue("path", run.Path.ToString());
            insert.Parameters.Add(new NpgsqlParameter("target", NpgsqlDbType.TimestampTz) { Value = (object?)run.TargetTime ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("label", NpgsqlDbType.Text) { Value = (object?)run.BackupLabel ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("snapshot", NpgsqlDbType.Text) { Value = (object?)run.SnapshotName ?? DBNull.Value });
            insert.Parameters.AddWithValue("operator", run.OperatorName);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var stored = await ReadRunAsync(tx, run.RestoreId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The restore run was not stored.");
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task UpdateRunAsync(
        Guid restoreId, RestorePhase phase, JsonObject? report, string? recordKey, string? recordSha256, CancellationToken cancellationToken = default)
    {
        var stamp = phase switch
        {
            RestorePhase.Reconciled => "reconciled_at = now(),",
            RestorePhase.Recorded => "recorded_at = now(),",
            RestorePhase.Verified => "verified_at = now(),",
            _ => string.Empty,
        };
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.restore_run
            SET phase = @phase, {stamp} report = coalesce(@report, report), record_key = coalesce(@key, record_key),
                record_sha256 = coalesce(@sha, record_sha256), updated_at = now()
            WHERE restore_id = @id
            """);
        command.Parameters.AddWithValue("id", restoreId);
        command.Parameters.AddWithValue("phase", phase.ToString());
        command.Parameters.Add(new NpgsqlParameter("report", NpgsqlDbType.Jsonb) { Value = (object?)report?.ToJsonString() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)recordKey ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("sha", NpgsqlDbType.Text) { Value = (object?)recordSha256 ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RestoreWorkspace>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT workspace_id, status FROM opportunity.workspace ORDER BY workspace_id");
        var workspaces = new List<RestoreWorkspace>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                workspaces.Add(new RestoreWorkspace(reader.GetGuid(0), Enum.Parse<WorkspaceStatus>(reader.GetString(1))));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return workspaces;
    }

    public async Task<WorkspaceReconcileResult> ReconcileWorkspaceAsync(Guid workspaceId, long? anchor, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        long outbox = 0, tasks = 0;
        long? before = null, after = null;
        if (anchor is { } g)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(g, nameof(anchor));

            // Design §4.3: every work record above the anchor goes back to Pending (Applied, Failed and in-flight alike).
            outbox = await ExecuteAsync(tx,
                """
                UPDATE opportunity.search_outbox
                   SET status = 1, claim_owner = NULL, claim_expires_at = NULL, dispatched_at = NULL, applied_at = NULL,
                       attempt_count = 0, available_at = now(), last_error = NULL, updated_at = now()
                 WHERE workspace_id = @ws AND search_generation > @g
                """, workspaceId, g, cancellationToken).ConfigureAwait(false);

            // Reindex tasks (task_kind 4) carry no generation; their runs are aborted below.
            tasks = await ExecuteAsync(tx,
                """
                UPDATE opportunity.index_chunk_task
                   SET status = 1, claim_owner = NULL, claim_expires_at = NULL, lease_owner = NULL, lease_expires_at = NULL,
                       dispatched_at = NULL, started_at = NULL, completed_at = NULL, attempt_count = 0, available_at = now(),
                       last_error = NULL, error_class = NULL, updated_at = now()
                 WHERE workspace_id = @ws AND search_generation > @g
                """, workspaceId, g, cancellationToken).ConfigureAwait(false);

            // ADR-016 B-4: the one sanctioned place where the visible watermark moves back.
            await using var watermark = tx.Command(
                """
                WITH old AS (SELECT indexed_through_generation FROM opportunity.workspace_search_watermark WHERE workspace_id = @ws FOR UPDATE)
                UPDATE opportunity.workspace_search_watermark w
                   SET indexed_through_generation = LEAST(w.indexed_through_generation, @g), refreshed_at = now(), updated_at = now()
                  FROM old
                 WHERE w.workspace_id = @ws
                RETURNING old.indexed_through_generation, w.indexed_through_generation
                """);
            watermark.Parameters.AddWithValue("ws", workspaceId);
            watermark.Parameters.AddWithValue("g", g);
            await using var reader = await watermark.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                before = reader.GetInt64(0);
                after = reader.GetInt64(1);
            }
        }

        // ADR-016 §6.2: redrive what the lost timeline had in flight (jobs redispatch without the age condition).
        var redriven = await ExecuteAsync(tx,
            """
            UPDATE opportunity.search_outbox
               SET status = 1, claim_owner = NULL, claim_expires_at = NULL, dispatched_at = NULL, updated_at = now()
             WHERE workspace_id = @ws AND status IN (2, 3)
            """, workspaceId, null, cancellationToken).ConfigureAwait(false);
        redriven += await ExecuteAsync(tx,
            """
            UPDATE opportunity.index_chunk_task
               SET status = CASE WHEN status IN (2, 3) THEN 1 ELSE status END, claim_owner = NULL, claim_expires_at = NULL,
                   lease_owner = NULL, lease_expires_at = NULL,
                   dispatched_at = CASE WHEN status IN (2, 3) THEN NULL ELSE dispatched_at END,
                   started_at = CASE WHEN status IN (2, 3) THEN NULL ELSE started_at END, updated_at = now()
             WHERE workspace_id = @ws AND (status IN (2, 3) OR claim_owner IS NOT NULL)
            """, workspaceId, null, cancellationToken).ConfigureAwait(false);
        redriven += await ExecuteAsync(tx,
            """
            UPDATE opportunity.job_chunk
               SET status = CASE WHEN status IN (2, 3) THEN 1 ELSE status END, claim_owner = NULL, claim_expires_at = NULL,
                   lease_owner = NULL, lease_expires_at = NULL,
                   dispatched_at = CASE WHEN status IN (2, 3) THEN NULL ELSE dispatched_at END, updated_at = now()
             WHERE workspace_id = @ws AND (status IN (2, 3) OR claim_owner IS NOT NULL)
            """, workspaceId, null, cancellationToken).ConfigureAwait(false);

        // ADR-016 §7.2–§7.3: break-glass from the past ends; point-in-time readers are gone with the old cluster.
        var breakGlass = await ExecuteAsync(tx,
            """
            UPDATE opportunity.break_glass_activation SET ended_at = now(), ended_reason = 'Revoked'
             WHERE workspace_id = @ws AND ended_at IS NULL
            """, workspaceId, null, cancellationToken).ConfigureAwait(false);
        var sessions = await ExecuteAsync(tx,
            "DELETE FROM opportunity.search_session WHERE workspace_id = @ws", workspaceId, null, cancellationToken).ConfigureAwait(false);

        // Design §4.5: a rebuild or move that was not terminal is aborted by its coordinator once the job is cancelling.
        var reindexes = await ExecuteAsync(tx,
            """
            UPDATE opportunity.job j SET status = 'Cancelling', updated_at = now()
              FROM opportunity.search_reindex r
             WHERE r.workspace_id = @ws AND j.workspace_id = @ws AND j.job_id = r.job_id AND r.phase BETWEEN 1 AND 5
               AND j.status NOT IN ('Cancelling', 'Cancelled', 'Completed', 'CompletedWithErrors', 'Failed')
            """, workspaceId, null, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(tx,
            """
            UPDATE opportunity.search_reindex SET lease_owner = NULL, lease_expires_at = NULL, updated_at = now()
             WHERE workspace_id = @ws AND lease_owner IS NOT NULL
            """, workspaceId, null, cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceReconcileResult(workspaceId, anchor, outbox, tasks, before, after, redriven, breakGlass, sessions, reindexes);
    }

    public async Task<long> RevokeSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            "UPDATE opportunity.user_session SET revoked_at = now(), revoked_reason = 'Restore' WHERE revoked_at IS NULL");
        long revoked = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Deletion runs of the lost timeline may hold a lease; the restored coordinator resumes them at once.
        await using var leases = tx.Command(
            "UPDATE opportunity.workspace_deletion SET lease_owner = NULL, lease_expires_at = NULL WHERE lease_owner IS NOT NULL");
        await leases.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revoked;
    }

    public async Task<long> CountLiveSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT count(*) FROM opportunity.user_session WHERE revoked_at IS NULL");
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public async Task<WorkspaceRestoreState> InspectWorkspaceAsync(Guid workspaceId, long? anchor, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, System.Data.IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT (SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND status IN (2, 3))
                 + (SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND (status IN (2, 3) OR claim_owner IS NOT NULL))
                 + (SELECT count(*) FROM opportunity.job_chunk WHERE workspace_id = @ws AND (status IN (2, 3) OR claim_owner IS NOT NULL)),
                   coalesce((SELECT indexed_through_generation FROM opportunity.workspace_search_watermark WHERE workspace_id = @ws), 0),
                   coalesce((SELECT value FROM opportunity.workspace_search_generation WHERE workspace_id = @ws), 0),
                   CASE WHEN @g::bigint IS NULL THEN 0 ELSE
                     (SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND search_generation > @g AND status <> 1)
                   + (SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND search_generation > @g AND status <> 1)
                   END,
                   (SELECT count(*) FROM opportunity.break_glass_activation WHERE workspace_id = @ws AND ended_at IS NULL),
                   (SELECT count(*) FROM opportunity.search_session WHERE workspace_id = @ws),
                   (SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND status = 5)
                 + (SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND status = 6)
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(new NpgsqlParameter("g", NpgsqlDbType.Bigint) { Value = (object?)anchor ?? DBNull.Value });
        WorkspaceRestoreState state;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            state = new WorkspaceRestoreState(workspaceId, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetDeletionStatusesAsync(
        IReadOnlyCollection<Guid> deletionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deletionIds);
        var statuses = new Dictionary<Guid, string>();
        if (deletionIds.Count == 0)
        {
            return statuses;
        }

        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT deletion_id, status FROM opportunity.workspace_deletion WHERE deletion_id = ANY(@ids)");
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = deletionIds.ToArray() });
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                statuses[reader.GetGuid(0)] = reader.GetString(1);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return statuses;
    }

    public async Task<bool> CloseWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.workspace SET status = '{nameof(WorkspaceStatus.Closed)}'
             WHERE workspace_id = @ws AND status = '{nameof(WorkspaceStatus.Active)}'
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<IReadOnlyList<StoredObjectEntry>> SampleStoredObjectsAsync(
        Guid workspaceId, DateTimeOffset createdFrom, int sampleOfOlder, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleOfOlder);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            (SELECT logical_key, size_bytes, created_at FROM opportunity.stored_object WHERE workspace_id = @ws AND created_at >= @from)
            UNION ALL
            (SELECT logical_key, size_bytes, created_at FROM opportunity.stored_object TABLESAMPLE BERNOULLI (@percent)
              WHERE workspace_id = @ws AND created_at < @from LIMIT @sample)
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("from", createdFrom);
        command.Parameters.AddWithValue("sample", sampleOfOlder);
        command.Parameters.AddWithValue("percent", await SamplePercentAsync(tx, workspaceId, createdFrom, sampleOfOlder, cancellationToken).ConfigureAwait(false));
        var entries = new List<StoredObjectEntry>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new StoredObjectEntry(reader.GetString(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entries;
    }

    public async Task<IReadOnlySet<string>> RegisteredKeysAsync(Guid workspaceId, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return found;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT logical_key FROM opportunity.stored_object WHERE workspace_id = @ws AND logical_key = ANY(@keys)");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = keys.ToArray() });
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found.Add(reader.GetString(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    public async Task<(IReadOnlySet<Guid> Productions, IReadOnlySet<Guid> Exports)> KnownOutputsAsync(
        Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var productions = new HashSet<Guid>();
        var exports = new HashSet<Guid>();
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            SELECT 1, production_id FROM opportunity.production WHERE workspace_id = @ws
            UNION ALL
            SELECT 2, export_id FROM opportunity.export WHERE workspace_id = @ws
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                (reader.GetInt32(0) == 1 ? productions : exports).Add(reader.GetGuid(1));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (productions, exports);
    }

    private static async Task<double> SamplePercentAsync(
        WorkspaceTransaction tx, Guid workspaceId, DateTimeOffset createdFrom, int sample, CancellationToken cancellationToken)
    {
        if (sample == 0)
        {
            return 0;
        }

        await using var count = tx.Command("SELECT count(*) FROM opportunity.stored_object WHERE workspace_id = @ws AND created_at < @from");
        count.Parameters.AddWithValue("ws", workspaceId);
        count.Parameters.AddWithValue("from", createdFrom);
        var older = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        // Oversample a little; LIMIT trims it. Small workspaces are checked completely.
        return older <= sample ? 100 : Math.Min(100, 100.0 * sample * 1.2 / older);
    }

    private static async Task<long> ExecuteAsync(WorkspaceTransaction tx, string sql, Guid workspaceId, long? generation, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", workspaceId);
        if (generation is { } g)
        {
            command.Parameters.AddWithValue("g", g);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RestoreRun?> ReadRunAsync(WorkspaceTransaction tx, Guid restoreId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command($"SELECT {RunColumns} FROM opportunity.restore_run WHERE restore_id = @id");
        command.Parameters.AddWithValue("id", restoreId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new RestoreRun(
            reader.GetGuid(0),
            Enum.Parse<RestorePath>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            Enum.Parse<RestorePhase>(reader.GetString(6)),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : JsonNode.Parse(reader.GetString(10)) as JsonObject);
    }
}
