using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Opportunity.Application.Telemetry;
using Opportunity.Hosting.Options;
using Opportunity.Hosting.Workers;

namespace Opportunity.Hosting.Telemetry;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>Standard OpenTelemetry variable; its presence switches export on (ADR-017 R1).</summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>Standard OpenTelemetry variable; overrides the derived <c>service.name</c>.</summary>
    public const string ServiceNameKey = "OTEL_SERVICE_NAME";

    /// <summary>
    /// <c>null</c> (default): the SDK is registered only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set. <c>true</c>
    /// registers it without an OTLP exporter (tests add in-memory exporters); <c>false</c> forces it off.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>Records <c>db.query.text</c> (parameterized SQL, never parameter values). Local debugging only.</summary>
    public bool RecordSqlStatements { get; set; }

    /// <summary>Distinct workspace IDs a process reports as metric attribute values before folding into <c>other</c>.</summary>
    [Range(1, 1000)]
    public int MaxWorkspaceAttributeValues { get; set; } = BoundedAttributeValues.DefaultCapacity;
}

/// <summary>
/// OpenTelemetry for every host (ADR-017): traces, metrics and logs over OTLP, with the attribute allow-list
/// (<see cref="TelemetryAttributePolicy"/>) applied before export. Off unless <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is
/// set; the exporter reads the other standard <c>OTEL_*</c> variables (protocol, headers, export interval, sampler).
/// </summary>
public static class OpportunityTelemetryRegistration
{
    public const string ServiceNamespace = "opportunity";

    /// <summary>Request start/finish logs of this category contain the raw query string (search text).</summary>
    public const string HostingDiagnosticsCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public static WebApplicationBuilder AddOpportunityTelemetry(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new();
        builder.Services.AddValidatedOptions<TelemetryOptions>(TelemetryOptions.SectionName);
        builder.Services.TryAddSingleton(sp => new OpportunityMetrics(
            sp.GetRequiredService<IMeterFactory>(), options.MaxWorkspaceAttributeValues));

        // ASP.NET Core takes its propagator from DI, HttpClient from the static; both drop baggage.
        TraceContextOnlyPropagator.Install();
        builder.Services.Replace(ServiceDescriptor.Singleton(DistributedContextPropagator.Current));

        // trace_id/span_id on every log record (console JSON scopes and OTLP), whether or not export is on.
        builder.Logging.Configure(o => o.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        builder.Logging.AddFilter(HostingDiagnosticsCategory, LogLevel.Warning);

        var endpoint = builder.Configuration[TelemetryOptions.OtlpEndpointKey];
        var exportOtlp = !string.IsNullOrWhiteSpace(endpoint);
        if (!(options.Enabled ?? exportOtlp))
        {
            return builder;
        }

        var serviceName = builder.Configuration[TelemetryOptions.ServiceNameKey] is { Length: > 0 } configured
            ? configured
            : ServiceNameFor(builder.Environment.ApplicationName);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName,
                    ServiceNamespace,
                    ServiceVersion(),
                    autoGenerateServiceInstanceId: false,
                    serviceInstanceId: $"{Environment.MachineName}-{Environment.ProcessId}")
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .AddSource(OpportunityTelemetry.ActivitySourceName, "Npgsql")
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = context => !IsProbe(context);
                    o.RecordException = false;
                })
                .AddHttpClientInstrumentation(o => o.RecordException = false)
                .AddProcessor(new TelemetryScrubbingProcessor(options.RecordSqlStatements)))
            .WithMetrics(metrics => metrics
                .AddMeter(OpportunityTelemetry.MeterName, WorkerStatus.MeterName, "Npgsql", "System.Runtime")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithLogging(static _ => { }, o =>
            {
                o.IncludeScopes = true;
                o.IncludeFormattedMessage = true;
            });

        if (exportOtlp)
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary><c>Opportunity.Worker.Indexing</c> → <c>opportunity-worker-indexing</c>.</summary>
    public static string ServiceNameFor(string applicationName) =>
        string.IsNullOrWhiteSpace(applicationName)
            ? "opportunity"
            : applicationName.Replace('.', '-').ToLowerInvariant();

    private static bool IsProbe(HttpContext context) => context.Request.Path.StartsWithSegments("/health");

    private static string ServiceVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(OpportunityTelemetryRegistration).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        return version;
    }
}
