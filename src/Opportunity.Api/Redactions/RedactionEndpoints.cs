using System.Globalization;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

using Opportunity.Api.Content;
using Opportunity.Api.Conventions;
using Opportunity.Application.Redactions;
using Opportunity.Contracts.Api;
using Opportunity.Core.Redactions;
using Opportunity.Core.Security;
using Opportunity.Data.Redactions;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Redactions;

/// <summary>
/// Non-destructive redactions (E11-T04, ADR-012) under <c>/api/v1/workspaces/{workspaceId}</c>:
/// <list type="bullet">
/// <item>Redaction Sets (<c>/redaction-sets</c>) and the reason picklist (<c>/redaction-reasons</c>): every member reads
/// them; <c>Workspace.ManageFields</c> creates and changes them (If-Match; audited). Sets are retired, reasons
/// deactivated, never deleted.</item>
/// <item><c>GET …/documents/{documentId}/redaction-sets/{redactionSetId}</c>: a document's redactions in a set with the
/// version as ETag (<c>?version=</c> reads an earlier one); <c>POST …/revisions</c> saves changes as one new version with
/// If-Match (412 with the current redactions when stale); <c>GET …/history</c> lists every revision, removed redactions
/// included.</item>
/// </list>
/// The document is authorized per request by the PDP, so a hidden or unknown document answers the content gateway's 404.
/// </summary>
public sealed class RedactionEndpoints : IApiEndpointModule
{
    public const string SetsPath = "/redaction-sets";
    public const string ReasonsPath = "/redaction-reasons";
    public const string DocumentPath = "/documents/{documentId}/redaction-sets/{redactionSetId}";

    private const string Tag = "Redactions";
    private const string DocumentNotFound = ProtectedContentGateway.DocumentNotFoundDetail;
    private const string ReasonNotFound = "No such redaction reason.";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(SetsPath, ListSetsAsync)
            .WithName("ListRedactionSets")
            .WithTags(Tag)
            .WithSummary("The workspace's Redaction Sets, active ones first (a new workspace starts with one set).")
            .Produces<RedactionSetList>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireWorkspaceMember();

        ws.MapPost(SetsPath, CreateSetAsync)
            .WithName("CreateRedactionSet")
            .WithTags(Tag)
            .WithSummary("Create a Redaction Set (Workspace.ManageFields; audited).")
            .Produces<RedactionSetResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapPut(SetsPath + "/{redactionSetId}", UpdateSetAsync)
            .WithName("UpdateRedactionSet")
            .WithTags(Tag)
            .WithSummary("Rename, describe, retire or reactivate a Redaction Set (If-Match required; Workspace.ManageFields; audited).")
            .Produces<RedactionSetResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapGet(ReasonsPath, ListReasonsAsync)
            .WithName("ListRedactionReasons")
            .WithTags(Tag)
            .WithSummary("The redaction reason picklist in display order: privilege, privacy and other reasons with their box labels.")
            .Produces<RedactionReasonList>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireWorkspaceMember();

        ws.MapPost(ReasonsPath, CreateReasonAsync)
            .WithName("CreateRedactionReason")
            .WithTags(Tag)
            .WithSummary("Add a redaction reason (Workspace.ManageFields; audited). The code is permanent.")
            .Produces<RedactionReasonResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapPut(ReasonsPath + "/{reasonCode}", UpdateReasonAsync)
            .WithName("UpdateRedactionReason")
            .WithTags(Tag)
            .WithSummary("Change a reason's name, category, box label, order or active flag (If-Match required; Workspace.ManageFields; audited).")
            .Produces<RedactionReasonResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.WorkspaceManageFields);

        ws.MapGet(DocumentPath, GetDocumentAsync)
            .WithName("GetDocumentRedactions")
            .WithTags(Tag)
            .WithSummary("A document's redactions in a Redaction Set, with the version as ETag (Document.View).")
            .WithDescription(
                "Rectangles are in normalized page coordinates (millionths of the page width and height at rotation 0) and apply "
                + "to every image of the page at any resolution. version reads the redactions as of an earlier version. redactable "
                + "is false, with unavailableReason, while the document has no rendered images.")
            .Produces<DocumentRedactionsResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.DocumentView);

        ws.MapPost(DocumentPath + "/revisions", SaveAsync)
            .WithName("SaveDocumentRedactions")
            .WithTags(Tag)
            .WithSummary("Add, modify and remove redactions as one new version (If-Match: the ETag read; Redaction.Apply; audited).")
            .WithDescription(
                "All changes apply together or none does. 412 version-conflict when another save came first: the problem carries "
                + "currentVersion, lastChange and current (the redactions now), and nothing is written. Removing a redaction, or "
                + "changing another user's, also needs Redaction.Remove. 409 redaction-requires-images when the document has no "
                + "rendered page images. Removed redactions stay in the history.")
            .Produces<DocumentRedactionsResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .RequirePermission(Permission.RedactionApply);

        ws.MapGet(DocumentPath + "/history", HistoryAsync)
            .WithName("GetDocumentRedactionHistory")
            .WithTags(Tag)
            .WithSummary("Every revision of a document's redactions in a set, newest first, removed redactions included (Document.View).")
            .Produces<RedactionHistoryResource>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permission.DocumentView);
    }

    // ── Sets ─────────────────────────────────────────────────────────────────────────────────────────────────────

    internal static async Task<IResult> ListSetsAsync(string workspaceId, HttpContext context, RedactionService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var sets = await service.ListSetsAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new RedactionSetList([.. sets.Select(ToResource)]));
    }

    internal static async Task<IResult> CreateSetAsync(
        string workspaceId, RedactionSetRequest request, HttpContext context, RedactionService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.CreateSetAsync(access.Principal, access.WorkspaceId, request?.Name, request?.Description, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Status != RedactionOutcomeStatus.Created)
        {
            return Problem(outcome.Status, outcome.Errors, outcome.Detail);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Value!.Version);
        return TypedResults.Created(
            $"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{SetsPath}/{outcome.Value.RedactionSetId}", ToResource(outcome.Value));
    }

    internal static async Task<IResult> UpdateSetAsync(
        string workspaceId, string redactionSetId, RedactionSetRequest request, HttpContext context, RedactionService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryParseId(redactionSetId, out var id)
            || await service.GetSetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound(RedactionService.SetNotFound);
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await service.UpdateSetAsync(access.Principal, access.WorkspaceId, current, current.Version, request?.Name, request?.Description,
            request?.Retired ?? false, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != RedactionOutcomeStatus.Ok)
        {
            return Problem(outcome.Status, outcome.Errors, outcome.Detail);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Value!.Version);
        return TypedResults.Ok(ToResource(outcome.Value));
    }

    // ── Reasons ──────────────────────────────────────────────────────────────────────────────────────────────────

    internal static async Task<IResult> ListReasonsAsync(string workspaceId, HttpContext context, RedactionService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var reasons = await service.ListReasonsAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new RedactionReasonList([.. reasons.Select(ToResource)]));
    }

    internal static async Task<IResult> CreateReasonAsync(
        string workspaceId, RedactionReasonRequest request, HttpContext context, RedactionService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (request is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["Send a redaction reason."] });
        }

        var outcome = await service.CreateReasonAsync(access.Principal, access.WorkspaceId, request.Code, request.Name, Category(request.Category),
            request.BoxLabel, request.Active, request.SortOrder, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != RedactionOutcomeStatus.Created)
        {
            return Problem(outcome.Status, outcome.Errors, outcome.Detail);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Value!.Version);
        return TypedResults.Created(
            $"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{ReasonsPath}/{Uri.EscapeDataString(outcome.Value.Code)}", ToResource(outcome.Value));
    }

    internal static async Task<IResult> UpdateReasonAsync(
        string workspaceId, string reasonCode, RedactionReasonRequest request, HttpContext context, RedactionService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound(ReasonNotFound);
        }

        var reasons = await service.ListReasonsAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (reasons.FirstOrDefault(r => string.Equals(r.Code, reasonCode, StringComparison.Ordinal)) is not { } current)
        {
            return Problems.NotFound(ReasonNotFound);
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        if (request is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["Send a redaction reason."] });
        }

        var outcome = await service.UpdateReasonAsync(access.Principal, access.WorkspaceId, current, current.Version, request.Name, Category(request.Category),
            request.BoxLabel, request.Active, request.SortOrder, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != RedactionOutcomeStatus.Ok)
        {
            return Problem(outcome.Status, outcome.Errors, outcome.Detail);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Value!.Version);
        return TypedResults.Ok(ToResource(outcome.Value));
    }

    // ── Document redactions ──────────────────────────────────────────────────────────────────────────────────────

    internal static async Task<IResult> GetDocumentAsync(
        string workspaceId, string documentId, string redactionSetId, [FromQuery] long? version, HttpContext context, RedactionService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryParseId(documentId, out var doc))
        {
            return Problems.NotFound(DocumentNotFound);
        }

        var parsedSet = TryParseId(redactionSetId, out var set);
        var outcome = await service.GetDocumentAsync(access.Principal, access.WorkspaceId, doc, parsedSet ? set : Guid.Empty, version, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Status != RedactionOutcomeStatus.Ok)
        {
            return Problem(outcome.Status, outcome.Errors, outcome.Detail);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Value!.CurrentVersion);
        return TypedResults.Ok(ToResource(outcome.Value));
    }

    internal static async Task<IResult> HistoryAsync(
        string workspaceId, string documentId, string redactionSetId, HttpContext context, RedactionService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryParseId(documentId, out var doc))
        {
            return Problems.NotFound(DocumentNotFound);
        }

        var parsedSet = TryParseId(redactionSetId, out var set);
        var outcome = await service.HistoryAsync(access.Principal, access.WorkspaceId, doc, parsedSet ? set : Guid.Empty, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != RedactionOutcomeStatus.Ok)
        {
            return Problem(outcome.Status, outcome.Errors, outcome.Detail);
        }

        var items = outcome.Value!;
        var truncated = items.Count > RedactionService.MaxHistory;
        var current = items.Count > 0 ? items[0].Version : 0;
        return TypedResults.Ok(new RedactionHistoryResource(doc, set, current, truncated,
            [.. items.Take(RedactionService.MaxHistory).Select(r => new RedactionRevisionResource(
                r.Version, r.RedactionId, Operation(r.Operation), r.PageSetId, r.Ordinal, Rect(r.Rect), Type(r.Type), r.ReasonCode, r.Note,
                Actor(r.Actor), r.At))]));
    }

    internal static async Task<IResult> SaveAsync(
        string workspaceId,
        string documentId,
        string redactionSetId,
        SaveRedactionsRequest request,
        HttpContext context,
        RedactionService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryParseId(documentId, out var doc))
        {
            return Problems.NotFound(DocumentNotFound);
        }

        var ifMatch = context.Request.Headers.IfMatch;
        if (StringValues.IsNullOrEmpty(ifMatch))
        {
            return Problems.Create(StatusCodes.Status428PreconditionRequired, ProblemCodes.PreconditionRequired,
                "Redactions are versioned; send If-Match with the ETag of the redactions you read.");
        }

        var parsedSet = TryParseId(redactionSetId, out var set);
        var changes = request?.Changes ?? [];
        var outcome = await service.SaveAsync(access.Principal, access.WorkspaceId, doc, parsedSet ? set : Guid.Empty, ExpectedVersion(ifMatch),
            [.. changes.Select(c => c is null
                ? new RedactionChange((RedactionOperation)0, null, null, null, null, null, null)
                : new RedactionChange(Operation(c.Operation), c.RedactionId, c.PageNumber, c.Rect is { } r ? new NormalizedRect(r.X, r.Y, r.W, r.H) : null,
                    c.Type is { } t ? Type(t) : null, c.ReasonCode, c.Note))],
            cancellationToken).ConfigureAwait(false);

        switch (outcome.Status)
        {
            case RedactionOutcomeStatus.Ok:
                context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Value!.CurrentVersion);
                return TypedResults.Ok(ToResource(outcome.Value));
            case RedactionOutcomeStatus.VersionConflict:
                {
                    var current = ToResource(outcome.Value!);
                    context.Response.Headers.ETag = EntityTags.ForVersion(current.CurrentVersion);
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status412PreconditionFailed,
                        detail: "Another user changed this document's redactions since you read them. Nothing was saved; refresh and redo your change.",
                        type: ProblemCodes.TypeFor(ProblemCodes.VersionConflict),
                        extensions: new Dictionary<string, object?>
                        {
                            [Problems.CodeExtension] = ProblemCodes.VersionConflict,
                            ["currentVersion"] = current.CurrentVersion,
                            ["lastChange"] = current.LastChange,
                            ["current"] = current,
                        });
                }

            default:
                return Problem(outcome.Status, outcome.Errors, outcome.Detail);
        }
    }

    /// <summary>
    /// The version an <c>If-Match</c> names: null for <c>*</c>; -1 (never current) when no single strong numeric tag
    /// is given, so the save fails with 412 and the current state. Version 0 is the state before the first save.
    /// </summary>
    internal static long? ExpectedVersion(StringValues ifMatch)
    {
        if (!EntityTagHeaderValue.TryParseList(ifMatch, out var tags))
        {
            return -1;
        }

        if (tags.Any(t => t.Equals(EntityTagHeaderValue.Any)))
        {
            return null;
        }

        var versions = tags
            .Where(t => !t.IsWeak)
            .Select(t => long.TryParse(t.Tag.AsSpan().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1)
            .Where(v => v >= 0)
            .Distinct()
            .ToList();
        return versions is [var single] ? single : -1;
    }

    // ── Mapping ──────────────────────────────────────────────────────────────────────────────────────────────────

    internal static DocumentRedactionsResource ToResource(DocumentRedactionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var redactable = state.Redactable;
        return new DocumentRedactionsResource(
            state.DocumentId,
            state.Set.RedactionSetId,
            state.Version,
            state.CurrentVersion,
            state.ActivePageSetId,
            redactable,
            redactable ? null : RedactionService.RequiresRenderedImages,
            state.Set.Retired,
            state.LastChange is { } c ? new RedactionChangeResource(Actor(c.Actor), c.At, c.Version) : null,
            [.. state.Redactions.Select(r => new RedactionResource(
                r.RedactionId,
                r.PageSetId,
                r.Ordinal,
                r.PageSetId == state.ActivePageSetId,
                Rect(r.Rect),
                Type(r.Type),
                r.ReasonCode,
                state.Reasons.TryGetValue(r.ReasonCode, out var reason) ? reason.Name : r.ReasonCode,
                Category(reason?.Category ?? RedactionReasonCategory.Other),
                r.Note,
                Actor(r.CreatedBy),
                r.CreatedAt,
                Actor(r.ModifiedBy),
                r.ModifiedAt,
                r.ChangedAtVersion))]);
    }

    internal static RedactionSetResource ToResource(RedactionSetRecord set) =>
        new(set.RedactionSetId, set.Name, set.Description, set.Retired, set.ModifiedBy is { } a ? Actor(a) : null, set.ModifiedAt, set.Version);

    internal static RedactionReasonResource ToResource(RedactionReasonRecord reason) =>
        new(reason.Code, reason.Name, Category(reason.Category), reason.BoxLabel, reason.Active, reason.SortOrder, reason.Version);

    private static RedactionActorResource Actor(RedactionActor actor) => new(actor.UserId, actor.DisplayName);

    private static RedactionRectResource Rect(NormalizedRect rect) => new(rect.X, rect.Y, rect.W, rect.H);

    private static RedactionTypeResource Type(RedactionType type) => type == RedactionType.Labelled ? RedactionTypeResource.Labelled : RedactionTypeResource.Black;

    private static RedactionType Type(RedactionTypeResource type) => type == RedactionTypeResource.Labelled ? RedactionType.Labelled : RedactionType.Black;

    private static RedactionOperationResource Operation(RedactionOperation operation) => operation switch
    {
        RedactionOperation.Add => RedactionOperationResource.Add,
        RedactionOperation.Modify => RedactionOperationResource.Modify,
        _ => RedactionOperationResource.Remove,
    };

    private static RedactionOperation Operation(RedactionOperationResource operation) => operation switch
    {
        RedactionOperationResource.Add => RedactionOperation.Add,
        RedactionOperationResource.Modify => RedactionOperation.Modify,
        RedactionOperationResource.Remove => RedactionOperation.Remove,
        _ => 0,
    };

    private static RedactionReasonCategoryResource Category(RedactionReasonCategory category) => category switch
    {
        RedactionReasonCategory.Privilege => RedactionReasonCategoryResource.Privilege,
        RedactionReasonCategory.Privacy => RedactionReasonCategoryResource.Privacy,
        _ => RedactionReasonCategoryResource.Other,
    };

    private static RedactionReasonCategory? Category(RedactionReasonCategoryResource? category) => category switch
    {
        RedactionReasonCategoryResource.Privilege => RedactionReasonCategory.Privilege,
        RedactionReasonCategoryResource.Privacy => RedactionReasonCategory.Privacy,
        RedactionReasonCategoryResource.Other => RedactionReasonCategory.Other,
        _ => null,
    };

    private static IResult Problem(RedactionOutcomeStatus status, IReadOnlyDictionary<string, string[]> errors, string? detail) => status switch
    {
        RedactionOutcomeStatus.Invalid => Problems.Validation(errors.ToDictionary()),
        RedactionOutcomeStatus.Forbidden => Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden,
            detail ?? "You do not have permission for this operation."),
        RedactionOutcomeStatus.VersionConflict => Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict,
            "The resource was modified since it was read."),
        RedactionOutcomeStatus.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, detail),
        RedactionOutcomeStatus.RequiresImages => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.RedactionRequiresImages,
            RedactionService.RequiresRenderedImages + "."),
        _ => Problems.NotFound(detail ?? DocumentNotFound),
    };

    private static bool TryParseId(string value, out Guid id) => Guid.TryParse(value, out id) && id != Guid.Empty;
}

public static class RedactionEndpointRegistration
{
    /// <summary>The redaction endpoints, use case and PostgreSQL store.</summary>
    public static IServiceCollection AddRedactionEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresRedactionStore();
        services.TryAddScoped<RedactionService>();
        services.AddSingleton<IApiEndpointModule, RedactionEndpoints>();
        return services;
    }
}
