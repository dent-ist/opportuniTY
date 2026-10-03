using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.IntegrationTests.Containers;
using Opportunity.Messaging;
using Opportunity.Testing.RabbitMq;

namespace Opportunity.IntegrationTests.Messaging;

[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class RabbitMqQueueMetricsTests(RabbitMqFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Queue_depth_and_consumer_gauges_report_the_broker_state()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(WorkQueues.Import), Ct);
        await harness.PublishRawAsync(WorkQueues.IndexBulk, "not an envelope"u8.ToArray());
        await harness.SubscribeAsync(WorkQueues.IndexBulk, (_, _) => Task.CompletedTask);

        var options = MessagingHarness.FastOptions(vhost.AmqpUri);
        options.QueueMetricsInterval = TimeSpan.FromMilliseconds(200);
        var services = new ServiceCollection().AddLogging().AddMetrics();
        services.AddSingleton(sp => new OpportunityMetrics(sp.GetRequiredService<IMeterFactory>()));
        services.AddRabbitMqMessaging(options).AddRabbitMqQueueMetrics();
        await using var provider = services.BuildServiceProvider();

        var depth = new ConcurrentDictionary<(string Queue, string State), long>();
        var consumers = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == OpportunityTelemetry.MeterName
                    && instrument.Name is "opportunity.queue.depth" or "opportunity.queue.consumers")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var queue = (string)Tag(tags, TelemetryAttributes.MessagingDestinationName)!;
            if (instrument.Name == "opportunity.queue.depth")
            {
                depth[(queue, (string)Tag(tags, TelemetryAttributes.QueueState)!)] = value;
            }
            else
            {
                consumers[queue] = value;
            }
        });
        listener.Start();

        var sampler = provider.GetServices<IHostedService>().Single();
        await sampler.StartAsync(Ct);
        try
        {
            var deadline = DateTime.UtcNow + MessagingHarness.Patience;
            while (!(depth.GetValueOrDefault(("import.chunks", "ready")) == 1
                     && depth.GetValueOrDefault(("index.bulk.dlq", "dlq")) == 1
                     && consumers.GetValueOrDefault("index.bulk") == 1))
            {
                DateTime.UtcNow.Should().BeBefore(deadline, "the gauges should reflect the broker within the patience window");
                await Task.Delay(100, Ct);
                listener.RecordObservableInstruments();
            }

            depth.Should().ContainKey(("index.parking", "parking"));
            consumers.GetValueOrDefault("index.security").Should().Be(0);
        }
        finally
        {
            await sampler.StopAsync(Ct);
        }
    }

    private static object? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == key)
            {
                return tag.Value;
            }
        }

        return null;
    }
}
