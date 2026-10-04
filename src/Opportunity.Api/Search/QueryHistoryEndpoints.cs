using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Search;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.Security;
using Opportunity.Data.Search;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Search;

/// <summary>
/// The query bar's recent searches (#186, E16-T01): <c>GET /api/v1/workspaces/{workspaceId}/query-history</c> lists the
/// caller's last 50 distinct queries in the workspace, newest first; <c>POST</c> records a run. Only queries that parse
/// are stored, the time is the server's, and every read and write is bound to the authorized caller (never a user ID
/// from the request). Query text is Q-16 search text: it stays in PostgreSQL (RLS, no read-only role access) and is
/// never cached in the browser. Recording is not a separate audit event; running the search is (Search.Executed).
/// </summary>
public sealed class QueryHistoryEndpoints : IApiEndpointModule
{
    public const string Path = "/query-history";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(Path, ListAsync)
            .WithName("ListQueryHistory")
            .WithTags("Search")
            .WithSummary("The caller's recent queries in the workspace: distinct, newest first, at most 50.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        routes.Workspace.MapPost(Path, RecordAsync)
            .WithName("RecordQueryHistory")
            .WithTags("Search")
            .WithSummary("Record a query the caller ran; re-running a query moves it to the top.")
            .WithDescription("The query must parse (see query-validations); surrounding whitespace is trimmed and empty queries are rejected.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);
    }

    internal static async Task<Results<Ok<QueryHistoryResource>, ProblemHttpResult>> ListAsync(
        string workspaceId, HttpContext context, IQueryHistoryStore store, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var entries = await store.ListAsync(access.WorkspaceId, access.Principal.UserId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new QueryHistoryResource([.. entries.Select(e => new QueryHistoryEntryResource(e.QueryText, e.LastRunAt))]));
    }

    internal static async Task<IResult> RecordAsync(
        string workspaceId,
        QueryHistoryRequest request,
        HttpContext context,
        QueryValidator validator,
        IQueryHistoryStore store,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var query = request.Query?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["query"] = ["A non-empty query is required."] });
        }

        var result = validator.Validate(query);
        if (!result.Valid)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Only valid queries are kept in the history; see queryErrors.",
                type: ProblemCodes.TypeFor(ProblemCodes.InvalidQuery),
                extensions: new Dictionary<string, object?>
                {
                    [Problems.CodeExtension] = ProblemCodes.InvalidQuery,
                    ["queryErrors"] = result.Errors,
                });
        }

        await store.RecordAsync(access.WorkspaceId, access.Principal.UserId, query, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }
}

public static class QueryHistoryEndpointRegistration
{
    /// <summary>The query-history endpoints and their PostgreSQL store (needs <see cref="QueryValidator"/>).</summary>
    public static IServiceCollection AddQueryHistoryEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresQueryHistoryStore();
        services.AddSingleton<IApiEndpointModule, QueryHistoryEndpoints>();
        return services;
    }
}
