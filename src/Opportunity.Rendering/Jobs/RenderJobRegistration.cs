using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Jobs;
using Opportunity.Rendering.Renderers;

namespace Opportunity.Rendering.Jobs;

public static class RenderJobRegistration
{
    /// <summary>
    /// The render worker's job side: the in-process <see cref="RasterRenderer"/> as <see cref="IRenderer"/> (E11-T03
    /// replaces it with a sandboxed one), the coordinator and the <see cref="RenderChunkExecutor"/>. The host registers
    /// the render store, job stores, object storage and the chunk consumer.
    /// </summary>
    public static IServiceCollection AddRenderJobs(
        this IServiceCollection services, RenderJobOptions? options = null, RenderSettings? settings = null, bool runCoordinator = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new RenderJobOptions());
        services.TryAddSingleton<IRenderer>(_ => new RasterRenderer(settings));
        services.TryAddScoped<RenderCoordinator>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobChunkExecutor, RenderChunkExecutor>());
        if (runCoordinator)
        {
            services.AddHostedService<RenderCoordinatorService>();
        }

        return services;
    }
}
