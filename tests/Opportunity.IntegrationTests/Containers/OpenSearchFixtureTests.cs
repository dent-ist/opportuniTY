using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Containers;

[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class OpenSearchFixtureTests(OpenSearchFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Indexes_and_searches_a_document_under_the_test_prefix()
    {
        await using var scope = fixture.CreateIndexScope();
        using var http = new HttpClient { BaseAddress = fixture.BaseAddress };
        var index = scope.IndexName("docs");

        using (var put = await http.PutAsJsonAsync($"{index}/_doc/1?refresh=true", new { title = "privileged memo" }, Ct))
        {
            put.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var search = await http.PostAsJsonAsync($"{index}/_search", new { query = new { match = new { title = "memo" } } }, Ct);
        search.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await search.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Index_scopes_are_unique_and_disposal_deletes_their_indices()
    {
        using var http = new HttpClient { BaseAddress = fixture.BaseAddress };
        string prefix;
        await using (var scope = fixture.CreateIndexScope())
        {
            await using var other = fixture.CreateIndexScope();
            other.Prefix.Should().NotBe(scope.Prefix);
            prefix = scope.Prefix;

            using var created = await http.PutAsync(new Uri(scope.IndexName("a"), UriKind.Relative), null, Ct);
            created.EnsureSuccessStatusCode();
            (await CountIndicesAsync(http, prefix)).Should().Be(1);
        }

        (await CountIndicesAsync(http, prefix)).Should().Be(0);
    }

    [Fact]
    public async Task Latency_toxic_slows_requests_and_lifting_it_restores_speed()
    {
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        using var http = new HttpClient { BaseAddress = OpenSearchFixture.BaseAddressVia(proxy) };
        await GetHealthAsync(http);

        await using (await proxy.AddLatencyAsync(Timing.InjectedLatency, cancellationToken: Ct))
        {
            (await Timing.MeasureAsync(() => GetHealthAsync(http))).Should().BeGreaterThanOrEqualTo(Timing.MinimumObservedLatency);
        }

        (await Timing.MeasureAsync(() => GetHealthAsync(http))).Should().BeLessThan(Timing.MinimumObservedLatency);
    }

    [Fact]
    public async Task Cut_link_fails_requests_and_restoring_it_recovers()
    {
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        using var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero })
        {
            BaseAddress = OpenSearchFixture.BaseAddressVia(proxy),
            Timeout = TimeSpan.FromSeconds(5),
        };
        await GetHealthAsync(http);

        await proxy.CutAsync(Ct);
        var request = () => GetHealthAsync(http);
        await request.Should().ThrowAsync<HttpRequestException>();

        await proxy.RestoreAsync(Ct);
        await GetHealthAsync(http);
    }

    [Fact]
    public async Task Timeout_toxic_makes_requests_time_out()
    {
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        using var http = new HttpClient { BaseAddress = OpenSearchFixture.BaseAddressVia(proxy), Timeout = TimeSpan.FromSeconds(1) };

        await using (await proxy.AddTimeoutAsync(TimeSpan.Zero, cancellationToken: Ct))
        {
            var request = () => GetHealthAsync(http);
            (await request.Should().ThrowAsync<TaskCanceledException>()).Which.InnerException.Should().BeOfType<TimeoutException>();
        }

        using var recovered = new HttpClient { BaseAddress = OpenSearchFixture.BaseAddressVia(proxy) };
        await GetHealthAsync(recovered);
    }

    [Fact]
    public async Task Reset_peer_toxic_fails_requests()
    {
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        using var http = new HttpClient { BaseAddress = OpenSearchFixture.BaseAddressVia(proxy), Timeout = TimeSpan.FromSeconds(5) };

        await using (await proxy.AddResetPeerAsync(cancellationToken: Ct))
        {
            var request = () => GetHealthAsync(http);
            await request.Should().ThrowAsync<HttpRequestException>();
        }

        await GetHealthAsync(http);
    }

    private static async Task GetHealthAsync(HttpClient http)
    {
        using var response = await http.GetAsync(new Uri("_cluster/health", UriKind.Relative), Ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<int> CountIndicesAsync(HttpClient http, string prefix)
    {
        using var response = await http.GetAsync(new Uri($"_cat/indices/{prefix}*?format=json&expand_wildcards=all", UriKind.Relative), Ct);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetArrayLength();
    }
}
