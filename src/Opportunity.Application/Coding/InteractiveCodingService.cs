using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Identity;
using Opportunity.Application.SearchWork;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;

namespace Opportunity.Application.Coding;

/// <summary>Who codes, in which workspace (PEP-1 has authorized membership and the endpoint permission).</summary>
public sealed record CodingCaller(SecurityPrincipal Principal, Guid WorkspaceId);

/// <summary>How far search has caught up with the document's interactive coding (read-your-own-writes, UI finding 6).</summary>
public enum CodingIndexingState
{
    /// <summary>Every interactive change of the document is in the search index.</summary>
    Indexed,

    /// <summary>A committed change is waiting for the interactive index worker.</summary>
    Pending,

    /// <summary>Indexing a change gave up (ADR-001 §6.4); the document needs repair to become searchable.</summary>
    Failed,
}

/// <summary>One field of the coding view: its current canonical value and who changed it last.</summary>
/// <param name="Editable">The caller may change it here (layout not read-only; Coding.Write, plus Coding.WritePrivilege when security-affecting).</param>
public sealed record CodedField(
    FieldDefinition Field, JsonNode? Value, long? ChangedAtVersion, Guid? ChangedBy, DateTimeOffset? ChangedAt, Guid? ChangedByJobId, bool Editable);

/// <summary>The author of the latest coding change among the fields the caller can see.</summary>
public sealed record CodingEditor(Guid UserId, string? DisplayName, DateTimeOffset At, long DocumentVersion, Guid? JobId);

/// <param name="ProjectedVersion">Highest DocumentVersion known to be searchable (equal to the version once indexed).</param>
public sealed record DocumentCodingView(
    Guid DocumentId,
    long DocumentVersion,
    long ProjectedVersion,
    CodingIndexingState IndexingState,
    Guid? LayoutId,
    IReadOnlyList<CodedField> Fields,
    CodingEditor? LastEditor);

/// <summary>One requested change: Set (null value clears), or AddChoices/RemoveChoices on a multiple-choice field.</summary>
public sealed record CodingChange(int FieldId, CodingOperationKind Kind, JsonNode? Value);

/// <param name="ExpectedVersion">From <c>If-Match</c>; null only for <c>If-Match: *</c>.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>, or null.</param>
/// <param name="LayoutId">When set, the changes are validated against the layout (editable fields, required values).</param>
public sealed record InteractiveCodingRequest(
    Guid DocumentId, long? ExpectedVersion, IReadOnlyList<CodingChange> Changes, Guid? LayoutId = null, string? IdempotencyKey = null);

public enum CodingStatus
{
    Ok,

    /// <summary>The idempotency key was applied before with the same request; nothing was written again.</summary>
    Replayed,

    /// <summary>The document does not exist or is hidden from the caller (restriction class, wall): 404.</summary>
    NotFound,

    /// <summary>The document is visible but the caller may not code it, or not its security-affecting fields: 403.</summary>
    Forbidden,

    Invalid,

    /// <summary>If-Match did not match: <see cref="CodingOutcome.View"/> is the current state to reconcile with.</summary>
    VersionConflict,

    IdempotencyKeyReuse,
}

public sealed record CodingOutcome
{
    public required CodingStatus Status { get; init; }

    /// <summary>The coding after the save (Ok, Replayed) or the current coding (VersionConflict).</summary>
    public DocumentCodingView? View { get; init; }

    public IReadOnlyList<FieldError> Errors { get; init; } = [];

    /// <summary>The save changed at least one value (and bumped DocumentVersion).</summary>
    public bool Changed { get; init; }

    internal static CodingOutcome Of(CodingStatus status, params FieldError[] errors) => new() { Status = status, Errors = errors };
}

/// <summary>
/// Interactive coding (E10-T01, baseline §7/§21/§24): reads a document's coding for a layout and saves a reviewer's
/// changes with optimistic concurrency on its DocumentVersion. Every save goes through <see cref="ICodingRepository"/>,
/// so current state, CodingEvents, the version bump, the SearchOutbox row (security lane when a security-affecting
/// field changed), the restriction classes it drives and the <c>Coding.Changed</c> audit event commit together.
/// Authorization is PEP-2: the document-level decision of the PDP per request (hidden → NotFound), plus
/// <c>Coding.WritePrivilege</c> for security-affecting fields.
/// </summary>
public sealed class InteractiveCodingService(
    ICodingRepository coding,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess,
    IAuthorizationService authorization,
    ISearchOutboxRepository outbox,
    IUserDirectory users,
    TimeProvider time)
{
    public const int MaxIdempotencyKeyLength = 128;

    private const string KeyPrefix = "api:";

    public async Task<CodingOutcome> GetAsync(CodingCaller caller, Guid documentId, Guid? layoutId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var decision = await authorization.AuthorizeAsync(caller.Principal, caller.WorkspaceId, Permission.DocumentView, documentId, cancellationToken)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return CodingOutcome.Of(Denied(decision));
        }

        var context = await LoadContextAsync(caller, layoutId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return CodingOutcome.Of(CodingStatus.Invalid, UnknownLayout());
        }

        var view = await ViewAsync(caller, documentId, context, cancellationToken).ConfigureAwait(false);
        return view is null ? CodingOutcome.Of(CodingStatus.NotFound) : new CodingOutcome { Status = CodingStatus.Ok, View = view };
    }

    public async Task<CodingOutcome> SaveAsync(CodingCaller caller, InteractiveCodingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (ValidateShape(request) is { } shape)
        {
            return CodingOutcome.Of(CodingStatus.Invalid, shape);
        }

        var principal = caller.Principal;
        var ws = caller.WorkspaceId;
        var decision = await authorization.AuthorizeAsync(principal, ws, Permission.CodingWrite, request.DocumentId, cancellationToken)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return CodingOutcome.Of(Denied(decision));
        }

        var context = await LoadContextAsync(caller, request.LayoutId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return CodingOutcome.Of(CodingStatus.Invalid, UnknownLayout());
        }

        // Values are validated against the catalogue here for early, complete errors; the store re-validates under lock.
        var errors = new List<FieldError>();
        var resolved = new List<(FieldDefinition Field, CodingChange Change, JsonNode? Canonical)>();
        foreach (var change in request.Changes)
        {
            var field = context.Catalog.Find(change.FieldId);
            if (field is null || field.IsDeleted || context.Restricted.Contains(change.FieldId))
            {
                errors.Add(new FieldError(FieldKey.For(change.FieldId), "unknown-field", "The field does not exist in this workspace."));
            }
            else if (field.Storage != FieldStorage.Coding)
            {
                errors.Add(new FieldError(field.Key, "not-coding-field", $"{field.Name} is imported data and cannot be coded."));
            }
            else if (Canonicalize(field, change, context.Catalog.ChoicesOf(field.FieldId), out var canonical) is { } error)
            {
                errors.Add(error);
            }
            else
            {
                resolved.Add((field, change, canonical));
            }
        }

        if (errors.Count > 0)
        {
            return new CodingOutcome { Status = CodingStatus.Invalid, Errors = errors };
        }

        // §24 / Q-11: security-affecting fields need Coding.WritePrivilege on this document as well.
        if (resolved.Any(r => r.Field.IsSecurityAffecting))
        {
            var privilege = await authorization.AuthorizeAsync(principal, ws, Permission.CodingWritePrivilege, request.DocumentId, cancellationToken)
                .ConfigureAwait(false);
            if (!privilege.IsAllowed)
            {
                return CodingOutcome.Of(Denied(privilege));
            }
        }

        if (context.Layout is { } layout)
        {
            var current = (await coding.GetCurrentAsync(ws, [request.DocumentId], cancellationToken).ConfigureAwait(false)).SingleOrDefault();
            if (current is null)
            {
                return CodingOutcome.Of(CodingStatus.NotFound);
            }

            // Checked only against the state the If-Match names: a stale save (or a retried one) goes on to the store,
            // which answers with the conflict or the replay.
            if (request.ExpectedVersion is null || request.ExpectedVersion == current.DocumentVersion)
            {
                var after = current.Fields.ToDictionary(f => f.FieldId, f => f.Value);
                foreach (var (field, change, canonical) in resolved)
                {
                    after[field.FieldId] = Apply(change.Kind, after.GetValueOrDefault(field.FieldId), canonical);
                }

                var layoutErrors = CodingLayoutValidator.ValidateSubmission(layout, after, [.. resolved.Select(r => r.Field.FieldId)]);
                if (layoutErrors.Count > 0)
                {
                    return new CodingOutcome { Status = CodingStatus.Invalid, Errors = layoutErrors };
                }
            }
        }

        var write = await coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = KeyPrefix + principal.UserId.ToString("N") + ":" + (request.IdempotencyKey ?? Guid.CreateVersion7().ToString("N")),
            Actor = new CodingActor(principal.UserId, CodingActorType.Human),
            Documents = [new CodingTarget(request.DocumentId)],
            Operations = [.. resolved.Select(r => new CodingFieldOperation(r.Field.FieldId, r.Change.Kind, r.Canonical))],
            ExpectedVersion = request.ExpectedVersion,
            Audit = Audit(principal, request),
        }, cancellationToken).ConfigureAwait(false);

        var status = write.Outcome switch
        {
            CodingWriteOutcome.Applied => CodingStatus.Ok,
            CodingWriteOutcome.Replayed => CodingStatus.Replayed,
            CodingWriteOutcome.VersionConflict => CodingStatus.VersionConflict,
            CodingWriteOutcome.NotFound => CodingStatus.NotFound,
            CodingWriteOutcome.IdempotencyKeyReuse => CodingStatus.IdempotencyKeyReuse,
            _ => CodingStatus.Invalid,
        };
        if (status is CodingStatus.Invalid or CodingStatus.NotFound or CodingStatus.IdempotencyKeyReuse)
        {
            return new CodingOutcome { Status = status, Errors = write.Errors };
        }

        var view = await ViewAsync(caller, request.DocumentId, context, cancellationToken).ConfigureAwait(false);
        if (view is null)
        {
            return CodingOutcome.Of(CodingStatus.NotFound);
        }

        return new CodingOutcome { Status = status, View = view, Changed = write.EventsWritten > 0 };
    }

    private static CodingStatus Denied(AuthorizationDecision decision) =>
        decision.Outcome == AuthorizationOutcome.NotFound ? CodingStatus.NotFound : CodingStatus.Forbidden;

    private static FieldError UnknownLayout() => new("layoutId", "unknown-layout", "The coding layout does not exist in this workspace.");

    private static FieldError? ValidateShape(InteractiveCodingRequest request)
    {
        if (request.Changes is null || request.Changes.Count is 0 or > CodingWriteRequest.MaxOperations)
        {
            return new FieldError("changes", "invalid-changes", $"Between 1 and {CodingWriteRequest.MaxOperations} changes are required.");
        }

        if (request.Changes.Select(c => c.FieldId).Distinct().Count() != request.Changes.Count)
        {
            return new FieldError("changes", "duplicate-field", "Each field may be changed at most once per save.");
        }

        if (request.IdempotencyKey is { } key && (key.Length is 0 or > MaxIdempotencyKeyLength))
        {
            return new FieldError("idempotencyKey", "invalid-idempotency-key", $"An idempotency key has 1–{MaxIdempotencyKeyLength} characters.");
        }

        return null;
    }

    internal static FieldError? Canonicalize(FieldDefinition field, CodingChange change, IReadOnlyList<Choice> choices, out JsonNode? canonical)
    {
        canonical = null;
        switch (change.Kind)
        {
            case CodingOperationKind.Set:
                return FieldValues.TryCanonicalize(field, change.Value, choices, forAssignment: true, out canonical, out var error) ? null : error;
            case CodingOperationKind.AddChoices or CodingOperationKind.RemoveChoices:
                if (field.Type != FieldType.MultiChoice)
                {
                    return new FieldError(field.Key, "not-multi-choice", "Adding or removing choices applies to multiple-choice fields.");
                }

                // Removing a choice that has since been deactivated is allowed; adding one is not.
                if (!FieldValues.TryCanonicalize(field, change.Value, choices, change.Kind == CodingOperationKind.AddChoices, out canonical, out var idError))
                {
                    return idError;
                }

                return canonical is null ? new FieldError(field.Key, "invalid-choice", "At least one choice id is required.") : null;
            default:
                return new FieldError(field.Key, "invalid-operation", "Unknown operation.");
        }
    }

    private static JsonNode? Apply(CodingOperationKind kind, JsonNode? current, JsonNode? canonical) => kind switch
    {
        CodingOperationKind.AddChoices => FieldValues.ChoiceArray(FieldValues.ChoiceIds(current).Union(FieldValues.ChoiceIds(canonical))),
        CodingOperationKind.RemoveChoices => FieldValues.ChoiceArray(FieldValues.ChoiceIds(current).Except(FieldValues.ChoiceIds(canonical))),
        _ => canonical?.DeepClone(),
    };

    private AuditEvent Audit(SecurityPrincipal principal, InteractiveCodingRequest request)
    {
        var details = new Dictionary<string, string?>
        {
            ["Operations"] = string.Join(',', request.Changes.OrderBy(c => c.FieldId).Select(c =>
                c.FieldId.ToString(CultureInfo.InvariantCulture) + ":" + c.Kind)),
        };
        if (request.LayoutId is { } layoutId)
        {
            details["LayoutId"] = layoutId.ToString();
        }

        if (request.ExpectedVersion is { } expected)
        {
            details["ExpectedVersion"] = expected.ToString(CultureInfo.InvariantCulture);
        }

        return new AuditEvent
        {
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Coding.Category,
            Action = AuditTaxonomy.Coding.Changed,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = principal.DisplayName,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            Outcome = AuditOutcome.Success,
            CorrelationId = principal.CorrelationId ?? Activity.Current?.TraceId.ToHexString(),
            Details = details,
        };
    }

    /// <summary>Catalogue, field restrictions, effective permissions and the layout; null when the layout does not exist.</summary>
    private async Task<ViewContext?> LoadContextAsync(CodingCaller caller, Guid? layoutId, CancellationToken cancellationToken)
    {
        CodingLayout? layout = null;
        if (layoutId is { } id)
        {
            layout = await fields.GetLayoutAsync(caller.WorkspaceId, id, cancellationToken).ConfigureAwait(false);
            if (layout is null)
            {
                return null;
            }
        }

        var catalog = await fields.GetCatalogAsync(caller.WorkspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(caller.WorkspaceId, caller.Principal, catalog, cancellationToken).ConfigureAwait(false);
        var effective = await authorization.GetEffectivePermissionsAsync(caller.Principal, caller.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return new ViewContext(catalog, restricted, layout, effective.Permissions.ToHashSet());
    }

    private async Task<DocumentCodingView?> ViewAsync(CodingCaller caller, Guid documentId, ViewContext context, CancellationToken cancellationToken)
    {
        var ws = caller.WorkspaceId;
        var current = (await coding.GetCurrentAsync(ws, [documentId], cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (current is null)
        {
            return null;
        }

        var canWrite = context.Permissions.Contains(Permission.CodingWrite);
        var canWritePrivilege = context.Permissions.Contains(Permission.CodingWritePrivilege);
        IEnumerable<(FieldDefinition Field, bool ReadOnly)> shown = context.Layout is { } layout
            ? layout.AllFields.Select(f => (Field: context.Catalog.Find(f.FieldId)!, ReadOnly: f.IsReadOnly)).Where(f => f.Field is not null)
            : context.Catalog.Fields.OrderBy(f => f.FieldId).Select(f => (Field: f, ReadOnly: false));

        var state = current.Fields.ToDictionary(f => f.FieldId);
        var result = new List<CodedField>();
        foreach (var (field, readOnly) in shown)
        {
            if (field.IsDeleted || field.Storage != FieldStorage.Coding || context.Restricted.Contains(field.FieldId))
            {
                continue;
            }

            var s = state.GetValueOrDefault(field.FieldId);
            var editable = !readOnly && canWrite && (!field.IsSecurityAffecting || canWritePrivilege);
            result.Add(new CodedField(field, s?.Value, s?.ChangedAtVersion, s?.ChangedBy, s?.ChangedAt, s?.ChangedByJobId, editable));
        }

        var last = current.Fields
            .Where(f => !context.Restricted.Contains(f.FieldId) && context.Catalog.Find(f.FieldId) is { IsDeleted: false })
            .OrderByDescending(f => f.ChangedAtVersion).ThenByDescending(f => f.ChangedAt)
            .FirstOrDefault();
        CodingEditor? editor = null;
        if (last is not null)
        {
            var names = await users.GetDisplayNamesAsync([last.ChangedBy], cancellationToken).ConfigureAwait(false);
            editor = new CodingEditor(last.ChangedBy, names.GetValueOrDefault(last.ChangedBy), last.ChangedAt, last.ChangedAtVersion, last.ChangedByJobId);
        }

        var indexing = await outbox.GetDocumentStateAsync(ws, documentId, cancellationToken).ConfigureAwait(false);
        var (indexingState, projected) = indexing.OldestUnappliedVersion is { } oldest
            ? (indexing.HasFailed ? CodingIndexingState.Failed : CodingIndexingState.Pending, Math.Min(current.DocumentVersion, oldest - 1))
            : (CodingIndexingState.Indexed, current.DocumentVersion);
        return new DocumentCodingView(documentId, current.DocumentVersion, projected, indexingState, context.Layout?.LayoutId, result, editor);
    }

    private sealed record ViewContext(FieldCatalog Catalog, IReadOnlySet<int> Restricted, CodingLayout? Layout, HashSet<Permission> Permissions);
}
