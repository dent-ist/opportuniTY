using System.Globalization;

using Opportunity.Application.Messaging;

using RabbitMQ.Client;

namespace Opportunity.Messaging;

public sealed record ExchangeDefinition(string Name, string Type);

public sealed record QueueDefinition(string Name, IReadOnlyDictionary<string, object?> Arguments);

public sealed record BindingDefinition(
    string Exchange, string Queue, string RoutingKey, IReadOnlyDictionary<string, object?>? Arguments = null);

/// <summary>
/// The broker topology, declared from code by the migrator (<see cref="RabbitMqTopologyBootstrapStep"/>); runtime
/// components never declare anything, so their users need no <c>configure</c> permission (ADR-015 D9.5).
/// <code>
/// opportunity.work (direct) --{queue}--> {queue}                        quorum, x-delivery-limit, DLX {area}.dlx
/// {area}.retry (direct) --{tier}--> {area}.retry.{tier}                 quorum, x-message-ttl = tier
///     --expired--> {area}.retry.return (headers: opportunity-queue = {queue}) --> {queue}
/// {area}.dlx (direct) --{queue}--> {queue}.dlq                          max-length, TTL 14 d (diagnostics only)
///                     --parking--> {area}.parking                       unknown type / unsupported major
/// </code>
/// All queues are quorum queues. Lanes (ADR-001 §5.3) are separate work queues, never message priorities.
/// </summary>
public sealed class RabbitMqTopology
{
    public const string WorkExchange = "opportunity.work";
    public const string ParkingRoutingKey = "parking";

    private RabbitMqTopology(
        IReadOnlyList<ExchangeDefinition> exchanges,
        IReadOnlyList<QueueDefinition> queues,
        IReadOnlyList<BindingDefinition> bindings)
    {
        Exchanges = exchanges;
        Queues = queues;
        Bindings = bindings;
    }

    public IReadOnlyList<ExchangeDefinition> Exchanges { get; }

    public IReadOnlyList<QueueDefinition> Queues { get; }

    public IReadOnlyList<BindingDefinition> Bindings { get; }

    public static string RetryExchange(string area) => $"{area}.retry";

    public static string RetryReturnExchange(string area) => $"{area}.retry.return";

    public static string RetryQueue(string area, TimeSpan delay) => $"{area}.retry.{RetryTier(delay)}";

    public static string DeadLetterExchange(string area) => $"{area}.dlx";

    public static string DeadLetterQueue(WorkQueue queue) => $"{Check(queue).Name}.dlq";

    public static string ParkingQueue(string area) => $"{area}.parking";

    /// <summary>Routing key of a retry tier: <c>5s</c>, <c>2m</c>, or <c>250ms</c> for sub-second (test) delays.</summary>
    public static string RetryTier(TimeSpan delay) =>
        delay.TotalMilliseconds % 60_000 == 0 ? string.Create(CultureInfo.InvariantCulture, $"{(long)delay.TotalMinutes}m")
        : delay.TotalMilliseconds % 1000 == 0 ? string.Create(CultureInfo.InvariantCulture, $"{(long)delay.TotalSeconds}s")
        : string.Create(CultureInfo.InvariantCulture, $"{(long)delay.TotalMilliseconds}ms");

    public static RabbitMqTopology Build(RabbitMqOptions options, IEnumerable<WorkQueue>? queues = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var workQueues = (queues ?? WorkQueues.All).Select(Check).ToList();
        var delays = options.RetryDelays();

        var exchanges = new List<ExchangeDefinition> { new(WorkExchange, ExchangeType.Direct) };
        var queueDefinitions = new List<QueueDefinition>();
        var bindings = new List<BindingDefinition>();

        foreach (var area in workQueues.Select(q => q.Area).Distinct(StringComparer.Ordinal))
        {
            exchanges.Add(new(RetryExchange(area), ExchangeType.Direct));
            exchanges.Add(new(RetryReturnExchange(area), ExchangeType.Headers));
            exchanges.Add(new(DeadLetterExchange(area), ExchangeType.Direct));

            foreach (var delay in delays)
            {
                var retryQueue = RetryQueue(area, delay);
                queueDefinitions.Add(new(retryQueue, Quorum(new()
                {
                    ["x-message-ttl"] = (long)delay.TotalMilliseconds,
                    ["x-dead-letter-exchange"] = RetryReturnExchange(area),
                    ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-overflow"] = "reject-publish",
                })));
                bindings.Add(new(RetryExchange(area), retryQueue, RetryTier(delay)));
            }

            queueDefinitions.Add(new(ParkingQueue(area), Quorum(new()
            {
                ["x-max-length"] = (long)options.ParkingMaxLength,
                ["x-overflow"] = "reject-publish",
            })));
            bindings.Add(new(DeadLetterExchange(area), ParkingQueue(area), ParkingRoutingKey));
        }

        foreach (var queue in workQueues)
        {
            queueDefinitions.Add(new(queue.Name, Quorum(new()
            {
                ["x-delivery-limit"] = (long)options.DeliveryLimit,
                ["x-dead-letter-exchange"] = DeadLetterExchange(queue.Area),
                ["x-dead-letter-routing-key"] = queue.Name,
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish",
            })));
            bindings.Add(new(WorkExchange, queue.Name, queue.Name));
            bindings.Add(new(
                RetryReturnExchange(queue.Area),
                queue.Name,
                string.Empty,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x-match"] = "all",
                    [TransportHeaders.Queue] = queue.Name,
                }));

            queueDefinitions.Add(new(DeadLetterQueue(queue), Quorum(new()
            {
                ["x-max-length"] = (long)options.DeadLetterMaxLength,
                ["x-overflow"] = "drop-head",
                ["x-message-ttl"] = (long)options.DeadLetterTtl.TotalMilliseconds,
            })));
            bindings.Add(new(DeadLetterExchange(queue.Area), DeadLetterQueue(queue), queue.Name));
        }

        return new RabbitMqTopology(exchanges, queueDefinitions, bindings);
    }

    /// <summary>Declares everything; idempotent while the definitions are unchanged.</summary>
    internal async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        foreach (var exchange in Exchanges)
        {
            await channel.ExchangeDeclareAsync(
                exchange.Name, exchange.Type, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (var queue in Queues)
        {
            await channel.QueueDeclareAsync(
                queue.Name,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>(queue.Arguments, StringComparer.Ordinal),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (var binding in Bindings)
        {
            await channel.QueueBindAsync(
                binding.Queue,
                binding.Exchange,
                binding.RoutingKey,
                binding.Arguments is null ? null : new Dictionary<string, object?>(binding.Arguments, StringComparer.Ordinal),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, object?> Quorum(Dictionary<string, object?> arguments)
    {
        arguments["x-queue-type"] = "quorum";
        return arguments;
    }

    private static WorkQueue Check(WorkQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (queue.Name.IndexOf('.', StringComparison.Ordinal) <= 0)
        {
            throw new ArgumentException($"Work queue '{queue.Name}' must be named '{{area}}.{{name}}'.", nameof(queue));
        }

        return queue;
    }
}

/// <summary>
/// RabbitMQ permission patterns (configure, write, read) for one component in one virtual host (ADR-015 D9.5):
/// the dispatcher may only publish to the work exchange; a worker may only consume its own area's queues and publish
/// to its own retry and dead-letter exchanges; only the migrator configures.
/// </summary>
public sealed record RabbitMqPermissions(string Configure, string Write, string Read)
{
    private const string Nothing = "^$";

    public static RabbitMqPermissions Migrator { get; } = new(".*", ".*", ".*");

    public static RabbitMqPermissions Dispatcher { get; } = new(Nothing, $"^{Escape(RabbitMqTopology.WorkExchange)}$", Nothing);

    /// <summary>Permissions of the worker consuming <paramref name="areas"/> (e.g. <c>index</c>).</summary>
    public static RabbitMqPermissions Worker(params string[] areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        var alternatives = string.Join('|', areas.Select(Escape));
        return new RabbitMqPermissions(
            Nothing,
            $@"^({alternatives})\.(retry|dlx)$",
            $@"^({alternatives})\.[^.]+$");
    }

    private static string Escape(string value) => System.Text.RegularExpressions.Regex.Escape(value);
}
