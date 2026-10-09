using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Bootstrap;
using Opportunity.Application.Keys;
using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging;

namespace Opportunity.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the RabbitMQ transport: <see cref="IMessagePublisher"/>, <see cref="IMessageConsumer"/>, the message
    /// contract registry and serializer. Nothing connects until first use.
    /// </summary>
    public static IServiceCollection AddRabbitMqMessaging(this IServiceCollection services, RabbitMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton(_ => MessageContracts.CreateRegistry());
        services.TryAddSingleton(sp => new MessageSerializer(sp.GetRequiredService<MessageTypeRegistry>()));
        services.TryAddSingleton(sp => options.Signing.Enabled
            ? new EnvelopeSigner(options.Signing, sp.GetService<ISecretProvider>())
            : EnvelopeSigner.Disabled);
        services.TryAddSingleton<RabbitMqConnections>();
        services.TryAddSingleton<RabbitMqMessagePublisher>();
        services.TryAddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<RabbitMqMessagePublisher>());
        services.TryAddSingleton<RabbitMqMessageConsumer>();
        services.TryAddSingleton<IMessageConsumer>(sp => sp.GetRequiredService<RabbitMqMessageConsumer>());
        return services;
    }

    /// <summary>Adds the migrator step that declares <see cref="RabbitMqTopology"/> (Refs ADR-001 lanes, ADR-010 §7).</summary>
    public static IServiceCollection AddRabbitMqTopologyBootstrap(
        this IServiceCollection services, RabbitMqOptions options, IReadOnlyList<WorkQueue>? queues = null)
    {
        services.AddRabbitMqMessaging(options);
        services.AddSingleton<IInfrastructureBootstrapStep>(sp => new RabbitMqTopologyBootstrapStep(
            sp.GetRequiredService<RabbitMqConnections>(),
            sp.GetRequiredService<RabbitMqOptions>(),
            sp.GetRequiredService<ILogger<RabbitMqTopologyBootstrapStep>>(),
            queues));
        return services;
    }

    /// <summary>
    /// Consumes <paramref name="queue"/> with <typeparamref name="THandler"/> (scoped) for <typeparamref name="TPayload"/>
    /// messages. Requires <see cref="AddRabbitMqMessaging"/>.
    /// </summary>
    public static IServiceCollection AddMessageHandler<TPayload, THandler>(this IServiceCollection services, WorkQueue queue)
        where TPayload : class
        where THandler : class, IMessageHandler<TPayload>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(queue);

        services.TryAddScoped<THandler>();
        services.AddSingleton(new MessageHandlerRegistration(
            queue,
            typeof(TPayload),
            (provider, payload, message, ct) => provider.GetRequiredService<THandler>().HandleAsync((TPayload)payload, message, ct)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MessageHandlerHostedService>());
        return services;
    }

    /// <summary>
    /// Runs the <see cref="DeadLetterRecorder"/> (one component per installation, the dispatcher). Requires
    /// <see cref="AddRabbitMqMessaging"/>; the PostgreSQL <see cref="IDeadLetterStore"/> is resolved when it starts.
    /// </summary>
    public static IServiceCollection AddDeadLetterRecorder(this IServiceCollection services, DeadLetterRecorderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new DeadLetterRecorderOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DeadLetterRecorder>());
        return services;
    }

    /// <summary>Samples queue depth and consumer metrics (register in one component, the dispatcher).</summary>
    public static IServiceCollection AddRabbitMqQueueMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RabbitMqQueueMetrics>());
        return services;
    }
}
