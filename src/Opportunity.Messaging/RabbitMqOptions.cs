using Microsoft.Extensions.Configuration;

using Opportunity.Application.Messaging;

namespace Opportunity.Messaging;

/// <summary>
/// RabbitMQ transport settings. The broker address comes from <c>ConnectionStrings:RabbitMq</c> (an AMQP URI, e.g.
/// <c>ConnectionStrings__RabbitMq</c> in the Compose profile); everything else from <c>Messaging:RabbitMq</c>.
/// Topology-affecting values (retry delays, delivery limit, DLQ limits) are declared by the migrator; changing them
/// for an existing installation needs the old queues removed first (RabbitMQ rejects re-declaring with other
/// arguments).
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "Messaging:RabbitMq";
    public const string ConnectionStringName = "RabbitMq";

    /// <summary>AMQP URI incl. credentials and virtual host. Each component uses its own user (ADR-015 D9.5).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Broker credentials per queue area (E05-T07, ADR-015 D9.5/D9.6), e.g. <c>AreaConnectionStrings:render</c>: a worker
    /// consumes the area's queues and publishes its retries and dead-letters with that user, whose permissions
    /// (<see cref="RabbitMqPermissions.ForArea"/>) cover only them. Areas without an entry use <see cref="ConnectionString"/>,
    /// which also publishes new work (the dispatcher's user). A combined worker holding several areas thus keeps each
    /// area's traffic on its own least-privilege connection.
    /// </summary>
    public Dictionary<string, string> AreaConnectionStrings { get; } = new(StringComparer.Ordinal);

    /// <summary>Optional HMAC envelope signing (E05-T07), <c>Messaging:RabbitMq:Signing</c>.</summary>
    public MessageSigningOptions Signing { get; } = new();

    /// <summary>Shown in the management UI as the connection name (suffixed with the connection's purpose).</summary>
    public string ClientName { get; set; } = "opportunity";

    /// <summary>A publish not confirmed within this time is reported as failed.</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay between automatic connection recovery attempts.</summary>
    public TimeSpan NetworkRecoveryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound of the backoff between attempts to open the initial connection.</summary>
    public TimeSpan MaxConnectRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Transport retries after the first failed delivery (ADR-010 §7: <c>MaxAttempts = 5</c>, so 4 retries). Used only
    /// when the work row cannot even be loaded; normal retry state is PostgreSQL.
    /// </summary>
    public int MaxTransportRetries { get; set; } = 4;

    /// <summary>First retry delay; retry n waits <c>base × 2^(n-1)</c>, capped at <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Quorum-queue <c>x-delivery-limit</c>: a message that crashes consumers this often is dead-lettered (ADR-010 §7.2).</summary>
    public int DeliveryLimit { get; set; } = 5;

    /// <summary>Each <c>*.dlq</c>: max length and TTL (ADR-010 §7.3); diagnostics only, never re-published.</summary>
    public int DeadLetterMaxLength { get; set; } = 100_000;

    public TimeSpan DeadLetterTtl { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Parking queues reject (and so never drop) beyond this length.</summary>
    public int ParkingMaxLength { get; set; } = 100_000;

    /// <summary>Per-queue prefetch overrides by queue name; defaults are <see cref="WorkQueue.Prefetch"/>.</summary>
    public Dictionary<string, ushort> Prefetch { get; } = new(StringComparer.Ordinal);

    /// <summary>How often queue depths and consumer counts are sampled for metrics.</summary>
    public TimeSpan QueueMetricsInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The credential key of <paramref name="area"/>: the area when it has its own user, else null (the default).</summary>
    public string? CredentialFor(string? area) =>
        area is not null && AreaConnectionStrings.TryGetValue(area, out var uri) && !string.IsNullOrWhiteSpace(uri) ? area : null;

    /// <summary>The AMQP URI of a credential key (<see cref="CredentialFor"/>).</summary>
    public string ConnectionStringFor(string? credential) =>
        credential is null ? ConnectionString! : AreaConnectionStrings[credential];

    public ushort PrefetchFor(WorkQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return Prefetch.TryGetValue(queue.Name, out var prefetch) && prefetch > 0 ? prefetch : queue.Prefetch;
    }

    /// <summary>Delay before transport retry 1..<see cref="MaxTransportRetries"/>; also the retry queue tiers.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays()
    {
        var delays = new List<TimeSpan>(Math.Max(0, MaxTransportRetries));
        for (var retry = 0; retry < MaxTransportRetries; retry++)
        {
            var delay = RetryBaseDelay * Math.Pow(2, retry);
            delays.Add(delay > MaxRetryDelay ? MaxRetryDelay : delay);
        }

        return [.. delays.Distinct()];
    }

    /// <summary>Binds <see cref="SectionName"/> and the <see cref="ConnectionStringName"/> connection string.</summary>
    public static RabbitMqOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new RabbitMqOptions();
        configuration.GetSection(SectionName).Bind(options);
        options.ConnectionString ??= configuration.GetConnectionString(ConnectionStringName);
        return options;
    }

    public void Validate()
    {
        if (!IsAmqpUri(ConnectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} must be an amqp:// or amqps:// URI.");
        }

        foreach (var (area, uri) in AreaConnectionStrings)
        {
            if (!string.IsNullOrWhiteSpace(uri) && !IsAmqpUri(uri))
            {
                throw new InvalidOperationException($"{SectionName}:AreaConnectionStrings:{area} must be an amqp:// or amqps:// URI.");
            }
        }

        Signing.Validate();

        if (MaxTransportRetries < 0 || RetryBaseDelay <= TimeSpan.Zero || MaxRetryDelay < RetryBaseDelay
            || DeliveryLimit < 1 || PublishTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName} has invalid retry, delivery-limit or timeout settings.");
        }
    }

    private static bool IsAmqpUri(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "amqp" || uri.Scheme == "amqps");
}
