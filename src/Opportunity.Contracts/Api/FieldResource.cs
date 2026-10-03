namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}/fields</c> (ADR-007 R8, ADR-003 R16): one field of the catalogue with the
/// search capabilities the grid and query bar may offer. Clients never derive capabilities from the type.
/// </summary>
/// <param name="FieldId">Stable id; system fields are 1–999, custom fields 1000+.</param>
/// <param name="DisplayName">Current display name.</param>
/// <param name="QueryName">Name used as <c>field:</c> in the query language (ADR-008 R11).</param>
/// <param name="Type">One of the nine field types.</param>
/// <param name="Storage">Where values live: structural column, imported metadata or coding.</param>
/// <param name="MultiValue">The field holds several values per document.</param>
/// <param name="IsSystem">Seeded system field (renameable and hideable, never deleted).</param>
/// <param name="IsHidden">Hidden from default views.</param>
/// <param name="IsSecurityAffecting">Values decide document visibility (Q-11).</param>
/// <param name="DatePrecision">Date fields only.</param>
/// <param name="Capabilities">What search can do with the field.</param>
/// <param name="ReducedCapabilities">The field lives in the overflow container (ADR-007 R6): exact and prefix match only.</param>
public sealed record FieldResource(
    int FieldId,
    string DisplayName,
    string QueryName,
    FieldResourceType Type,
    FieldResourceStorage Storage,
    bool MultiValue,
    bool IsSystem,
    bool IsHidden,
    bool IsSecurityAffecting,
    FieldResourceDatePrecision? DatePrecision,
    FieldCapabilitiesResource Capabilities,
    bool ReducedCapabilities);

/// <summary>ADR-007 R8 capability flags. All false for a field that is not searchable.</summary>
public sealed record FieldCapabilitiesResource(
    bool Sortable,
    bool Filterable,
    bool Rangeable,
    bool Aggregatable,
    bool FullText,
    bool Wildcard,
    bool LeadingWildcard,
    bool Highlightable,
    bool Exists);

/// <summary>The nine field types of §6.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Names of the §6 field types.")]
public enum FieldResourceType
{
    Text,
    Keyword,
    Integer,
    Decimal,
    Date,
    Boolean,
    SingleChoice,
    MultiChoice,
    User,
}

public enum FieldResourceStorage
{
    Column,
    Metadata,
    Coding,
}

public enum FieldResourceDatePrecision
{
    Date,
    DateTime,
}
