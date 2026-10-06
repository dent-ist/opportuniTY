using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Api.Jobs;
using Opportunity.Application.Jobs;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Search.Reindex;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Jobs;
using Opportunity.Data.Search;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Search;

/// <summary>
/// Search index operations (E07-T11): <c>GET …/search-index</c> shows where the workspace's search projection lives and
/// its recent reindexes; <c>POST …/search-index/reindexes</c> starts an alias-based reindex as a job (202 + job,
/// Idempotency-Key). Starting needs <c>Job.Manage</c>: a reindex is an operational job on the workspace's search
/// infrastructure (admin-only, like managing other users' jobs) that changes no document and grants no access, so it
/// needs neither a field nor a security permission, and a new catalogue entry would add nothing the admin role lacks.
/// Reading needs <c>Job.ViewAll</c>, which already shows every job and, per Q-73, the generation numbers.
/// </summary>
public sealed class SearchIndexEndpoints : IApiEndpointModule
{
    public const string StatusPath = "/search-index";
    public const string ReindexesPath = "/search-index/reindexes";

    private const string Tag = "Search";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(StatusPath, GetStatusAsync)
            .WithName("GetSearchIndexStatus")
            .WithTags(Tag)
            .WithSummary("Where the workspace's search projection lives and its recent reindexes.")
            .WithDescription("Placement facts only (tier, projection generation, revision); physical index names never leave the server.")
            .RequirePermission(Permission.JobViewAll)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.Workspace.MapPost(ReindexesPath, StartAsync)
            .WithName("StartSearchReindex")
            .WithTags(Tag)
            .WithSummary("Rebuild the workspace's search projection into a new index; 202 with the job.")
            .WithDescription(
                "Searches keep working throughout: changes go to both indexes while the new one is filled from the database, it " +
                "is validated against the database and searches move to it in one alias switch. The previous index stays " +
                "read-only for the retention period, then is deleted. Cancel the job to abort before the switch. At most one " +
                "reindex per workspace runs at a time (409).")
            .RequirePermission(Permission.JobManage)
            .RequireIdempotencyKey()
            .Produces<JobResource>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    internal static async Task<Results<Ok<SearchIndexStatusResource>, ProblemHttpResult>> GetStatusAsync(
        string workspaceId, HttpContext context, ReindexService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var status = await service.GetStatusAsync(access.WorkspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new SearchIndexStatusResource(
            status.Placement is { } p ? new WorkspaceSearchPlacementResource(Kind(p.Kind), p.ProjectionGeneration, State(p.State)) : null,
            [.. status.Runs.Select(ToResource)]));
    }

    internal static async Task<Results<Accepted<JobResource>, ValidationProblem, ProblemHttpResult>> StartAsync(
        string workspaceId, SearchReindexRequest? body, HttpContext context, ReindexService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        body ??= new SearchReindexRequest();
        if (body.Placement is { } placement && !Enum.IsDefined(placement))
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["placement"] = ["placement must be shared or dedicated."] });
        }

        var request = new ReindexRequest(
            body.Placement switch
            {
                WorkspaceSearchPlacementKind.Dedicated => IndexPlacementKind.Dedicated,
                WorkspaceSearchPlacementKind.Shared => IndexPlacementKind.Shared,
                _ => null,
            },
            body.Generation,
            body.PrimaryShards);
        var key = context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString();
        var outcome = await service.StartAsync(access.WorkspaceId, request,
            ReindexInitiator.User(access.Principal.UserId, access.Principal.CorrelationId), key.Length > 0 ? key : null, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Status switch
        {
            ReindexStartStatus.Ok => ApiResults.JobAccepted(access.WorkspaceId.ToString(), outcome.Job!.JobId.ToString(), JobEndpoints.ToResource(outcome.Job)),
            ReindexStartStatus.Invalid => Problems.Validation(new Dictionary<string, string[]>(outcome.Errors), "The reindex was not started."),
            ReindexStartStatus.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict,
                $"Reindex job {outcome.Run?.JobId} of this workspace is still in progress."),
            _ => Problems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.IdempotencyKeyReuse,
                $"This {IdempotencyMiddleware.HeaderName} was already used for a different job."),
        };
    }

    internal static SearchReindexResource ToResource(ReindexRun run) => new(
        run.JobId,
        (SearchReindexPhase)((int)run.Phase - 1),
        Location(run.Source),
        Location(run.Target),
        run.DocumentsPlanned,
        run.Validation is { } v
            ? new SearchReindexValidationResource(v.Passed, v.FullCheck, v.PostgresDocuments, v.IndexDocuments, v.DocumentsCompared, v.Rechecked,
                v.StaleDocuments, v.MissingDocuments, v.UnexpectedDocuments, v.HashesCompared, v.HashMismatches, v.Failure)
            : null,
        run.Error,
        run.CreatedAt,
        run.UpdatedAt,
        run.SwitchedAt,
        run.RetainUntil,
        run.FinishedAt);

    private static SearchIndexLocationResource? Location(IndexLocation? location) =>
        location is null ? null : new SearchIndexLocationResource(Kind(location.Kind), location.Generation, location.Revision);

    private static WorkspaceSearchPlacementKind Kind(IndexPlacementKind kind) =>
        kind == IndexPlacementKind.Dedicated ? WorkspaceSearchPlacementKind.Dedicated : WorkspaceSearchPlacementKind.Shared;

    private static WorkspaceSearchPlacementState State(IndexPlacementState state) => state switch
    {
        IndexPlacementState.Building => WorkspaceSearchPlacementState.Building,
        IndexPlacementState.Moving => WorkspaceSearchPlacementState.Moving,
        IndexPlacementState.Deleting => WorkspaceSearchPlacementState.Deleting,
        _ => WorkspaceSearchPlacementState.Active,
    };
}

public static class SearchIndexEndpointRegistration
{
    /// <summary>
    /// The search index API over the reindex store and the job store. Placement facts come from index management when the
    /// workspace endpoints registered it (OpenSearch configured); without it a start is not checked against the placement.
    /// </summary>
    public static IServiceCollection AddSearchIndexEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPostgresReindexStore();
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.TryAddScoped(sp => new ReindexService(
            sp.GetRequiredService<IReindexStore>(), sp.GetRequiredService<IJobRepository>(), sp.GetService<IWorkspaceSearchPlacement>()));
        services.AddSingleton<IApiEndpointModule, SearchIndexEndpoints>();
        return services;
    }
}
