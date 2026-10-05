using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Search.GridViews;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.Security;
using Opportunity.Data.Search;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Search;

/// <summary>
/// Document-list views (E16-T09) under <c>/api/v1/workspaces/{workspaceId}/grid-views</c>: saved column sets and sorts,
/// personal or shared with the workspace, and each user's layout (the view last used plus adjustments). Everything needs
/// <c>Search.Execute</c>; shared views are created, changed and deleted with <c>View.ManageShared</c> (403 otherwise).
/// A view the caller cannot see answers 404 exactly like one that does not exist.
/// </summary>
public sealed class GridViewEndpoints : IApiEndpointModule
{
    public const string ViewsPath = "/grid-views";
    public const string LayoutPath = ViewsPath + "/layout";

    private const string NotFoundDetail = "No such view.";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(ViewsPath, ListAsync)
            .WithName("ListGridViews")
            .WithTags("Grid views")
            .WithSummary("The document-list views you can see: every shared view and your own (all as Workspace Admin), shared first, by name.")
            .Produces<GridViewList>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        ws.MapPost(ViewsPath, CreateAsync)
            .WithName("CreateGridView")
            .WithTags("Grid views")
            .WithSummary("Save a view (columns and up to 3 sort fields). Personal by default; a shared view needs View.ManageShared.")
            .Produces<GridViewResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(Permission.SearchExecute);

        ws.MapGet(LayoutPath, GetLayoutAsync)
            .WithName("GetGridLayout")
            .WithTags("Grid views")
            .WithSummary("Your document-list layout in this workspace: the view you last used and your unsaved adjustments to it.")
            .Produces<GridLayoutResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        ws.MapPut(LayoutPath, SaveLayoutAsync)
            .WithName("SaveGridLayout")
            .WithTags("Grid views")
            .WithSummary("Remember your document-list layout in this workspace (view, columns, sort).")
            .Produces<GridLayoutResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        ws.MapGet(ViewsPath + "/{viewId}", GetAsync)
            .WithName("GetGridView")
            .WithTags("Grid views")
            .WithSummary("A view with its ETag.")
            .Produces<GridViewResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.SearchExecute);

        ws.MapPut(ViewsPath + "/{viewId}", UpdateAsync)
            .WithName("UpdateGridView")
            .WithTags("Grid views")
            .WithSummary("Replace a view (If-Match required). Personal: its owner or a Workspace Admin; shared, or changing visibility: View.ManageShared.")
            .Produces<GridViewResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.SearchExecute);

        ws.MapDelete(ViewsPath + "/{viewId}", DeleteAsync)
            .WithName("DeleteGridView")
            .WithTags("Grid views")
            .WithSummary("Delete a view (If-Match required). Personal: its owner or a Workspace Admin; shared: View.ManageShared.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.SearchExecute);
    }

    internal static async Task<IResult> ListAsync(string workspaceId, HttpContext context, GridViewService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var views = await service.ListAsync(access.Principal, access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new GridViewList([.. views.Select(ToResource)]));
    }

    internal static async Task<IResult> CreateAsync(
        string workspaceId, GridViewRequest request, HttpContext context, GridViewService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.CreateAsync(access.Principal, access.WorkspaceId, request, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != GridViewOutcomeStatus.Created)
        {
            return Problem(outcome);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.View!.Record.Version);
        return TypedResults.Created(
            $"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{ViewsPath}/{outcome.View.Record.ViewId}", ToResource(outcome.View));
    }

    internal static async Task<IResult> GetAsync(
        string workspaceId, string viewId, HttpContext context, GridViewService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(viewId, out var id))
        {
            return Problems.NotFound(NotFoundDetail);
        }

        var outcome = await service.GetAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        return outcome.Status == GridViewOutcomeStatus.Ok ? Ok(context, outcome.View!) : Problem(outcome);
    }

    internal static async Task<IResult> UpdateAsync(
        string workspaceId, string viewId, GridViewRequest request, HttpContext context, GridViewService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, viewId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        if (EntityTags.CheckIfMatch(context.Request, current.View.Record.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.UpdateAsync(current.Access.Principal, current.Access.WorkspaceId, current.View.Record.ViewId,
            current.View.Record.Version, request, cancellationToken).ConfigureAwait(false);
        return outcome.Status == GridViewOutcomeStatus.Ok ? Ok(context, outcome.View!) : Problem(outcome);
    }

    internal static async Task<IResult> DeleteAsync(
        string workspaceId, string viewId, HttpContext context, GridViewService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, viewId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        if (EntityTags.CheckIfMatch(context.Request, current.View.Record.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.DeleteAsync(current.Access.Principal, current.Access.WorkspaceId, current.View.Record.ViewId,
            current.View.Record.Version, cancellationToken).ConfigureAwait(false);
        return outcome.Status == GridViewOutcomeStatus.Ok ? TypedResults.NoContent() : Problem(outcome);
    }

    internal static async Task<IResult> GetLayoutAsync(string workspaceId, HttpContext context, GridViewService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var layout = await service.GetLayoutAsync(access.Principal, access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToResource(layout));
    }

    internal static async Task<IResult> SaveLayoutAsync(
        string workspaceId, GridLayoutRequest request, HttpContext context, GridViewService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.SaveLayoutAsync(access.Principal, access.WorkspaceId, request, cancellationToken).ConfigureAwait(false);
        return outcome.Status == GridViewOutcomeStatus.Ok ? TypedResults.Ok(ToResource(outcome.Layout)) : Problem(outcome);
    }

    internal static GridViewResource ToResource(GridView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var r = view.Record;
        return new GridViewResource(
            r.ViewId,
            r.Name,
            r.Shared ? GridViewVisibility.Shared : GridViewVisibility.Personal,
            new GridViewOwner(r.OwnerId, r.OwnerDisplayName),
            r.Columns,
            r.Sort,
            r.ModifiedAt,
            r.Version,
            view.CanEdit);
    }

    private static GridLayoutResource ToResource(GridLayoutRecord? layout) =>
        layout is null ? new GridLayoutResource(null, null, null, null) : new GridLayoutResource(layout.ViewId, layout.Columns, layout.Sort, layout.ModifiedAt);

    private static async Task<(WorkspaceAccess Access, GridView View)?> CurrentAsync(
        HttpContext context, string viewId, GridViewService service, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(viewId, out var id))
        {
            return null;
        }

        var outcome = await service.GetAsync(access.Principal, access.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
        return outcome.View is { } view ? (access, view) : null;
    }

    private static Ok<GridViewResource> Ok(HttpContext context, GridView view)
    {
        context.Response.Headers.ETag = EntityTags.ForVersion(view.Record.Version);
        return TypedResults.Ok(ToResource(view));
    }

    private static IResult Problem(GridViewOutcome outcome) => outcome.Status switch
    {
        GridViewOutcomeStatus.InvalidRequest => Problems.Validation(outcome.RequestErrors.ToDictionary()),
        GridViewOutcomeStatus.Forbidden => Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden,
            "Shared views are managed with the View.ManageShared permission; personal views by their owner or a Workspace Admin."),
        GridViewOutcomeStatus.VersionConflict => Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict,
            "The resource was modified since it was read."),
        GridViewOutcomeStatus.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict,
            outcome.ConflictDetail ?? "The view conflicts with another view."),
        _ => Problems.NotFound(NotFoundDetail),
    };
}

public static class GridViewEndpointRegistration
{
    /// <summary>The document-list view endpoints, their use cases and the PostgreSQL store.</summary>
    public static IServiceCollection AddGridViewEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresGridViewStore();
        services.TryAddScoped<GridViewService>();
        services.AddSingleton<IApiEndpointModule, GridViewEndpoints>();
        return services;
    }
}
