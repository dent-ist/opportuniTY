using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Authorization;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Core.Workspaces;
using Opportunity.Data.Workspaces;
using Opportunity.Security.Authorization;

namespace Opportunity.Api.Workspaces;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}</c> with the caller's effective permissions (E05-T02). The workspace list
/// (<c>GET /api/v1/workspaces</c>) needs a cross-workspace membership lookup and is not part of this module yet.
/// </summary>
public sealed class WorkspaceEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet(string.Empty, GetWorkspaceAsync)
            .WithName("GetWorkspace")
            .WithTags("Workspaces")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("The workspace and the current user's effective permissions in it.")
            .WithDescription("A workspace the user is not a member of returns 404, exactly like one that does not exist.")
            .RequireWorkspaceMember();
    }

    internal static async Task<Results<Ok<WorkspaceResource>, ProblemHttpResult>> GetWorkspaceAsync(
        string workspaceId, HttpContext context, IWorkspaceReader workspaces, IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access)
        {
            return Problems.NotFound();
        }

        var effective = await authorization.GetEffectivePermissionsAsync(access.Principal, access.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (!effective.Decision.IsAllowed
            || await workspaces.GetAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false) is not { } workspace)
        {
            return Problems.NotFound();
        }

        return TypedResults.Ok(new WorkspaceResource(
            workspace.WorkspaceId,
            workspace.Name,
            workspace.MatterNumber,
            workspace.DisplayTimeZone,
            workspace.Status == WorkspaceStatus.Active ? WorkspaceResourceStatus.Active : WorkspaceResourceStatus.Closed,
            workspace.CreatedAt,
            [.. effective.Permissions.Select(p => p.Name())],
            effective.BreakGlass));
    }
}

public static class WorkspaceEndpointRegistration
{
    public static IServiceCollection AddWorkspaceEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IWorkspaceReader, WorkspaceReader>();
        services.AddSingleton<IApiEndpointModule, WorkspaceEndpoints>();
        return services;
    }
}
