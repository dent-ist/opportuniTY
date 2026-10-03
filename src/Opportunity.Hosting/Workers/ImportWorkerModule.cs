using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Fields;
using Opportunity.Application.Import;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Storage;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Data.Audit;
using Opportunity.Data.Fields;
using Opportunity.Data.Import;
using Opportunity.Data.Jobs;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Workspaces;
using Opportunity.Import.Jobs;
using Opportunity.Jobs;
using Opportunity.Messaging;
using Opportunity.Storage;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// The import worker type (E08-T03): prepares import jobs and executes their chunks through the idempotent
/// <see cref="JobChunkConsumer"/> with <see cref="ImportChunkExecutor"/>. It needs object storage (the uploaded DATs);
/// without an <c>ObjectStorage</c> section the module registers nothing beyond its placeholder. With RabbitMQ configured
/// the consumer is bound to <c>import.chunks</c>, where the job dispatcher (E06-T04) publishes import chunks.
/// </summary>
public static class ImportWorkerModule
{
    public static IServiceCollection AddImportWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var storageSection = configuration.GetSection(ObjectStorageOptions.SectionName);
        if (!storageSection.Exists())
        {
            return services;
        }

        if (!services.Any(s => s.ServiceType == typeof(IObjectStore)))
        {
            var storage = new ObjectStorageOptions();
            storageSection.Bind(storage);
            storage.Validate();
            services.AddObjectStorage(storage);
        }

        services.AddPostgresAuditStore();
        services.TryAddSingleton<IImportBatchStore>(sp => new ImportBatchRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IJobRepository>(sp => new JobRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddPostgresJobChunkStore();
        services.TryAddSingleton<IFieldCatalogRepository>(sp => new FieldCatalogRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IWorkspaceReader>(sp => new WorkspaceReader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddJobChunkConsumer();
        services.AddImportJobs();

        // The dispatcher (E06-T04) publishes import chunks to import.chunks; this worker consumes them when RabbitMQ is
        // configured (without it, preparation still runs and chunks wait in PostgreSQL).
        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName)))
        {
            services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
            services.AddMessageHandler<JobChunkMessage, JobChunkConsumer>(WorkQueues.Import);
        }

        return services;
    }
}
