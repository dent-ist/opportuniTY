using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Data.Audit;
using Opportunity.Data.Jobs;
using Opportunity.Data.Rendering;
using Opportunity.Data.SearchWork;
using Opportunity.Jobs;
using Opportunity.Messaging;
using Opportunity.Rendering.Jobs;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// The rendering worker type (E11-T02): creates and plans render jobs for finished imports
/// (<see cref="RenderCoordinatorService"/>) and executes their chunks through the idempotent
/// <see cref="JobChunkConsumer"/> with <see cref="RenderChunkExecutor"/>. It needs object storage (sources and
/// renditions), which the worker host registers from the <c>ObjectStorage</c> section (this assembly never reaches
/// storage, ADR-015 D12.1); without that section the module registers nothing beyond its placeholder. With RabbitMQ
/// configured the consumer is bound to <c>render.chunks</c>, where the job dispatcher publishes render chunks (failed
/// deliveries are retried and parked per ADR-010 §7 by the messaging layer).
/// </summary>
public static class RenderingWorkerModule
{
    private const string ObjectStorageSection = "ObjectStorage";

    public static IServiceCollection AddRenderingWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.GetSection(ObjectStorageSection).Exists())
        {
            return services;
        }

        services.AddPostgresAuditStore();
        services.AddPostgresRenderStore();
        services.TryAddSingleton<IJobRepository>(sp => new JobRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddPostgresJobChunkStore();
        services.AddJobChunkConsumer();
        services.AddRenderJobs(BindOptions(configuration));

        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName)))
        {
            services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
            services.AddMessageHandler<JobChunkMessage, JobChunkConsumer>(WorkQueues.Rendering);
        }

        return services;
    }

    /// <summary><c>Render:TempDirectory</c> (source copies and page files; small, one page at a time).</summary>
    public static RenderJobOptions BindOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new RenderJobOptions { TempDirectory = configuration["Render:TempDirectory"] };
    }
}
