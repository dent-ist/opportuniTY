using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Data.Audit;
using Opportunity.Data.Exports;
using Opportunity.Data.Identity;
using Opportunity.Data.Jobs;
using Opportunity.Data.Productions;
using Opportunity.Data.Rendering;
using Opportunity.Data.SearchWork;
using Opportunity.Jobs;
using Opportunity.Messaging;
using Opportunity.Production.Volumes;
using Opportunity.Rendering.Jobs;
using Opportunity.Rendering.Sandboxing;
using Opportunity.Security.Authorization;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// The rendering worker type (E11-T02): creates and plans render jobs for finished imports
/// (<see cref="RenderCoordinatorService"/>) and executes their chunks through the idempotent
/// <see cref="JobChunkConsumer"/> with <see cref="RenderChunkExecutor"/>. It needs object storage (sources and
/// renditions), which the worker host registers from the <c>ObjectStorage</c> section (this assembly never reaches
/// storage, ADR-015 D12.1); without that section the module registers nothing beyond its placeholder. With RabbitMQ
/// configured the consumer is bound to <c>render.chunks</c>, where the job dispatcher publishes render chunks (failed
/// deliveries are retried and parked per ADR-010 §7 by the messaging layer).
/// <para>
/// It also writes production volumes (E12-T05): their pages are decoded, redacted and endorsed in the render sandbox,
/// so the <see cref="ProductionVolumeChunkExecutor"/> and the <see cref="ProductionVolumeCoordinatorService"/> run here,
/// with the PDP for the per-chunk re-authorization of the run's initiator (Q-15).
/// </para>
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
        services.AddRenderJobs(BindOptions(configuration), sandbox: BindSandboxOptions(configuration));

        // Production volumes (E12-T05).
        services.AddPostgresSecurityState();
        services.AddOpportunityAuthorization();
        services.AddAuthorization();
        services.TryAddSingleton<IFieldAccessFilter, UnrestrictedFieldAccess>();
        services.AddPostgresExportStore();
        services.AddPostgresProductionStore();
        services.AddProductionVolumeJobs(BindVolumeOptions(configuration));

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

    /// <summary><c>Production:Volume:DocumentConcurrency</c> and <c>Production:Volume:TempDirectory</c> (E12-T05).</summary>
    public static ProductionVolumeOptions BindVolumeOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new ProductionVolumeOptions();
        return new ProductionVolumeOptions
        {
            DocumentConcurrency = configuration.GetValue("Production:Volume:DocumentConcurrency", defaults.DocumentConcurrency),
            TempDirectory = configuration["Production:Volume:TempDirectory"] ?? configuration["Render:TempDirectory"],
        };
    }

    /// <summary>
    /// <c>Render:Sandbox</c> (E11-T03): every document renders in a sandboxed child process unless
    /// <c>Render:Sandbox:Enabled</c> is false (development only); limits and requirements in docs/architecture/rendering.md.
    /// </summary>
    public static RenderSandboxOptions BindSandboxOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new RenderSandboxOptions();
        configuration.GetSection(RenderSandboxOptions.SectionName).Bind(options);
        options.Validate();
        return options;
    }
}
