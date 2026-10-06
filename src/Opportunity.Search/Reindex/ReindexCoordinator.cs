using Microsoft.Extensions.Logging;

using Opportunity.Application.Jobs;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Search.Reindex;
using Opportunity.Core.Jobs;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;

namespace Opportunity.Search.Reindex;

/// <summary>
/// Drives reindex jobs (E07-T11) through ADR-006 R13 with the watermark rules of ADR-001 §7.5. Every step is idempotent
/// and its outcome is saved in <c>search_reindex</c> under a coordinator lease, so a crashed or restarted worker (or
/// another replica, once the lease expired) resumes where the run stood:
/// <list type="number">
/// <item><b>Pending</b>: <see cref="IIndexManager.BeginRebuildAsync"/> creates the target (no refresh, no replicas)
/// and every writer dual-targets from now on.</item>
/// <item><b>Building</b>: waits <see cref="ReindexOptions.WriterSettleDelay"/>, so no write can still come from a
/// placement cached before the dual-target start or from a read older than that; then plans DocumentId key ranges.</item>
/// <item><b>Backfilling</b>: commits the job's chunks one by one, each with one <c>Reindex</c> IndexChunkTask (no
/// SearchGeneration, so the watermark is untouched) that the chunk index worker writes to the target only, keeping at
/// most <see cref="ReindexOptions.TaskWindow"/> tasks un-applied. Failed tasks wait for an operator replay.</item>
/// <item><b>Validating</b>: <see cref="ReindexValidator"/>; a failure aborts and fails the job, the old alias keeps
/// serving.</item>
/// <item><b>Switching</b>: <see cref="IIndexManager.CompleteRebuildAsync"/> (refresh, one <c>_aliases</c> request) and
/// the job completes. The generation sequence and the visible watermark are per workspace and continue unchanged.</item>
/// <item><b>Switched → Retaining → Completed</b>: after the settle delay the old dedicated index is write-blocked, after
/// <see cref="ReindexOptions.Retention"/> it is deleted (a shared index loses the workspace's documents).</item>
/// </list>
/// Cancelling the job before the switch aborts: the placement goes back to its current location and the target is
/// dropped, once more after the settle delay in case a writer with a cached placement recreated it.
/// </summary>
internal sealed partial class ReindexCoordinator(
    IReindexStore store,
    IJobRepository jobs,
    IJobChunkRepository chunks,
    IIndexManager indexes,
    IProjectionService projections,
    ReindexValidator validator,
    ReindexOptions options,
    TimeProvider time,
    ILogger<ReindexCoordinator> logger)
{
    /// <summary>Identifies this coordinator in run leases and chunk claims.</summary>
    public string Owner { get; init; } = $"reindex:{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>One pass over every run still to drive; returns how many runs changed phase.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var advanced = 0;
        foreach (var (workspaceId, jobId) in await store.ListActiveAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await StepAsync(workspaceId, jobId, cancellationToken).ConfigureAwait(false))
                {
                    advanced++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The step is retried on the next pass; a persistent failure shows as a run that does not progress.
                LogStepFailed(logger, workspaceId, jobId, ex);
            }
        }

        return advanced;
    }

    /// <summary>Advances one run as far as it can go now; true when its phase changed.</summary>
    public async Task<bool> StepAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken)
    {
        var run = await store.TryLeaseAsync(workspaceId, jobId, Owner, options.LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return false;
        }

        var start = run.Phase;
        using var stopHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatAsync(workspaceId, jobId, stopHeartbeat.Token);
        try
        {
            for (var i = 0; i < 12; i++)
            {
                var next = await AdvanceAsync(run, cancellationToken).ConfigureAwait(false);
                if (next is null)
                {
                    break;
                }

                var saved = await store.SaveAsync(next, Owner, cancellationToken).ConfigureAwait(false);
                if (saved is null)
                {
                    // The lease went to another coordinator, which redoes the step (every step is idempotent).
                    return false;
                }

                run = saved;
                LogPhase(logger, workspaceId, jobId, run.Phase);
                if (ReindexPhases.IsFinished(run.Phase))
                {
                    break;
                }
            }
        }
        finally
        {
            await stopHeartbeat.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }

        return run.Phase != start;
    }

    /// <summary>Keeps the run lease while a long step (backfill window, validation) runs.</summary>
    private async Task HeartbeatAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.LeaseDuration / 3, time, cancellationToken).ConfigureAwait(false);
                await store.TryLeaseAsync(workspaceId, jobId, Owner, options.LeaseDuration, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A failed renewal only risks a duplicate (idempotent) step by another coordinator.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, workspaceId, jobId, ex);
        }
    }

    /// <returns>The run in its next phase, or null when it has to wait.</returns>
    private async Task<ReindexRun?> AdvanceAsync(ReindexRun run, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(run.WorkspaceId, run.JobId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (job is null)
        {
            return await AbortAsync(run, "The reindex job no longer exists.", failJob: false, cancellationToken).ConfigureAwait(false);
        }

        if (ReindexPhases.IsInFlight(run.Phase) && job.Status is JobStatus.Cancelling or JobStatus.Cancelled or JobStatus.Failed)
        {
            return await AbortAsync(run, job.Status == JobStatus.Failed ? job.StatusReason ?? "The job failed." : "The job was cancelled.",
                failJob: false, cancellationToken).ConfigureAwait(false);
        }

        switch (run.Phase)
        {
            case ReindexPhase.Pending:
                return await BeginAsync(run, job, cancellationToken).ConfigureAwait(false);

            case ReindexPhase.Building:
                if (await LostRebuildAsync(run, cancellationToken).ConfigureAwait(false) is { } lostWhileBuilding)
                {
                    return lostWhileBuilding;
                }

                return now < run.NextStepAt ? null : await PlanAsync(run, job, cancellationToken).ConfigureAwait(false);

            case ReindexPhase.Backfilling:
                if (await LostRebuildAsync(run, cancellationToken).ConfigureAwait(false) is { } lostWhileBackfilling)
                {
                    return lostWhileBackfilling;
                }

                return await BackfillAsync(run, job, cancellationToken).ConfigureAwait(false);

            case ReindexPhase.Validating:
                if (await LostRebuildAsync(run, cancellationToken).ConfigureAwait(false) is { } lostWhileValidating)
                {
                    return lostWhileValidating;
                }

                return await ValidateAsync(run, cancellationToken).ConfigureAwait(false);

            case ReindexPhase.Switching:
                return await SwitchAsync(run, cancellationToken).ConfigureAwait(false);

            case ReindexPhase.Switched:
                if (now < run.NextStepAt)
                {
                    return null;
                }

                await RetireAsync(run, run.Source, (l, ct) => indexes.BlockWritesAsync(run.WorkspaceId, l, ct), cancellationToken).ConfigureAwait(false);
                return run with { Phase = ReindexPhase.Retaining, NextStepAt = run.RetainUntil };

            case ReindexPhase.Retaining:
                if (now < run.NextStepAt)
                {
                    return null;
                }

                await RetireAsync(run, run.Source, (l, ct) => indexes.DropAsync(run.WorkspaceId, l, ct), cancellationToken).ConfigureAwait(false);
                return run with { Phase = ReindexPhase.Completed, NextStepAt = null };

            case ReindexPhase.Aborting:
                if (now < run.NextStepAt)
                {
                    return null;
                }

                await RetireAsync(run, run.Target, (l, ct) => indexes.DropAsync(run.WorkspaceId, l, ct), cancellationToken).ConfigureAwait(false);
                return run with { Phase = ReindexPhase.Aborted, NextStepAt = null };

            default:
                return null;
        }
    }

    /// <summary>R13 steps 1–2. Adopts a rebuild this run already began (crash between the begin and the save).</summary>
    private async Task<ReindexRun?> BeginAsync(ReindexRun run, JobInfo job, CancellationToken cancellationToken)
    {
        if (job.Status == JobStatus.Created)
        {
            await jobs.BeginPreparingAsync(run.WorkspaceId, run.JobId, cancellationToken).ConfigureAwait(false);
        }

        Placement placement;
        try
        {
            placement = await indexes.ResolveAsync(run.WorkspaceId, IndexPurpose.Read, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceNotPlacedException)
        {
            return await AbortAsync(run, "Nothing is indexed for this workspace yet.", failJob: true, cancellationToken).ConfigureAwait(false);
        }

        var generation = run.Request.Generation ?? projections.Generation;
        if (generation != projections.Generation)
        {
            return await AbortAsync(run,
                $"This build projects generation {projections.Generation}; it cannot build generation {generation}.", failJob: true, cancellationToken)
                .ConfigureAwait(false);
        }

        if (placement.PendingLocation is { } pending)
        {
            if (run.Target is not null && run.Target != pending)
            {
                return await AbortAsync(run, "Another rebuild of this workspace is running.", failJob: true, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            try
            {
                placement = await indexes.BeginRebuildAsync(run.WorkspaceId,
                    new IndexRebuildRequest(run.Request.Kind, generation, run.Request.PrimaryShards), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return await AbortAsync(run, ex.Message, failJob: true, cancellationToken).ConfigureAwait(false);
            }
        }

        return run with
        {
            Phase = ReindexPhase.Building,
            Source = placement.Location,
            Target = placement.PendingLocation,
            NextStepAt = time.GetUtcNow() + options.WriterSettleDelay,
        };
    }

    /// <summary>R13 step 3 planning: key ranges over every possible DocumentId; the job starts Running.</summary>
    private async Task<ReindexRun?> PlanAsync(ReindexRun run, JobInfo job, CancellationToken cancellationToken)
    {
        var documents = run.DocumentsPlanned;
        if (job.Status == JobStatus.Preparing)
        {
            var (plan, count) = await store.PlanKeyRangesAsync(
                run.WorkspaceId, run.Target!.Generation, options.DocumentsPerTask, cancellationToken).ConfigureAwait(false);
            await jobs.StartAsync(new JobStartRequest(run.WorkspaceId, run.JobId, ChunkOperationKind.ReindexChunk, plan, run.Target.Generation),
                cancellationToken).ConfigureAwait(false);
            documents = count;
        }

        return run with { Phase = ReindexPhase.Backfilling, DocumentsPlanned = documents, NextStepAt = null };
    }

    /// <summary>R13 step 3: chunk commits create the reindex tasks, bounded by the task window.</summary>
    private async Task<ReindexRun?> BackfillAsync(ReindexRun run, JobInfo job, CancellationToken cancellationToken)
    {
        if (job.Status != JobStatus.Running)
        {
            return null;
        }

        var progress = await store.GetTaskProgressAsync(run.WorkspaceId, run.JobId, cancellationToken).ConfigureAwait(false);
        var unapplied = progress.Unapplied;
        while (unapplied < options.TaskWindow)
        {
            var claim = await chunks.ClaimNextAsync(run.WorkspaceId, run.JobId, Owner, options.LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (!claim.Claimed)
            {
                break;
            }

            var commit = await store.CommitChunkAsync(claim.Chunk!, cancellationToken).ConfigureAwait(false);
            if (!commit.Committed)
            {
                break;
            }

            unapplied++;
        }

        job = await jobs.GetAsync(run.WorkspaceId, run.JobId, cancellationToken).ConfigureAwait(false) ?? job;
        progress = await store.GetTaskProgressAsync(run.WorkspaceId, run.JobId, cancellationToken).ConfigureAwait(false);
        var c = job.Counters;
        var done = c.ChunksCommitted == c.ChunksTotal && progress.Total == c.ChunksTotal && progress.Applied == progress.Total;
        return done ? run with { Phase = ReindexPhase.Validating } : null;
    }

    /// <summary>R13 steps 4–5: validation decides between the switch and an abort.</summary>
    private async Task<ReindexRun?> ValidateAsync(ReindexRun run, CancellationToken cancellationToken)
    {
        var placement = await indexes.ResolveAsync(run.WorkspaceId, IndexPurpose.Read, cancellationToken).ConfigureAwait(false);
        var validation = await validator.ValidateAsync(placement, cancellationToken).ConfigureAwait(false);
        if (!validation.Passed)
        {
            var aborted = await AbortAsync(run, "Validation failed: " + validation.Failure, failJob: true, cancellationToken).ConfigureAwait(false);
            return aborted with { Validation = validation };
        }

        return run with { Phase = ReindexPhase.Switching, Validation = validation };
    }

    /// <summary>R13 steps 4 and 6: the alias switch (retry-safe), then the job completes.</summary>
    private async Task<ReindexRun?> SwitchAsync(ReindexRun run, CancellationToken cancellationToken)
    {
        var placement = await indexes.ResolveAsync(run.WorkspaceId, IndexPurpose.Read, cancellationToken).ConfigureAwait(false);
        if (placement.PendingLocation == run.Target)
        {
            placement = await indexes.CompleteRebuildAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        }

        if (placement.Location != run.Target || placement.PendingLocation is not null)
        {
            return await AbortAsync(run, "The rebuild ended outside the reindex job before the switch.", failJob: true, cancellationToken)
                .ConfigureAwait(false);
        }

        var now = time.GetUtcNow();
        await jobs.CompleteAsync(run.WorkspaceId, run.JobId, "Switched to the new index", cancellationToken).ConfigureAwait(false);
        LogSwitched(logger, run.WorkspaceId, run.JobId, run.Target!.Kind, run.Target.Generation, run.Target.Revision);
        return run with
        {
            Phase = ReindexPhase.Switched,
            SwitchedAt = run.SwitchedAt ?? now,
            NextStepAt = now + options.WriterSettleDelay,
            RetainUntil = now + options.Retention,
        };
    }

    /// <summary>The placement no longer rebuilds towards this run's target (aborted or switched by someone else).</summary>
    private async Task<ReindexRun?> LostRebuildAsync(ReindexRun run, CancellationToken cancellationToken)
    {
        var placement = await indexes.ResolveAsync(run.WorkspaceId, IndexPurpose.Read, cancellationToken).ConfigureAwait(false);
        return placement.PendingLocation == run.Target
            ? null
            : await AbortAsync(run, "The rebuild ended outside the reindex job.", failJob: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Abandons the rebuild: the placement keeps (or goes back to) its current location and the target is dropped now and
    /// once more after the settle delay. The job fails with <paramref name="reason"/> unless it already ended.
    /// </summary>
    private async Task<ReindexRun> AbortAsync(ReindexRun run, string reason, bool failJob, CancellationToken cancellationToken)
    {
        if (run.Target is not null)
        {
            Placement? placement = null;
            try
            {
                placement = await indexes.ResolveAsync(run.WorkspaceId, IndexPurpose.Read, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkspaceNotPlacedException)
            {
            }

            if (placement?.PendingLocation is { } pending && pending == run.Target)
            {
                await indexes.AbortRebuildAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
            }
        }

        if (failJob)
        {
            await jobs.FailAsync(run.WorkspaceId, run.JobId, reason, cancellationToken).ConfigureAwait(false);
        }

        LogAborted(logger, run.WorkspaceId, run.JobId, reason);
        return run.Target is null
            ? run with { Phase = ReindexPhase.Aborted, Error = reason, NextStepAt = null }
            : run with { Phase = ReindexPhase.Aborting, Error = reason, NextStepAt = time.GetUtcNow() + options.WriterSettleDelay };
    }

    /// <summary>Blocks or drops a location that is no longer in use; one in use again (a later rebuild went back) is left alone.</summary>
    private async Task RetireAsync(
        ReindexRun run, IndexLocation? location, Func<IndexLocation, CancellationToken, Task> retire, CancellationToken cancellationToken)
    {
        if (location is null)
        {
            return;
        }

        try
        {
            await retire(location, cancellationToken).ConfigureAwait(false);
        }
        catch (IndexPlacementConflictException ex)
        {
            LogRetireSkipped(logger, run.WorkspaceId, run.JobId, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reindex {JobId} of workspace {WorkspaceId} is now {Phase}")]
    private static partial void LogPhase(ILogger logger, Guid workspaceId, Guid jobId, ReindexPhase phase);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Reindex {JobId} switched workspace {WorkspaceId} to {Kind} generation {Generation} revision {Revision}")]
    private static partial void LogSwitched(ILogger logger, Guid workspaceId, Guid jobId, IndexPlacementKind kind, int generation, int revision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reindex {JobId} of workspace {WorkspaceId} aborted: {Reason}")]
    private static partial void LogAborted(ILogger logger, Guid workspaceId, Guid jobId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reindex {JobId} of workspace {WorkspaceId} left a location in place: {Reason}")]
    private static partial void LogRetireSkipped(ILogger logger, Guid workspaceId, Guid jobId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reindex {JobId} of workspace {WorkspaceId}: step failed; retried on the next pass")]
    private static partial void LogStepFailed(ILogger logger, Guid workspaceId, Guid jobId, Exception exception);
}
