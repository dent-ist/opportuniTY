using Opportunity.Core.Jobs;

namespace Opportunity.Application.Jobs;

/// <summary>
/// Job operations (E06-T06, ADR-010 §7): the job monitor's reads (lists, detail, failures) and the PostgreSQL-driven
/// replay. PostgreSQL is the failure ledger; replay resets rows to Pending and the dispatcher publishes them again,
/// RabbitMQ dead-letter queues are never re-published. Every call is one workspace transaction (RLS); callers
/// authorize first.
/// </summary>
public interface IJobOperationsStore
{
    /// <summary>One page of jobs (keyset on <see cref="JobListQuery.After"/>), with the workspace's applied watermark.</summary>
    Task<JobOverviewPage> ListAsync(JobListQuery query, CancellationToken cancellationToken = default);

    /// <summary>The job with its chunk breakdown, or null.</summary>
    Task<JobOperationsDetail?> GetDetailAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Failed chunks and failed index tasks of the job, and its recorded dead-lettered messages whose chunk or task is
    /// still failed (or that name none), oldest failure first (keyset on <paramref name="after"/>).
    /// </summary>
    Task<IReadOnlyList<JobFailureRecord>> ListFailuresAsync(
        Guid workspaceId, Guid jobId, JobFailureRecord? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>Failed SearchOutbox rows of the workspace (interactive edits), oldest failure first.</summary>
    Task<IReadOnlyList<JobFailureRecord>> ListOutboxFailuresAsync(
        Guid workspaceId, JobFailureRecord? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays everything failed of the job in one transaction (ADR-010 §7.4): Failed chunks → Pending (only while the
    /// job is Running, Paused or CompletedWithErrors; the latter returns to Running) and Failed index tasks → Pending
    /// (always: a committed change must reach the index, whatever happened to the job). Attempts reset, replay count
    /// + 1. A replay that moved anything writes <c>Job.Replayed</c> attributed to <paramref name="actor"/>; a repeated
    /// call finds nothing failed and changes nothing.
    /// </summary>
    Task<JobReplayOutcome> ReplayFailedAsync(Guid workspaceId, Guid jobId, OperationsActor actor, CancellationToken cancellationToken = default);

    /// <summary>Failed SearchOutbox rows → Pending with attempts reset; audited like a job replay. Returns the count.</summary>
    Task<int> ReplayFailedOutboxAsync(Guid workspaceId, OperationsActor actor, CancellationToken cancellationToken = default);
}

/// <summary>Filters of the job list. Without <see cref="UpdatedSince"/> the list is newest first; with it (the polling
/// form) it is in <c>UpdatedAt</c> order, oldest change first.</summary>
public sealed record JobListQuery(Guid WorkspaceId)
{
    public const int MaxLimit = 500;

    /// <summary>Only jobs this user initiated (callers without <c>Job.ViewAll</c>).</summary>
    public Guid? InitiatedBy { get; init; }

    public IReadOnlyCollection<JobType> Types { get; init; } = [];

    public IReadOnlyCollection<JobStatus> Statuses { get; init; } = [];

    /// <summary>Created at or after.</summary>
    public DateTimeOffset? CreatedFrom { get; init; }

    /// <summary>Created before.</summary>
    public DateTimeOffset? CreatedTo { get; init; }

    /// <summary>Changed after (exclusive).</summary>
    public DateTimeOffset? UpdatedSince { get; init; }

    /// <summary>Keyset position: the sort time (created or updated) and job id of the previous page's last item.</summary>
    public (DateTimeOffset At, Guid JobId)? After { get; init; }

    public int Limit { get; init; } = 50;
}

/// <summary>A job with what the monitor shows besides its counters.</summary>
public sealed record JobOverview(JobInfo Job, string? InitiatorDisplayName, string? Name, long FailedIndexTasks);

/// <param name="IndexedThroughGeneration">The workspace's refresh-aware search watermark (ADR-001 §7.3), read with the page.</param>
public sealed record JobOverviewPage(IReadOnlyList<JobOverview> Items, long IndexedThroughGeneration);

/// <param name="ChunkCounts">Chunks by status; failed chunks whose attempts ran out are counted separately.</param>
/// <param name="IndexedThroughGeneration">The workspace's refresh-aware search watermark (ADR-001 §7.3), read with the job.</param>
public sealed record JobOperationsDetail(
    JobOverview Overview,
    IReadOnlyDictionary<JobChunkStatus, long> ChunkCounts,
    long ExhaustedChunks,
    long Attempts,
    string? LastError,
    long IndexedThroughGeneration);

public enum JobFailureSource
{
    Chunk,
    IndexTask,
    Outbox,

    /// <summary>A broker message of the job that was dead-lettered or parked (recorded copy, ADR-010 §7.3).</summary>
    DeadLetter,
}

/// <param name="Id">Chunk or task id, the outbox id in invariant decimal, or a dead-lettered message's id.</param>
/// <param name="Attempts">Attempts, or for a dead-lettered message how often the broker dead-lettered it.</param>
public sealed record JobFailureRecord(JobFailureSource Source, string Id, long Attempts, string? Error, DateTimeOffset FailedAt);

public sealed record JobReplayOutcome(JobTransitionOutcome Outcome, int ChunksReplayed, int IndexTasksReplayed, JobStatus? Status);

/// <summary>Who performs an operation: a signed-in user (API) or an operator through the operations CLI.</summary>
public sealed record OperationsActor
{
    public const string CliServiceId = "service:ops-cli";

    /// <summary>The <c>InitiatedBy</c> of jobs started from the operations CLI (no user): a fixed, never-issued id.</summary>
    public static readonly Guid CliPrincipalId = new("00000000-0000-0000-0000-00000000c11a");

    private OperationsActor(Guid? userId, string? operatorName)
    {
        UserId = userId;
        OperatorName = operatorName;
    }

    public Guid? UserId { get; }

    /// <summary>The operator named on the command line (CLI only).</summary>
    public string? OperatorName { get; }

    public static OperationsActor User(Guid userId) => new(userId, null);

    public static OperationsActor Cli(string operatorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);
        return new(null, operatorName.Trim());
    }
}
