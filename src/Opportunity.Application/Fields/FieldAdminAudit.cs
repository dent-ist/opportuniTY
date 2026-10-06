using System.Diagnostics;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;

namespace Opportunity.Application.Fields;

/// <summary>
/// Audit events of field and coding layout administration (E04-T06, <c>Workspace</c> / <c>Field.*</c>,
/// <c>CodingLayout.*</c>). Details are IDs and enums only (ADR-013 §4); the store adds the resource id and the new
/// version and writes the event in the transaction of the change.
/// </summary>
public static class FieldAdminAudit
{
    private const string CorrelationTag = "opportunity.correlation_id";

    public static AuditEvent Field(SecurityPrincipal actor, Guid workspaceId, string action, DateTimeOffset now, IReadOnlyDictionary<string, string?> details) =>
        Event(actor, workspaceId, action, AuditTaxonomy.FieldCatalog.FieldResourceType, now, details);

    public static AuditEvent Layout(SecurityPrincipal actor, Guid workspaceId, string action, DateTimeOffset now, IReadOnlyDictionary<string, string?> details) =>
        Event(actor, workspaceId, action, AuditTaxonomy.FieldCatalog.LayoutResourceType, now, details);

    private static AuditEvent Event(
        SecurityPrincipal actor, Guid workspaceId, string action, string resourceType, DateTimeOffset now, IReadOnlyDictionary<string, string?> details)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditEvent
        {
            WorkspaceId = workspaceId,
            OccurredAt = now,
            Category = AuditTaxonomy.FieldCatalog.Category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = actor.UserId.ToString(),
            ActorDisplay = actor.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? actor.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : actor.DisplayName,
            ClientIp = actor.ClientIp,
            UserAgent = actor.UserAgent,
            ResourceType = resourceType,
            Outcome = AuditOutcome.Success,
            CorrelationId = actor.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = details,
        };
    }
}
