using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Search;
using Opportunity.Core.Documents;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Snapshots;
using Opportunity.Search.Indexing;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E09-T03 expansion budget over a family-heavy corpus (1 parent : 3 attachments). Opt-in (it writes a large data set):
/// run with <c>OPPORTUNITY_EXPANSION_BENCHMARK=1</c> and optionally <c>OPPORTUNITY_EXPANSION_BENCHMARK_DOCUMENTS</c>
/// (default 100,000 — CI scale; 1,000,000 on reference hardware, Q-70) and <c>OPPORTUNITY_EXPANSION_BENCHMARK_OUT</c> (a
/// file for the Markdown result). Measures, for a narrow query (every 10th family's parent) and a broad one (the first
/// attachment of every family), the interactive first page and a next page with "Include family", and a snapshot frozen
/// with family expansion. The recorded result is in ADR-009 §6 (interim); nothing is asserted about absolute times
/// (Q-44, Q-70), only that the expanded sets are exactly right.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class RelationshipExpansionBenchmark(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private const int FamilySize = 4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Family_expansion_over_a_family_heavy_corpus()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OPPORTUNITY_EXPANSION_BENCHMARK") == "1",
            "Opt-in benchmark: set OPPORTUNITY_EXPANSION_BENCHMARK=1 (ADR-009 §6, E09-T03).");
        var documents = int.Parse(Environment.GetEnvironmentVariable("OPPORTUNITY_EXPANSION_BENCHMARK_DOCUMENTS") ?? "100000", CultureInfo.InvariantCulture);
        var families = documents / FamilySize;

        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres, o =>
        {
            o.SynchronousMaxDocuments = 2_000_000;
            o.SelectionPageSize = 10_000;
        });
        var ws = await h.Search.WorkspaceAsync(dedicated: true);
        var user = await h.MemberAsync(ws, WorkspaceRole.Reviewer);
        var load = Stopwatch.StartNew();
        await LoadAsync(h, ws, families);
        load.Stop();

        var rows = new List<string>();
        foreach (var (name, query, hits, expanded) in new[]
        {
            ("narrow (10% of families, parents)", "needle", families / 10, families / 10 * FamilySize),
            ("broad (every family, first attachment)", "firstattachment", families, families * FamilySize),
        })
        {
            var first = Stopwatch.StartNew();
            var outcome = await h.Search.InScopeSearchAsync(ws, user,
                new SearchRequest(query, PageSize: 100, Highlight: false, Expand: new SearchExpand(Family: true)));
            first.Stop();
            outcome.Status.Should().Be(Application.Search.SearchStatus.Ok, string.Join(";", outcome.RequestErrors.SelectMany(e => e.Value)));
            var page = outcome.Page!;
            page.Total.Value.Should().Be(hits);
            page.Expanded!.Total.Should().Be(expanded);
            page.Expanded.Family.Should().Be(expanded - hits);

            var next = Stopwatch.StartNew();
            var second = await h.Search.PageAsync(ws, user, page.SearchId, new Application.Search.SearchPageRequest(page.NextCursor));
            next.Stop();
            second.Status.Should().Be(Application.Search.SearchStatus.Ok);

            var plain = Stopwatch.StartNew();
            (await h.ReadyAsync(ws, user, new SnapshotCreateRequest(SnapshotPurpose.Report, Query: query))).DocumentCount.Should().Be(hits);
            plain.Stop();

            var freeze = Stopwatch.StartNew();
            var snapshot = await h.ReadyAsync(ws, user, new SnapshotCreateRequest(SnapshotPurpose.Report, Query: query,
                Expansion: new RelationshipExpansion(true, false, false)));
            freeze.Stop();
            snapshot.DocumentCount.Should().Be(expanded);
            snapshot.InclusionCounts[SnapshotInclusionReason.Family].Should().Be(expanded - hits);

            rows.Add(string.Create(CultureInfo.InvariantCulture,
                $"| {documents:N0} | {name} | {hits:N0} | {expanded:N0} | {first.Elapsed.TotalSeconds:F2} s | {next.Elapsed.TotalSeconds:F2} s | {plain.Elapsed.TotalSeconds:F2} s | {freeze.Elapsed.TotalSeconds:F2} s |"));
        }

        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Corpus load (PostgreSQL + bulk index): {load.Elapsed.TotalSeconds:F1} s")
            .AppendLine()
            .AppendLine("| Documents | Query | Base hits | Expanded | First page (keys + page) | Next page | Snapshot without expansion | Snapshot with family |")
            .AppendLine("|---|---|---|---|---|---|---|---|");
        rows.ForEach(r => report.AppendLine(r));
        TestContext.Current.SendDiagnosticMessage(report.ToString());
        if (Environment.GetEnvironmentVariable("OPPORTUNITY_EXPANSION_BENCHMARK_OUT") is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, report.ToString(), Ct);
        }
    }

    /// <summary>
    /// <paramref name="families"/> families of <see cref="FamilySize"/>: in PostgreSQL (one statement) and bulk-indexed.
    /// Every 10th parent says "needle"; the first attachment of every family says "firstattachment".
    /// </summary>
    private static async Task LoadAsync(SnapshotHarness h, Guid ws, int families)
    {
        var core = h.Search.Db.Core;
        await core.ExecuteAsync(
            """
            WITH m AS (
                SELECT f, s, md5(@ws::text || ':' || f || ':' || s)::uuid AS id, md5(@ws::text || ':' || f || ':0')::uuid AS family
                  FROM generate_series(1, @families) AS f, generate_series(0, 3) AS s)
            INSERT INTO opportunity.document
                (workspace_id, document_id, control_number, control_number_norm, family_id, parent_document_id, family_sequence)
            SELECT @ws, m.id, 'FAM' || lpad(m.f::text, 8, '0') || '.' || m.s, 'FAM' || lpad(m.f::text, 8, '0') || '.' || m.s, m.family,
                   CASE WHEN m.s = 0 THEN NULL ELSE m.family END, m.s
              FROM m
            """,
            ("ws", ws), ("families", families));
        await core.ExecuteAsync(
            "INSERT INTO opportunity.document_projection_state (workspace_id, document_id) SELECT workspace_id, document_id FROM opportunity.document WHERE workspace_id = @ws",
            ("ws", ws));
        await core.ExecuteAsync("ANALYZE opportunity.document");

        var rows = await core.ColumnAsync(
            $"SELECT document_id::text || '|' || control_number || '|' || family_id::text || '|' || family_sequence FROM opportunity.document WHERE workspace_id = '{ws}'");
        var placement = await h.Search.Indexes.ResolveAsync(ws, IndexPurpose.Write, Ct);
        foreach (var target in placement.WriteTargets)
        {
            foreach (var batch in rows.Chunk(5_000))
            {
                var body = new StringBuilder();
                foreach (var row in batch)
                {
                    var p = row.Split('|');
                    var sequence = int.Parse(p[3], CultureInfo.InvariantCulture);
                    var family = int.Parse(p[1][3..11], CultureInfo.InvariantCulture);
                    var action = new JsonObject { ["index"] = new JsonObject { ["_index"] = target.Index, ["_id"] = p[0] } };
                    if (target.Routing is { } routing)
                    {
                        action["index"]!["routing"] = routing;
                    }

                    body.Append(action.ToJsonString()).Append('\n');
                    body.Append(new JsonObject
                    {
                        ["workspaceId"] = ws.ToString("D"),
                        ["documentId"] = p[0],
                        ["controlNumber"] = p[1],
                        ["controlNumberSort"] = p[1],
                        ["familyId"] = p[2],
                        ["familySequence"] = sequence,
                        ["familyDate"] = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(family).ToString("O"),
                        ["securityTags"] = new JsonArray(),
                        ["text"] = sequence == 0 ? (family % 10 == 0 ? "needle parent" : "parent") : sequence == 1 ? "firstattachment" : "attachment",
                    }.ToJsonString()).Append('\n');
                }

                using var content = new StringContent(body.ToString(), Encoding.UTF8, "application/x-ndjson");
                using var response = await h.Search.OpenSearchHttp.PostAsync("_bulk", content, Ct);
                response.EnsureSuccessStatusCode();
                var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
                result["errors"]!.GetValue<bool>().Should().BeFalse();
            }

            using var refresh = await h.Search.OpenSearchHttp.PostAsync($"{target.Index}/_refresh", null, Ct);
            refresh.EnsureSuccessStatusCode();
        }
    }
}
