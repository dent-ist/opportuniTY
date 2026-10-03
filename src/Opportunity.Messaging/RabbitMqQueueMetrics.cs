using System.Diagnostics.Metrics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;

using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Opportunity.Messaging;

/// <summary>
/// Samples work, dead-letter and parking queue depths and consumer counts with passive declares and reports them as
/// <c>opportunity.queue.depth</c> (states <c>ready</c>, <c>dlq</c>, <c>parking</c>) and <c>opportunity.queue.consumers</c>.
/// Unacknowledged counts are not visible over AMQP; they come from the broker's own Prometheus endpoint.
/// Run it in one component per installation (the dispatcher) so series are not duplicated.
/// </summary>
internal sealed partial class RabbitMqQueueMetrics : BackgroundService
{
    private readonly RabbitMqConnections _connections;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqQueueMetrics> _logger;
    private readonly IReadOnlyList<(string Queue, string State, bool Work)> _queues;
    private volatile IReadOnlyList<Measurement<long>> _depths = [];
    private volatile IReadOnlyList<Measurement<long>> _consumers = [];

    public RabbitMqQueueMetrics(
        RabbitMqConnections connections, RabbitMqOptions options, OpportunityMetrics metrics, ILogger<RabbitMqQueueMetrics> logger)
    {
        _connections = connections;
        _options = options;
        _logger = logger;
        _queues =
        [
            .. WorkQueues.All.Select(q => (q.Name, "ready", true)),
            .. WorkQueues.All.Select(q => (RabbitMqTopology.DeadLetterQueue(q), "dlq", false)),
            .. WorkQueues.All.Select(q => q.Area).Distinct(StringComparer.Ordinal).Select(a => (RabbitMqTopology.ParkingQueue(a), "parking", false)),
        ];
        metrics.Gauge(OpportunityMetricCatalog.QueueDepth, () => _depths);
        metrics.Gauge(OpportunityMetricCatalog.QueueConsumers, () => _consumers);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        try
        {
            using var timer = new PeriodicTimer(_options.QueueMetricsInterval);
            do
            {
                try
                {
                    if (channel is not { IsOpen: true })
                    {
                        if (channel is not null)
                        {
                            await channel.DisposeAsync().ConfigureAwait(false);
                        }

                        var connection = await _connections.GetAsync(ConnectionPurpose.Consume, stoppingToken).ConfigureAwait(false);
                        channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                    }

                    await SampleAsync(channel, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or IOException)
                {
                    LogSampleFailed(_logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task SampleAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var depths = new List<Measurement<long>>(_queues.Count);
        var consumers = new List<Measurement<long>>(WorkQueues.All.Count);
        foreach (var (queue, state, work) in _queues)
        {
            // A missing queue (topology not yet declared) closes the channel; the next tick reopens it.
            var ok = await channel.QueueDeclarePassiveAsync(queue, cancellationToken).ConfigureAwait(false);
            var destination = new KeyValuePair<string, object?>(TelemetryAttributes.MessagingDestinationName, queue);
            depths.Add(new Measurement<long>(ok.MessageCount, destination, new(TelemetryAttributes.QueueState, state)));
            if (work)
            {
                consumers.Add(new Measurement<long>(ok.ConsumerCount, destination));
            }
        }

        _depths = depths;
        _consumers = consumers;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sampling RabbitMQ queue metrics failed")]
    private static partial void LogSampleFailed(ILogger logger, Exception exception);
}
