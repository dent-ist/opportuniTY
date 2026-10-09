using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Identity;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Data.Audit;
using Opportunity.Data.Coding;
using Opportunity.Data.Fields;
using Opportunity.Data.Identity;
using Opportunity.Data.Jobs;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Snapshots;
using Opportunity.Hosting.Health;
using Opportunity.Jobs;
using Opportunity.Messaging;
using Opportunity.Security.Authorization;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// The bulk-coding worker type (E10-T04): executes <c>BulkCodingChunk</c>s through the idempotent
/// <see cref="JobChunkConsumer"/> with <see cref="BulkCodingChunkExecutor"/>, which re-authorizes the initiator through
/// the PDP per chunk (ADR-015 D9.4) and writes coding, provenance, the chunk's IndexChunkTask and fence F3 in one
/// PostgreSQL transaction. Jobs are planned by the API at submission. Needs PostgreSQL (<c>ConnectionStrings:App</c>);
/// without it the module registers nothing beyond its placeholder. With RabbitMQ configured the consumer is bound to
/// <c>bulkcoding.chunks</c>, where the job dispatcher (E06-T04) publishes the chunks.
/// </summary>
public static class BulkCodingWorkerModule
{
    public static IServiceCollection AddBulkCodingWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(PostgresReadiness.ConnectionStringName)))
        {
            return services;
        }

        services.AddPostgresAuditStore();
        services.AddPostgresJobChunkStore();
        services.AddPostgresSnapshotStore();
        services.AddPostgresSecurityState();
        services.TryAddSingleton<IJobRepository>(sp => new JobRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IFieldCatalogRepository>(sp => new FieldCatalogRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IUserDirectory>(sp => new PostgresUserDirectory(sp.GetRequiredService<NpgsqlDataSource>()));

        // The restriction-class binding is E05-T06's; until it registers one, coding derives no class (as in the API).
        services.TryAddSingleton<IRestrictionClassBinding>(NoRestrictionClassBinding.Instance);
        services.TryAddSingleton<ICodingRepository>(sp => new CodingRepository(
            sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IRestrictionClassBinding>()));
        services.TryAddSingleton<IFieldAccessFilter, UnrestrictedFieldAccess>();
        // Grouped propagations (E13-T02) read each member's current duplicate group.
        services.TryAddSingleton<ICodingPropagationRepository>(sp => new CodingPropagationRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddOpportunityAuthorization();
        // The worker host is a WebApplication: once authorization services exist it adds the authorization middleware,
        // which needs the full registration (no endpoint of the worker uses a policy).
        services.AddAuthorization();
        services.AddJobChunkConsumer();
        services.AddJobChunkExecutor<BulkCodingChunkExecutor>();

        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName)))
        {
            services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
            services.AddMessageHandler<JobChunkMessage, JobChunkConsumer>(WorkQueues.BulkCoding);
        }

        return services;
    }
}
