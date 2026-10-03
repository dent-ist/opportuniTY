using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Bootstrap;
using Opportunity.Application.Search;
using Opportunity.Application.Search.Indexing;
using Opportunity.Contracts.Search;
using Opportunity.Data.Search;
using Opportunity.Data.Security;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search;
using Opportunity.Search.Indexing;
using Opportunity.Search.Querying;
using Opportunity.Security.Authorization;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// The real search service over a migrated PostgreSQL (security state, placements, search sessions, all as the
/// RLS-bound app login) and the shared OpenSearch container under a per-test index prefix. Every OpenSearch request is
/// recorded so tests can inspect the DSL that was actually sent.
/// </summary>
internal sealed class SearchHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly HttpClient _http;

    private SearchHarness(
        ServiceProvider services, AuthorizationDatabase db, OpenSearchIndexScope scope, RecordingHandler recorder, InMemoryAuditEventWriter audit, HttpClient http)
    {
        _services = services;
        Audit = audit;
        Db = db;
        Scope = scope;
        Recorder = recorder;
        _http = http;
    }

    public AuthorizationDatabase Db { get; }

    public OpenSearchIndexScope Scope { get; }

    public RecordingHandler Recorder { get; }

    public InMemoryAuditEventWriter Audit { get; }

    public IIndexManager Indexes => _services.GetRequiredService<IIndexManager>();

    public OpenSearchOptions Options => _services.GetRequiredService<OpenSearchOptions>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<SearchHarness> CreateAsync(
        OpenSearchFixture openSearch, MigrationPostgresFixture postgres, Action<OpenSearchOptions>? configure = null)
    {
        var db = await AuthorizationDatabase.CreateAsync(postgres);
        var scope = openSearch.CreateIndexScope();
        var options = new OpenSearchOptions
        {
            Endpoint = openSearch.BaseAddress,
            IndexPrefix = scope.Prefix,
            Placement = { CacheTtl = TimeSpan.Zero },
        };
        configure?.Invoke(options);

        var recorder = new RecordingHandler();
        var audit = new InMemoryAuditEventWriter();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IAuditEventWriter>(audit);
        services.AddSingleton(options);
        services.AddSingleton(new OpenSearchConnection(options, recorder));
        services.AddSingleton<ISecurityStateReader>(db.Reader);
        services.AddSingleton<IIndexPlacementStore>(new IndexPlacementStore(db.Core.AppDataSource));
        services.AddSingleton<ISearchSessionStore>(new SearchSessionStore(db.Core.AppDataSource));
        services.AddOpenSearchIndexTemplateBootstrap(options);
        services.AddOpportunityAuthorization();
        services.AddOpenSearchSearchService();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var harness = new SearchHarness(provider, db, scope, recorder, audit, new HttpClient { BaseAddress = openSearch.BaseAddress });
        await provider.GetServices<IInfrastructureBootstrapStep>().Single().RunAsync(Ct);
        return harness;
    }

    /// <summary>A workspace (shared placement unless asked otherwise) with the built-in restriction classes.</summary>
    public async Task<Guid> WorkspaceAsync(bool dedicated = false)
    {
        var ws = await Db.Core.CreateWorkspaceAsync();
        await _services.GetRequiredService<IWorkspaceSearchPlacement>().PlaceAsync(ws, new WorkspacePlacementRequest(dedicated), Ct);
        return ws;
    }

    public async Task<Guid> MemberAsync(Guid workspaceId, Core.Security.WorkspaceRole role = Core.Security.WorkspaceRole.Reviewer, Guid? user = null)
    {
        var id = await Db.CreateUserAsync(user);
        await Db.AssignAsync(workspaceId, role, id);
        return id;
    }

    /// <summary>A PostgreSQL document plus its projection row (written like the projection builder, refresh on).</summary>
    public async Task<Guid> DocumentAsync(
        Guid workspaceId, string controlNumber, string text, string[]? classes = null, Guid[]? walls = null, bool projectSecurity = true,
        Guid? projectedWorkspace = null, Guid? documentId = null)
    {
        var id = documentId ?? await Db.DocumentAsync(workspaceId, classes ?? []);
        var tags = new JsonArray();
        if (projectSecurity)
        {
            foreach (var c in classes ?? [])
            {
                tags.Add(SecurityTags.Class(c));
            }

            foreach (var w in walls ?? [])
            {
                tags.Add(SecurityTags.Wall(w));
            }
        }

        await ProjectAsync(workspaceId, new JsonObject
        {
            ["workspaceId"] = (projectedWorkspace ?? workspaceId).ToString("D"),
            ["documentId"] = id.ToString("D"),
            ["controlNumber"] = controlNumber,
            ["fileName"] = controlNumber + ".msg",
            ["fileType"] = "Email",
            ["documentDate"] = "2024-05-0" + ((controlNumber.Length % 9) + 1) + "T10:00:00Z",
            ["securityTags"] = tags,
            ["text"] = text,
        }, id);
        return id;
    }

    /// <summary>Writes a raw projection row to the workspace's write targets.</summary>
    public async Task ProjectAsync(Guid workspaceId, JsonObject row, Guid documentId)
    {
        var placement = await Indexes.ResolveAsync(workspaceId, IndexPurpose.Write, Ct);
        foreach (var target in placement.WriteTargets)
        {
            var routing = target.Routing is { } r ? "&routing=" + r : string.Empty;
            using var response = await _http.PutAsJsonAsync($"{target.Index}/_doc/{documentId:D}?refresh=true{routing}", row, Ct);
            response.EnsureSuccessStatusCode();
        }
    }

    public Task<SearchOutcome> SearchAsync(Guid workspaceId, Guid userId, string query, int pageSize = 50, Guid? session = null,
        IReadOnlyList<SearchSortKey>? sort = null, bool? countExact = null, IReadOnlyList<string>? facets = null, string[]? groups = null) =>
        InScopeAsync(s => s.SearchAsync(Caller(workspaceId, userId, session, groups),
            new SearchRequest(query, sort ?? [new SearchSortKey("controlNumber")], pageSize, countExact, Facets: facets), Ct));

    public Task<SearchOutcome> PageAsync(Guid workspaceId, Guid userId, string? searchId, SearchPageRequest page, Guid? session = null) =>
        InScopeAsync(s => s.GetPageAsync(Caller(workspaceId, userId, session), searchId ?? string.Empty, page, Ct));

    public static SearchCaller Caller(Guid workspaceId, Guid userId, Guid? session = null, string[]? groups = null) =>
        new(new SecurityPrincipal { UserId = userId, DisplayName = "user " + userId.ToString("N")[..6], Groups = groups ?? [] }, workspaceId, session);

    /// <summary>Deletes every point-in-time reader on the cluster, as if they had all expired.</summary>
    public async Task ExpireReadersAsync()
    {
        using var response = await _http.DeleteAsync("_search/point_in_time/_all", Ct);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        using (var _ = await _http.DeleteAsync($"_index_template/{Scope.Prefix}-projection-g1", CancellationToken.None))
        {
        }

        await Scope.DisposeAsync();
        _http.Dispose();
        await _services.DisposeAsync();
        await Db.DisposeAsync();
    }

    private async Task<SearchOutcome> InScopeAsync(Func<ISearchService, Task<SearchOutcome>> call)
    {
        // One DI scope per call, like one HTTP request: the PDP caches principal state per scope only.
        await using var scope = _services.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<ISearchService>());
    }

    /// <summary>Records every request sent to OpenSearch (method, path and body).</summary>
    internal sealed class RecordingHandler() : DelegatingHandler(new SocketsHttpHandler())
    {
        public ConcurrentQueue<(HttpMethod Method, string PathAndQuery, string? Body)> Requests { get; } = new();

        public IReadOnlyList<JsonObject> SearchBodies =>
        [
            .. Requests.Where(r => r.Method == HttpMethod.Post && r.PathAndQuery.StartsWith("/_search", StringComparison.Ordinal)
                    && !r.PathAndQuery.Contains("point_in_time", StringComparison.Ordinal))
                .Select(r => JsonNode.Parse(r.Body!)!.AsObject()),
        ];

        public void Clear() => Requests.Clear();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue((request.Method, request.RequestUri!.PathAndQuery, body));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
