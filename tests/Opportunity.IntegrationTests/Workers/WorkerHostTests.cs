using System.Diagnostics.Metrics;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Opportunity.Hosting.Workers;

namespace Opportunity.IntegrationTests.Workers;

public sealed class WorkerHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Builds a worker exactly as a worker host's Program does, on a test server.</summary>
    private static Worker BuildWorker(string hostDefault, params string[] args)
    {
        var builder = OpportunityWorkerHost.CreateBuilder(args, hostDefault);
        builder.WebHost.UseTestServer();
        return new Worker(OpportunityWorkerHost.Build(builder.AddWorkerModules()));
    }

    /// <summary>Stops before disposing, as the real host does on SIGTERM, so background services end cleanly.</summary>
    private sealed class Worker(WebApplication app) : IAsyncDisposable
    {
        public WebApplication App => app;

        public IServiceProvider Services => app.Services;

        public Task StartAsync(CancellationToken cancellationToken) => app.StartAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    private static int ModuleServiceCount(Worker app) =>
        app.Services.GetServices<IHostedService>().Count(s => s.GetType().Name == "WorkerHeartbeatService");

    [Fact]
    public async Task Combined_worker_hosts_every_worker_type()
    {
        await using var app = BuildWorker(WorkerTypes.AllKeyword);
        await app.StartAsync(Ct);

        app.Services.GetRequiredService<WorkerStatus>().EnabledTypes.Should().Equal(WorkerTypes.All);
        ModuleServiceCount(app).Should().Be(WorkerTypes.All.Count);
    }

    [Fact]
    public async Task Same_combined_build_runs_a_single_type_by_configuration_only()
    {
        await using var app = BuildWorker(WorkerTypes.AllKeyword, "--Workers:Enabled=indexing");
        await app.StartAsync(Ct);

        app.Services.GetRequiredService<WorkerStatus>().EnabledTypes.Should().Equal(WorkerTypes.Indexing);
        ModuleServiceCount(app).Should().Be(1);
    }

    [Fact]
    public async Task Single_type_host_can_be_widened_to_a_subset()
    {
        await using var app = BuildWorker(WorkerTypes.Import, "--Workers:Enabled=import, bulk-coding");
        await app.StartAsync(Ct);

        app.Services.GetRequiredService<WorkerStatus>().EnabledTypes
            .Should().Equal(WorkerTypes.Import, WorkerTypes.BulkCoding);
    }

    [Theory]
    [InlineData("--Workers:Enabled=imports")]
    [InlineData("--Workers:Enabled=all,import")]
    [InlineData("--Workers:Enabled= , ")]
    [InlineData("--Workers:HeartbeatInterval=00:00:00")]
    public async Task Invalid_worker_configuration_fails_fast_at_start(string arg)
    {
        await using var app = BuildWorker(WorkerTypes.Import, arg);

        var start = () => app.StartAsync(Ct);

        await start.Should().ThrowAsync<OptionsValidationException>();
    }

    [Fact]
    public async Task Worker_exposes_live_and_ready_probes()
    {
        await using var app = BuildWorker(WorkerTypes.AllKeyword);
        await app.StartAsync(Ct);
        using var client = app.App.GetTestClient();

        (await client.GetAsync("/health/live", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        HttpStatusCode ready;
        do
        {
            ready = (await client.GetAsync("/health/ready", Ct)).StatusCode;
        }
        while (ready != HttpStatusCode.OK && DateTime.UtcNow < deadline);

        ready.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Worker_reports_heartbeat_age_per_type()
    {
        await using var app = BuildWorker(WorkerTypes.Dispatcher);
        await app.StartAsync(Ct);
        var status = app.Services.GetRequiredService<WorkerStatus>();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (status.LastHeartbeat(WorkerTypes.Dispatcher) is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        var measured = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkerStatus.MeterName && instrument.Name == "opportunity.worker.heartbeat.age")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "worker.type")
                {
                    measured.Add((string)tag.Value!);
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();

        measured.Should().Contain(WorkerTypes.Dispatcher);
    }
}
