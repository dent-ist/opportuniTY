using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Opportunity.Hosting.Workers;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Exports;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Messaging;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Search;
using Opportunity.Storage;
using Opportunity.Testing.OpenSearch;
using Opportunity.Testing.RabbitMq;

namespace Opportunity.IntegrationTests.Scale;

/// <summary>
/// The system the nightly scale-regression run (E18-T06) measures, in the developer-regression topology (§29) on one
/// machine: PostgreSQL, OpenSearch and RabbitMQ containers, and ONE real worker host composed exactly like
/// <c>Opportunity.Worker.All</c> (<see cref="OpportunityWorkerHost"/>, the dispatcher with its outbox/job relays and the
/// search watermark ticker, the import, indexing, bulk-coding and export worker types; production defaults except the
/// dispatcher's workspace refresh). Work flows through RabbitMQ as in production. The "API side" makes the same calls
/// the API does (import start, bulk-coding submission, export creation, searches through the real search service).
/// </summary>
internal sealed class ScaleWorld : IAsyncDisposable
{
    public const string WorkerTypesEnabled = "dispatcher,import,indexing,bulk-coding,export";

    private readonly string _temp;
    private WebApplication? _worker;
    private RabbitMqVirtualHost? _vhost;

    private ScaleWorld(SearchHarness search, ImportHarness import, BulkCodingHarness bulk, ExportHarness exports, string temp)
    {
        Search = search;
        Import = import;
        Bulk = bulk;
        Exports = exports;
        _temp = temp;
    }

    public SearchHarness Search { get; }

    public ImportHarness Import { get; }

    public BulkCodingHarness Bulk { get; }

    public ExportHarness Exports { get; }

    public CoreSchemaDatabase Core => Search.Db.Core;

    /// <summary>The effective worker-host settings (recorded in the run's configuration; no secrets).</summary>
    public IReadOnlyDictionary<string, string> WorkerSettings { get; private set; } = new Dictionary<string, string>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ScaleWorld> CreateAsync(
        OpenSearchFixture openSearch, MigrationPostgresFixture postgres, RabbitMqFixture rabbit, string volumeShare)
    {
        var temp = Directory.CreateTempSubdirectory("opp-scale-world-").FullName;
        var search = await SearchHarness.CreateAsync(openSearch, postgres);
        var import = ImportHarness.Over(search.Db.Core, volumeRoot: volumeShare);
        var world = new ScaleWorld(search, import, BulkCodingHarness.Over(search.Db), ExportHarness.Over(import), temp);
        try
        {
            await world.StartWorkerAsync(openSearch, rabbit, volumeShare);
            return world;
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    private async Task StartWorkerAsync(OpenSearchFixture openSearch, RabbitMqFixture rabbit, string volumeShare)
    {
        _vhost = await rabbit.CreateVirtualHostAsync(Ct);
        Directory.CreateDirectory(Path.Combine(_temp, "export"));

        // Topology once, up front (as the fault world does), so the first consumers never race the declaration.
        await using (var _ = await MessagingHarness.StartAsync(_vhost.AmqpUri, _vhost.AmqpUri))
        {
        }

        var sealer = await CoreSchemaDatabase.CreateLoginAsync(Core.ConnectionString, "opportunity_audit_sealer");
        var secrets = new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = Core.AppConnectionString,
            ["ConnectionStrings:AuditSealer"] = sealer,
            ["ConnectionStrings:RabbitMq"] = _vhost.AmqpUri.ToString(),
        };
        var settings = new Dictionary<string, string?>
        {
            ["Workers:Enabled"] = WorkerTypesEnabled,
            ["ConnectionStrings:OpenSearch"] = openSearch.BaseAddress.ToString(),
            ["OpenSearch:IndexPrefix"] = Search.Scope.Prefix,
            ["ObjectStorage:Provider"] = "FileSystem",
            ["ObjectStorage:FileSystem:RootPath"] = Import.StoreRoot,
            ["Import:VolumeShareRoot"] = volumeShare,
            ["KeyManagement:Local:KeyDirectory"] = Path.Combine(_temp, "keys"),
            ["Export:TempDirectory"] = Path.Combine(_temp, "export"),
            // New workspaces are picked up within a second instead of ten (the run creates its workspace just before it).
            ["Dispatcher:WorkspaceRefreshInterval"] = "00:00:01",
        };
        WorkerSettings = settings
            .Where(s => s.Key is "Workers:Enabled" or "ObjectStorage:Provider" or "Dispatcher:WorkspaceRefreshInterval")
            .ToDictionary(s => "worker." + s.Key, s => s.Value!, StringComparer.Ordinal);

        var builder = OpportunityWorkerHost.CreateBuilder([], WorkerTypes.AllKeyword);
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection([.. settings, .. secrets]);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var storage = new ObjectStorageOptions();
        builder.Configuration.GetSection(ObjectStorageOptions.SectionName).Bind(storage);
        builder.Services.AddObjectStorage(storage);
        _worker = OpportunityWorkerHost.Build(builder.AddWorkerModules());
        await _worker.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.StopAsync(CancellationToken.None);
            await _worker.DisposeAsync();
        }

        await Exports.DisposeAsync();
        await Bulk.DisposeAsync();
        await Search.DisposeAsync();
        if (_vhost is not null)
        {
            await _vhost.DisposeAsync();
        }

        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }
}
