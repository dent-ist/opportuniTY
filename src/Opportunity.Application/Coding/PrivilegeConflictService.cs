using System.Diagnostics;
using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Identity;
using Opportunity.Application.Productions;
using Opportunity.Application.Search;
using Opportunity.Application.Snapshots;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.Application.Coding;

/// <summary>
/// Family and duplicate privilege inconsistencies (E13-T02, §14 conflict detection, legal finding 8, Q-14):
/// <list type="bullet">
/// <item>The on-demand report: families with a member coded Privilege Status = Withhold next to members that are not
/// (and, optionally, families whose members' responsiveness calls differ), and duplicate groups whose members' privilege
/// calls differ (<see cref="PrivilegeConflictRules"/>), with each member's values and the reviewers who set them. The
/// store finds the groups set-based over every document; the report then keeps only the members the caller may see and
/// only the groups still in conflict among them, so a conflict that needs a hidden document leaves no trace: no
/// placeholder, no count (Q-52, Q-13).</item>
/// <item>"Propagate privilege call to duplicates": for chosen duplicate groups, copy one member's privilege fields to the
/// group's other members the caller may code, as one bulk coding job over a frozen set (Q-07 skip rule per chunk) whose
/// CodingEvents reference the source's originating edits; audited as <c>Coding.FamilyApplied</c>.</item>
/// </list>
/// Authorization: the report needs <c>PrivilegeLog.Generate</c> (PEP-1; plus <c>Production.Create</c> to scope it to a
/// production), the propagation <c>Coding.Bulk</c> (PEP-1) and <c>Coding.WritePrivilege</c>.
/// </summary>
public sealed class PrivilegeConflictService(
    ICodingRepository coding,
    ICodingPropagationRepository relationships,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess,
    IAuthorizationService authorization,
    IUserDirectory users,
    IProductionStore productions,
    DocumentSetSnapshotService snapshots,
    BulkCodingService bulk,
    TimeProvider time)
{
    /// <summary>Conflict groups read per kind; the report says when there were more.</summary>
    public const int MaxGroups = 1_000;

    /// <summary>The most documents one propagation may change.</summary>
    public const int MaxTargets = 100_000;

    private const int Batch = 5_000;

    /// <summary>The fields a propagation copies when the request names none: the privilege call itself.</summary>
    public static IReadOnlyList<int> DefaultPropagatedFields { get; } = [PrivilegeFields.Status, PrivilegeFields.Basis];

    public async Task<PrivilegeConflictReportOutcome> ReportAsync(
        SecurityPrincipal principal, Guid workspaceId, int? responsivenessFieldId, Guid? productionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (productionId is { } production)
        {
            if (!(await authorization.AuthorizeAsync(principal, workspaceId, Permission.ProductionCreate, cancellationToken).ConfigureAwait(false)).IsAllowed)
            {
                return PrivilegeConflictReportOutcome.Of(PrivilegeConflictStatus.Forbidden);
            }

            if (await productions.GetAsync(workspaceId, production, cancellationToken).ConfigureAwait(false) is null)
            {
                return PrivilegeConflictReportOutcome.Of(PrivilegeConflictStatus.NotFound);
            }
        }

        var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(workspaceId, principal, catalog, cancellationToken).ConfigureAwait(false);
        if (catalog.Find(PrivilegeFields.Status) is not { IsSystem: true, IsDeleted: false } || restricted.Contains(PrivilegeFields.Status))
        {
            // Privilege Status hidden from the caller's roles: the report would be about values they may not read.
            return PrivilegeConflictReportOutcome.Of(PrivilegeConflictStatus.Forbidden);
        }

        if (responsivenessFieldId is { } fieldId
            && (catalog.Find(fieldId) is not { IsDeleted: false, Storage: FieldStorage.Coding, Type: FieldType.SingleChoice } || restricted.Contains(fieldId)))
        {
            return PrivilegeConflictReportOutcome.Of(PrivilegeConflictStatus.Invalid,
                new FieldError("responsivenessField", "invalid-field", "The responsiveness field must be a single-choice coding field of this workspace."));
        }

        var candidates = await coding.FindPrivilegeConflictsAsync(
            new PrivilegeConflictQuery(workspaceId, responsivenessFieldId, productionId, MaxGroups), cancellationToken).ConfigureAwait(false);

        // Q-52: only members the caller may open take part; a group is reported when they alone are in conflict.
        var memberIds = candidates.Groups.SelectMany(g => g.Members).Select(m => m.DocumentId).Distinct().ToList();
        var visible = new HashSet<Guid>();
        foreach (var batch in memberIds.Chunk(Batch))
        {
            var decisions = await authorization.AuthorizeManyAsync(principal, workspaceId, Permission.DocumentView, batch, DenialAudit.Summary, cancellationToken)
                .ConfigureAwait(false);
            visible.UnionWith(batch.Where(id => decisions.TryGetValue(id, out var d) && d.IsAllowed));
        }

        var withhold = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, PrivilegeFields.Keys.Withhold);
        var notPrivileged = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, PrivilegeFields.Keys.NotPrivileged);
        var basisHidden = restricted.Contains(PrivilegeFields.Basis);
        var groups = new List<PrivilegeConflictGroup>();
        foreach (var candidate in candidates.Groups)
        {
            var members = candidate.Members.Where(m => visible.Contains(m.DocumentId))
                .Select(m => basisHidden ? m with { Basis = PrivilegeCodedValue.Empty } : m)
                .ToList();
            if (productionId is not null && !members.Any(m => m.InProduction))
            {
                continue;
            }

            var reasons = PrivilegeConflictRules.Evaluate(candidate.Kind, members, withhold, notPrivileged, responsivenessFieldId is not null);
            if (reasons != PrivilegeConflictReasons.None)
            {
                groups.Add(new PrivilegeConflictGroup(candidate.Kind, candidate.GroupId, reasons, members));
            }
        }

        var editors = groups.SelectMany(g => g.Members)
            .SelectMany(m => new[] { m.Status.ChangedBy, m.Basis.ChangedBy, m.Responsiveness.ChangedBy })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var names = editors.Count == 0
            ? new Dictionary<Guid, string>()
            : await users.GetDisplayNamesAsync(editors, cancellationToken).ConfigureAwait(false);
        return new PrivilegeConflictReportOutcome
        {
            Status = PrivilegeConflictStatus.Ok,
            Report = new PrivilegeConflictReport(time.GetUtcNow(), catalog, responsivenessFieldId, productionId, groups, names, candidates.Truncated),
        };
    }

    public async Task<PrivilegePropagationOutcome> PropagateAsync(
        CodingCaller caller, PrivilegePropagationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (Validate(request) is { } shape)
        {
            return PrivilegePropagationOutcome.Of(PrivilegeConflictStatus.Invalid, shape);
        }

        var principal = caller.Principal;
        var ws = caller.WorkspaceId;
        foreach (var permission in new[] { Permission.CodingBulk, Permission.CodingWritePrivilege })
        {
            var decision = await authorization.AuthorizeAsync(principal, ws, permission, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return PrivilegePropagationOutcome.Of(
                    decision.Outcome == AuthorizationOutcome.NotFound ? PrivilegeConflictStatus.NotFound : PrivilegeConflictStatus.Forbidden);
            }
        }

        var fieldIds = request.FieldIds.Count == 0 ? DefaultPropagatedFields : request.FieldIds;
        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(ws, principal, catalog, cancellationToken).ConfigureAwait(false);
        var readOnly = await fieldAccess.ReadOnlyFieldIdsAsync(ws, principal, catalog, cancellationToken).ConfigureAwait(false);
        var fieldErrors = fieldIds
            .Where(id => catalog.Find(id) is not { IsSystem: true, IsDeleted: false } || restricted.Contains(id) || readOnly.Contains(id))
            .Select(id => new FieldError(FieldKey.For(id), "unavailable-field", "The privilege field cannot be coded by you in this workspace."))
            .ToArray();
        if (fieldErrors.Length > 0)
        {
            return PrivilegePropagationOutcome.Of(PrivilegeConflictStatus.Invalid, fieldErrors);
        }

        // Every source must be a document the caller may open; a hidden or foreign one is "no such document".
        var sources = request.Groups.Select(g => g.SourceDocumentId).Distinct().ToList();
        var sourceDecisions = await authorization.AuthorizeManyAsync(principal, ws, Permission.DocumentView, sources, DenialAudit.Summary, cancellationToken)
            .ConfigureAwait(false);
        if (sources.Any(id => !sourceDecisions.TryGetValue(id, out var d) || !d.IsAllowed))
        {
            return PrivilegePropagationOutcome.Of(PrivilegeConflictStatus.NotFound);
        }

        var members = await relationships.GetDuplicateGroupMembersAsync(ws, [.. request.Groups.Select(g => g.DuplicateGroupId)], MaxTargets, cancellationToken)
            .ConfigureAwait(false);
        if (members.Count > MaxTargets)
        {
            return PrivilegePropagationOutcome.Of(PrivilegeConflictStatus.Invalid, new FieldError("groups", "too-many-targets",
                $"These groups have more than {MaxTargets.ToString("N0", CultureInfo.InvariantCulture)} documents; propagate fewer groups at a time."));
        }

        var byGroup = members.ToLookup(m => m.DuplicateGroupId, m => m.Member);
        var errors = new List<FieldError>();
        for (var i = 0; i < request.Groups.Count; i++)
        {
            var group = request.Groups[i];
            if (!byGroup[group.DuplicateGroupId].Any(m => m.DocumentId == group.SourceDocumentId))
            {
                errors.Add(new FieldError($"groups[{i}].sourceDocumentId", "not-in-group", "The source document is not a member of that duplicate group."));
            }
        }

        if (errors.Count > 0)
        {
            return new PrivilegePropagationOutcome { Status = PrivilegeConflictStatus.Invalid, Errors = errors };
        }

        // Targets: the other members the caller may code; hidden ones are never targeted or counted (Q-52).
        var sourceSet = sources.ToHashSet();
        var candidates = members.Select(m => m.Member.DocumentId).Where(id => !sourceSet.Contains(id)).Distinct().ToList();
        var targets = new List<Guid>(candidates.Count);
        foreach (var batch in candidates.Chunk(Batch))
        {
            var decisions = await authorization.AuthorizeManyAsync(principal, ws, Permission.CodingWrite, batch, DenialAudit.Summary, cancellationToken)
                .ConfigureAwait(false);
            targets.AddRange(batch.Where(id => decisions.TryGetValue(id, out var d) && d.IsAllowed));
        }

        if (targets.Count == 0)
        {
            return PrivilegePropagationOutcome.Of(PrivilegeConflictStatus.Invalid,
                new FieldError("groups", "no-targets", "These groups have no other document you can code."));
        }

        var states = await coding.GetFieldStatesAsync(ws, sources, fieldIds, cancellationToken).ConfigureAwait(false);
        var origins = await coding.GetLatestChangeEventIdsAsync(ws, sources, fieldIds, cancellationToken).ConfigureAwait(false);
        var entries = request.Groups.Select(g =>
        {
            var state = states.GetValueOrDefault(g.SourceDocumentId);
            var events = origins.GetValueOrDefault(g.SourceDocumentId);
            return new GroupPropagationEntry(
                g.DuplicateGroupId,
                g.SourceDocumentId,
                [.. fieldIds.Order().Select(f => CodingFieldOperation.Set(f, state is not null && state.TryGetValue(f, out var s) ? s.Value?.DeepClone() : null))],
                events is null ? new Dictionary<int, Guid>() : fieldIds.Where(events.ContainsKey).ToDictionary(f => f, f => events[f]));
        }).ToList();

        var key = "privilege-propagation:" + request.IdempotencyKey;
        var name = "Propagate privilege call " + time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        var frozen = await snapshots.FreezeSelectionAsync(
            new SearchCaller(principal, ws, null), SnapshotPurpose.BulkCoding, name, targets, Permission.CodingWrite, key, cancellationToken).ConfigureAwait(false);
        if (frozen.Status != SnapshotCreateStatus.Ready || frozen.Snapshot is not { DocumentCount: > 0 } snapshot)
        {
            return PrivilegePropagationOutcome.Of(PrivilegeConflictStatus.Invalid,
                new FieldError("groups", "no-targets", "These groups have no other document you can code."));
        }

        var submitted = await bulk.SubmitGroupedPropagationAsync(caller, snapshot, entries, securityAffecting: true, request.IdempotencyKey,
            Audit(principal, ws, entries, targets.Count, fieldIds), cancellationToken).ConfigureAwait(false);
        return submitted.Status switch
        {
            BulkCodingSubmitStatus.Accepted => new PrivilegePropagationOutcome { Status = PrivilegeConflictStatus.Ok, Job = submitted.Job },
            BulkCodingSubmitStatus.IdempotencyKeyReuse => PrivilegePropagationOutcome.Of(PrivilegeConflictStatus.IdempotencyKeyReuse),
            _ => PrivilegePropagationOutcome.Of(PrivilegeConflictStatus.Invalid,
                new FieldError("groups", "not-ready", "The documents could not be frozen for the job; try again.")),
        };
    }

    private static FieldError? Validate(PrivilegePropagationRequest request)
    {
        if (request.Groups is null || request.Groups.Count is 0 or > BulkCodingParameters.MaxGroups)
        {
            return new FieldError("groups", "invalid-groups", $"Give 1–{BulkCodingParameters.MaxGroups} duplicate groups.");
        }

        if (request.Groups.Any(g => g is null || g.DuplicateGroupId == Guid.Empty || g.SourceDocumentId == Guid.Empty))
        {
            return new FieldError("groups", "invalid-groups", "Each group needs its duplicateGroupId and sourceDocumentId.");
        }

        if (request.Groups.Select(g => g.DuplicateGroupId).Distinct().Count() != request.Groups.Count)
        {
            return new FieldError("groups", "duplicate-group", "Each duplicate group may be named once.");
        }

        if (request.FieldIds is null || request.FieldIds.Any(f => !PrivilegeFields.IsPrivilegeField(f))
            || request.FieldIds.Distinct().Count() != request.FieldIds.Count)
        {
            return new FieldError("fields", "invalid-fields", "Name distinct privilege fields (Privilege Status, Basis, Description, Attorneys Involved, Log Category).");
        }

        if (string.IsNullOrEmpty(request.IdempotencyKey) || request.IdempotencyKey.Length > BulkCodingService.MaxIdempotencyKeyLength)
        {
            return new FieldError("idempotencyKey", "invalid-idempotency-key",
                $"An idempotency key has 1–{BulkCodingService.MaxIdempotencyKeyLength} characters.");
        }

        return null;
    }

    private AuditEvent Audit(SecurityPrincipal principal, Guid workspaceId, List<GroupPropagationEntry> groups, int targets, IReadOnlyList<int> fieldIds) => new()
    {
        OccurredAt = time.GetUtcNow(),
        WorkspaceId = workspaceId,
        Category = AuditTaxonomy.Coding.Category,
        Action = AuditTaxonomy.Coding.FamilyApplied,
        ActorType = AuditActorType.User,
        ActorId = principal.UserId.ToString(),
        ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
        ClientIp = principal.ClientIp,
        UserAgent = principal.UserAgent,
        Outcome = AuditOutcome.Success,
        CorrelationId = principal.CorrelationId ?? Activity.Current?.TraceId.ToHexString(),
        Details = new Dictionary<string, string?>
        {
            ["Scope"] = nameof(CodingPropagationScope.Duplicates),
            ["Origin"] = "PrivilegeConflicts",
            ["Mode"] = nameof(CodingPropagationMode.Job),
            ["Groups"] = groups.Count.ToString(CultureInfo.InvariantCulture),
            ["Targets"] = targets.ToString(CultureInfo.InvariantCulture),
            ["PropagatedFields"] = string.Join(',', fieldIds.Order()),
            ["SourceDocuments"] = string.Join(',', groups.Take(50).Select(g => g.SourceDocumentId)),
        },
    };
}
