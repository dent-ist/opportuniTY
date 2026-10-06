using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Api.Jobs;
using Opportunity.Application.Documents.Dedupe;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Contracts.Api;
using Opportunity.Core.Documents;
using Opportunity.Core.Security;
using Opportunity.Data.Fields;
using Opportunity.Data.Jobs;
using Opportunity.Data.Relationships;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Dedupe;

/// <summary>
/// Computed duplicate grouping (E09-T04, Q-09): <c>GET/PUT …/dedupe-policy</c> reads and saves the workspace's policy
/// (ETag/If-Match), <c>POST …/dedupe-runs</c> applies the saved policy as a job (202 + job, Idempotency-Key). Every route
/// needs <c>Workspace.ManageFields</c>: the policy decides how the Duplicate Group and Duplicate Primary system fields
/// are derived for the whole workspace, which is field administration; it grants no access to anything, so it is not
/// <c>Workspace.ManageSecurity</c>.
/// </summary>
public sealed class DedupeEndpoints : IApiEndpointModule
{
    public const string PolicyPath = "/dedupe-policy";
    public const string RunsPath = "/dedupe-runs";

    private const string Tag = "Relationships";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(PolicyPath, GetAsync)
            .WithName("GetDedupePolicy")
            .WithTags(Tag)
            .WithSummary("The workspace's computed duplicate grouping policy and what its last run did.")
            .WithDescription("Version 0 means no policy was saved: the default (disabled, auto hash, global scope) applies.")
            .RequirePermission(Permission.WorkspaceManageFields)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.Workspace.MapPut(PolicyPath, PutAsync)
            .WithName("UpdateDedupePolicy")
            .WithTags(Tag)
            .WithSummary("Save the computed duplicate grouping policy (If-Match required; \"0\" before the first save).")
            .WithDescription(
                "Grouping is family-level: top-level parents are compared on the chosen hash (and, in custodial scope, their " +
                "custodian) and attachments follow their parent. Documents with an upstream duplicate group are never regrouped. " +
                "Duplicates are labelled, never hidden. Saving changes no document; POST …/dedupe-runs applies the policy.")
            .RequirePermission(Permission.WorkspaceManageFields)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        routes.Workspace.MapPost(RunsPath, RunAsync)
            .WithName("StartDedupeRun")
            .WithTags(Tag)
            .WithSummary("Apply the saved dedupe policy to every document of the workspace; 202 with the job.")
            .WithDescription(
                "Re-runnable and idempotent: group ids derive from the hash (and custodian), so an unchanged workspace changes " +
                "nothing. Documents whose duplicate group or primary flag changes are reindexed; follow the job at Location. " +
                "A disabled policy removes the computed groups.")
            .RequirePermission(Permission.WorkspaceManageFields)
            .RequireIdempotencyKey()
            .Produces<JobResource>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    internal static async Task<Results<Ok<DedupePolicyResource>, ProblemHttpResult>> GetAsync(
        string workspaceId, HttpContext context, DedupeService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var record = await service.GetAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        context.Response.Headers.ETag = EntityTags.ForVersion(record.Version);
        return TypedResults.Ok(ToResource(record));
    }

    internal static async Task<Results<Ok<DedupePolicyResource>, ValidationProblem, ProblemHttpResult>> PutAsync(
        string workspaceId, DedupePolicyWrite body, HttpContext context, DedupeService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var current = await service.GetAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        if (body is null || !Enum.IsDefined(body.HashSource) || !Enum.IsDefined(body.Scope))
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["body"] = ["enabled, hashSource and scope are required."] });
        }

        var policy = new DedupePolicy(body.Enabled, HashSource(body.HashSource), Scope(body.Scope), body.CustodianFieldId);
        var outcome = await service.SaveAsync(access.WorkspaceId, current.Version, policy, access.Principal, cancellationToken).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case DedupeOutcomeStatus.Invalid:
                return Problems.Validation(new Dictionary<string, string[]>(outcome.Errors), "The dedupe policy was not saved.");
            case DedupeOutcomeStatus.VersionConflict:
                return Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict, "The resource was modified since it was read.");
            default:
                context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Record!.Version);
                return TypedResults.Ok(ToResource(outcome.Record));
        }
    }

    internal static async Task<Results<Accepted<JobResource>, ValidationProblem, ProblemHttpResult>> RunAsync(
        string workspaceId, HttpContext context, DedupeService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var key = context.Request.Headers[IdempotencyMiddleware.HeaderName].ToString();
        var outcome = await service.StartRunAsync(access.WorkspaceId, access.Principal, key.Length > 0 ? key : null, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Status switch
        {
            DedupeOutcomeStatus.Ok => ApiResults.JobAccepted(access.WorkspaceId.ToString(), outcome.Job!.JobId.ToString(), JobEndpoints.ToResource(outcome.Job)),
            DedupeOutcomeStatus.Invalid => Problems.Validation(new Dictionary<string, string[]>(outcome.Errors), "The dedupe run was not started."),
            _ => Problems.Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.IdempotencyKeyReuse,
                $"This {IdempotencyMiddleware.HeaderName} was already used for a different job."),
        };
    }

    private static DedupePolicyResource ToResource(DedupePolicyRecord record) => new(
        record.Policy.Enabled,
        HashSource(record.Policy.HashSource),
        Scope(record.Policy.Scope),
        record.Policy.CustodianFieldId,
        record.Version,
        record.ModifiedBy,
        record.ModifiedAt,
        record.LastRun is { } run
            ? new DedupeRunSummaryResource(
                run.JobId, run.RanAt, run.Policy.Enabled, HashSource(run.Policy.HashSource), Scope(run.Policy.Scope), run.Policy.CustodianFieldId,
                run.FamiliesCompared, run.FamiliesWithoutHash, run.FamiliesWithoutCustodian, run.FamiliesWithUpstreamGroup, run.Groups,
                run.DocumentsGrouped, run.DocumentsChanged)
            : null);

    private static DedupeHashSource HashSource(DedupeHashSourceResource source) => source switch
    {
        DedupeHashSourceResource.Sha256 => DedupeHashSource.Sha256,
        DedupeHashSourceResource.Md5 => DedupeHashSource.Md5,
        DedupeHashSourceResource.Sha1 => DedupeHashSource.Sha1,
        DedupeHashSourceResource.UpstreamHash => DedupeHashSource.UpstreamHash,
        _ => DedupeHashSource.Auto,
    };

    private static DedupeHashSourceResource HashSource(DedupeHashSource source) => source switch
    {
        DedupeHashSource.Sha256 => DedupeHashSourceResource.Sha256,
        DedupeHashSource.Md5 => DedupeHashSourceResource.Md5,
        DedupeHashSource.Sha1 => DedupeHashSourceResource.Sha1,
        DedupeHashSource.UpstreamHash => DedupeHashSourceResource.UpstreamHash,
        _ => DedupeHashSourceResource.Auto,
    };

    private static DuplicateGroupScope Scope(DedupeScopeResource scope) =>
        scope == DedupeScopeResource.Custodial ? DuplicateGroupScope.Custodial : DuplicateGroupScope.Global;

    private static DedupeScopeResource Scope(DuplicateGroupScope scope) =>
        scope == DuplicateGroupScope.Custodial ? DedupeScopeResource.Custodial : DedupeScopeResource.Global;
}

public static class DedupeEndpointRegistration
{
    /// <summary>The dedupe policy and run API over <see cref="DedupeStore"/>, the job store and the field catalogue.</summary>
    public static IServiceCollection AddDedupeEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDedupeStore, DedupeStore>();
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.TryAddSingleton<IFieldCatalogRepository, FieldCatalogRepository>();
        services.TryAddScoped<DedupeService>();
        services.AddSingleton<IApiEndpointModule, DedupeEndpoints>();
        return services;
    }
}
