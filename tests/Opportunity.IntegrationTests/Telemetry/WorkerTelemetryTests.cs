using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Opportunity.Application.Telemetry;
using Opportunity.Hosting.Workers;

namespace Opportunity.IntegrationTests.Telemetry;

public sealed class WorkerTelemetryTests : IAsyncLifetime
{
    private const string MessageType = "indexing.indexChunkTask";
    private const string Queue = "opportunity.indexing.l3-bulk";

    private readonly TelemetryCapture _capture = new();
    private WebApplication _app = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = OpportunityWorkerHost.CreateBuilder(
            ["--Telemetry:Enabled=true", "--Workers:HeartbeatInterval=00:00:01"], WorkerTypes.Indexing);
        builder.WebHost.UseTestServer();
        _capture.AddTo(builder.Services);
        _app = OpportunityWorkerHost.Build(builder.AddWorkerModules());
        await _app.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Consumer_span_continues_the_producer_trace_through_envelope_headers()
    {
        var correlation = new MessageCorrelation("corr-worker-1", WorkspaceId: Guid.NewGuid(), JobId: Guid.NewGuid(), MessageId: Guid.NewGuid());
        var headers = new Dictionary<string, string>();
        Activity? producer;
        using (producer = MessageTracePropagation.StartPublish(MessageType, Queue, correlation, headers))
        {
            producer.Should().NotBeNull();
        }

        // The envelope crosses the broker as data: only the headers map carries the context.
        var received = new Dictionary<string, string>(headers);
        using (var processing = _app.Services.GetRequiredService<WorkerTelemetry>()
                   .BeginProcessing(WorkerTypes.Indexing, MessageType, Queue, correlation, received))
        {
            processing.Activity.Should().NotBeNull();
        }

        var consumer = await _capture.WaitForSpanAsync(s => s.TraceId == producer!.TraceId && s.Kind == ActivityKind.Consumer, Ct);
        consumer.ParentSpanId.Should().Be(producer!.SpanId);
        consumer.DisplayName.Should().Be($"process {Queue}");
        consumer.GetTagItem(TelemetryAttributes.WorkerType).Should().Be(WorkerTypes.Indexing);
        consumer.GetTagItem(TelemetryAttributes.MessagingDestinationName).Should().Be(Queue);
        consumer.GetTagItem(TelemetryAttributes.MessageType).Should().Be(MessageType);
        consumer.GetTagItem(TelemetryAttributes.CorrelationId).Should().Be("corr-worker-1");
        consumer.GetTagItem(TelemetryAttributes.WorkspaceId).Should().Be(correlation.WorkspaceId.ToString());
        consumer.GetTagItem(TelemetryAttributes.JobId).Should().Be(correlation.JobId.ToString());
    }

    [Fact]
    public async Task Logs_written_while_processing_carry_trace_job_workspace_and_correlation()
    {
        var correlation = new MessageCorrelation("corr-worker-2", CausationId: Guid.NewGuid().ToString(), WorkspaceId: Guid.NewGuid(), JobId: Guid.NewGuid());
        var logger = _app.Services.GetRequiredService<ILogger<WorkerTelemetryTests>>();
        ActivityTraceId trace;

        using (var processing = _app.Services.GetRequiredService<WorkerTelemetry>()
                   .BeginProcessing(WorkerTypes.Indexing, MessageType, Queue, correlation, headers: null))
        {
            trace = processing.Activity!.TraceId;
            TestLog.ChunkApplied(logger, 500);
        }

        await _capture.WaitForSpanAsync(s => s.TraceId == trace, Ct);
        var log = _capture.Logs.Should().ContainSingle(l => l.TraceId == trace).Subject;
        log.Scope(LogScopeKeys.CorrelationId).Should().Be("corr-worker-2");
        log.Scope(LogScopeKeys.CausationId).Should().Be(correlation.CausationId);
        log.Scope(LogScopeKeys.WorkspaceId).Should().Be(correlation.WorkspaceId.ToString());
        log.Scope(LogScopeKeys.JobId).Should().Be(correlation.JobId.ToString());
    }

    [Fact]
    public async Task Worker_exports_processing_and_heartbeat_metrics()
    {
        var telemetry = _app.Services.GetRequiredService<WorkerTelemetry>();
        using (telemetry.BeginProcessing(WorkerTypes.Indexing, MessageType, Queue, new MessageCorrelation("corr-worker-3"), headers: null))
        {
        }

        using (var failed = telemetry.BeginProcessing(WorkerTypes.Indexing, MessageType, Queue, new MessageCorrelation("corr-worker-4"), headers: null))
        {
            failed.Fail("TimeoutException");
        }

        var status = _app.Services.GetRequiredService<WorkerStatus>();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (status.LastHeartbeat(WorkerTypes.Indexing) is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        _app.Services.GetRequiredService<MeterProvider>().ForceFlush().Should().BeTrue();

        var metrics = _capture.Metrics.Snapshot();
        metrics.Select(m => m.Name).Should().Contain([
            "messaging.process.duration",
            "messaging.client.consumed.messages",
            "opportunity.worker.heartbeat.age",
            "opportunity.worker.last_consumed.age",
        ]);
        var consumed = metrics.Last(m => m.Name == "messaging.client.consumed.messages");
        var points = consumed.MetricPoints.Select(p => (Tags: ToDictionary(p.Tags), Value: p.GetSumLong())).ToList();
        points.Should().Contain(p => (string?)p.Tags[TelemetryAttributes.WorkerType] == WorkerTypes.Indexing && !p.Tags.ContainsKey(TelemetryAttributes.ErrorType));
        points.Should().Contain(p => (string?)p.Tags.GetValueOrDefault(TelemetryAttributes.ErrorType) == "TimeoutException");
        metrics.Last(m => m.Name == "messaging.process.duration").Unit.Should().Be("s");

        var resource = _app.Services.GetRequiredService<TracerProvider>().GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);
        resource["service.name"].Should().Be("opportunity-integrationtests", "service.name derives from the host's application name");
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlyTagCollection tags)
    {
        var result = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            result[tag.Key] = tag.Value;
        }

        return result;
    }
}
