using Opportunity.Core.Fields;
using Opportunity.Search.Querying;

namespace Opportunity.UnitTests.Search;

/// <summary>
/// The field fixture of the planner's golden and unit tests: the system fields plus one custom field per slot kind,
/// coding fields, an overflow field and a field that is not searchable. Analysis is a deterministic stand-in for the
/// projection analyzers (runs of letters and digits, lower-cased); the integration tests use OpenSearch's own.
/// </summary>
internal static class PlannerFixture
{
    public static readonly Guid Workspace = Guid.Parse("0199a8a0-1111-7000-8000-000000000001");

    public const int Custodian = 1000;
    public const int Responsiveness = 1001;
    public const int Hot = 1002;
    public const int ReviewDate = 1003;
    public const int Amount = 1004;
    public const int Notes = 1005;
    public const int Reviewer = 1006;
    public const int OverflowTag = 1007;
    public const int Unsearchable = 1008;
    public const int Issues = 1009;
    public const int Privilege = 1010;
    public const int Subject = 1011;
    public const int Sent = 1012;

    public static FieldCatalog Catalog { get; } = new(
        [
            .. SystemFields.Create(Workspace),
            Field(Custodian, "Custodian", FieldType.Keyword, FieldStorage.Metadata, "kw.s001"),
            Field(Responsiveness, "Responsiveness", FieldType.SingleChoice, FieldStorage.Coding, "ch.s001"),
            Field(Hot, "Hot", FieldType.Boolean, FieldStorage.Coding, "bool.s001"),
            Field(ReviewDate, "Review Date", FieldType.Date, FieldStorage.Metadata, "dt.s001", f => f.DatePrecision = DatePrecision.Date),
            Field(Amount, "Amount", FieldType.Decimal, FieldStorage.Metadata, "dec.s001", f => (f.DecimalPrecision, f.DecimalScale) = (12, 2)),
            Field(Notes, "Notes", FieldType.Text, FieldStorage.Metadata, "txt.s001", f => f.TextAnalysis = TextAnalysis.Prose),
            Field(Reviewer, "Reviewer", FieldType.User, FieldStorage.Coding, "usr.s001"),
            Field(OverflowTag, "Overflow Tag", FieldType.Keyword, FieldStorage.Metadata, FieldRules.OverflowSlot),
            Field(Unsearchable, "Unsearchable", FieldType.Keyword, FieldStorage.Metadata, null),
            Field(Issues, "Issues", FieldType.MultiChoice, FieldStorage.Coding, "ch.s002", f => f.IsMultiValue = true),
            Field(Privilege, "Privilege", FieldType.SingleChoice, FieldStorage.Coding, "ch.s003"),
            Field(Subject, "Subject", FieldType.Text, FieldStorage.Metadata, "txt.s002", f => f.TextAnalysis = TextAnalysis.Prose),
            Field(Sent, "Sent", FieldType.Date, FieldStorage.Metadata, "dt.s002", f => f.DatePrecision = DatePrecision.DateTime),
        ],
        [
            Choice(Responsiveness, 1, "Responsive"),
            Choice(Responsiveness, 2, "Not Responsive"),
            Choice(Issues, 1, "Fraud"),
            Choice(Issues, 2, "Pricing"),
            Choice(Issues, 3, "fraud", sortOrder: 3),
            Choice(Privilege, 1, "Privileged"),
            Choice(Privilege, 2, "Not Privileged"),
        ]);

    public static SearchQueryPlanner Planner(FieldCatalog? catalog = null) =>
        new(new FixedCatalog(catalog ?? Catalog), new FakeAnalyzer());

    private static FieldDefinition Field(
        int id, string name, FieldType type, FieldStorage storage, string? slot, Action<FieldDefinition>? configure = null)
    {
        var field = new FieldDefinition
        {
            WorkspaceId = Workspace,
            FieldId = id,
            Name = name,
            Type = type,
            Storage = storage,
            IsSearchable = slot is not null,
            SearchSlot = slot,
            Capabilities = FieldRules.CapabilitiesForSlot(slot),
        };
        configure?.Invoke(field);
        return field;
    }

    private static Choice Choice(int fieldId, int choiceId, string name, int? sortOrder = null) => new()
    {
        WorkspaceId = Workspace,
        FieldId = fieldId,
        ChoiceId = choiceId,
        Name = name,
        SortOrder = sortOrder ?? choiceId,
    };

    private sealed class FixedCatalog(FieldCatalog catalog) : ISearchFieldCatalogSource
    {
        public ValueTask<FieldCatalog> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(catalog);
    }

    internal sealed class FakeAnalyzer : ISearchTextAnalyzer
    {
        public ValueTask<IReadOnlyList<IReadOnlyList<string>>> AnalyzeAsync(
            int generation, string path, AnalysisMode mode, IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<IReadOnlyList<string>>>(
                [.. texts.Select(t => mode == AnalysisMode.Normalize ? (IReadOnlyList<string>)[t.ToLowerInvariant()] : Tokens(t))]);

        private static List<string> Tokens(string text)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(char.ToLowerInvariant(c));
                }
                else if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            return tokens;
        }
    }
}
