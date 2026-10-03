using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Opportunity.Hosting.Options;

namespace Opportunity.Hosting.Health;

/// <summary>
/// Tags that decide which probe runs a health check. Register dependency checks with <see cref="Ready"/>
/// (e.g. <c>AddCheck&lt;PostgresCheck&gt;("postgres", tags: [HealthTags.Ready])</c>); only process-local checks may
/// use <see cref="Live"/>, because a failing liveness probe restarts the container.
/// </summary>
public static class HealthTags
{
    public const string Live = "live";
    public const string Ready = "ready";
}

public sealed class HealthOptions
{
    public const string SectionName = "Health";

    /// <summary>
    /// Applied to every check registered without its own timeout, so a hung dependency fails readiness within the
    /// orchestrator's probe window (E01-T02: "readiness fails within 10 s").
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:10")]
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

public static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Registers the health-check service. Readiness checks are pluggable: each epic adds its own dependency check with
    /// <see cref="HealthTags.Ready"/>. Lag-type signals (index lag, queue depth) are metrics, never readiness checks.
    /// </summary>
    public static IHealthChecksBuilder AddOpportunityHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddValidatedOptions<HealthOptions>(HealthOptions.SectionName);
        services.AddSingleton<IPostConfigureOptions<HealthCheckServiceOptions>, ApplyDefaultCheckTimeout>();
        return services.AddHealthChecks();
    }

    /// <summary>Maps <c>/health/live</c> (process only) and <c>/health/ready</c> (checks tagged ready).</summary>
    public static IEndpointRouteBuilder MapOpportunityHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Probes are anonymous: the API's fallback policy requires a signed-in user everywhere else (ADR-015 D5).
        endpoints.MapHealthChecks(LivePath, Probe(HealthTags.Live)).ExcludeFromDescription().AllowAnonymous();
        endpoints.MapHealthChecks(ReadyPath, Probe(HealthTags.Ready)).ExcludeFromDescription().AllowAnonymous();
        return endpoints;
    }

    private static HealthCheckOptions Probe(string tag) => new()
    {
        Predicate = registration => registration.Tags.Contains(tag),
        ResponseWriter = WriteResponseAsync,
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
        },
    };

    // Names and statuses only: check descriptions and exceptions can carry connection strings or host names.
    private static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        var body = new
        {
            status = Format(report.Status),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = Format(entry.Value.Status),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
            }),
        };

        context.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(context.Response.Body, body, JsonOptions, context.RequestAborted);
    }

    private static string Format(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "healthy",
        HealthStatus.Degraded => "degraded",
        _ => "unhealthy",
    };

    private sealed class ApplyDefaultCheckTimeout(IOptions<HealthOptions> health) : IPostConfigureOptions<HealthCheckServiceOptions>
    {
        public void PostConfigure(string? name, HealthCheckServiceOptions options)
        {
            foreach (var registration in options.Registrations)
            {
                if (registration.Timeout == Timeout.InfiniteTimeSpan)
                {
                    registration.Timeout = health.Value.CheckTimeout;
                }
            }
        }
    }
}
