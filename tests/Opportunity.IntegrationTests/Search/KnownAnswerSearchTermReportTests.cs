using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Audit;
using Opportunity.Core.Security;
using Opportunity.DataGenerator.Corpus.KnownAnswer;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Snapshots;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E17-T09 for E07-T10: the generated known-answer STR corpus (term frequencies, a stemming family, hyphenated and
/// apostrophe tokens with near-miss decoys, <c>W/5</c> pairs at and just past the limit, an attachment-only term) is
/// loaded with its families, indexed through the projection mapping's real analyzers and reported on through the API.
/// Every term's counts, the totals and each term's hit set equal the generator's ground truth.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class KnownAnswerSearchTermReportTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private const ulong Seed = 147;

    [Fact]
    public async Task The_report_on_the_generated_corpus_equals_its_ground_truth()
    {
        var fixture = StrFixtureGenerator.Generate(Seed);
        var truth = StrFixtureGenerator.GroundTruth(fixture);
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var ws = await h.Search.WorkspaceAsync();
        var qc = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var document in fixture.Documents)
        {
            // Parents precede their attachments in load order.
            ids[document.ControlNumber] = await h.DocumentAsync(ws, document.ControlNumber, document.Text,
                parent: document.ParentControlNumber is { } parent ? ids[parent] : null, sequence: document.FamilySequence);
        }

        await using var factory = SearchTermReportApiTests.Factory(h, new InMemoryAuditEventWriter());
        using var client = factory.CreateClient();
        var body = new JsonObject
        {
            ["name"] = $"Known answer seed {Seed}",
            ["terms"] = new JsonArray([.. fixture.Terms.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["expression"] = t.Expression })]),
            ["scope"] = new JsonObject { ["kind"] = "workspace" },
        };
        var report = await SearchTermReportApiTests.CreateAndRunAsync(client, factory, ws, $"/api/v1/workspaces/{ws}/search-term-reports", qc, body, "ka1");

        var reportId = report.GetProperty("reportId").GetGuid();
        var rows = await h.Search.Db.Core.ColumnAsync(
            $"""
            SELECT t.term_no || '|' || d.control_number
              FROM opportunity.search_term_report_hit x
              JOIN opportunity.search_term_report_term t ON t.workspace_id = x.workspace_id AND t.report_id = x.report_id AND t.term_no = x.term_no
              JOIN opportunity.document d ON d.workspace_id = x.workspace_id AND d.document_id = x.document_id
             WHERE x.workspace_id = '{ws}' AND x.report_id = '{reportId}'
            """);
        var hitSets = rows.Select(r => r.Split('|')).GroupBy(r => int.Parse(r[0], System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Select(r => r[1]).Order(StringComparer.Ordinal).ToList());
        for (var t = 0; t < truth.Terms.Count; t++)
        {
            hitSets.GetValueOrDefault(t + 1, []).Should().Equal(truth.Terms[t].HitDocuments, $"the hit set of {truth.Terms[t].Expression}");
        }

        SearchTermReportApiTests.Counts(report).Should().Equal(
            truth.Terms.Select(t => (t.Name, t.DocumentsWithHits, t.DocumentsWithHitsIncludingFamily, t.UniqueHits, t.UniqueHitsIncludingFamily)));
        SearchTermReportApiTests.Totals(report).Should().Equal(
            truth.Totals.DocumentsInScope, truth.Totals.DocumentsWithHits, truth.Totals.DocumentsWithHitsIncludingFamily, truth.Totals.DocumentsWithoutHits);
    }
}
