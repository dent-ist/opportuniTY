using System.Globalization;

namespace Opportunity.Core.Fields;

/// <summary>Installation limits of ADR-003 R11 and the search slot budgets of ADR-007 R3 (initial values).</summary>
public static class FieldLimits
{
    public const int FirstCustomFieldId = 1000;
    public const int MaxCustomFieldsPerWorkspace = 1000;
    public const int MaxNameLength = 200;
    public const int MaxChoiceNameLength = 200;
    public const int MaxChoicesPerField = 10_000;
    public const int MaxTextLength = 100_000;
    public const int MaxKeywordLength = 8_191;
    public const int MaxDecimalPrecision = 18;
    public const int MaxDecimalScale = 6;

    /// <summary>JSON numbers are exact in double only up to ±(2^53−1) (ADR-003 §3).</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;
}

/// <summary>Definition invariants, search slot kinds and capability derivation (ADR-003, ADR-007).</summary>
public static class FieldRules
{
    public const string OverflowSlot = "overflow";

    /// <summary>Metadata slot budget per kind (ADR-007 R3). Coding fields use the same kinds in their own namespace.</summary>
    public static readonly IReadOnlyDictionary<string, int> SlotBudgets = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["txt"] = 100,
        ["idt"] = 50,
        ["kw"] = 300,
        ["int"] = 100,
        ["dec"] = 50,
        ["dt"] = 100,
        ["bool"] = 100,
        ["ch"] = 200,
        ["usr"] = 50,
    };

    /// <summary>
    /// Coding slot budget per kind: the interim Candidate A layout <c>coding.&lt;kind&gt;.s&lt;NNN&gt;</c> of projection
    /// generation 2 (ADR-007 §3, ADR-004b). Smaller than the metadata budgets so the mapping stays well inside the
    /// 2,000-field limit; a workspace beyond them projects further coding fields into the coding overflow.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> CodingSlotBudgets = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["txt"] = 20,
        ["idt"] = 5,
        ["kw"] = 20,
        ["int"] = 10,
        ["dec"] = 10,
        ["dt"] = 20,
        ["bool"] = 50,
        ["ch"] = 100,
        ["usr"] = 10,
    };

    /// <summary>The slot budgets of a storage's namespace: <c>metadata.*</c> or <c>coding.*</c>.</summary>
    public static IReadOnlyDictionary<string, int> SlotBudgetsFor(FieldStorage storage) =>
        storage == FieldStorage.Coding ? CodingSlotBudgets : SlotBudgets;

    public static string SlotKind(FieldType type, TextAnalysis? analysis) => type switch
    {
        FieldType.Text => analysis == TextAnalysis.Identifier ? "idt" : "txt",
        FieldType.Keyword => "kw",
        FieldType.Integer => "int",
        FieldType.Decimal => "dec",
        FieldType.Date => "dt",
        FieldType.Boolean => "bool",
        FieldType.SingleChoice or FieldType.MultiChoice => "ch",
        FieldType.User => "usr",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static string Slot(string kind, int number) => string.Create(CultureInfo.InvariantCulture, $"{kind}.s{number:D3}");

    /// <summary>Capabilities of a slot (ADR-007 R8 table). Not searchable → none.</summary>
    public static FieldCapabilities CapabilitiesForSlot(string? slot)
    {
        if (slot is null)
        {
            return FieldCapabilities.None;
        }

        if (slot == OverflowSlot)
        {
            return FieldCapabilities.Filterable | FieldCapabilities.Wildcard | FieldCapabilities.Exists;
        }

        var kind = slot[..slot.IndexOf('.', StringComparison.Ordinal)];
        const FieldCapabilities Base = FieldCapabilities.Filterable | FieldCapabilities.Exists;
        return kind switch
        {
            "txt" or "idt" => Base | FieldCapabilities.Sortable | FieldCapabilities.FullText | FieldCapabilities.Wildcard
                | FieldCapabilities.Highlightable,
            "kw" => Base | FieldCapabilities.Sortable | FieldCapabilities.Aggregatable | FieldCapabilities.Wildcard,
            "int" or "dec" or "dt" => Base | FieldCapabilities.Sortable | FieldCapabilities.Rangeable | FieldCapabilities.Aggregatable,
            "bool" => Base | FieldCapabilities.Sortable | FieldCapabilities.Aggregatable,
            "ch" or "usr" => Base | FieldCapabilities.Aggregatable,
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown slot kind."),
        };
    }

    /// <summary>
    /// Checks the type-dependent attributes of a definition. Storage/system rules: only system fields use
    /// <see cref="FieldStorage.Column"/>; security-affecting fields are Coding or Metadata fields with a class.
    /// </summary>
    public static IReadOnlyList<FieldError> ValidateDefinition(FieldDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<FieldError>();
        var name = definition.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > FieldLimits.MaxNameLength || name.Any(char.IsControl))
        {
            errors.Add(new("name", "invalid-name", $"Name must be 1–{FieldLimits.MaxNameLength} characters without control characters."));
        }

        if (!Enum.IsDefined(definition.Type))
        {
            errors.Add(new("type", "invalid-type", "Unknown field type."));
            return errors;
        }

        if (!Enum.IsDefined(definition.Storage))
        {
            errors.Add(new("storage", "invalid-storage", "Unknown storage."));
        }
        else if (definition.Storage == FieldStorage.Column && !definition.IsSystem)
        {
            errors.Add(new("storage", "column-storage-reserved", "Only system fields are structural columns (ADR-003 R1)."));
        }

        if (definition.IsMultiValue && definition.Type is not (FieldType.Text or FieldType.Keyword or FieldType.MultiChoice))
        {
            errors.Add(new("isMultiValue", "multi-value-not-supported", "Only Text, Keyword and MultiChoice fields hold several values."));
        }

        if (definition.Type == FieldType.MultiChoice && !definition.IsMultiValue)
        {
            errors.Add(new("isMultiValue", "multi-choice-is-multi-value", "MultiChoice fields are always multi-valued."));
        }

        if ((definition.Type == FieldType.Date) != definition.DatePrecision.HasValue)
        {
            errors.Add(new("datePrecision", "date-precision", "Date fields need a precision (Date or DateTime); other types must not have one."));
        }

        if (definition.Type == FieldType.Decimal)
        {
            if (definition.DecimalPrecision is not { } p || p < 1 || p > FieldLimits.MaxDecimalPrecision
                || definition.DecimalScale is not { } s || s < 0 || s > FieldLimits.MaxDecimalScale || s > p)
            {
                errors.Add(new("decimalPrecision", "decimal-precision",
                    $"Decimal fields need precision 1–{FieldLimits.MaxDecimalPrecision} and scale 0–{FieldLimits.MaxDecimalScale} (scale ≤ precision)."));
            }
        }
        else if (definition.DecimalPrecision.HasValue || definition.DecimalScale.HasValue)
        {
            errors.Add(new("decimalPrecision", "decimal-precision", "Only Decimal fields have precision and scale."));
        }

        if (definition.TextAnalysis.HasValue && definition.Type != FieldType.Text)
        {
            errors.Add(new("textAnalysis", "text-analysis", "Only Text fields have a text analysis."));
        }

        if (definition.IsSecurityAffecting != definition.SecurityClass.HasValue)
        {
            errors.Add(new("securityClass", "security-class", "Security-affecting fields need a security class, and only they have one."));
        }
        else if (definition.IsSecurityAffecting && definition.Storage == FieldStorage.Column)
        {
            errors.Add(new("isSecurityAffecting", "security-affecting-storage", "Security-affecting fields are Coding or Metadata fields."));
        }

        return errors;
    }
}
