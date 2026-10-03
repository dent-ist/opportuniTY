using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Opportunity.Api.Conventions;
using Opportunity.Api.Conventions.Idempotency;
using Opportunity.Contracts.Api;
using Opportunity.Hosting.Health;

namespace Opportunity.IntegrationTests.Api;

/// <summary>
/// The real API host plus a test-only endpoint module that exercises the conventions (feature endpoints arrive with
/// their epics) and a controllable readiness dependency standing in for PostgreSQL/OpenSearch/RabbitMQ.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public FakeDependency Dependency { get; } = new();

    public JobCounter Jobs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Health:CheckTimeout", "00:00:01");
        // Search endpoints validate their OpenSearch settings on first use; nothing listens here unless a test overrides it.
        builder.UseSetting("ConnectionStrings:OpenSearch", "http://127.0.0.1:9/");
        builder.UsePlaceholderAuthenticationSettings();
        builder.ConfigureTestServices(services =>
        {
            services.AddTestUserAuthentication();
            services.AddSingleton(Dependency);
            services.AddSingleton(Jobs);
            services.AddSingleton<IApiEndpointModule, ConventionsTestEndpoints>();
            services.AddHealthChecks().AddCheck<FakeDependencyCheck>("fake-dependency", tags: [HealthTags.Ready]);
        });
    }
}

public enum DependencyState
{
    Up,
    Down,
    Hung,
}

public sealed class FakeDependency
{
    public DependencyState State { get; set; } = DependencyState.Up;
}

public sealed class JobCounter
{
    private int _created;

    public int Created => _created;

    /// <summary>The next job creation throws, simulating a server error.</summary>
    public bool FailNext { get; set; }

    public int Next() => Interlocked.Increment(ref _created);
}

internal sealed class FakeDependencyCheck(FakeDependency dependency) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        switch (dependency.State)
        {
            case DependencyState.Down:
                return HealthCheckResult.Unhealthy("connection refused (secret-host:5432)");
            case DependencyState.Hung:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return HealthCheckResult.Healthy();
            default:
                return HealthCheckResult.Healthy();
        }
    }
}

public enum TestJobStatus
{
    Queued,
    InProgress,
}

public sealed record TestJob(string Id, string WorkspaceId, TestJobStatus Status, DateTimeOffset CreatedAt, string Name);

public sealed record CreateTestJob(string Name);

internal sealed class ConventionsTestEndpoints(JobCounter jobs) : IApiEndpointModule
{
    public static readonly DateTimeOffset CreatedAt = new(2026, 10, 2, 16, 3, 22, 123, TimeSpan.FromHours(2));

    public const long CurrentVersion = 3;

    public void MapEndpoints(ApiRouteGroups routes)
    {
        routes.MemberOnly().MapPost("/test-jobs", (string workspaceId, CreateTestJob request) =>
        {
            if (jobs.FailNext)
            {
                jobs.FailNext = false;
                throw new InvalidOperationException("simulated failure");
            }

            var job = new TestJob($"job-{jobs.Next()}", workspaceId, TestJobStatus.Queued, CreatedAt, request.Name);
            return ApiResults.JobAccepted(workspaceId, job.Id, job);
        }).RequireIdempotencyKey();

        routes.MemberOnly().MapGet("/test-items", ([AsParameters] PageQuery page) =>
        {
            if (page.Validate() is { } problem)
            {
                return (IResult)problem;
            }

            var items = Enumerable.Range(1, Math.Min(page.EffectiveLimit, 3)).Select(i => $"item-{i}").ToList();
            return TypedResults.Ok(new CursorPage<string>(items, NextCursor: null, new TotalCount(10_000, TotalRelation.Gte)));
        });

        routes.MemberOnly().MapPut("/test-versioned", (HttpContext context) =>
        {
            if (EntityTags.CheckIfMatch(context.Request, CurrentVersion) is { } problem)
            {
                return (IResult)problem;
            }

            context.Response.Headers.ETag = EntityTags.ForVersion(CurrentVersion + 1);
            return TypedResults.NoContent();
        });

        routes.MemberOnly().MapGet("/test-throws", IResult () =>
            throw new InvalidOperationException("SELECT * FROM secret_table at Opportunity.Secret()"));
    }
}
