using Opportunity.Core.Security;

namespace Opportunity.Application.Authorization;

/// <summary>
/// Who is asking (ADR-015 D3). Built from the server-side session by the API, or rebuilt from PostgreSQL by a worker
/// acting for a job's initiator (D9.4). <see cref="Groups"/> is the IdP group snapshot (D3.4); everything else the
/// decision needs (roles, grants, walls, break-glass) is read from PostgreSQL per request.
/// </summary>
public sealed record SecurityPrincipal
{
    public required Guid UserId { get; init; }

    public required string DisplayName { get; init; }

    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>Request context for audit only; never used in a decision.</summary>
    public string? ClientIp { get; init; }

    public string? UserAgent { get; init; }

    public string? CorrelationId { get; init; }
}

public enum AuthorizationOutcome
{
    Allow,

    /// <summary>The resource is visible but the operation is not permitted: 403.</summary>
    Deny,

    /// <summary>The workspace or document must look as if it does not exist (ADR-015 D5.3): 404.</summary>
    NotFound,
}

/// <summary>Reason codes. The true reason goes to audit only; clients see 403 or 404 without it (D5.3).</summary>
public static class AuthorizationReasons
{
    public const string Granted = "Granted";
    public const string Unauthenticated = "Unauthenticated";
    public const string WorkspaceNotFound = "WorkspaceNotFound";
    public const string NotAMember = "NotAMember";
    public const string PermissionNotGranted = "PermissionNotGranted";
    public const string DocumentNotFound = "DocumentNotFound";
    public const string RestrictionClass = "RestrictionClass";
    public const string EthicalWall = "EthicalWall";

    /// <summary>The member has not accepted the workspace's current acknowledgment text (E20-T03).</summary>
    public const string AcknowledgmentRequired = "AcknowledgmentRequired";
}

/// <summary>One decision. <see cref="BreakGlass"/> is true when the allow relied on an active activation (D6.4).</summary>
public readonly record struct AuthorizationDecision(AuthorizationOutcome Outcome, string Reason, bool BreakGlass = false)
{
    public bool IsAllowed => Outcome == AuthorizationOutcome.Allow;

    public static AuthorizationDecision Allow(bool breakGlass = false) => new(AuthorizationOutcome.Allow, AuthorizationReasons.Granted, breakGlass);

    public static AuthorizationDecision Deny(string reason) => new(AuthorizationOutcome.Deny, reason);

    public static AuthorizationDecision NotFound(string reason) => new(AuthorizationOutcome.NotFound, reason);
}

/// <summary>
/// The principal side of search and list filtering (ADR-015 D5.1, D7.6, D8.1): documents carrying any of
/// <see cref="DeniedClasses"/> or covered by any of <see cref="WallIds"/> are invisible. Both are empty while a
/// break-glass activation is active.
/// </summary>
public sealed record VisibilityFilter(IReadOnlyList<string> DeniedClasses, IReadOnlyList<Guid> WallIds, bool BreakGlass);

/// <summary><see cref="Filter"/> is set only when <see cref="Decision"/> allows <c>Search.Execute</c>.</summary>
public sealed record VisibilityResult(AuthorizationDecision Decision, VisibilityFilter? Filter);

/// <summary>
/// Would the principal be allowed <c>permission</c> on a document carrying these security attributes (the PDP's document
/// step, evaluated against principal-side state read once)? Pure and audit-free: the coding store asks it inside its
/// transaction about the attributes a write is about to commit (E16-T08: a save that hides the document from its author).
/// </summary>
public delegate bool DocumentAccessCheck(DocumentSecurityAttributes document);

/// <summary>Membership decision plus the permissions held (break-glass ones only while an activation is live).</summary>
public sealed record EffectivePermissions(AuthorizationDecision Decision, IReadOnlyList<Permission> Permissions, bool BreakGlass);

/// <summary>How <see cref="IAuthorizationService.AuthorizeManyAsync"/> audits denials.</summary>
public enum DenialAudit
{
    /// <summary>One <c>AuthZ.Denied</c> event per denied document (use cases, job chunks).</summary>
    PerDocument,

    /// <summary>
    /// One <c>AuthZ.Denied</c> event per call carrying the denied count by reason, for the search page post-filter
    /// (Q-12), whose per-hit drops ADR-015 D8.4 keeps out of the audit trail.
    /// </summary>
    Summary,

    /// <summary>
    /// No <c>AuthZ.Denied</c> event: the caller records the denial (with the true reason) in its own, more specific
    /// action event, e.g. <c>Document.Retrieved</c> with outcome Denied (ADR-013 §4: one event per attempted action;
    /// <c>AuthZ.Denied</c> only when no more specific action applies). Used by the content gateway.
    /// </summary>
    Caller,
}

/// <summary>
/// The single policy decision point (ADR-015 D5). Default deny; deny overrides allow; the only negative rule is an
/// ethical wall. Every call reads PostgreSQL (principal-side state at most once per DI scope, i.e. once per request or
/// job chunk), and every non-allow decision writes an <c>AuthZ.Denied</c> audit event. Endpoints, use cases, the
/// content gateway, search and workers call this; none re-implements policy.
/// </summary>
public interface IAuthorizationService
{
    /// <summary>Workspace-level check: membership (else NotFound) and the permission (else Deny).</summary>
    Task<AuthorizationDecision> AuthorizeAsync(
        SecurityPrincipal principal, Guid workspaceId, Permission permission, CancellationToken cancellationToken = default);

    /// <summary>Document-level check: also restriction classes and walls (NotFound), and existence.</summary>
    Task<AuthorizationDecision> AuthorizeAsync(
        SecurityPrincipal principal, Guid workspaceId, Permission permission, Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batched document check (search post-filter, bulk operations, export chunks). Returns a decision for every
    /// distinct requested ID. One PostgreSQL round trip per call.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, AuthorizationDecision>> AuthorizeManyAsync(
        SecurityPrincipal principal,
        Guid workspaceId,
        Permission permission,
        IReadOnlyCollection<Guid> documentIds,
        DenialAudit audit = DenialAudit.PerDocument,
        CancellationToken cancellationToken = default);

    /// <summary>Membership only, for endpoints whose permission depends on the resource (e.g. own jobs).</summary>
    Task<AuthorizationDecision> AuthorizeMembershipAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Break-glass activation (Q-45, ADR-015 D6.4): allowed only for the holder of a BreakGlass role assignment in the
    /// workspace, whether or not an activation is open; other members get Deny, everyone else NotFound (audited).
    /// </summary>
    Task<AuthorizationDecision> AuthorizeBreakGlassHolderAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The workspace-level permissions the principal holds now (for UI affordances only; every operation is still
    /// authorized on its own). <see cref="EffectivePermissions.Permissions"/> is empty unless the decision allows.
    /// </summary>
    Task<EffectivePermissions> GetEffectivePermissionsAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>The filter search and document lists apply in their queries (requires <c>Search.Execute</c>).</summary>
    Task<VisibilityResult> GetVisibilityAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The document step of the policy for <paramref name="permission"/> as a check over security attributes, for
    /// "would the caller still see it after this change" questions. Writes no audit event; never grants anything itself.
    /// </summary>
    Task<DocumentAccessCheck> GetDocumentAccessCheckAsync(
        SecurityPrincipal principal, Guid workspaceId, Permission permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// The acknowledgment gate (E20-T03) from the principal-side state already read in this scope: the workspace's current
    /// acknowledgment version and whether the principal accepted it. Writes no audit event.
    /// </summary>
    Task<AcknowledgmentGateState> GetAcknowledgmentGateAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// PEP-1's acknowledgment step (E20-T03): Allow when the workspace requires no acknowledgment or the principal accepted
    /// its current version, otherwise Deny with <see cref="AuthorizationReasons.AcknowledgmentRequired"/> (audited).
    /// </summary>
    Task<AuthorizationDecision> AuthorizeAcknowledgmentAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default);
}
