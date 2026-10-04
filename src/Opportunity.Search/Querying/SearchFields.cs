using Opportunity.Core.Fields;
using Opportunity.Search.Projection;

namespace Opportunity.Search.Querying;

/// <summary>How the planner compares values of a field (ADR-008 §2 "per type").</summary>
internal enum SearchValueKind
{
    /// <summary>Analyzed text (<c>text</c>, <c>fileName</c>, Text slots): match, phrase, token wildcards, proximity.</summary>
    FullText,

    /// <summary>Normalized keyword: exact value, wildcards, natural-sort ranges where rangeable.</summary>
    Keyword,
    Integer,
    Decimal,
    Date,
    Boolean,

    /// <summary>Choice ids as keywords; the query names choices (case-insensitive) and the planner maps them to ids.</summary>
    Choice,

    /// <summary>User ids as keywords.</summary>
    User,

    /// <summary>A key of an overflow <c>flat_object</c> (ADR-007 R6): every value is a string.</summary>
    Overflow,
}

/// <summary>One projection path a query field searches.</summary>
/// <param name="Name">Query name, for messages.</param>
/// <param name="Path">Projection path (never one of <see cref="ProjectionFields.NotAddressable"/>).</param>
/// <param name="Definition">The catalogue field; null for the relationship fields and <c>text</c>, which have none.</param>
/// <param name="WholeValuePath">Untokenized companion used by leading/infix wildcards (<c>fileName.wc</c>).</param>
/// <param name="SortKeyPath">Natural-sort key that keyword ranges compare (ADR-008 R9).</param>
/// <param name="CalendarDate">Date precision: values are calendar dates compared without a zone (R8).</param>
internal sealed record SearchTarget(
    string Name,
    string Path,
    SearchValueKind Kind,
    FieldCapabilities Capabilities,
    FieldDefinition? Definition = null,
    string? WholeValuePath = null,
    string? SortKeyPath = null,
    bool CalendarDate = false)
{
    public bool Has(FieldCapabilities capability) => (Capabilities & capability) == capability;

    public bool IsCoding => Definition?.Storage == FieldStorage.Coding;
}

internal enum FieldResolution
{
    Resolved,
    Unknown,

    /// <summary>The field exists but has no search slot (not searchable, or a coding field the index generation lacks).</summary>
    NotSearchable,
}

/// <summary>
/// Query names → projection paths of one workspace (ADR-008 R10–R11, ADR-007 §3, R8). Names follow
/// <see cref="FieldQueryNames"/> and compare case-insensitively; relationship fields addressed directly and <c>text</c>
/// are fixed; injected-only fields never resolve. Capabilities come from the catalogue (ADR-003 R16), never from the
/// type alone.
/// </summary>
internal sealed class SearchFieldResolver
{
    public const string TextName = "text";

    private const FieldCapabilities KeywordCapabilities = FieldCapabilities.Sortable | FieldCapabilities.Filterable
        | FieldCapabilities.Aggregatable | FieldCapabilities.Exists;

    private const FieldCapabilities NumberCapabilities = KeywordCapabilities | FieldCapabilities.Rangeable;

    private const FieldCapabilities FlagCapabilities = KeywordCapabilities;

    /// <summary>The default field (R12) and the relationship fields with fixed names (ADR-007 §3).</summary>
    private static readonly IReadOnlyDictionary<string, SearchTarget> Fixed = new Dictionary<string, SearchTarget>(StringComparer.OrdinalIgnoreCase)
    {
        [TextName] = new(TextName, ProjectionFields.Text, SearchValueKind.FullText,
            FieldCapabilities.FullText | FieldCapabilities.Wildcard | FieldCapabilities.Highlightable | FieldCapabilities.Exists),
        ["familyid"] = new("familyid", ProjectionFields.FamilyId, SearchValueKind.Keyword, FieldCapabilities.Filterable | FieldCapabilities.Exists),
        ["familysequence"] = new("familysequence", ProjectionFields.FamilySequence, SearchValueKind.Integer, NumberCapabilities),
        ["familystatus"] = new("familystatus", "familyStatus", SearchValueKind.Keyword, KeywordCapabilities),
        ["duplicategroup"] = new("duplicategroup", "duplicateGroupId", SearchValueKind.Keyword, FieldCapabilities.Filterable | FieldCapabilities.Exists),
        ["duplicateprimary"] = new("duplicateprimary", "isDuplicatePrimary", SearchValueKind.Boolean, FlagCapabilities),
        ["threadid"] = new("threadid", "emailThreadId", SearchValueKind.Keyword, FieldCapabilities.Filterable | FieldCapabilities.Exists),
    };

    private readonly Dictionary<string, FieldDefinition> _byName;
    private readonly FieldCatalog _catalog;
    private readonly int _generation;
    private readonly SearchFieldOptions? _options;

    private SearchFieldResolver(FieldCatalog catalog, int generation, SearchFieldOptions? options)
    {
        _catalog = catalog;
        _generation = generation;
        _options = options;
        var live = catalog.Fields.Where(f => !f.IsDeleted).ToList();
        var names = FieldQueryNames.Assign(live);
        _byName = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in live)
        {
            _byName[names[field.FieldId]] = field;
        }
    }

    public static SearchTarget Text => Fixed[TextName];

    /// <summary>
    /// A resolver over <paramref name="catalog"/>. Structural system fields missing from it (a workspace whose catalogue
    /// was never seeded) are added from <see cref="SystemFields"/>: their names and slots are fixed, so the fallback
    /// cannot shadow a custom field.
    /// </summary>
    public static SearchFieldResolver Create(FieldCatalog catalog, int generation, SearchFieldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var present = catalog.Fields.Select(f => f.FieldId).ToHashSet();
        var missing = SystemFields.Create(Guid.Empty).Where(f => f.Storage == FieldStorage.Column && !present.Contains(f.FieldId)).ToList();
        if (missing.Count > 0)
        {
            catalog = new FieldCatalog(
                catalog.Fields.Concat(missing),
                catalog.Fields.SelectMany(f => catalog.ChoicesOf(f.FieldId)));
        }

        return new SearchFieldResolver(catalog, generation, options);
    }

    public FieldCatalog Catalog => _catalog;

    /// <summary>Resolves <paramref name="name"/> to the paths it searches (more than one with custodian expansion).</summary>
    public FieldResolution Resolve(string name, out IReadOnlyList<SearchTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(name);
        targets = [];
        if (ProjectionFields.NotAddressable.Contains(name))
        {
            return FieldResolution.Unknown;
        }

        if (Fixed.TryGetValue(name, out var fixedTarget))
        {
            targets = [fixedTarget];
            return FieldResolution.Resolved;
        }

        if (!_byName.TryGetValue(name, out var field))
        {
            return FieldResolution.Unknown;
        }

        var resolved = new List<SearchTarget>();
        foreach (var definition in SearchFieldExpansion.Expand(_catalog, field, _options))
        {
            if (Target(definition, definition.FieldId == field.FieldId ? name.ToLowerInvariant() : definition.Name) is { } target)
            {
                resolved.Add(target);
            }
            else if (definition.FieldId == field.FieldId)
            {
                return FieldResolution.NotSearchable;
            }
        }

        targets = resolved;
        return FieldResolution.Resolved;
    }

    /// <summary>Up to three known names closest to an unknown one (ADR-008 R11 "with suggestions").</summary>
    public IReadOnlyList<string> Suggest(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var wanted = name.ToLowerInvariant();
        return [.. _byName.Keys.Concat(Fixed.Keys)
            .Select(candidate => (Name: candidate, Distance: Distance(wanted, candidate)))
            .Where(c => c.Distance <= Math.Max(2, wanted.Length / 3) || c.Name.StartsWith(wanted, StringComparison.Ordinal))
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(3)
            .Select(c => c.Name)];
    }

    private SearchTarget? Target(FieldDefinition field, string name)
    {
        if (ProjectionFieldPaths.For(field) is not { } path)
        {
            return null;
        }

        var capabilities = field.Capabilities;
        if (field.Storage == FieldStorage.Column)
        {
            return field.Type switch
            {
                FieldType.Text => new SearchTarget(name, path, SearchValueKind.FullText, capabilities | FieldCapabilities.FullText, field,
                    WholeValuePath: path == ProjectionFields.FileName ? ProjectionFields.FileName + ".wc" : null),
                FieldType.Keyword => new SearchTarget(name, path, SearchValueKind.Keyword, capabilities, field,
                    SortKeyPath: ProjectionFieldPaths.SortKeyFields.GetValueOrDefault(path)),
                FieldType.Integer => new SearchTarget(name, path, SearchValueKind.Integer, capabilities, field),
                FieldType.Decimal => new SearchTarget(name, path, SearchValueKind.Decimal, capabilities, field),
                FieldType.Date => new SearchTarget(name, path, SearchValueKind.Date, capabilities, field,
                    CalendarDate: field.DatePrecision == DatePrecision.Date),
                FieldType.Boolean => new SearchTarget(name, path, SearchValueKind.Boolean, capabilities, field),
                _ => null,
            };
        }

        // Generation 1 has no coding slots: coding fields search only after a reindex (E07-T11).
        if (field.Storage == FieldStorage.Coding && _generation < 2)
        {
            return null;
        }

        if (field.SearchSlot == FieldRules.OverflowSlot)
        {
            return new SearchTarget(name, path, SearchValueKind.Overflow, capabilities, field);
        }

        var kind = field.SearchSlot![..field.SearchSlot!.IndexOf('.', StringComparison.Ordinal)];
        return kind switch
        {
            "txt" or "idt" => new SearchTarget(name, path, SearchValueKind.FullText, capabilities, field),
            "kw" => new SearchTarget(name, path, SearchValueKind.Keyword, capabilities, field),
            "int" => new SearchTarget(name, path, SearchValueKind.Integer, capabilities, field),
            "dec" => new SearchTarget(name, path, SearchValueKind.Decimal, capabilities, field),
            "dt" => new SearchTarget(name, path, SearchValueKind.Date, capabilities, field, CalendarDate: field.DatePrecision == DatePrecision.Date),
            "bool" => new SearchTarget(name, path, SearchValueKind.Boolean, capabilities, field),
            "ch" => new SearchTarget(name, path, SearchValueKind.Choice, capabilities, field),
            "usr" => new SearchTarget(name, path, SearchValueKind.User, capabilities, field),
            _ => null,
        };
    }

    /// <summary>Optimal string alignment distance (Levenshtein with adjacent transpositions).</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }
}
