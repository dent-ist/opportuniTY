using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Messaging;

namespace Opportunity.Messaging;

/// <summary>One <see cref="IMessageHandler{TPayload}"/> bound to a work queue (see <see cref="MessagingServiceCollectionExtensions.AddMessageHandler{TPayload, THandler}"/>).</summary>
public sealed record MessageHandlerRegistration(
    WorkQueue Queue,
    Type PayloadType,
    Func<IServiceProvider, object, ReceivedMessage, CancellationToken, Task> Invoke);

/// <summary>
/// Subscribes every queue that has registered handlers and dispatches each message, by payload type, to its handler
/// resolved from a fresh DI scope. A payload type without a handler on that queue is a routing error and is
/// dead-lettered.
/// </summary>
internal sealed partial class MessageHandlerHostedService(
    IEnumerable<MessageHandlerRegistration> registrations,
    IMessageConsumer consumer,
    IServiceScopeFactory scopes,
    ILogger<MessageHandlerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriptions = new List<IAsyncDisposable>();
        try
        {
            foreach (var group in registrations.GroupBy(r => r.Queue))
            {
                var handlers = group.ToDictionary(r => r.PayloadType);
                subscriptions.Add(await consumer.SubscribeAsync(group.Key, (message, ct) => DispatchAsync(handlers, message, ct), stoppingToken)
                    .ConfigureAwait(false));
            }

            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
            }

            LogStopped(logger, subscriptions.Count);
        }
    }

    private async Task DispatchAsync(
        Dictionary<Type, MessageHandlerRegistration> handlers, ReceivedMessage message, CancellationToken cancellationToken)
    {
        if (!handlers.TryGetValue(message.Payload.GetType(), out var registration))
        {
            throw new PermanentMessageException(
                $"No handler for '{message.Envelope.MessageType}' on queue '{message.Queue.Name}'.");
        }

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await registration.Invoke(scope.ServiceProvider, message.Payload, message, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped {Count} message subscriptions")]
    private static partial void LogStopped(ILogger logger, int count);
}
