namespace Opportunity.Core.Fields;

/// <summary>The nine field types of §6 / ADR-003 §3. Stored as smallint; values are fixed forever.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Names of the §6 field types.")]
public enum FieldType : short
{
    Text = 1,
    Keyword = 2,
    Integer = 3,
    Decimal = 4,
    Date = 5,
    Boolean = 6,
    SingleChoice = 7,
    MultiChoice = 8,
    User = 9,
}

/// <summary>Where a field's values live (ADR-003 §1). Stored as smallint.</summary>
public enum FieldStorage : short
{
    /// <summary>A typed structural column on <c>Document</c>; system fields only.</summary>
    Column = 1,

    /// <summary><c>Document.Metadata</c> JSONB under key <c>"f" + FieldId</c>; written by import/overlay only.</summary>
    Metadata = 2,

    /// <summary>The coding current-state store (interim PostgreSQL model, ADR-004a); written by review.</summary>
    Coding = 3,
}

/// <summary>Canonical form of a Date field (ADR-003 §3 amendment): calendar date or UTC instant.</summary>
public enum DatePrecision : short
{
    Date = 1,
    DateTime = 2,
}

/// <summary>Search analysis of a Text field (ADR-007 R5).</summary>
public enum TextAnalysis : short
{
    Prose = 1,
    Identifier = 2,
}

/// <summary>
/// Restricted classes of security-affecting fields (Q-11). A change to such a field alters document visibility, so it
/// takes the security lanes of ADR-001 §5 and smaller bulk chunks (ADR-010 §6).
/// </summary>
public enum SecurityClass : short
{
    PrivilegeStatus = 1,
    ConfidentialityDesignation = 2,
    EthicalWall = 3,
}

/// <summary>Search capabilities served to the UI (ADR-003 R16, ADR-007 R8). Clients never derive them from the type.</summary>
[Flags]
public enum FieldCapabilities
{
    None = 0,
    Sortable = 1,
    Filterable = 2,
    Rangeable = 4,
    Aggregatable = 8,
    FullText = 16,
    Wildcard = 32,
    LeadingWildcard = 64,
    Highlightable = 128,
    Exists = 256,
}
