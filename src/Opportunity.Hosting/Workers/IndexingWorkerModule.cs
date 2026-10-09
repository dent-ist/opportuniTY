using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Messaging;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Data.Audit;
using Opportunity.Data.Search;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Workspaces;
using Opportunity.Messaging;
using Opportunity.Search;
using Opportunity.Search.Projection;
using Opportunity.Search.Workers;
using Opportunity.Search.Writing;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// The interactive half of the indexing worker type (E07-T03): <see cref="InteractiveIndexWorker"/> consumes the SearchOutbox lanes the dispatcher
/// publishes to — <c>index.security</c> (L0) and <c>index.interactive</c> (L1), each with its own consumer pool so
/// security work is never queued behind coding (ADR-001 §5.3, Q-10). The chunk half (E07-T04) binds the bulk lanes with
/// its own consumers; both share <see cref="IProjectionIndexWriter"/>. Needs PostgreSQL, OpenSearch (<c>ConnectionStrings:OpenSearch</c>), RabbitMQ and object storage
/// (extracted text), which the worker host registers from the <c>ObjectStorage</c> section (this assembly never reaches
/// storage, ADR-015 D12.1); without any of them the module registers nothing beyond its placeholder.
/// </summary>
public static class IndexingWorkerModule
{
    private const string ObjectStorageSection = "ObjectStorage";

    public static IServiceCollection AddIndexingWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var openSearch = OpenSearchOptions.Bind(configuration);
        if (!configuration.GetSection(ObjectStorageSection).Exists()
            || openSearch.Endpoint is null
            || string.IsNullOrWhiteSpace(configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName)))
        {
            return services;
        }

        services.AddPostgresAuditStore();
        services.AddPostgresSearchWorkStore();
        services.AddPostgresProjectionSource();
        services.AddPostgresIndexPlacementStore();
        services.TryAddSingleton<IWorkspaceReader>(sp => new WorkspaceReader(sp.GetRequiredService<NpgsqlDataSource>()));

        var options = new InteractiveIndexWorkerOptions();
        configuration.GetSection(InteractiveIndexWorkerOptions.SectionName).Bind(options);
        var writer = new ProjectionWriterOptions();
        configuration.GetSection(ProjectionWriterOptions.SectionName).Bind(writer);
        services.AddOpenSearchIndexManagement(openSearch);
        services.AddInteractiveIndexWorker(options, writer, ProjectionOptions.Bind(configuration));

        services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
        services.AddMessageHandler<SearchOutboxMessage, InteractiveIndexWorker>(WorkQueues.IndexSecurity);
        services.AddMessageHandler<SearchOutboxMessage, InteractiveIndexWorker>(WorkQueues.IndexInteractive);
        return services;
    }
}
