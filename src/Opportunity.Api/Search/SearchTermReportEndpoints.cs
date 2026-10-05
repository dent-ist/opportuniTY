using System.Globalization;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Api.Jobs;
using Opportunity.Application.Jobs;
using Opportunity.Application.Search;
using Opportunity.Application.Search.TermReports;
using Opportunity.Contracts.Api;
using Opportunity.Core.SearchTermReports;
using Opportunity.Core.Security;
using Opportunity.Data.Jobs;
using Opportunity.Data.Search;
using Opportunity.Data.SearchWork;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Search;

/// <summary>
/// Search term reports (E07-T10, Q-30), under <c>/api/v1/workspaces/{workspaceId}/search-term-reports</c>: create (a
/// background <c>searchTermReport</c> job over a materialized Report snapshot), list, read, rerun over the same snapshot
/// and delete. PEP-1 requires <c>SearchTermReport.Run</c>; the service also requires <c>Search.Execute</c>. A report is
/// visible to its creator and to holders of <c>Job.ViewAll</c>; its counts are its executor's. The CSV/XLSX export is a
/// protected-content gateway endpoint (<see cref="Content.SearchTermReportContentEndpoints"/>), and a term opens as a
/// search through <c>POST …/searches {searchTermReportId, termId}</c>.
/// </summary>
public sealed class SearchTermReportEndpoints : IApiEndpointModule
{
    public const string Path = "/search-term-reports";

    private const string Tag = "Search Term Reports";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.Workspace.MapGroup(Path).WithTags(Tag).RequirePermission(Permission.SearchTermReportRun);

        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreateSearchTermReport")
            .WithSummary("Count named terms over the workspace, a saved search or a snapshot; 202 with the report and its job.")
            .WithDescription(
                "Give terms as [{name, expression}] or as termsCsv (Name,Expression lines). The report runs as a background job over a " +
                "materialized snapshot of the scope: per term documents with hits, with family, unique hits and unique hits with family, " +
                "plus totals. A term that does not parse or bind gets a per-term error and the report still runs. Counts exclude documents " +
                "the person running the report may not see.")
            .RequireIdempotencyKey()
            .Produces<SearchTermReportResource>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListSearchTermReports")
            .WithSummary("Search term reports, newest first: your own, or everyone's with Job.ViewAll.")
            .Produces<CursorPage<SearchTermReportSummary>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{reportId}", GetAsync)
            .WithName("GetSearchTermReport")
            .WithSummary("One search term report with its provenance (snapshot, search generation, index current), terms, counts and job.")
            .Produces<SearchTermReportResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{reportId}/rerun", RerunAsync)
            .WithName("RerunSearchTermReport")
            .WithSummary("Run a completed or failed report again over the same snapshot (its creator or a workspace admin); 202.")
            .WithDescription("The same snapshot and terms give the same numbers while the documents and access rights are unchanged.")
            .RequireIdempotencyKey()
            .Produces<SearchTermReportResource>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{reportId}", DeleteAsync)
            .WithName("DeleteSearchTermReport")
            .WithSummary("Delete a report (its creator or a workspace admin); a run in progress is cancelled. Audited.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> CreateAsync(
        string workspaceId, CreateSearchTermReportRequest request, HttpContext context, SearchTermReportService reports, ISearchTermReportStore store,
        IJobRepository jobs, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (request is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["Send the report request."] });
        }

        var key = context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString();
        var outcome = await reports.CreateAsync(access.Principal, access.WorkspaceId, request, key.Length > 0 ? key : null, cancellationToken)
            .ConfigureAwait(false);
        return await AcceptedAsync(access, outcome, store, jobs, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IResult> RerunAsync(
        string workspaceId, string reportId, HttpContext context, SearchTermReportService reports, ISearchTermReportStore store, IJobRepository jobs,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(reportId, out var id))
        {
            return Problems.NotFound("No such search term report.");
        }

        var outcome = await reports.RerunAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        return await AcceptedAsync(access, outcome, store, jobs, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IResult> DeleteAsync(
        string workspaceId, string reportId, HttpContext context, SearchTermReportService reports, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(reportId, out var id))
        {
            return Problems.NotFound("No such search term report.");
        }

        var outcome = await reports.DeleteAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        return outcome.Status == SearchTermReportOutcomeStatus.Accepted ? TypedResults.NoContent() : Problem(outcome);
    }

    internal static async Task<Results<Ok<CursorPage<SearchTermReportSummary>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        string workspaceId, [AsParameters] PageQuery page, HttpContext context, SearchTermReportService reports, ISearchTermReportStore store,
        CancellationToken cancellationToken)
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

        SearchTermReportListCursor? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 2) is not [var ticks, var rid]
                || !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t) || !Guid.TryParse(rid, out var reportId))
            {
                return PageCursor.Invalid();
            }

            after = new SearchTermReportListCursor(new DateTimeOffset(t, TimeSpan.Zero), reportId);
        }

        var all = await reports.SeesAllAsync(access.Principal, access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var limit = page.EffectiveLimit;
        var records = await store.ListAsync(access.WorkspaceId, all ? null : access.Principal.UserId, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var items = records.Take(limit).Select(ToSummary).ToList();
        var next = records.Count > limit
            ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId,
                records[limit - 1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), records[limit - 1].ReportId.ToString("N"))
            : null;
        return TypedResults.Ok(new CursorPage<SearchTermReportSummary>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<IResult> GetAsync(
        string workspaceId, string reportId, HttpContext context, SearchTermReportService reports, ISearchTermReportStore store, IJobRepository jobs,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(reportId, out var id)
            || await reports.GetVisibleAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } report
            || await ToResourceAsync(report, store, jobs, cancellationToken).ConfigureAwait(false) is not { } resource)
        {
            return Problems.NotFound("No such search term report.");
        }

        return TypedResults.Ok(resource);
    }

    private static async Task<IResult> AcceptedAsync(
        WorkspaceAccess access, SearchTermReportOutcome outcome, ISearchTermReportStore store, IJobRepository jobs, CancellationToken cancellationToken)
    {
        if (outcome.Status != SearchTermReportOutcomeStatus.Accepted)
        {
            return Problem(outcome);
        }

        var resource = await ToResourceAsync(outcome.Report!, store, jobs, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The report's job is missing.");
        return ApiResults.JobAccepted(access.WorkspaceId.ToString(), resource.JobId.ToString(), resource);
    }

    private static IResult Problem(SearchTermReportOutcome outcome) => outcome.Status switch
    {
        SearchTermReportOutcomeStatus.Invalid => Problems.Validation(outcome.Errors!.ToDictionary()),
        SearchTermReportOutcomeStatus.Forbidden => Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden,
            outcome.Detail ?? "You do not have permission for this operation."),
        SearchTermReportOutcomeStatus.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, outcome.Detail),
        _ => Problems.NotFound(outcome.Detail),
    };

    internal static async Task<SearchTermReportResource?> ToResourceAsync(
        SearchTermReportRecord report, ISearchTermReportStore store, IJobRepository jobs, CancellationToken cancellationToken)
    {
        if (await jobs.GetAsync(report.WorkspaceId, report.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return null;
        }

        var terms = await store.GetTermsAsync(report.WorkspaceId, report.ReportId, cancellationToken).ConfigureAwait(false);
        var completed = report.Status == SearchTermReportStatus.Completed;
        return new SearchTermReportResource
        {
            ReportId = report.ReportId,
            Name = report.Name,
            Status = Status(report.Status),
            StatusReason = report.StatusReason,
            Scope = Scope(report),
            TermCount = report.TermCount,
            CreatedBy = new SearchTermReportUser(report.CreatedBy, report.CreatedByDisplay),
            CreatedAt = report.CreatedAt,
            CompletedAt = report.CompletedAt,
            JobId = report.JobId,
            SnapshotId = report.SnapshotId,
            SearchGeneration = report.SearchGeneration,
            IndexCurrent = report.IndexCurrent,
            ExecutedBy = new SearchTermReportUser(report.ExecutedBy, report.ExecutedByDisplay),
            ExecutedAt = report.ExecutedAt,
            Totals = completed
                ? new SearchTermReportTotals(report.DocumentsInScope ?? 0, report.DocumentsWithHits ?? 0, report.DocumentsWithHitsIncludingFamily ?? 0,
                    report.DocumentsWithoutHits ?? 0)
                : null,
            Terms = [.. terms.Select(t => new SearchTermReportTerm(
                t.TermId,
                t.Name,
                t.Expression,
                t.Error is { } e ? new SearchTermReportTermError(e.Code, e.Message, e.Position) : null,
                completed ? t.DocumentsWithHits : null,
                completed ? t.DocumentsWithHitsIncludingFamily : null,
                completed ? t.UniqueHits : null,
                completed ? t.UniqueHitsIncludingFamily : null))],
            Job = JobEndpoints.ToResource(job),
        };
    }

    internal static SearchTermReportSummary ToSummary(SearchTermReportRecord report) => new()
    {
        ReportId = report.ReportId,
        Name = report.Name,
        Status = Status(report.Status),
        Scope = Scope(report),
        TermCount = report.TermCount,
        CreatedBy = new SearchTermReportUser(report.CreatedBy, report.CreatedByDisplay),
        CreatedAt = report.CreatedAt,
        CompletedAt = report.CompletedAt,
        JobId = report.JobId,
    };

    private static SearchTermReportStatusResource Status(SearchTermReportStatus status) => Enum.Parse<SearchTermReportStatusResource>(status.ToString());

    private static SearchTermReportScopeResource Scope(SearchTermReportRecord report) =>
        new(Enum.Parse<SearchTermReportScopeKindResource>(report.ScopeKind.ToString()), report.ScopeId, report.ScopeName);
}

/// <summary>
/// Runs search term reports in the API host (it composes search and the PDP): polls the workspaces' active reports and
/// advances each with <see cref="SearchTermReportRunner"/>; any number of instances may run (claims and chunk leases are
/// in PostgreSQL).
/// </summary>
internal sealed partial class SearchTermReportBackgroundService(
    IServiceScopeFactory scopes,
    SearchTermReportSignal signal,
    SearchTermReportOptions options,
    ILogger<SearchTermReportBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.BackgroundEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (signal.Reader.TryRead(out var nudge))
                {
                    await ProcessAsync(nudge.WorkspaceId, nudge.ReportId, stoppingToken).ConfigureAwait(false);
                }

                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ISearchTermReportStore>();
                var workspaces = scope.ServiceProvider.GetRequiredService<Application.Snapshots.IDocumentSetSnapshotStore>();
                foreach (var ws in await workspaces.GetWorkspacesAsync(stoppingToken).ConfigureAwait(false))
                {
                    foreach (var id in await store.GetActiveAsync(ws, 10, stoppingToken).ConfigureAwait(false))
                    {
                        await ProcessAsync(ws, id, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // PostgreSQL or OpenSearch unavailable (e.g. a host without them configured): retry on the next pass.
                LogPassFailed(logger, ex);
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(options.PollInterval);
            try
            {
                await signal.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ProcessAsync(Guid workspaceId, Guid reportId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SearchTermReportRunner>().ProcessAsync(workspaceId, reportId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            LogReportFailed(logger, ex, reportId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search term report pass failed; retrying on the next poll")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search term report {ReportId} could not be advanced; retrying on the next poll")]
    private static partial void LogReportFailed(ILogger logger, Exception exception, Guid reportId);
}

public static class SearchTermReportEndpointRegistration
{
    /// <summary>The report endpoints, service, store, runner and its background loop (needs search and snapshots registered).</summary>
    public static IServiceCollection AddSearchTermReportEndpoints(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new SearchTermReportOptions();
        configuration.GetSection(SearchTermReportOptions.SectionName).Bind(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresSearchTermReportStore();
        services.AddPostgresJobChunkStore();
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.TryAddSingleton<SearchTermReportSignal>();
        services.TryAddScoped<SearchTermReportService>();
        services.TryAddScoped<SearchTermReportRunner>();
        services.TryAddScoped<ISearchTermHitSource, SearchTermHitSource>();
        services.AddHostedService<SearchTermReportBackgroundService>();
        services.AddSingleton<IApiEndpointModule, SearchTermReportEndpoints>();
        return services;
    }
}
