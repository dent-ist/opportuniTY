using Opportunity.Application.Fields;
using Opportunity.Application.Search.Indexing;

namespace Opportunity.Api.Workspaces;

/// <summary>
/// What a workspace needs before documents can be loaded and searched: the system fields and default coding layout
/// (<see cref="IFieldCatalogRepository.InitializeWorkspaceAsync"/>) and, when search is configured, its index placement
/// (<see cref="IWorkspaceSearchPlacement.PlaceAsync"/>). Both are idempotent. Creation runs both; an import ensures the
/// fields again (repairing workspaces created without them, e.g. the developer seed), and resolving the write placement
/// places a workspace on its first index write.
/// </summary>
internal static partial class WorkspaceProvisioning
{
    public static Task EnsureFieldsAsync(IServiceProvider services, Guid workspaceId, CancellationToken cancellationToken) =>
        services.GetRequiredService<IFieldCatalogRepository>().InitializeWorkspaceAsync(workspaceId, cancellationToken);

    /// <summary>At creation the workspace already exists: a search outage is logged, and the first index write places it.</summary>
    public static async Task EnsureAfterCreateAsync(IServiceProvider services, Guid workspaceId, ILogger logger, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<IFieldCatalogRepository>().InitializeWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (services.GetService<IWorkspaceSearchPlacement>() is not { } search)
        {
            return;
        }

        try
        {
            await search.PlaceAsync(workspaceId, WorkspacePlacementRequest.Default, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException)
        {
            LogPlacementDeferred(logger, workspaceId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search placement of new workspace {WorkspaceId} failed; its first index write places it.")]
    private static partial void LogPlacementDeferred(ILogger logger, Guid workspaceId, Exception exception);
}
