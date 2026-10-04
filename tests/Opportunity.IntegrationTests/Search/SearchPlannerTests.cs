using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Fields;
using Opportunity.Application.Search;
using Opportunity.Contracts.Search;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search.Indexing;
using Opportunity.Search.Querying;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E07-T07 against a real OpenSearch with the projection v2 mapping and a real field catalogue: every AST node kind
/// and every §9 example returns exactly the known answers of a small hand-built corpus, <c>apple W/10 iphone</c>
/// included; binding problems and cost limits are positioned errors, never a silent match-none.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class SearchPlannerTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_node_kind_and_every_S9_example_returns_the_known_answers()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var c = await CorpusAsync(h);

        var cases = new (string Query, string[] Expected)[]
        {
            // §9 examples.
            ("contract AND termination", ["C-1"]),
            ("\"trade secret\"", ["T-1"]),
            ("apple W/10 iphone", ["APL-01", "APL-03", "APL-05", "APL-06"]),
            ("custodian:\"John Smith\"", ["CU-1", "CU-4"]),
            ("date:[2025-01-01 TO 2025-12-31]", ["D-2"]),
            ("filename:*.xlsx", ["F-1"]),

            // One per remaining node kind (MatchAll, Or, And, Not, Proximity, Field, Term, Phrase, Wildcard, Range, Exists).
            ("", c.All),
            ("termination OR secret", ["C-1", "T-1", "T-2"]),
            ("NOT apple", [.. c.All.Where(n => !n.StartsWith("APL-", StringComparison.Ordinal))]),
            ("contr* AND NOT termination", ["C-2"]),
            ("TERMINATION", ["C-1"]),
            ("custodian:*", ["CU-1", "CU-2", "CU-3", "CU-4"]),
            ("filesize:[100 TO 200}", ["F-1"]),
            ("controlnumber:[R9 TO R10]", ["R10", "R9"]),

            // Proximity details: phrase and OR operands, wildcard operands, a field around the proximity.
            ("\"trade secret\" W/2 stolen", ["T-1"]),
            ("(pear OR apple) W/1 iphone", ["APL-05"]),
            ("appl* W/1 iphon*", ["APL-05"]),
            ("notes:(budget W/3 forecast)", ["N-1"]),
            ("e-mail W/2 sent", ["E-1"]),

            // Coding filters against the interim projection, choice names case-insensitive.
            ("responsiveness:\"NOT RESPONSIVE\"", ["CU-2"]),
            ("hot:yes AND contract", ["C-1"]),
            ("filetype:email AND NOT hot:true", [.. c.All.Where(n => n != "C-1")]),
        };

        foreach (var (query, expected) in cases)
        {
            var outcome = await h.SearchAsync(c.Workspace, c.User, query, pageSize: 100);
            outcome.Status.Should().Be(SearchStatus.Ok, "{0}: {1}", query, string.Join("; ", outcome.QueryErrors.Select(e => e.Code + " " + e.Message)));
            outcome.Page!.Items.Select(i => i.ControlNumber).Should().BeEquivalentTo(expected, "query {0}", query);
        }
    }

    [Fact]
    public async Task Binding_problems_and_cost_limits_are_positioned_errors()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var c = await CorpusAsync(h);

        var cases = new (string Query, string Code, int Start, int End)[]
        {
            ("custodain:smith", SearchQueryErrorCodes.UnknownField, 0, 9),
            ("workspaceId:x", SearchQueryErrorCodes.UnknownField, 0, 11),
            ("contract AND *tract", SearchQueryErrorCodes.LeadingWildcard, 13, 19),
            ("custodian:[a TO b]", SearchQueryErrorCodes.UnsupportedForField, 10, 18),
            ("custodian:(john W/2 smith)", SearchQueryErrorCodes.UnsupportedForField, 10, 26),
            ("responsiveness:maybe", SearchQueryErrorCodes.InvalidFieldValue, 15, 20),
            ("date:yesterday", SearchQueryErrorCodes.InvalidFieldValue, 5, 14),
            ("contract &", SearchQueryErrorCodes.NoSearchableTerms, 9, 10),
            ("(aaa* OR bbb* OR ccc* OR ddd* OR eee* OR fff* OR ggg* OR hhh* OR iii*) W/3 x", "TOO_MANY_WILDCARDS", 65, 69),

            // R7: the expansion is never truncated; OpenSearch's clause limit becomes WILDCARD_TOO_BROAD at the term.
            ("zqx* W/5 anchor", SearchQueryErrorCodes.WildcardTooBroad, 0, 4),
        };

        foreach (var (query, code, start, end) in cases)
        {
            var outcome = await h.SearchAsync(c.Workspace, c.User, query);
            outcome.Status.Should().Be(SearchStatus.InvalidQuery, query);
            var error = outcome.QueryErrors.Should().ContainSingle(query).Subject;
            error.Code.Should().Be(code, query);
            error.Span.Should().Be(new TextSpan(start, end), query);
            error.Message.Should().NotBeNullOrWhiteSpace();
        }

        // Without W/n the same broad wildcard runs (constant_score never truncates and never fails).
        Ok(await h.SearchAsync(c.Workspace, c.User, "zqx* AND anchor")).Items.Select(i => i.ControlNumber).Should().Equal("BROAD");
    }

    [Fact]
    public async Task Query_analysis_uses_OpenSearch_analyzers()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var analyzer = h.Services.GetRequiredService<ISearchTextAnalyzer>();

        var tokens = await analyzer.AnalyzeAsync(2, "text", AnalysisMode.Tokens, ["e-mail", "Café", "&", "東京", "plain", "U.S.A."], Ct);
        var ident = await analyzer.AnalyzeAsync(2, "fileName", AnalysisMode.Tokens, ["Budget_2025.XLSX"], Ct);
        var patterns = await analyzer.AnalyzeAsync(2, "text", AnalysisMode.Normalize, ["Café*", "Ünï?ode"], Ct);

        tokens.Select(t => string.Join('|', t)).Should().Equal("e|mail", "cafe", "", "東|京", "plain", "u.s.a");
        ident.Single().Should().Equal("budget", "2025", "xlsx");
        patterns.Select(p => p.Single()).Should().Equal("cafe*", "uni?ode");
    }

    [Fact]
    public async Task The_validate_binder_reports_the_same_errors_as_a_search()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var c = await CorpusAsync(h);
        var validator = new QueryValidator(Core.QueryLanguage.QueryLimits.Default);
        var binder = h.Services.GetRequiredService<IQueryBinder>();

        var result = await validator.ValidateAsync("responsivenes:responsive", c.Workspace, binder, Ct);

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Expected.Should().Equal("responsiveness");
        (await validator.ValidateAsync("responsiveness:responsive", c.Workspace, binder, Ct)).Valid.Should().BeTrue();
    }

    private static SearchResultPage Ok(SearchOutcome outcome)
    {
        outcome.Status.Should().Be(SearchStatus.Ok, string.Join("; ", outcome.QueryErrors.Select(e => e.Code)));
        return outcome.Page!;
    }

    private sealed record Corpus(Guid Workspace, Guid User, string[] All);

    private static async Task<Corpus> CorpusAsync(SearchHarness h)
    {
        var ws = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws);
        await h.Fields.InitializeWorkspaceAsync(ws, Ct);
        var custodian = await FieldAsync(h, new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata));
        var notes = await FieldAsync(h, new NewField(ws, "Notes", FieldType.Text, FieldStorage.Metadata, TextAnalysis: TextAnalysis.Prose));
        var hot = await FieldAsync(h, new NewField(ws, "Hot", FieldType.Boolean, FieldStorage.Coding));
        var responsiveness = await FieldAsync(h, new NewField(ws, "Responsiveness", FieldType.SingleChoice, FieldStorage.Coding));
        var responsive = (await h.Fields.AddChoiceAsync(ws, responsiveness.FieldId, "Responsive", Ct)).Value!;
        var notResponsive = (await h.Fields.AddChoiceAsync(ws, responsiveness.FieldId, "Not Responsive", Ct)).Value!;
        var allCustodians = SystemFields.ReservedSlots[SystemFields.AllCustodians];

        var nine = string.Join(' ', Enumerable.Range(1, 9).Select(i => "w" + i));
        var ten = string.Join(' ', Enumerable.Range(1, 10).Select(i => "w" + i));
        var names = new List<string>();

        async Task Doc(string control, string text, Action<JsonObject>? extra = null)
        {
            var id = await h.Db.DocumentAsync(ws);
            var row = new JsonObject
            {
                ["workspaceId"] = ws.ToString("D"),
                ["documentId"] = id.ToString("D"),
                ["controlNumber"] = control,
                ["controlNumberSort"] = ControlNumber.SortKey(control),
                ["fileName"] = control + ".msg",
                ["fileType"] = "Email",
                ["securityTags"] = new JsonArray(),
                ["text"] = text,
            };
            extra?.Invoke(row);
            await h.ProjectAsync(ws, row, id);
            names.Add(control);
        }

        static void Slot(JsonObject row, string container, string slot, JsonNode value)
        {
            var dot = slot.IndexOf('.', StringComparison.Ordinal);
            if (row[container] is not JsonObject root)
            {
                row[container] = root = [];
            }

            if (root[slot[..dot]] is not JsonObject kind)
            {
                root[slot[..dot]] = kind = [];
            }

            kind[slot[(dot + 1)..]] = value;
        }

        // apple W/10 iphone: at most 9 words between, either order, any occurrence.
        await Doc("APL-01", $"apple {nine} iphone");
        await Doc("APL-02", $"apple {ten} iphone");
        await Doc("APL-03", "iphone w1 w2 w3 apple");
        await Doc("APL-04", "apple without the other one");
        await Doc("APL-05", "APPLE IPHONE");
        await Doc("APL-06", $"apple {ten} {ten} then apple {nine} iphone");

        await Doc("C-1", "termination of the supply contract", r => Slot(r, "coding", hot.SearchSlot!, true));
        await Doc("C-2", "the contract continues", r => Slot(r, "coding", hot.SearchSlot!, false));
        await Doc("T-1", "the trade secret was stolen");
        await Doc("T-2", "a secret trade route");
        await Doc("E-1", "the e-mail was sent late");
        await Doc("N-1", "notes doc", r => Slot(r, "metadata", notes.SearchSlot!, "the budget for the forecast"));
        await Doc("N-2", "notes doc", r => Slot(r, "metadata", notes.SearchSlot!, "the budget was cut, no forecast at all"));

        await Doc("CU-1", "custodian doc", r =>
        {
            Slot(r, "metadata", custodian.SearchSlot!, "John Smith");
            Slot(r, "coding", responsiveness.SearchSlot!, responsive.ChoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        await Doc("CU-2", "custodian doc", r =>
        {
            Slot(r, "metadata", custodian.SearchSlot!, "John Smithers");
            Slot(r, "coding", responsiveness.SearchSlot!, notResponsive.ChoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        await Doc("CU-3", "custodian doc", r =>
        {
            Slot(r, "metadata", custodian.SearchSlot!, "Jane Doe");
            Slot(r, "metadata", allCustodians, new JsonArray("Jane Doe", "John Smith"));
        });
        await Doc("CU-4", "custodian doc", r => Slot(r, "metadata", custodian.SearchSlot!, "JOHN SMITH"));

        await Doc("D-1", "dated", r => r["documentDate"] = "2024-12-31T23:59:59Z");
        await Doc("D-2", "dated", r => r["documentDate"] = "2025-12-31T23:00:00Z");
        await Doc("D-3", "dated", r => r["documentDate"] = "2026-01-01T00:00:00Z");

        await Doc("F-1", "file", r => (r["fileName"], r["fileSize"]) = ("Budget.XLSX", 150));
        await Doc("F-2", "file", r => (r["fileName"], r["fileSize"]) = ("xlsx-notes.docx", 200));

        await Doc("R9", "range");
        await Doc("R10", "range");
        await Doc("R100", "range");

        // More distinct zqx… terms than any clause limit a node may be configured with (default 1,024; ADR-008 4,096).
        await Doc("BROAD", "anchor " + string.Join(' ', Enumerable.Range(0, 5_000).Select(i => "zqx" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))));

        return new Corpus(ws, user, [.. names]);
    }

    private static async Task<FieldDefinition> FieldAsync(SearchHarness h, NewField field)
    {
        var result = await h.Fields.CreateFieldAsync(field, Ct);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Value!;
    }
}
