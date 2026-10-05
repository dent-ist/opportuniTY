using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Search.TermReports;
using Opportunity.Core.Documents;
using Opportunity.Core.SearchTermReports;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Snapshots;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E07-T10 through the real API host, PostgreSQL PDP and OpenSearch, on a hand-built known-answer corpus (E17-T09's
/// generated fixtures do not exist yet): two families and standalone documents with known term hits, one document
/// walled from the executor. Counts, family expansion and unique hits equal the hand-computed answer and
/// <see cref="FamilyHitCounting"/>; reruns give identical numbers; an invalid term gets its own error; exports carry the
/// provenance; a term opens as a search; a stale index is reported.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class SearchTermReportApiTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Known_answer_corpus_counts_family_and_unique_hits_and_reruns_identically()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var ws = await h.Search.WorkspaceAsync();
        var qc = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        var privilege = await h.MemberAsync(ws, WorkspaceRole.PrivilegeReviewer);
        var reviewer = await h.MemberAsync(ws, WorkspaceRole.Reviewer);

        // Family F1: parent hits alpha, attachment hits beta, second attachment hits nothing. Family F2: parent hits alpha,
        // attachment nothing. Standalone: beta; alpha + beta; nothing; and an alpha document walled from the QC reviewer.
        var p1 = await h.DocumentAsync(ws, "STR-0001", "alpha contract");
        var a1 = await h.DocumentAsync(ws, "STR-0002", "beta schedule", parent: p1, sequence: 1);
        var a2 = await h.DocumentAsync(ws, "STR-0003", "plain attachment", parent: p1, sequence: 2);
        var p2 = await h.DocumentAsync(ws, "STR-0004", "alpha memo");
        var a3 = await h.DocumentAsync(ws, "STR-0005", "nothing here", parent: p2, sequence: 1);
        var s1 = await h.DocumentAsync(ws, "STR-0006", "beta note");
        var s2 = await h.DocumentAsync(ws, "STR-0007", "alpha beta");
        var s3 = await h.DocumentAsync(ws, "STR-0008", "unrelated");
        var walled = await h.DocumentAsync(ws, "STR-0009", "alpha walled", projectSecurity: false);
        await h.Search.Db.WallAsync(ws, [qc], [], [walled]);

        var audit = new InMemoryAuditEventWriter();
        await using var factory = Factory(h, audit);
        using var client = factory.CreateClient();
        var url = $"/api/v1/workspaces/{ws}/search-term-reports";
        var body = new JsonObject
        {
            ["name"] = "Known answer",
            ["terms"] = new JsonArray(
                new JsonObject { ["name"] = "Alpha", ["expression"] = "alpha" },
                new JsonObject { ["name"] = "Beta", ["expression"] = "beta" },
                new JsonObject { ["name"] = "Gamma", ["expression"] = "gamma" },
                new JsonObject { ["name"] = "Broken", ["expression"] = "alpha AND (" }),
            ["scope"] = new JsonObject { ["kind"] = "workspace" },
        };

        // Permissions: the Reviewer role does not hold SearchTermReport.Run.
        await (await SendAsync(client, HttpMethod.Post, url, reviewer, body, "k0")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        using var created = await SendAsync(client, HttpMethod.Post, url, qc, body, "k1");
        created.StatusCode.Should().Be(HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync(Ct));
        var accepted = await JsonAsync(created);
        accepted.GetProperty("status").GetString().Should().Be("queued");
        accepted.GetProperty("job").GetProperty("jobType").GetString().Should().Be("searchTermReport");
        accepted.GetProperty("terms")[0].GetProperty("documentsWithHits").ValueKind.Should().Be(JsonValueKind.Null, "counts are null until completed");
        var reportId = accepted.GetProperty("reportId").GetGuid();
        (await RunAsync(factory, ws, reportId)).Should().Be(SearchTermReportStatus.Completed);

        var report = await GetAsync(client, $"{url}/{reportId}", qc);
        report.GetProperty("status").GetString().Should().Be("completed");
        report.GetProperty("indexCurrent").GetBoolean().Should().BeTrue();
        report.GetProperty("searchGeneration").ValueKind.Should().Be(JsonValueKind.Number);
        var snapshotId = report.GetProperty("snapshotId").GetGuid();
        Counts(report).Should().Equal(
            ("Alpha", 3L, 6L, 2L, 2L), ("Beta", 3L, 5L, 2L, 1L), ("Gamma", 0L, 0L, 0L, 0L), ("Broken", -1L, -1L, -1L, -1L));
        Totals(report).Should().Equal(8L, 5L, 7L, 3L);
        var broken = report.GetProperty("terms")[3].GetProperty("error");
        broken.GetProperty("code").GetString().Should().NotBeNullOrEmpty();
        broken.GetProperty("position").ValueKind.Should().Be(JsonValueKind.Number);

        // The in-memory reference agrees (the walled document is not in the executor's scope).
        static FamilyScopedDocument D(Guid id, Guid family, params int[] terms) => new(id, family, new HashSet<int>(terms));
        var (reference, referenceTotals) = FamilyHitCounting.Count(
            [D(p1, p1, 1), D(a1, p1, 2), D(a2, p1), D(p2, p2, 1), D(a3, p2), D(s1, s1, 2), D(s2, s2, 1, 2), D(s3, s3)], [1, 2, 3]);
        reference.Select(r => (r.WithHits, r.WithHitsIncludingFamily, r.Unique, r.UniqueIncludingFamily))
            .Should().Equal(Counts(report).Take(3).Select(c => (c.Item2, c.Item3, c.Item4, c.Item5)));
        referenceTotals.Should().Be(new ScopeHitTotals(8, 5, 7, 3));

        // Rerun on the same snapshot: identical numbers.
        using (var rerun = await SendAsync(client, HttpMethod.Post, $"{url}/{reportId}/rerun", qc, null, "k2"))
        {
            rerun.StatusCode.Should().Be(HttpStatusCode.Accepted, await rerun.Content.ReadAsStringAsync(Ct));
        }

        await (await SendAsync(client, HttpMethod.Post, $"{url}/{reportId}/rerun", qc, null, "k3")).ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");
        (await RunAsync(factory, ws, reportId)).Should().Be(SearchTermReportStatus.Completed);
        var again = await GetAsync(client, $"{url}/{reportId}", qc);
        again.GetProperty("snapshotId").GetGuid().Should().Be(snapshotId);
        again.GetProperty("jobId").GetGuid().Should().NotBe(report.GetProperty("jobId").GetGuid());
        Counts(again).Should().Equal(Counts(report));
        Totals(again).Should().Equal(Totals(report));

        // Someone who is not walled counts the walled document too (counts are the executor's).
        var other = await CreateAndRunAsync(client, factory, ws, url, privilege, body, "k4");
        Counts(other)[0].Should().Be(("Alpha", 4L, 7L, 3L, 3L));
        Totals(other).Should().Equal(9L, 6L, 8L, 3L);

        // Visibility: the creator and Job.ViewAll; another QC reviewer does not see it; the list shows own reports.
        await (await SendAsync(client, HttpMethod.Get, $"{url}/{reportId}", privilege, null)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        var list = await GetAsync(client, url, qc);
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reportId").GetGuid()).Should().Equal(reportId);
        list.GetProperty("items")[0].GetProperty("termCount").GetInt32().Should().Be(4);

        // Open a term as a search: exactly its hits within the snapshot, filtered for the caller.
        var alphaTerm = again.GetProperty("terms")[0].GetProperty("termId").GetGuid();
        using (var search = await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", qc,
            new JsonObject { ["searchTermReportId"] = reportId.ToString(), ["termId"] = alphaTerm.ToString(), ["pageSize"] = 50 }, null))
        {
            var page = await JsonAsync(search);
            page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("controlNumber").GetString()).Order()
                .Should().Equal("STR-0001", "STR-0004", "STR-0007");
        }

        var brokenTerm = again.GetProperty("terms")[3].GetProperty("termId").GetGuid();
        await (await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", qc,
            new JsonObject { ["searchTermReportId"] = reportId.ToString(), ["termId"] = brokenTerm.ToString() }, null))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        // Exports through the gateway, audited.
        using (var csv = await SendAsync(client, HttpMethod.Get, $"{url}/{reportId}/export?format=csv", qc, null))
        {
            csv.StatusCode.Should().Be(HttpStatusCode.OK);
            csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
            var text = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync(Ct));
            text.Should().Contain($"Snapshot ID,{snapshotId:D}");
            text.Should().Contain("Index current at execution,Yes");
            text.Should().Contain("Alpha,alpha,3,6,2,2,");
            text.Should().Contain("Total documents in scope,,8");
        }

        using (var xlsx = await SendAsync(client, HttpMethod.Get, $"{url}/{reportId}/export?format=xlsx", qc, null))
        {
            xlsx.StatusCode.Should().Be(HttpStatusCode.OK);
            using var zip = new ZipArchive(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync(Ct)));
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            (await reader.ReadToEndAsync(Ct)).Should().Contain("Known answer").And.Contain("<v>6</v>");
        }

        audit.Events.Where(e => e.Action == AuditTaxonomy.SearchTermReport.Exported).Select(e => e.Details["format"]).Should().Equal("csv", "xlsx");
        (await h.Search.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Search' AND action = 'TermReportGenerated' AND snapshot_id = @s",
            ("ws", ws), ("s", snapshotId))).Should().Be(2, "each run is audited with its snapshot");

        // Delete: the creator; the report and its hit rows go.
        using (var deleted = await SendAsync(client, HttpMethod.Delete, $"{url}/{reportId}", qc, null))
        {
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await h.Search.Db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.search_term_report_hit WHERE workspace_id = @ws AND report_id = @r",
            ("ws", ws), ("r", reportId))).Should().Be(0);
    }

    [Fact]
    public async Task Saved_search_scope_csv_terms_and_a_stale_index_are_reported()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var ws = await h.Search.WorkspaceAsync();
        var qc = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        await h.DocumentAsync(ws, "SCO-0001", "alpha in scope");
        await h.DocumentAsync(ws, "SCO-0002", "alpha beta in scope");
        await h.DocumentAsync(ws, "SCO-0003", "alpha out of scope");

        await using var factory = Factory(h, new InMemoryAuditEventWriter());
        using var client = factory.CreateClient();
        using var saved = await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches", qc,
            new JsonObject { ["name"] = "In scope", ["query"] = "scope AND NOT out" }, null);
        var savedSearchId = (await JsonAsync(saved)).GetProperty("savedSearchId").GetGuid();

        // Changes committed but not yet searchable: the report says the index was not current.
        await h.Search.Db.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.workspace_search_generation (workspace_id, value) VALUES (@ws, 1000)
            ON CONFLICT (workspace_id) DO UPDATE SET value = 1000
            """, ("ws", ws));

        var report = await CreateAndRunAsync(client, factory, ws, $"/api/v1/workspaces/{ws}/search-term-reports", qc, new JsonObject
        {
            ["name"] = "Scoped",
            ["termsCsv"] = "Name,Expression\r\nAlpha,alpha\r\nBeta,beta\r\n",
            ["scope"] = new JsonObject { ["kind"] = "savedSearch", ["id"] = savedSearchId.ToString() },
        }, "s1");

        report.GetProperty("scope").GetProperty("name").GetString().Should().Be("In scope");
        report.GetProperty("indexCurrent").GetBoolean().Should().BeFalse();
        Counts(report).Should().Equal(("Alpha", 2L, 2L, 1L, 1L), ("Beta", 1L, 1L, 0L, 0L));
        Totals(report).Should().Equal(2L, 2L, 2L, 0L);

        await (await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/search-term-reports", qc, new JsonObject
        {
            ["name"] = "Bad",
            ["terms"] = new JsonArray(),
            ["scope"] = new JsonObject { ["kind"] = "workspace" },
        }, "s2")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/search-term-reports", qc, new JsonObject
        {
            ["name"] = "Missing",
            ["terms"] = new JsonArray(new JsonObject { ["name"] = "A", ["expression"] = "a" }),
            ["scope"] = new JsonObject { ["kind"] = "savedSearch", ["id"] = Guid.NewGuid().ToString() },
        }, "s3")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
    }

    private static WebApplicationFactory<Program> Factory(SnapshotHarness h, InMemoryAuditEventWriter audit) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", h.Search.Db.Core.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OpenSearch", h.Search.Options.Endpoint!.ToString());
            builder.UseSetting("OpenSearch:IndexPrefix", h.Search.Scope.Prefix);
            builder.UseSetting("OpenSearch:Placement:CacheTtl", "00:00:00");
            builder.UseSetting("OpenSearch:Search:FieldCatalogCacheTtl", "00:00:00");
            builder.UseSetting("Snapshots:BackgroundEnabled", "false");
            builder.UseSetting("SearchTermReports:BackgroundEnabled", "false");
            builder.UseSetting("SearchTermReports:DocumentsPerChunk", "3");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader>(h.Search.Db.Reader);
                services.AddSingleton<IAuditEventWriter>(audit);
            });
        });

    private static async Task<SearchTermReportStatus?> RunAsync(WebApplicationFactory<Program> factory, Guid ws, Guid reportId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SearchTermReportRunner>().RunToEndAsync(ws, reportId, cancellationToken: Ct);
    }

    private static async Task<JsonElement> CreateAndRunAsync(
        HttpClient client, WebApplicationFactory<Program> factory, Guid ws, string url, Guid user, JsonObject body, string key)
    {
        using var created = await SendAsync(client, HttpMethod.Post, url, user, body, key);
        var id = (await JsonAsync(created, HttpStatusCode.Accepted)).GetProperty("reportId").GetGuid();
        (await RunAsync(factory, ws, id)).Should().Be(SearchTermReportStatus.Completed);
        return await GetAsync(client, $"{url}/{id}", user);
    }

    private static List<(string, long, long, long, long)> Counts(JsonElement report) =>
        [.. report.GetProperty("terms").EnumerateArray().Select(t => (
            t.GetProperty("name").GetString()!,
            N(t, "documentsWithHits"), N(t, "documentsWithHitsIncludingFamily"), N(t, "uniqueHits"), N(t, "uniqueHitsIncludingFamily")))];

    private static List<long> Totals(JsonElement report)
    {
        var t = report.GetProperty("totals");
        return [N(t, "documentsInScope"), N(t, "documentsWithHits"), N(t, "documentsWithHitsIncludingFamily"), N(t, "documentsWithoutHits")];
    }

    private static long N(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? -1 : e.GetProperty(name).GetInt64();

    private static async Task<JsonElement> GetAsync(HttpClient client, string url, Guid user)
    {
        using var response = await SendAsync(client, HttpMethod.Get, url, user, null);
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode? expected = null)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        if (expected is { } status)
        {
            response.StatusCode.Should().Be(status, text);
        }
        else
        {
            response.IsSuccessStatusCode.Should().BeTrue(text);
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, Guid user, JsonNode? body, string? key = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }
}
