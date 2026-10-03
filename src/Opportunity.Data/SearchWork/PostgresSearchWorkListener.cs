using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using Npgsql;

using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;

namespace Opportunity.Data.SearchWork;

public sealed class SearchWorkListenerOptions
{
    /// <summary>An idle subscription is checked with a round trip this often, so a dead connection is noticed.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan MinReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// <c>LISTEN search_outbox</c> on one dedicated session (ADR-001 §6.2). The payload <c>{workspaceId}|{lane}</c> written
/// by <see cref="SearchWorkSql.AddOutboxRowsAsync"/> becomes a <see cref="SearchWorkWakeUp"/>; anything else is ignored.
/// A broken session is reported and re-established with backoff.
/// </summary>
public sealed partial class PostgresSearchWorkListener(
    NpgsqlDataSource dataSource, SearchWorkListenerOptions options, ILogger<PostgresSearchWorkListener> logger) : ISearchWorkWakeUpListener
{
    public async Task ListenAsync(ISearchWorkWakeUpObserver observer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var delay = options.MinReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await WorkspaceTransaction.ListenAsync(dataSource, SearchWorkSql.NotifyChannel, cancellationToken)
                    .ConfigureAwait(false);
                connection.Notification += (_, args) =>
                {
                    if (TryParse(args.Payload, out var wakeUp))
                    {
                        observer.OnWakeUp(wakeUp);
                    }
                };

                observer.OnListening();
                delay = options.MinReconnectDelay;
                while (true)
                {
                    if (!await connection.WaitAsync(options.KeepAliveInterval, cancellationToken).ConfigureAwait(false))
                    {
                        await using var ping = new NpgsqlCommand("SELECT 1", connection) { CommandTimeout = 10 };
                        await ping.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or SocketException or IOException or TimeoutException or InvalidOperationException)
            {
                LogLost(logger, delay, ex);
                observer.OnLost(ex);
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.MaxReconnectDelay.Ticks));
        }
    }

    /// <summary>Parses <c>{workspaceId}|{lane}</c>.</summary>
    public static bool TryParse(string? payload, out SearchWorkWakeUp wakeUp)
    {
        wakeUp = default;
        var separator = payload?.IndexOf('|', StringComparison.Ordinal) ?? -1;
        if (separator <= 0
            || !Guid.TryParse(payload.AsSpan(0, separator), out var workspaceId)
            || !Enum.TryParse<MessageLane>(payload.AsSpan(separator + 1), ignoreCase: false, out var lane)
            || !Enum.IsDefined(lane))
        {
            return false;
        }

        wakeUp = new SearchWorkWakeUp(workspaceId, lane);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Search outbox LISTEN session lost; polling covers until it is re-established (next attempt in {Delay})")]
    private static partial void LogLost(ILogger logger, TimeSpan delay, Exception exception);
}
