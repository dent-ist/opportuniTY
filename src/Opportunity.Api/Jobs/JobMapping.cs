using Opportunity.Application.Jobs;
using Opportunity.Contracts.Api;
using Opportunity.Core.Jobs;

namespace Opportunity.Api.Jobs;

/// <summary>Maps the job store's views onto the job monitor's resources (E06-T06).</summary>
internal static class JobMapping
{
    public static JobSummary ToSummary(JobOverview overview, long indexedThroughGeneration)
    {
        ArgumentNullException.ThrowIfNull(overview);
        var job = overview.Job;
        return new JobSummary(
            job.JobId,
            Type(job.JobType),
            Name(overview),
            Status(job.Status),
            new JobCreator(job.InitiatedBy, overview.InitiatorDisplayName),
            job.CreatedAt,
            job.UpdatedAt,
            JobStateMachine.IsFinished(job.Status) ? job.FinishedAt : null,
            Committed(job),
            Searchable(job, indexedThroughGeneration),
            ErrorCount(overview),
            job.CorrelationId,
            job.TargetSnapshotId,
            Link(job));
    }

    public static JobDetail ToDetail(JobOperationsDetail detail, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var summary = ToSummary(detail.Overview, detail.IndexedThroughGeneration);
        var job = detail.Overview.Job;
        long Count(params JobChunkStatus[] statuses) => statuses.Sum(s => detail.ChunkCounts.GetValueOrDefault(s));
        var legacy = JobEndpoints.ToResource(job);
        return new JobDetail(
            summary.JobId,
            summary.Type,
            summary.Name,
            summary.Status,
            summary.CreatedBy,
            summary.CreatedAt,
            summary.UpdatedAt,
            summary.CompletedAt,
            summary.Committed,
            summary.Searchable,
            summary.ErrorCount,
            summary.CorrelationId,
            summary.SnapshotId,
            summary.Link,
            new JobChunkCounts(
                Pending: Count(JobChunkStatus.Pending, JobChunkStatus.Dispatched, JobChunkStatus.RetryWait),
                Running: Count(JobChunkStatus.Running),
                Done: Count(JobChunkStatus.Committed),
                Failed: Count(JobChunkStatus.Failed),
                DeadLettered: detail.ExhaustedChunks,
                Cancelled: Count(JobChunkStatus.Cancelled)),
            detail.Attempts,
            detail.LastError,
            EtaSeconds(job, now),
            legacy.WorkspaceId,
            legacy.JobType,
            legacy.StatusReason,
            legacy.InitiatedBy,
            legacy.TargetSnapshotId,
            legacy.StartedAt,
            legacy.FinishedAt,
            legacy.Indexed);
    }

    public static JobEvent ToEvent(JobOverview overview, long indexedThroughGeneration)
    {
        ArgumentNullException.ThrowIfNull(overview);
        var job = overview.Job;
        return new JobEvent(job.JobId, Status(job.Status), Committed(job), Searchable(job, indexedThroughGeneration), job.UpdatedAt);
    }

    public static JobFailure ToFailure(JobFailureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var kind = record.Source switch
        {
            JobFailureSource.Chunk => JobFailureKind.Chunk,
            JobFailureSource.IndexTask => JobFailureKind.IndexTask,
            JobFailureSource.DeadLetter => JobFailureKind.DeadLetter,
            _ => JobFailureKind.Outbox,
        };
        return new JobFailure(kind, record.Id, record.Attempts, record.Error, record.FailedAt);
    }

    public static JobResourceType Type(JobType type) => Enum.Parse<JobResourceType>(type.ToString());

    public static JobResourceStatus Status(JobStatus status) => Enum.Parse<JobResourceStatus>(status.ToString());

    public static JobCommittedProgress Committed(JobInfo job)
    {
        var c = job.Counters;
        return new JobCommittedProgress(
            c.ChunksTotal, c.ChunksCommitted, c.ChunksFailed, c.ChunksCancelled, c.ChunksPending,
            c.ItemsApplied, c.ItemsUnchanged, c.ItemsSkippedConcurrentEdit, c.ItemsExcludedNoAccess, c.ItemsFailed);
    }

    public static JobSearchableProgress Searchable(JobInfo job, long indexedThroughGeneration)
    {
        var (done, total, state) = JobSearchability.Evaluate(job, indexedThroughGeneration);
        return new JobSearchableProgress(
            done,
            total,
            state switch
            {
                SearchabilityState.NotApplicable => JobSearchableState.NotApplicable,
                SearchabilityState.Pending => JobSearchableState.Pending,
                SearchabilityState.CatchingUp => JobSearchableState.CatchingUp,
                _ => JobSearchableState.Current,
            },
            job.JobGeneration,
            indexedThroughGeneration);
    }

    private static long ErrorCount(JobOverview overview) =>
        overview.Job.Counters.ChunksFailed + overview.Job.Counters.ItemsFailed + overview.FailedIndexTasks;

    /// <summary>The import or export name, else the workflow name of the job type (UI wording, Q-69 "Mass Edit").</summary>
    private static string Name(JobOverview overview) => overview.Name ?? overview.Job.JobType switch
    {
        JobType.Import => "Import",
        JobType.BulkCoding => "Mass Edit",
        JobType.RelationshipFixup => "Relationship update",
        JobType.Reindex => "Search reindex",
        JobType.Export => "Export",
        JobType.Production => "Production",
        _ => "Rendering",
    };

    private static string Link(JobInfo job) => job.JobType == JobType.Import && job.ImportBatchId is { } batch
        ? $"imports/{batch}"
        : $"jobs/{job.JobId}";

    /// <summary>Remaining chunks at the rate committed so far, while the job runs.</summary>
    private static long? EtaSeconds(JobInfo job, DateTimeOffset now)
    {
        var c = job.Counters;
        if (job.Status != JobStatus.Running || job.StartedAt is not { } started || c.ChunksCommitted == 0 || c.ChunksPending == 0)
        {
            return null;
        }

        var elapsed = Math.Max(0, (now - started).TotalSeconds);
        return (long)Math.Ceiling(elapsed * c.ChunksPending / c.ChunksCommitted);
    }
}
