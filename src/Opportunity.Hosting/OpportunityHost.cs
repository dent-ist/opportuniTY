using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Opportunity.Application.Keys;
using Opportunity.Data.Keys;
using Opportunity.Hosting.Health;
using Opportunity.Hosting.Telemetry;
using Opportunity.Security.Keys;

namespace Opportunity.Hosting;

/// <summary>Composition shared by every host (API and workers).</summary>
public static class OpportunityHost
{
    public static WebApplicationBuilder AddOpportunityHostDefaults(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Docker/Compose secrets through the *_FILE convention (ADR-015 D10.1), e.g. ConnectionStrings__App_FILE.
        builder.Configuration.AddInMemoryCollection(SecretFileConvention.Resolve(Environment.GetEnvironmentVariables()));

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.AddOpportunityTelemetry();
        builder.Services.AddOpportunityHealthChecks();
        builder.Services.AddOpportunityPostgres();
        builder.Services.AddPostgresWorkspaceDataKeys();
        builder.Services.AddOpportunityKeyManagement();
        return builder;
    }

    public static WebApplication MapOpportunityHostDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapOpportunityHealthEndpoints();
        return app;
    }
}
