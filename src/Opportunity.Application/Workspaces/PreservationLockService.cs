using System.Diagnostics;
using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;

namespace Opportunity.Application.Workspaces;

public enum PreservationLockStatus
{
    Ok,
    NotFound,
    Invalid,
    VersionConflict,

    /// <summary>The lock is already released, or the release step does not fit its state (no request to approve, a request already pending).</summary>
    Conflict,

    /// <summary>The person who requested the release cannot also approve it (Q-23 two-person rule).</summary>
    SecondPersonRequired,
}

public sealed record PreservationLockOutcome(PreservationLockStatus Status, PreservationLock? Lock = null, string? Detail = null)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}

/// <summary>What a hold manager submits to place a lock.</summary>
public sealed record PreservationLockRequest(string? Reason, string? MatterReference, bool? ReleaseRequiresApproval);

/// <summary>
/// Preservation locks (legal holds; E20-T01, ADR-014 §2, Q-23). Callers hold <c>Workspace.ManageHolds</c> (declared by
/// the endpoints). Placing a lock needs a reason and takes effect at once. Releasing one needs a reason; when the lock
/// requires approval (the default) the release is a request that a different hold manager approves, and until then the
/// lock stays active and either may cancel the request. Each step is audited in its transaction (the reason text is kept
/// on the lock record, not in audit: ADR-013 §7).
/// </summary>
public sealed class PreservationLockService(IPreservationLockStore store, TimeProvider time)
{
    public const int MaxReasonLength = 2000;
    public const int MaxMatterReferenceLength = 200;

    private const string CorrelationTag = "opportunity.correlation_id";

    public Task<IReadOnlyList<PreservationLock>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ListAsync(workspaceId, cancellationToken);

    public Task<PreservationLock?> GetAsync(Guid workspaceId, Guid lockId, CancellationToken cancellationToken = default) =>
        store.GetAsync(workspaceId, lockId, cancellationToken);

    public async Task<PreservationLockOutcome> PlaceAsync(
        SecurityPrincipal caller, Guid workspaceId, PreservationLockRequest? request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var errors = new Dictionary<string, string[]>();
        var reason = Text(request?.Reason, MaxReasonLength, multiline: true, "reason", required: true, errors);
        var matter = Text(request?.MatterReference, MaxMatterReferenceLength, multiline: false, "matterReference", required: false, errors);
        if (errors.Count > 0)
        {
            return new PreservationLockOutcome(PreservationLockStatus.Invalid) { Errors = errors };
        }

        var placed = new PreservationLock
        {
            WorkspaceId = workspaceId,
            LockId = Guid.CreateVersion7(),
            Reason = reason!,
            MatterReference = matter,
            ReleaseRequiresApproval = request!.ReleaseRequiresApproval ?? true,
            PlacedBy = caller.UserId,
            PlacedAt = time.GetUtcNow(),
            Version = 1,
        };
        var saved = await store.PlaceAsync(placed, Audit(caller, placed, AuditTaxonomy.Workspace.HoldPlaced, new Dictionary<string, string?>
        {
            ["scope"] = "Workspace",
            ["releaseRequiresApproval"] = placed.ReleaseRequiresApproval ? "true" : "false",
            ["hasMatterReference"] = matter is null ? "false" : "true",
        }), cancellationToken).ConfigureAwait(false);
        return new PreservationLockOutcome(PreservationLockStatus.Ok, saved);
    }

    /// <summary>Releases the lock at once, or (when it requires approval) records the request for a second person.</summary>
    public async Task<PreservationLockOutcome> RequestReleaseAsync(
        SecurityPrincipal caller, Guid workspaceId, Guid lockId, long expectedVersion, string? reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var (current, refused) = await CurrentAsync(workspaceId, lockId, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
        {
            return refused;
        }

        var errors = new Dictionary<string, string[]>();
        var text = Text(reason, MaxReasonLength, multiline: true, "reason", required: true, errors);
        if (errors.Count > 0)
        {
            return new PreservationLockOutcome(PreservationLockStatus.Invalid) { Errors = errors };
        }

        if (current.ReleasePending)
        {
            return new PreservationLockOutcome(PreservationLockStatus.Conflict, current,
                "A release of this lock is already waiting for approval.");
        }

        var now = time.GetUtcNow();
        var release = current.ReleaseRequiresApproval
            ? new PreservationLockRelease(caller.UserId, now, text, null, null)
            : new PreservationLockRelease(caller.UserId, now, text, null, now);
        var action = current.ReleaseRequiresApproval ? AuditTaxonomy.Workspace.HoldReleaseRequested : AuditTaxonomy.Workspace.HoldReleased;
        return await ApplyAsync(caller, current, release, action, new Dictionary<string, string?>
        {
            ["releaseRequiresApproval"] = current.ReleaseRequiresApproval ? "true" : "false",
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The second person approves a pending release: the lock is released.</summary>
    public async Task<PreservationLockOutcome> ApproveReleaseAsync(
        SecurityPrincipal caller, Guid workspaceId, Guid lockId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var (current, refused) = await CurrentAsync(workspaceId, lockId, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
        {
            return refused;
        }

        if (!current.ReleasePending)
        {
            return new PreservationLockOutcome(PreservationLockStatus.Conflict, current, "No release of this lock is waiting for approval.");
        }

        if (current.ReleaseRequestedBy == caller.UserId)
        {
            return new PreservationLockOutcome(PreservationLockStatus.SecondPersonRequired, current,
                "You requested this release; another person who manages legal holds must approve it.");
        }

        var release = new PreservationLockRelease(current.ReleaseRequestedBy, current.ReleaseRequestedAt, current.ReleaseReason, caller.UserId, time.GetUtcNow());
        return await ApplyAsync(caller, current, release, AuditTaxonomy.Workspace.HoldReleased, new Dictionary<string, string?>
        {
            ["releaseRequiresApproval"] = "true",
            ["requestedBy"] = current.ReleaseRequestedBy!.Value.ToString(),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Withdraws a pending release request; the lock stays active.</summary>
    public async Task<PreservationLockOutcome> CancelReleaseAsync(
        SecurityPrincipal caller, Guid workspaceId, Guid lockId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var (current, refused) = await CurrentAsync(workspaceId, lockId, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
        {
            return refused;
        }

        if (!current.ReleasePending)
        {
            return new PreservationLockOutcome(PreservationLockStatus.Conflict, current, "No release of this lock is waiting for approval.");
        }

        return await ApplyAsync(caller, current, new PreservationLockRelease(null, null, null, null, null), AuditTaxonomy.Workspace.HoldReleaseCancelled,
            new Dictionary<string, string?> { ["requestedBy"] = current.ReleaseRequestedBy!.Value.ToString() }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The lock, and a refusal unless it exists, is active and still has <paramref name="expectedVersion"/>.</summary>
    private async Task<(PreservationLock Current, PreservationLockOutcome? Refused)> CurrentAsync(
        Guid workspaceId, Guid lockId, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(workspaceId, lockId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return (null!, new PreservationLockOutcome(PreservationLockStatus.NotFound));
        }

        if (current.Version != expectedVersion)
        {
            return (current, new PreservationLockOutcome(PreservationLockStatus.VersionConflict, current));
        }

        return current.IsActive
            ? (current, null)
            : (current, new PreservationLockOutcome(PreservationLockStatus.Conflict, current, "This lock is already released."));
    }

    private async Task<PreservationLockOutcome> ApplyAsync(
        SecurityPrincipal caller, PreservationLock current, PreservationLockRelease release, string action, Dictionary<string, string?> details,
        CancellationToken cancellationToken)
    {
        details["version"] = (current.Version + 1).ToString(CultureInfo.InvariantCulture);
        var result = await store.UpdateReleaseAsync(current.WorkspaceId, current.LockId, current.Version, release,
            Audit(caller, current, action, details), cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            PreservationLockWriteOutcome.Ok => new PreservationLockOutcome(PreservationLockStatus.Ok, result.Lock),
            PreservationLockWriteOutcome.VersionConflict => new PreservationLockOutcome(PreservationLockStatus.VersionConflict, result.Lock),
            _ => new PreservationLockOutcome(PreservationLockStatus.NotFound),
        };
    }

    private static string? Text(string? value, int max, bool multiline, string field, bool required, Dictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            if (required)
            {
                errors[field] = [$"{field} is required."];
            }

            return null;
        }

        if (trimmed.Length > max || trimmed.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t')))
        {
            errors[field] = [$"{field}: at most {max} characters{(multiline ? string.Empty : " on one line")}, without control characters."];
            return null;
        }

        return trimmed;
    }

    private AuditEvent Audit(SecurityPrincipal caller, PreservationLock target, string action, IReadOnlyDictionary<string, string?> details) => new()
    {
        WorkspaceId = target.WorkspaceId,
        OccurredAt = time.GetUtcNow(),
        Category = AuditTaxonomy.Workspace.Category,
        Action = action,
        ActorType = AuditActorType.User,
        ActorId = caller.UserId.ToString(),
        ActorDisplay = caller.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
            ? caller.DisplayName[..AuditEventRules.MaxActorDisplayLength]
            : caller.DisplayName,
        ClientIp = caller.ClientIp,
        UserAgent = caller.UserAgent,
        ResourceType = AuditTaxonomy.Workspace.PreservationLockResourceType,
        ResourceId = target.LockId.ToString(),
        Outcome = AuditOutcome.Success,
        CorrelationId = caller.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
        Details = details,
    };
}
