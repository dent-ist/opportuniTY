using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Jobs;
using Opportunity.Rendering.Renderers;
using Opportunity.Rendering.Sandboxing;

namespace Opportunity.Rendering.Jobs;

public static class RenderJobRegistration
{
    /// <summary>
    /// The render worker's job side: the <see cref="IRenderer"/>, the coordinator and the <see cref="RenderChunkExecutor"/>.
    /// With <paramref name="sandbox"/> enabled (the worker's default, E11-T03) every document renders in a sandboxed
    /// child process (<see cref="SandboxedRenderer"/>); without it, in this process (<see cref="RasterRenderer"/>). The
    /// host registers the render store, job stores, object storage and the chunk consumer.
    /// </summary>
    public static IServiceCollection AddRenderJobs(
        this IServiceCollection services, RenderJobOptions? options = null, RenderSettings? settings = null, bool runCoordinator = true,
        RenderSandboxOptions? sandbox = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new RenderJobOptions());
        if (sandbox is { Enabled: true })
        {
            sandbox.Validate();
            services.TryAddSingleton<IRenderer>(sp => new SandboxedRenderer(settings, sandbox, sp.GetRequiredService<ILogger<SandboxedRenderer>>()));
        }
        else
        {
            services.TryAddSingleton<IRenderer>(_ => new RasterRenderer(settings));
        }

        services.TryAddScoped<RenderCoordinator>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobChunkExecutor, RenderChunkExecutor>());
        if (runCoordinator)
        {
            services.AddHostedService<RenderCoordinatorService>();
        }

        return services;
    }
}
