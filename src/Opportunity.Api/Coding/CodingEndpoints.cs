using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

using Npgsql;

using Opportunity.Api.Content;
using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.SearchWork;
using Opportunity.Contracts.Api;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Data.Coding;
using Opportunity.Data.Fields;
using Opportunity.Data.SearchWork;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Coding;

/// <summary>
/// Interactive coding (E10-T01): <c>GET …/documents/{documentId}/coding</c> reads the document's coding (by layout when
/// <c>layoutId</c> is given) with its <c>ETag</c> (DocumentVersion) and indexing state; <c>PUT</c> saves the changes of
/// one save with <c>If-Match</c> (412 with the current coding and its last editor when stale) and an optional
/// <c>Idempotency-Key</c>. PEP-1 checks the workspace permission (Document.View / Coding.Write); the use case asks the
/// PDP about the document itself, so a hidden or unknown document answers the same 404.
/// </summary>
public sealed class CodingEndpoints : IApiEndpointModule
{
    public const string Path = "/documents/{documentId}/coding";

    /// <summary>The <c>reason</c> of the 409 a save answers when it would hide the document from its author (E16-T08).</summary>
    public const string RemovesOwnAccess = "removes-own-access";

    private const string Tag = "Coding";
    // The document 404 of every document route (the content gateway's), so coding and content answer an unknown, hidden or
    // malformed document identically.
    private const string NotFoundDetail = ProtectedContentGateway.DocumentNotFoundDetail;

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(Path, GetAsync)
            .WithName("GetDocumentCoding")
            .WithTags(Tag)
            .WithSummary("A document's current coding, by layout, with its version (ETag) and indexing state.")
            .WithDescription(
                "Lists the coding fields of the layout (every coding field without layoutId) that you may see, with value, " +
                "editability and last change. projectedVersion reaches documentVersion when search has caught up.")
            .RequirePermission(Permission.DocumentView)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.Workspace.MapPut(Path, SaveAsync)
            .WithName("SaveDocumentCoding")
            .WithTags(Tag)
            .WithSummary("Save coding changes with optimistic concurrency (If-Match: the ETag last read).")
            .WithDescription(
                "All changes apply together or not at all, as one DocumentVersion. 412 version-conflict when the document " +
                "changed since it was read: the problem carries currentVersion, lastEditor and current (the coding now). " +
                "Security-affecting fields also need Coding.WritePrivilege. A retry with the same Idempotency-Key returns the " +
                "original result (header Idempotent-Replayed) and writes nothing. A change that would hide the document from you " +
                "answers 409 confirmation-required (reason removes-own-access) and writes nothing unless confirmAccessLoss is " +
                "true; the confirmed save answers with accessRetained false and no fields.")
            .RequirePermission(Permission.CodingWrite)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    internal static async Task<Results<Ok<DocumentCodingResource>, ValidationProblem, ProblemHttpResult>> GetAsync(
        string workspaceId,
        string documentId,
        [FromQuery] Guid? layoutId,
        HttpContext context,
        InteractiveCodingService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryParseId(documentId, out var id))
        {
            return Problems.NotFound(NotFoundDetail);
        }

        var outcome = await service.GetAsync(new CodingCaller(access.Principal, access.WorkspaceId), id, layoutId, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Status switch
        {
            CodingStatus.Ok => Ok(context, outcome.View!),
            CodingStatus.Invalid => Invalid(outcome.Errors),
            CodingStatus.Forbidden => Forbidden(),
            _ => Problems.NotFound(NotFoundDetail),
        };
    }

    internal static async Task<Results<Ok<DocumentCodingResource>, ValidationProblem, ProblemHttpResult>> SaveAsync(
        string workspaceId,
        string documentId,
        UpdateDocumentCodingRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromHeader(Name = IdempotencyMiddleware.HeaderName)] string? idempotencyKey,
        HttpContext context,
        InteractiveCodingService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !TryParseId(documentId, out var id))
        {
            return Problems.NotFound(NotFoundDetail);
        }

        if (string.IsNullOrEmpty(ifMatch))
        {
            return Problems.Create(StatusCodes.Status428PreconditionRequired, ProblemCodes.PreconditionRequired,
                "Coding is versioned; send If-Match with the ETag of the coding you read.");
        }

        if (idempotencyKey is not null && !IsValidIdempotencyKey(idempotencyKey))
        {
            return Problems.Create(StatusCodes.Status400BadRequest, ProblemCodes.IdempotencyKeyInvalid,
                $"{IdempotencyMiddleware.HeaderName} must be a single value of 1–{IdempotencyMiddleware.MaxKeyLength} printable ASCII characters.");
        }

        if (request?.Changes is not { Count: > 0 } changes)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["changes"] = ["At least one change is required."] });
        }

        var outcome = await service.SaveAsync(
            new CodingCaller(access.Principal, access.WorkspaceId),
            new InteractiveCodingRequest(
                id,
                ExpectedVersion(context.Request.Headers.IfMatch),
                [.. changes.Select(c => new CodingChange(c.FieldId, Operation(c.Operation), c.Value?.DeepClone()))],
                request.LayoutId,
                idempotencyKey,
                request.ConfirmAccessLoss),
            cancellationToken).ConfigureAwait(false);

        switch (outcome.Status)
        {
            case CodingStatus.Ok:
                return Ok(context, outcome.View!, outcome.AccessRetained);
            case CodingStatus.Replayed:
                context.Response.Headers[IdempotencyMiddleware.ReplayedHeaderName] = "true";
                return Ok(context, outcome.View!, outcome.AccessRetained);
            case CodingStatus.Invalid:
                return Invalid(outcome.Errors);
            case CodingStatus.Forbidden:
                return Forbidden();
            case CodingStatus.VersionConflict:
                return Conflict(context, outcome.View!);
            case CodingStatus.AccessLossUnconfirmed:
                return AccessLossUnconfirmed();
            case CodingStatus.IdempotencyKeyReuse:
                return Problems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.IdempotencyKeyReuse,
                    $"This {IdempotencyMiddleware.HeaderName} was already used with a different coding request.");
            default:
                return Problems.NotFound(NotFoundDetail);
        }
    }

    /// <summary>
    /// The version an <c>If-Match</c> names: null for <c>*</c>; -1 (never current) when no single strong numeric tag is
    /// given, so the save fails with 412 and the current state.
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
            .Select(t => long.TryParse(t.Tag.AsSpan().Trim('"'), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1)
            .Where(v => v >= 1)
            .Distinct()
            .ToList();
        return versions is [var single] ? single : -1;
    }

    internal static DocumentCodingResource ToResource(DocumentCodingView view, bool accessRetained = true) => new(
        view.DocumentId,
        view.DocumentVersion,
        view.ProjectedVersion,
        view.IndexingState switch
        {
            CodingIndexingState.Indexed => CodingIndexingStateResource.Indexed,
            CodingIndexingState.Failed => CodingIndexingStateResource.Failed,
            _ => CodingIndexingStateResource.Pending,
        },
        view.LayoutId,
        [.. view.Fields.Select(f => new CodingFieldValueResource(
            f.Field.FieldId, f.Value?.DeepClone(), f.Editable, f.Field.IsSecurityAffecting, f.ChangedAtVersion, f.ChangedBy, f.ChangedAt))],
        view.LastEditor is { } e ? new CodingEditorResource(e.UserId, e.DisplayName, e.At, e.DocumentVersion, e.JobId) : null,
        accessRetained);

    private static Ok<DocumentCodingResource> Ok(HttpContext context, DocumentCodingView view, bool accessRetained = true)
    {
        context.Response.Headers.ETag = EntityTags.ForVersion(view.DocumentVersion);
        return TypedResults.Ok(ToResource(view, accessRetained));
    }

    /// <summary>E16-T08: like giving up one's own role (E05-T08), losing one's own access to a document needs a confirmation.</summary>
    private static ProblemHttpResult AccessLossUnconfirmed() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            detail: "This change would remove your own access to the document. Confirm it with confirmAccessLoss.",
            type: ProblemCodes.TypeFor(ProblemCodes.ConfirmationRequired),
            extensions: new Dictionary<string, object?>
            {
                [Problems.CodeExtension] = ProblemCodes.ConfirmationRequired,
                ["reason"] = RemovesOwnAccess,
            });

    private static ProblemHttpResult Conflict(HttpContext context, DocumentCodingView current)
    {
        context.Response.Headers.ETag = EntityTags.ForVersion(current.DocumentVersion);
        var resource = ToResource(current);
        return TypedResults.Problem(
            statusCode: StatusCodes.Status412PreconditionFailed,
            detail: "The document's coding changed since you read it. Review the current values and save again.",
            type: ProblemCodes.TypeFor(ProblemCodes.VersionConflict),
            extensions: new Dictionary<string, object?>
            {
                [Problems.CodeExtension] = ProblemCodes.VersionConflict,
                ["currentVersion"] = current.DocumentVersion,
                ["lastEditor"] = resource.LastEditor,
                ["current"] = resource,
            });
    }

    private static ProblemHttpResult Forbidden() =>
        Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation.");

    private static ValidationProblem Invalid(IReadOnlyList<FieldError> errors) =>
        TypedResults.ValidationProblem(
            errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()),
            detail: "The coding was not saved.",
            type: ProblemCodes.TypeFor(ProblemCodes.Validation),
            extensions: new Dictionary<string, object?>
            {
                [Problems.CodeExtension] = ProblemCodes.Validation,
                ["fieldErrors"] = errors.Select(e => new { field = e.Field, code = e.Code, message = e.Message }).ToArray(),
            });

    private static CodingOperationKind Operation(CodingOperationResource operation) => operation switch
    {
        CodingOperationResource.AddChoices => CodingOperationKind.AddChoices,
        CodingOperationResource.RemoveChoices => CodingOperationKind.RemoveChoices,
        _ => CodingOperationKind.Set,
    };

    private static bool IsValidIdempotencyKey(string key) =>
        key.Length is > 0 and <= IdempotencyMiddleware.MaxKeyLength && key.All(c => c is >= '\x21' and <= '\x7e');

    private static bool TryParseId(string value, out Guid id) => Guid.TryParse(value, out id) && id != Guid.Empty;
}

public static class CodingEndpointRegistration
{
    /// <summary>
    /// The interactive coding API: the PostgreSQL coding store (with the restriction-class binding, none until E05-T06
    /// registers one), the search outbox for indexing state, the field catalogue and the use case.
    /// </summary>
    public static IServiceCollection AddCodingEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRestrictionClassBinding>(NoRestrictionClassBinding.Instance);
        services.TryAddSingleton<ICodingRepository>(sp => new CodingRepository(
            sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IRestrictionClassBinding>()));
        services.TryAddSingleton<ISearchOutboxRepository>(sp => new SearchOutboxRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IFieldCatalogRepository, FieldCatalogRepository>();
        services.TryAddSingleton<IFieldAccessFilter, UnrestrictedFieldAccess>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<InteractiveCodingService>();
        services.AddSingleton<IApiEndpointModule, CodingEndpoints>();
        return services;
    }
}
