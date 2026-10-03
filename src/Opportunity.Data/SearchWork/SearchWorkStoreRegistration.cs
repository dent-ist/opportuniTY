using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Opportunity.Application.Jobs;
using Opportunity.Application.SearchWork;
using Opportunity.Data.Jobs;

namespace Opportunity.Data.SearchWork;

public static class SearchWorkStoreRegistration
{
    /// <summary>
    /// The PostgreSQL search work store (V0011): <see cref="ISearchOutboxRepository"/>, <see cref="IIndexChunkTaskRepository"/>,
    /// <see cref="ISearchWorkMaintenance"/> and the <c>LISTEN</c> wake-up channel (<see cref="ISearchWorkWakeUpListener"/>).
    /// The data source is resolved when a repository is first used.
    /// </summary>
    public static IServiceCollection AddPostgresSearchWorkStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISearchOutboxRepository>(sp => new SearchOutboxRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IIndexChunkTaskRepository>(sp => new IndexChunkTaskRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<ISearchWorkMaintenance>(sp => new SearchWorkMaintenance(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(new SearchWorkListenerOptions());
        services.TryAddSingleton<ISearchWorkWakeUpListener>(sp => new PostgresSearchWorkListener(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<SearchWorkListenerOptions>(),
            sp.GetService<ILogger<PostgresSearchWorkListener>>() ?? NullLogger<PostgresSearchWorkListener>.Instance));
        return services;
    }

    /// <summary>
    /// The PostgreSQL job chunk store (V0006, V0013): <see cref="IJobChunkRepository"/> for the lease sweeper and
    /// <see cref="IJobChunkDispatchRepository"/> for the dispatcher, one instance.
    /// </summary>
    public static IServiceCollection AddPostgresJobChunkStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new JobChunkRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IJobChunkRepository>(sp => sp.GetRequiredService<JobChunkRepository>());
        services.TryAddSingleton<IJobChunkDispatchRepository>(sp => sp.GetRequiredService<JobChunkRepository>());
        return services;
    }
}
