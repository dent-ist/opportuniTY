using System.Diagnostics;
using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Search;
using Opportunity.Application.Snapshots;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.Application.Coding;

/// <summary>
/// "Apply to family / duplicates" (E09-T05, Q-14, Q-48): copies the source document's current values of chosen coding
/// fields to its family and/or duplicates. Never automatic: a preview shows the targets and the conflicts (targets
/// already coded differently) and is stored; applying it writes exactly what it showed, or is refused as stale when it
/// is older than <see cref="CodingPropagationOptions.PreviewLifetime"/> or the source's coding of those fields changed.
/// Up to <see cref="CodingPropagationOptions.InteractiveThreshold"/> targets the apply is one coding write (actor
/// <see cref="CodingActorType.SystemRule"/>, a target changed since the preview is skipped); above it a bulk coding job
/// over a materialized snapshot of the targets (actor <see cref="CodingActorType.BulkHuman"/>, Q-07 skip rule per
/// chunk). Either way every CodingEvent references the source's originating CodingEvent of its field and the action is
/// audited as <c>Coding.FamilyApplied</c>.
/// Authorization: <c>Coding.Write</c> (PEP-1), <c>Document.View</c> on the source, <c>Coding.WritePrivilege</c> for
/// security-affecting fields, and every target is authorized for <c>Coding.Write</c>: documents the caller cannot see
/// are omitted entirely, never targeted, listed or counted (Q-52, Q-13).
/// </summary>
public sealed class CodingPropagationService(
    ICodingPropagationRepository store,
    ICodingRepository coding,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess,
    IAuthorizationService authorization,
    IAuditEventWriter audit,
    DocumentSetSnapshotService snapshots,
    BulkCodingService bulk,
    CodingPropagationOptions options,
    TimeProvider time)
{
    public const int MaxListedConflicts = 100;

    /// <summary>The <c>code</c> of the 409 problem of a stale preview (wave-12 contract).</summary>
    public const string PreviewStaleCode = "PREVIEW_STALE";

    private const int Batch = 5_000;

    public async Task<CodingPropagationPreviewOutcome> PreviewAsync(
        CodingCaller caller, Guid sourceDocumentId, CodingPropagationScope scope, IReadOnlyList<int> fieldIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(fieldIds);
        if (!Enum.IsDefined(scope))
        {
            return CodingPropagationPreviewOutcome.Of(CodingPropagationStatus.Invalid, new FieldError("scope", "invalid-scope", "scope must be family, duplicates or familyAndDuplicates."));
        }

        if (fieldIds.Count is 0 or > CodingWriteRequest.MaxOperations || fieldIds.Distinct().Count() != fieldIds.Count)
        {
            return CodingPropagationPreviewOutcome.Of(CodingPropagationStatus.Invalid,
                new FieldError("fields", "invalid-fields", $"Give 1–{CodingWriteRequest.MaxOperations} distinct coding fields."));
        }

        var principal = caller.Principal;
        var ws = caller.WorkspaceId;
        var source = await authorization.AuthorizeAsync(principal, ws, Permission.DocumentView, sourceDocumentId, cancellationToken).ConfigureAwait(false);
        if (!source.IsAllowed)
        {
            return CodingPropagationPreviewOutcome.Of(Denied(source));
        }

        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(ws, principal, catalog, cancellationToken).ConfigureAwait(false);
        var readOnly = await fieldAccess.ReadOnlyFieldIdsAsync(ws, principal, catalog, cancellationToken).ConfigureAwait(false);
        var errors = new List<FieldError>();
        var definitions = new List<FieldDefinition>(fieldIds.Count);
        foreach (var fieldId in fieldIds)
        {
            var field = catalog.Find(fieldId);
            if (field is null || field.IsDeleted || restricted.Contains(fieldId))
            {
                errors.Add(new FieldError(FieldKey.For(fieldId), "unknown-field", "The field does not exist in this workspace."));
            }
            else if (field.Storage != FieldStorage.Coding)
            {
                errors.Add(new FieldError(field.Key, "not-coding-field", $"{field.Name} is imported data and cannot be propagated."));
            }
            else if (readOnly.Contains(fieldId))
            {
                errors.Add(new FieldError(field.Key, "read-only-field", $"{field.Name} is read-only for your roles."));
            }
            else
            {
                definitions.Add(field);
            }
        }

        if (errors.Count > 0)
        {
            return new CodingPropagationPreviewOutcome { Status = CodingPropagationStatus.Invalid, Errors = errors };
        }

        var security = definitions.Any(f => f.IsSecurityAffecting);
        if (security && await PrivilegeDeniedAsync(principal, ws, cancellationToken).ConfigureAwait(false) is { } privilege)
        {
            return CodingPropagationPreviewOutcome.Of(privilege);
        }

        var ids = definitions.Select(f => f.FieldId).ToList();
        var sourceStates = await coding.GetFieldStatesAsync(ws, [sourceDocumentId], ids, cancellationToken).ConfigureAwait(false);
        if (!sourceStates.TryGetValue(sourceDocumentId, out var sourceState))
        {
            return CodingPropagationPreviewOutcome.Of(CodingPropagationStatus.NotFound);
        }

        var origins = await coding.GetLatestChangeEventIdsAsync(ws, sourceDocumentId, ids, cancellationToken).ConfigureAwait(false);
        var values = definitions.Select(f => sourceState.TryGetValue(f.FieldId, out var s)
                ? new PropagationFieldValue(f.FieldId, s.Value?.DeepClone(), s.ChangedAtVersion, origins.TryGetValue(f.FieldId, out var o) ? o : null)
                : new PropagationFieldValue(f.FieldId, null, 0, null))
            .ToList();

        var candidates = await store.GetCandidatesAsync(ws, sourceDocumentId, scope, options.MaxTargets, cancellationToken).ConfigureAwait(false);
        if (candidates is null)
        {
            return CodingPropagationPreviewOutcome.Of(CodingPropagationStatus.NotFound);
        }

        if (candidates.Truncated)
        {
            return CodingPropagationPreviewOutcome.Of(CodingPropagationStatus.TooLarge, new FieldError("scope", "too-many-targets",
                $"More than {options.MaxTargets.ToString("N0", CultureInfo.InvariantCulture)} related documents; code them with Mass Edit instead."));
        }

        // Targets: the related documents the caller may code. Hidden ones are omitted entirely (Q-52, Q-13).
        var targets = new List<PropagationCandidate>(candidates.Candidates.Count);
        foreach (var batch in candidates.Candidates.Chunk(Batch))
        {
            var decisions = await authorization.AuthorizeManyAsync(
                principal, ws, Permission.CodingWrite, [.. batch.Select(c => c.DocumentId)], DenialAudit.Summary, cancellationToken).ConfigureAwait(false);
            foreach (var candidate in batch)
            {
                if (decisions.TryGetValue(candidate.DocumentId, out var decision) && decision.IsAllowed)
                {
                    targets.Add(candidate);
                }
            }
        }

        var conflicts = new List<PropagationConflict>();
        var conflictCount = 0;
        foreach (var batch in targets.Chunk(Batch))
        {
            var states = await coding.GetFieldStatesAsync(ws, [.. batch.Select(t => t.DocumentId)], ids, cancellationToken).ConfigureAwait(false);
            foreach (var target in batch)
            {
                var state = states.GetValueOrDefault(target.DocumentId);
                var conflict = false;
                foreach (var value in values)
                {
                    var current = state is not null && state.TryGetValue(value.FieldId, out var s) ? s.Value : null;
                    if (FieldValues.AreEqual(current, value.Value))
                    {
                        continue;
                    }

                    if (current is null || current is System.Text.Json.Nodes.JsonArray { Count: 0 })
                    {
                        continue;
                    }

                    conflict = true;
                    if (conflicts.Count < MaxListedConflicts)
                    {
                        conflicts.Add(new PropagationConflict(target.DocumentId, target.ControlNumber, catalog.Find(value.FieldId)!, current, value.Value));
                    }
                }

                conflictCount += conflict ? 1 : 0;
            }
        }

        var threshold = options.InteractiveThreshold;
        var mode = targets.Count > threshold ? CodingPropagationMode.Job : CodingPropagationMode.Interactive;
        var preview = new CodingPropagationPreview
        {
            WorkspaceId = ws,
            PreviewId = Guid.CreateVersion7(),
            CreatedBy = principal.UserId,
            CreatedAt = time.GetUtcNow(),
            SourceDocumentId = sourceDocumentId,
            Scope = scope,
            Fields = values,
            SecurityAffecting = security,
            TargetIds = [.. targets.Select(t => t.DocumentId)],
            TargetVersions = mode == CodingPropagationMode.Interactive ? [.. targets.Select(t => t.DocumentVersion)] : [],
            Mode = mode,
            Threshold = threshold,
        };
        await store.SaveAsync(preview, cancellationToken).ConfigureAwait(false);
        return new CodingPropagationPreviewOutcome
        {
            Status = CodingPropagationStatus.Ok,
            Preview = preview,
            TargetCount = targets.Count,
            ConflictCount = conflictCount,
            Conflicts = conflicts,
            // Q-07 skips happen only at apply.
            SkippedCount = 0,
            Catalog = catalog,
        };
    }

    public async Task<CodingPropagationApplyOutcome> ApplyAsync(CodingCaller caller, Guid previewId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var principal = caller.Principal;
        var ws = caller.WorkspaceId;
        var preview = await store.GetAsync(ws, previewId, cancellationToken).ConfigureAwait(false);
        if (preview is null || preview.CreatedBy != principal.UserId)
        {
            return CodingPropagationApplyOutcome.Of(CodingPropagationStatus.NotFound);
        }

        if (time.GetUtcNow() - preview.CreatedAt > options.PreviewLifetime)
        {
            return CodingPropagationApplyOutcome.Of(CodingPropagationStatus.Stale);
        }

        if (preview.SecurityAffecting && await PrivilegeDeniedAsync(principal, ws, cancellationToken).ConfigureAwait(false) is { } privilege)
        {
            return CodingPropagationApplyOutcome.Of(privilege);
        }

        var source = await authorization.AuthorizeAsync(principal, ws, Permission.DocumentView, preview.SourceDocumentId, cancellationToken)
            .ConfigureAwait(false);
        if (!source.IsAllowed)
        {
            return CodingPropagationApplyOutcome.Of(Denied(source));
        }

        // Stale when the source's coding of a propagated field changed since the preview read it.
        var fieldIds = preview.Fields.Select(f => f.FieldId).ToList();
        var now = await coding.GetFieldStatesAsync(ws, [preview.SourceDocumentId], fieldIds, cancellationToken).ConfigureAwait(false);
        if (!now.TryGetValue(preview.SourceDocumentId, out var sourceState)
            || preview.Fields.Any(f => (sourceState.TryGetValue(f.FieldId, out var s) ? s.ChangedAtVersion : 0) != f.ChangedAtVersion))
        {
            return CodingPropagationApplyOutcome.Of(CodingPropagationStatus.Stale);
        }

        var operations = preview.Fields.Select(f => CodingFieldOperation.Set(f.FieldId, f.Value?.DeepClone())).ToList();
        var origins = preview.Fields.Where(f => f.OriginEventId is not null).ToDictionary(f => f.FieldId, f => f.OriginEventId!.Value);
        var key = "propagation:" + preview.PreviewId.ToString("N");
        return preview.Mode == CodingPropagationMode.Job && preview.TargetIds.Count > 0
            ? await ApplyAsJobAsync(caller, preview, operations, origins, key, cancellationToken).ConfigureAwait(false)
            : await ApplyInteractiveAsync(caller, preview, operations, origins, key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodingPropagationApplyOutcome> ApplyInteractiveAsync(
        CodingCaller caller, CodingPropagationPreview preview, List<CodingFieldOperation> operations, Dictionary<int, Guid> origins, string key,
        CancellationToken cancellationToken)
    {
        var principal = caller.Principal;
        var ws = caller.WorkspaceId;
        var targets = new List<CodingTarget>(preview.TargetIds.Count);
        if (preview.TargetIds.Count > 0)
        {
            var decisions = await authorization.AuthorizeManyAsync(principal, ws, Permission.CodingWrite, preview.TargetIds, DenialAudit.Summary, cancellationToken)
                .ConfigureAwait(false);
            for (var i = 0; i < preview.TargetIds.Count; i++)
            {
                var id = preview.TargetIds[i];
                if (decisions.TryGetValue(id, out var decision) && decision.IsAllowed)
                {
                    // The preview's version is the baseline: a field changed since is left alone (the reviewer did not see it).
                    targets.Add(new CodingTarget(id, preview.TargetVersions.Count > i ? preview.TargetVersions[i] : null));
                }
            }
        }

        // Targets the caller can no longer see are left alone and not reported (Q-52); only the audit event counts them.
        var hidden = preview.TargetIds.Count - targets.Count;
        var auditEvent = Audit(principal, preview, CodingPropagationMode.Interactive);
        if (targets.Count == 0)
        {
            await audit.WriteAsync(auditEvent with
            {
                WorkspaceId = ws,
                Details = new Dictionary<string, string?>(auditEvent.Details) { ["Changed"] = "0", ["NoLongerAccessible"] = Invariant(hidden) },
            }, cancellationToken).ConfigureAwait(false);
            return new CodingPropagationApplyOutcome { Status = CodingPropagationStatus.Ok, Mode = CodingPropagationMode.Interactive };
        }

        var write = await coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = key,
            Actor = new CodingActor(principal.UserId, CodingActorType.SystemRule),
            Documents = targets,
            Operations = operations,
            OriginEventIds = origins,
            Audit = auditEvent with
            {
                Details = new Dictionary<string, string?>(auditEvent.Details) { ["NoLongerAccessible"] = Invariant(hidden) },
            },
        }, cancellationToken).ConfigureAwait(false);
        switch (write.Outcome)
        {
            case CodingWriteOutcome.Applied or CodingWriteOutcome.Replayed:
                var applied = write.Documents.Count(d => d.Outcome == DocumentCodingOutcome.Changed);
                // A member left unchanged because the result would be invalid (Withhold without a basis) counts as skipped.
                var skipped = write.Documents.Count(d => d.Outcome is DocumentCodingOutcome.Skipped or DocumentCodingOutcome.Rejected);
                return new CodingPropagationApplyOutcome
                {
                    Status = CodingPropagationStatus.Ok,
                    Mode = CodingPropagationMode.Interactive,
                    Applied = applied,
                    Skipped = skipped,
                };
            case CodingWriteOutcome.IdempotencyKeyReuse:
                // Applied before with other targets (access changed in between): preview again.
                return CodingPropagationApplyOutcome.Of(CodingPropagationStatus.Stale);
            default:
                return new CodingPropagationApplyOutcome { Status = CodingPropagationStatus.Invalid, Errors = write.Errors };
        }
    }

    private async Task<CodingPropagationApplyOutcome> ApplyAsJobAsync(
        CodingCaller caller, CodingPropagationPreview preview, List<CodingFieldOperation> operations, Dictionary<int, Guid> origins, string key,
        CancellationToken cancellationToken)
    {
        var principal = caller.Principal;
        var name = "Apply to family " + time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        var frozen = await snapshots.FreezeSelectionAsync(
            new SearchCaller(principal, caller.WorkspaceId, null), SnapshotPurpose.BulkCoding, name, preview.TargetIds, Permission.CodingWrite, key,
            cancellationToken).ConfigureAwait(false);
        if (frozen.Status != SnapshotCreateStatus.Ready || frozen.Snapshot is not { } snapshot)
        {
            return CodingPropagationApplyOutcome.Of(CodingPropagationStatus.Stale);
        }

        if (snapshot.DocumentCount is 0)
        {
            // Every target was excluded at the freeze (access changed): nothing to run, nothing visible to report.
            return new CodingPropagationApplyOutcome { Status = CodingPropagationStatus.Ok, Mode = CodingPropagationMode.Interactive };
        }

        var submitted = await bulk.SubmitPropagationAsync(
            caller, snapshot, operations, preview.SecurityAffecting,
            new PropagationJobParameters(preview.PreviewId, preview.SourceDocumentId, origins),
            key,
            Audit(principal, preview, CodingPropagationMode.Job) with { WorkspaceId = caller.WorkspaceId },
            cancellationToken).ConfigureAwait(false);
        return submitted.Status == BulkCodingSubmitStatus.Accepted
            ? new CodingPropagationApplyOutcome { Status = CodingPropagationStatus.Ok, Mode = CodingPropagationMode.Job, Job = submitted.Job }
            : CodingPropagationApplyOutcome.Of(CodingPropagationStatus.Stale);
    }

    private async Task<CodingPropagationStatus?> PrivilegeDeniedAsync(SecurityPrincipal principal, Guid ws, CancellationToken cancellationToken)
    {
        var decision = await authorization.AuthorizeAsync(principal, ws, Permission.CodingWritePrivilege, cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed ? null : Denied(decision);
    }

    private AuditEvent Audit(SecurityPrincipal principal, CodingPropagationPreview preview, CodingPropagationMode mode) => new()
    {
        OccurredAt = time.GetUtcNow(),
        Category = AuditTaxonomy.Coding.Category,
        Action = AuditTaxonomy.Coding.FamilyApplied,
        ActorType = AuditActorType.User,
        ActorId = principal.UserId.ToString(),
        ActorDisplay = string.IsNullOrEmpty(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName,
        ClientIp = principal.ClientIp,
        UserAgent = principal.UserAgent,
        Outcome = AuditOutcome.Success,
        CorrelationId = principal.CorrelationId ?? Activity.Current?.TraceId.ToHexString(),
        ResourceType = "Document",
        ResourceId = preview.SourceDocumentId.ToString(),
        Details = new Dictionary<string, string?>
        {
            ["PreviewId"] = preview.PreviewId.ToString(),
            ["Scope"] = preview.Scope.ToString(),
            ["Mode"] = mode.ToString(),
            ["Targets"] = Invariant(preview.TargetIds.Count),
            ["PropagatedFields"] = string.Join(',', preview.Fields.Select(f => f.FieldId).Order()),
            ["OriginEventIds"] = string.Join(',', preview.Fields.Where(f => f.OriginEventId is not null).OrderBy(f => f.FieldId)
                .Select(f => Invariant(f.FieldId) + ":" + f.OriginEventId)),
        },
    };

    private static CodingPropagationStatus Denied(AuthorizationDecision decision) =>
        decision.Outcome == AuthorizationOutcome.NotFound ? CodingPropagationStatus.NotFound : CodingPropagationStatus.Forbidden;

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
