using Opportunity.Core.Jobs;

namespace Opportunity.Application.Jobs;

public enum SearchabilityState
{
    NotApplicable,
    Pending,
    CatchingUp,
    Current,
}

/// <summary>
/// The "searchable" phase of a job (ADR-010 §10): index tasks of committed chunks applied, and the workspace's
/// refresh-aware watermark (ADR-001 §7.3: every change up to it is applied and refreshed, so searchable) at or past the
/// job's generation (the SearchGeneration of its last committed chunk task). A job that is still running may commit
/// more chunks, so it is never current before it finishes.
/// </summary>
public static class JobSearchability
{
    /// <summary>Job types whose chunks create no index tasks.</summary>
    public static bool AppliesTo(JobType type) => type is not (JobType.Export or JobType.Production or JobType.Render or JobType.SearchTermReport);

    public static (long Done, long Total, SearchabilityState State) Evaluate(JobInfo job, long indexedThroughGeneration)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!AppliesTo(job.JobType))
        {
            return (0, 0, SearchabilityState.NotApplicable);
        }

        var total = job.Counters.IndexTasksTotal;
        var done = Math.Min(job.Counters.IndexTasksApplied, total);
        var state = JobStateMachine.IsFinished(job.Status) && done >= total && (job.JobGeneration is not { } g || indexedThroughGeneration >= g)
            ? SearchabilityState.Current
            : done == 0 ? SearchabilityState.Pending : SearchabilityState.CatchingUp;
        return (done, total, state);
    }
}
