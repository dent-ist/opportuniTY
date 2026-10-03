using Microsoft.Extensions.Logging;

using Opportunity.Application.Bootstrap;
using Opportunity.Application.Messaging;

using RabbitMQ.Client;

namespace Opportunity.Messaging;

/// <summary>
/// Migrator step declaring <see cref="RabbitMqTopology"/> (exchanges, quorum work queues per lane and worker type,
/// retry tiers, DLX/DLQs, parking queues). Idempotent; fails if an existing queue was declared with other arguments.
/// </summary>
/// <param name="queues">Work queues to declare; <see cref="WorkQueues.All"/> when null.</param>
public sealed partial class RabbitMqTopologyBootstrapStep(
    RabbitMqConnections connections,
    RabbitMqOptions options,
    ILogger<RabbitMqTopologyBootstrapStep> logger,
    IReadOnlyList<WorkQueue>? queues) : IInfrastructureBootstrapStep
{
    /// <summary>Upper bound for reaching the broker; the migrator then fails instead of waiting forever.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);

    public string Name => "rabbitmq-topology";

    public int Order => 200;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var topology = RabbitMqTopology.Build(options, queues);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(ConnectTimeout);
        var connection = await connections.GetAsync(ConnectionPurpose.Publish, connectTimeout.Token).ConfigureAwait(false);

        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            await topology.DeclareAsync(channel, cancellationToken).ConfigureAwait(false);
            await channel.CloseAsync(cancellationToken).ConfigureAwait(false);
        }

        LogDeclared(logger, topology.Exchanges.Count, topology.Queues.Count, topology.Bindings.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ topology declared: {Exchanges} exchanges, {Queues} queues, {Bindings} bindings")]
    private static partial void LogDeclared(ILogger logger, int exchanges, int queues, int bindings);
}
