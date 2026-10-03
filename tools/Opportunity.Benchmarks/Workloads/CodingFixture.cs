using System.Text.Json.Serialization;

using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.Benchmarks.Workloads;

/// <summary>A coding field the benchmark workspace defines (ADR-003 types); <c>QueryName</c> per ADR-008 R11.</summary>
public sealed record CodingFieldDefinition
{
    public required string QueryName { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>SingleChoice, MultiChoice or WholeNumber.</summary>
    public required string Type { get; init; }

    /// <summary>Choices with their share among coded documents (SingleChoice) or per-choice probability (MultiChoice).</summary>
    public IReadOnlyList<CodingChoice> Choices { get; init; } = [];

    public int? Minimum { get; init; }

    public int? Maximum { get; init; }
}

public sealed record CodingChoice(string Name, double Share);

/// <summary>Coding state of one document under the fixture; null/empty = not coded.</summary>
public sealed record CodingValues(string? Responsiveness, string? Privilege, IReadOnlyList<string> Issues, int ReviewPriority);

/// <summary>
/// The coding state a benchmark workspace starts from. Content+coding-filter queries and grid sort/facet on coding
/// columns (E17-T04, UI finding 3) need coding values with known distributions, so they are a pure function of
/// (fixture seed, document index) — document index = control-number sequence − 1 — and every loader (bulk coding job
/// before the run, fast-path loader E17-T05) must apply exactly <see cref="ValuesFor"/>. The workloads write their
/// own changes to <see cref="BulkTagField"/> (bulk driver) and to the fixture fields (reviewers), so expected hit
/// counts describe the state at the start of a run.
/// </summary>
public sealed record CodingFixture
{
    public const string ResponsivenessField = "responsiveness";
    public const string PrivilegeField = "privilege";
    public const string IssuesField = "issues";
    public const string ReviewPriorityField = "review_priority";

    /// <summary>MultiChoice field the background bulk-coding driver writes; never used in a measured query.</summary>
    public const string BulkTagField = "bench_bulk_tag";

    public int Version { get; init; } = 1;

    public required ulong Seed { get; init; }

    /// <summary>Share of documents with first-pass review coding (responsiveness, privilege, issues).</summary>
    public double CodedShare { get; init; } = 0.6;

    public IReadOnlyList<CodingFieldDefinition> Fields { get; init; } = DefaultFields;

    public static IReadOnlyList<CodingFieldDefinition> DefaultFields { get; } =
    [
        new()
        {
            QueryName = ResponsivenessField,
            DisplayName = "Responsiveness",
            Type = "SingleChoice",
            Choices = [new("Responsive", 0.25), new("Not Responsive", 0.65), new("Needs Further Review", 0.10)],
        },
        new()
        {
            QueryName = PrivilegeField,
            DisplayName = "Privilege",
            Type = "SingleChoice",
            Choices = [new("Privileged", 0.08), new("Not Privileged", 0.92)],
        },
        new()
        {
            QueryName = IssuesField,
            DisplayName = "Issues",
            Type = "MultiChoice",
            Choices = [new("Pricing", 0.15), new("Antitrust", 0.10), new("Employment", 0.08), new("Product Safety", 0.06), new("Export Control", 0.04), new("Board", 0.03)],
        },
        new() { QueryName = ReviewPriorityField, DisplayName = "Review Priority", Type = "WholeNumber", Minimum = 1, Maximum = 5 },
        new()
        {
            QueryName = BulkTagField,
            DisplayName = "Bench Bulk Tag",
            Type = "MultiChoice",
            Choices = [.. Enumerable.Range(0, 8).Select(i => new CodingChoice($"Batch {(char)('A' + i)}", 0))],
        },
    ];

    [JsonIgnore]
    public IReadOnlySet<string> QueryNames => Fields.Select(f => f.QueryName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public CodingFieldDefinition Field(string queryName) =>
        Fields.FirstOrDefault(f => string.Equals(f.QueryName, queryName, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown coding field '{queryName}'.", nameof(queryName));

    public CodingValues ValuesFor(long documentIndex)
    {
        var rng = new Rng(StableHash.Combine(Seed, (ulong)documentIndex));
        bool coded = rng.NextDouble() < CodedShare;
        string? responsiveness = PickSingle(rng, Field(ResponsivenessField));
        string? privilege = PickSingle(rng, Field(PrivilegeField));
        var issues = new List<string>();
        foreach (CodingChoice choice in Field(IssuesField).Choices)
        {
            if (rng.NextDouble() < choice.Share)
            {
                issues.Add(choice.Name);
            }
        }

        CodingFieldDefinition priority = Field(ReviewPriorityField);
        int reviewPriority = rng.NextInt(priority.Minimum!.Value, priority.Maximum!.Value);
        return coded
            ? new CodingValues(responsiveness, privilege, issues, reviewPriority)
            : new CodingValues(null, null, [], reviewPriority);
    }

    private static string? PickSingle(Rng rng, CodingFieldDefinition field)
    {
        double u = rng.NextDouble();
        foreach (CodingChoice choice in field.Choices)
        {
            if (u < choice.Share)
            {
                return choice.Name;
            }

            u -= choice.Share;
        }

        return field.Choices[^1].Name;
    }
}
