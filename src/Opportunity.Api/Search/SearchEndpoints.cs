using Microsoft.AspNetCore.Mvc;

using Opportunity.Api.Conventions;
using Opportunity.Application.Search;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.Security;
using Opportunity.Data.Search;
using Opportunity.Search;
using Opportunity.Security.Authentication;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Search;

/// <summary>
/// The search endpoints behind the query bar and document list (E07-T05, UI E16):
/// <c>POST /api/v1/workspaces/{workspaceId}/searches</c> runs a search and returns its first page;
/// <c>GET .../searches/{searchId}/pages</c> pages through it by opaque cursor, page number (top of the result window)
/// or <c>last=true</c> (Q-49). Handles are bound to the caller, their session and the workspace; anything else is 404.
/// </summary>
public sealed class SearchEndpoints : IApiEndpointModule
{
    public const string Path = "/searches";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapPost(Path, SearchAsync)
            .WithName("RunSearch")
            .WithTags("Search")
            .WithSummary("Run a query-language search and return its first page of post-filtered results.")
            .WithDescription(
                "Hits are re-checked against current access rights before the page is returned (restricted and walled documents " +
                "are omitted). Totals above 10,000 are lower bounds unless countExact is set; counts and facets are approximate " +
                "while the index catches up (freshness).")
            .Produces<SearchResultPage>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        routes.Workspace.MapGet(Path + "/{searchId}/pages", GetPageAsync)
            .WithName("GetSearchPage")
            .WithTags("Search")
            .WithSummary("Another page of a search: by cursor (next/previous), page number near the top, or the last page.")
            .WithDescription("Give exactly one of cursor, page or last=true. Searches expire after inactivity (404).")
            .Produces<SearchResultPage>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);
    }

    internal static async Task<IResult> SearchAsync(
        string workspaceId, SearchRequest request, HttpContext context, ISearchService search, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (Caller(context) is not { } caller)
        {
            return Problems.NotFound();
        }

        return ToResult(await search.SearchAsync(caller, request, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<IResult> GetPageAsync(
        string workspaceId,
        string searchId,
        [FromQuery] string? cursor,
        [FromQuery] int? page,
        [FromQuery] bool? last,
        HttpContext context,
        ISearchService search,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (Caller(context) is not { } caller)
        {
            return Problems.NotFound();
        }

        return ToResult(await search.GetPageAsync(caller, searchId, new SearchPageRequest(cursor, page, last == true), cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>The caller from PEP-1's authorized workspace (never the raw route value) and the server-side session.</summary>
    private static SearchCaller? Caller(HttpContext context)
    {
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return null;
        }

        var session = Guid.TryParse(context.User.FindFirst(OpportunityClaimTypes.SessionId)?.Value, out var id) ? id : (Guid?)null;
        return new SearchCaller(access.Principal, access.WorkspaceId, session);
    }

    private static IResult ToResult(SearchOutcome outcome) => outcome.Status switch
    {
        SearchStatus.Ok => TypedResults.Ok(outcome.Page),
        SearchStatus.InvalidRequest => Problems.Validation(outcome.RequestErrors.ToDictionary()),
        SearchStatus.InvalidQuery => TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            detail: "The query cannot be run; see queryErrors.",
            type: ProblemCodes.TypeFor(ProblemCodes.InvalidQuery),
            extensions: new Dictionary<string, object?>
            {
                [Problems.CodeExtension] = ProblemCodes.InvalidQuery,
                ["queryErrors"] = outcome.QueryErrors,
            }),
        SearchStatus.Forbidden => Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden,
            "You do not have permission for this operation."),
        _ => Problems.NotFound("No such search."),
    };
}

public static class SearchEndpointRegistration
{
    /// <summary>The search endpoints, the search service and its PostgreSQL stores.</summary>
    public static IServiceCollection AddSearchEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPostgresIndexPlacementStore();
        services.AddPostgresSearchSessionStore();
        services.AddOpenSearchSearchService();
        services.AddSingleton<IApiEndpointModule, SearchEndpoints>();
        return services;
    }
}
