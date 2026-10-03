using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Core.QueryLanguage;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Opportunity.UnitTests.QueryLanguage;

/// <summary>
/// ADR-008 R18–R19 golden-file contract tests: one case per <c>QueryLanguage/Golden/*.yaml</c>, holding the query and
/// the expected canonical AST JSON, canonical text and warnings, or the expected error code, span and expected tokens.
/// The planner (E07-T07) adds the expected <c>dsl</c> to the same files. Files change only through an explicit update
/// run (<c>OPPORTUNITY_UPDATE_GOLDEN=1 dotnet test --project tests/Opportunity.UnitTests</c>), reviewed by the Search
/// owner.
/// </summary>
public class QueryGoldenTests
{
    private const string UpdateVariable = "OPPORTUNITY_UPDATE_GOLDEN";

    private static readonly string Directory = Path.Combine(RepositoryRoot(), "tests", "Opportunity.UnitTests", "QueryLanguage", "Golden");

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static TheoryData<string> Cases => new(System.IO.Directory.EnumerateFiles(Directory, "*.yaml").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!);

    [Fact]
    public void The_suite_covers_every_S9_example_and_rule()
    {
        var names = Cases.Select(row => row.Data).ToList();

        names.Should().Contain(["s9-contract-and-termination", "s9-trade-secret", "s9-apple-w10-iphone", "s9-custodian-john-smith", "s9-date-range", "s9-filename-xlsx"]);
        foreach (var rule in Enumerable.Range(1, 10).Select(r => $"r{r}-"))
        {
            names.Should().Contain(n => n.StartsWith(rule, StringComparison.Ordinal), $"rule {rule} needs a golden case");
        }

        names.Should().Contain(n => n.StartsWith("limit-", StringComparison.Ordinal));
        names.Should().Contain(n => n.StartsWith("error-", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Golden_case(string name)
    {
        var path = Path.Combine(Directory, name + ".yaml");
        var golden = Yaml.Deserialize<GoldenCase>(File.ReadAllText(path));
        golden.Query.Should().NotBeNull($"{name} must have a query");

        var result = QueryParser.Parse(golden.Query!);
        var actual = GoldenCase.From(golden.Description, golden.Query!, result);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, actual.ToYaml());
            return;
        }

        if (golden.Error is null)
        {
            result.Errors.Should().BeEmpty($"{name}: {string.Join("; ", result.Errors.Select(e => e.Message))}");
            JsonNode.DeepEquals(JsonNode.Parse(golden.Ast!), JsonNode.Parse(actual.Ast!)).Should().BeTrue($"{name}: AST differs:\n{actual.Ast}");
            actual.Printed.Should().Be(golden.Printed, name);
            Describe(actual.Warnings).Should().Equal(Describe(golden.Warnings), name);
            QueryAstComparer.IgnoringSpans.Equals(QueryParser.Parse(actual.Printed!).Ast, result.Ast).Should().BeTrue($"{name}: canonical text must round-trip");
        }
        else
        {
            result.Success.Should().BeFalse($"{name} expects error {golden.Error.Code}");
            actual.Error!.Code.Should().Be(golden.Error.Code, name);
            actual.Error.Span.Should().Equal(golden.Error.Span, name);
            actual.Error.Expected.Should().Equal(golden.Error.Expected, name);
        }
    }

    private static IEnumerable<string> Describe(List<GoldenDiagnostic> diagnostics) =>
        diagnostics.Select(d => $"{d.Code}@{string.Join(',', d.Span)}");

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Opportunity.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Opportunity.slnx) not found.");
    }

#pragma warning disable CA2227 // YamlDotNet populates collections through setters.
    public sealed class GoldenCase
    {
        public string? Description { get; set; }

        public string? Query { get; set; }

        public string? Printed { get; set; }

        public List<GoldenDiagnostic> Warnings { get; set; } = [];

        public string? Ast { get; set; }

        public GoldenDiagnostic? Error { get; set; }

        public static GoldenCase From(string? description, string query, QueryParseResult result) => new()
        {
            Description = description,
            Query = query,
            Printed = result.Ast is { } ast ? QueryPrinter.Print(ast) : null,
            Ast = result.Ast is { } tree ? QueryAstJson.Serialize(tree, indented: true) : null,
            Warnings = [.. result.Warnings.Select(GoldenDiagnostic.From)],
            Error = result.Errors.Count > 0 ? GoldenDiagnostic.From(result.Errors[0]) : null,
        };

        public string ToYaml()
        {
            var b = new StringBuilder();
            b.Append("description: ").AppendLine(Quote(Description ?? string.Empty));
            b.Append("query: ").AppendLine(Quote(Query!));
            if (Error is not null)
            {
                b.AppendLine("error:");
                b.Append("  code: ").AppendLine(Error.Code);
                b.Append("  span: [").Append(string.Join(", ", Error.Span)).AppendLine("]");
                b.Append("  expected: [").Append(string.Join(", ", Error.Expected.Select(Quote))).AppendLine("]");
                b.Append("  message: ").AppendLine(Quote(Error.Message ?? string.Empty));
                return b.ToString();
            }

            b.Append("printed: ").AppendLine(Quote(Printed!));
            if (Warnings.Count == 0)
            {
                b.AppendLine("warnings: []");
            }
            else
            {
                b.AppendLine("warnings:");
                foreach (var warning in Warnings)
                {
                    b.Append("  - code: ").AppendLine(warning.Code);
                    b.Append("    span: [").Append(string.Join(", ", warning.Span)).AppendLine("]");
                }
            }

            b.AppendLine("ast: |");
            foreach (var line in Ast!.Split('\n'))
            {
                b.Append("  ").AppendLine(line.TrimEnd('\r'));
            }

            return b.ToString();
        }

        private static string Quote(string value) => JsonSerializer.Serialize(value, Relaxed);
    }

    public sealed class GoldenDiagnostic
    {
        public string Code { get; set; } = string.Empty;

        public List<int> Span { get; set; } = [];

        public List<string> Expected { get; set; } = [];

        /// <summary>Informational; not compared, so wording can improve without a golden change.</summary>
        public string? Message { get; set; }

        public static GoldenDiagnostic From(QueryDiagnostic d) => new()
        {
            Code = d.Code,
            Span = [d.Span.Start, d.Span.End],
            Expected = [.. d.Expected],
            Message = d.Message,
        };
    }
#pragma warning restore CA2227
}
