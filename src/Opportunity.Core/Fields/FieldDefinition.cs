using System.Globalization;

namespace Opportunity.Core.Fields;

/// <summary>
/// One field of a workspace's catalogue (ADR-003 §1). Ids 1–999 are system fields, custom ids start at 1000 and are
/// never reused (R3, R4). Display name, description and aliases live only here, so a rename never touches documents.
/// </summary>
public sealed class FieldDefinition
{
    public Guid WorkspaceId { get; set; }

    public int FieldId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public FieldType Type { get; set; }

    public FieldStorage Storage { get; set; }

    public bool IsSystem { get; set; }

    /// <summary>Text and Keyword only; MultiChoice is inherently multi-valued (ADR-003 §3).</summary>
    public bool IsMultiValue { get; set; }

    /// <summary>Required for Date fields, null otherwise.</summary>
    public DatePrecision? DatePrecision { get; set; }

    /// <summary>Decimal fields: total significant digits, 1–18.</summary>
    public short? DecimalPrecision { get; set; }

    /// <summary>Decimal fields: digits after the point, 0–6 and ≤ precision.</summary>
    public short? DecimalScale { get; set; }

    /// <summary>Text fields only.</summary>
    public TextAnalysis? TextAnalysis { get; set; }

    /// <summary>Q-11: values decide who may see a document (privilege status, confidentiality, ethical walls).</summary>
    public bool IsSecurityAffecting { get; set; }

    /// <summary>Set exactly when <see cref="IsSecurityAffecting"/>.</summary>
    public SecurityClass? SecurityClass { get; set; }

    public bool IsSearchable { get; set; }

    /// <summary>Typed search slot (ADR-007 R3/R4), e.g. <c>kw.s001</c>, or <c>overflow</c>; null when not searchable.</summary>
    public string? SearchSlot { get; set; }

    public FieldCapabilities Capabilities { get; set; }

    /// <summary>Structural column name for <see cref="FieldStorage.Column"/> system fields.</summary>
    public string? ColumnName { get; set; }

    public bool IsHidden { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Incremented by every change to the field or its choices (the admin API's ETag).</summary>
    public long Version { get; set; } = 1;

    /// <summary>JSONB key (ADR-003 R5).</summary>
    public string Key => FieldKey.For(FieldId);

    public bool IsChoice => Type is FieldType.SingleChoice or FieldType.MultiChoice;
}

/// <summary>A choice of a SingleChoice/MultiChoice field. Values store <see cref="ChoiceId"/>; the name lives only here (R8).</summary>
public sealed class Choice
{
    public Guid WorkspaceId { get; set; }

    public int FieldId { get; set; }

    public int ChoiceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>Inactive choices keep their existing values but cannot be newly assigned.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>First time a value used this choice; from then on it can only be deactivated, never deleted.</summary>
    public DateTimeOffset? FirstUsedAt { get; set; }

    /// <summary>
    /// Built-in choice whose meaning the application relies on (e.g. <c>privilege-status.withhold</c>, V0047): renameable
    /// and reorderable, never deleted or deactivated. Null for every other choice.
    /// </summary>
    public string? SystemKey { get; set; }
}

/// <summary>JSONB key format of ADR-003 R5: <c>"f" + FieldId</c>.</summary>
public static class FieldKey
{
    public static string For(int fieldId) => "f" + fieldId.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string key, out int fieldId)
    {
        ArgumentNullException.ThrowIfNull(key);
        fieldId = 0;
        return key.Length > 1 && key[0] == 'f' && key[1] != '0' && key.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0
            && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out fieldId);
    }
}

/// <summary>A validation failure attributed to one field (API problem details list these per field).</summary>
/// <param name="Field">Field key (<c>f1017</c>), property name or path the error is about.</param>
/// <param name="Code">Stable machine-readable code.</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record FieldError(string Field, string Code, string Message);
