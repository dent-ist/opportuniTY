namespace Opportunity.Contracts.Api;

// Field and coding layout administration (E04-T06, Admin › Fields / Choices / Coding Layouts). Every change needs
// Workspace.ManageFields, carries If-Match with the resource's version (ETag) and is audited. Field types and storage
// use the names of GET …/fields.

/// <summary>
/// <c>GET/PUT …/fields/{fieldId}</c>: one field with everything the admin editor needs. Choice changes return the field
/// too, because they move its version on.
/// </summary>
/// <param name="FieldId">Stable id; system fields are 1–999, custom fields 1000+.</param>
/// <param name="DisplayName">Current display name.</param>
/// <param name="QueryName">Name used as <c>field:</c> in the query language (ADR-008 R11).</param>
/// <param name="Description">Optional admin note.</param>
/// <param name="Type">One of the nine field types.</param>
/// <param name="Storage">Structural column, imported metadata or coding (fixed after creation).</param>
/// <param name="MultiValue">Several values per document (Text, Keyword; Multiple Choice always).</param>
/// <param name="IsSystem">Seeded system field: renameable and hideable, never retyped or retired.</param>
/// <param name="IsHidden">Hidden from default views and pickers.</param>
/// <param name="SecurityClass">Set for security-affecting fields (Q-11); fixed after creation.</param>
/// <param name="DatePrecision">Date fields only.</param>
/// <param name="DecimalPrecision">Decimal fields only: total digits.</param>
/// <param name="DecimalScale">Decimal fields only: digits after the point.</param>
/// <param name="TextAnalysis">Long Text fields only: prose (words) or identifier (codes, ids).</param>
/// <param name="IsSearchable">Projected for search; fixed after creation.</param>
/// <param name="Capabilities">What search can do with the field.</param>
/// <param name="ReducedCapabilities">The field lives in the overflow container (ADR-007 R6): no sorting, ranges or facets.</param>
/// <param name="Limitations">Plain-language mapping limitations, e.g. "This field will not be sortable." (ADR-007 R8).</param>
/// <param name="HasValues">Some document holds a value, so the type is locked (ADR-003 R7). Only on single reads.</param>
/// <param name="Choices">Choice fields only: the choices in admin order with their use.</param>
/// <param name="Version">The ETag value; send it as <c>If-Match</c>.</param>
public sealed record FieldDefinitionResource(
    int FieldId,
    string DisplayName,
    string QueryName,
    string? Description,
    FieldResourceType Type,
    FieldResourceStorage Storage,
    bool MultiValue,
    bool IsSystem,
    bool IsHidden,
    FieldSecurityClass? SecurityClass,
    FieldResourceDatePrecision? DatePrecision,
    int? DecimalPrecision,
    int? DecimalScale,
    FieldTextAnalysis? TextAnalysis,
    bool IsSearchable,
    FieldCapabilitiesResource Capabilities,
    bool ReducedCapabilities,
    IReadOnlyList<string> Limitations,
    bool? HasValues,
    IReadOnlyList<FieldDefinitionChoiceResource>? Choices,
    long Version);

/// <param name="ChoiceId">Stable id; values store it.</param>
/// <param name="Name">Current name.</param>
/// <param name="IsActive">Inactive choices keep their values (still shown and searchable) but cannot be newly assigned.</param>
/// <param name="InUse">The choice has been assigned at least once: it can be deactivated but never deleted (ADR-003 R8).</param>
/// <param name="SystemKey">Built-in choice (E13-T01): it can be renamed and reordered, never deactivated or deleted. Null otherwise.</param>
public sealed record FieldDefinitionChoiceResource(int ChoiceId, string Name, bool IsActive, bool InUse, string? SystemKey = null);

/// <summary>Q-11 restricted classes of security-affecting fields.</summary>
public enum FieldSecurityClass
{
    PrivilegeStatus,
    ConfidentialityDesignation,
    EthicalWall,
}

/// <summary>Search analysis of a Long Text field (ADR-007 R5).</summary>
public enum FieldTextAnalysis
{
    Prose,
    Identifier,
}

/// <summary><c>POST …/fields</c>: a new custom field. Type-dependent members are ignored for other types.</summary>
/// <param name="DisplayName">1–200 characters, unique in the workspace (ignoring case).</param>
/// <param name="Type">The field type.</param>
/// <param name="Storage"><c>coding</c> (set by reviewers) or <c>metadata</c> (filled by imports).</param>
/// <param name="MultiValue">Text and Keyword only.</param>
/// <param name="DatePrecision">Date fields: <c>date</c> or <c>dateTime</c> (default).</param>
/// <param name="DecimalPrecision">Decimal fields: 1–18 (default 18).</param>
/// <param name="DecimalScale">Decimal fields: 0–6, at most the precision (default 2).</param>
/// <param name="TextAnalysis">Long Text fields: prose (default) or identifier.</param>
/// <param name="SecurityClass">Makes the field security-affecting (Q-11); never offered "apply to family by default".</param>
/// <param name="IsSearchable">Projected for search (default true).</param>
/// <param name="Description">Optional admin note, up to 2,000 characters.</param>
public sealed record CreateFieldRequest(
    string DisplayName,
    FieldResourceType Type,
    FieldResourceStorage Storage,
    bool MultiValue = false,
    FieldResourceDatePrecision? DatePrecision = null,
    int? DecimalPrecision = null,
    int? DecimalScale = null,
    FieldTextAnalysis? TextAnalysis = null,
    FieldSecurityClass? SecurityClass = null,
    bool IsSearchable = true,
    string? Description = null);

/// <summary>
/// <c>PUT …/fields/{fieldId}</c> (If-Match): the editable attributes. A type change (type, multi-value, date precision,
/// decimal precision or scale) is refused with 409 <c>field-has-values</c> once any document holds a value, and always
/// for system fields.
/// </summary>
public sealed record UpdateFieldRequest(
    string DisplayName,
    string? Description,
    bool IsHidden,
    FieldResourceType Type,
    bool MultiValue = false,
    FieldResourceDatePrecision? DatePrecision = null,
    int? DecimalPrecision = null,
    int? DecimalScale = null);

/// <summary><c>POST …/fields/{fieldId}/choices</c> (If-Match of the field): a new choice at the end of the list.</summary>
public sealed record CreateChoiceRequest(string Name);

/// <summary><c>PUT …/fields/{fieldId}/choices/{choiceId}</c> (If-Match of the field): rename and/or (de)activate.</summary>
public sealed record UpdateChoiceRequest(string? Name, bool? IsActive);

/// <summary><c>PUT …/fields/{fieldId}/choice-order</c> (If-Match of the field): every choice id once, in the new order.</summary>
public sealed record ChoiceOrderRequest(IReadOnlyList<int> ChoiceIds);

/// <summary>
/// <c>GET …/field-capacity</c>: what a new field of each kind gets from the search mapping (ADR-007 R3/R8), so the
/// editor can say before saving whether a field will be sortable. Field ids, names and slots are never needed.
/// </summary>
public sealed record FieldCapacityResource(IReadOnlyList<FieldCapacityEntry> Items);

/// <param name="Storage">Coding or metadata.</param>
/// <param name="Type">Field type.</param>
/// <param name="TextAnalysis">Long Text only: the analysis the entry is about.</param>
/// <param name="Budget">Typed search slots of this kind in the workspace.</param>
/// <param name="Available">Slots still free; at 0 a new field goes to the overflow container.</param>
/// <param name="Capabilities">What a new searchable field of this kind gets now (a slot if one is free, else overflow).</param>
/// <param name="Limitations">The same in words: "This field will not be sortable." etc.</param>
public sealed record FieldCapacityEntry(
    FieldResourceStorage Storage,
    FieldResourceType Type,
    FieldTextAnalysis? TextAnalysis,
    int Budget,
    int Available,
    FieldCapabilitiesResource Capabilities,
    IReadOnlyList<string> Limitations);

/// <summary>
/// <c>GET/PUT …/coding-layouts/{layoutId}</c>: a layout as the admin editor sees it, with every field (no per-user
/// filtering), its role assignment and version.
/// </summary>
/// <param name="LayoutId">Stable id.</param>
/// <param name="Name">Display name, unique in the workspace.</param>
/// <param name="IsDefault">The workspace's default layout: everyone may use it; it cannot be deleted.</param>
/// <param name="Roles">Workspace role keys (e.g. <c>Reviewer</c>) that may use the layout besides the default.</param>
/// <param name="Sections">Sections in display order.</param>
/// <param name="Version">The ETag value; send it as <c>If-Match</c>.</param>
public sealed record CodingLayoutDefinitionResource(
    Guid LayoutId,
    string Name,
    bool IsDefault,
    IReadOnlyList<string> Roles,
    IReadOnlyList<CodingLayoutSectionResource> Sections,
    long Version);

/// <summary>
/// <c>POST …/coding-layouts</c> and <c>PUT …/coding-layouts/{layoutId}</c> (If-Match): the whole layout. Section ids may
/// be omitted for new sections. A field appears once; a condition depends on a choice or Yes/No field of the same
/// layout; only editable coding fields can be required or applied to the family by default (Q-48).
/// </summary>
public sealed record CodingLayoutRequest(
    string Name,
    bool IsDefault,
    IReadOnlyList<string>? Roles,
    IReadOnlyList<CodingLayoutSectionRequest> Sections);

public sealed record CodingLayoutSectionRequest(Guid? SectionId, string Title, IReadOnlyList<CodingLayoutFieldResource> Fields);
