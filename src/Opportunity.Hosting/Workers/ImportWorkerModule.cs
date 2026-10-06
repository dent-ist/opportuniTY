using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Coding;
using Opportunity.Application.Documents.Dedupe;
using Opportunity.Application.Fields;
using Opportunity.Application.Import;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Data.Audit;
using Opportunity.Data.Fields;
using Opportunity.Data.Import;
using Opportunity.Data.Jobs;
using Opportunity.Data.Relationships;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Workspaces;
using Opportunity.Import.Jobs;
using Opportunity.Import.Volumes;
using Opportunity.Jobs;
using Opportunity.Messaging;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// The import worker type (E08-T03): prepares import jobs and executes their chunks through the idempotent
/// <see cref="JobChunkConsumer"/> with <see cref="ImportChunkExecutor"/>. It needs object storage (the uploaded DATs),
/// which the worker host registers from the <c>ObjectStorage</c> section (this assembly never reaches storage,
/// ADR-015 D12.1); without that section the module registers nothing beyond its placeholder. With RabbitMQ configured
/// the consumer is bound to <c>import.chunks</c>, where the job dispatcher (E06-T04) publishes import chunks.
/// </summary>
public static class ImportWorkerModule
{
    private const string ObjectStorageSection = "ObjectStorage";

    public static IServiceCollection AddImportWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.GetSection(ObjectStorageSection).Exists())
        {
            return services;
        }

        services.AddPostgresAuditStore();
        // Security-affecting overlays recompute restriction classes in the chunk transaction (§24); none bound until E05-T06.
        services.TryAddSingleton<IRestrictionClassBinding>(NoRestrictionClassBinding.Instance);
        services.TryAddSingleton<IImportBatchStore>(sp => new ImportBatchRepository(
            sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IRestrictionClassBinding>()));
        services.TryAddSingleton<ImportKeyCollisionChecker>();
        services.TryAddSingleton<IJobRepository>(sp => new JobRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddPostgresJobChunkStore();
        services.TryAddSingleton<IFieldCatalogRepository>(sp => new FieldCatalogRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IWorkspaceReader>(sp => new WorkspaceReader(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddJobChunkConsumer();
        services.AddImportJobs(BindOptions(configuration));

        // Dedupe runs (E09-T04): RelationshipChunks on the same queue, over the relationship writers.
        services.TryAddSingleton<IDedupeStore>(sp => new DedupeStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddJobChunkExecutor<DedupeChunkExecutor>();

        // Volumes whose natives, text (E08-T04) and OPT images the worker reads: Import:VolumeShareRoot.
        services.TryAddSingleton(new ImportVolumeOptions
        {
            VolumeShareRoot = configuration[$"{ImportVolumeOptions.SectionName}:{nameof(ImportVolumeOptions.VolumeShareRoot)}"],
        });

        // The dispatcher (E06-T04) publishes import chunks to import.chunks; this worker consumes them when RabbitMQ is
        // configured (without it, preparation still runs and chunks wait in PostgreSQL).
        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName)))
        {
            services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
            services.AddMessageHandler<JobChunkMessage, JobChunkConsumer>(WorkQueues.Import);
        }

        return services;
    }

    /// <summary>
    /// <c>Import:FileConcurrency</c> (volume files stored in parallel per chunk, E08-T04) and the indexed-text cap shared
    /// with the projection (<c>Search:IndexedTextCap</c>, Q-29).
    /// </summary>
    public static ImportJobOptions BindOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new ImportJobOptions();
        return new ImportJobOptions
        {
            FileConcurrency = configuration.GetValue("Import:FileConcurrency", defaults.FileConcurrency),
            IndexedTextCap = configuration.GetValue("Search:IndexedTextCap", defaults.IndexedTextCap),
        };
    }
}
