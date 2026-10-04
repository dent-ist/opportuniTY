using Opportunity.Application.Authorization;
using Opportunity.Core.Fields;

namespace Opportunity.Application.Fields;

/// <summary>
/// Field-level restrictions (E05-T06, ADR-015): which fields a principal may not see at all. Every surface that lists
/// fields (the field catalogue and the query bar's search field list today; grid columns and exports later) omits
/// them, together with their choices. Field restrictions are not modelled yet, so the default
/// (<c>UnrestrictedFieldAccess</c>) restricts nothing; E05-T06 replaces it with a PostgreSQL-backed policy.
/// </summary>
public interface IFieldAccessFilter
{
    /// <summary>IDs of the fields of <paramref name="catalog"/> that <paramref name="principal"/> may not see.</summary>
    ValueTask<IReadOnlySet<int>> RestrictedFieldIdsAsync(
        Guid workspaceId, SecurityPrincipal principal, FieldCatalog catalog, CancellationToken cancellationToken = default);
}
