using System.Diagnostics;
using System.Globalization;
using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Identity;
using Opportunity.Application.Snapshots;
using Opportunity.Application.Workspaces;
using Opportunity.Core.ReviewBatches;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.Application.ReviewBatches;

public enum ReviewBatchOutcomeStatus
{
    Ok,
    Created,
    NotFound,
    Invalid,
    Forbidden,

    /// <summary>The resource is not in a state the operation applies to: 409.</summary>
    Conflict,

    /// <summary>The batch changed since it was read: 412.</summary>
    VersionConflict,
}

/// <summary>A Batch Set as one caller sees it.</summary>
/// <param name="VisibleDocuments">Members the caller may see (Q-52).</param>
public sealed record ReviewBatchSetView(ReviewBatchSetRecord Set, long VisibleDocuments, string? CreatorName);

/// <summary>A batch as one caller sees it.</summary>
public sealed record ReviewBatchView(ReviewBatchRecord Batch, long VisibleDocuments, string? AssigneeName);

/// <summary>A conflict with the field and reviewer names resolved.</summary>
public sealed record ReviewConflictView(ReviewConflict Conflict, string FieldName, string? FirstPassReviewer, string? QcReviewer);

public sealed record ReviewBatchOutcome
{
    public required ReviewBatchOutcomeStatus Status { get; init; }

    public ReviewBatchSetView? Set { get; init; }

    public ReviewBatchView? Batch { get; init; }

    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();

    public string? Detail { get; init; }

    internal static ReviewBatchOutcome Of(ReviewBatchOutcomeStatus status, string? detail = null) => new() { Status = status, Detail = detail };

    internal static ReviewBatchOutcome Invalid(string key, string message) =>
        new() { Status = ReviewBatchOutcomeStatus.Invalid, Errors = new Dictionary<string, string[]> { [key] = [message] } };
}

/// <summary>Validated input of a new Batch Set.</summary>
public sealed record ReviewBatchSetRequest(
    string? Name,
    Guid SnapshotId,
    string? BatchPrefix,
    int MaxBatchSize,
    bool KeepFamiliesTogether,
    bool KeepThreadsTogether,
    ReviewPass Pass,
    Guid? QcOfBatchSetId,
    string? ReviewerGroup);

/// <summary>
/// Review batches (E10-T05; baseline §14, Q-53, familiarity guide §5.3): Batch Sets cut from a materialized ReviewBatch
/// snapshot (families, optionally threads, kept together; at most N documents per batch; frozen membership), batches
/// that move Available → Checked out (one reviewer) → Completed, manager assignment, and the first-pass vs QC conflicts
/// read from CodingEvent provenance. Every change is audited in its transaction. A batch is a candidate set, never a
/// grant (ADR-015 D5.8): reading members, counts and conflicts filters through the PDP for the caller (Q-52).
/// </summary>
public sealed class ReviewBatchService(
    IReviewBatchStore store,
    ICodingRepository coding,
    IDocumentSetSnapshotStore snapshots,
    IAuthorizationService authorization,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess,
    IUserDirectory users,
    IRoleAssignmentStore roles,
    TimeProvider time)
{
    public const int MaxLimit = 500;

    private const string CorrelationTag = "opportunity.correlation_id";

    public async Task<ReviewBatchOutcome> CreateSetAsync(
        SecurityPrincipal principal, Guid workspaceId, ReviewBatchSetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > ReviewBatchRules.MaxNameLength || name.Any(char.IsControl))
        {
            errors["name"] = [$"A name has 1 to {ReviewBatchRules.MaxNameLength} characters."];
        }

        var prefix = request.BatchPrefix?.Trim();
        if (!ReviewBatchRules.IsValidPrefix(prefix))
        {
            errors["batchPrefix"] =
                [$"A prefix has 1 to {ReviewBatchRules.MaxPrefixLength} letters, digits, spaces, dots, hyphens or underscores, starting with a letter or digit."];
        }

        if (request.MaxBatchSize is < 1 or > ReviewBatchRules.MaxBatchSize)
        {
            errors["maxBatchSize"] = [$"A batch holds 1 to {ReviewBatchRules.MaxBatchSize:N0} documents."];
        }

        var group = string.IsNullOrWhiteSpace(request.ReviewerGroup) ? null : request.ReviewerGroup.Trim();
        if (group is { Length: > ReviewBatchRules.MaxGroupNameLength })
        {
            errors["reviewerGroup"] = [$"A group name has at most {ReviewBatchRules.MaxGroupNameLength} characters."];
        }

        if (!Enum.IsDefined(request.Pass))
        {
            errors["reviewPass"] = ["The review pass is firstPass or qc."];
        }
        else if (request.Pass == ReviewPass.Qc && request.QcOfBatchSetId is null)
        {
            errors["qcOfBatchSetId"] = ["A QC Batch Set names the first-pass Batch Set it checks."];
        }
        else if (request.Pass == ReviewPass.FirstPass && request.QcOfBatchSetId is not null)
        {
            errors["qcOfBatchSetId"] = ["Only a QC Batch Set names a first-pass Batch Set."];
        }

        if (errors.Count > 0)
        {
            return new ReviewBatchOutcome { Status = ReviewBatchOutcomeStatus.Invalid, Errors = errors };
        }

        // The frozen set must be the caller's own ReviewBatch snapshot; anything else looks like a missing one.
        var snapshot = await snapshots.GetAsync(workspaceId, request.SnapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.CreatedBy != principal.UserId)
        {
            return ReviewBatchOutcome.Of(ReviewBatchOutcomeStatus.NotFound, "No such snapshot.");
        }

        if (snapshot.Purpose != SnapshotPurpose.ReviewBatch)
        {
            return ReviewBatchOutcome.Invalid("snapshotId", "Freeze the documents for review batches first (snapshot purpose ReviewBatch).");
        }

        if (snapshot.Status != SnapshotStatus.Ready)
        {
            return ReviewBatchOutcome.Of(ReviewBatchOutcomeStatus.Conflict, "The snapshot is not ready; wait until it is frozen.");
        }

        var creation = await store.CreateSetAsync(new NewReviewBatchSet
        {
            WorkspaceId = workspaceId,
            Name = name,
            BatchPrefix = prefix!,
            MaxBatchSize = request.MaxBatchSize,
            KeepFamiliesTogether = request.KeepFamiliesTogether || request.KeepThreadsTogether,
            KeepThreadsTogether = request.KeepThreadsTogether,
            Pass = request.Pass,
            QcOfBatchSetId = request.QcOfBatchSetId,
            ReviewerGroup = group,
            SnapshotId = snapshot.SnapshotId,
            CreatedBy = principal.UserId,
            Audit = set => Event(principal, workspaceId, AuditTaxonomy.Coding.BatchSetCreated, AuditTaxonomy.Coding.BatchSetResourceType,
                set.BatchSetId, new Dictionary<string, string?>
                {
                    ["snapshotId"] = set.SnapshotId.ToString(),
                    ["reviewPass"] = set.Pass.ToString(),
                    ["qcOfBatchSetId"] = set.QcOfBatchSetId?.ToString(),
                    ["maxBatchSize"] = Number(set.MaxBatchSize),
                    ["keepFamiliesTogether"] = set.KeepFamiliesTogether ? "true" : "false",
                    ["keepThreadsTogether"] = set.KeepThreadsTogether ? "true" : "false",
                    ["batchCount"] = Number(set.BatchCount),
                    ["documentCount"] = Number(set.DocumentCount),
                }),
        }, cancellationToken).ConfigureAwait(false);

        return creation.Status switch
        {
            ReviewBatchSetCreateStatus.Created => new ReviewBatchOutcome
            {
                Status = ReviewBatchOutcomeStatus.Created,
                Set = (await SetViewsAsync(principal, workspaceId, [creation.Set!], cancellationToken).ConfigureAwait(false))[0],
            },
            ReviewBatchSetCreateStatus.PrefixTaken => ReviewBatchOutcome.Of(ReviewBatchOutcomeStatus.Conflict,
                "Another Batch Set uses this batch prefix."),
            ReviewBatchSetCreateStatus.QcSourceNotFound => ReviewBatchOutcome.Of(ReviewBatchOutcomeStatus.NotFound, "No such batch set."),
            _ => ReviewBatchOutcome.Of(ReviewBatchOutcomeStatus.Conflict, "The snapshot is not ready; wait until it is frozen."),
        };
    }

    public async Task<ReviewBatchSetView?> GetSetAsync(SecurityPrincipal principal, Guid workspaceId, Guid batchSetId, CancellationToken cancellationToken = default)
    {
        var set = await store.GetSetAsync(workspaceId, batchSetId, cancellationToken).ConfigureAwait(false);
        return set is null ? null : (await SetViewsAsync(principal, workspaceId, [set], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<ReviewBatchSetView>> ListSetsAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid? after, int limit, CancellationToken cancellationToken = default)
    {
        var sets = await store.ListSetsAsync(workspaceId, after, limit, cancellationToken).ConfigureAwait(false);
        return await SetViewsAsync(principal, workspaceId, sets, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReviewBatchView?> GetBatchAsync(SecurityPrincipal principal, Guid workspaceId, Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await store.GetBatchAsync(workspaceId, batchId, cancellationToken).ConfigureAwait(false);
        return batch is null ? null : (await BatchViewsAsync(principal, workspaceId, [batch], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<ReviewBatchView>> ListBatchesAsync(
        SecurityPrincipal principal, Guid workspaceId, ReviewBatchFilter filter, ReviewBatchCursor? after, int limit,
        CancellationToken cancellationToken = default)
    {
        var batches = await store.ListBatchesAsync(workspaceId, filter, after, limit, cancellationToken).ConfigureAwait(false);
        return await BatchViewsAsync(principal, workspaceId, batches, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One page of a batch's members in review order (<paramref name="limit"/> stored members at most) without the ones
    /// the caller may not see; <c>Next</c> is the last stored position read when more may follow.
    /// </summary>
    public async Task<(IReadOnlyList<ReviewBatchMember> Members, int? Next)> ListMembersAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid batchId, int afterPosition, int limit, CancellationToken cancellationToken = default)
    {
        var page = await store.ListMembersAsync(workspaceId, batchId, afterPosition, limit + 1, cancellationToken).ConfigureAwait(false);
        var more = page.Count > limit;
        var members = more ? page.Take(limit).ToList() : page;
        if (members.Count == 0)
        {
            return ([], null);
        }

        var decisions = await authorization.AuthorizeManyAsync(principal, workspaceId, Permission.DocumentView,
            [.. members.Select(m => m.DocumentId)], DenialAudit.Summary, cancellationToken).ConfigureAwait(false);
        return ([.. members.Where(m => decisions.GetValueOrDefault(m.DocumentId).IsAllowed)], more ? members[^1].Position : null);
    }

    /// <summary>The caller takes an Available batch (Coding.Write; in the set's reviewer group unless a manager).</summary>
    public async Task<ReviewBatchOutcome> CheckOutAsync(
        SecurityPrincipal principal, Guid workspaceId, ReviewBatchRecord batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Status != ReviewBatchStatus.Available)
        {
            return ReviewBatchOutcome.Of(ReviewBatchOutcomeStatus.Conflict, "The batch is not available; it is checked out or completed.");
        }

        if (batch.ReviewerGroup is { } group && !principal.Groups.Contains(group, StringComparer.Ordinal)
            && !await IsManagerAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false))
        {
            return ReviewBatchOutcome.Of(ReviewBatchOutcomeStatus.Forbidden, "This Batch Set is for another reviewer group.");
        }

        return await TransitionAsync(principal, workspaceId, batch, ReviewBatchTransitionKind.CheckOut, principal.UserId,
            AuditTaxonomy.Coding.BatchCheckedOut, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The holder (or a manager) checks a batch in, done or not.</summary>
    public async Task<ReviewBatchOutcome> CheckInAsync(
        SecurityPrincipal principal, Guid workspaceId, ReviewBatchRecord batch, bool completed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Status != ReviewBatchStatus.CheckedOut)
        {
            return ReviewBatchOutcome.Of(ReviewBatchOutcomeStatus.Conflict, "The batch is not checked out.");
        }

        if (batch.AssigneeId != principal.UserId && !await IsManagerAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false))
        {
            return ReviewBatchOutcome.Of(ReviewBatchOutcomeStatus.Forbidden, "Another reviewer holds this batch.");
        }

        return await TransitionAsync(principal, workspaceId, batch,
            completed ? ReviewBatchTransitionKind.Complete : ReviewBatchTransitionKind.Return, null, AuditTaxonomy.Coding.BatchCheckedIn,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A manager (ReviewBatch.Manage, declared by the endpoint) checks the batch out to <paramref name="assigneeId"/> — a
    /// member who may code, in the set's reviewer group — whatever its status, or makes it Available (null).
    /// </summary>
    public async Task<ReviewBatchOutcome> AssignAsync(
        SecurityPrincipal principal, Guid workspaceId, ReviewBatchRecord batch, Guid? assigneeId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(batch);
        if (assigneeId is null && batch.Status == ReviewBatchStatus.Available)
        {
            return new ReviewBatchOutcome
            {
                Status = ReviewBatchOutcomeStatus.Ok,
                Batch = (await BatchViewsAsync(principal, workspaceId, [batch], cancellationToken).ConfigureAwait(false))[0],
            };
        }

        if (assigneeId is { } id)
        {
            const string NotEligible = "The assignee must be a member of this workspace who may code documents.";
            var user = await users.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return ReviewBatchOutcome.Invalid("assigneeId", NotEligible);
            }

            // Eligibility, not access: the assignee's roles (directly or through a group of their last sign-in) must grant
            // Coding.Write. Asking the PDP for another principal would audit a denial in the assignee's name; every action
            // the assignee then takes is decided by the PDP as usual.
            var assignments = await roles.ReadAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            var held = assignments is null
                ? []
                : user.Groups.Select(RolePrincipal.Group).Append(RolePrincipal.User(user.UserId)).SelectMany(assignments.RolesOf).ToHashSet();
            if (!held.Any(r => r.Grants(Permission.CodingWrite)))
            {
                return ReviewBatchOutcome.Invalid("assigneeId", NotEligible);
            }

            if (batch.ReviewerGroup is { } group && !user.Groups.Contains(group, StringComparer.Ordinal))
            {
                return ReviewBatchOutcome.Invalid("assigneeId", "The assignee is not in this Batch Set's reviewer group.");
            }
        }

        return await TransitionAsync(principal, workspaceId, batch, ReviewBatchTransitionKind.Assign, assigneeId,
            AuditTaxonomy.Coding.BatchAssigned, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Conflicts of a QC Batch Set with its first pass, by (document, field): documents the caller may not see and fields
    /// hidden from the caller (or deleted) are left out. <c>Next</c> is set when more stored conflicts may follow.
    /// </summary>
    public async Task<(IReadOnlyList<ReviewConflictView> Conflicts, ReviewConflictCursor? Next)> GetConflictsAsync(
        SecurityPrincipal principal, Guid workspaceId, ReviewBatchSetRecord set, ReviewConflictCursor? after, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(set);
        if (set.Pass != ReviewPass.Qc || set.QcOfBatchSetId is not { } firstPass)
        {
            return ([], null);
        }

        var catalog = await fields.GetCatalogAsync(workspaceId, includeDeleted: true, cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(workspaceId, principal, catalog, cancellationToken).ConfigureAwait(false);
        var excluded = catalog.Fields.Where(f => f.IsDeleted).Select(f => f.FieldId).Concat(restricted).ToHashSet();
        var rows = await coding.GetReviewConflictsAsync(workspaceId, set.BatchSetId, firstPass, excluded, after, limit + 1, cancellationToken).ConfigureAwait(false);
        var more = rows.Count > limit;
        var page = more ? rows.Take(limit).ToList() : rows;
        if (page.Count == 0)
        {
            return ([], null);
        }

        var decisions = await authorization.AuthorizeManyAsync(principal, workspaceId, Permission.DocumentView,
            [.. page.Select(c => c.DocumentId).Distinct()], DenialAudit.Summary, cancellationToken).ConfigureAwait(false);
        var visible = page.Where(c => decisions.GetValueOrDefault(c.DocumentId).IsAllowed && catalog.Find(c.FieldId) is not null).ToList();
        var names = await NamesAsync(visible.SelectMany(c => new[] { c.FirstPass.ReviewerId, c.Qc.ReviewerId }), cancellationToken).ConfigureAwait(false);
        var views = visible
            .Select(c => new ReviewConflictView(c, catalog.Find(c.FieldId)!.Name, names.GetValueOrDefault(c.FirstPass.ReviewerId), names.GetValueOrDefault(c.Qc.ReviewerId)))
            .ToList();
        return (views, more ? new ReviewConflictCursor(page[^1].DocumentId, page[^1].FieldId) : null);
    }

    private async Task<ReviewBatchOutcome> TransitionAsync(
        SecurityPrincipal principal, Guid workspaceId, ReviewBatchRecord batch, ReviewBatchTransitionKind kind, Guid? assigneeId, string action,
        CancellationToken cancellationToken)
    {
        var result = await store.TransitionAsync(new ReviewBatchTransition
        {
            WorkspaceId = workspaceId,
            BatchId = batch.BatchId,
            ExpectedVersion = batch.Version,
            Kind = kind,
            ActorId = principal.UserId,
            AssigneeId = assigneeId,
            Audit = (before, after) => Event(principal, workspaceId, action, AuditTaxonomy.Coding.BatchResourceType, after.BatchId,
                new Dictionary<string, string?>
                {
                    ["batchSetId"] = after.BatchSetId.ToString(),
                    ["batchName"] = after.Name,
                    ["statusBefore"] = before.Status.ToString(),
                    ["statusAfter"] = after.Status.ToString(),
                    ["assigneeBefore"] = before.AssigneeId?.ToString(),
                    ["assigneeAfter"] = after.AssigneeId?.ToString(),
                    ["version"] = Number(after.Version),
                }),
        }, cancellationToken).ConfigureAwait(false);

        return result.Status switch
        {
            ReviewBatchTransitionStatus.Ok => new ReviewBatchOutcome
            {
                Status = ReviewBatchOutcomeStatus.Ok,
                Batch = (await BatchViewsAsync(principal, workspaceId, [result.Batch!], cancellationToken).ConfigureAwait(false))[0],
            },
            ReviewBatchTransitionStatus.VersionConflict => new ReviewBatchOutcome
            {
                Status = ReviewBatchOutcomeStatus.VersionConflict,
                Batch = (await BatchViewsAsync(principal, workspaceId, [result.Batch!], cancellationToken).ConfigureAwait(false))[0],
            },
            ReviewBatchTransitionStatus.InvalidState => ReviewBatchOutcome.Of(ReviewBatchOutcomeStatus.Conflict,
                "The batch's status no longer allows this change."),
            _ => ReviewBatchOutcome.Of(ReviewBatchOutcomeStatus.NotFound),
        };
    }

    private async Task<bool> IsManagerAsync(SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken) =>
        (await authorization.AuthorizeAsync(principal, workspaceId, Permission.ReviewBatchManage, cancellationToken).ConfigureAwait(false)).IsAllowed;

    private async Task<IReadOnlyList<ReviewBatchSetView>> SetViewsAsync(
        SecurityPrincipal principal, Guid workspaceId, IReadOnlyList<ReviewBatchSetRecord> sets, CancellationToken cancellationToken)
    {
        if (sets.Count == 0)
        {
            return [];
        }

        var counts = await VisibleCountsAsync(principal, workspaceId, sets.ToDictionary(s => s.BatchSetId, s => s.DocumentCount), bySet: true,
            cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(sets.Select(s => s.CreatedBy), cancellationToken).ConfigureAwait(false);
        return [.. sets.Select(s => new ReviewBatchSetView(s, counts.GetValueOrDefault(s.BatchSetId), names.GetValueOrDefault(s.CreatedBy)))];
    }

    private async Task<IReadOnlyList<ReviewBatchView>> BatchViewsAsync(
        SecurityPrincipal principal, Guid workspaceId, IReadOnlyList<ReviewBatchRecord> batches, CancellationToken cancellationToken)
    {
        if (batches.Count == 0)
        {
            return [];
        }

        var counts = await VisibleCountsAsync(principal, workspaceId, batches.ToDictionary(b => b.BatchId, b => (long)b.DocumentCount), bySet: false,
            cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(batches.Where(b => b.AssigneeId is not null).Select(b => b.AssigneeId!.Value), cancellationToken).ConfigureAwait(false);
        return [.. batches.Select(b => new ReviewBatchView(b, counts.GetValueOrDefault(b.BatchId),
            b.AssigneeId is { } a ? names.GetValueOrDefault(a) : null))];
    }

    /// <summary>
    /// Q-52: counts never include documents the caller may not see. Without denied classes or walls (the common case)
    /// the frozen counts are the visible ones; otherwise PostgreSQL counts with the caller's visibility filter.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, long>> VisibleCountsAsync(
        SecurityPrincipal principal, Guid workspaceId, Dictionary<Guid, long> frozen, bool bySet, CancellationToken cancellationToken)
    {
        var visibility = await authorization.GetVisibilityAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        if (visibility.Filter is not { } filter)
        {
            return frozen.ToDictionary(c => c.Key, _ => 0L);
        }

        if (filter.DeniedClasses.Count == 0 && filter.WallIds.Count == 0)
        {
            return frozen;
        }

        return await store.CountVisibleAsync(workspaceId, frozen.Keys, bySet, filter, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        return ids.Count == 0 ? new Dictionary<Guid, string>() : await users.GetDisplayNamesAsync(ids, cancellationToken).ConfigureAwait(false);
    }

    private AuditEvent Event(
        SecurityPrincipal actor, Guid workspaceId, string action, string resourceType, Guid resourceId, Dictionary<string, string?> details) => new()
        {
            WorkspaceId = workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Coding.Category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = actor.UserId.ToString(),
            ActorDisplay = actor.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
            ? actor.DisplayName[..AuditEventRules.MaxActorDisplayLength]
            : actor.DisplayName,
            ClientIp = actor.ClientIp,
            UserAgent = actor.UserAgent,
            ResourceType = resourceType,
            ResourceId = resourceId.ToString(),
            Outcome = AuditOutcome.Success,
            CorrelationId = actor.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = details,
        };

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
