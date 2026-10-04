using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Api.Jobs;
using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Contracts.Api;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Data.Jobs;
using Opportunity.Data.Snapshots;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Coding;

/// <summary>
/// Bulk coding ("Mass Edit", E10-T04): <c>POST …/bulk-coding</c> submits a job over a Ready frozen set and answers
/// <c>202 Accepted</c> with the job (progress at <c>GET …/jobs/{jobId}</c>, committed and indexed phases);
/// <c>GET …/bulk-coding/{jobId}/report?outcome=</c> pages the per-document outcomes: applied, skippedChanged (Q-07),
/// skippedHidden (access changed, ADR-015 D9.4) and failed. Submitting needs <c>Coding.Bulk</c> (PEP-1) and
/// <c>Coding.WritePrivilege</c> for security-affecting fields; the report is the job owner's, or anyone's with
/// <c>Job.ViewAll</c>.
/// </summary>
public sealed class BulkCodingEndpoints : IApiEndpointModule
{
    public const string Path = "/bulk-coding";

    private const string Tag = "Coding";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapPost(Path, SubmitAsync)
            .WithName("SubmitBulkCoding")
            .WithTags(Tag)
            .WithSummary("Apply coding operations to every document of a frozen set; 202 with the job.")
            .WithDescription(
                "The set must be a Ready snapshot with purpose BulkCoding (POST …/snapshots). Runs in chunks: each chunk " +
                "re-checks your access, skips documents whose requested field someone else changed after the set was frozen " +
                "(Q-07) and documents you can no longer access, and is applied at most once. Security-affecting fields also " +
                "need Coding.WritePrivilege. Follow the job at Location; outcomes per document at …/bulk-coding/{jobId}/report.")
            .RequirePermission(Permission.CodingBulk)
            .RequireIdempotencyKey()
            .Produces<JobResource>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        routes.Workspace.MapGet(Path + "/{jobId}/report", ReportAsync)
            .WithName("GetBulkCodingReport")
            .WithTags(Tag)
            .WithSummary("Per-document outcomes of a bulk coding job, one outcome at a time, cursor-paged.")
            .WithDescription(
                "outcome=applied (default) lists documents with at least one changed value, by document id; skippedChanged, skippedHidden " +
                "and failed list the documents left unchanged, in processing order. total is the job's count for the outcome so far.")
            .RequireWorkspaceMember()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<Results<Accepted<JobResource>, ValidationProblem, ProblemHttpResult>> SubmitAsync(
        string workspaceId,
        BulkCodingRequest request,
        HttpContext context,
        BulkCodingService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        if (request?.Changes is not { Count: > 0 } operations)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["changes"] = ["At least one change is required."] });
        }

        var changes = new List<CodingChange>(operations.Count);
        foreach (var operation in operations)
        {
            if (operation is null || !Enum.IsDefined(operation.Operation))
            {
                return Problems.Validation(new Dictionary<string, string[]> { ["changes"] = ["Each change needs a field and a valid operation."] });
            }

            if (operation.Operation == BulkCodingOperationKind.Clear && operation.Value is not null)
            {
                return Problems.Validation(new Dictionary<string, string[]> { [FieldKey.For(operation.FieldId)] = ["clear takes no value."] });
            }

            changes.Add(new CodingChange(operation.FieldId, Kind(operation.Operation), operation.Value?.DeepClone()));
        }

        var outcome = await service.SubmitAsync(
            new CodingCaller(access.Principal, access.WorkspaceId),
            new BulkCodingSubmission(request.SnapshotId, changes, context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString() is { Length: > 0 } key ? key : null),
            cancellationToken).ConfigureAwait(false);

        switch (outcome.Status)
        {
            case BulkCodingSubmitStatus.Accepted:
                var job = outcome.Job!;
                return TypedResults.Accepted(
                    $"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}/jobs/{job.JobId}", JobEndpoints.ToResource(job));
            case BulkCodingSubmitStatus.Invalid:
                return TypedResults.ValidationProblem(
                    outcome.Errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()),
                    detail: "The bulk coding job was not started.",
                    type: ProblemCodes.TypeFor(ProblemCodes.Validation),
                    extensions: new Dictionary<string, object?>
                    {
                        [Problems.CodeExtension] = ProblemCodes.Validation,
                        ["fieldErrors"] = outcome.Errors.Select(e => new { field = e.Field, code = e.Code, message = e.Message }).ToArray(),
                    });
            case BulkCodingSubmitStatus.Forbidden:
                return Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation.");
            case BulkCodingSubmitStatus.SnapshotNotReady:
                return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict,
                    "The frozen set is not ready (still materializing, failed or expired); freeze the selection again.");
            case BulkCodingSubmitStatus.IdempotencyKeyReuse:
                return Problems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.IdempotencyKeyReuse,
                    $"This {IdempotencyMiddleware.HeaderName} was already used for a different job.");
            default:
                return Problems.NotFound("No such snapshot.");
        }
    }

    internal static async Task<Results<Ok<CursorPage<BulkCodingReportItemResource>>, ValidationProblem, ProblemHttpResult>> ReportAsync(
        string workspaceId,
        string jobId,
        [FromQuery(Name = "outcome")] string? outcome,
        [AsParameters] PageQuery page,
        HttpContext context,
        BulkCodingService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(jobId, out var id)
            || await service.GetJobAsync(new CodingCaller(access.Principal, access.WorkspaceId), id, cancellationToken).ConfigureAwait(false)
                is not { } job)
        {
            return Problems.NotFound("No such bulk coding job.");
        }

        if (page.Validate() is { } invalid)
        {
            return invalid;
        }

        var kind = BulkCodingOutcomeResource.Applied;
        if (outcome is not null && (!Enum.TryParse(outcome, ignoreCase: true, out kind) || !Enum.IsDefined(kind) || outcome.Any(char.IsDigit)))
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["outcome"] = ["outcome must be applied, skippedChanged, skippedHidden or failed."] });
        }

        string? position = null;
        if (page.Cursor is { } cursor)
        {
            if (PageCursor.Decode(cursor, access.Principal.UserId, access.WorkspaceId, 3) is not { } state
                || state[0] != job.JobId.ToString("N") || state[1] != kind.ToString())
            {
                return PageCursor.Invalid();
            }

            position = state[2];
        }

        var report = await service.GetReportAsync(job, Outcome(kind), position, page.EffectiveLimit, cancellationToken).ConfigureAwait(false);
        if (report is null)
        {
            return PageCursor.Invalid();
        }

        var c = job.Counters;
        var total = kind switch
        {
            BulkCodingOutcomeResource.Applied => c.ItemsApplied,
            BulkCodingOutcomeResource.SkippedChanged => c.ItemsSkippedConcurrentEdit,
            BulkCodingOutcomeResource.SkippedHidden => c.ItemsExcludedNoAccess,
            _ => c.ItemsFailed,
        };
        var next = report.Next is { } n ? PageCursor.Encode(access.Principal.UserId, access.WorkspaceId, job.JobId.ToString("N"), kind.ToString(), n) : null;
        return TypedResults.Ok(new CursorPage<BulkCodingReportItemResource>(
            [.. report.Items.Select(i => new BulkCodingReportItemResource(i.DocumentId, kind, i.ReasonCode, i.FieldIds))],
            next,
            // The job counts each document once (a partial Q-07 skip counts as skipped), while the applied list names every
            // document with at least one change: with skips, the applied counter is a lower bound of that list.
            new TotalCount(total, kind == BulkCodingOutcomeResource.Applied && c.ItemsSkippedConcurrentEdit > 0 ? TotalRelation.Gte : TotalRelation.Eq)));
    }

    private static CodingOperationKind Kind(BulkCodingOperationKind kind) => kind switch
    {
        BulkCodingOperationKind.AddChoices => CodingOperationKind.AddChoices,
        BulkCodingOperationKind.RemoveChoices => CodingOperationKind.RemoveChoices,
        _ => CodingOperationKind.Set,
    };

    private static BulkCodingOutcome Outcome(BulkCodingOutcomeResource outcome) => outcome switch
    {
        BulkCodingOutcomeResource.SkippedChanged => BulkCodingOutcome.SkippedChanged,
        BulkCodingOutcomeResource.SkippedHidden => BulkCodingOutcome.SkippedHidden,
        BulkCodingOutcomeResource.Failed => BulkCodingOutcome.Failed,
        _ => BulkCodingOutcome.Applied,
    };
}

public static class BulkCodingEndpointRegistration
{
    /// <summary>
    /// The bulk coding API: the use case over the job, snapshot and coding stores (the coding store and field catalogue
    /// come from <see cref="CodingEndpointRegistration.AddCodingEndpoints"/>).
    /// </summary>
    public static IServiceCollection AddBulkCodingEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.AddPostgresSnapshotStore();
        services.TryAddSingleton<IFieldAccessFilter, UnrestrictedFieldAccess>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<BulkCodingService>();
        services.AddSingleton<IApiEndpointModule, BulkCodingEndpoints>();
        return services;
    }
}
