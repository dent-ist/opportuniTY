using System.Diagnostics;
using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;

namespace Opportunity.Application.Workspaces.Deletion;

public enum WorkspaceDeletionResultStatus
{
    Ok,
    NotFound,
    Invalid,
    VersionConflict,

    /// <summary>The workspace already has an open deletion, or the step does not fit the deletion's state.</summary>
    Conflict,

    /// <summary>The requester cannot approve their own request (Q-23 two-person rule).</summary>
    SecondPersonRequired,
}

public sealed record WorkspaceDeletionResult(WorkspaceDeletionResultStatus Status, WorkspaceDeletion? Deletion = null, string? Detail = null)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}

/// <summary>What a Workspace Admin submits to request a deletion; <paramref name="ConfirmName"/> is the workspace name, typed.</summary>
public sealed record WorkspaceDeletionRequest(DeletionRetentionProfile? RetentionProfile, string? Reason, string? ExternalReference, string? ConfirmName);

/// <summary>Who is asking, as far as deletion visibility goes.</summary>
/// <param name="CanApprove">Holds the installation permission <c>Installation.ApproveDeletion</c> (Retention Approver).</param>
public sealed record DeletionCaller(SecurityPrincipal Principal, bool CanApprove);

/// <summary>
/// Deletion requests and approvals (E20-T02, ADR-014 §3, Q-23). A Workspace Admin (<c>Workspace.RequestDeletion</c>,
/// declared by the endpoint) requests with a retention profile and a reason; a different person holding
/// <c>Installation.ApproveDeletion</c> approves, and the run starts once <see cref="WorkspaceDeletionOptions.WaitingPeriod"/>
/// has passed. Either party may cancel until then; an unapproved request expires. A legal hold refuses requests and
/// approvals (423, audited by the API). Only the requester and approvers see a deletion; anyone else gets NotFound.
/// </summary>
public sealed class WorkspaceDeletionService(IWorkspaceDeletionStore store, IWorkspaceReader workspaces, WorkspaceDeletionOptions options, TimeProvider time)
{
    private const string CorrelationTag = "opportunity.correlation_id";

    public async Task<WorkspaceDeletionResult> RequestAsync(
        SecurityPrincipal caller, Guid workspaceId, WorkspaceDeletionRequest? request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var errors = new Dictionary<string, string[]>();
        var reason = Text(request?.Reason, WorkspaceDeletionRules.MaxReasonLength, multiline: true, "reason", required: true, errors);
        var reference = Text(request?.ExternalReference, WorkspaceDeletionRules.MaxExternalReferenceLength, multiline: false, "externalReference",
            required: false, errors);
        var profile = request?.RetentionProfile ?? DeletionRetentionProfile.RetainRecords;
        if (!Enum.IsDefined(profile))
        {
            errors["retentionProfile"] = ["Use retainRecords or purgeAll."];
        }

        if (await workspaces.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false) is not { } workspace)
        {
            return new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.NotFound);
        }

        if (!string.Equals(request?.ConfirmName?.Trim(), workspace.Name, StringComparison.Ordinal))
        {
            errors["confirmName"] = ["Type the workspace name exactly to confirm."];
        }

        if (errors.Count > 0)
        {
            return new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.Invalid) { Errors = errors };
        }

        var now = time.GetUtcNow();
        var deletion = new WorkspaceDeletion
        {
            DeletionId = Guid.CreateVersion7(),
            WorkspaceId = workspaceId,
            WorkspaceName = workspace.Name,
            MatterNumber = workspace.MatterNumber,
            RetentionProfile = profile,
            Reason = reason!,
            ExternalReference = reference,
            Status = WorkspaceDeletionStatus.Requested,
            RequestedBy = caller.UserId,
            RequestedAt = now,
            ExpiresAt = now + options.RequestExpiry,
            Version = 1,
        };
        var result = await store.CreateAsync(deletion, Audit(caller, deletion, AuditTaxonomy.Workspace.DeletionRequested, new Dictionary<string, string?>
        {
            ["retentionProfile"] = profile.ToString(),
            ["hasExternalReference"] = reference is null ? "false" : "true",
            ["expiresAt"] = deletion.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
        }), cancellationToken).ConfigureAwait(false);
        return result.Outcome == DeletionWriteOutcome.Ok
            ? new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.Ok, result.Deletion)
            : new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.Conflict, result.Deletion,
                "This workspace already has a deletion request or run in progress.");
    }

    /// <summary>The deletion when the caller may see it: its requester, or anyone who can approve deletions.</summary>
    public async Task<WorkspaceDeletion?> GetAsync(DeletionCaller caller, Guid deletionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var deletion = await store.GetAsync(deletionId, cancellationToken).ConfigureAwait(false);
        return deletion is not null && CanSee(caller, deletion) ? deletion : null;
    }

    public Task<IReadOnlyList<WorkspaceDeletion>> ListAsync(DeletionCaller caller, bool openOnly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return store.ListAsync(new WorkspaceDeletionQuery(caller.CanApprove ? null : caller.Principal.UserId, openOnly), cancellationToken);
    }

    /// <summary>The workspace's deletions, for its administrators (the endpoint requires <c>Workspace.RequestDeletion</c>).</summary>
    public Task<IReadOnlyList<WorkspaceDeletion>> ListForWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ListForWorkspaceAsync(workspaceId, cancellationToken);

    public Task<IReadOnlyList<WorkspaceDeletionStepRecord>> GetStepsAsync(Guid deletionId, CancellationToken cancellationToken = default) =>
        store.GetStepsAsync(deletionId, cancellationToken);

    public async Task<StoredDestructionCertificate?> GetCertificateAsync(DeletionCaller caller, Guid deletionId, CancellationToken cancellationToken = default) =>
        await GetAsync(caller, deletionId, cancellationToken).ConfigureAwait(false) is { HasCertificate: true }
            ? await store.GetCertificateAsync(deletionId, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>The second person approves (the endpoint requires <c>Installation.ApproveDeletion</c> and MFA).</summary>
    public async Task<WorkspaceDeletionResult> ApproveAsync(
        DeletionCaller caller, Guid deletionId, long expectedVersion, string? note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var errors = new Dictionary<string, string[]>();
        var text = Text(note, WorkspaceDeletionRules.MaxNoteLength, multiline: true, "note", required: false, errors);
        if (errors.Count > 0)
        {
            return new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.Invalid) { Errors = errors };
        }

        if (await GetAsync(caller, deletionId, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.NotFound);
        }

        var now = time.GetUtcNow();
        if (current.Version != expectedVersion)
        {
            return new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.VersionConflict, current);
        }

        if (current.Status != WorkspaceDeletionStatus.Requested || current.ExpiresAt <= now)
        {
            return new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.Conflict, current,
                current.Status == WorkspaceDeletionStatus.Requested ? "The request has expired." : $"The deletion is {current.Status} and cannot be approved.");
        }

        if (current.RequestedBy == caller.Principal.UserId)
        {
            return new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.SecondPersonRequired, current,
                "The person who requested a deletion cannot approve it; a second person must (Q-23).");
        }

        var runNotBefore = now + options.WaitingPeriod;
        var result = await store.ApproveAsync(deletionId, expectedVersion, caller.Principal.UserId, text, now, runNotBefore,
            Audit(caller.Principal, current, AuditTaxonomy.Workspace.DeletionApproved, new Dictionary<string, string?>
            {
                ["retentionProfile"] = current.RetentionProfile.ToString(),
                ["runNotBefore"] = runNotBefore.ToString("O", CultureInfo.InvariantCulture),
                ["waitingPeriodHours"] = options.WaitingPeriod.TotalHours.ToString("0.##", CultureInfo.InvariantCulture),
            }), cancellationToken).ConfigureAwait(false);
        return Map(result);
    }

    /// <summary>The requester or an approver withdraws the request before its run starts.</summary>
    public async Task<WorkspaceDeletionResult> CancelAsync(
        DeletionCaller caller, Guid deletionId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await GetAsync(caller, deletionId, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.NotFound);
        }

        if (current.Version != expectedVersion)
        {
            return new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.VersionConflict, current);
        }

        if (!current.Status.IsCancellable())
        {
            return new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.Conflict, current,
                $"The deletion is {current.Status}; only a request that has not started can be cancelled.");
        }

        var result = await store.CancelAsync(deletionId, expectedVersion, caller.Principal.UserId, time.GetUtcNow(),
            Audit(caller.Principal, current, AuditTaxonomy.Workspace.DeletionCancelled, new Dictionary<string, string?>
            {
                ["previousStatus"] = current.Status.ToString(),
            }), cancellationToken).ConfigureAwait(false);
        return Map(result);
    }

    /// <summary>The requester sees their request; holders of the approval permission see every deletion.</summary>
    public static bool CanSee(DeletionCaller caller, WorkspaceDeletion deletion)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(deletion);
        return caller.CanApprove || deletion.RequestedBy == caller.Principal.UserId;
    }

    public static bool CanCancel(DeletionCaller caller, WorkspaceDeletion deletion) =>
        CanSee(caller, deletion) && deletion.Status.IsCancellable();

    public static bool CanApproveDeletion(DeletionCaller caller, WorkspaceDeletion deletion, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(deletion);
        return caller.CanApprove && deletion.Status == WorkspaceDeletionStatus.Requested && deletion.ExpiresAt > now
            && deletion.RequestedBy != caller.Principal.UserId;
    }

    private static WorkspaceDeletionResult Map(DeletionWriteResult result) => result.Outcome switch
    {
        DeletionWriteOutcome.Ok => new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.Ok, result.Deletion),
        DeletionWriteOutcome.VersionConflict => new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.VersionConflict, result.Deletion),
        DeletionWriteOutcome.Conflict => new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.Conflict, result.Deletion,
            "The deletion changed state; reload it."),
        _ => new WorkspaceDeletionResult(WorkspaceDeletionResultStatus.NotFound),
    };

    /// <summary>Trimmed text; required or optional; one line unless <paramref name="multiline"/>. Errors keyed by field.</summary>
    private static string? Text(string? value, int max, bool multiline, string field, bool required, Dictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            if (required)
            {
                errors[field] = ["Required."];
            }

            return null;
        }

        if (trimmed.Length > max)
        {
            errors[field] = [$"At most {max.ToString(CultureInfo.InvariantCulture)} characters."];
        }
        else if (trimmed.Any(c => char.IsControl(c) && (!multiline || (c != '\n' && c != '\r' && c != '\t'))))
        {
            errors[field] = [multiline ? "Control characters are not allowed." : "One line without control characters."];
        }

        return trimmed;
    }

    /// <summary>The request's audit event, in the workspace's chain. IDs and enums only: the reason stays on the record (ADR-013 §7).</summary>
    private static AuditEvent Audit(SecurityPrincipal caller, WorkspaceDeletion deletion, string action, Dictionary<string, string?> details)
    {
        details["deletionId"] = deletion.DeletionId.ToString("D", CultureInfo.InvariantCulture);
        return new AuditEvent
        {
            WorkspaceId = deletion.WorkspaceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditTaxonomy.Workspace.Category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = caller.UserId.ToString(),
            ActorDisplay = caller.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? caller.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : caller.DisplayName,
            ClientIp = caller.ClientIp,
            UserAgent = caller.UserAgent,
            ResourceType = AuditTaxonomy.Workspace.DeletionResourceType,
            ResourceId = deletion.DeletionId.ToString("D", CultureInfo.InvariantCulture),
            Outcome = AuditOutcome.Success,
            CorrelationId = caller.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = details,
        };
    }
}

