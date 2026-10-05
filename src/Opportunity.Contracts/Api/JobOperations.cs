namespace Opportunity.Contracts.Api;

/// <summary>
/// One job in the job monitor (E06-T06): <c>GET /api/v1/workspaces/{workspaceId}/jobs</c>. Progress has two phases
/// (ADR-010 §10): <see cref="Committed"/> is authoritative (PostgreSQL), <see cref="Searchable"/> tells when search
/// reflects it.
/// </summary>
/// <param name="Type">The job type (same values as <see cref="JobResource.JobType"/>).</param>
/// <param name="Name">The import or export name, else a name derived from the type.</param>
/// <param name="CompletedAt">When the job reached a finished status (completed, with errors, failed or cancelled).</param>
/// <param name="ErrorCount">Failed chunks + failed items + failed index tasks.</param>
/// <param name="SnapshotId">The frozen set the job works on, when it has one.</param>
/// <param name="Link">Relative app route of the job's own page inside the workspace, e.g. <c>imports/{importId}</c>.</param>
public sealed record JobSummary(
    Guid JobId,
    JobResourceType Type,
    string Name,
    JobResourceStatus Status,
    JobCreator CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    JobCommittedProgress Committed,
    JobSearchableProgress Searchable,
    long ErrorCount,
    string? CorrelationId,
    Guid? SnapshotId,
    string Link);

/// <param name="DisplayName">Null when the user has no display name on record.</param>
public sealed record JobCreator(Guid UserId, string? DisplayName);

/// <summary>
/// Search progress: index tasks created by the job's committed chunks and how many are applied.
/// <see cref="JobSearchableState.Current"/> means the job is finished and the workspace's refresh-aware search watermark
/// has reached the job's generation: <c>indexedThroughGeneration &gt;= jobGeneration</c> (ADR-001 §7.3, ADR-010 §10).
/// Raw generations are for admins and support (Q-10); reviewers see the state.
/// </summary>
/// <param name="JobGeneration">
/// The SearchGeneration of the job's last committed index task; null while the job has committed none (or creates no
/// search work).
/// </param>
/// <param name="IndexedThroughGeneration">
/// The workspace's refresh-aware watermark when this was read: every change up to it is searchable.
/// </param>
public sealed record JobSearchableProgress(
    long Done, long Total, JobSearchableState State, long? JobGeneration, long IndexedThroughGeneration);

public enum JobSearchableState
{
    /// <summary>The job changes nothing that is searched (export, production, render).</summary>
    NotApplicable,

    /// <summary>None of the job's changes is searchable yet.</summary>
    Pending,

    /// <summary>Some of the job's changes are searchable; the rest is being indexed (or waits for a replay).</summary>
    CatchingUp,

    /// <summary>The job is finished and search reflects every change it committed (watermark at or past its generation).</summary>
    Current,
}

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}/jobs/{jobId}</c>: the <see cref="JobSummary"/> fields, chunk and attempt
/// detail, and the fields of the first-slice <see cref="JobResource"/> (kept for compatibility).
/// </summary>
/// <param name="Chunks">Chunks by state; <c>failed + deadLettered</c> are the chunks in Failed.</param>
/// <param name="Attempts">Attempts made by the job's chunks (a replay resets a chunk's count).</param>
/// <param name="LastError">The most recent error recorded by a chunk or index task of the job.</param>
/// <param name="EtaSeconds">Estimated seconds until every chunk is committed, while the job runs and has progress.</param>
public sealed record JobDetail(
    Guid JobId,
    JobResourceType Type,
    string Name,
    JobResourceStatus Status,
    JobCreator CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    JobCommittedProgress Committed,
    JobSearchableProgress Searchable,
    long ErrorCount,
    string? CorrelationId,
    Guid? SnapshotId,
    string Link,
    JobChunkCounts Chunks,
    long Attempts,
    string? LastError,
    long? EtaSeconds,
    Guid WorkspaceId,
    JobResourceType JobType,
    string? StatusReason,
    Guid InitiatedBy,
    Guid? TargetSnapshotId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    JobIndexedProgress Indexed);

/// <param name="Pending">Waiting to run: pending, dispatched or waiting to retry.</param>
/// <param name="Running">Held by a worker (leased).</param>
/// <param name="Done">Committed.</param>
/// <param name="Failed">Failed with an error a replay may fix.</param>
/// <param name="DeadLettered">Failed because their attempts ran out (crash loops, poison messages; ADR-010 §7.2).</param>
/// <param name="Cancelled">Cancelled before they ran.</param>
public sealed record JobChunkCounts(long Pending, long Running, long Done, long Failed, long DeadLettered, long Cancelled);

/// <summary>
/// A failed work record (<c>GET …/jobs/{jobId}/failures</c>, <c>GET …/search-outbox/failures</c>). PostgreSQL is the
/// failure ledger (ADR-010 §7); broker dead-letter queues hold diagnostic copies only.
/// </summary>
/// <param name="Id">Chunk or task id (UUID), or the outbox id (decimal).</param>
/// <param name="Attempts">Attempts since the record was created or last replayed.</param>
public sealed record JobFailure(JobFailureKind Kind, string Id, long Attempts, string? Error, DateTimeOffset FailedAt);

public enum JobFailureKind
{
    /// <summary>A job chunk (ADR-010 §1).</summary>
    Chunk,

    /// <summary>An IndexChunkTask that brings a committed chunk into search.</summary>
    IndexTask,

    /// <summary>A SearchOutbox row of an interactive edit (not tied to a job).</summary>
    Outbox,
}

/// <summary>
/// <c>202 Accepted</c> of <c>POST …/jobs/{jobId}/retry-failed</c>: what this call returned to Pending for the dispatcher
/// to publish again (0 and 0 when there was nothing left to replay, e.g. a repeated call).
/// </summary>
public sealed record JobReplayResource(int ChunksReplayed, int IndexTasksReplayed, JobDetail Job);

/// <summary><c>202 Accepted</c> of <c>POST …/search-outbox/retry-failed</c>.</summary>
public sealed record SearchOutboxReplayResource(int RowsReplayed);

/// <summary>
/// One server-sent event of <c>GET …/job-events</c> (event type <c>job</c>, event id = <see cref="UpdatedAt"/>): the
/// job changed. Events carry state, not deltas, so a repeated or late event is harmless.
/// </summary>
public sealed record JobEvent(
    Guid JobId,
    JobResourceStatus Status,
    JobCommittedProgress Committed,
    JobSearchableProgress Searchable,
    DateTimeOffset UpdatedAt);
