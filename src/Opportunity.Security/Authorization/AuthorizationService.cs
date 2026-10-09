using System.Diagnostics;
using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Core.Security;

namespace Opportunity.Security.Authorization;

/// <summary>
/// The PDP (ADR-015 D5). Scoped: the principal-side state of a workspace is read once per scope (one HTTP request or
/// one job chunk, D5.5) and reused by later checks in that scope; document attributes are read on every call.
/// </summary>
internal sealed class AuthorizationService(ISecurityStateReader reader, IAuditEventWriter audit, TimeProvider time)
    : IAuthorizationService
{
    private const string ResourceWorkspace = "Workspace";
    private const string ResourceDocument = "Document";
    private const string CorrelationTag = "opportunity.correlation_id";

    private readonly Dictionary<(Guid UserId, Guid WorkspaceId), PrincipalSecurityState?> _principalStates = [];

    public async Task<AuthorizationDecision> AuthorizeAsync(
        SecurityPrincipal principal, Guid workspaceId, Permission permission, CancellationToken cancellationToken = default)
    {
        var decision = Unauthenticated(principal)
            ?? PolicyEvaluator.EvaluateWorkspace(await PrincipalStateAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false), permission, time.GetUtcNow());
        await AuditAsync(principal, workspaceId, permission, decision, null, cancellationToken).ConfigureAwait(false);
        return decision;
    }

    public async Task<AuthorizationDecision> AuthorizeAsync(
        SecurityPrincipal principal, Guid workspaceId, Permission permission, Guid documentId, CancellationToken cancellationToken = default)
    {
        var decisions = await AuthorizeManyAsync(principal, workspaceId, permission, [documentId], DenialAudit.PerDocument, cancellationToken)
            .ConfigureAwait(false);
        return decisions[documentId];
    }

    public async Task<IReadOnlyDictionary<Guid, AuthorizationDecision>> AuthorizeManyAsync(
        SecurityPrincipal principal,
        Guid workspaceId,
        Permission permission,
        IReadOnlyCollection<Guid> documentIds,
        DenialAudit audit = DenialAudit.PerDocument,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        _ = PermissionCatalog.Get(permission);
        var ids = documentIds.Distinct().ToList();
        var results = new Dictionary<Guid, AuthorizationDecision>(ids.Count);
        if (ids.Count == 0)
        {
            return results;
        }

        PrincipalSecurityState? state = null;
        IReadOnlyDictionary<Guid, DocumentSecurityAttributes> documents = new Dictionary<Guid, DocumentSecurityAttributes>();
        var workspaceDecision = Unauthenticated(principal);
        if (workspaceDecision is null)
        {
            var cached = _principalStates.TryGetValue((principal.UserId, workspaceId), out state);
            if (cached)
            {
                workspaceDecision = PolicyEvaluator.EvaluateWorkspace(state, permission, time.GetUtcNow());
            }

            // A principal known to fail the workspace steps never causes a document read.
            if (workspaceDecision is null || workspaceDecision.Value.IsAllowed)
            {
                var read = await reader.ReadAsync(workspaceId, principal, includePrincipal: !cached, ids, cancellationToken).ConfigureAwait(false);
                if (!cached)
                {
                    state = read.Principal;
                    _principalStates[(principal.UserId, workspaceId)] = state;
                    workspaceDecision = null;
                }

                documents = read.Documents;
            }
        }

        var now = time.GetUtcNow();
        workspaceDecision ??= PolicyEvaluator.EvaluateWorkspace(state, permission, now);
        if (!workspaceDecision.Value.IsAllowed)
        {
            // Membership or permission failed: one workspace-level event, the same decision for every ID.
            foreach (var id in ids)
            {
                results[id] = workspaceDecision.Value;
            }

            if (audit != DenialAudit.Caller)
            {
                await AuditAsync(principal, workspaceId, permission, workspaceDecision.Value, null, cancellationToken).ConfigureAwait(false);
            }

            return results;
        }

        foreach (var id in ids)
        {
            results[id] = PolicyEvaluator.EvaluateDocument(state, permission, documents.GetValueOrDefault(id), now);
        }

        await AuditManyAsync(principal, workspaceId, permission, results, audit, cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<AuthorizationDecision> AuthorizeMembershipAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var decision = Unauthenticated(principal)
            ?? PolicyEvaluator.EvaluateWorkspace(await PrincipalStateAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false), null, time.GetUtcNow());
        await AuditAsync(principal, workspaceId, null, decision, null, cancellationToken).ConfigureAwait(false);
        return decision;
    }

    public async Task<AuthorizationDecision> AuthorizeBreakGlassHolderAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var decision = Unauthenticated(principal)
            ?? PolicyEvaluator.EvaluateBreakGlassHolder(await PrincipalStateAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false));
        await AuditAsync(principal, workspaceId, null, decision, null, cancellationToken).ConfigureAwait(false);
        return decision;
    }

    public async Task<EffectivePermissions> GetEffectivePermissionsAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var state = Unauthenticated(principal) is null
            ? await PrincipalStateAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false)
            : null;
        var decision = Unauthenticated(principal) ?? PolicyEvaluator.EvaluateWorkspace(state, null, now);
        await AuditAsync(principal, workspaceId, null, decision, null, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return new EffectivePermissions(decision, [], BreakGlass: false);
        }

        var permissions = PermissionCatalog.All
            .Select(p => p.Permission)
            .Where(p => PolicyEvaluator.EvaluateWorkspace(state, p, now).IsAllowed)
            .ToList();
        return new EffectivePermissions(decision, permissions, PolicyEvaluator.BreakGlassActive(state!, now));
    }

    public async Task<VisibilityResult> GetVisibilityAsync(
        SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var state = Unauthenticated(principal) is null
            ? await PrincipalStateAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false)
            : null;
        var decision = Unauthenticated(principal) ?? PolicyEvaluator.EvaluateWorkspace(state, Permission.SearchExecute, now);
        await AuditAsync(principal, workspaceId, Permission.SearchExecute, decision, null, cancellationToken).ConfigureAwait(false);
        return new VisibilityResult(decision, decision.IsAllowed ? PolicyEvaluator.Visibility(state!, now) : null);
    }

    public async Task<DocumentAccessCheck> GetDocumentAccessCheckAsync(
        SecurityPrincipal principal, Guid workspaceId, Permission permission, CancellationToken cancellationToken = default)
    {
        _ = PermissionCatalog.Get(permission);
        if (Unauthenticated(principal) is not null)
        {
            return _ => false;
        }

        var state = await PrincipalStateAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        return document => PolicyEvaluator.EvaluateDocument(state, permission, document, time.GetUtcNow()).IsAllowed;
    }

    private static AuthorizationDecision? Unauthenticated(SecurityPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.UserId == Guid.Empty ? AuthorizationDecision.Deny(AuthorizationReasons.Unauthenticated) : null;
    }

    private async Task<PrincipalSecurityState?> PrincipalStateAsync(SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (_principalStates.TryGetValue((principal.UserId, workspaceId), out var state))
        {
            return state;
        }

        var read = await reader.ReadAsync(workspaceId, principal, includePrincipal: true, documentIds: null, cancellationToken).ConfigureAwait(false);
        _principalStates[(principal.UserId, workspaceId)] = read.Principal;
        return read.Principal;
    }

    private async Task AuditManyAsync(
        SecurityPrincipal principal,
        Guid workspaceId,
        Permission permission,
        Dictionary<Guid, AuthorizationDecision> results,
        DenialAudit mode,
        CancellationToken cancellationToken)
    {
        var denied = results.Where(r => !r.Value.IsAllowed).ToList();
        if (denied.Count == 0 || mode == DenialAudit.Caller)
        {
            return;
        }

        if (mode == DenialAudit.PerDocument)
        {
            foreach (var (documentId, decision) in denied)
            {
                await AuditAsync(principal, workspaceId, permission, decision, documentId, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var details = new Dictionary<string, string?>
        {
            ["permission"] = permission.Name(),
            ["requested"] = results.Count.ToString(CultureInfo.InvariantCulture),
            ["denied"] = denied.Count.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var group in denied.GroupBy(d => d.Value.Reason).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            details["denied." + group.Key] = group.Count().ToString(CultureInfo.InvariantCulture);
        }

        var reason = denied.Select(d => d.Value.Reason).Distinct().Count() == 1 ? denied[0].Value.Reason : "Multiple";
        await WriteDeniedAsync(principal, workspaceId, reason, ResourceWorkspace, workspaceId.ToString(), details, cancellationToken).ConfigureAwait(false);
    }

    private Task AuditAsync(
        SecurityPrincipal principal,
        Guid workspaceId,
        Permission? permission,
        AuthorizationDecision decision,
        Guid? documentId,
        CancellationToken cancellationToken)
    {
        if (decision.IsAllowed)
        {
            return Task.CompletedTask;
        }

        var details = new Dictionary<string, string?> { ["permission"] = permission?.Name() ?? "Workspace.Member" };
        return documentId is { } id
            ? WriteDeniedAsync(principal, workspaceId, decision.Reason, ResourceDocument, id.ToString(), details, cancellationToken)
            : WriteDeniedAsync(principal, workspaceId, decision.Reason, ResourceWorkspace, workspaceId.ToString(), details, cancellationToken);
    }

    private async Task WriteDeniedAsync(
        SecurityPrincipal principal,
        Guid workspaceId,
        string reason,
        string resourceType,
        string resourceId,
        Dictionary<string, string?> details,
        CancellationToken cancellationToken)
    {
        var breakGlass = _principalStates.TryGetValue((principal.UserId, workspaceId), out var state)
            && state is not null && PolicyEvaluator.BreakGlassActive(state, time.GetUtcNow());
        // A workspace that does not exist has no audit chain: the attempt goes to the system chain with the claimed ID.
        var unknownWorkspace = reason == AuthorizationReasons.WorkspaceNotFound;
        if (unknownWorkspace)
        {
            details["workspaceId"] = workspaceId.ToString();
        }

        await audit.WriteAsync(new AuditEvent
        {
            WorkspaceId = unknownWorkspace ? null : workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.AuthZ.Category,
            Action = AuditTaxonomy.AuthZ.Denied,
            ActorType = principal.UserId == Guid.Empty ? AuditActorType.System : AuditActorType.User,
            ActorId = principal.UserId == Guid.Empty ? "anonymous" : principal.UserId.ToString(),
            ActorDisplay = principal.DisplayName,
            AccessPath = breakGlass ? AuditAccessPath.BreakGlass : AuditAccessPath.Normal,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Outcome = AuditOutcome.Denied,
            ReasonCode = reason,
            CorrelationId = principal.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = details,
        }, cancellationToken).ConfigureAwait(false);
    }
}
