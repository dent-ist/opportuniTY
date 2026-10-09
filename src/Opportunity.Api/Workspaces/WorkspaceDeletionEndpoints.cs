using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Opportunity.Api.Conventions;
using Opportunity.Application.Storage;
using Opportunity.Application.Workspaces;
using Opportunity.Application.Workspaces.Deletion;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Workspaces;
using Opportunity.Security.Authentication;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Workspaces;

/// <summary>
/// Defensible workspace deletion (E20-T02, ADR-014 §3-§8, Q-23). A Workspace Admin requests from Admin › Workspace
/// Settings (<c>Workspace.RequestDeletion</c>, a reason, the retention profile and the workspace name typed); a different
/// person holding <c>Installation.ApproveDeletion</c> (the Retention Approver role, with MFA) approves; the run starts
/// once the waiting period after the approval has passed and is driven by the deletion coordinator. Status, progress and
/// the destruction certificate are installation-level routes, so the requester and approvers can follow the deletion
/// after the workspace itself answers 404; anyone else gets 404 for them. A legal hold refuses requests and approvals
/// with 423 (audited) through <see cref="ApiExceptionHandler"/>.
/// </summary>
public sealed class WorkspaceDeletionEndpoints : IApiEndpointModule
{
    public const string WorkspacePath = "/deletions";
    public const string CollectionPath = "/workspace-deletions";

    private const string Tag = "Workspace deletion";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.Workspace.MapGet(WorkspacePath, ListForWorkspaceAsync)
            .WithName("ListWorkspaceDeletionRequests")
            .WithTags(Tag)
            .Produces<WorkspaceDeletionList>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("The workspace's deletion requests and runs, newest first.")
            .RequirePermission(Permission.WorkspaceRequestDeletion);

        routes.Workspace.MapPost(WorkspacePath, RequestAsync)
            .WithName("RequestWorkspaceDeletion")
            .WithTags(Tag)
            .Produces<WorkspaceDeletionResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status423Locked)
            .WithSummary("Request the deletion of the workspace: a reason, the retention profile and the workspace name typed to confirm.")
            .WithDescription("Nothing is removed until a different person with Installation.ApproveDeletion approves and the waiting period "
                + "has passed. Refused with 423 while a legal hold applies; 409 while another request is open.")
            .RequirePermission(Permission.WorkspaceRequestDeletion);

        routes.V1.MapGet(CollectionPath, ListAsync)
            .WithName("ListWorkspaceDeletions")
            .WithTags(Tag)
            .Produces<WorkspaceDeletionList>()
            .WithSummary("Workspace deletions: every one for Retention Approvers, the caller's own requests for anyone else.")
            .RequireAuthorization();

        routes.V1.MapGet(CollectionPath + "/{deletionId}", GetAsync)
            .WithName("GetWorkspaceDeletion")
            .WithTags(Tag)
            .Produces<WorkspaceDeletionResource>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("A deletion with its progress; the version is the ETag. Visible to its requester and to Retention Approvers.")
            .RequireAuthorization();

        routes.V1.MapPost(CollectionPath + "/{deletionId}/approve", ApproveAsync)
            .WithName("ApproveWorkspaceDeletion")
            .WithTags(Tag)
            .Produces<WorkspaceDeletionResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status423Locked)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithSummary("Approve a requested deletion (If-Match): the run starts once the waiting period has passed.")
            .WithDescription("Needs Installation.ApproveDeletion and an MFA session; the requester cannot approve their own request "
                + "(403 second-person-required). Refused with 423 while a legal hold applies.")
            .RequireInstallationPermission(InstallationPermissions.ApproveDeletion)
            .RequireMfa();

        routes.V1.MapPost(CollectionPath + "/{deletionId}/cancel", CancelAsync)
            .WithName("CancelWorkspaceDeletion")
            .WithTags(Tag)
            .Produces<WorkspaceDeletionResource>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithSummary("Withdraw a requested or approved deletion before its run starts (If-Match); its requester or a Retention Approver.")
            .RequireAuthorization();

        routes.V1.MapGet(CollectionPath + "/{deletionId}/certificate", GetCertificateAsync)
            .WithName("GetDestructionCertificate")
            .WithTags(Tag)
            .Produces<DestructionCertificateDocument>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("The destruction certificate of a finished deletion, with its SHA-256 and signature, as a JSON download.")
            .RequireAuthorization();
    }

    internal static async Task<IResult> ListForWorkspaceAsync(
        string workspaceId, HttpContext context, WorkspaceDeletionService service, IOptionsMonitor<InstallationAuthorizationOptions> installation,
        TimeProvider time, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var caller = Caller(context, installation);
        var deletions = await service.ListForWorkspaceAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new WorkspaceDeletionList([.. deletions.Select(d => ToResource(d, [], caller, time.GetUtcNow()))]));
    }

    internal static async Task<IResult> RequestAsync(
        string workspaceId, WorkspaceDeletionWrite? body, HttpContext context, WorkspaceDeletionService service,
        IOptionsMonitor<InstallationAuthorizationOptions> installation, TimeProvider time, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var profile = body?.RetentionProfile switch
        {
            DeletionRetentionProfileResource.PurgeAll => DeletionRetentionProfile.PurgeAll,
            _ => DeletionRetentionProfile.RetainRecords,
        };
        var outcome = await service.RequestAsync(access.Principal, access.WorkspaceId,
            new WorkspaceDeletionRequest(profile, body?.Reason, body?.ExternalReference, body?.ConfirmName), cancellationToken).ConfigureAwait(false);
        if (outcome.Status != WorkspaceDeletionResultStatus.Ok)
        {
            return Problem(outcome);
        }

        var deletion = outcome.Deletion!;
        context.Response.Headers.ETag = EntityTags.ForVersion(deletion.Version);
        return TypedResults.Created($"{ApiRoutes.V1Prefix}{CollectionPath}/{deletion.DeletionId}",
            ToResource(deletion, [], Caller(context, installation), time.GetUtcNow()));
    }

    internal static async Task<IResult> ListAsync(
        bool? openOnly, HttpContext context, WorkspaceDeletionService service, IOptionsMonitor<InstallationAuthorizationOptions> installation,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var caller = Caller(context, installation);
        var deletions = await service.ListAsync(caller, openOnly ?? false, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new WorkspaceDeletionList([.. deletions.Select(d => ToResource(d, [], caller, time.GetUtcNow()))]));
    }

    internal static async Task<IResult> GetAsync(
        string deletionId, HttpContext context, WorkspaceDeletionService service, IOptionsMonitor<InstallationAuthorizationOptions> installation,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var caller = Caller(context, installation);
        if (!Guid.TryParse(deletionId, out var id) || await service.GetAsync(caller, id, cancellationToken).ConfigureAwait(false) is not { } deletion)
        {
            return Problems.NotFound();
        }

        var steps = await service.GetStepsAsync(id, cancellationToken).ConfigureAwait(false);
        context.Response.Headers.ETag = EntityTags.ForVersion(deletion.Version);
        return TypedResults.Ok(ToResource(deletion, steps, caller, time.GetUtcNow()));
    }

    internal static Task<IResult> ApproveAsync(
        string deletionId, WorkspaceDeletionApprovalWrite? body, HttpContext context, WorkspaceDeletionService service,
        IOptionsMonitor<InstallationAuthorizationOptions> installation, TimeProvider time, CancellationToken cancellationToken) =>
        StepAsync(deletionId, context, service, installation, time,
            (caller, current) => service.ApproveAsync(caller, current.DeletionId, current.Version, body?.Note, cancellationToken), cancellationToken);

    internal static Task<IResult> CancelAsync(
        string deletionId, HttpContext context, WorkspaceDeletionService service, IOptionsMonitor<InstallationAuthorizationOptions> installation,
        TimeProvider time, CancellationToken cancellationToken) =>
        StepAsync(deletionId, context, service, installation, time,
            (caller, current) => service.CancelAsync(caller, current.DeletionId, current.Version, cancellationToken), cancellationToken);

    internal static async Task<IResult> GetCertificateAsync(
        string deletionId, HttpContext context, WorkspaceDeletionService service, IOptionsMonitor<InstallationAuthorizationOptions> installation,
        CancellationToken cancellationToken)
    {
        var caller = Caller(context, installation);
        if (!Guid.TryParse(deletionId, out var id)
            || await service.GetCertificateAsync(caller, id, cancellationToken).ConfigureAwait(false) is not { } certificate)
        {
            return Problems.NotFound();
        }

        context.Response.Headers.ContentDisposition = ContentDispositionHeader.Attachment($"destruction-certificate-{id:N}.json");
        return TypedResults.Ok(new DestructionCertificateDocument(
            JsonNode.Parse(certificate.CertificateJson)!.AsObject(),
            certificate.CertificateJson,
            Convert.ToHexStringLower(certificate.Sha256),
            certificate.IssuedAt,
            certificate.Signature is { } signature && certificate.SignatureKeyId is { } keyId
                ? new DestructionCertificateSignature(keyId, "ES256", Convert.ToBase64String(signature))
                : null));
    }

    private static async Task<IResult> StepAsync(
        string deletionId, HttpContext context, WorkspaceDeletionService service, IOptionsMonitor<InstallationAuthorizationOptions> installation,
        TimeProvider time, Func<DeletionCaller, WorkspaceDeletion, Task<WorkspaceDeletionResult>> step, CancellationToken cancellationToken)
    {
        var caller = Caller(context, installation);
        if (!Guid.TryParse(deletionId, out var id) || await service.GetAsync(caller, id, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound();
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await step(caller, current).ConfigureAwait(false);
        if (outcome.Status != WorkspaceDeletionResultStatus.Ok)
        {
            return Problem(outcome);
        }

        var deletion = outcome.Deletion!;
        var steps = await service.GetStepsAsync(id, cancellationToken).ConfigureAwait(false);
        context.Response.Headers.ETag = EntityTags.ForVersion(deletion.Version);
        return TypedResults.Ok(ToResource(deletion, steps, caller, time.GetUtcNow()));
    }

    private static DeletionCaller Caller(HttpContext context, IOptionsMonitor<InstallationAuthorizationOptions> installation) =>
        new(context.ToSecurityPrincipal(), InstallationPermissions.IsRetentionApprover(context.User, installation.CurrentValue));

    private static IResult Problem(WorkspaceDeletionResult outcome) => outcome.Status switch
    {
        WorkspaceDeletionResultStatus.Invalid => Problems.Validation(outcome.Errors.ToDictionary()),
        WorkspaceDeletionResultStatus.VersionConflict =>
            Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict, "The deletion was changed since it was read."),
        WorkspaceDeletionResultStatus.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, outcome.Detail),
        WorkspaceDeletionResultStatus.SecondPersonRequired =>
            Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.SecondPersonRequired, outcome.Detail),
        _ => Problems.NotFound(),
    };

    internal static WorkspaceDeletionResource ToResource(
        WorkspaceDeletion d, IReadOnlyList<WorkspaceDeletionStepRecord> steps, DeletionCaller caller, DateTimeOffset now) => new(
        d.DeletionId,
        d.WorkspaceId,
        d.WorkspaceName,
        d.MatterNumber,
        d.RetentionProfile == DeletionRetentionProfile.PurgeAll ? DeletionRetentionProfileResource.PurgeAll : DeletionRetentionProfileResource.RetainRecords,
        d.Reason,
        d.ExternalReference,
        Enum.Parse<WorkspaceDeletionStatusResource>(d.Status.ToString()),
        d.Status is WorkspaceDeletionStatus.Running or WorkspaceDeletionStatus.Halted && d.Step is { } current
            ? Enum.Parse<DeletionStepResource>(current.ToString())
            : null,
        new DeletionActor(d.RequestedBy, d.RequestedByName),
        d.RequestedAt,
        d.ExpiresAt,
        d.ApprovedBy is { } approver ? new DeletionActor(approver, d.ApprovedByName) : null,
        d.ApprovedAt,
        d.ApprovalNote,
        d.RunNotBefore,
        d.CancelledBy is { } canceller ? new DeletionActor(canceller, d.CancelledByName) : null,
        d.CancelledAt,
        d.StartedAt,
        d.FinishedAt,
        d.HaltedAt,
        d.Error,
        [.. steps.OrderBy(s => s.Step).Select(s => new WorkspaceDeletionStepProgress(
            Enum.Parse<DeletionStepResource>(s.Step.ToString()), s.Attempt, s.StartedAt, s.FinishedAt, OutcomeName(s.Outcome), Totals(s.Counts)))],
        d.HasCertificate,
        WorkspaceDeletionService.CanApproveDeletion(caller, d, now),
        WorkspaceDeletionService.CanCancel(caller, d),
        d.Version);

    private static string? OutcomeName(string? outcome) =>
        outcome is null ? null : char.ToLowerInvariant(outcome[0]) + outcome[1..];

    /// <summary>Headline numbers of a step: its top-level counts plus the totals of each store it counted.</summary>
    internal static IReadOnlyDictionary<string, long> Totals(JsonObject? counts)
    {
        var totals = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (counts is null)
        {
            return totals;
        }

        foreach (var (name, value) in counts)
        {
            if (value is JsonValue number && long.TryParse(number.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                totals[name] = n;
            }
        }

        if (counts["postgres"] is JsonObject tables)
        {
            totals["rows"] = tables.Sum(t => Number(t.Value?["total"]));
        }

        if (counts["openSearch"] is JsonObject search)
        {
            totals["documents"] = Number(search["documents"]);
        }

        if (counts["objects"] is JsonObject objects && counts["postgres"] is not null)
        {
            totals["objects"] = Number(objects["objects"]);
            if (objects["bytes"] is not null)
            {
                totals["bytes"] = Number(objects["bytes"]);
            }
        }

        if (counts["keys"] is JsonObject keys)
        {
            totals["keys"] = keys["total"] is not null ? Number(keys["total"]) : Number(keys["usable"]);
        }

        return totals;
    }

    private static long Number(JsonNode? node) =>
        node is JsonValue value && long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}

public static class WorkspaceDeletionEndpointRegistration
{
    /// <summary>The deletion endpoints, their use case and the PostgreSQL store (options from section <c>WorkspaceDeletion</c>).</summary>
    public static IServiceCollection AddWorkspaceDeletionEndpoints(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new WorkspaceDeletionOptions();
        configuration.GetSection(WorkspaceDeletionOptions.SectionName).Bind(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IWorkspaceReader, WorkspaceReader>();
        services.AddPostgresWorkspaceDeletions();
        services.TryAddScoped<WorkspaceDeletionService>();
        services.AddSingleton<IApiEndpointModule, WorkspaceDeletionEndpoints>();
        return services;
    }
}
