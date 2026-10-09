using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Api.Jobs;
using Opportunity.Application.Coding;
using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Coding;

/// <summary>
/// Family and duplicate privilege inconsistencies (E13-T02): <c>GET …/privilege-conflicts</c> computes the report on
/// request (<c>PrivilegeLog.Generate</c>; documents the caller may not see are neither listed nor counted, Q-52);
/// <c>POST …/privilege-conflicts/propagations</c> copies chosen members' privilege calls to the rest of their duplicate
/// groups as one bulk coding job (<c>Coding.Bulk</c> and <c>Coding.WritePrivilege</c>). The CSV form of the report is a
/// protected-content endpoint (<see cref="Content.PrivilegeConflictContentEndpoints"/>).
/// </summary>
public sealed class PrivilegeConflictEndpoints : IApiEndpointModule
{
    public const string Path = "/privilege-conflicts";

    private const string Tag = "Privilege";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(Path, ReportAsync)
            .WithName("GetPrivilegeConflicts")
            .WithTags(Tag)
            .WithSummary("Families and duplicate groups with inconsistent privilege calls, with each member's values and who set them.")
            .WithDescription(
                "Families: a member coded Privilege Status = Withhold next to members that are not (Q-14), and, with responsivenessField " +
                "(a single-choice coding field id), members with different responsiveness calls. Duplicates: members whose Privilege Status " +
                "differs, at least one being other than Not Privileged. Only documents you may see are listed or counted; a group appears " +
                "only when they alone are in conflict. productionId (needs Production.Create) keeps groups with a member in that production. " +
                "At most 1,000 groups per kind (truncated tells there are more).")
            .RequirePermission(Permission.PrivilegeLogGenerate)
            .Produces<PrivilegeConflictReportResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.Workspace.MapPost(Path + "/propagations", PropagateAsync)
            .WithName("PropagatePrivilegeCalls")
            .WithTags(Tag)
            .WithSummary("Copy chosen documents' privilege calls to the rest of their duplicate groups as one bulk coding job; 202 with the job.")
            .WithDescription(
                "Each group names the member whose values of fields (privilege field ids; default Privilege Status and Basis) the group's " +
                "other members you may code get. The targets are frozen at once; each chunk re-checks your access, leaves fields someone " +
                "else changed after the freeze alone (Q-07) and references the source's originating coding event (provenance). Needs " +
                "Coding.WritePrivilege. Follow the job at Location and its outcomes at …/bulk-coding/{jobId}/report.")
            .RequirePermission(Permission.CodingBulk)
            .RequireIdempotencyKey()
            .Produces<JobResource>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    internal static async Task<Results<Ok<PrivilegeConflictReportResource>, ValidationProblem, ProblemHttpResult>> ReportAsync(
        string workspaceId,
        [FromQuery(Name = "responsivenessField")] int? responsivenessField,
        [FromQuery(Name = "productionId")] Guid? productionId,
        HttpContext context,
        PrivilegeConflictService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.ReportAsync(access.Principal, access.WorkspaceId, responsivenessField, productionId, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Status switch
        {
            PrivilegeConflictStatus.Ok => TypedResults.Ok(ToResource(outcome.Report!)),
            PrivilegeConflictStatus.Invalid => Problems.Validation(Errors(outcome.Errors)),
            PrivilegeConflictStatus.Forbidden => Forbidden(),
            _ => Problems.NotFound("No such production."),
        };
    }

    internal static async Task<Results<Accepted<JobResource>, ValidationProblem, ProblemHttpResult>> PropagateAsync(
        string workspaceId,
        PrivilegeConflictPropagationRequest request,
        HttpContext context,
        PrivilegeConflictService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (request?.Groups is not { Count: > 0 } groups)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["groups"] = ["At least one duplicate group is required."] });
        }

        var key = context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString();
        var outcome = await service.PropagateAsync(
            new CodingCaller(access.Principal, access.WorkspaceId),
            new PrivilegePropagationRequest(
                [.. groups.Select(g => new PrivilegePropagationGroup(g?.DuplicateGroupId ?? Guid.Empty, g?.SourceDocumentId ?? Guid.Empty))],
                request.Fields ?? [],
                key),
            cancellationToken).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case PrivilegeConflictStatus.Ok:
                var job = outcome.Job!;
                return TypedResults.Accepted($"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}/jobs/{job.JobId}", JobEndpoints.ToResource(job));
            case PrivilegeConflictStatus.Invalid:
                return Problems.Validation(Errors(outcome.Errors));
            case PrivilegeConflictStatus.Forbidden:
                return Forbidden();
            case PrivilegeConflictStatus.IdempotencyKeyReuse:
                return Problems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.IdempotencyKeyReuse,
                    $"This {IdempotencyMiddleware.HeaderName} was already used for a different job.");
            default:
                return Problems.NotFound("No such document.");
        }
    }

    internal static PrivilegeConflictReportResource ToResource(PrivilegeConflictReport report)
    {
        var catalog = report.Catalog;
        PrivilegeCodedValueResource Value(int fieldId, PrivilegeCodedValue value) => new(
            PrivilegeConflictReportCsv.ChoiceNames(catalog, fieldId, value.ChoiceIds),
            value.ChoiceIds,
            value.ChangedBy is { } by ? new ReviewerResource(by, report.DisplayNames.GetValueOrDefault(by) ?? by.ToString("D")) : null,
            value.ChangedAt);

        var groups = report.Groups.Select(g => new PrivilegeConflictGroupResource(
            g.Kind == PrivilegeConflictKind.Family ? PrivilegeConflictKindResource.Family : PrivilegeConflictKindResource.Duplicates,
            g.GroupId,
            Reasons(g.Reasons),
            [.. g.Members.Select(m => new PrivilegeConflictMemberResource(
                m.DocumentId,
                m.ControlNumber,
                m.FamilySequence,
                m.IsDuplicatePrimary,
                m.InProduction,
                Value(PrivilegeFields.Status, m.Status),
                Value(PrivilegeFields.Basis, m.Basis),
                report.ResponsivenessFieldId is { } r ? Value(r, m.Responsiveness) : null))])).ToList();
        return new PrivilegeConflictReportResource(
            report.GeneratedAt,
            report.ResponsivenessFieldId,
            report.ProductionId,
            groups.Count(g => g.Kind == PrivilegeConflictKindResource.Family),
            groups.Count(g => g.Kind == PrivilegeConflictKindResource.Duplicates),
            report.Truncated,
            groups);
    }

    private static List<PrivilegeConflictReasonResource> Reasons(PrivilegeConflictReasons reasons)
    {
        var list = new List<PrivilegeConflictReasonResource>();
        if (reasons.HasFlag(PrivilegeConflictReasons.WithheldMember))
        {
            list.Add(PrivilegeConflictReasonResource.WithheldMember);
        }

        if (reasons.HasFlag(PrivilegeConflictReasons.ResponsivenessDiffers))
        {
            list.Add(PrivilegeConflictReasonResource.ResponsivenessDiffers);
        }

        if (reasons.HasFlag(PrivilegeConflictReasons.PrivilegeCallsDiffer))
        {
            list.Add(PrivilegeConflictReasonResource.PrivilegeCallsDiffer);
        }

        return list;
    }

    private static Dictionary<string, string[]> Errors(IEnumerable<FieldError> errors) =>
        errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());

    private static ProblemHttpResult Forbidden() =>
        Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation.");
}

public static class PrivilegeConflictEndpointRegistration
{
    /// <summary>The privilege conflict report and propagation (needs the coding, bulk coding, propagation, production and snapshot endpoints).</summary>
    public static IServiceCollection AddPrivilegeConflictEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<PrivilegeConflictService>();
        services.AddSingleton<IApiEndpointModule, PrivilegeConflictEndpoints>();
        return services;
    }
}
