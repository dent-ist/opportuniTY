using Opportunity.Application.Audit;
using Opportunity.Core.Fields;

namespace Opportunity.Application.Fields;

/// <summary>
/// Field definitions, choices and coding layouts of a workspace (ADR-003, E04-T03). Every change is a catalogue row
/// change only: no call here rewrites document rows. Changes will be audited once the audit port exists (E14-T01).
/// </summary>
public interface IFieldCatalogRepository
{
    /// <summary>
    /// Seeds a new workspace: the field-id counter, the system fields (ids 1–999) and an empty default layout.
    /// Idempotent.
    /// </summary>
    Task InitializeWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Fields (deleted ones excluded unless asked) and their choices in one consistent read.</summary>
    Task<FieldCatalog> GetCatalogAsync(Guid workspaceId, bool includeDeleted = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a custom field: allocates the next field id (never reused), a search slot when searchable (ADR-007 R4)
    /// and derives its capabilities. Name, type attributes and the workspace field limit are validated.
    /// </summary>
    Task<CatalogResult<FieldDefinition>> CreateFieldAsync(NewField field, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames, hides or retypes a field. Retyping (type, multi-value, precision, scale, storage) is rejected for system
    /// fields and for any field that holds values (ADR-003 R7).
    /// </summary>
    Task<CatalogResult<FieldDefinition>> UpdateFieldAsync(FieldChange change, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a custom field (ADR-003 R6); its values become invisible and are purged by a job.</summary>
    Task<CatalogResult<FieldDefinition>> DeleteFieldAsync(Guid workspaceId, int fieldId, CancellationToken cancellationToken = default);

    Task<CatalogResult<Choice>> AddChoiceAsync(Guid workspaceId, int fieldId, string name, CancellationToken cancellationToken = default);

    Task<CatalogResult<Choice>> RenameChoiceAsync(Guid workspaceId, int fieldId, int choiceId, string name, CancellationToken cancellationToken = default);

    /// <summary>Sets the display order; <paramref name="orderedChoiceIds"/> must list every choice of the field once.</summary>
    Task<CatalogResult<IReadOnlyList<Choice>>> ReorderChoicesAsync(
        Guid workspaceId, int fieldId, IReadOnlyList<int> orderedChoiceIds, CancellationToken cancellationToken = default);

    Task<CatalogResult<Choice>> SetChoiceActiveAsync(
        Guid workspaceId, int fieldId, int choiceId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Deletes a choice that was never used; a used choice can only be deactivated (ADR-003 R8).</summary>
    Task<CatalogResult<Choice>> DeleteChoiceAsync(Guid workspaceId, int fieldId, int choiceId, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a layout after <see cref="CodingLayoutValidator.ValidateStructure"/> passes.</summary>
    Task<CatalogResult<CodingLayout>> SaveLayoutAsync(CodingLayout layout, CancellationToken cancellationToken = default);

    Task<CodingLayout?> GetLayoutAsync(Guid workspaceId, Guid layoutId, CancellationToken cancellationToken = default);

    /// <summary>All layouts, or those assigned to <paramref name="role"/>; the default layout is always included.</summary>
    Task<IReadOnlyList<CodingLayout>> GetLayoutsAsync(Guid workspaceId, string? role = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a non-default layout.</summary>
    Task<CatalogResult<CodingLayout>> DeleteLayoutAsync(Guid workspaceId, Guid layoutId, CancellationToken cancellationToken = default);

    /// <summary>Whether any document holds a value for the field (a scan; admin screens only, ADR-003 R12).</summary>
    Task<bool> FieldHasValuesAsync(Guid workspaceId, int fieldId, CancellationToken cancellationToken = default);

    Task<CatalogResult<FieldDefinition>> CreateFieldAsync(NewField field, CatalogWrite write, CancellationToken cancellationToken = default);

    Task<CatalogResult<FieldDefinition>> UpdateFieldAsync(FieldChange change, CatalogWrite write, CancellationToken cancellationToken = default);

    Task<CatalogResult<FieldDefinition>> DeleteFieldAsync(Guid workspaceId, int fieldId, CatalogWrite write, CancellationToken cancellationToken = default);

    /// <summary>Adds a choice; the field's version is the one checked and incremented.</summary>
    Task<CatalogResult<Choice>> AddChoiceAsync(
        Guid workspaceId, int fieldId, string name, CatalogWrite write, CancellationToken cancellationToken = default);

    /// <summary>Renames and/or (de)activates a choice; null members are left unchanged.</summary>
    Task<CatalogResult<Choice>> UpdateChoiceAsync(
        Guid workspaceId, int fieldId, int choiceId, string? name, bool? isActive, CatalogWrite write, CancellationToken cancellationToken = default);

    Task<CatalogResult<IReadOnlyList<Choice>>> ReorderChoicesAsync(
        Guid workspaceId, int fieldId, IReadOnlyList<int> orderedChoiceIds, CatalogWrite write, CancellationToken cancellationToken = default);

    Task<CatalogResult<Choice>> DeleteChoiceAsync(
        Guid workspaceId, int fieldId, int choiceId, CatalogWrite write, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates (empty <see cref="CodingLayout.LayoutId"/>) or replaces a layout. With an expected version the layout must
    /// exist (a replace never creates).
    /// </summary>
    Task<CatalogResult<CodingLayout>> SaveLayoutAsync(CodingLayout layout, CatalogWrite write, CancellationToken cancellationToken = default);

    Task<CatalogResult<CodingLayout>> DeleteLayoutAsync(Guid workspaceId, Guid layoutId, CatalogWrite write, CancellationToken cancellationToken = default);
}

/// <summary>
/// An administrative catalogue change: the version the caller read (<c>If-Match</c>; null skips the check) and the
/// audit event to write in the same transaction. The store sets the event's resource id and adds the new version.
/// </summary>
public sealed record CatalogWrite(long? ExpectedVersion, AuditEvent? Audit)
{
    public static CatalogWrite None { get; } = new(null, null);
}

/// <summary>Input for a new custom field.</summary>
public sealed record NewField(
    Guid WorkspaceId,
    string Name,
    FieldType Type,
    FieldStorage Storage,
    bool IsMultiValue = false,
    DatePrecision? DatePrecision = null,
    short? DecimalPrecision = null,
    short? DecimalScale = null,
    TextAnalysis? TextAnalysis = null,
    SecurityClass? SecurityClass = null,
    bool IsSearchable = true,
    string? Description = null);

/// <summary>A change to an existing field; null members are left unchanged.</summary>
public sealed record FieldChange(Guid WorkspaceId, int FieldId)
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public bool? IsHidden { get; init; }

    public FieldType? Type { get; init; }

    public bool? IsMultiValue { get; init; }

    public DatePrecision? DatePrecision { get; init; }

    public short? DecimalPrecision { get; init; }

    public short? DecimalScale { get; init; }

    public bool ChangesType => Type.HasValue || IsMultiValue.HasValue || DatePrecision.HasValue
        || DecimalPrecision.HasValue || DecimalScale.HasValue;
}

public enum CatalogOutcome
{
    Ok,
    NotFound,

    /// <summary>Field-level validation failed; see <see cref="CatalogResult{T}.Errors"/>.</summary>
    Invalid,

    /// <summary>The change conflicts with existing data (type change with values, delete of a used choice, …).</summary>
    Conflict,

    /// <summary>The expected version is stale (412).</summary>
    VersionConflict,
}

public sealed record CatalogResult<T>(CatalogOutcome Outcome, T? Value, IReadOnlyList<FieldError> Errors)
{
    public bool Succeeded => Outcome == CatalogOutcome.Ok;
}

public static class CatalogResult
{
    public static CatalogResult<T> Ok<T>(T value) => new(CatalogOutcome.Ok, value, []);

    public static CatalogResult<T> NotFound<T>() => new(CatalogOutcome.NotFound, default, []);

    public static CatalogResult<T> Invalid<T>(IReadOnlyList<FieldError> errors) => new(CatalogOutcome.Invalid, default, errors);

    public static CatalogResult<T> Conflict<T>(FieldError error) => new(CatalogOutcome.Conflict, default, [error]);

    public static CatalogResult<T> VersionConflict<T>() => new(CatalogOutcome.VersionConflict, default, []);
}
