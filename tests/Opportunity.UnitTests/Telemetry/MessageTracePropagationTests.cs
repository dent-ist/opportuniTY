using System.Diagnostics;

using AwesomeAssertions;

using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;

namespace Opportunity.UnitTests.Telemetry;

/// <summary>ADR-017 R4: W3C trace context through the envelope headers, without a transport.</summary>
public sealed class MessageTracePropagationTests : IDisposable
{
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = source => source.Name == OpportunityTelemetry.ActivitySourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    public MessageTracePropagationTests() => ActivitySource.AddActivityListener(_listener);

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void Inject_then_extract_round_trips_the_w3c_context()
    {
        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.TraceStateString = "vendor=1";
        activity.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        var headers = new Dictionary<string, string>();

        MessageTracePropagation.Inject(activity, headers);
        var context = MessageTracePropagation.Extract(headers);

        headers[MessageHeaderNames.TraceParent].Should().Be($"00-{activity.TraceId}-{activity.SpanId}-01");
        context.TraceId.Should().Be(activity.TraceId);
        context.SpanId.Should().Be(activity.SpanId);
        context.TraceState.Should().Be("vendor=1");
        context.IsRemote.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-traceparent")]
    [InlineData("00-00000000000000000000000000000000-0000000000000000-01")]
    public void Missing_or_malformed_traceparent_yields_no_parent(string? traceParent)
    {
        var headers = new Dictionary<string, string>();
        if (traceParent is not null)
        {
            headers[MessageHeaderNames.TraceParent] = traceParent;
        }

        MessageTracePropagation.Extract(headers).Should().Be(default(ActivityContext));
    }

    [Fact]
    public void Inject_without_activity_removes_stale_context()
    {
        var headers = new Dictionary<string, string>
        {
            [MessageHeaderNames.TraceParent] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            [MessageHeaderNames.TraceState] = "a=b",
        };

        MessageTracePropagation.Inject(null, headers);

        headers.Should().BeEmpty();
    }

    [Fact]
    public void Process_span_is_a_child_of_the_publish_span_and_carries_correlation()
    {
        var correlation = new MessageCorrelation("corr-1", WorkspaceId: Guid.NewGuid(), JobId: Guid.NewGuid(), MessageId: Guid.NewGuid());
        var headers = new Dictionary<string, string>();

        using var publish = MessageTracePropagation.StartPublish("indexing.indexChunkTask", "q.l1", correlation, headers);
        publish!.Stop();
        using var process = MessageTracePropagation.StartProcess("indexing.indexChunkTask", "q.l1", correlation, headers);

        publish.Kind.Should().Be(ActivityKind.Producer);
        process!.Kind.Should().Be(ActivityKind.Consumer);
        process.TraceId.Should().Be(publish.TraceId);
        process.ParentSpanId.Should().Be(publish.SpanId);
        process.DisplayName.Should().Be("process q.l1");
        process.GetTagItem(TelemetryAttributes.CorrelationId).Should().Be("corr-1");
        process.GetTagItem(TelemetryAttributes.WorkspaceId).Should().Be(correlation.WorkspaceId.ToString());
        process.GetTagItem(TelemetryAttributes.JobId).Should().Be(correlation.JobId.ToString());
        process.GetTagItem(TelemetryAttributes.MessagingMessageId).Should().Be(correlation.MessageId.ToString());
    }

    [Fact]
    public void Caused_by_keeps_the_correlation_and_points_causation_at_the_handled_message()
    {
        var handled = Guid.NewGuid();
        var next = Guid.NewGuid();
        var correlation = new MessageCorrelation("corr-2", WorkspaceId: Guid.NewGuid(), JobId: Guid.NewGuid(), MessageId: handled);

        var caused = correlation.CausedBy(handled, next);

        caused.CorrelationId.Should().Be("corr-2");
        caused.CausationId.Should().Be(handled.ToString());
        caused.MessageId.Should().Be(next);
        caused.WorkspaceId.Should().Be(correlation.WorkspaceId);
        caused.ToLogScope().Select(kv => kv.Key).Should().Equal(
            LogScopeKeys.CorrelationId, LogScopeKeys.CausationId, LogScopeKeys.WorkspaceId, LogScopeKeys.JobId, LogScopeKeys.MessageId);
    }
}
