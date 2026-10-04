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
using Opportunity.Data.SearchWork;
using Opportunity.Data.Snapshots;
using Opportunity.Jobs;
using Opportunity.Messaging;
using Opportunity.Production.Exports;
using Opportunity.Security.Authorization;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// The export worker type (E12-T01): plans and finalizes exports (<see cref="ExportCoordinatorService"/>) and executes
/// their chunks through the idempotent <see cref="JobChunkConsumer"/> with <see cref="ExportChunkExecutor"/>, which
/// re-authorizes every document for the initiator through the PDP (Q-15). It needs object storage (sources and the
/// volume), which the worker host registers from the <c>ObjectStorage</c> section (this assembly never reaches storage,
/// ADR-015 D12.1); without that section the module registers nothing beyond its placeholder. With RabbitMQ configured the
/// consumer is bound to <c>export.chunks</c>, where the job dispatcher publishes export chunks.
/// </summary>
public static class ExportWorkerModule
{
    private const string ObjectStorageSection = "ObjectStorage";

    public static IServiceCollection AddExportWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.GetSection(ObjectStorageSection).Exists())
        {
            return services;
        }

        services.AddPostgresAuditStore();
        services.AddPostgresSecurityState();
        services.AddOpportunityAuthorization();
        services.TryAddSingleton<IFieldAccessFilter, UnrestrictedFieldAccess>();
        services.AddPostgresSnapshotStore();
        services.AddPostgresExportStore();
        services.TryAddSingleton<IJobRepository>(sp => new JobRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddPostgresJobChunkStore();
        services.AddJobChunkConsumer();
        services.AddExportJobs(BindOptions(configuration));

        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName)))
        {
            services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
            services.AddMessageHandler<JobChunkMessage, JobChunkConsumer>(WorkQueues.Export);
        }

        return services;
    }

    /// <summary><c>Export:FileConcurrency</c> (files copied in parallel per chunk) and <c>Export:TempDirectory</c>.</summary>
    public static ExportJobOptions BindOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new ExportJobOptions();
        return new ExportJobOptions
        {
            FileConcurrency = configuration.GetValue("Export:FileConcurrency", defaults.FileConcurrency),
            TempDirectory = configuration["Export:TempDirectory"],
        };
    }
}
