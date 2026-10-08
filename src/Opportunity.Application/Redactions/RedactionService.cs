using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Core.Pages;
using Opportunity.Core.Redactions;
using Opportunity.Core.Security;

namespace Opportunity.Application.Redactions;

public enum RedactionOutcomeStatus
{
    Ok,
    Created,

    /// <summary>The document, set or reason does not exist or is hidden from the caller: 404.</summary>
    NotFound,

    /// <summary>The document is visible but the caller may not make this change: 403.</summary>
    Forbidden,

    Invalid,

    /// <summary>If-Match is stale: 412 with the current state.</summary>
    VersionConflict,

    /// <summary>A name or code is taken, the set is retired, or the page set changed while saving: 409.</summary>
    Conflict,

    /// <summary>The document has no rendered images to redact on (ADR-012 §3.6): 409 redaction-requires-images.</summary>
    RequiresImages,
}

public sealed record RedactionOutcome<T>(RedactionOutcomeStatus Status, T? Value = default)
    where T : class
{
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();

    public string? Detail { get; init; }
}

/// <summary>One requested change (already shaped by the endpoint).</summary>
public sealed record RedactionChange(
    RedactionOperation Operation,
    Guid? RedactionId,
    int? PageNumber,
    NormalizedRect? Rect,
    RedactionType? Type,
    string? ReasonCode,
    string? Note);

/// <summary>
/// Non-destructive redactions (E11-T04, ADR-012 §2–§3, Q-22): Redaction Sets and the reason picklist (read by every
/// member, changed with <c>Workspace.ManageFields</c>), and per document and set an append-only history of rectangles in
/// normalized page coordinates. A save is one version: If-Match is checked against the current version (412 with the
/// current state when stale, nothing is ever overwritten), every added, modified or removed rectangle is one revision
/// row and one <c>Redaction.*</c> audit event in the same transaction, and removed redactions stay in the history.
/// Authorization is per document through the PDP (hidden → 404): <c>Document.View</c> reads, <c>Redaction.Apply</c>
/// adds and modifies the caller's own redactions, <c>Redaction.Remove</c> removes any and modifies other users' (ADR-012
/// §3.8). New redactions need a rendered page: the active page set Ready and an image on the page (§3.6).
/// </summary>
public sealed partial class RedactionService(IRedactionStore store, IAuthorizationService authorization, TimeProvider time)
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2_000;
    public const int MaxReasonNameLength = 100;
    public const int MaxBoxLabelLength = 60;
    public const int MaxReasons = 200;
    public const int MaxChanges = 500;
    public const int MaxActiveRedactions = 5_000;
    public const int MaxNoteLength = 1_000;
    public const int MaxHistory = 2_000;

    /// <summary>What the viewer shows when the document has no rendered images to redact on.</summary>
    public const string RequiresRenderedImages = "Redaction requires rendered images";

    private const string CorrelationTag = "opportunity.correlation_id";

    // ── Redaction Sets ───────────────────────────────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<RedactionSetRecord>> ListSetsAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ListSetsAsync(workspaceId, cancellationToken);

    public Task<RedactionSetRecord?> GetSetAsync(Guid workspaceId, Guid redactionSetId, CancellationToken cancellationToken = default) =>
        store.GetSetAsync(workspaceId, redactionSetId, cancellationToken);

    public async Task<RedactionOutcome<RedactionSetRecord>> CreateSetAsync(
        SecurityPrincipal actor, Guid workspaceId, string? name, string? description, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (ValidateSet(name, description, out var errors) is not { } valid)
        {
            return new(RedactionOutcomeStatus.Invalid) { Errors = errors };
        }

        var id = Guid.CreateVersion7();
        var result = await store.CreateSetAsync(workspaceId, id, actor.UserId, valid.Name, valid.Description,
            Event(actor, workspaceId, AuditTaxonomy.Redaction.SetCreated, AuditTaxonomy.Redaction.SetResourceType, id.ToString(),
                new() { ["retired"] = "false" }), cancellationToken).ConfigureAwait(false);
        return MapSet(result, RedactionOutcomeStatus.Created);
    }

    public async Task<RedactionOutcome<RedactionSetRecord>> UpdateSetAsync(
        SecurityPrincipal actor, Guid workspaceId, RedactionSetRecord current, long expectedVersion, string? name, string? description, bool retired,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(current);
        if (ValidateSet(name, description, out var errors) is not { } valid)
        {
            return new(RedactionOutcomeStatus.Invalid) { Errors = errors };
        }

        var result = await store.UpdateSetAsync(workspaceId, current.RedactionSetId, expectedVersion, actor.UserId, valid.Name, valid.Description, retired,
            Event(actor, workspaceId, AuditTaxonomy.Redaction.SetModified, AuditTaxonomy.Redaction.SetResourceType, current.RedactionSetId.ToString(),
                new() { ["retired"] = retired ? "true" : "false", ["renamed"] = valid.Name == current.Name ? "false" : "true" }),
            cancellationToken).ConfigureAwait(false);
        return MapSet(result, RedactionOutcomeStatus.Ok);
    }

    // ── Reasons ──────────────────────────────────────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<RedactionReasonRecord>> ListReasonsAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ListReasonsAsync(workspaceId, cancellationToken);

    public async Task<RedactionOutcome<RedactionReasonRecord>> CreateReasonAsync(
        SecurityPrincipal actor, Guid workspaceId, string? code, string? name, RedactionReasonCategory? category, string? boxLabel, bool active,
        int? sortOrder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var errors = new Dictionary<string, string[]>();
        var trimmedCode = code?.Trim() ?? string.Empty;
        if (!ReasonCodePattern().IsMatch(trimmedCode))
        {
            errors["code"] = ["A code starts with a letter and has 1 to 40 letters and digits."];
        }

        var existing = await store.ListReasonsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (existing.Count >= MaxReasons)
        {
            errors["code"] = [$"A workspace has at most {MaxReasons} reasons."];
        }

        if (ValidateReason(name, category, boxLabel, errors) is not { } valid || errors.Count > 0)
        {
            return new(RedactionOutcomeStatus.Invalid) { Errors = errors };
        }

        var reason = new RedactionReasonRecord(trimmedCode, valid.Name, valid.Category, valid.BoxLabel, active,
            sortOrder ?? (existing.Count == 0 ? 10 : existing.Max(r => r.SortOrder) + 10), 1);
        var result = await store.CreateReasonAsync(workspaceId, reason, actor.UserId,
            ReasonEvent(actor, workspaceId, AuditTaxonomy.Redaction.ReasonCreated, reason), cancellationToken).ConfigureAwait(false);
        return MapReason(result, RedactionOutcomeStatus.Created);
    }

    public async Task<RedactionOutcome<RedactionReasonRecord>> UpdateReasonAsync(
        SecurityPrincipal actor, Guid workspaceId, RedactionReasonRecord current, long expectedVersion, string? name, RedactionReasonCategory? category,
        string? boxLabel, bool active, int? sortOrder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(current);
        var errors = new Dictionary<string, string[]>();
        if (ValidateReason(name, category, boxLabel, errors) is not { } valid)
        {
            return new(RedactionOutcomeStatus.Invalid) { Errors = errors };
        }

        var reason = current with
        {
            Name = valid.Name,
            Category = valid.Category,
            BoxLabel = valid.BoxLabel,
            Active = active,
            SortOrder = sortOrder ?? current.SortOrder,
        };
        var result = await store.UpdateReasonAsync(workspaceId, reason, expectedVersion, actor.UserId,
            ReasonEvent(actor, workspaceId, AuditTaxonomy.Redaction.ReasonModified, reason), cancellationToken).ConfigureAwait(false);
        return MapReason(result, RedactionOutcomeStatus.Ok);
    }

    // ── Document redactions ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The document's redactions in the set (current, or as of an earlier version); hidden documents are NotFound.</summary>
    public async Task<RedactionOutcome<DocumentRedactionState>> GetDocumentAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid documentId, Guid redactionSetId, long? asOfVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = await authorization.AuthorizeAsync(principal, workspaceId, Permission.DocumentView, documentId, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return new(Denied(decision));
        }

        if (asOfVersion is < 0)
        {
            return new(RedactionOutcomeStatus.Invalid) { Errors = new Dictionary<string, string[]> { ["version"] = ["A version is 0 or more."] } };
        }

        var state = await store.GetDocumentAsync(workspaceId, documentId, redactionSetId, asOfVersion, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new(RedactionOutcomeStatus.NotFound) { Detail = SetNotFound };
        }

        return asOfVersion > state.CurrentVersion
            ? new(RedactionOutcomeStatus.Invalid) { Errors = new Dictionary<string, string[]> { ["version"] = [$"The newest version is {state.CurrentVersion}."] } }
            : new(RedactionOutcomeStatus.Ok, state);
    }

    public async Task<RedactionOutcome<IReadOnlyList<RedactionRevisionRecord>>> HistoryAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid documentId, Guid redactionSetId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decision = await authorization.AuthorizeAsync(principal, workspaceId, Permission.DocumentView, documentId, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return new(Denied(decision));
        }

        if (await store.GetSetAsync(workspaceId, redactionSetId, cancellationToken).ConfigureAwait(false) is null)
        {
            return new(RedactionOutcomeStatus.NotFound) { Detail = SetNotFound };
        }

        var history = await store.HistoryAsync(workspaceId, documentId, redactionSetId, MaxHistory + 1, cancellationToken).ConfigureAwait(false);
        return new(RedactionOutcomeStatus.Ok, history);
    }

    /// <summary>
    /// Saves one version of changes. <paramref name="expectedVersion"/> is the If-Match version (null for <c>*</c>).
    /// VersionConflict carries the current state.
    /// </summary>
    public async Task<RedactionOutcome<DocumentRedactionState>> SaveAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid documentId, Guid redactionSetId, long? expectedVersion, IReadOnlyList<RedactionChange> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(changes);
        var decision = await authorization.AuthorizeAsync(principal, workspaceId, Permission.RedactionApply, documentId, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return new(Denied(decision));
        }

        if (changes.Count is 0 or > MaxChanges)
        {
            return Invalid("changes", $"Send 1 to {MaxChanges} changes.");
        }

        var state = await store.GetDocumentAsync(workspaceId, documentId, redactionSetId, null, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new(RedactionOutcomeStatus.NotFound) { Detail = SetNotFound };
        }

        var expected = expectedVersion ?? state.CurrentVersion;
        if (expected != state.CurrentVersion)
        {
            return new(RedactionOutcomeStatus.VersionConflict, state);
        }

        if (state.Set.Retired)
        {
            return new(RedactionOutcomeStatus.Conflict) { Detail = "The Redaction Set is retired; its redactions cannot change." };
        }

        var reasons = (await store.ListReasonsAsync(workspaceId, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.Code, StringComparer.Ordinal);
        var plan = Plan(principal, state, reasons, changes, out var errors, out var touchesOthers, out var needsPages);
        if (errors.Count > 0)
        {
            if (errors.ContainsKey(RequiresImagesKey))
            {
                return new(RedactionOutcomeStatus.RequiresImages, state) { Detail = RequiresRenderedImages + "." };
            }

            return new(RedactionOutcomeStatus.Invalid) { Errors = errors };
        }

        if (touchesOthers)
        {
            var remove = await authorization.AuthorizeAsync(principal, workspaceId, Permission.RedactionRemove, documentId, cancellationToken).ConfigureAwait(false);
            if (!remove.IsAllowed)
            {
                return new(Denied(remove)) { Detail = "Removing redactions, or changing another user's, needs Redaction.Remove." };
            }
        }

        var version = expected + 1;
        var audits = plan.Select(p => RevisionEvent(principal, workspaceId, documentId, redactionSetId, version, p, reasons)).ToList();
        var status = await store.SaveAsync(workspaceId, documentId, redactionSetId, expected, needsPages ? state.ActivePageSetId : null, principal.UserId,
            plan, audits, cancellationToken).ConfigureAwait(false);
        var saved = await store.GetDocumentAsync(workspaceId, documentId, redactionSetId, null, cancellationToken).ConfigureAwait(false);
        return status switch
        {
            RedactionWriteStatus.Ok => new(RedactionOutcomeStatus.Ok, saved),
            RedactionWriteStatus.VersionConflict => new(RedactionOutcomeStatus.VersionConflict, saved),
            RedactionWriteStatus.PageSetChanged => new(RedactionOutcomeStatus.Conflict, saved)
            {
                Detail = "The document's page images changed while saving; reload the document and redact again.",
            },
            _ => new(RedactionOutcomeStatus.NotFound) { Detail = SetNotFound },
        };
    }

    public const string SetNotFound = "No such redaction set.";

    private const string RequiresImagesKey = "pageNumber.requiresImages";

    private static List<PlannedRevision> Plan(
        SecurityPrincipal principal,
        DocumentRedactionState state,
        IReadOnlyDictionary<string, RedactionReasonRecord> reasons,
        IReadOnlyList<RedactionChange> changes,
        out Dictionary<string, string[]> errors,
        out bool touchesOthers,
        out bool needsPages)
    {
        errors = [];
        touchesOthers = false;
        needsPages = false;
        var current = state.Redactions.ToDictionary(r => r.RedactionId);
        var seen = new HashSet<Guid>();
        var plan = new List<PlannedRevision>(changes.Count);
        var added = 0;
        for (var i = 0; i < changes.Count; i++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"changes[{i}]");
            var change = changes[i];
            if (change.RedactionId is { } named && !seen.Add(named))
            {
                errors[key + ".redactionId"] = ["A redaction can change only once per save."];
                continue;
            }

            switch (change.Operation)
            {
                case RedactionOperation.Add:
                    {
                        var id = change.RedactionId ?? Guid.CreateVersion7();
                        if (change.RedactionId is { } existingId && (current.ContainsKey(existingId) || id == Guid.Empty))
                        {
                            errors[key + ".redactionId"] = ["This redaction already exists; modify it instead."];
                            continue;
                        }

                        seen.Add(id);
                        if (change.PageNumber is null || change.Rect is null || change.Type is null || string.IsNullOrEmpty(change.ReasonCode))
                        {
                            errors[key] = ["An added redaction needs pageNumber, rect, type and reasonCode."];
                            continue;
                        }

                        needsPages = true;
                        if (!state.Redactable)
                        {
                            errors[RequiresImagesKey] = [RequiresRenderedImages];
                            continue;
                        }

                        if (ValidatePlacement(state, change.PageNumber.Value, change.Rect.Value, key, errors) is not { } ordinal
                            || ValidateReasonChoice(reasons, change.ReasonCode, null, key, errors) is not { } reasonCode
                            || ValidateNote(change.Note, key, errors, out var note) is false)
                        {
                            continue;
                        }

                        added++;
                        plan.Add(new PlannedRevision(id, RedactionOperation.Add, state.ActivePageSetId!.Value, ordinal, change.Rect.Value, change.Type.Value,
                            reasonCode, note));
                        break;
                    }

                case RedactionOperation.Modify:
                    {
                        if (change.RedactionId is not { } id || !current.TryGetValue(id, out var existing))
                        {
                            errors[key + ".redactionId"] = ["No such redaction (it may have been removed)."];
                            continue;
                        }

                        touchesOthers |= existing.CreatedBy.UserId != principal.UserId;
                        var pageSetId = existing.PageSetId;
                        var ordinal = existing.Ordinal;
                        var rect = change.Rect ?? existing.Rect;
                        if (change.PageNumber is not null || change.Rect is not null)
                        {
                            needsPages = true;
                            if (!state.Redactable)
                            {
                                errors[RequiresImagesKey] = [RequiresRenderedImages];
                                continue;
                            }

                            if (ValidatePlacement(state, change.PageNumber ?? existing.Ordinal, rect, key, errors) is not { } placed)
                            {
                                continue;
                            }

                            // Moving or resizing a redaction of a replaced page set re-anchors it on the active one.
                            pageSetId = state.ActivePageSetId!.Value;
                            ordinal = placed;
                        }

                        if (ValidateReasonChoice(reasons, change.ReasonCode, existing.ReasonCode, key, errors) is not { } reasonCode)
                        {
                            continue;
                        }

                        string? note = existing.Note;
                        if (change.Note is not null && !ValidateNote(change.Note, key, errors, out note))
                        {
                            continue;
                        }

                        var type = change.Type ?? existing.Type;
                        if (pageSetId == existing.PageSetId && ordinal == existing.Ordinal && rect == existing.Rect && type == existing.Type
                            && reasonCode == existing.ReasonCode && note == existing.Note)
                        {
                            errors[key] = ["The change does not change the redaction."];
                            continue;
                        }

                        plan.Add(new PlannedRevision(id, RedactionOperation.Modify, pageSetId, ordinal, rect, type, reasonCode, note));
                        break;
                    }

                case RedactionOperation.Remove:
                    {
                        if (change.RedactionId is not { } id || !current.TryGetValue(id, out var existing))
                        {
                            errors[key + ".redactionId"] = ["No such redaction (it may have been removed)."];
                            continue;
                        }

                        touchesOthers = true;
                        plan.Add(new PlannedRevision(id, RedactionOperation.Remove, existing.PageSetId, existing.Ordinal, existing.Rect, existing.Type,
                            existing.ReasonCode, existing.Note));
                        break;
                    }

                default:
                    errors[key + ".operation"] = ["Use add, modify or remove."];
                    break;
            }
        }

        if (state.Redactions.Count + added > MaxActiveRedactions)
        {
            errors["changes"] = [$"A document has at most {MaxActiveRedactions} redactions per set."];
        }

        return plan;
    }

    private static int? ValidatePlacement(DocumentRedactionState state, int pageNumber, NormalizedRect rect, string key, Dictionary<string, string[]> errors)
    {
        if (!state.Pages.TryGetValue(pageNumber, out var page))
        {
            errors[key + ".pageNumber"] = [$"The document has no page {pageNumber}."];
            return null;
        }

        if (!page.HasImage)
        {
            errors[key + ".pageNumber"] = [$"Page {pageNumber} has no rendered image. {RequiresRenderedImages}."];
            return null;
        }

        if (!rect.IsWithinPage)
        {
            errors[key + ".rect"] = [$"A rectangle lies within the page: 0 ≤ x < x + w ≤ {NormalizedRect.Scale:N0}, the same for y."];
            return null;
        }

        var (minW, minH) = RedactionGeometry.MinimumSize(page.WidthPt, page.HeightPt);
        if (rect.W < minW || rect.H < minH)
        {
            errors[key + ".rect"] = [$"A redaction is at least {RedactionGeometry.MinimumDevicePixels} × {RedactionGeometry.MinimumDevicePixels} pixels at {RedactionGeometry.ProductionDpi} DPI."];
            return null;
        }

        return pageNumber;
    }

    private static string? ValidateReasonChoice(
        IReadOnlyDictionary<string, RedactionReasonRecord> reasons, string? requested, string? existing, string key, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(requested) || requested == existing)
        {
            return existing ?? requested;
        }

        if (!reasons.TryGetValue(requested, out var reason))
        {
            errors[key + ".reasonCode"] = ["No such redaction reason."];
            return null;
        }

        if (!reason.Active)
        {
            errors[key + ".reasonCode"] = [$"The reason {reason.Name} is inactive."];
            return null;
        }

        return reason.Code;
    }

    private static bool ValidateNote(string? requested, string key, Dictionary<string, string[]> errors, out string? note)
    {
        note = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (note is { Length: > MaxNoteLength })
        {
            errors[key + ".note"] = [$"A note has at most {MaxNoteLength} characters."];
            return false;
        }

        return true;
    }

    private static (string Name, string? Description)? ValidateSet(string? name, string? description, out Dictionary<string, string[]> errors)
    {
        errors = [];
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"A name has 1 to {MaxNameLength} characters."];
        }

        var desc = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (desc is { Length: > MaxDescriptionLength })
        {
            errors["description"] = [$"A description has at most {MaxDescriptionLength} characters."];
        }

        return errors.Count == 0 ? (trimmed, desc) : null;
    }

    private static (string Name, RedactionReasonCategory Category, string BoxLabel)? ValidateReason(
        string? name, RedactionReasonCategory? category, string? boxLabel, Dictionary<string, string[]> errors)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxReasonNameLength)
        {
            errors["name"] = [$"A name has 1 to {MaxReasonNameLength} characters."];
        }

        if (category is null || !Enum.IsDefined(category.Value))
        {
            errors["category"] = ["Choose privilege, privacy or other."];
        }

        var label = boxLabel?.Trim() ?? string.Empty;
        if (label.Length is 0 or > MaxBoxLabelLength)
        {
            errors["boxLabel"] = [$"A box label has 1 to {MaxBoxLabelLength} characters."];
        }

        return errors.Count == 0 ? (trimmed, category!.Value, label) : null;
    }

    private static RedactionOutcome<DocumentRedactionState> Invalid(string key, string message) =>
        new(RedactionOutcomeStatus.Invalid) { Errors = new Dictionary<string, string[]> { [key] = [message] } };

    private static RedactionOutcomeStatus Denied(AuthorizationDecision decision) =>
        decision.Outcome == AuthorizationOutcome.NotFound ? RedactionOutcomeStatus.NotFound : RedactionOutcomeStatus.Forbidden;

    private static RedactionOutcome<RedactionSetRecord> MapSet(RedactionSetWriteResult result, RedactionOutcomeStatus success) => result.Status switch
    {
        RedactionWriteStatus.Ok => new(success, result.Set),
        RedactionWriteStatus.VersionConflict => new(RedactionOutcomeStatus.VersionConflict),
        RedactionWriteStatus.NameTaken => new(RedactionOutcomeStatus.Conflict) { Detail = "A Redaction Set with this name already exists." },
        _ => new(RedactionOutcomeStatus.NotFound) { Detail = SetNotFound },
    };

    private static RedactionOutcome<RedactionReasonRecord> MapReason(RedactionReasonWriteResult result, RedactionOutcomeStatus success) => result.Status switch
    {
        RedactionWriteStatus.Ok => new(success, result.Reason),
        RedactionWriteStatus.VersionConflict => new(RedactionOutcomeStatus.VersionConflict),
        RedactionWriteStatus.NameTaken => new(RedactionOutcomeStatus.Conflict) { Detail = "A reason with this code or name already exists." },
        _ => new(RedactionOutcomeStatus.NotFound) { Detail = "No such redaction reason." },
    };

    private AuditEvent RevisionEvent(
        SecurityPrincipal actor, Guid workspaceId, Guid documentId, Guid redactionSetId, long version, PlannedRevision revision,
        Dictionary<string, RedactionReasonRecord> reasons)
    {
        var action = revision.Operation switch
        {
            RedactionOperation.Add => AuditTaxonomy.Redaction.Added,
            RedactionOperation.Modify => AuditTaxonomy.Redaction.Modified,
            _ => AuditTaxonomy.Redaction.Removed,
        };

        // IDs, enums and geometry only: the note is free text and never copied into audit (ADR-012 §3.3).
        return Event(actor, workspaceId, action, AuditTaxonomy.Redaction.ResourceType, revision.RedactionId.ToString(), new()
        {
            ["documentId"] = documentId.ToString(),
            ["redactionSetId"] = redactionSetId.ToString(),
            ["redactionVersion"] = version.ToString(CultureInfo.InvariantCulture),
            ["pageSetId"] = revision.PageSetId.ToString(),
            ["pageNumber"] = revision.Ordinal.ToString(CultureInfo.InvariantCulture),
            ["rect"] = string.Create(CultureInfo.InvariantCulture, $"{revision.Rect.X},{revision.Rect.Y},{revision.Rect.W},{revision.Rect.H}"),
            ["type"] = revision.Type.ToString(),
            ["reasonCode"] = revision.ReasonCode,
            ["reasonCategory"] = reasons.TryGetValue(revision.ReasonCode, out var reason) ? reason.Category.ToString() : null,
        });
    }

    private AuditEvent ReasonEvent(SecurityPrincipal actor, Guid workspaceId, string action, RedactionReasonRecord reason) =>
        Event(actor, workspaceId, action, AuditTaxonomy.Redaction.ReasonResourceType, reason.Code, new()
        {
            ["category"] = reason.Category.ToString(),
            ["active"] = reason.Active ? "true" : "false",
        });

    private AuditEvent Event(SecurityPrincipal actor, Guid workspaceId, string action, string resourceType, string resourceId, Dictionary<string, string?> details) =>
        new()
        {
            WorkspaceId = workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Redaction.Category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = actor.UserId.ToString(),
            ActorDisplay = actor.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? actor.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : actor.DisplayName,
            ClientIp = actor.ClientIp,
            UserAgent = actor.UserAgent,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Outcome = AuditOutcome.Success,
            CorrelationId = actor.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = details,
        };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,39}$")]
    private static partial Regex ReasonCodePattern();
}
