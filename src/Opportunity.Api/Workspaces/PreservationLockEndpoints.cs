using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Workspaces;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Workspaces;

/// <summary>
/// Preservation locks (legal holds; E20-T01, ADR-014 §2, Q-23) for Admin › Workspace Settings: list, place, and release
/// (at once, or as a request a different hold manager approves or either cancels). Everything needs
/// <c>Workspace.ManageHolds</c>; every step names a reason (placing and releasing) and is audited in its transaction.
/// Release steps take If-Match with the lock's version. Every workspace member sees whether the workspace is held through
/// <c>activePreservationLocks</c> on the workspace resource. Enforcement is in the database (V0048): a refused delete
/// answers 423 through <see cref="ApiExceptionHandler"/>.
/// </summary>
public sealed class PreservationLockEndpoints : IApiEndpointModule
{
    public const string LocksPath = "/preservation-locks";

    private const string Tag = "Legal holds";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(LocksPath, ListAsync)
            .WithName("ListPreservationLocks")
            .WithTags(Tag)
            .Produces<PreservationLockList>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Every preservation lock (legal hold) of the workspace, active ones first.")
            .RequirePermission(Permission.WorkspaceManageHolds);

        ws.MapPost(LocksPath, PlaceAsync)
            .WithName("PlacePreservationLock")
            .WithTags(Tag)
            .Produces<PreservationLockResource>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Place a preservation lock (legal hold): takes effect at once; a reason is required (audited).")
            .WithDescription("While any lock is active, deleting or purging the workspace's documents, artifacts, coding and redaction "
                + "history, snapshots, productions, exports, reports or audit answers 423 preservation-locked.")
            .RequirePermission(Permission.WorkspaceManageHolds);

        ws.MapGet(LocksPath + "/{lockId}", GetAsync)
            .WithName("GetPreservationLock")
            .WithTags(Tag)
            .Produces<PreservationLockResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("A preservation lock with its version as ETag.")
            .RequirePermission(Permission.WorkspaceManageHolds);

        ws.MapPost(LocksPath + "/{lockId}/release", ReleaseAsync)
            .WithName("ReleasePreservationLock")
            .WithTags(Tag)
            .Produces<PreservationLockResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithSummary("Release a lock (reason required, If-Match): at once, or as a request a second person approves.")
            .WithDescription("A lock that requires approval stays active with status releasePending until a different hold manager "
                + "approves the release.")
            .RequirePermission(Permission.WorkspaceManageHolds);

        ws.MapPost(LocksPath + "/{lockId}/release/approve", ApproveAsync)
            .WithName("ApprovePreservationLockRelease")
            .WithTags(Tag)
            .Produces<PreservationLockResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithSummary("Approve a pending release (If-Match): the lock is released. The requester cannot approve it (403 second-person-required).")
            .RequirePermission(Permission.WorkspaceManageHolds);

        ws.MapPost(LocksPath + "/{lockId}/release/cancel", CancelAsync)
            .WithName("CancelPreservationLockRelease")
            .WithTags(Tag)
            .Produces<PreservationLockResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithSummary("Withdraw a pending release request (If-Match); the lock stays active.")
            .RequirePermission(Permission.WorkspaceManageHolds);
    }

    internal static async Task<IResult> ListAsync(
        string workspaceId, HttpContext context, PreservationLockService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var locks = await service.ListAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new PreservationLockList([.. locks.Select(ToResource)], locks.Count(l => l.IsActive)));
    }

    internal static async Task<IResult> PlaceAsync(
        string workspaceId, PreservationLockWrite? body, HttpContext context, PreservationLockService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var outcome = await service.PlaceAsync(access.Principal, access.WorkspaceId,
            new PreservationLockRequest(body?.Reason, body?.MatterReference, body?.ReleaseRequiresApproval), cancellationToken).ConfigureAwait(false);
        if (outcome.Status != PreservationLockStatus.Ok)
        {
            return Problem(outcome);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Lock!.Version);
        return TypedResults.Created($"{ApiRoutes.V1Prefix}/workspaces/{access.WorkspaceId}{LocksPath}/{outcome.Lock.LockId}", ToResource(outcome.Lock));
    }

    internal static async Task<IResult> GetAsync(
        string workspaceId, string lockId, HttpContext context, PreservationLockService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(lockId, out var id)
            || await service.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return Problems.NotFound();
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(found.Version);
        return TypedResults.Ok(ToResource(found));
    }

    internal static Task<IResult> ReleaseAsync(
        string workspaceId, string lockId, PreservationLockReleaseWrite? body, HttpContext context, PreservationLockService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        return StepAsync(lockId, context, service,
            (access, current) => service.RequestReleaseAsync(access.Principal, access.WorkspaceId, current.LockId, current.Version, body?.Reason, cancellationToken),
            cancellationToken);
    }

    internal static Task<IResult> ApproveAsync(
        string workspaceId, string lockId, HttpContext context, PreservationLockService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        return StepAsync(lockId, context, service,
            (access, current) => service.ApproveReleaseAsync(access.Principal, access.WorkspaceId, current.LockId, current.Version, cancellationToken),
            cancellationToken);
    }

    internal static Task<IResult> CancelAsync(
        string workspaceId, string lockId, HttpContext context, PreservationLockService service, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        return StepAsync(lockId, context, service,
            (access, current) => service.CancelReleaseAsync(access.Principal, access.WorkspaceId, current.LockId, current.Version, cancellationToken),
            cancellationToken);
    }

    private static async Task<IResult> StepAsync(
        string lockId, HttpContext context, PreservationLockService service,
        Func<WorkspaceAccess, PreservationLock, Task<PreservationLockOutcome>> step, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access || !Guid.TryParse(lockId, out var id)
            || await service.GetAsync(access.WorkspaceId, id, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound();
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        var outcome = await step(access, current).ConfigureAwait(false);
        if (outcome.Status != PreservationLockStatus.Ok)
        {
            return Problem(outcome);
        }

        context.Response.Headers.ETag = EntityTags.ForVersion(outcome.Lock!.Version);
        return TypedResults.Ok(ToResource(outcome.Lock));
    }

    private static IResult Problem(PreservationLockOutcome outcome) => outcome.Status switch
    {
        PreservationLockStatus.Invalid => Problems.Validation(outcome.Errors.ToDictionary()),
        PreservationLockStatus.VersionConflict =>
            Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict, "The lock was changed since it was read."),
        PreservationLockStatus.Conflict => Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.Conflict, outcome.Detail),
        PreservationLockStatus.SecondPersonRequired =>
            Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.SecondPersonRequired, outcome.Detail),
        _ => Problems.NotFound(),
    };

    internal static PreservationLockResource ToResource(PreservationLock l) => new(
        l.LockId,
        l.ReleasedAt is not null ? PreservationLockStatusResource.Released
            : l.ReleasePending ? PreservationLockStatusResource.ReleasePending : PreservationLockStatusResource.Active,
        PreservationLockScope.Workspace,
        l.Reason,
        l.MatterReference,
        l.ReleaseRequiresApproval,
        new PreservationLockActor(l.PlacedBy, l.PlacedByName),
        l.PlacedAt,
        l.ReleaseRequestedBy is { } requester ? new PreservationLockActor(requester, l.ReleaseRequestedByName) : null,
        l.ReleaseRequestedAt,
        l.ReleaseReason,
        l.ReleaseApprovedBy is { } approver ? new PreservationLockActor(approver, l.ReleaseApprovedByName) : null,
        l.ReleasedAt,
        l.Version);
}

public static class PreservationLockEndpointRegistration
{
    /// <summary>The legal hold endpoints, their use case and the PostgreSQL store.</summary>
    public static IServiceCollection AddPreservationLockEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddPostgresPreservationLocks();
        services.TryAddScoped<PreservationLockService>();
        services.AddSingleton<IApiEndpointModule, PreservationLockEndpoints>();
        return services;
    }
}
