using System.Globalization;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Authorization;
using Opportunity.Application.Jobs;
using Opportunity.Contracts.Api;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Data.Jobs;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Jobs;

/// <summary>
/// Job operations (E06-T02, E06-T06): the job list (with the <c>updatedSince</c> polling form), job detail with
/// committed and searchable progress, failures, cancel and PostgreSQL-driven replay (ADR-010 §7.4), plus the failed
/// SearchOutbox rows of interactive edits. Users see and cancel their own jobs; other users' jobs need
/// <c>Job.ViewAll</c> to see (403 otherwise, Q-59) and <c>Job.Manage</c> to cancel; replay needs <c>Job.Replay</c>.
/// Live updates: <see cref="JobEventEndpoints"/>.
/// </summary>
public sealed class JobEndpoints : IApiEndpointModule
{
    private const string Tag = "Jobs";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var jobs = routes.Workspace.MapGroup("/jobs");

        jobs.MapGet(string.Empty, ListAsync)
            .WithName("ListJobs")
            .WithTags(Tag)
            .WithSummary("Jobs, newest first (or, with updatedSince, in change order): your own, or everyone's with Job.ViewAll.")
            .WithDescription(
                "Filters: type and status (repeat or comma-separate), createdBy=me|all (all needs Job.ViewAll, else only your own jobs " +
                "are listed), from/to (creation time, ISO 8601). updatedSince=<ISO 8601> is the polling fallback of the job-events " +
                "stream: jobs changed after that instant, oldest change first. Timestamps have millisecond precision and a change " +
                "becomes visible when its transaction commits, so poll with the newest updatedAt seen minus a few seconds; items " +
                "are state, so a repeated one is harmless.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireWorkspaceMember();

        jobs.MapGet("/{jobId}", GetJobAsync)
            .WithName("GetJob")
            .WithTags(Tag)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Job detail: committed and searchable progress, chunks by state, attempts, last error and ETA.")
            .WithDescription("Users always see their own jobs; other users' jobs need Job.ViewAll.")
            .RequireWorkspaceMember();

        jobs.MapGet("/{jobId}/failures", ListFailuresAsync)
            .WithName("ListJobFailures")
            .WithTags(Tag)
            .WithSummary("Failed chunks and index tasks of the job, and its dead-lettered messages, oldest failure first (PostgreSQL is the failure ledger).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireWorkspaceMember();

        jobs.MapPost("/{jobId}/cancel", CancelAsync)
            .WithName("CancelJob")
            .WithTags(Tag)
            .WithSummary("Cancel a job: pending chunks are cancelled now, running chunks stop at their next fence (your own jobs, or Job.Manage).")
            .WithDescription("Committed chunks stay (no undo, Q-34) and their index tasks still run. Repeating the call is harmless.")
            .Produces<JobDetail>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireWorkspaceMember();

        jobs.MapPost("/{jobId}/retry-failed", RetryFailedAsync)
            .WithName("RetryFailedJobWork")
            .WithTags(Tag)
            .WithSummary("Replay the job's failed chunks and index tasks from PostgreSQL (Job.Replay); idempotent and audited.")
            .WithDescription(
                "Failed rows return to Pending with their attempts reset and the dispatcher publishes them again; dead-lettered " +
                "broker messages are never re-published (ADR-010 §7.3). Chunks replay while the job is running, paused or completed " +
                "with errors; index tasks always (a committed change must reach the index). Fix the cause first.")
            .Produces<JobReplayResource>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.JobReplay);

        var outbox = routes.Workspace.MapGroup("/search-outbox");
        outbox.MapGet("/failures", ListOutboxFailuresAsync)
            .WithName("ListSearchOutboxFailures")
            .WithTags(Tag)
            .WithSummary("Failed SearchOutbox rows (interactive edits not yet searchable), oldest failure first (Job.ViewAll).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.JobViewAll);

        outbox.MapPost("/retry-failed", RetryOutboxAsync)
            .WithName("RetrySearchOutboxFailures")
            .WithTags(Tag)
            .WithSummary("Replay every failed SearchOutbox row of the workspace from PostgreSQL (Job.Replay); idempotent and audited.")
            .Produces<SearchOutboxReplayResource>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.JobReplay);
    }

    internal static async Task<Results<Ok<CursorPage<JobSummary>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        string workspaceId,
        [AsParameters] PageQuery page,
        [FromQuery(Name = "type")] string[]? type,
        [FromQuery(Name = "status")] string[]? status,
        [FromQuery(Name = "createdBy")] string? createdBy,
        [FromQuery(Name = "from")] DateTimeOffset? from,
        [FromQuery(Name = "to")] DateTimeOffset? to,
        [FromQuery(Name = "updatedSince")] DateTimeOffset? updatedSince,
        HttpContext context,
        IJobOperationsStore store,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var errors = new Dictionary<string, string[]>();
        var types = Parse<JobResourceType, JobType>(type, "type", errors);
        var statuses = Parse<JobResourceStatus, JobStatus>(status, "status", errors);
        if (createdBy is not (null or "me" or "all"))
        {
            errors["createdBy"] = ["createdBy is me or all."];
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var polling = updatedSince is not null;
        (DateTimeOffset, Guid)? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 3) is not [var mode, var ticks, var id]
                || mode != (polling ? "u" : "c")
                || !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t) || t > DateTimeOffset.MaxValue.UtcTicks
                || !Guid.TryParse(id, out var jobId))
            {
                return PageCursor.Invalid();
            }

            after = (new DateTimeOffset(t, TimeSpan.Zero), jobId);
        }

        var viewAll = await ViewAllAsync(access, authorization, cancellationToken).ConfigureAwait(false);
        var limit = page.EffectiveLimit;
        var result = await store.ListAsync(new JobListQuery(access.WorkspaceId)
        {
            InitiatedBy = viewAll && createdBy != "me" ? null : access.Principal.UserId,
            Types = types,
            Statuses = statuses,
            CreatedFrom = from,
            CreatedTo = to,
            UpdatedSince = updatedSince,
            After = after,
            Limit = limit + 1,
        }, cancellationToken).ConfigureAwait(false);

        var items = result.Items.Take(limit).Select(o => JobMapping.ToSummary(o, result.AppliedWatermark)).ToList();
        string? next = null;
        if (result.Items.Count > limit)
        {
            var last = result.Items[limit - 1].Job;
            next = PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, polling ? "u" : "c",
                (polling ? last.UpdatedAt : last.CreatedAt).UtcTicks.ToString(CultureInfo.InvariantCulture), last.JobId.ToString("N"));
        }

        return TypedResults.Ok(new CursorPage<JobSummary>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<Results<Ok<JobDetail>, ProblemHttpResult>> GetJobAsync(
        string workspaceId, string jobId, HttpContext context, IJobOperationsStore store, IAuthorizationService authorization,
        TimeProvider time, CancellationToken cancellationToken)
    {
        // PEP-1 has resolved and authorized the route's workspace; use its result, not the raw route value.
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(jobId, out var id)
            || await store.GetDetailAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } detail)
        {
            return Problems.NotFound("No such job.");
        }

        if (await DenyOthersAsync(access, detail.Overview.Job, Permission.JobViewAll, authorization, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        return TypedResults.Ok(JobMapping.ToDetail(detail, time.GetUtcNow()));
    }

    internal static async Task<Results<Ok<CursorPage<JobFailure>>, ValidationProblem, ProblemHttpResult>> ListFailuresAsync(
        string workspaceId, string jobId, [AsParameters] PageQuery page, HttpContext context, IJobRepository jobs, IJobOperationsStore store,
        IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(jobId, out var id)
            || await jobs.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return Problems.NotFound("No such job.");
        }

        if (await DenyOthersAsync(access, job, Permission.JobViewAll, authorization, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        if (DecodeFailureCursor(page.Cursor, access) is not { } after)
        {
            return PageCursor.Invalid();
        }

        var limit = page.EffectiveLimit;
        var records = await store.ListFailuresAsync(access.WorkspaceId, id, after.Record, limit + 1, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(FailurePage(records, limit, access, after.Record is null));
    }

    internal static async Task<Results<Accepted<JobDetail>, ProblemHttpResult>> CancelAsync(
        string workspaceId, string jobId, HttpContext context, IJobRepository jobs, IJobOperationsStore store,
        IAuthorizationService authorization, TimeProvider time, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(jobId, out var id)
            || await jobs.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return Problems.NotFound("No such job.");
        }

        if (await DenyOthersAsync(access, job, Permission.JobManage, authorization, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var result = await jobs.CancelAsync(access.WorkspaceId, id, access.Principal.UserId, cancellationToken: cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case JobTransitionOutcome.NotFound:
                return Problems.NotFound("No such job.");
            case JobTransitionOutcome.NotAllowed when result.Status is not (JobStatus.Cancelling or JobStatus.Cancelled):
                return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, "The job has finished and can no longer be cancelled.");
        }

        var detail = (await store.GetDetailAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false))!;
        return ApiResults.JobAccepted(access.WorkspaceId.ToString(), id.ToString(), JobMapping.ToDetail(detail, time.GetUtcNow()));
    }

    internal static async Task<Results<Accepted<JobReplayResource>, ProblemHttpResult>> RetryFailedAsync(
        string workspaceId, string jobId, HttpContext context, IJobOperationsStore store, TimeProvider time, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(jobId, out var id))
        {
            return Problems.NotFound("No such job.");
        }

        var outcome = await store.ReplayFailedAsync(access.WorkspaceId, id, OperationsActor.User(access.Principal.UserId), cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Outcome == JobTransitionOutcome.NotFound
            || await store.GetDetailAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } detail)
        {
            return Problems.NotFound("No such job.");
        }

        return ApiResults.JobAccepted(access.WorkspaceId.ToString(), id.ToString(),
            new JobReplayResource(outcome.ChunksReplayed, outcome.IndexTasksReplayed, JobMapping.ToDetail(detail, time.GetUtcNow())));
    }

    internal static async Task<Results<Ok<CursorPage<JobFailure>>, ValidationProblem, ProblemHttpResult>> ListOutboxFailuresAsync(
        string workspaceId, [AsParameters] PageQuery page, HttpContext context, IJobOperationsStore store, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        if (DecodeFailureCursor(page.Cursor, access) is not { } after)
        {
            return PageCursor.Invalid();
        }

        var limit = page.EffectiveLimit;
        var records = await store.ListOutboxFailuresAsync(access.WorkspaceId, after.Record, limit + 1, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(FailurePage(records, limit, access, after.Record is null));
    }

    internal static async Task<Results<Accepted<SearchOutboxReplayResource>, ProblemHttpResult>> RetryOutboxAsync(
        string workspaceId, HttpContext context, IJobOperationsStore store, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var rows = await store.ReplayFailedOutboxAsync(access.WorkspaceId, OperationsActor.User(access.Principal.UserId), cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Accepted((string?)null, new SearchOutboxReplayResource(rows));
    }

    /// <summary>The first-slice job resource (body of the 202s that start jobs).</summary>
    internal static JobResource ToResource(JobInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var c = job.Counters;
        // ADR-010 §10: current once the job's PG phase is over and every index task it created has been applied. The
        // job monitor's searchable state (JobMapping) also checks the applied watermark.
        var indexState = JobStateMachine.IsFinished(job.Status) && c.IndexTasksApplied >= c.IndexTasksTotal
            ? JobIndexState.Current
            : JobIndexState.Indexing;
        return new JobResource(
            job.JobId,
            job.WorkspaceId,
            JobMapping.Type(job.JobType),
            JobMapping.Status(job.Status),
            job.StatusReason,
            job.InitiatedBy,
            job.TargetSnapshotId,
            job.CreatedAt,
            job.StartedAt,
            job.FinishedAt,
            JobMapping.Committed(job),
            new JobIndexedProgress(indexState, c.IndexTasksTotal, c.IndexTasksApplied));
    }

    /// <summary>Whether the caller holds <c>Job.ViewAll</c>, without auditing a denial (a list simply narrows).</summary>
    internal static async Task<bool> ViewAllAsync(WorkspaceAccess access, IAuthorizationService authorization, CancellationToken cancellationToken) =>
        (await authorization.GetEffectivePermissionsAsync(access.Principal, access.WorkspaceId, cancellationToken).ConfigureAwait(false))
            .Permissions.Contains(Permission.JobViewAll);

    /// <summary>Another user's job needs <paramref name="permission"/>: the audited 403 (Q-59) when it is not held.</summary>
    private static async Task<ProblemHttpResult?> DenyOthersAsync(
        WorkspaceAccess access, JobInfo job, Permission permission, IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        if (job.InitiatedBy == access.Principal.UserId)
        {
            return null;
        }

        var decision = await authorization.AuthorizeAsync(access.Principal, access.WorkspaceId, permission, cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed ? null : AuthorizationResults.Problem(decision);
    }

    private static List<TCore> Parse<TWire, TCore>(string[]? values, string name, Dictionary<string, string[]> errors)
        where TWire : struct, Enum
        where TCore : struct, Enum
    {
        var parsed = new List<TCore>();
        foreach (var value in (values ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            // Wire names are the camelCase enum names (e.g. bulkCoding, completedWithErrors).
            if (Enum.GetNames<TWire>().FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                parsed.Add(Enum.Parse<TCore>(match));
            }
            else
            {
                errors[name] = [$"Unknown {name} '{(value.Length > 40 ? value[..40] : value)}'."];
            }
        }

        return parsed;
    }

    private sealed record FailureCursor(JobFailureRecord? Record);

    private static FailureCursor? DecodeFailureCursor(string? cursor, WorkspaceAccess access)
    {
        if (cursor is null)
        {
            return new FailureCursor(null);
        }

        return PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 3) is [var ticks, var kind, var id]
            && long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t <= DateTimeOffset.MaxValue.UtcTicks
            && Enum.TryParse<JobFailureSource>(kind, out var source) && Enum.IsDefined(source) && id.Length is > 0 and <= 64
            ? new FailureCursor(new JobFailureRecord(source, id, 0, null, new DateTimeOffset(t, TimeSpan.Zero)))
            : null;
    }

    private static CursorPage<JobFailure> FailurePage(IReadOnlyList<JobFailureRecord> records, int limit, WorkspaceAccess access, bool first)
    {
        var items = records.Take(limit).Select(JobMapping.ToFailure).ToList();
        string? next = null;
        if (records.Count > limit)
        {
            var last = records[limit - 1];
            next = PageCursor.Encode(access.Principal.UserId, access.WorkspaceId,
                last.FailedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), last.Source.ToString(), last.Id);
        }

        return new CursorPage<JobFailure>(items, next, new TotalCount(items.Count, next is null && first ? TotalRelation.Eq : TotalRelation.Gte));
    }
}

public static class JobEndpointRegistration
{
    public static IServiceCollection AddJobEndpoints(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.TryAddSingleton<IJobOperationsStore, JobOperationsStore>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IApiEndpointModule, JobEndpoints>();
        services.AddJobEventEndpoints(configuration);
        return services;
    }
}
