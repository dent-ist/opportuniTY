using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Api.Content;
using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Api.Jobs;
using Opportunity.Application.Coding;
using Opportunity.Application.Documents;
using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Data.Coding;
using Opportunity.Data.Relationships;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Coding;

/// <summary>
/// Family, duplicate and thread relationships and coding propagation (E09-T05, wave-12 contract):
/// <c>GET …/documents/{documentId}/relationships</c> lists what the caller may see of the document's family, duplicates
/// and email thread; <c>POST …/coding-propagations/preview</c> shows what copying the document's values of some coding
/// fields to its family and/or duplicates would do; <c>POST …/coding-propagations</c> applies a preview (Q-14: one
/// write up to the threshold, a bulk coding job above it).
/// </summary>
public sealed class CodingPropagationEndpoints : IApiEndpointModule
{
    public const string RelationshipsPath = "/documents/{documentId}/relationships";
    public const string Path = "/coding-propagations";

    private const string Tag = "Coding";
    private const string NotFoundDetail = ProtectedContentGateway.DocumentNotFoundDetail;

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(RelationshipsPath, RelationshipsAsync)
            .WithName("GetDocumentRelationships")
            .WithTags(Tag)
            .WithSummary("A document's family, duplicates and email thread, as far as you may see them.")
            .WithDescription(
                "Members you may not see are never listed; those hidden by a restriction class only add to restrictedCount. " +
                "fields=a,b (query names, at most 20 coding fields) adds each member's current values (choices by name). " +
                "The thread lists its first 200 visible members; total counts them all.")
            .RequirePermission(Permission.DocumentView)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.Workspace.MapPost(Path + "/preview", PreviewAsync)
            .WithName("PreviewCodingPropagation")
            .WithTags(Tag)
            .WithSummary("Preview copying a document's coding of some fields to its family and/or duplicates.")
            .WithDescription(
                "Reads the source document's current values of fields and compares them with every related document you may " +
                "code: targetCount, conflicts (targets already coded differently; the first 100) and mode (job above the " +
                "threshold, Q-14). Privilege, confidentiality and wall fields need Coding.WritePrivilege. Apply the preview " +
                "within 10 minutes with POST …/coding-propagations.")
            .RequirePermission(Permission.CodingWrite)
            .Produces<CodingPropagationPreviewResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        routes.Workspace.MapPost(Path, ApplyAsync)
            .WithName("ApplyCodingPropagation")
            .WithTags(Tag)
            .WithSummary("Apply a coding propagation preview: 200 when applied at once, 202 with the job above the threshold.")
            .WithDescription(
                "Writes exactly what the preview showed. 409 PREVIEW_STALE when the preview is older than 10 minutes or the " +
                "source's coding of its fields changed since. Interactive: targets changed since the preview are skipped. " +
                "Job: a bulk coding job over a frozen set of the targets (Q-07 skip rule); follow it at Location and its " +
                "outcomes at …/bulk-coding/{jobId}/report. Every change references the source's originating coding event.")
            .RequirePermission(Permission.CodingWrite)
            .RequireIdempotencyKey()
            .Produces<CodingPropagationResultResource>()
            .Produces<CodingPropagationResultResource>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<Results<Ok<DocumentRelationshipsResource>, ValidationProblem, ProblemHttpResult>> RelationshipsAsync(
        string workspaceId,
        string documentId,
        [FromQuery(Name = "fields")] string? fields,
        HttpContext context,
        DocumentRelationshipService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(documentId, out var id) || id == Guid.Empty)
        {
            return Problems.NotFound(NotFoundDetail);
        }

        var names = (fields ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var outcome = await service.GetAsync(access.Principal, access.WorkspaceId, id, names, cancellationToken).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case RelationshipsStatus.Ok:
                var view = outcome.View!;
                return TypedResults.Ok(new DocumentRelationshipsResource(
                    view.DocumentId,
                    new RelatedFamilyResource(view.FamilyId, view.FamilyParent is { } p ? ToResource(p) : null,
                        [.. view.FamilyMembers.Select(ToResource)], view.FamilyRestricted),
                    new RelatedDuplicatesResource(view.DuplicateGroupId, view.PrimaryDocumentId,
                        [.. view.DuplicateMembers.Select(ToResource)], view.DuplicatesRestricted),
                    new RelatedThreadResource(view.EmailThreadId, [.. view.ThreadMembers.Select(ToResource)], view.ThreadTotal, view.ThreadRestricted)));
            case RelationshipsStatus.Invalid:
                return Problems.Validation(Errors(outcome.Errors ?? []));
            case RelationshipsStatus.Forbidden:
                return Forbidden();
            default:
                return Problems.NotFound(NotFoundDetail);
        }
    }

    internal static async Task<Results<Ok<CodingPropagationPreviewResource>, ValidationProblem, ProblemHttpResult>> PreviewAsync(
        string workspaceId,
        CodingPropagationPreviewRequest request,
        HttpContext context,
        CodingPropagationService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (request is null || request.SourceDocumentId == Guid.Empty)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["sourceDocumentId"] = ["A source document is required."] });
        }

        if (request.Fields is not { Count: > 0 } fieldIds)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["fields"] = ["At least one coding field is required."] });
        }

        if (!Enum.IsDefined(request.Scope))
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["scope"] = ["scope must be family, duplicates or familyAndDuplicates."] });
        }

        var outcome = await service.PreviewAsync(
            new CodingCaller(access.Principal, access.WorkspaceId), request.SourceDocumentId, Scope(request.Scope), fieldIds, cancellationToken)
            .ConfigureAwait(false);
        switch (outcome.Status)
        {
            case CodingPropagationStatus.Ok:
                var preview = outcome.Preview!;
                var catalog = outcome.Catalog!;
                return TypedResults.Ok(new CodingPropagationPreviewResource(
                    preview.PreviewId,
                    outcome.TargetCount,
                    outcome.ConflictCount,
                    [.. outcome.Conflicts.Select(c => new CodingPropagationConflictResource(
                        c.DocumentId,
                        c.ControlNumber,
                        c.Field.FieldId,
                        CodingValueText.Format(c.Field, c.Current, catalog.ChoicesOf(c.Field.FieldId)),
                        CodingValueText.Format(c.Field, c.New, catalog.ChoicesOf(c.Field.FieldId))))],
                    outcome.RestrictedCount,
                    outcome.SkippedCount,
                    Mode(preview.Mode),
                    preview.Threshold));
            case CodingPropagationStatus.Invalid:
                return Problems.Validation(Errors(outcome.Errors));
            case CodingPropagationStatus.TooLarge:
                return Problems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.UnprocessableContent,
                    string.Join(" ", outcome.Errors.Select(e => e.Message)));
            case CodingPropagationStatus.Forbidden:
                return Forbidden();
            default:
                return Problems.NotFound(NotFoundDetail);
        }
    }

    internal static async Task<Results<Ok<CodingPropagationResultResource>, Accepted<CodingPropagationResultResource>, ValidationProblem, ProblemHttpResult>> ApplyAsync(
        string workspaceId,
        CodingPropagationApplyRequest request,
        HttpContext context,
        CodingPropagationService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (request is null || request.PreviewId == Guid.Empty)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["previewId"] = ["A preview is required."] });
        }

        var outcome = await service.ApplyAsync(new CodingCaller(access.Principal, access.WorkspaceId), request.PreviewId, cancellationToken)
            .ConfigureAwait(false);
        switch (outcome.Status)
        {
            case CodingPropagationStatus.Ok when outcome.Mode == CodingPropagationMode.Job:
                var job = outcome.Job!;
                return TypedResults.Accepted(
                    $"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}/jobs/{job.JobId}",
                    new CodingPropagationResultResource(CodingPropagationModeResource.Job, Job: JobEndpoints.ToResource(job)));
            case CodingPropagationStatus.Ok:
                return TypedResults.Ok(new CodingPropagationResultResource(CodingPropagationModeResource.Interactive, outcome.Applied, outcome.Skipped));
            case CodingPropagationStatus.Stale:
                return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.PreviewStale,
                    "The preview is out of date (older than 10 minutes, or the source document's coding changed). Preview again.");
            case CodingPropagationStatus.Invalid:
                return Problems.Validation(Errors(outcome.Errors));
            case CodingPropagationStatus.Forbidden:
                return Forbidden();
            default:
                return Problems.NotFound("No such propagation preview.");
        }
    }

    private static RelatedDocumentResource ToResource(RelatedDocument d) => new(
        d.Row.DocumentId,
        d.Row.ControlNumber,
        d.Row.FileName,
        d.Row.DocumentDate,
        d.Row.FamilySequence,
        d.IsParent,
        d.Row.IsDuplicatePrimary,
        d.IsSelf,
        d.Coding);

    private static Dictionary<string, string[]> Errors(IEnumerable<FieldError> errors) =>
        errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());

    private static ProblemHttpResult Forbidden() =>
        Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation.");

    private static CodingPropagationScope Scope(CodingPropagationScopeResource scope) => scope switch
    {
        CodingPropagationScopeResource.Duplicates => CodingPropagationScope.Duplicates,
        CodingPropagationScopeResource.FamilyAndDuplicates => CodingPropagationScope.FamilyAndDuplicates,
        _ => CodingPropagationScope.Family,
    };

    private static CodingPropagationModeResource Mode(CodingPropagationMode mode) =>
        mode == CodingPropagationMode.Job ? CodingPropagationModeResource.Job : CodingPropagationModeResource.Interactive;
}

public static class CodingPropagationEndpointRegistration
{
    /// <summary>
    /// Relationships and coding propagation (needs <see cref="CodingEndpointRegistration.AddCodingEndpoints"/>,
    /// <see cref="BulkCodingEndpointRegistration.AddBulkCodingEndpoints"/> and the snapshot endpoints).
    /// </summary>
    public static IServiceCollection AddCodingPropagationEndpoints(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new CodingPropagationOptions();
        configuration.GetSection(CodingPropagationOptions.SectionName).Bind(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton<ICodingPropagationRepository>(sp => new CodingPropagationRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IDocumentRelationshipViewReader>(sp => new DocumentRelationshipViewReader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddScoped<CodingPropagationService>();
        services.TryAddScoped<DocumentRelationshipService>();
        services.AddSingleton<IApiEndpointModule, CodingPropagationEndpoints>();
        return services;
    }
}
