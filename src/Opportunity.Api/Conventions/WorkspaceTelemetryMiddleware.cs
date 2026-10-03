using System.Diagnostics;
using Opportunity.Application.Telemetry;

namespace Opportunity.Api.Conventions;

/// <summary>
/// Adds the route's workspace ID to the request span (<c>opportunity.workspace_id</c>) and the logging scope
/// (<c>WorkspaceId</c>), ADR-017 R5. Runs after routing. Only well-formed GUIDs are recorded, so a junk route value
/// never reaches telemetry.
/// </summary>
internal sealed class WorkspaceTelemetryMiddleware(RequestDelegate next, ILogger<WorkspaceTelemetryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetRouteValue(ApiRoutes.WorkspaceIdParameter) is not string raw || !Guid.TryParse(raw, out var workspaceId))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var value = workspaceId.ToString();
        Activity.Current?.SetTag(TelemetryAttributes.WorkspaceId, value);
        using (logger.BeginScope(new Dictionary<string, object> { [LogScopeKeys.WorkspaceId] = value }))
        {
            await next(context).ConfigureAwait(false);
        }
    }
}
