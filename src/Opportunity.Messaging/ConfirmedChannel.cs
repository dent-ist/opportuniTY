using Opportunity.Application.Messaging;

using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Opportunity.Messaging;

/// <summary>
/// A publisher-confirm channel on the publish connection of one credential (null: the default user). <see cref="PublishAsync"/> returns only once the broker has
/// confirmed the message (routed with <c>mandatory</c>, persisted by the quorum queues); every other outcome is a
/// <see cref="MessagePublishException"/>. Concurrent publishes are pipelined on the one channel.
/// </summary>
internal sealed class ConfirmedChannel(RabbitMqConnections connections, RabbitMqOptions options, string? credential = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync(
        string exchange,
        string routingKey,
        BasicProperties properties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.PublishTimeout);
        try
        {
            var channel = await GetChannelAsync(timeout.Token).ConfigureAwait(false);
            await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, body, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (PublishException ex) when (ex.IsReturn)
        {
            throw new MessagePublishException($"The broker could not route the message to '{exchange}' / '{routingKey}'.", ex);
        }
        catch (PublishException ex)
        {
            throw new MessagePublishException($"The broker rejected (nacked) the message to '{exchange}' / '{routingKey}'.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MessagePublishException($"No broker confirm within {options.PublishTimeout}.", ex);
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or BrokerUnreachableException
                                       or IOException or ObjectDisposedException)
        {
            throw new MessagePublishException("The broker connection is unavailable; the message was not confirmed.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            try
            {
                await _channel.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or IOException)
            {
                // Already down.
            }

            await _channel.DisposeAsync().ConfigureAwait(false);
            _channel = null;
        }

        _gate.Dispose();
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        var channel = _channel;
        if (channel is { IsOpen: true })
        {
            return channel;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            var connection = await connections.GetAsync(ConnectionPurpose.Publish, credential, cancellationToken).ConfigureAwait(false);
            if (!connection.IsOpen)
            {
                // Automatic recovery is reconnecting; fail fast so the caller keeps the work unpublished.
                throw new MessagePublishException("The broker connection is down and recovering; the message was not published.");
            }

            if (_channel is not null)
            {
                // Closed by a channel-level error (recovery restores only connection-level failures).
                await _channel.DisposeAsync().ConfigureAwait(false);
            }

            _channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }
}
