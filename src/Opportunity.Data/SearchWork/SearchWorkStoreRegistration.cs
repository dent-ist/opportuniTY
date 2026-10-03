using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.SearchWork;

namespace Opportunity.Data.SearchWork;

public static class SearchWorkStoreRegistration
{
    /// <summary>
    /// The PostgreSQL search work store (V0011): <see cref="ISearchOutboxRepository"/>, <see cref="IIndexChunkTaskRepository"/>
    /// and <see cref="ISearchWorkMaintenance"/>. The data source is resolved when a repository is first used.
    /// </summary>
    public static IServiceCollection AddPostgresSearchWorkStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISearchOutboxRepository>(sp => new SearchOutboxRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IIndexChunkTaskRepository>(sp => new IndexChunkTaskRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<ISearchWorkMaintenance>(sp => new SearchWorkMaintenance(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
