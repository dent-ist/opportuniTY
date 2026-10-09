using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Workspaces;
using Opportunity.Application.Workspaces.Deletion;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Workspaces;

/// <summary>
/// PostgreSQL <see cref="IWorkspaceDeletionStore"/> (E20-T02, V0054). Deletion rows are installation-level; every write
/// that carries a workspace audit event runs in that workspace's transaction so the event lands in its chain. Run-side
/// writes require the caller's lease; destructive ones re-check the legal hold in the same transaction
/// (<c>assert_workspace_not_preserved</c>), and the purge itself goes through the owner-run functions of V0054.
/// </summary>
public sealed class WorkspaceDeletionStore(NpgsqlDataSource dataSource) : IWorkspaceDeletionStore
{
    public const string JobCancellationReason = "Cancelled: the workspace is being deleted.";

    private const string Columns =
        """
        d.deletion_id, d.deletion_workspace_id, d.workspace_name, d.matter_number, d.retention_profile, d.reason, d.external_reference,
        d.status, d.step, d.requested_by, d.requested_at, d.expires_at, d.approved_by, d.approved_at, d.approval_note, d.run_not_before,
        d.cancelled_by, d.cancelled_at, d.fence_epoch, d.started_at, d.finished_at, d.next_step_at, d.halted_at, d.error,
        d.lease_owner, d.lease_expires_at, d.version,
        EXISTS (SELECT FROM opportunity.destruction_certificate c WHERE c.deletion_id = d.deletion_id),
        (SELECT coalesce(u.display_name, u.email) FROM opportunity.app_user u WHERE u.user_id = d.requested_by),
        (SELECT coalesce(u.display_name, u.email) FROM opportunity.app_user u WHERE u.user_id = d.approved_by),
        (SELECT coalesce(u.display_name, u.email) FROM opportunity.app_user u WHERE u.user_id = d.cancelled_by)
        """;

    private const string OpenStatuses = "'Requested', 'Approved', 'Running', 'Halted'";

    private const string LeaseLost = "The lease on this workspace deletion was lost.";

    private readonly ConcurrentDictionary<Guid, Guid> _workspaces = new();

    // ---- Requests ---------------------------------------------------------------------------------------------------

    public async Task<DeletionWriteResult> CreateAsync(WorkspaceDeletion request, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, request.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await PreservationLockViolation.EnsureNotLockedAsync(tx, "workspace", cancellationToken).ConfigureAwait(false);
        int inserted;
        await using (var insert = tx.Command(
            $"""
            INSERT INTO opportunity.workspace_deletion
                (deletion_id, deletion_workspace_id, workspace_name, matter_number, retention_profile, reason, external_reference,
                 status, requested_by, requested_at, expires_at)
            SELECT @id, w.workspace_id, @name, @matter, @profile, @reason, @reference, 'Requested', @by, @at, @expires
              FROM opportunity.workspace w
             WHERE w.workspace_id = @ws AND w.status IN ('Active', 'Closed')
            ON CONFLICT (deletion_workspace_id) WHERE status IN ({OpenStatuses}) DO NOTHING
            """))
        {
            insert.Parameters.AddWithValue("id", request.DeletionId);
            insert.Parameters.AddWithValue("ws", request.WorkspaceId);
            insert.Parameters.AddWithValue("name", request.WorkspaceName);
            insert.Parameters.Add(Text("matter", request.MatterNumber));
            insert.Parameters.AddWithValue("profile", (short)request.RetentionProfile);
            insert.Parameters.AddWithValue("reason", request.Reason);
            insert.Parameters.Add(Text("reference", request.ExternalReference));
            insert.Parameters.AddWithValue("by", request.RequestedBy);
            insert.Parameters.AddWithValue("at", request.RequestedAt);
            insert.Parameters.AddWithValue("expires", request.ExpiresAt);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (inserted == 0)
        {
            var open = (await ReadAsync(tx, $"d.deletion_workspace_id = @ws AND d.status IN ({OpenStatuses})", cancellationToken,
                ("ws", request.WorkspaceId)).ConfigureAwait(false)).FirstOrDefault();
            return new DeletionWriteResult(open is null ? DeletionWriteOutcome.NotFound : DeletionWriteOutcome.Conflict, open);
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = await ReadOneAsync(tx, request.DeletionId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        _workspaces[request.DeletionId] = request.WorkspaceId;
        return new DeletionWriteResult(DeletionWriteOutcome.Ok, saved);
    }

    public async Task<WorkspaceDeletion?> GetAsync(Guid deletionId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var found = await ReadOneAsync(tx, deletionId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    public async Task<IReadOnlyList<WorkspaceDeletion>> ListAsync(WorkspaceDeletionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var filter = "(@by::uuid IS NULL OR d.requested_by = @by)" + (query.OpenOnly ? $" AND d.status IN ({OpenStatuses})" : string.Empty);
        var found = await ReadAsync(tx, filter + " ORDER BY d.requested_at DESC, d.deletion_id DESC LIMIT @limit", cancellationToken,
            ("by", (object?)query.RequestedBy ?? DBNull.Value), ("limit", Math.Clamp(query.Limit, 1, 1000))).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    public async Task<IReadOnlyList<WorkspaceDeletion>> ListForWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var found = await ReadAsync(tx, "d.deletion_workspace_id = @ws ORDER BY d.requested_at DESC, d.deletion_id DESC LIMIT 100", cancellationToken,
            ("ws", workspaceId)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    public async Task<DeletionWriteResult> ApproveAsync(
        Guid deletionId, long expectedVersion, Guid approvedBy, string? note, DateTimeOffset approvedAt, DateTimeOffset runNotBefore,
        AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (await WorkspaceOfAsync(deletionId, cancellationToken).ConfigureAwait(false) is not { } workspaceId)
        {
            return new DeletionWriteResult(DeletionWriteOutcome.NotFound);
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await PreservationLockViolation.EnsureNotLockedAsync(tx, "workspace", cancellationToken).ConfigureAwait(false);
        return await TransitionAsync(tx, deletionId, expectedVersion,
            """
            status = 'Approved', approved_by = @actor, approved_at = @at, approval_note = @note, run_not_before = @run_not_before
            """,
            "d.status = 'Requested' AND d.expires_at > @at AND d.requested_by <> @actor", audit, cancellationToken,
            ("actor", approvedBy), ("at", approvedAt), ("note", (object?)note ?? DBNull.Value), ("run_not_before", runNotBefore)).ConfigureAwait(false);
    }

    public async Task<DeletionWriteResult> CancelAsync(
        Guid deletionId, long expectedVersion, Guid cancelledBy, DateTimeOffset cancelledAt, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (await WorkspaceOfAsync(deletionId, cancellationToken).ConfigureAwait(false) is not { } workspaceId)
        {
            return new DeletionWriteResult(DeletionWriteOutcome.NotFound);
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        return await TransitionAsync(tx, deletionId, expectedVersion,
            "status = 'Cancelled', cancelled_by = @actor, cancelled_at = @at, finished_at = @at",
            "d.status IN ('Requested', 'Approved')", audit, cancellationToken, ("actor", cancelledBy), ("at", cancelledAt)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkspaceDeletionStepRecord>> GetStepsAsync(Guid deletionId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT step, attempt, started_at, finished_at, outcome, counts::text, detail::text
            FROM opportunity.workspace_deletion_step WHERE deletion_id = @id ORDER BY started_at, step
            """);
        command.Parameters.AddWithValue("id", deletionId);
        var steps = new List<WorkspaceDeletionStepRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!Enum.TryParse<DeletionStep>(reader.GetString(0), out var step))
                {
                    continue;
                }

                steps.Add(new WorkspaceDeletionStepRecord(
                    step,
                    reader.GetInt32(1),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : JsonNode.Parse(reader.GetString(5))?.AsObject(),
                    reader.IsDBNull(6) ? null : JsonNode.Parse(reader.GetString(6))?.AsObject()));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return steps;
    }

    public async Task<StoredDestructionCertificate?> GetCertificateAsync(Guid deletionId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT certificate_workspace_id, issued_at, sha256, certificate, object_key, signature_key_id, signature
            FROM opportunity.destruction_certificate WHERE deletion_id = @id
            """);
        command.Parameters.AddWithValue("id", deletionId);
        StoredDestructionCertificate? found = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found = new StoredDestructionCertificate(
                    deletionId,
                    reader.GetGuid(0),
                    reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetFieldValue<byte[]>(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    // ---- The run ------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT deletion_id FROM opportunity.workspace_deletion
             WHERE (status = 'Requested' AND expires_at <= @now)
                OR (status = 'Approved' AND run_not_before <= @now)
                OR status IN ('Running', 'Halted')
             ORDER BY requested_at, deletion_id
            """);
        command.Parameters.AddWithValue("now", now);
        var ids = new List<Guid>();
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

    public async Task<WorkspaceDeletion?> TryLeaseAsync(Guid deletionId, string owner, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        int leased;
        await using (var command = tx.Command(
            """
            UPDATE opportunity.workspace_deletion
               SET lease_owner = @owner, lease_expires_at = now() + @duration
             WHERE deletion_id = @id AND (lease_owner IS NULL OR lease_owner = @owner OR lease_expires_at < now())
            """))
        {
            command.Parameters.AddWithValue("id", deletionId);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("duration", duration);
            leased = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var found = leased == 1 ? await ReadOneAsync(tx, deletionId, cancellationToken).ConfigureAwait(false) : null;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (found is not null)
        {
            _workspaces[deletionId] = found.WorkspaceId;
        }

        return found;
    }

    public async Task ReleaseLeaseAsync(Guid deletionId, string owner, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await UpdateAsync(tx,
            "UPDATE opportunity.workspace_deletion SET lease_owner = NULL, lease_expires_at = NULL WHERE deletion_id = @id AND lease_owner = @owner",
            cancellationToken, ("id", deletionId), ("owner", owner)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ExpireAsync(Guid deletionId, string owner, DateTimeOffset now, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await BeginRunAsync(deletionId, cancellationToken).ConfigureAwait(false);
        var updated = await UpdateAsync(tx,
            """
            UPDATE opportunity.workspace_deletion
               SET status = 'Expired', finished_at = @now, version = version + 1, updated_at = now()
             WHERE deletion_id = @id AND lease_owner = @owner AND status = 'Requested' AND expires_at <= @now
            """, cancellationToken, ("id", deletionId), ("owner", owner), ("now", now)).ConfigureAwait(false);
        if (updated == 0)
        {
            return false;
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<DeletionStartOutcome> StartAsync(Guid deletionId, string owner, DateTimeOffset now, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await BeginRunAsync(deletionId, cancellationToken).ConfigureAwait(false);
        var current = await ReadOneAsync(tx, deletionId, cancellationToken, forUpdate: true).ConfigureAwait(false);
        if (current is null || current.LeaseOwner != owner)
        {
            return DeletionStartOutcome.LeaseLost;
        }

        if (current.Status != WorkspaceDeletionStatus.Approved || current.RunNotBefore > now)
        {
            return DeletionStartOutcome.NotReady;
        }

        // The fence: the workspace row FOR UPDATE waits for every transaction that is inserting work for it (the write
        // fence takes FOR KEY SHARE) and every later one sees Deleting.
        int held;
        await using (var lockRow = tx.Command("SELECT active_preservation_locks FROM opportunity.workspace WHERE workspace_id = @ws FOR UPDATE"))
        {
            lockRow.Parameters.AddWithValue("ws", tx.WorkspaceId);
            held = (int)(await lockRow.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (held > 0)
        {
            // Not started; the reason shows on the deletion and is audited once (ADR-014 §2.3: runs are refused under a hold).
            const string Waiting = "Waiting: the workspace is under a legal hold.";
            if (current.Error != Waiting)
            {
                await UpdateAsync(tx, "UPDATE opportunity.workspace_deletion SET error = @error, updated_at = now() WHERE deletion_id = @id",
                    cancellationToken, ("id", deletionId), ("error", Waiting)).ConfigureAwait(false);
                await AuditSql.InsertAsync(tx, audit with
                {
                    Action = AuditTaxonomy.Workspace.DeletionBlocked,
                    Outcome = AuditOutcome.Denied,
                    ReasonCode = "LegalHold",
                }, cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return DeletionStartOutcome.Held;
        }

        long epoch;
        await using (var fence = tx.Command(
            """
            UPDATE opportunity.workspace
               SET status = 'Deleting', epoch = epoch + 1, closed_at = coalesce(closed_at, now()), updated_at = now()
             WHERE workspace_id = @ws AND status IN ('Active', 'Closed')
            RETURNING epoch
            """))
        {
            fence.Parameters.AddWithValue("ws", tx.WorkspaceId);
            if (await fence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long fenced)
            {
                return DeletionStartOutcome.NotReady;
            }

            epoch = fenced;
        }

        await UpdateAsync(tx,
            """
            UPDATE opportunity.workspace_deletion
               SET status = 'Running', step = @step, started_at = @now, fence_epoch = @epoch, error = NULL, version = version + 1, updated_at = now()
             WHERE deletion_id = @id
            """, cancellationToken, ("id", deletionId), ("step", nameof(DeletionStep.Drain)), ("now", now), ("epoch", epoch)).ConfigureAwait(false);
        await UpdateAsync(tx,
            """
            INSERT INTO opportunity.workspace_deletion_step (deletion_id, step, started_at, finished_at, outcome, counts)
            VALUES (@id, @step, @now, @now, 'Success', jsonb_build_object('epoch', @epoch::bigint))
            ON CONFLICT (deletion_id, step) DO NOTHING
            """, cancellationToken, ("id", deletionId), ("step", nameof(DeletionStep.Fence)), ("now", now), ("epoch", epoch)).ConfigureAwait(false);
        var details = new Dictionary<string, string?>(audit.Details)
        {
            ["epoch"] = epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await AuditSql.InsertAsync(tx, audit with { Details = details }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return DeletionStartOutcome.Started;
    }

    public async Task<WorkspaceDeletionStepRecord> BeginStepAsync(
        Guid deletionId, string owner, DeletionStep deletionStep, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginRunAsync(deletionId, cancellationToken).ConfigureAwait(false);
        await RequireLeaseAsync(tx, deletionId, owner, cancellationToken).ConfigureAwait(false);
        await PreservationLockViolation.EnsureNotLockedAsync(tx, "workspace deletion: " + deletionStep, cancellationToken).ConfigureAwait(false);
        await UpdateAsync(tx,
            """
            INSERT INTO opportunity.workspace_deletion_step (deletion_id, step) VALUES (@id, @step)
            ON CONFLICT (deletion_id, step) DO NOTHING
            """, cancellationToken, ("id", deletionId), ("step", deletionStep.ToString())).ConfigureAwait(false);
        WorkspaceDeletionStepRecord record;
        await using (var read = tx.Command(
            "SELECT attempt, started_at, finished_at, outcome FROM opportunity.workspace_deletion_step WHERE deletion_id = @id AND step = @step"))
        {
            read.Parameters.AddWithValue("id", deletionId);
            read.Parameters.AddWithValue("step", deletionStep.ToString());
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            record = new WorkspaceDeletionStepRecord(deletionStep, reader.GetInt32(0), reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetString(3), null, null);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task CompleteStepAsync(
        Guid deletionId, string owner, DeletionStep deletionStep, string outcome, JsonObject? counts, JsonObject? detail, DeletionStep? nextStep,
        DateTimeOffset? nextStepAt, AuditEvent? audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        await using var tx = await BeginRunAsync(deletionId, cancellationToken).ConfigureAwait(false);
        await RequireLeaseAsync(tx, deletionId, owner, cancellationToken).ConfigureAwait(false);
        await using (var step = tx.Command(
            """
            UPDATE opportunity.workspace_deletion_step
               SET finished_at = now(), outcome = @outcome, counts = @counts::jsonb, detail = @detail::jsonb
             WHERE deletion_id = @id AND step = @step
            """))
        {
            step.Parameters.AddWithValue("id", deletionId);
            step.Parameters.AddWithValue("step", deletionStep.ToString());
            step.Parameters.AddWithValue("outcome", outcome);
            step.Parameters.Add(Text("counts", counts?.ToJsonString()));
            step.Parameters.Add(Text("detail", detail?.ToJsonString()));
            if (await step.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException($"Step {deletionStep} of deletion {deletionId} was never started.");
            }
        }

        await UpdateAsync(tx,
            """
            UPDATE opportunity.workspace_deletion
               SET step = coalesce(@next, step), next_step_at = coalesce(@next_at, next_step_at), error = NULL, updated_at = now()
             WHERE deletion_id = @id
            """, cancellationToken, ("id", deletionId), ("next", (object?)nextStep?.ToString() ?? DBNull.Value),
            ("next_at", new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)nextStepAt ?? DBNull.Value })).ConfigureAwait(false);
        if (audit is not null)
        {
            await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task HaltAsync(Guid deletionId, string owner, DateTimeOffset now, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await BeginRunAsync(deletionId, cancellationToken).ConfigureAwait(false);
        var updated = await UpdateAsync(tx,
            """
            UPDATE opportunity.workspace_deletion
               SET status = 'Halted', halted_at = @now, error = 'Halted: the workspace was placed under a legal hold.',
                   version = version + 1, updated_at = now()
             WHERE deletion_id = @id AND lease_owner = @owner AND status = 'Running'
            """, cancellationToken, ("id", deletionId), ("owner", owner), ("now", now)).ConfigureAwait(false);
        if (updated == 1)
        {
            await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> ResumeAsync(Guid deletionId, string owner, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await BeginRunAsync(deletionId, cancellationToken).ConfigureAwait(false);
        await using (var held = tx.Command("SELECT active_preservation_locks FROM opportunity.workspace WHERE workspace_id = @ws FOR SHARE"))
        {
            held.Parameters.AddWithValue("ws", tx.WorkspaceId);
            if ((int)(await held.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0)
            {
                return false;
            }
        }

        var updated = await UpdateAsync(tx,
            """
            UPDATE opportunity.workspace_deletion SET status = 'Running', error = NULL, version = version + 1, updated_at = now()
             WHERE deletion_id = @id AND lease_owner = @owner AND status = 'Halted'
            """, cancellationToken, ("id", deletionId), ("owner", owner)).ConfigureAwait(false);
        if (updated == 0)
        {
            return false;
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task RecordErrorAsync(Guid deletionId, string owner, string? message, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await UpdateAsync(tx,
            """
            UPDATE opportunity.workspace_deletion SET error = @error, updated_at = now()
             WHERE deletion_id = @id AND (lease_owner IS NULL OR lease_owner = @owner) AND status IN ('Approved', 'Running', 'Halted')
            """, cancellationToken, ("id", deletionId), ("owner", owner),
            ("error", (object?)(message is { Length: > 2000 } ? message[..2000] : message) ?? DBNull.Value)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CancelJobsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await UpdateAsync(tx,
            """
            UPDATE opportunity.job
               SET status = 'Cancelled', status_reason = @reason, finished_at = coalesce(finished_at, now()), updated_at = now()
             WHERE workspace_id = @ws AND status NOT IN ('Completed', 'CompletedWithErrors', 'Cancelled', 'Failed')
            """, cancellationToken, ("ws", workspaceId), ("reason", JobCancellationReason)).ConfigureAwait(false);
        int cancelled;
        await using (var count = tx.Command(
            "SELECT count(*) FROM opportunity.job WHERE workspace_id = @ws AND status = 'Cancelled' AND status_reason = @reason"))
        {
            count.Parameters.AddWithValue("ws", workspaceId);
            count.Parameters.AddWithValue("reason", JobCancellationReason);
            cancelled = (int)(long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return cancelled;
    }

    public async Task<WorkspaceWorkInFlight> CountWorkInFlightAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT
              (SELECT count(*) FROM opportunity.job_chunk WHERE workspace_id = @ws AND status NOT IN (5, 6, 7) AND lease_expires_at > now()),
              (SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND status NOT IN (5, 6)
                  AND (lease_expires_at > now() OR claim_expires_at > now())),
              (SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND status NOT IN (4, 5) AND claim_expires_at > now()),
              (SELECT count(*) FROM opportunity.document_set_snapshot WHERE workspace_id = @ws AND claimed_until > now())
              + (SELECT count(*) FROM opportunity.export WHERE workspace_id = @ws AND claimed_until > now())
              + (SELECT count(*) FROM opportunity.production WHERE workspace_id = @ws AND claimed_until > now())
              + (SELECT count(*) FROM opportunity.import_batch WHERE workspace_id = @ws AND prepare_claimed_until > now())
              + (SELECT count(*) FROM opportunity.search_term_report WHERE workspace_id = @ws AND claimed_until > now())
              + (SELECT count(*) FROM opportunity.search_reindex WHERE workspace_id = @ws AND lease_expires_at > now())
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        WorkspaceWorkInFlight result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            result = new WorkspaceWorkInFlight(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<(IReadOnlyList<string> Tables, IReadOnlyList<PurgeTableReference> References)> GetPurgeSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var tables = new List<string>();
        await using (var command = tx.Command("SELECT t FROM opportunity.workspace_purge_tables() AS t"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var references = new List<PurgeTableReference>();
        await using (var command = tx.Command(
            """
            SELECT child.relname::text, parent.relname::text, c.condeferrable
              FROM pg_constraint c
              JOIN pg_class child ON child.oid = c.conrelid
              JOIN pg_class parent ON parent.oid = c.confrelid
             WHERE c.contype = 'f' AND c.connamespace = 'opportunity'::regnamespace AND c.conparentid = 0
             ORDER BY 1, 2
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                references.Add(new PurgeTableReference(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (tables, references);
    }

    public async Task<long> PurgeBatchAsync(Guid deletionId, string table, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        await using var tx = await BeginRunAsync(deletionId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT opportunity.workspace_purge_batch(@id, @table, @batch)");
        command.Parameters.AddWithValue("id", deletionId);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("batch", batchSize);
        long deleted;
        try
        {
            deleted = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        catch (PostgresException ex) when (ex.SqlState == PreservationLockViolation.SqlState)
        {
            throw new PreservationLockedException(tx.WorkspaceId, table, ex);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public async Task<IReadOnlyList<PurgeTableCount>> CountRowsAsync(Guid deletionId, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginRunAsync(deletionId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT table_name, total, retained FROM opportunity.workspace_purge_counts(@id) ORDER BY 1");
        command.Parameters.AddWithValue("id", deletionId);
        var counts = new List<PurgeTableCount>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                counts.Add(new PurgeTableCount(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PreservationLockViolation.SqlState)
        {
            throw new PreservationLockedException(tx.WorkspaceId, "inventory", ex);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return counts;
    }

    public async Task<WorkspaceKeyCounts> CountKeysAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT count(*) FILTER (WHERE state = 1), count(*) FILTER (WHERE state = 2), count(*) FILTER (WHERE state = 3)
              FROM opportunity.workspace_data_key WHERE workspace_id = @ws
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        WorkspaceKeyCounts counts;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            counts = new WorkspaceKeyCounts((int)reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2));
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return counts;
    }

    public async Task CertifyAsync(
        Guid deletionId, string owner, StoredDestructionCertificate certificate, bool residuals, DateTimeOffset finishedAt,
        AuditEvent workspaceAudit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(workspaceAudit);
        await using var tx = await BeginRunAsync(deletionId, cancellationToken).ConfigureAwait(false);
        await RequireLeaseAsync(tx, deletionId, owner, cancellationToken).ConfigureAwait(false);
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.destruction_certificate
                (deletion_id, certificate_workspace_id, issued_at, sha256, certificate, object_key, signature_key_id, signature)
            VALUES (@id, @ws, @issued, @sha, @certificate, @key, @key_id, @signature)
            ON CONFLICT (deletion_id) DO NOTHING
            """))
        {
            insert.Parameters.AddWithValue("id", deletionId);
            insert.Parameters.AddWithValue("ws", certificate.WorkspaceId);
            insert.Parameters.AddWithValue("issued", certificate.IssuedAt);
            insert.Parameters.AddWithValue("sha", certificate.Sha256);
            insert.Parameters.AddWithValue("certificate", certificate.CertificateJson);
            insert.Parameters.Add(Text("key", certificate.ObjectKey));
            insert.Parameters.Add(Text("key_id", certificate.SignatureKeyId));
            insert.Parameters.Add(new NpgsqlParameter("signature", NpgsqlDbType.Bytea) { Value = (object?)certificate.Signature ?? DBNull.Value });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpdateAsync(tx, "UPDATE opportunity.workspace SET status = 'Purged', updated_at = now() WHERE workspace_id = @ws AND status = 'Deleting'",
            cancellationToken, ("ws", tx.WorkspaceId)).ConfigureAwait(false);
        await UpdateAsync(tx,
            """
            UPDATE opportunity.workspace_deletion_step SET finished_at = coalesce(finished_at, now()), outcome = coalesce(outcome, 'Success')
             WHERE deletion_id = @id AND step = @step
            """, cancellationToken, ("id", deletionId), ("step", nameof(DeletionStep.Certification))).ConfigureAwait(false);
        await UpdateAsync(tx,
            """
            UPDATE opportunity.workspace_deletion
               SET status = @status, finished_at = @finished, error = NULL, lease_owner = NULL, lease_expires_at = NULL,
                   version = version + 1, updated_at = now()
             WHERE deletion_id = @id
            """, cancellationToken, ("id", deletionId), ("finished", finishedAt),
            ("status", residuals ? nameof(WorkspaceDeletionStatus.CompletedWithResiduals) : nameof(WorkspaceDeletionStatus.Completed))).ConfigureAwait(false);
        await AuditSql.InsertAsync(tx, workspaceAudit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteInstallationAuditAsync(AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A transaction in the deletion's workspace (the run's audit events belong to its chain).</summary>
    private async Task<WorkspaceTransaction> BeginRunAsync(Guid deletionId, CancellationToken cancellationToken)
    {
        var workspaceId = await WorkspaceOfAsync(deletionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Deletion {deletionId} does not exist.");
        return await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid?> WorkspaceOfAsync(Guid deletionId, CancellationToken cancellationToken)
    {
        if (_workspaces.TryGetValue(deletionId, out var cached))
        {
            return cached;
        }

        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT deletion_workspace_id FROM opportunity.workspace_deletion WHERE deletion_id = @id");
        command.Parameters.AddWithValue("id", deletionId);
        var found = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (found is { } workspaceId)
        {
            _workspaces[deletionId] = workspaceId;
        }

        return found;
    }

    private static async Task RequireLeaseAsync(WorkspaceTransaction tx, Guid deletionId, string owner, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            "SELECT 1 FROM opportunity.workspace_deletion WHERE deletion_id = @id AND lease_owner = @owner AND status = 'Running' FOR UPDATE");
        command.Parameters.AddWithValue("id", deletionId);
        command.Parameters.AddWithValue("owner", owner);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(LeaseLost);
        }
    }

    /// <summary>A request-side transition guarded by version and state; audited in the same transaction.</summary>
    private static async Task<DeletionWriteResult> TransitionAsync(
        WorkspaceTransaction tx, Guid deletionId, long expectedVersion, string set, string guard, AuditEvent audit, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        var current = await ReadOneAsync(tx, deletionId, cancellationToken, forUpdate: true).ConfigureAwait(false);
        if (current is null)
        {
            return new DeletionWriteResult(DeletionWriteOutcome.NotFound);
        }

        if (current.Version != expectedVersion)
        {
            return new DeletionWriteResult(DeletionWriteOutcome.VersionConflict, current);
        }

        var updated = await UpdateAsync(tx,
            $"""
            UPDATE opportunity.workspace_deletion AS d SET {set}, version = version + 1, updated_at = now()
             WHERE d.deletion_id = @id AND d.version = @version AND {guard}
            """, cancellationToken, [.. parameters, ("id", deletionId), ("version", expectedVersion)]).ConfigureAwait(false);
        if (updated == 0)
        {
            return new DeletionWriteResult(DeletionWriteOutcome.Conflict, current);
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = await ReadOneAsync(tx, deletionId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DeletionWriteResult(DeletionWriteOutcome.Ok, saved);
    }

    private static async Task<int> UpdateAsync(
        WorkspaceTransaction tx, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = tx.Command(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(value switch
            {
                NpgsqlParameter typed => Named(typed, name),
                DBNull => new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value },
                _ => new NpgsqlParameter(name, value),
            });
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorkspaceDeletion?> ReadOneAsync(WorkspaceTransaction tx, Guid deletionId, CancellationToken cancellationToken, bool forUpdate = false) =>
        (await ReadAsync(tx, "d.deletion_id = @id" + (forUpdate ? " FOR UPDATE OF d" : string.Empty), cancellationToken, ("id", deletionId))
            .ConfigureAwait(false)).FirstOrDefault();

    private static async Task<List<WorkspaceDeletion>> ReadAsync(
        WorkspaceTransaction tx, string where, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.workspace_deletion d WHERE {where}");
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(value is DBNull ? new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = DBNull.Value } : new NpgsqlParameter(name, value));
        }

        var found = new List<WorkspaceDeletion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found.Add(new WorkspaceDeletion
            {
                DeletionId = reader.GetGuid(0),
                WorkspaceId = reader.GetGuid(1),
                WorkspaceName = reader.GetString(2),
                MatterNumber = reader.IsDBNull(3) ? null : reader.GetString(3),
                RetentionProfile = (DeletionRetentionProfile)reader.GetInt16(4),
                Reason = reader.GetString(5),
                ExternalReference = reader.IsDBNull(6) ? null : reader.GetString(6),
                Status = Enum.Parse<WorkspaceDeletionStatus>(reader.GetString(7)),
                Step = reader.IsDBNull(8) ? null : Enum.Parse<DeletionStep>(reader.GetString(8)),
                RequestedBy = reader.GetGuid(9),
                RequestedAt = reader.GetFieldValue<DateTimeOffset>(10),
                ExpiresAt = reader.GetFieldValue<DateTimeOffset>(11),
                ApprovedBy = reader.IsDBNull(12) ? null : reader.GetGuid(12),
                ApprovedAt = Time(reader, 13),
                ApprovalNote = reader.IsDBNull(14) ? null : reader.GetString(14),
                RunNotBefore = Time(reader, 15),
                CancelledBy = reader.IsDBNull(16) ? null : reader.GetGuid(16),
                CancelledAt = Time(reader, 17),
                FenceEpoch = reader.IsDBNull(18) ? null : reader.GetInt64(18),
                StartedAt = Time(reader, 19),
                FinishedAt = Time(reader, 20),
                NextStepAt = Time(reader, 21),
                HaltedAt = Time(reader, 22),
                Error = reader.IsDBNull(23) ? null : reader.GetString(23),
                LeaseOwner = reader.IsDBNull(24) ? null : reader.GetString(24),
                LeaseExpiresAt = Time(reader, 25),
                Version = reader.GetInt64(26),
                HasCertificate = reader.GetBoolean(27),
                RequestedByName = reader.IsDBNull(28) ? null : reader.GetString(28),
                ApprovedByName = reader.IsDBNull(29) ? null : reader.GetString(29),
                CancelledByName = reader.IsDBNull(30) ? null : reader.GetString(30),
            });
        }

        return found;
    }

    private static NpgsqlParameter Named(NpgsqlParameter parameter, string name)
    {
        parameter.ParameterName = name;
        return parameter;
    }

    private static DateTimeOffset? Time(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}

/// <summary>
/// Recognizes the database's write fence (SQLSTATE <c>O0410</c>, V0054): a workspace being deleted accepts no new jobs,
/// documents, objects or search work.
/// </summary>
public static class WorkspaceFenceViolation
{
    public const string SqlState = "O0410";

    public static bool TryGet(Exception? exception, out WorkspaceFencedException? violation)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case WorkspaceFencedException fenced:
                    violation = fenced;
                    return true;
                case PostgresException { SqlState: SqlState } pg:
                    var words = pg.MessageText.Split(' ', 3);
                    violation = new WorkspaceFencedException(
                        words.Length > 1 && Guid.TryParse(words[1], out var ws) ? ws : Guid.Empty, string.IsNullOrEmpty(pg.Hint) ? "data" : pg.Hint, pg);
                    return true;
            }
        }

        violation = null;
        return false;
    }
}

public static class WorkspaceDeletionStoreRegistration
{
    /// <summary>Registers the PostgreSQL deletion store (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresWorkspaceDeletions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IWorkspaceDeletionStore, WorkspaceDeletionStore>();
        return services;
    }
}
