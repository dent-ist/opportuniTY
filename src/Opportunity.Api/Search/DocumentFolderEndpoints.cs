using Microsoft.AspNetCore.Mvc;

using Opportunity.Api.Conventions;
using Opportunity.Application.Documents;
using Opportunity.Application.Search;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.Security;
using Opportunity.Data.Documents;
using Opportunity.Security.Authentication;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Search;

/// <summary>
/// The read-only Folders tree of the Documents browser (#179, Q-55): folders derived from the Folder Path an import maps.
/// <c>GET /api/v1/workspaces/{workspaceId}/document-folders</c> lists one level (top level, or the children of
/// <c>parentId</c>) with counts of the documents the caller may see; <c>GET …/document-folders/{documentFolderId}</c>
/// reads one folder with the folders above it. Selecting a folder runs a search with <c>folder</c>
/// (<see cref="SearchRequest.Folder"/>). Folders that hold nothing the caller may see are never listed (Q-52).
/// </summary>
public sealed class DocumentFolderEndpoints : IApiEndpointModule
{
    public const string Path = "/document-folders";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(Path, ListAsync)
            .WithName("ListDocumentFolders")
            .WithTags("Search")
            .WithSummary("One level of the imported folder tree, in name order, with the documents the caller may see in each folder.")
            .WithDescription(
                "Without parentId the top-level folders; with it the folder's subfolders (load the tree lazily, one level at a time). " +
                "Restricted and walled documents are not counted and a folder holding none the caller may see is not listed. Counts " +
                "follow the search index (countsApproximate while it catches up).")
            .Produces<DocumentFolderPage>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        routes.Workspace.MapGet(Path + "/{documentFolderId}", GetAsync)
            .WithName("GetDocumentFolder")
            .WithTags("Search")
            .WithSummary("One imported folder with its counts and the folders above it (to restore a selection or a link).")
            .Produces<DocumentFolderResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);
    }

    internal static async Task<IResult> ListAsync(
        string workspaceId,
        [FromQuery] string? parentId,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        HttpContext context,
        DocumentFolderService folders,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (Caller(context) is not { } caller)
        {
            return Problems.NotFound();
        }

        Guid? parent = null;
        if (parentId is not null)
        {
            if (!Guid.TryParse(parentId, out var id))
            {
                return Problems.NotFound("No such folder.");
            }

            parent = id;
        }

        var outcome = await folders.ListAsync(caller, parent, cursor, limit, cancellationToken).ConfigureAwait(false);
        if (outcome.Errors is { } errors)
        {
            return Problems.Validation(errors.ToDictionary());
        }

        return outcome.Status switch
        {
            DocumentFolderCountStatus.Ok => TypedResults.Ok(outcome.Page),
            DocumentFolderCountStatus.Forbidden => Forbidden(),
            _ => Problems.NotFound("No such folder."),
        };
    }

    internal static async Task<IResult> GetAsync(
        string workspaceId, string documentFolderId, HttpContext context, DocumentFolderService folders, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (Caller(context) is not { } caller || !Guid.TryParse(documentFolderId, out var id))
        {
            return Problems.NotFound("No such folder.");
        }

        var outcome = await folders.GetAsync(caller, id, cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            DocumentFolderCountStatus.Ok => TypedResults.Ok(outcome.Folder),
            DocumentFolderCountStatus.Forbidden => Forbidden(),
            _ => Problems.NotFound("No such folder."),
        };
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Forbidden() =>
        Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation.");

    /// <summary>The caller from PEP-1's authorized workspace (never the raw route value) and the server-side session.</summary>
    private static SearchCaller? Caller(HttpContext context)
    {
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return null;
        }

        var session = Guid.TryParse(context.User.FindFirst(OpportunityClaimTypes.SessionId)?.Value, out var sessionId) ? sessionId : (Guid?)null;
        return new SearchCaller(access.Principal, access.WorkspaceId, session);
    }
}

public static class DocumentFolderEndpointRegistration
{
    /// <summary>The Folders tree endpoints and their PostgreSQL store (needs the search service for the counts).</summary>
    public static IServiceCollection AddDocumentFolderEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPostgresDocumentFolderStore();
        services.AddScoped<DocumentFolderService>();
        services.AddSingleton<IApiEndpointModule, DocumentFolderEndpoints>();
        return services;
    }
}
