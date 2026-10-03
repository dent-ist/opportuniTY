using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Opportunity.Application.Telemetry;
using Opportunity.Hosting.Health;
using Opportunity.Hosting.Options;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// The one builder every worker host uses (ADR-019 R5). A worker process hosts the worker types named in
/// <c>Workers:Enabled</c> (default supplied by the host project); HTTP is used only for health probes.
/// </summary>
public static class OpportunityWorkerHost
{
    public const string ReadyCheckName = "worker-modules";

    public static WebApplicationBuilder CreateBuilder(string[] args, string defaultEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultEnabled);

        var builder = WebApplication.CreateBuilder(args);

        // Lowest precedence, so appsettings, environment (Workers__Enabled) and command line all override it.
        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource
        {
            InitialData = [new($"{WorkerHostOptions.SectionName}:{nameof(WorkerHostOptions.Enabled)}", defaultEnabled)],
        });

        builder.AddOpportunityHostDefaults();
        return builder;
    }

    /// <summary>Registers the selected worker modules. Call after any host-specific registrations.</summary>
    public static WebApplicationBuilder AddWorkerModules(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddValidatedOptions<WorkerHostOptions>(WorkerHostOptions.SectionName);
        builder.Services.AddSingleton<IValidateOptions<WorkerHostOptions>, WorkerHostOptionsValidator>();

        // Unknown names are ignored here and rejected by the options validator when the host starts.
        var enabled = WorkerHostOptions.Parse(
            builder.Configuration[$"{WorkerHostOptions.SectionName}:{nameof(WorkerHostOptions.Enabled)}"], out _);

        builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<WorkerStatus>(sp, enabled));
        builder.Services.AddSingleton<WorkerTelemetry>();
        builder.Services.AddSingleton<IMessageProcessingMeter>(sp => sp.GetRequiredService<WorkerTelemetry>());
        builder.Services.AddHealthChecks().AddCheck<WorkerModulesReadyCheck>(ReadyCheckName, tags: [HealthTags.Ready]);

        foreach (var type in enabled)
        {
            WorkerModuleCatalog.Modules[type].Register(builder.Services, builder.Configuration);
        }

        return builder;
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var app = builder.Build();
        app.MapOpportunityHostDefaults();
        return app;
    }

    /// <summary>Entire composition root of a worker host: <c>await OpportunityWorkerHost.RunAsync(args, WorkerTypes.Import);</c></summary>
    public static Task RunAsync(string[] args, string defaultEnabled) =>
        Build(CreateBuilder(args, defaultEnabled).AddWorkerModules()).RunAsync();
}
