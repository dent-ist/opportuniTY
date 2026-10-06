using System.Globalization;
using System.Text.Json.Nodes;

using Opportunity.Application.Jobs;
using Opportunity.Application.Search.Indexing;
using Opportunity.Core.Jobs;

namespace Opportunity.Application.Search.Reindex;

public enum ReindexStartStatus
{
    Ok,
    Invalid,

    /// <summary>Another reindex of the workspace is in flight (<see cref="ReindexStartOutcome.Run"/> is that one).</summary>
    Conflict,
    IdempotencyKeyReuse,
}

public sealed record ReindexStartOutcome
{
    public required ReindexStartStatus Status { get; init; }

    public JobInfo? Job { get; init; }

    public ReindexRun? Run { get; init; }

    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();

    public static ReindexStartOutcome Invalid(string property, string message) =>
        new() { Status = ReindexStartStatus.Invalid, Errors = new Dictionary<string, string[]> { [property] = [message] } };
}

/// <summary>Who starts a reindex: a user (API) or an operator on the operations CLI.</summary>
public sealed record ReindexInitiator(Guid UserId, string? OperatorName, string? CorrelationId)
{
    public static ReindexInitiator User(Guid userId, string? correlationId) => new(userId, null, correlationId);

    public static ReindexInitiator Cli(string operatorName) =>
        new(OperationsActor.CliPrincipalId, OperationsActor.Cli(operatorName).OperatorName, $"ops-cli:{Guid.NewGuid():N}");
}

/// <summary>The current placement and the recent reindex runs of a workspace.</summary>
public sealed record ReindexStatus(WorkspaceSearchPlacementInfo? Placement, IReadOnlyList<ReindexRun> Runs);

/// <summary>
/// Alias-based reindex (E07-T11, ADR-006 R13): submits a <see cref="JobType.Reindex"/> job and its run, which the reindex
/// coordinator of the indexing worker then drives (target creation and dual-target writes, key-range backfill through
/// <c>Reindex</c> IndexChunkTasks, validation, alias switch, retention of the old location). Cancelling the job before the
/// switch aborts it; the current placement keeps serving throughout.
/// </summary>
public sealed class ReindexService(IReindexStore store, IJobRepository jobs, IWorkspaceSearchPlacement? placements = null)
{
    public const int MaxListedRuns = 20;

    /// <summary>Starts a reindex. A retry with the same <paramref name="idempotencyKey"/> returns the same job.</summary>
    public async Task<ReindexStartOutcome> StartAsync(
        Guid workspaceId, ReindexRequest request, ReindexInitiator initiator, string? idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(initiator);
        if (request.Kind is { } kind && !Enum.IsDefined(kind))
        {
            return ReindexStartOutcome.Invalid("placement", "placement must be shared or dedicated.");
        }

        if (request.Generation is < 1)
        {
            return ReindexStartOutcome.Invalid("generation", "generation must be a projection generation (1 or higher).");
        }

        if (request.PrimaryShards is { } shards && (shards < 1 || shards > ReindexRequest.MaxPrimaryShards))
        {
            return ReindexStartOutcome.Invalid("primaryShards",
                string.Create(CultureInfo.InvariantCulture, $"primaryShards must be between 1 and {ReindexRequest.MaxPrimaryShards}."));
        }

        if (placements is not null)
        {
            var current = await placements.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return ReindexStartOutcome.Invalid("workspace", "Nothing is indexed for this workspace yet, so there is nothing to reindex.");
            }

            var target = request.Kind ?? current.Kind;
            if (current.Kind == IndexPlacementKind.Dedicated && target == IndexPlacementKind.Shared)
            {
                return ReindexStartOutcome.Invalid("placement", "A dedicated index is never moved back to a shared index (ADR-006 R4).");
            }

            if (request.PrimaryShards is not null && target != IndexPlacementKind.Dedicated)
            {
                return ReindexStartOutcome.Invalid("primaryShards", "primaryShards applies to a dedicated index only.");
            }
        }

        var parameters = request.ToJson();
        var existing = await InFlightAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var creation = await jobs.CreateAsync(new NewJob
        {
            WorkspaceId = workspaceId,
            JobType = JobType.Reindex,
            InitiatedBy = initiator.UserId,
            OperatorName = initiator.OperatorName,
            Parameters = parameters,
            ClientIdempotencyKey = idempotencyKey,
            CorrelationId = initiator.CorrelationId,
        }, cancellationToken).ConfigureAwait(false);
        var job = creation.Job;
        if (!creation.Created && (job.JobType != JobType.Reindex || !JsonNode.DeepEquals(job.Parameters, parameters)))
        {
            return new ReindexStartOutcome { Status = ReindexStartStatus.IdempotencyKeyReuse };
        }

        if (existing is not null && existing.JobId != job.JobId)
        {
            await CancelUnstartedAsync(job, initiator, cancellationToken).ConfigureAwait(false);
            return new ReindexStartOutcome { Status = ReindexStartStatus.Conflict, Run = existing };
        }

        var (outcome, run) = await store.CreateAsync(workspaceId, job.JobId, request, cancellationToken).ConfigureAwait(false);
        if (outcome == ReindexCreateOutcome.Conflict)
        {
            await CancelUnstartedAsync(job, initiator, cancellationToken).ConfigureAwait(false);
            return new ReindexStartOutcome { Status = ReindexStartStatus.Conflict, Run = run };
        }

        if (job.Status == JobStatus.Created)
        {
            await jobs.BeginPreparingAsync(workspaceId, job.JobId, cancellationToken).ConfigureAwait(false);
        }

        job = await jobs.GetAsync(workspaceId, job.JobId, cancellationToken).ConfigureAwait(false) ?? job;
        return new ReindexStartOutcome { Status = ReindexStartStatus.Ok, Job = job, Run = run };
    }

    public async Task<ReindexStatus> GetStatusAsync(Guid workspaceId, int limit = MaxListedRuns, CancellationToken cancellationToken = default)
    {
        var placement = placements is null ? null : await placements.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var runs = await store.ListAsync(workspaceId, Math.Clamp(limit, 1, MaxListedRuns), cancellationToken).ConfigureAwait(false);
        return new ReindexStatus(placement, runs);
    }

    public Task<ReindexRun?> GetRunAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default) =>
        store.GetAsync(workspaceId, jobId, cancellationToken);

    private async Task<ReindexRun?> InFlightAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        (await store.ListAsync(workspaceId, MaxListedRuns, cancellationToken).ConfigureAwait(false))
        .FirstOrDefault(r => ReindexPhases.IsInFlight(r.Phase));

    /// <summary>A job created for a submission that lost to an in-flight reindex never runs.</summary>
    private async Task CancelUnstartedAsync(JobInfo job, ReindexInitiator initiator, CancellationToken cancellationToken)
    {
        if (job.Status == JobStatus.Created && await store.GetAsync(job.WorkspaceId, job.JobId, cancellationToken).ConfigureAwait(false) is null)
        {
            await jobs.CancelAsync(job.WorkspaceId, job.JobId, initiator.UserId, "Another reindex of this workspace is in progress.", cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
