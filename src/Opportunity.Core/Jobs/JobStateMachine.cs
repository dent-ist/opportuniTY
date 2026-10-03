using System.Collections.Frozen;

namespace Opportunity.Core.Jobs;

/// <summary>What moves a job (ADR-010 §2).</summary>
public enum JobTrigger
{
    /// <summary>A planner claims the job to materialize its target and plan chunks.</summary>
    BeginPreparing,

    /// <summary>Target ready and every chunk planned.</summary>
    Start,

    /// <summary>Job-level permanent error (materialization/validation in Preparing, e.g. target field deleted later).</summary>
    Fail,

    /// <summary>Operator pause or circuit breaker (ADR-010 §7.5).</summary>
    Pause,

    Resume,

    /// <summary>User cancel or workspace deletion. From Created straight to Cancelled; otherwise via Cancelling.</summary>
    Cancel,

    /// <summary>Cancelling and no chunk is Running any more.</summary>
    FinishCancelling,

    /// <summary>Every chunk Committed and no item errors.</summary>
    Complete,

    /// <summary>Every chunk settled with at least one Failed chunk or item error.</summary>
    CompleteWithErrors,

    /// <summary>An operator replays the Failed chunks of a job that completed with errors.</summary>
    ReplayFailedChunks,
}

/// <summary>What moves a chunk (ADR-010 §2, §3).</summary>
public enum JobChunkTrigger
{
    /// <summary>
    /// Published with a broker confirm: a Pending chunk, or a RetryWait chunk whose backoff elapsed (the re-dispatch of
    /// ADR-010 §2), so the dispatcher does not publish it again on every pass while it waits for a worker.
    /// </summary>
    Dispatch,

    /// <summary>Claim: conditional UPDATE, AttemptCount + 1, new LeaseToken. Doubles as the consumer inbox.</summary>
    Claim,

    /// <summary>Claim of a Running chunk whose lease expired (new LeaseToken fences out the old holder).</summary>
    Reclaim,

    /// <summary>The commit transaction passed fence F3.</summary>
    Commit,

    /// <summary>Transient error with attempts left.</summary>
    RetryLater,

    /// <summary>Permanent error.</summary>
    FailPermanently,

    /// <summary>
    /// <c>AttemptCount ≥ MaxAttempts</c> at claim or at lease reclamation. Includes Pending, which the diagram leaves out:
    /// a chunk whose last attempt crashed is returned to Pending with its attempts used up.
    /// </summary>
    ExhaustAttempts,

    /// <summary>
    /// A fence saw the job Paused (or the workspace not Active, or the worker shutting down): back to Pending, no attempt
    /// charged. Includes Dispatched, so a message claimed while the job is Paused is re-dispatched after resume.
    /// </summary>
    Yield,

    /// <summary>The lease sweeper returns a chunk whose lease expired for re-dispatch (ADR-010 §3.3).</summary>
    ExpireLease,

    /// <summary>The job is Cancelling (or Failed): the chunk will not run.</summary>
    Cancel,

    /// <summary>Operator replay (audited): AttemptCount reset, ReplayCount + 1.</summary>
    Replay,
}

/// <summary>Raised when a transition is not in the matrix.</summary>
public sealed class InvalidJobTransitionException(string message) : InvalidOperationException(message);

/// <summary>
/// The job state machine of ADR-010 §2. The persistence layer derives the <c>WHERE status IN (…)</c> of every
/// conditional UPDATE from <see cref="SourcesOf"/>, so SQL and this matrix cannot drift apart.
/// </summary>
public static class JobStateMachine
{
    private static readonly (JobStatus From, JobTrigger Trigger, JobStatus To)[] Edges =
    [
        (JobStatus.Created, JobTrigger.BeginPreparing, JobStatus.Preparing),
        (JobStatus.Created, JobTrigger.Cancel, JobStatus.Cancelled),
        (JobStatus.Preparing, JobTrigger.Start, JobStatus.Running),
        (JobStatus.Preparing, JobTrigger.Fail, JobStatus.Failed),
        (JobStatus.Preparing, JobTrigger.Cancel, JobStatus.Cancelling),
        (JobStatus.Running, JobTrigger.Pause, JobStatus.Paused),
        (JobStatus.Paused, JobTrigger.Resume, JobStatus.Running),
        (JobStatus.Running, JobTrigger.Cancel, JobStatus.Cancelling),
        (JobStatus.Paused, JobTrigger.Cancel, JobStatus.Cancelling),
        (JobStatus.Running, JobTrigger.Complete, JobStatus.Completed),
        (JobStatus.Running, JobTrigger.CompleteWithErrors, JobStatus.CompletedWithErrors),
        (JobStatus.Running, JobTrigger.Fail, JobStatus.Failed),
        (JobStatus.Cancelling, JobTrigger.FinishCancelling, JobStatus.Cancelled),
        (JobStatus.CompletedWithErrors, JobTrigger.ReplayFailedChunks, JobStatus.Running),
    ];

    private static readonly FrozenDictionary<(JobStatus, JobTrigger), JobStatus> Map =
        Edges.ToFrozenDictionary(e => (e.From, e.Trigger), e => e.To);

    private static readonly FrozenDictionary<JobTrigger, JobStatus[]> Sources =
        Enum.GetValues<JobTrigger>().ToFrozenDictionary(t => t, t => Edges.Where(e => e.Trigger == t).Select(e => e.From).ToArray());

    public static IReadOnlyList<(JobStatus From, JobTrigger Trigger, JobStatus To)> Transitions => Edges;

    public static bool TryTransition(JobStatus from, JobTrigger trigger, out JobStatus to) =>
        Map.TryGetValue((from, trigger), out to);

    public static JobStatus Transition(JobStatus from, JobTrigger trigger) =>
        TryTransition(from, trigger, out var to)
            ? to
            : throw new InvalidJobTransitionException($"Job transition {trigger} is not allowed from {from}.");

    public static IReadOnlyList<JobStatus> SourcesOf(JobTrigger trigger) => Sources[trigger];

    /// <summary>No transition leaves these.</summary>
    public static bool IsTerminal(JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed;

    /// <summary>The job's PG phase is over (CompletedWithErrors may still be replayed).</summary>
    public static bool IsFinished(JobStatus status) => IsTerminal(status) || status == JobStatus.CompletedWithErrors;
}

/// <summary>The chunk state machine of ADR-010 §2 (see <see cref="JobStateMachine"/> for how SQL uses it).</summary>
public static class JobChunkStateMachine
{
    private static readonly (JobChunkStatus From, JobChunkTrigger Trigger, JobChunkStatus To)[] Edges =
    [
        (JobChunkStatus.Pending, JobChunkTrigger.Dispatch, JobChunkStatus.Dispatched),
        (JobChunkStatus.RetryWait, JobChunkTrigger.Dispatch, JobChunkStatus.Dispatched),
        (JobChunkStatus.Pending, JobChunkTrigger.Claim, JobChunkStatus.Running),
        (JobChunkStatus.Dispatched, JobChunkTrigger.Claim, JobChunkStatus.Running),
        (JobChunkStatus.RetryWait, JobChunkTrigger.Claim, JobChunkStatus.Running),
        (JobChunkStatus.Running, JobChunkTrigger.Reclaim, JobChunkStatus.Running),
        (JobChunkStatus.Running, JobChunkTrigger.Commit, JobChunkStatus.Committed),
        (JobChunkStatus.Running, JobChunkTrigger.RetryLater, JobChunkStatus.RetryWait),
        (JobChunkStatus.Running, JobChunkTrigger.FailPermanently, JobChunkStatus.Failed),
        (JobChunkStatus.Pending, JobChunkTrigger.ExhaustAttempts, JobChunkStatus.Failed),
        (JobChunkStatus.Dispatched, JobChunkTrigger.ExhaustAttempts, JobChunkStatus.Failed),
        (JobChunkStatus.RetryWait, JobChunkTrigger.ExhaustAttempts, JobChunkStatus.Failed),
        (JobChunkStatus.Running, JobChunkTrigger.ExhaustAttempts, JobChunkStatus.Failed),
        (JobChunkStatus.Running, JobChunkTrigger.Yield, JobChunkStatus.Pending),
        (JobChunkStatus.Dispatched, JobChunkTrigger.Yield, JobChunkStatus.Pending),
        (JobChunkStatus.Running, JobChunkTrigger.ExpireLease, JobChunkStatus.Pending),
        (JobChunkStatus.Pending, JobChunkTrigger.Cancel, JobChunkStatus.Cancelled),
        (JobChunkStatus.Dispatched, JobChunkTrigger.Cancel, JobChunkStatus.Cancelled),
        (JobChunkStatus.RetryWait, JobChunkTrigger.Cancel, JobChunkStatus.Cancelled),
        (JobChunkStatus.Running, JobChunkTrigger.Cancel, JobChunkStatus.Cancelled),
        (JobChunkStatus.Failed, JobChunkTrigger.Replay, JobChunkStatus.Pending),
    ];

    private static readonly FrozenDictionary<(JobChunkStatus, JobChunkTrigger), JobChunkStatus> Map =
        Edges.ToFrozenDictionary(e => (e.From, e.Trigger), e => e.To);

    private static readonly FrozenDictionary<JobChunkTrigger, JobChunkStatus[]> Sources =
        Enum.GetValues<JobChunkTrigger>().ToFrozenDictionary(t => t, t => Edges.Where(e => e.Trigger == t).Select(e => e.From).ToArray());

    public static IReadOnlyList<(JobChunkStatus From, JobChunkTrigger Trigger, JobChunkStatus To)> Transitions => Edges;

    public static bool TryTransition(JobChunkStatus from, JobChunkTrigger trigger, out JobChunkStatus to) =>
        Map.TryGetValue((from, trigger), out to);

    public static JobChunkStatus Transition(JobChunkStatus from, JobChunkTrigger trigger) =>
        TryTransition(from, trigger, out var to)
            ? to
            : throw new InvalidJobTransitionException($"Chunk transition {trigger} is not allowed from {from}.");

    public static IReadOnlyList<JobChunkStatus> SourcesOf(JobChunkTrigger trigger) => Sources[trigger];

    /// <summary>Every chunk trigger leads to one status, whatever its source.</summary>
    public static JobChunkStatus TargetOf(JobChunkTrigger trigger) => Targets[trigger];

    private static readonly FrozenDictionary<JobChunkTrigger, JobChunkStatus> Targets =
        Enum.GetValues<JobChunkTrigger>().ToFrozenDictionary(t => t, t => Edges.Where(e => e.Trigger == t).Select(e => e.To).Distinct().Single());

    /// <summary>No transition leaves these.</summary>
    public static bool IsTerminal(JobChunkStatus status) => status is JobChunkStatus.Committed or JobChunkStatus.Cancelled;

    /// <summary>Counts towards job completion: terminal, or Failed (terminal for completion but replayable).</summary>
    public static bool IsSettled(JobChunkStatus status) => IsTerminal(status) || status == JobChunkStatus.Failed;
}
