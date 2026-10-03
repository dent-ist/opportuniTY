using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Bootstrap;
using Opportunity.Application.Search.Indexing;
using Opportunity.Search;
using Opportunity.Search.Indexing;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// Index management wired against the shared OpenSearch container under a per-test index prefix, with an in-memory
/// placement store. Also carries a minimal stand-in for the search service path (filter + routing, ADR-006 R7/R8) so
/// the same suite can run against shared and dedicated placements.
/// </summary>
internal sealed class IndexHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly bool _ownsScope;

    private IndexHarness(ServiceProvider services, OpenSearchIndexScope scope, bool ownsScope, IIndexPlacementStore store, HttpClient http)
    {
        _services = services;
        _ownsScope = ownsScope;
        Scope = scope;
        Store = store;
        Http = http;
    }

    public OpenSearchIndexScope Scope { get; }

    public IIndexPlacementStore Store { get; }

    public HttpClient Http { get; }

    public IIndexManager Manager => _services.GetRequiredService<IIndexManager>();

    public IWorkspaceSearchPlacement Placements => _services.GetRequiredService<IWorkspaceSearchPlacement>();

    public IInfrastructureBootstrapStep TemplateStep => _services.GetServices<IInfrastructureBootstrapStep>().Single();

    public ProjectionMappings Mappings => _services.GetRequiredService<ProjectionMappings>();

    public OpenSearchOptions Options => _services.GetRequiredService<OpenSearchOptions>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<IndexHarness> CreateAsync(
        OpenSearchFixture fixture,
        Action<OpenSearchOptions>? configure = null,
        ProjectionMappings? mappings = null,
        bool installTemplates = true,
        IndexHarness? sharing = null)
    {
        var scope = sharing?.Scope ?? fixture.CreateIndexScope();
        var options = new OpenSearchOptions
        {
            Endpoint = fixture.BaseAddress,
            IndexPrefix = scope.Prefix,
            Placement = { CacheTtl = TimeSpan.Zero },
        };
        configure?.Invoke(options);

        var store = sharing?.Store ?? new InMemoryIndexPlacementStore();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(store);
        SearchServiceCollectionExtensions.AddOpenSearchCore(services, options, mappings);
        services.AddOpenSearchIndexTemplateBootstrap(options);
        services.AddOpenSearchIndexManagement(options);
        var provider = services.BuildServiceProvider();

        var harness = new IndexHarness(provider, scope, sharing is null, store, new HttpClient { BaseAddress = fixture.BaseAddress });
        if (installTemplates)
        {
            await harness.TemplateStep.RunAsync(Ct);
        }

        return harness;
    }

    /// <summary>Writes one projection document to every write target of the placement, as a projection writer would.</summary>
    public async Task IndexAsync(Placement placement, string documentId, string text, object? extra = null)
    {
        foreach (var target in placement.WriteTargets)
        {
            var body = extra ?? new { workspaceId = placement.WorkspaceFilterValue, documentId, controlNumber = documentId, text };
            using var response = await Http.PutAsJsonAsync(DocumentPath(target, documentId), body, Ct);
            await EnsureSuccessAsync(response);
        }
    }

    public async Task<HttpResponseMessage> RawIndexAsync(string index, string documentId, object body, string? routing) =>
        await Http.PutAsJsonAsync(DocumentPath(new IndexTarget(index, routing), documentId), body, Ct);

    /// <summary>
    /// The search path stand-in: reads the alias with the injected workspace filter and routing (ADR-006 R7/R8); the
    /// user query only ever lands in <c>must</c>.
    /// </summary>
    public async Task<string[]> SearchAsync(Guid workspaceId, string text)
    {
        var placement = await Manager.ResolveAsync(workspaceId, IndexPurpose.Read, Ct);
        var routing = placement.Read.Routing is { } r ? $"?routing={r}" : string.Empty;
        var query = new
        {
            query = new
            {
                @bool = new
                {
                    filter = new object[] { new { term = new { workspaceId = placement.WorkspaceFilterValue } } },
                    must = new object[] { new { match = new { text } } },
                },
            },
            sort = new object[] { new { documentId = "asc" } },
            size = 100,
        };
        using var response = await Http.PostAsJsonAsync($"{placement.Read.Index}/_search{routing}", query, Ct);
        await EnsureSuccessAsync(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. body.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray()
            .Select(h => h.GetProperty("_source").GetProperty("documentId").GetString()!)];
    }

    /// <summary>Documents of <paramref name="workspaceId"/> in a physical index (no placement involved).</summary>
    public async Task<long> CountAsync(string index, Guid workspaceId)
    {
        using var response = await Http.PostAsJsonAsync(
            $"{index}/_count", new { query = new { term = new { workspaceId = workspaceId.ToString("D") } } }, Ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return 0;
        }

        await EnsureSuccessAsync(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("count").GetInt64();
    }

    public async Task<bool> IndexExistsAsync(string index)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, index);
        using var response = await Http.SendAsync(request, Ct);
        return response.StatusCode == HttpStatusCode.OK;
    }

    /// <summary>Physical indexes an alias points to.</summary>
    public async Task<string[]> AliasTargetsAsync(string alias)
    {
        using var response = await Http.GetAsync($"_alias/{alias}", Ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        await EnsureSuccessAsync(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
    }

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await Http.GetAsync(path, Ct);
        await EnsureSuccessAsync(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsScope)
        {
            // Templates live outside the index prefix cleanup; harnesses sharing this scope may add later generations.
            foreach (var generation in Enumerable.Range(1, 4))
            {
                using var _ = await Http.DeleteAsync($"_index_template/{Scope.Prefix}-projection-g{generation}", CancellationToken.None);
            }

            await Scope.DisposeAsync();
        }

        Http.Dispose();
        await _services.DisposeAsync();
    }

    private static string DocumentPath(IndexTarget target, string documentId) =>
        $"{target.Index}/_doc/{documentId}?refresh=true" + (target.Routing is { } r ? $"&routing={r}" : string.Empty);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync(Ct)}");
        }
    }
}
