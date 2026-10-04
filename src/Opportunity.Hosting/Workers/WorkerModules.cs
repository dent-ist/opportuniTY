using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Opportunity.Data.Audit;
using Opportunity.Data.SearchWork;
using Opportunity.Jobs;
using Opportunity.Jobs.Dispatch;
using Opportunity.Messaging;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// One worker type's service registrations (consumers, handlers, its options). A worker process registers the modules
/// selected by <c>Workers:Enabled</c>; the module code is identical whether it runs alone or combined.
/// </summary>
public sealed record WorkerModule(string Type, Action<IServiceCollection, IConfiguration> Register);

public static class WorkerModuleCatalog
{
    // Each epic replaces its module's placeholder with real consumer registrations (E06 dispatcher, E07 indexing, ...).
    public static IReadOnlyDictionary<string, WorkerModule> Modules { get; } =
        WorkerTypes.All.ToDictionary(type => type, type => new WorkerModule(type, (services, configuration) => Register(services, configuration, type)));

    private static void Register(IServiceCollection services, IConfiguration configuration, string type)
    {
        AddPlaceholder(services, type);

        if (type == WorkerTypes.Import)
        {
            services.AddImportWorker(configuration);
        }

        if (type == WorkerTypes.Indexing)
        {
            services.AddIndexingWorker(configuration);
        }

        if (type == WorkerTypes.Dispatcher)
        {
            AddDispatcher(services, configuration);
        }

        if (type == WorkerTypes.Indexing)
        {
            services.AddChunkIndexWorkerModule(configuration);
        }
    }

    private static void AddDispatcher(IServiceCollection services, IConfiguration configuration)
    {
        // Audit (ADR-013 §1.1) and coding-event partitions are created ahead by the dispatcher (E14-T01), and so are the
        // search work day partitions, with their retention and lost-work recovery (E06-T03, ADR-001 §1 R4, §6.3).
        services.AddPartitionMaintenance();
        services.AddPostgresSearchWorkStore();
        services.AddSearchWorkHousekeeping();

        // Job chunk leases are swept (ADR-010 §3.3) and SearchOutbox rows, IndexChunkTasks and job chunks are published
        // (E06-T04, ADR-001 §6) by the dispatcher. Each loop resolves its stores lazily: a dispatcher without PostgreSQL
        // or RabbitMQ configured reports the loop disabled instead of failing to start.
        services.AddPostgresJobChunkStore();
        services.AddJobLeaseSweeper();
        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName)))
        {
            services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
            services.AddRabbitMqQueueMetrics();
        }

        services.AddOutboxDispatcher(configuration.GetSection(OutboxDispatcherOptions.SectionName).Get<OutboxDispatcherOptions>());
    }

    private static void AddPlaceholder(IServiceCollection services, string type) =>
        // Not AddHostedService: it de-duplicates by implementation type, and every module uses the same placeholder.
        services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<WorkerHeartbeatService>(sp, type));
}
