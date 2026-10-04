using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;

using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Search;
using Opportunity.Testing.OpenSearch;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// E10-T01 with the interactive index worker (E07-T03 harness): a save through the API commits its search work, the
/// worker indexes it, the coding becomes searchable through the search API, and the coding GET reports the projected
/// version so the panel can clear "saved · indexing". Coding → searchable latency is measured and reported.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class CodingSearchabilityTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_saved_coding_becomes_searchable_and_the_coding_read_reports_when_it_is_indexed()
    {
        const int Samples = 15;
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        var core = h.Db.Core;
        var w = await WorkspaceAsync(core);
        var reviewer = await MemberAsync(core, w.Id, WorkspaceRole.Reviewer);
        var documents = new List<Guid>();
        for (var i = 0; i <= Samples; i++)
        {
            documents.Add(await DocumentAsync(core, w.Id));
        }

        await using var factory = Factory(core.AppConnectionString, b =>
        {
            b.UseSetting("ConnectionStrings:OpenSearch", openSearch.BaseAddress.ToString());
            b.UseSetting("OpenSearch:IndexPrefix", h.Scope.Prefix);
            b.UseSetting("OpenSearch:Placement:CacheTtl", "00:00:00");
            b.UseSetting("OpenSearch:Search:FieldCatalogCacheTtl", "00:00:00");
        });
        using var client = factory.CreateClient();
        var queryName = await QueryNameAsync(client, w.Id, reviewer, "Responsive");

        // The first document places the workspace (index created on demand by the worker).
        var first = documents[0];
        using (var save = await PutAsync(client, CodingUrl(w.Id, first), reviewer, Set(w.Responsive, true), "\"1\""))
        {
            save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync(Ct));
            var saved = await JsonAsync(save);
            saved.GetProperty("indexingState").GetString().Should().Be("pending");
            saved.GetProperty("projectedVersion").GetInt64().Should().Be(1);
        }

        await IndexAsync(h, w.Id);
        await WaitSearchableAsync(h, w.Id, first, 2);

        using (var read = await GetAsync(client, CodingUrl(w.Id, first), reviewer))
        {
            var coding = await JsonAsync(read);
            coding.GetProperty("indexingState").GetString().Should().Be("indexed");
            coding.GetProperty("projectedVersion").GetInt64().Should().Be(2);
            coding.GetProperty("documentVersion").GetInt64().Should().Be(2);
        }

        (await SearchAsync(client, w.Id, reviewer, $"{queryName}:true")).Should().Contain(first);

        // Save → searchable, measured from before the PUT to the first search hit at the new version (refresh 1 s). The
        // dispatcher and worker run in-process here; the p95 ≤ 1 s target is asserted only under
        // OPPORTUNITY_STRICT_LATENCY=1 (Q-44), elsewhere the samples are recorded.
        var samples = new List<TimeSpan>();
        foreach (var doc in documents.Skip(1))
        {
            var clock = Stopwatch.StartNew();
            using (var save = await PutAsync(client, CodingUrl(w.Id, doc), reviewer, Set(w.Responsive, true), "\"1\""))
            {
                save.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            await IndexAsync(h, w.Id);
            await WaitSearchableAsync(h, w.Id, doc, 2);
            samples.Add(clock.Elapsed);
        }

        var hits = await SearchAsync(client, w.Id, reviewer, $"{queryName}:true");
        hits.Should().BeEquivalentTo(documents);

        var sorted = samples.Order().ToList();
        var p50 = sorted[(sorted.Count - 1) / 2];
        var p95 = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * 0.95) - 1)];
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"API save → searchable over {samples.Count} saves: p50 {p50.TotalMilliseconds:F0} ms, p95 {p95.TotalMilliseconds:F0} ms, " +
            $"max {sorted[^1].TotalMilliseconds:F0} ms");
        if (Environment.GetEnvironmentVariable("OPPORTUNITY_STRICT_LATENCY") == "1")
        {
            p95.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1), "code → visible in search p95 ≤ 1 s (E10-T01)");
        }
    }

    /// <summary>One dispatcher pass and the worker handling every published message.</summary>
    private static async Task IndexAsync(IndexWorkerHarness h, Guid workspaceId)
    {
        foreach (var message in await h.DispatchAsync(workspaceId))
        {
            ((SearchOutboxMessage)message.Payload).DocumentVersion.Should().BeGreaterThan(1);
            await h.HandleAsync(message);
        }
    }

    private static async Task WaitSearchableAsync(IndexWorkerHarness h, Guid workspaceId, Guid doc, long version)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await h.SearchableAtAsync(workspaceId, doc, version))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "document {0} must become searchable at version {1}", doc, version);
            await Task.Delay(10, Ct);
        }
    }

    private static async Task<string> QueryNameAsync(HttpClient client, Guid workspaceId, Guid user, string displayName)
    {
        using var response = await GetAsync(client, $"/api/v1/workspaces/{workspaceId}/fields", user);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await JsonAsync(response)).GetProperty("items").EnumerateArray()
            .Single(f => f.GetProperty("displayName").GetString() == displayName).GetProperty("queryName").GetString()!;
    }

    private static async Task<List<Guid>> SearchAsync(HttpClient client, Guid workspaceId, Guid user, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workspaces/{workspaceId}/searches", UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query, pageSize = 100 }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return [.. (await JsonAsync(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("documentId").GetGuid())];
    }
}
