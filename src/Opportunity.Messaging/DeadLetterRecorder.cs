using System.Diagnostics.Metrics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Opportunity.Messaging;

/// <summary>Settings of the dead-letter recorder (<c>Messaging:DeadLetterRecorder</c>).</summary>
public sealed class DeadLetterRecorderOptions
{
    public const string SectionName = "Messaging:DeadLetterRecorder";

    /// <summary>Records are deleted this long after they were recorded (the broker's DLQ copies expire after 14 days).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often expired records are deleted.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Bodies are stored up to this many bytes (the full size is kept alongside).</summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>Unacknowledged deliveries the recorder holds; it records them one at a time.</summary>
    public ushort Prefetch { get; set; } = 32;

    /// <summary>Backoff ceiling while PostgreSQL or the broker is unavailable.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public static DeadLetterRecorderOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new DeadLetterRecorderOptions();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }
}

/// <summary>
/// The dead-letter recorder (ADR-010 §7.3): drains <see cref="RabbitMqTopology.DeadLetterRecordQueue"/> — a copy of
/// every message routed to a <c>*.dlq</c> or <c>*.parking</c> queue — into PostgreSQL through
/// <see cref="IDeadLetterStore"/>, and deletes records past their retention. A delivery is acknowledged only after its
/// record is committed (idempotent by message id, so a redelivery is harmless); while PostgreSQL is unavailable the
/// delivery stays unacknowledged and is retried with backoff, and on shutdown it returns to the queue. The diagnostic
/// queues themselves are untouched, and nothing is ever re-published. Runs in the dispatcher.
/// </summary>
public sealed partial class DeadLetterRecorder : BackgroundService
{
    private readonly RabbitMqConnections _connections;
    private readonly IServiceProvider _services;
    private readonly DeadLetterRecorderOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DeadLetterRecorder> _logger;
    private readonly Counter<long>? _recorded;

    public DeadLetterRecorder(
        RabbitMqConnections connections,
        IServiceProvider services,
        DeadLetterRecorderOptions options,
        TimeProvider time,
        ILogger<DeadLetterRecorder> logger)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _recorded = services.GetService<OpportunityMetrics>()?.Counter(OpportunityMetricCatalog.DeadLetteredMessages);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Resolved lazily, like the dispatcher's other loops: without PostgreSQL the recorder reports itself disabled.
        IDeadLetterStore store;
        try
        {
            store = _services.GetRequiredService<IDeadLetterStore>();
        }
        catch (InvalidOperationException ex)
        {
            LogDisabled(_logger, ex.Message);
            return;
        }

        await Task.WhenAll(ConsumeAsync(store, stoppingToken), CleanUpAsync(store, stoppingToken)).ConfigureAwait(false);
    }

    private async Task ConsumeAsync(IDeadLetterStore store, CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            IChannel? channel = null;
            try
            {
                var connection = await _connections.GetAsync(ConnectionPurpose.Consume, stoppingToken).ConfigureAwait(false);
                channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(
                        publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false, consumerDispatchConcurrency: 1),
                    stoppingToken).ConfigureAwait(false);
                await channel.BasicQosAsync(0, Math.Max((ushort)1, _options.Prefetch), global: false, stoppingToken).ConfigureAwait(false);

                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) =>
                {
                    // A lost connection is recovered with its channels and consumers (topology recovery); only a channel
                    // closed on an open connection (e.g. the queue was deleted) needs a new one.
                    if (connection.IsOpen)
                    {
                        closed.TrySetResult();
                    }

                    return Task.CompletedTask;
                };
                var consumer = new AsyncEventingBasicConsumer(channel);
                var current = channel;
                consumer.ReceivedAsync += (_, delivery) => RecordAsync(store, current, delivery, stoppingToken);
                await channel.BasicConsumeAsync(
                    RabbitMqTopology.DeadLetterRecordQueue, autoAck: false, consumer, stoppingToken).ConfigureAwait(false);
                LogConsuming(_logger, RabbitMqTopology.DeadLetterRecordQueue);
                failures = 0;

                // Runs until the channel closes (broker restart, queue deleted) or the host stops.
                await closed.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or BrokerUnreachableException
                or IOException or TimeoutException)
            {
                LogConsumeFailed(_logger, ex);
            }
            finally
            {
                if (channel is not null)
                {
                    await CloseAsync(channel).ConfigureAwait(false);
                }
            }

            if (!await DelayAsync(++failures, stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task RecordAsync(IDeadLetterStore store, IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        var message = DeadLetterMessageReader.Read(
            delivery.Exchange, delivery.RoutingKey, delivery.BasicProperties.MessageId, delivery.BasicProperties.Headers, delivery.Body,
            Math.Max(0, _options.MaxBodyBytes));
        var attempt = 0;
        while (true)
        {
            try
            {
                var outcome = await store.RecordAsync(message, stoppingToken).ConfigureAwait(false);
                if (outcome != DeadLetterWriteOutcome.Duplicate)
                {
                    _recorded?.Add(1,
                        new KeyValuePair<string, object?>(TelemetryAttributes.MessagingDestinationName, message.Queue),
                        new KeyValuePair<string, object?>(TelemetryAttributes.ErrorType, message.DeathReason));
                }

                LogRecorded(_logger, message.MessageId, message.Queue, message.DeathReason, outcome);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown: leave the delivery unsettled; the channel close returns it to the queue.
                return;
            }
#pragma warning disable CA1031 // A failed write is retried; the delivery is never acknowledged without its record.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogRecordFailed(_logger, message.MessageId, message.Queue, ex);
                if (!await DelayAsync(++attempt, stoppingToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }

        try
        {
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or IOException or ObjectDisposedException)
        {
            // The channel died first: the broker redelivers and the record is already there (idempotent).
            LogAckFailed(_logger, message.MessageId, ex);
        }
    }

    private async Task CleanUpAsync(IDeadLetterStore store, CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.CleanupInterval, _time);
            do
            {
                try
                {
                    var deleted = await store.DeleteRecordedBeforeAsync(_time.GetUtcNow() - _options.Retention, stoppingToken)
                        .ConfigureAwait(false);
                    if (deleted > 0)
                    {
                        LogExpired(_logger, deleted, _options.Retention);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // Retention retries on the next tick.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogCleanupFailed(_logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>Backoff 0.5 s × 2ⁿ up to <see cref="DeadLetterRecorderOptions.MaxRetryDelay"/>; false when stopping.</summary>
    private async Task<bool> DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var seconds = Math.Min(_options.MaxRetryDelay.TotalSeconds, Math.Pow(2, Math.Min(attempt, 10) - 1) * 0.5);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1, seconds)), _time, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task CloseAsync(IChannel channel)
    {
        try
        {
            if (channel.IsOpen)
            {
                await channel.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or IOException)
        {
            // Already closed.
        }

        await channel.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dead-letter recorder disabled: {Reason}")]
    private static partial void LogDisabled(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording dead-lettered messages from {Queue}")]
    private static partial void LogConsuming(ILogger logger, string queue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dead-letter recorder lost its channel; reconnecting")]
    private static partial void LogConsumeFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded dead-lettered message {MessageId} from {Queue} ({Reason}): {Outcome}")]
    private static partial void LogRecorded(ILogger logger, string messageId, string queue, string reason, DeadLetterWriteOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record dead-lettered message {MessageId} from {Queue}; retrying")]
    private static partial void LogRecordFailed(ILogger logger, string messageId, string queue, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not acknowledge recorded dead-letter {MessageId}; the broker will redeliver it")]
    private static partial void LogAckFailed(ILogger logger, string messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} dead-letter record(s) older than {Retention}")]
    private static partial void LogExpired(ILogger logger, int count, TimeSpan retention);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting expired dead-letter records failed; retrying on the next tick")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
