using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Bootstrap;
using Opportunity.Application.Search.Indexing;
using Opportunity.Search.Indexing;

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
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IndexManager>();
        services.TryAddSingleton<IIndexManager>(sp => sp.GetRequiredService<IndexManager>());
        services.TryAddSingleton<IWorkspaceSearchPlacement>(sp => sp.GetRequiredService<IndexManager>());
        return services;
    }

    internal static IServiceCollection AddOpenSearchCore(IServiceCollection services, OpenSearchOptions options, ProjectionMappings? mappings = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(mappings ?? ProjectionMappings.Embedded);
        services.TryAddSingleton(sp => new OpenSearchConnection(sp.GetRequiredService<OpenSearchOptions>()));
        services.TryAddSingleton(sp => new IndexNames(sp.GetRequiredService<OpenSearchOptions>().IndexPrefix));
        services.TryAddSingleton<IndexTemplates>();
        return services;
    }
}
