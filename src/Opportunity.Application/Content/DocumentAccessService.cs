using System.Diagnostics;
using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Storage;
using Opportunity.Core.Security;

namespace Opportunity.Application.Content;

/// <summary>A document operation the gateway authorizes; each maps to one catalogued permission (Q-18).</summary>
public enum DocumentOperation
{
    View,
    DownloadNative,
    Print,
}

/// <summary>
/// The authoritative access service for protected document content (E05-T04, ADR-015 D5.4 PEP-3, D12, baseline §24).
/// Every decision is taken by the PDP against PostgreSQL at the time of the request (workspace role, restriction
/// classes, ethical walls, break-glass), never from OpenSearch, so a security-affecting change is enforced on the next
/// request whether or not the search projection has caught up.
/// </summary>
public interface IDocumentAccessService
{
    /// <summary>Batched authoritative check of many documents for one operation (one PostgreSQL round trip).</summary>
    Task<IReadOnlyDictionary<Guid, AuthorizationDecision>> AuthorizeAsync(
        SecurityPrincipal principal,
        Guid workspaceId,
        DocumentOperation operation,
        IReadOnlyCollection<Guid> documentIds,
        DenialAudit audit = DenialAudit.PerDocument,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// PDP check → locate the object in the registry → durable audit event → grant. Every attempt, allowed or not,
    /// writes exactly one <c>Document.*</c> event with the document ID, rendition and outcome. If the audit write
    /// fails, the exception propagates and nothing may be delivered (ADR-013 §2.2).
    /// </summary>
    /// <param name="deliveryMode">How the caller would deliver; presigning is used only for natives (ADR-015 D12.3).</param>
    Task<ContentAccessResult> OpenAsync(
        SecurityPrincipal principal, ContentRequest request, ObjectDeliveryMode deliveryMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// The viewer displayed the document as the active document (<c>Document.Viewed</c>, ADR-013 §5); re-checks
    /// <c>Document.View</c>. Prefetch alone never produces this event.
    /// </summary>
    Task<AuthorizationDecision> RecordViewedAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid documentId, Guid? retrievalId, CancellationToken cancellationToken = default);
}

public sealed class DocumentAccessService(
    IAuthorizationService authorization, IDocumentContentCatalog catalog, IAuditEventWriter audit, TimeProvider time)
    : IDocumentAccessService
{
    public const string ResourceType = "Document";

    /// <summary>Audit reason codes of the gateway beyond the PDP's (<see cref="AuthorizationReasons"/>).</summary>
    public static class Reasons
    {
        public const string RenditionUnavailable = "RenditionUnavailable";
        public const string Quarantined = "Quarantined";
        public const string ObjectKeyMismatch = "ObjectKeyMismatch";
        public const string RangeNotSatisfiable = "RangeNotSatisfiable";
    }

    public static Permission PermissionFor(DocumentOperation operation) => operation switch
    {
        DocumentOperation.View => Permission.DocumentView,
        DocumentOperation.DownloadNative => Permission.DocumentDownloadNative,
        DocumentOperation.Print => Permission.DocumentPrint,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    /// <summary>The operation a rendition/purpose pair needs; throws for combinations the gateway does not offer.</summary>
    public static DocumentOperation OperationFor(ContentRendition rendition, ContentPurpose purpose) => (rendition, purpose) switch
    {
        (ContentRendition.Native, ContentPurpose.Download) => DocumentOperation.DownloadNative,
        (ContentRendition.PageImage, ContentPurpose.Print) => DocumentOperation.Print,
        (ContentRendition.Text or ContentRendition.PageImage or ContentRendition.Thumbnail, ContentPurpose.Display or ContentPurpose.Prefetch)
            => DocumentOperation.View,
        _ => throw new ArgumentException($"{rendition} cannot be fetched for {purpose}.", nameof(purpose)),
    };

    public Task<IReadOnlyDictionary<Guid, AuthorizationDecision>> AuthorizeAsync(
        SecurityPrincipal principal,
        Guid workspaceId,
        DocumentOperation operation,
        IReadOnlyCollection<Guid> documentIds,
        DenialAudit audit = DenialAudit.PerDocument,
        CancellationToken cancellationToken = default) =>
        authorization.AuthorizeManyAsync(principal, workspaceId, PermissionFor(operation), documentIds, audit, cancellationToken);

    public async Task<ContentAccessResult> OpenAsync(
        SecurityPrincipal principal, ContentRequest request, ObjectDeliveryMode deliveryMode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var operation = OperationFor(request.Rendition, request.Purpose);
        var permission = PermissionFor(operation);
        var details = new Dictionary<string, string?>
        {
            ["rendition"] = request.Rendition.ToString(),
            ["purpose"] = request.Purpose.ToString(),
            ["permission"] = permission.Name(),
        };
        if (request.PageNumber is { } page)
        {
            details["page"] = page.ToString(CultureInfo.InvariantCulture);
        }

        // 1. Authoritative decision (PG, this request). The denial is audited below as the specific Document.* action.
        var decision = await DecideAsync(principal, request, permission, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await WriteAsync(principal, request, decision, AuditOutcome.Denied, decision.Reason, details, cancellationToken).ConfigureAwait(false);
            return ContentAccessResult.Denied(decision);
        }

        // 2. Locate the object in the registry (the PDP has allowed the document, so this cannot disclose anything).
        var content = await catalog.FindAsync(request.WorkspaceId, request.DocumentId, request.Rendition, request.PageNumber, cancellationToken)
            .ConfigureAwait(false);
        if (content is null)
        {
            // Deleted between the decision and the lookup: indistinguishable from a document that never existed.
            var gone = AuthorizationDecision.NotFound(AuthorizationReasons.DocumentNotFound);
            await WriteAsync(principal, request, gone, AuditOutcome.Denied, gone.Reason, details, cancellationToken).ConfigureAwait(false);
            return ContentAccessResult.Denied(gone);
        }

        if (content.Location is not { } location)
        {
            return await UnavailableAsync(principal, request, decision, Reasons.RenditionUnavailable, details, cancellationToken).ConfigureAwait(false);
        }

        details["objectId"] = location.ObjectId.ToString();

        // 3. Quarantine (ADR-015 D5.2 step 6, D16.2): a quarantined native needs Document.ViewQuarantined as well and is
        //    only ever downloaded; anything else quarantined is never served.
        if (location.Quarantined)
        {
            details["quarantined"] = "true";
            if (request.Rendition != ContentRendition.Native)
            {
                return await UnavailableAsync(principal, request, decision, Reasons.Quarantined, details, cancellationToken).ConfigureAwait(false);
            }

            var quarantine = await DecideAsync(principal, request, Permission.DocumentViewQuarantined, cancellationToken).ConfigureAwait(false);
            if (!quarantine.IsAllowed)
            {
                details["permission"] = Permission.DocumentViewQuarantined.Name();
                await WriteAsync(principal, request, quarantine, AuditOutcome.Denied, quarantine.Reason, details, cancellationToken).ConfigureAwait(false);
                return ContentAccessResult.Denied(quarantine);
            }
        }

        // 4. The registry row must point inside this document of this workspace (defence in depth against a corrupted
        //    or tampered reference; RLS already confines the row to the workspace).
        if (!ObjectKey.TryParse(location.LogicalKey, out var key) || key.WorkspaceId != request.WorkspaceId
            || key.DocumentId != request.DocumentId || !AreaMatches(request.Rendition, key.Area) || location.Sha256.Length != 32)
        {
            return await UnavailableAsync(principal, request, decision, Reasons.ObjectKeyMismatch, details, cancellationToken).ConfigureAwait(false);
        }

        var delivery = deliveryMode == ObjectDeliveryMode.Presign && request.Rendition == ContentRendition.Native && PresignPolicy.MayPresignGet(key)
            ? ObjectDeliveryMode.Presign
            : ObjectDeliveryMode.Stream;
        details["deliveryMode"] = delivery.ToString();

        ByteRange? range = null;
        if (delivery == ObjectDeliveryMode.Stream && request.Range is { } requested)
        {
            details["range"] = requested.ToString();
            range = requested.Resolve(location.Length);
            if (range is null)
            {
                await WriteAsync(principal, request, decision, AuditOutcome.Failure, Reasons.RangeNotSatisfiable, details, cancellationToken)
                    .ConfigureAwait(false);
                return new ContentAccessResult(ContentAccessOutcome.RangeNotSatisfiable, decision, null, location.Length);
            }
        }

        // 5. Durable audit before any byte or URL leaves (ADR-011 §5.1, ADR-013 §2.2).
        var eventId = await WriteAsync(principal, request, decision, AuditOutcome.Success, null, details, cancellationToken).ConfigureAwait(false);
        var fileName = request.Rendition == ContentRendition.Native ? DownloadFileName(content) : null;
        return new ContentAccessResult(
            ContentAccessOutcome.Granted,
            decision,
            new ContentGrant(
                eventId,
                key,
                Sha256Digest.FromBytes(location.Sha256),
                location.Length,
                location.ContentType,
                delivery,
                range,
                fileName,
                decision.BreakGlass),
            location.Length);
    }

    public async Task<AuthorizationDecision> RecordViewedAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid documentId, Guid? retrievalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var decisions = await authorization.AuthorizeManyAsync(
            principal, workspaceId, Permission.DocumentView, [documentId], DenialAudit.Caller, cancellationToken).ConfigureAwait(false);
        var decision = decisions[documentId];
        var details = new Dictionary<string, string?> { ["permission"] = Permission.DocumentView.Name() };
        if (retrievalId is { } id)
        {
            details["retrievedEventId"] = id.ToString();
        }

        var breakGlass = decision.BreakGlass || await BreakGlassActiveAsync(principal, workspaceId, decision, cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(Event(principal, workspaceId, documentId, AuditTaxonomy.Document.Viewed, breakGlass,
            decision.IsAllowed ? AuditOutcome.Success : AuditOutcome.Denied, decision.IsAllowed ? null : decision.Reason, details), cancellationToken)
            .ConfigureAwait(false);
        return decision;
    }

    /// <summary>Sanitized <c>ControlNumber.ext</c>; the extension is used only when it is short and alphanumeric.</summary>
    public static string DownloadFileName(DocumentContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var extension = content.FileExtension?.Trim().TrimStart('.');
        var name = extension is { Length: > 0 and <= 10 } && extension.All(char.IsAsciiLetterOrDigit)
            ? $"{content.ControlNumber}.{extension.ToLowerInvariant()}"
            : content.ControlNumber;
        return ContentDispositionHeader.Sanitize(name);
    }

    private static bool AreaMatches(ContentRendition rendition, ObjectArea area) => rendition switch
    {
        ContentRendition.Native => area == ObjectArea.Native,
        ContentRendition.Text => area == ObjectArea.Text,
        _ => area is ObjectArea.Image or ObjectArea.Rendition,
    };

    private async Task<AuthorizationDecision> DecideAsync(
        SecurityPrincipal principal, ContentRequest request, Permission permission, CancellationToken cancellationToken)
    {
        var decisions = await authorization.AuthorizeManyAsync(
            principal, request.WorkspaceId, permission, [request.DocumentId], DenialAudit.Caller, cancellationToken).ConfigureAwait(false);
        return decisions[request.DocumentId];
    }

    /// <summary>
    /// Whether a member acts under an active break-glass activation, so a refused or failed attempt is still audited on
    /// the break-glass path (ADR-015 D6.4). Reuses the principal state the PDP cached for this scope: no extra read.
    /// </summary>
    private async Task<bool> BreakGlassActiveAsync(SecurityPrincipal principal, Guid workspaceId, AuthorizationDecision decision, CancellationToken cancellationToken)
    {
        if (decision.Reason is AuthorizationReasons.NotAMember or AuthorizationReasons.WorkspaceNotFound or AuthorizationReasons.Unauthenticated)
        {
            return false;
        }

        var effective = await authorization.GetEffectivePermissionsAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        return effective.BreakGlass;
    }

    private async Task<ContentAccessResult> UnavailableAsync(
        SecurityPrincipal principal,
        ContentRequest request,
        AuthorizationDecision decision,
        string reason,
        Dictionary<string, string?> details,
        CancellationToken cancellationToken)
    {
        await WriteAsync(principal, request, decision, AuditOutcome.Failure, reason, details, cancellationToken).ConfigureAwait(false);
        return new ContentAccessResult(ContentAccessOutcome.Unavailable, decision, null);
    }

    private async Task<Guid> WriteAsync(
        SecurityPrincipal principal,
        ContentRequest request,
        AuthorizationDecision decision,
        AuditOutcome outcome,
        string? reason,
        Dictionary<string, string?> details,
        CancellationToken cancellationToken)
    {
        var action = request.Rendition == ContentRendition.Native ? AuditTaxonomy.Document.NativeDownloaded
            : request.Purpose == ContentPurpose.Print ? AuditTaxonomy.Document.Printed
            : AuditTaxonomy.Document.Retrieved;
        var breakGlass = decision.BreakGlass || await BreakGlassActiveAsync(principal, request.WorkspaceId, decision, cancellationToken).ConfigureAwait(false);
        var auditEvent = Event(principal, request.WorkspaceId, request.DocumentId, action, breakGlass, outcome, reason, new Dictionary<string, string?>(details));
        await audit.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        return auditEvent.EventId;
    }

    private AuditEvent Event(
        SecurityPrincipal principal,
        Guid workspaceId,
        Guid documentId,
        string action,
        bool breakGlass,
        AuditOutcome outcome,
        string? reason,
        Dictionary<string, string?> details) => new()
        {
            WorkspaceId = workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Document.Category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId.ToString(),
            ActorDisplay = principal.DisplayName,
            AccessPath = breakGlass ? AuditAccessPath.BreakGlass : AuditAccessPath.Normal,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            ResourceType = ResourceType,
            ResourceId = documentId.ToString(),
            Outcome = outcome,
            ReasonCode = reason,
            CorrelationId = principal.CorrelationId ?? Activity.Current?.TraceId.ToHexString(),
            Details = details,
        };
}
