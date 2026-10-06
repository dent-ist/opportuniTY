using Opportunity.Application.Authorization;
using Opportunity.Core.Fields;

namespace Opportunity.Application.Fields;

/// <summary>
/// Field-level restrictions (E05-T06, ADR-015): which fields a principal may not see at all, and which it sees but may
/// not change. Every surface that lists or returns fields (field catalogue, query bar suggestions, search field
/// binding, sort and result columns, document metadata, coding, coding layouts, relationships, exports and productions)
/// omits hidden fields together with their choices; a hidden field answers exactly like a field that does not exist.
/// The default (<c>UnrestrictedFieldAccess</c>) restricts nothing; hosts with PostgreSQL register the stored policy.
/// </summary>
public interface IFieldAccessFilter
{
    /// <summary>IDs of the fields of <paramref name="catalog"/> that <paramref name="principal"/> may not see.</summary>
    ValueTask<IReadOnlySet<int>> RestrictedFieldIdsAsync(
        Guid workspaceId, SecurityPrincipal principal, FieldCatalog catalog, CancellationToken cancellationToken = default);

    /// <summary>IDs of visible fields of <paramref name="catalog"/> that <paramref name="principal"/> may not change.</summary>
    ValueTask<IReadOnlySet<int>> ReadOnlyFieldIdsAsync(
        Guid workspaceId, SecurityPrincipal principal, FieldCatalog catalog, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlySet<int>>(new HashSet<int>());
}
