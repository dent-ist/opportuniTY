using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Core.Fields;

namespace Opportunity.Security.Authorization;

/// <summary>
/// The <see cref="IFieldAccessFilter"/> until field-level restrictions exist (E05-T06): every member sees every field.
/// Document-level restrictions and walls (Q-11, Q-13) are unaffected; they hide documents, not fields.
/// </summary>
public sealed class UnrestrictedFieldAccess : IFieldAccessFilter
{
    private static readonly IReadOnlySet<int> None = new HashSet<int>();

    public ValueTask<IReadOnlySet<int>> RestrictedFieldIdsAsync(
        Guid workspaceId, SecurityPrincipal principal, FieldCatalog catalog, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(None);
}
