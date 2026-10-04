using System.Globalization;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Search;
using Opportunity.Contracts.Search;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Search;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Security;

/// <summary>
/// E05-T05 randomized property variant: three workspaces, two sharing an index (routing) and one with a dedicated index,
/// hold documents over one shared vocabulary plus words, control numbers and file extensions of their own. Over 1,000
/// generated queries (terms, Boolean combinations, phrases, wildcards, field queries naming the other workspaces'
/// control numbers and marker words, the empty query), each with facets, return 0 hits, 0 counted documents and 0
/// aggregation buckets from another workspace. The seed is printed so a failure replays exactly.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class RandomizedIsolationTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private const int DocumentsPerWorkspace = 12;
    private const int QueriesPerWorkspace = 340;

    private static readonly string[] Vocabulary =
    [
        "merger", "contract", "pricing", "supply", "board", "minutes", "invoice", "termination", "audit", "memo",
        "settlement", "privileged", "draft", "shipment", "forecast", "budget", "counsel", "agreement", "notice", "review",
    ];

    private static readonly string[] FileTypes = ["Email", "Document", "Spreadsheet"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Over_a_thousand_generated_queries_return_no_hits_counts_or_buckets_from_another_workspace()
    {
        var seed = Environment.GetEnvironmentVariable("OPPORTUNITY_ISOLATION_SEED") is { } s
            ? int.Parse(s, CultureInfo.InvariantCulture)
            : Random.Shared.Next();
        TestContext.Current.TestOutputHelper?.WriteLine($"OPPORTUNITY_ISOLATION_SEED={seed}");
        var random = new Random(seed);

        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var workspaces = new List<Tenant>();
        foreach (var (name, dedicated) in new[] { ("a", false), ("b", false), ("c", true) })
        {
            var ws = await h.WorkspaceAsync(dedicated);
            await h.Db.Core.Fields.InitializeWorkspaceAsync(ws, Ct);
            var user = await h.MemberAsync(ws);
            var tenant = new Tenant(name, ws, user, $"{name}{name}ext", $"marker{name}zz", []);
            for (var i = 1; i <= DocumentsPerWorkspace; i++)
            {
                var id = await h.Db.DocumentAsync(ws);
                var controlNumber = $"{name.ToUpperInvariant()}-{i:0000}";
                var words = Enumerable.Range(0, 6).Select(_ => Vocabulary[random.Next(Vocabulary.Length)]).Append(tenant.Marker);
                await h.ProjectAsync(ws, new JsonObject
                {
                    ["workspaceId"] = ws.ToString("D"),
                    ["documentId"] = id.ToString("D"),
                    ["controlNumber"] = controlNumber,
                    ["fileName"] = controlNumber + "." + tenant.Extension,
                    ["fileExtension"] = tenant.Extension,
                    ["fileType"] = FileTypes[i % FileTypes.Length],
                    ["securityTags"] = new JsonArray(),
                    ["text"] = string.Join(' ', words),
                }, id);
                tenant.Documents[id] = controlNumber;
            }

            workspaces.Add(tenant);
        }

        var queries = 0;
        var hits = 0L;
        foreach (var tenant in workspaces)
        {
            var others = workspaces.Where(t => t != tenant).ToList();
            for (var q = 0; q < QueriesPerWorkspace; q++)
            {
                var query = Generate(random, tenant, others);
                var outcome = await h.SearchAsync(tenant.WorkspaceId, tenant.User, query, pageSize: 50, countExact: true,
                    facets: ["fileExtension", "fileType"]);
                outcome.Status.Should().Be(SearchStatus.Ok, "seed {0}, {1}: {2} {3}", seed, tenant.Name, query,
                    string.Join("; ", outcome.QueryErrors.Select(e => e.Code + " " + e.Message)));
                var page = outcome.Page!;
                var label = $"seed {seed}, workspace {tenant.Name}, query «{query}»";
                page.Items.Select(i => i.DocumentId).Should().OnlyContain(id => tenant.Documents.ContainsKey(id), label + ": hits");
                page.Items.Select(i => i.ControlNumber).Should().OnlyContain(cn => cn.StartsWith(tenant.Name.ToUpperInvariant() + "-", StringComparison.Ordinal), label);
                page.Total.Value.Should().Be(page.Items.Count, label + ": the count includes no other workspace's documents");
                var extensions = page.Facets.Single(f => f.Field == "fileExtension").Buckets;
                extensions.Should().OnlyContain(b => b.Value == tenant.Extension, label + ": extension buckets");
                extensions.Sum(b => b.Count).Should().Be(page.Total.Value, label);
                page.Facets.Single(f => f.Field == "fileType").Buckets.Sum(b => b.Count).Should().Be(page.Total.Value, label + ": type buckets");
                queries++;
                hits += page.Items.Count;

                if (queries % 100 == 0)
                {
                    await h.ExpireReadersAsync(); // keep point-in-time readers under the cluster's open-context limit
                }
            }
        }

        queries.Should().BeGreaterThanOrEqualTo(1_000);
        hits.Should().BeGreaterThan(queries, "the generated queries mostly match documents of the searching workspace");
        TestContext.Current.TestOutputHelper?.WriteLine($"{queries} queries, {hits} hits, 0 from another workspace.");
    }

    private static string Generate(Random random, Tenant tenant, List<Tenant> others)
    {
        string Word() => Vocabulary[random.Next(Vocabulary.Length)];
        Tenant Other() => others[random.Next(others.Count)];
        string ForeignControlNumber()
        {
            var other = Other();
            return other.Documents.Values.ElementAt(random.Next(other.Documents.Count));
        }

        return random.Next(12) switch
        {
            0 => Word(),
            1 => $"{Word()} AND {Word()}",
            2 => $"{Word()} OR {Word()} OR {Other().Marker}",
            3 => $"{Word()} NOT {Word()}",
            4 => $"\"{Word()} {Word()}\"",
            5 => Word()[..3] + "*",
            6 => Other().Marker,
            7 => $"{tenant.Marker} OR {Other().Marker}",
            8 => $"controlNumber:\"{ForeignControlNumber()}\" OR {Word()}",
            9 => $"extension:{Other().Extension} OR extension:{tenant.Extension}",
            10 => string.Empty,
            _ => $"({Word()} OR {Other().Marker}) AND NOT {Word()}",
        };
    }

    private sealed record Tenant(string Name, Guid WorkspaceId, Guid User, string Extension, string Marker, Dictionary<Guid, string> Documents);
}
