using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Telemetry;

namespace Opportunity.Jobs;

public static class JobChunkConsumerRegistration
{
    /// <summary>
    /// Registers <see cref="JobChunkConsumer"/> (scoped, like every message handler). Bind it to the worker's queue with
    /// <c>AddMessageHandler&lt;JobChunkMessage, JobChunkConsumer&gt;(queue)</c> from <c>Opportunity.Messaging</c> and add
    /// one executor per operation kind with <see cref="AddJobChunkExecutor{TExecutor}"/>. The host must also register
    /// <see cref="IJobChunkRepository"/> and the audit store (<c>AddPostgresAuditStore</c>).
    /// </summary>
    public static IServiceCollection AddJobChunkConsumer(
        this IServiceCollection services, JobChunkConsumerOptions? options = null, JobLeaseOptions? lease = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new JobChunkConsumerOptions());
        services.TryAddSingleton(lease ?? new JobLeaseOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMessageProcessingMeter>(NullMessageProcessingMeter.Instance);
        services.TryAddScoped<JobChunkConsumer>();
        return services;
    }

    /// <summary>Adds the executor of one <see cref="Core.Jobs.ChunkOperationKind"/> (scoped).</summary>
    public static IServiceCollection AddJobChunkExecutor<TExecutor>(this IServiceCollection services)
        where TExecutor : class, IJobChunkExecutor
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobChunkExecutor, TExecutor>());
        return services;
    }
}
