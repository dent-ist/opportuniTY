using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Data.Audit;
using Opportunity.Data.Identity;
using Opportunity.Data.Jobs;
using Opportunity.Data.Productions;
using Opportunity.Data.SearchWork;
using Opportunity.Hosting.Health;
using Opportunity.Jobs;
using Opportunity.Messaging;
using Opportunity.Production.Productions;
using Opportunity.Security.Authorization;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// The production worker type (E12-T03): plans and completes Bates allocations
/// (<see cref="BatesAllocationCoordinatorService"/>) and executes their chunks through the idempotent
/// <see cref="JobChunkConsumer"/> with <see cref="BatesChunkExecutor"/>, which re-checks the initiator's
/// <c>Production.Create</c> through the PDP per chunk (ADR-015 D9.4). Needs PostgreSQL (<c>ConnectionStrings:App</c>);
/// without it the module registers nothing beyond its placeholder. With RabbitMQ configured the consumer is bound to
/// <c>production.chunks</c>, where the job dispatcher publishes production chunks. Volume generation (E12-T05) adds
/// object storage here.
/// </summary>
public static class ProductionWorkerModule
{
    public static IServiceCollection AddProductionWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(PostgresReadiness.ConnectionStringName)))
        {
            return services;
        }

        services.AddPostgresAuditStore();
        services.AddPostgresSecurityState();
        services.AddOpportunityAuthorization();

        // The worker host is a WebApplication: once authorization services exist it adds the authorization middleware,
        // which needs the full registration (no endpoint of the worker uses a policy).
        services.AddAuthorization();
        services.AddPostgresProductionStore();
        services.TryAddSingleton<IJobRepository>(sp => new JobRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddPostgresJobChunkStore();
        services.AddJobChunkConsumer();
        services.AddProductionJobs(BindOptions(configuration));

        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName)))
        {
            services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
            services.AddMessageHandler<JobChunkMessage, JobChunkConsumer>(WorkQueues.Production);
        }

        return services;
    }

    /// <summary><c>Production:DocumentsPerChunk</c> and <c>Production:NumbersPerChunk</c> (ADR-010 §6 defaults 100 and 2,000).</summary>
    public static ProductionJobOptions BindOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new ProductionJobOptions();
        return new ProductionJobOptions
        {
            DocumentsPerChunk = configuration.GetValue("Production:DocumentsPerChunk", defaults.DocumentsPerChunk),
            NumbersPerChunk = configuration.GetValue("Production:NumbersPerChunk", defaults.NumbersPerChunk),
        };
    }
}
