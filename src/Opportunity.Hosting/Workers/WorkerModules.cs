using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Opportunity.Data.Audit;

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
        WorkerTypes.All.ToDictionary(type => type, type => new WorkerModule(type, (services, _) => Register(services, type)));

    private static void Register(IServiceCollection services, string type)
    {
        AddPlaceholder(services, type);

        // Audit (ADR-013 §1.1) and coding-event partitions are created ahead by the dispatcher (E14-T01).
        if (type == WorkerTypes.Dispatcher)
        {
            services.AddPartitionMaintenance();
        }
    }

    private static void AddPlaceholder(IServiceCollection services, string type) =>
        // Not AddHostedService: it de-duplicates by implementation type, and every module uses the same placeholder.
        services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<WorkerHeartbeatService>(sp, type));
}
