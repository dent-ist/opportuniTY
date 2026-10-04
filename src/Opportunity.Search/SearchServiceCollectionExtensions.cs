using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Bootstrap;
using Opportunity.Application.Search;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Search.Projection;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Storage;
using Opportunity.Application.Telemetry;
using Opportunity.Core.QueryLanguage;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;
using Opportunity.Search.Querying;
using Opportunity.Search.Workers;
using Opportunity.Search.Writing;

namespace Opportunity.Search;

public static class SearchServiceCollectionExtensions
{
    /// <summary>Adds the migrator step that installs the projection index templates (E04-T01, ADR-007 R2).</summary>
    public static IServiceCollection AddOpenSearchIndexTemplateBootstrap(this IServiceCollection services, OpenSearchOptions options)
    {
        AddOpenSearchCore(services, options);
        services.AddSingleton<IInfrastructureBootstrapStep, IndexTemplateBootstrapStep>();
        return services;
    }

    /// <summary>
    /// Registers index management: <see cref="IIndexManager"/> for search-module code and
    /// <see cref="IWorkspaceSearchPlacement"/> for use cases. Needs an <see cref="IIndexPlacementStore"/> registration.
    /// </summary>
    public static IServiceCollection AddOpenSearchIndexManagement(this IServiceCollection services, OpenSearchOptions options)
    {
        AddOpenSearchCore(services, options);
        AddIndexManagement(services);
        return services;
    }

    /// <summary>
    /// Registers the logical search service (<see cref="ISearchService"/>, scoped like the PDP it calls) with index
    /// management and the first-slice <see cref="ISearchQueryTranslator"/>. Settings are bound from configuration
    /// (<c>OpenSearch</c>, <c>ConnectionStrings:OpenSearch</c>) and validated on first use, unless an
    /// <see cref="OpenSearchOptions"/> instance is registered first. Needs <see cref="IIndexPlacementStore"/>,
    /// <see cref="ISearchSessionStore"/>, the PDP and an audit writer.
    /// </summary>
    public static IServiceCollection AddOpenSearchSearchService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp =>
        {
            var options = OpenSearchOptions.Bind(sp.GetRequiredService<IConfiguration>());
            options.Validate();
            return options;
        });
        AddOpenSearchServices(services, null);
        AddIndexManagement(services);
        services.TryAddSingleton<ISearchQueryTranslator, BasicSearchQueryTranslator>();
        services.TryAddSingleton(sp => sp.GetService<QueryValidator>()?.Limits ?? QueryLimits.Default);
        services.TryAddScoped<ISearchService, SearchService>();
        return services;
    }

    /// <summary>
    /// Registers the projection pipeline (E07-T02): <see cref="IProjectionService"/> for index workers, the interim
    /// Candidate A <see cref="IProjectionBuilder"/> (replace that registration to swap the coding representation,
    /// ADR-004b) and the object-storage text loader. Needs <see cref="IProjectionSourceReader"/> and
    /// <see cref="IObjectStore"/> registrations.
    /// </summary>
    public static IServiceCollection AddSearchProjection(this IServiceCollection services, ProjectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new ProjectionOptions();
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton<IProjectionBuilder, CandidateAProjectionBuilder>();
        services.TryAddSingleton<IProjectionTextLoader>(sp => new ObjectStoreProjectionTextLoader(
            sp.GetRequiredService<IObjectStore>(), sp.GetRequiredService<ProjectionOptions>()));
        services.TryAddSingleton<IProjectionService, ProjectionService>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="IProjectionIndexWriter"/>, the byte-bounded <c>_bulk</c> write path every index worker shares,
    /// with index management. OpenSearch settings are bound from configuration on first use unless an
    /// <see cref="OpenSearchOptions"/> instance is registered first. Needs <see cref="IIndexPlacementStore"/>.
    /// </summary>
    public static IServiceCollection AddProjectionIndexWriter(this IServiceCollection services, ProjectionWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new ProjectionWriterOptions();
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(sp =>
        {
            var bound = OpenSearchOptions.Bind(sp.GetRequiredService<IConfiguration>());
            bound.Validate();
            return bound;
        });
        AddOpenSearchServices(services, null);
        AddIndexManagement(services);
        services.TryAddSingleton<IProjectionIndexWriter>(sp => new ProjectionIndexWriter(
            sp.GetRequiredService<IIndexManager>(), sp.GetRequiredService<OpenSearchConnection>(), sp.GetRequiredService<ProjectionWriterOptions>()));
        return services;
    }

    /// <summary>
    /// Registers the chunk index worker (E07-T04): <see cref="ChunkIndexTaskConsumer"/> (scoped, like every message
    /// handler), the projection pipeline and the shared writer. Bind it to the security-bulk and bulk lanes with
    /// <c>AddMessageHandler&lt;IndexChunkTaskMessage, ChunkIndexTaskConsumer&gt;(queue)</c>. The host also registers
    /// <see cref="IIndexChunkTaskRepository"/>, <see cref="IIndexTaskMembershipReader"/>,
    /// <see cref="IProjectionSourceReader"/>, <see cref="IIndexPlacementStore"/>, <see cref="IObjectStore"/> and an audit writer.
    /// </summary>
    public static IServiceCollection AddChunkIndexWorker(
        this IServiceCollection services, ChunkIndexWorkerOptions? options = null, ProjectionWriterOptions? writer = null, ProjectionOptions? projection = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new ChunkIndexWorkerOptions();
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMessageProcessingMeter>(NullMessageProcessingMeter.Instance);
        services.AddSearchProjection(projection);
        services.AddProjectionIndexWriter(writer);
        services.TryAddScoped<ChunkIndexTaskConsumer>();
        return services;
    }

    internal static IServiceCollection AddOpenSearchCore(IServiceCollection services, OpenSearchOptions options, ProjectionMappings? mappings = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.TryAddSingleton(options);
        return AddOpenSearchServices(services, mappings);
    }

    private static IServiceCollection AddOpenSearchServices(IServiceCollection services, ProjectionMappings? mappings)
    {
        services.TryAddSingleton(mappings ?? ProjectionMappings.Embedded);
        services.TryAddSingleton(sp => new OpenSearchConnection(sp.GetRequiredService<OpenSearchOptions>()));
        services.TryAddSingleton(sp => new IndexNames(sp.GetRequiredService<OpenSearchOptions>().IndexPrefix));
        services.TryAddSingleton<IndexTemplates>();
        return services;
    }

    private static void AddIndexManagement(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IndexManager>();
        services.TryAddSingleton<IIndexManager>(sp => sp.GetRequiredService<IndexManager>());
        services.TryAddSingleton<IWorkspaceSearchPlacement>(sp => sp.GetRequiredService<IndexManager>());
    }
}
