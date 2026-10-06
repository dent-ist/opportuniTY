using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Security;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;

namespace Opportunity.Security.Authorization;

/// <summary>
/// Field-level restrictions (E05-T06) as a pure function of the stored restrictions and the roles a principal holds:
/// a restricted field is visible only with a role in its visible list and editable only with a role in its editable
/// list. Unrestricted fields are visible and editable (the operation's own permission still applies). Break-glass is
/// never a field grant: an activation lifts restriction classes and walls (ADR-015 D6.4), not field restrictions.
/// </summary>
public static class FieldAccessPolicy
{
    public static (IReadOnlySet<int> Hidden, IReadOnlySet<int> ReadOnly) Evaluate(FieldAccessState state, FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        var hidden = new HashSet<int>();
        var readOnly = new HashSet<int>();
        foreach (var restriction in state.Restrictions)
        {
            if (catalog.Find(restriction.FieldId) is null)
            {
                continue;
            }

            if (!Holds(state.Roles, restriction.VisibleTo))
            {
                hidden.Add(restriction.FieldId);
            }
            else if (!Holds(state.Roles, restriction.EditableBy))
            {
                readOnly.Add(restriction.FieldId);
            }
        }

        return (hidden, readOnly);
    }

    private static bool Holds(IReadOnlySet<WorkspaceRole> held, IReadOnlyList<WorkspaceRole> granted) =>
        granted.Any(r => r != WorkspaceRole.BreakGlass && held.Contains(r));
}

/// <summary>The PostgreSQL-backed <see cref="IFieldAccessFilter"/>: one read of roles and restrictions per call.</summary>
public sealed class StoredFieldAccess(IDocumentSecurityStore store) : IFieldAccessFilter
{
    public async ValueTask<IReadOnlySet<int>> RestrictedFieldIdsAsync(
        Guid workspaceId, SecurityPrincipal principal, FieldCatalog catalog, CancellationToken cancellationToken = default)
    {
        var state = await store.ReadFieldAccessAsync(workspaceId, principal, cancellationToken).ConfigureAwait(false);
        return FieldAccessPolicy.Evaluate(state, catalog).Hidden;
    }

    public async ValueTask<IReadOnlySet<int>> ReadOnlyFieldIdsAsync(
        Guid workspaceId, SecurityPrincipal principal, FieldCatalog catalog, CancellationToken cancellationToken = default)
    {
        var state = await store.ReadFieldAccessAsync(workspaceId, principal, cancellationToken).ConfigureAwait(false);
        return FieldAccessPolicy.Evaluate(state, catalog).ReadOnly;
    }
}
