using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Workspaces.Deletion;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Data.Audit;
using Opportunity.Data.Jobs;
using Opportunity.Data.Search;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Workspaces;
using Opportunity.Jobs.Lifecycle;
using Opportunity.Messaging;
using Opportunity.Search;
using Opportunity.Search.Projection;
using Opportunity.Search.Reindex;
using Opportunity.Search.Workers;
using Opportunity.Search.Writing;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// The chunk half of the indexing worker type (E07-T04): <see cref="ChunkIndexTaskConsumer"/> on the security-bulk
/// (L2) and bulk (L3) lanes, where the dispatcher publishes IndexChunkTasks. It needs OpenSearch
/// (<c>ConnectionStrings:OpenSearch</c> or <c>OpenSearch:Endpoint</c>) and object storage for extracted text, which the
/// worker host registers from the <c>ObjectStorage</c> section (this assembly never reaches storage, ADR-015 D12.1);
/// without either the module registers nothing beyond its placeholder. The interactive half (E07-T03) registers its
/// own consumer next to this one and shares the projection writer. The reindex coordinator (E07-T11) runs here too: it
/// drives reindex jobs (target, backfill tasks for this worker, validation, alias switch, retention). So does the workspace
/// deletion coordinator (E20-T02), which needs PostgreSQL, OpenSearch, object storage and the key provider.
/// </summary>
public static class ChunkIndexWorkerModule
{
    private const string ObjectStorageSection = "ObjectStorage";

    public static IServiceCollection AddChunkIndexWorkerModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var openSearch = configuration.GetConnectionString(OpenSearchOptions.ConnectionStringName)
            ?? configuration[$"{OpenSearchOptions.SectionName}:{nameof(OpenSearchOptions.Endpoint)}"];
        if (!configuration.GetSection(ObjectStorageSection).Exists() || string.IsNullOrWhiteSpace(openSearch))
        {
            return services;
        }

        var options = new ChunkIndexWorkerOptions();
        configuration.GetSection(ChunkIndexWorkerOptions.SectionName).Bind(options);
        var writer = new ProjectionWriterOptions();
        configuration.GetSection(ProjectionWriterOptions.SectionName).Bind(writer);

        services.AddPostgresAuditStore();
        services.AddPostgresSearchWorkStore();
        services.AddPostgresProjectionSource();
        services.AddPostgresIndexPlacementStore();
        services.AddChunkIndexWorker(options, writer, ProjectionOptions.Bind(configuration));

        services.AddPostgresReindexStore();
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.AddPostgresJobChunkStore();
        services.AddPostgresSearchWatermarkStore();
        services.AddPostgresPreservationLocks();
        services.AddReindexCoordinator(configuration);

        // Defensible workspace deletion (E20-T02): this host reaches every store a deletion purges.
        var deletion = new WorkspaceDeletionOptions();
        configuration.GetSection(WorkspaceDeletionOptions.SectionName).Bind(deletion);
        services.AddPostgresWorkspaceDeletions();
        services.AddWorkspaceDeletionCoordinator(deletion);

        // Before a purge the workspace's audit chain gets a signed checkpoint (E14-T03, ADR-013 §3.4), with the sealer
        // login (ConnectionStrings:AuditSealer). Without it the dispatcher's 10-minute checkpoints are the evidence.
        if (configuration.GetConnectionString(AuditChainDataSource.ConnectionStringName) is { Length: > 0 } sealer)
        {
            services.AddAuditChain(_ => NpgsqlDataSource.Create(sealer));
            services.TryAddSingleton<IBeforeWorkspaceDeletion, AuditCheckpointBeforeDeletion>();
        }

        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName)))
        {
            services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
            services.AddMessageHandler<IndexChunkTaskMessage, ChunkIndexTaskConsumer>(WorkQueues.IndexSecurityBulk);
            services.AddMessageHandler<IndexChunkTaskMessage, ChunkIndexTaskConsumer>(WorkQueues.IndexBulk);
        }

        return services;
    }
}
