using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Opportunity.Hosting.Health;

namespace Opportunity.Hosting;

/// <summary>Composition shared by every host (API and workers).</summary>
public static class OpportunityHost
{
    public static WebApplicationBuilder AddOpportunityHostDefaults(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddOpportunityHealthChecks();
        return builder;
    }

    public static WebApplication MapOpportunityHostDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapOpportunityHealthEndpoints();
        return app;
    }
}
