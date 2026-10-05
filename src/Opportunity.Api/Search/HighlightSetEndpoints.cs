using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Search.HighlightSets;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.Security;
using Opportunity.Data.Search;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Search;

/// <summary>
/// Highlight Sets (E16-T12, Admin › Highlight Sets; familiarity guide §3.2 persistent highlighting) under
/// <c>/api/v1/workspaces/{workspaceId}</c>: every member lists and reads them and keeps their own toggles
/// (<c>…/highlight-set-selection</c>); <c>HighlightSet.Manage</c> creates, replaces and deletes them (If-Match on
/// replace and delete; audited). The viewer applies them through <c>GET …/documents/{id}/text/hits</c>.
/// </summary>
public sealed class HighlightSetEndpoints : IApiEndpointModule
{
    public const string SetsPath = "/highlight-sets";
    public const string SelectionPath = "/highlight-set-selection";

    private const string Tag = "Highlight sets";
    private const string NotFoundDetail = "No such highlight set.";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(SetsPath, ListAsync)
            .WithName("ListHighlightSets")
            .WithTags(Tag)
            .WithSummary("Every Highlight Set of the workspace by name, with the colour palette.")
            .Produces<HighlightSetList>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireWorkspaceMember();

        ws.MapPost(SetsPath, CreateAsync)
            .WithName("CreateHighlightSet")
            .WithTags(Tag)
            .WithSummary("Create a Highlight Set: name, colour and terms (HighlightSet.Manage; audited).")
            .WithDescription(
                "Each term is a word, \"phrase\", wildcard (at least 3 letters before * or ?), W/n proximity or an OR of these; "
                + "several unquoted words are a phrase. Invalid terms answer 400 with errors keyed terms[i].expression.")
            .Produces<HighlightSetResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(Permission.HighlightSetManage);

        ws.MapGet(SetsPath + "/{highlightSetId}", GetAsync)
            .WithName("GetHighlightSet")
            .WithTags(Tag)
            .WithSummary("A Highlight Set with its ETag.")
            .Produces<HighlightSetResource>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireWorkspaceMember();

        ws.MapPut(SetsPath + "/{highlightSetId}", UpdateAsync)
            .WithName("UpdateHighlightSet")
            .WithTags(Tag)
            .WithSummary("Replace a Highlight Set's name, colour and terms (If-Match required; HighlightSet.Manage; audited).")
            .Produces<HighlightSetResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.HighlightSetManage);

        ws.MapDelete(SetsPath + "/{highlightSetId}", DeleteAsync)
            .WithName("DeleteHighlightSet")
            .WithTags(Tag)
            .WithSummary("Delete a Highlight Set (If-Match required; HighlightSet.Manage; audited).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.HighlightSetManage);

        ws.MapGet(SelectionPath, GetSelectionAsync)
            .WithName("GetHighlightSetSelection")
            .WithTags(Tag)
            .WithSummary("Your highlighting toggles in this workspace: the sets you switched off and whether search hits show.")
            .Produces<HighlightSetSelection>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireWorkspaceMember();

        ws.MapPut(SelectionPath, SetSelectionAsync)
            .WithName("SetHighlightSetSelection")
            .WithTags(Tag)
            .WithSummary("Store your highlighting toggles in this workspace (last write wins; not audited).")
            .Produces<HighlightSetSelection>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireWorkspaceMember();
    }

    internal static async Task<IResult> ListAsync(string workspaceId, HttpContext context, HighlightSetService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var sets = await service.ListAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new HighlightSetList([.. sets.Select(ToResource)], HighlightSetService.Colors));
    }

    internal static async Task<IResult> CreateAsync(
        string workspaceId, HighlightSetRequest request, HttpContext context, HighlightSetService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.CreateAsync(access.Principal, access.WorkspaceId, request, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != HighlightSetOutcomeStatus.Created)
        {
            return Problem(outcome);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Set!.Version);
        return TypedResults.Created(
            $"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{SetsPath}/{outcome.Set.HighlightSetId}", ToResource(outcome.Set));
    }

    internal static async Task<IResult> GetAsync(
        string workspaceId, string highlightSetId, HttpContext context, HighlightSetService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, highlightSetId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        return Ok(context, current.Set);
    }

    internal static async Task<IResult> UpdateAsync(
        string workspaceId, string highlightSetId, HighlightSetRequest request, HttpContext context, HighlightSetService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, highlightSetId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Set.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.UpdateAsync(current.Access.Principal, current.Access.WorkspaceId, current.Set, current.Set.Version, request,
            cancellationToken).ConfigureAwait(false);
        return outcome.Status == HighlightSetOutcomeStatus.Ok ? Ok(context, outcome.Set!) : Problem(outcome);
    }

    internal static async Task<IResult> DeleteAsync(
        string workspaceId, string highlightSetId, HttpContext context, HighlightSetService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (await CurrentAsync(context, highlightSetId, service, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Set.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.DeleteAsync(current.Access.Principal, current.Access.WorkspaceId, current.Set, current.Set.Version, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Status == HighlightSetOutcomeStatus.Ok ? TypedResults.NoContent() : Problem(outcome);
    }

    internal static async Task<IResult> GetSelectionAsync(string workspaceId, HttpContext context, HighlightSetService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        return TypedResults.Ok(await service.GetSelectionAsync(access.WorkspaceId, access.Principal.UserId, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<IResult> SetSelectionAsync(
        string workspaceId, HighlightSetSelection selection, HttpContext context, HighlightSetService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (selection is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["Send the selection."] });
        }

        var outcome = await service.SetSelectionAsync(access.WorkspaceId, access.Principal.UserId, selection, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != HighlightSetOutcomeStatus.Ok)
        {
            return Problem(outcome);
        }

        return TypedResults.Ok(await service.GetSelectionAsync(access.WorkspaceId, access.Principal.UserId, cancellationToken).ConfigureAwait(false));
    }

    internal static HighlightSetResource ToResource(HighlightSetRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new HighlightSetResource(
            r.HighlightSetId,
            r.Name,
            r.Description,
            r.Color,
            [.. r.Terms.Select(t => new HighlightTermResource(t.TermId, t.Expression, t.Color))],
            new HighlightSetActor(r.ModifiedBy, r.ModifiedByDisplayName),
            r.ModifiedAt,
            r.Version);
    }

    private static async Task<(WorkspaceAccess Access, HighlightSetRecord Set)?> CurrentAsync(
        HttpContext context, string highlightSetId, HighlightSetService service, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(highlightSetId, out var id))
        {
            return null;
        }

        return await service.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is { } set ? (access, set) : null;
    }

    private static Ok<HighlightSetResource> Ok(HttpContext context, HighlightSetRecord set)
    {
        context.Response.Headers.ETag = EntityTags.ForVersion(set.Version);
        return TypedResults.Ok(ToResource(set));
    }

    private static IResult Problem(HighlightSetOutcome outcome) => outcome.Status switch
    {
        HighlightSetOutcomeStatus.InvalidRequest => Problems.Validation(outcome.Errors.ToDictionary()),
        HighlightSetOutcomeStatus.VersionConflict => Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict,
            "The highlight set was modified since it was read."),
        HighlightSetOutcomeStatus.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict,
            "A highlight set with this name already exists."),
        _ => Problems.NotFound(NotFoundDetail),
    };
}

public static class HighlightSetEndpointRegistration
{
    /// <summary>The Highlight Set endpoints, use cases and PostgreSQL store.</summary>
    public static IServiceCollection AddHighlightSetEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresHighlightSetStore();
        services.TryAddScoped<HighlightSetService>();
        services.AddSingleton<IApiEndpointModule, HighlightSetEndpoints>();
        return services;
    }
}
