using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Data.Audit;
using Opportunity.Data.Jobs;
using Opportunity.Data.Search;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Workspaces;
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
/// drives reindex jobs (target, backfill tasks for this worker, validation, alias switch, retention).
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

        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName)))
        {
            services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
            services.AddMessageHandler<IndexChunkTaskMessage, ChunkIndexTaskConsumer>(WorkQueues.IndexSecurityBulk);
            services.AddMessageHandler<IndexChunkTaskMessage, ChunkIndexTaskConsumer>(WorkQueues.IndexBulk);
        }

        return services;
    }
}
