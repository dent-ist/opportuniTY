using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Search;
using Opportunity.Application.Search.SavedSearches;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.QueryLanguage;
using Opportunity.Core.Security;
using Opportunity.Data.Search;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Search;

/// <summary>
/// The Searches section (E07-T09, Q-65): saved searches and their folders under <c>/api/v1/workspaces/{workspaceId}</c>.
/// Everything needs <c>Search.Execute</c>; sharing also <c>SavedSearch.Share</c>. A search the caller cannot see answers
/// 404 exactly like one that does not exist; a visible search that only its owner or a Workspace Admin may change answers
/// 403. Saved searches run through <c>POST …/searches</c> with <c>savedSearchId</c> and freeze through
/// <c>POST …/snapshots</c> with <c>savedSearchId</c>; nested criteria are written <c>savedsearch:&lt;id&gt;</c>.
/// </summary>
public sealed class SavedSearchEndpoints : IApiEndpointModule
{
    public const string SearchesPath = "/saved-searches";
    public const string FoldersPath = "/saved-search-folders";

    /// <summary>The problem code of deleting a folder that still holds folders or saved searches (wave-9 contract).</summary>
    public const string FolderNotEmptyCode = "FOLDER_NOT_EMPTY";

    private const string NotFoundDetail = "No such saved search.";
    private const string FolderNotFoundDetail = "No such folder.";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(FoldersPath, ListFoldersAsync)
            .WithName("ListSavedSearchFolders")
            .WithTags("Saved searches")
            .WithSummary("Every saved-search folder of the workspace, ordered by name.")
            .Produces<SavedSearchFolderList>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        ws.MapPost(FoldersPath, CreateFolderAsync)
            .WithName("CreateSavedSearchFolder")
            .WithTags("Saved searches")
            .WithSummary("Create a folder (top level, or below parentFolderId).")
            .Produces<SavedSearchFolderResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(Permission.SearchExecute);

        ws.MapPut(FoldersPath + "/{folderId}", UpdateFolderAsync)
            .WithName("UpdateSavedSearchFolder")
            .WithTags("Saved searches")
            .WithSummary("Rename or move a folder (If-Match required). Its creator or a Workspace Admin may change it.")
            .Produces<SavedSearchFolderResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.SearchExecute);

        ws.MapDelete(FoldersPath + "/{folderId}", DeleteFolderAsync)
            .WithName("DeleteSavedSearchFolder")
            .WithTags("Saved searches")
            .WithSummary("Delete an empty folder (409 FOLDER_NOT_EMPTY otherwise). If-Match is honoured when sent.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(Permission.SearchExecute);

        ws.MapGet(SearchesPath, ListAsync)
            .WithName("ListSavedSearches")
            .WithTags("Saved searches")
            .WithSummary("The saved searches you can see (yours, shared with you or a group of yours; all as Workspace Admin), by name.")
            .WithDescription(
                "folderId narrows to one folder (folderId=root: searches outside any folder); q matches names (case-insensitive). " +
                "Each entry carries its owner, sharing, and the time, hit count and freshness of its last run.")
            .Produces<CursorPage<SavedSearchSummary>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        ws.MapGet(SearchesPath + "/share-candidates", ListShareCandidatesAsync)
            .WithName("ListSavedSearchShareCandidates")
            .WithTags("Saved searches")
            .WithSummary("Users and IdP groups of the workspace you can share a saved search with (needs SavedSearch.Share).")
            .WithDescription(
                "Users with a role in the workspace (directly or through one of their groups) and the IdP groups that hold a role, " +
                "except yourself, by display name; q matches names and e-mail addresses (case-insensitive); limit 1–500, default 50.")
            .Produces<SavedSearchShareCandidateList>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SavedSearchShare);

        ws.MapPost(SearchesPath, CreateAsync)
            .WithName("CreateSavedSearch")
            .WithTags("Saved searches")
            .WithSummary("Save a search: query text (validated now, re-parsed at every run), columns, sort and folder. Private until shared.")
            .Produces<SavedSearchResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        ws.MapGet(SearchesPath + "/{savedSearchId}", GetAsync)
            .WithName("GetSavedSearch")
            .WithTags("Saved searches")
            .WithSummary("A saved search with its criteria and ETag.")
            .Produces<SavedSearchResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        ws.MapPut(SearchesPath + "/{savedSearchId}", UpdateAsync)
            .WithName("UpdateSavedSearch")
            .WithTags("Saved searches")
            .WithSummary("Replace a saved search's name, folder, criteria, columns and sort (If-Match required; owner or Workspace Admin).")
            .Produces<SavedSearchResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.SearchExecute);

        ws.MapDelete(SearchesPath + "/{savedSearchId}", DeleteAsync)
            .WithName("DeleteSavedSearch")
            .WithTags("Saved searches")
            .WithSummary("Delete a saved search (If-Match required; owner or Workspace Admin).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.SearchExecute);

        ws.MapPost(SearchesPath + "/{savedSearchId}/clone", CloneAsync)
            .WithName("CloneSavedSearch")
            .WithTags("Saved searches")
            .WithSummary("Copy a saved search you can see as a new private search of yours.")
            .Produces<SavedSearchResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        ws.MapPut(SearchesPath + "/{savedSearchId}/sharing", ShareAsync)
            .WithName("ShareSavedSearch")
            .WithTags("Saved searches")
            .WithSummary("Replace the users and IdP groups a saved search is shared with (owner or Workspace Admin; needs SavedSearch.Share).")
            .WithDescription("Sharing never grants document access: whoever runs the search sees only documents they may see. If-Match is honoured when sent.")
            .Produces<SavedSearchResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequirePermission(Permission.SavedSearchShare);
    }

    internal static async Task<IResult> ListFoldersAsync(string workspaceId, HttpContext context, SavedSearchService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var folders = await service.ListFoldersAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new SavedSearchFolderList([.. folders.Select(ToResource)]));
    }

    internal static async Task<IResult> CreateFolderAsync(
        string workspaceId, SavedSearchFolderRequest request, HttpContext context, SavedSearchService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.CreateFolderAsync(access.Principal, access.WorkspaceId, request, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != SavedSearchOutcomeStatus.Created)
        {
            return Problem(outcome, FolderNotFoundDetail);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Folder!.Version);
        return TypedResults.Created(FolderLocation(access.WorkspaceId, outcome.Folder.FolderId), ToResource(outcome.Folder));
    }

    internal static async Task<IResult> UpdateFolderAsync(
        string workspaceId, string folderId, SavedSearchFolderRequest request, HttpContext context, SavedSearchService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(folderId, out var id)
            || await service.GetFolderAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(FolderNotFoundDetail);
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.UpdateFolderAsync(access.Principal, access.WorkspaceId, id, current.Version, request, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Status != SavedSearchOutcomeStatus.Ok)
        {
            return Problem(outcome, FolderNotFoundDetail);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Folder!.Version);
        return TypedResults.Ok(ToResource(outcome.Folder));
    }

    internal static async Task<IResult> DeleteFolderAsync(
        string workspaceId, string folderId, HttpContext context, SavedSearchService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(folderId, out var id)
            || await service.GetFolderAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(FolderNotFoundDetail);
        }

        if (OptionalIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.DeleteFolderAsync(access.Principal, access.WorkspaceId, id, current.Version, cancellationToken).ConfigureAwait(false);
        return outcome.Status == SavedSearchOutcomeStatus.Ok ? TypedResults.NoContent() : Problem(outcome, FolderNotFoundDetail);
    }

    internal static async Task<IResult> ListAsync(
        string workspaceId,
        [FromQuery] string? folderId,
        [FromQuery] string? q,
        [AsParameters] PageQuery page,
        HttpContext context,
        SavedSearchService service,
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

        Guid? folder = null;
        if (!string.IsNullOrEmpty(folderId))
        {
            if (string.Equals(folderId, "root", StringComparison.OrdinalIgnoreCase))
            {
                folder = Guid.Empty;
            }
            else if (Guid.TryParse(folderId, out var parsed))
            {
                folder = parsed;
            }
            else
            {
                return Problems.Validation(new Dictionary<string, string[]> { ["folderId"] = ["folderId is a folder ID or 'root'."] });
            }
        }

        if (q is { Length: > SavedSearchService.MaxNameLength })
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["q"] = [$"q has at most {SavedSearchService.MaxNameLength} characters."] });
        }

        var user = access.Principal.UserId;
        SavedSearchListPosition? after = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, user, access.WorkspaceId, 2) is not { } position || !Guid.TryParseExact(position[1], "N", out var lastId))
            {
                return PageCursor.Invalid();
            }

            after = new SavedSearchListPosition(position[0], lastId);
        }

        var limit = page.EffectiveLimit;
        var result = await service.ListAsync(access.Principal, access.WorkspaceId, new SavedSearchListFilter(folder, q, after, limit), cancellationToken)
            .ConfigureAwait(false);
        var items = result.Items.Select(ToSummary).ToList();
        var next = result.HasMore ? PageCursor.Encode(user, access.WorkspaceId, result.Items[^1].Name, result.Items[^1].SavedSearchId.ToString("N")) : null;
        return TypedResults.Ok(new CursorPage<SavedSearchSummary>(items, next,
            new TotalCount(items.Count, next is null && after is null ? TotalRelation.Eq : TotalRelation.Gte)));
    }

    internal static async Task<IResult> ListShareCandidatesAsync(
        string workspaceId,
        [FromQuery] string? q,
        [FromQuery] int? limit,
        HttpContext context,
        SavedSearchService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (new PageQuery(limit, null).Validate() is { } invalid)
        {
            return invalid;
        }

        if (q is { Length: > 256 })
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["q"] = ["q has at most 256 characters."] });
        }

        var items = await service.ListShareCandidatesAsync(access.Principal, access.WorkspaceId, q, limit ?? Pagination.DefaultLimit, cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(new SavedSearchShareCandidateList(items));
    }

    internal static async Task<IResult> CreateAsync(
        string workspaceId, SavedSearchRequest request, HttpContext context, SavedSearchService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.CreateAsync(access.Principal, access.WorkspaceId, request, cancellationToken).ConfigureAwait(false);
        return outcome.Status == SavedSearchOutcomeStatus.Created ? Created(context, access.WorkspaceId, outcome.Search!) : Problem(outcome, NotFoundDetail);
    }

    internal static async Task<IResult> GetAsync(
        string workspaceId, string savedSearchId, HttpContext context, SavedSearchService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(savedSearchId, out var id))
        {
            return Problems.NotFound(NotFoundDetail);
        }

        var outcome = await service.GetAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        return outcome.Status == SavedSearchOutcomeStatus.Ok ? Ok(context, outcome.Search!) : Problem(outcome, NotFoundDetail);
    }

    internal static async Task<IResult> UpdateAsync(
        string workspaceId, string savedSearchId, SavedSearchRequest request, HttpContext context, SavedSearchService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, savedSearchId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Search.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.UpdateAsync(current.Access.Principal, current.Access.WorkspaceId, current.Search.SavedSearchId, current.Search.Version,
            request, cancellationToken).ConfigureAwait(false);
        return outcome.Status == SavedSearchOutcomeStatus.Ok ? Ok(context, outcome.Search!) : Problem(outcome, NotFoundDetail);
    }

    internal static async Task<IResult> DeleteAsync(
        string workspaceId, string savedSearchId, HttpContext context, SavedSearchService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, savedSearchId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Search.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.DeleteAsync(current.Access.Principal, current.Access.WorkspaceId, current.Search.SavedSearchId, current.Search.Version,
            cancellationToken).ConfigureAwait(false);
        return outcome.Status == SavedSearchOutcomeStatus.Ok ? TypedResults.NoContent() : Problem(outcome, NotFoundDetail);
    }

    internal static async Task<IResult> CloneAsync(
        string workspaceId, string savedSearchId, SavedSearchCloneRequest? request, HttpContext context, SavedSearchService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(savedSearchId, out var id))
        {
            return Problems.NotFound(NotFoundDetail);
        }

        var outcome = await service.CloneAsync(access.Principal, access.WorkspaceId, id, request ?? new SavedSearchCloneRequest(), cancellationToken)
            .ConfigureAwait(false);
        return outcome.Status == SavedSearchOutcomeStatus.Created ? Created(context, access.WorkspaceId, outcome.Search!) : Problem(outcome, NotFoundDetail);
    }

    internal static async Task<IResult> ShareAsync(
        string workspaceId, string savedSearchId, SavedSearchSharingRequest request, HttpContext context, SavedSearchService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, savedSearchId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        if (OptionalIfMatch(context.Request, current.Search.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.ShareAsync(current.Access.Principal, current.Access.WorkspaceId, current.Search.SavedSearchId, current.Search.Version,
            request, cancellationToken).ConfigureAwait(false);
        return outcome.Status == SavedSearchOutcomeStatus.Ok ? Ok(context, outcome.Search!) : Problem(outcome, NotFoundDetail);
    }

    internal static SavedSearchSummary ToSummary(SavedSearchRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var run = r.LastRun;
        return new SavedSearchSummary(
            r.SavedSearchId,
            r.Name,
            r.FolderId,
            new SavedSearchOwner(r.OwnerId, r.OwnerDisplayName),
            r.Shares.Count == 0 ? SavedSearchScope.Private : SavedSearchScope.Shared,
            r.Shares,
            run?.At,
            run?.HitCount,
            run is null ? null : run.Exact ? TotalRelation.Eq : TotalRelation.Gte,
            run is null ? null : new SavedSearchFreshness(run.Current == true ? SavedSearchFreshnessState.Current : SavedSearchFreshnessState.CatchingUp, run.At),
            r.ModifiedAt,
            r.Version);
    }

    internal static SavedSearchResource ToResource(SavedSearchRecord r)
    {
        var s = ToSummary(r);
        return new SavedSearchResource(s.SavedSearchId, s.Name, s.FolderId, s.Owner, s.Scope, s.SharedWith, s.LastRunAt, s.LastHitCount, s.LastHitRelation,
            s.LastRunFreshness, s.ModifiedAt, s.Version, r.QueryText, r.Columns, r.Sort, r.IncludeFamily, r.AstVersion, r.IncludeDuplicates,
            r.IncludeThread);
    }

    private static SavedSearchFolderResource ToResource(SavedSearchFolderRecord f) => new(f.FolderId, f.Name, f.ParentFolderId, f.Version);

    private static async Task<(WorkspaceAccess Access, SavedSearchRecord Search)?> CurrentAsync(
        HttpContext context, string savedSearchId, SavedSearchService service, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(savedSearchId, out var id))
        {
            return null;
        }

        var outcome = await service.GetAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        return outcome.Search is { } search ? (access, search) : null;
    }

    /// <summary>Checks If-Match only when the client sent it.</summary>
    private static ProblemHttpResult? OptionalIfMatch(HttpRequest request, long version) =>
        request.Headers.IfMatch.Count == 0 ? null : EntityTags.CheckIfMatch(request, version);

    private static Ok<SavedSearchResource> Ok(HttpContext context, SavedSearchRecord search)
    {
        context.Response.Headers.ETag = EntityTags.ForVersion(search.Version);
        return TypedResults.Ok(ToResource(search));
    }

    private static Created<SavedSearchResource> Created(HttpContext context, Guid workspaceId, SavedSearchRecord search)
    {
        context.Response.Headers.ETag = EntityTags.ForVersion(search.Version);
        return TypedResults.Created(
            $"{ApiRoutes.V1Prefix}/workspaces/{workspaceId}{SearchesPath}/{search.SavedSearchId}", ToResource(search));
    }

    private static string FolderLocation(Guid workspaceId, Guid folderId) => $"{ApiRoutes.V1Prefix}/workspaces/{workspaceId}{FoldersPath}/{folderId}";

    private static IResult Problem(SavedSearchOutcome outcome, string notFoundDetail) => outcome.Status switch
    {
        SavedSearchOutcomeStatus.InvalidRequest => Problems.Validation(outcome.RequestErrors.ToDictionary()),
        SavedSearchOutcomeStatus.InvalidQuery => TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            detail: "The saved search's query cannot be run; see queryErrors.",
            type: ProblemCodes.TypeFor(ProblemCodes.InvalidQuery),
            extensions: new Dictionary<string, object?>
            {
                [Problems.CodeExtension] = ProblemCodes.InvalidQuery,
                ["queryErrors"] = outcome.QueryErrors,
            }),
        SavedSearchOutcomeStatus.Forbidden => Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden,
            "Only the owner or a Workspace Admin may change this."),
        SavedSearchOutcomeStatus.VersionConflict => Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict,
            "The resource was modified since it was read."),
        SavedSearchOutcomeStatus.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict,
            "A folder with this name already exists here."),
        SavedSearchOutcomeStatus.FolderNotEmpty => Problems.Create(StatusCodes.Status409Conflict, FolderNotEmptyCode,
            "The folder still holds folders or saved searches; move or delete them first."),
        _ => Problems.NotFound(notFoundDetail),
    };
}

public static class SavedSearchEndpointRegistration
{
    /// <summary>
    /// The Searches-section endpoints, the saved-search use cases and store, and <see cref="ISavedSearchQueries"/> for the
    /// search path (nested references, runs) and the validate endpoint.
    /// </summary>
    public static IServiceCollection AddSavedSearchEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => sp.GetService<QueryValidator>()?.Limits ?? QueryLimits.Default);
        services.AddPostgresSavedSearchStore();
        services.TryAddScoped<ISavedSearchQueries, SavedSearchQueries>();
        services.TryAddScoped<SavedSearchService>();
        services.AddSingleton<IApiEndpointModule, SavedSearchEndpoints>();
        return services;
    }
}
