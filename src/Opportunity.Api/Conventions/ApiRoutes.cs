namespace Opportunity.Api.Conventions;

/// <summary>
/// Route groups every endpoint hangs off (ADR-019 §2.2–2.3). <see cref="Workspace"/> is
/// <c>/api/v1/workspaces/{workspaceId}</c>; the workspace authorization filter (E05) is attached to it once, so no
/// workspace-scoped endpoint can skip it.
/// </summary>
public sealed record ApiRouteGroups(RouteGroupBuilder V1, RouteGroupBuilder Workspace);

/// <summary>A feature's endpoints. Register implementations in DI; <see cref="ApiRoutes.MapApiV1"/> maps them all.</summary>
public interface IApiEndpointModule
{
    void MapEndpoints(ApiRouteGroups routes);
}

public static class ApiRoutes
{
    public const string V1Prefix = "/api/v1";
    public const string WorkspaceIdParameter = "workspaceId";

    public static ApiRouteGroups MapApiV1(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup(V1Prefix);
        var workspace = v1.MapGroup($"/workspaces/{{{WorkspaceIdParameter}}}");
        var routes = new ApiRouteGroups(v1, workspace);

        foreach (var module in app.Services.GetServices<IApiEndpointModule>())
        {
            module.MapEndpoints(routes);
        }

        return routes;
    }
}
