using Microsoft.Extensions.Logging;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Opportunity.Messaging;

internal enum ConnectionPurpose
{
    Publish,
    Consume,
}

/// <summary>
/// The process's broker connections: one for publishing and one for consuming, so broker flow control on publishers
/// cannot stall acknowledgements. Opened lazily; the first open retries with backoff until it succeeds or is
/// cancelled. Afterwards the client's automatic recovery reconnects and restores channels and consumers.
/// </summary>
public sealed partial class RabbitMqConnections : IAsyncDisposable, IDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnections> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ConnectionPurpose, IConnection> _connections = [];
    private bool _disposed;

    public RabbitMqConnections(RabbitMqOptions options, ILogger<RabbitMqConnections> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _options = options;
        _logger = logger;
    }

    internal async Task<IConnection> GetAsync(ConnectionPurpose purpose, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connections.TryGetValue(purpose, out var existing))
            {
                return existing;
            }

            var connection = await ConnectWithRetryAsync(purpose, cancellationToken).ConfigureAwait(false);
            _connections[purpose] = connection;
            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>For containers disposed synchronously (e.g. <c>using var host</c>).</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var connection in _connections.Values)
            {
                try
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or IOException)
                {
                    // Closing a connection that is already down.
                }

                await connection.DisposeAsync().ConfigureAwait(false);
            }

            _connections.Clear();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private async Task<IConnection> ConnectWithRetryAsync(ConnectionPurpose purpose, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(_options.ConnectionString!),
            ClientProvidedName = $"{_options.ClientName}:{purpose.ToString().ToLowerInvariant()}",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = _options.NetworkRecoveryInterval,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
        };

        var delay = TimeSpan.FromMilliseconds(500);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
                connection.ConnectionShutdownAsync += (_, args) =>
                {
                    LogConnectionLost(_logger, factory.ClientProvidedName, args.ReplyText);
                    return Task.CompletedTask;
                };
                connection.RecoverySucceededAsync += (_, _) =>
                {
                    LogConnectionRecovered(_logger, factory.ClientProvidedName);
                    return Task.CompletedTask;
                };
                connection.ConnectionRecoveryErrorAsync += (_, args) =>
                {
                    LogRecoveryFailed(_logger, factory.ClientProvidedName, args.Exception);
                    return Task.CompletedTask;
                };
                return connection;
            }
            catch (BrokerUnreachableException ex)
            {
                LogConnectFailed(_logger, factory.ClientProvidedName, attempt, delay, ex);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxConnectRetryDelay.Ticks));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ connection {Name} attempt {Attempt} failed; retrying in {Delay}")]
    private static partial void LogConnectFailed(ILogger logger, string? name, int attempt, TimeSpan delay, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ connection {Name} lost: {Reason}")]
    private static partial void LogConnectionLost(ILogger logger, string? name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ connection {Name} recovered")]
    private static partial void LogConnectionRecovered(ILogger logger, string? name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ connection {Name} recovery attempt failed")]
    private static partial void LogRecoveryFailed(ILogger logger, string? name, Exception exception);
}
