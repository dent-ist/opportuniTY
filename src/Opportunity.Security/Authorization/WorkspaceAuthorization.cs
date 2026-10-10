using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Authorization;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Security.Authentication;
using Opportunity.Security.Http;

namespace Opportunity.Security.Authorization;

/// <summary>The workspace permission an endpoint requires (PEP-1, ADR-015 D5.4). The last one added wins.</summary>
public sealed record RequiredPermissionMetadata(Permission Permission);

/// <summary>
/// The endpoint checks a resource-dependent permission itself through <see cref="IAuthorizationService"/> (e.g. a job is
/// visible to its initiator or with <c>Job.ViewAll</c>); PEP-1 enforces workspace membership only.
/// </summary>
public sealed class WorkspaceMembershipMetadata
{
    public static WorkspaceMembershipMetadata Instance { get; } = new();
}

/// <summary>
/// The endpoint is for holders of a BreakGlass role assignment (Q-45 activation), who are not members until they
/// activate: PEP-1 asks the PDP for the assignment instead of membership; other members get 403, anyone else the non-member 404.
/// </summary>
public sealed class BreakGlassHolderMetadata
{
    public static BreakGlassHolderMetadata Instance { get; } = new();
}

/// <summary>The outcome of PEP-1 for the current request, for handlers that authorize resources further.</summary>
public sealed record WorkspaceAccess(Guid WorkspaceId, SecurityPrincipal Principal);

public static class WorkspaceAuthorizationConventions
{
    public const string WorkspaceIdRouteValue = "workspaceId";

    /// <summary>Requires <paramref name="permission"/> in the route's workspace; non-members get 404, members without it 403.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Permission permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        _ = PermissionCatalog.Get(permission);
        builder.Add(endpoint => endpoint.Metadata.Add(new RequiredPermissionMetadata(permission)));
        return builder;
    }

    /// <summary>Requires workspace membership only; the handler must authorize the resource itself.</summary>
    public static TBuilder RequireWorkspaceMember<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(WorkspaceMembershipMetadata.Instance));
        return builder;
    }

    /// <summary>Requires a BreakGlass role assignment in the route's workspace (not membership); other members 403, others 404.</summary>
    public static TBuilder RequireBreakGlassHolder<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(BreakGlassHolderMetadata.Instance);
        return builder;
    }

    /// <summary>True when the endpoint's route has a <c>{workspaceId}</c> parameter, wherever it was mapped.</summary>
    public static bool IsWorkspaceScoped(Endpoint endpoint) =>
        endpoint is RouteEndpoint route && route.RoutePattern.GetParameter(WorkspaceIdRouteValue) is not null;

    public static WorkspaceAccess? GetWorkspaceAccess(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<WorkspaceAccess>();
    }

    /// <summary>The request principal for PDP calls. <see cref="SecurityPrincipal.UserId"/> is empty when anonymous.</summary>
    public static SecurityPrincipal ToSecurityPrincipal(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.User;
        var userId = user.Identity?.IsAuthenticated == true
            && Guid.TryParse(user.FindFirst(OpportunityClaimTypes.UserId)?.Value, out var id)
                ? id
                : Guid.Empty;
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return new SecurityPrincipal
        {
            UserId = userId,
            DisplayName = user.Identity?.Name ?? user.FindFirst(OpportunityClaimTypes.Subject)?.Value ?? "anonymous",
            Groups = [.. user.FindAll(OpportunityClaimTypes.Group).Select(c => c.Value).Where(g => g.Length > 0).Distinct(StringComparer.Ordinal)],
            ClientIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent[..Math.Min(userAgent.Length, 512)],
            CorrelationId = RequestCorrelation.Get(context),
        };
    }
}

/// <summary>
/// PEP-1 (ADR-015 D5.4): for every endpoint with a <c>{workspaceId}</c> route value, after authentication and before
/// model binding, idempotency and endpoint filters. A malformed ID, an unknown workspace and a workspace the user is
/// not a member of all give the same 404 (no enumeration); a member lacking the endpoint's permission gets 403. An
/// endpoint that declares neither a permission nor membership-only is refused (fail closed; the architecture test
/// keeps that from shipping).
/// </summary>
internal sealed partial class WorkspaceAuthorizationMiddleware(RequestDelegate next, ILogger<WorkspaceAuthorizationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problems)
    {
        // Resolved only for workspace routes, so requests elsewhere (health, OIDC) never need the database.
        var endpoint = context.GetEndpoint();
        if (endpoint is null || !WorkspaceAuthorizationConventions.IsWorkspaceScoped(endpoint)
            || context.User.Identity?.IsAuthenticated != true)
        {
            // Anonymous requests to workspace routes never get here: the fallback policy answers 401 first.
            await next(context).ConfigureAwait(false);
            return;
        }

        var principal = context.ToSecurityPrincipal();
        if (context.GetRouteValue(WorkspaceAuthorizationConventions.WorkspaceIdRouteValue) is not string raw
            || !Guid.TryParse(raw, out var workspaceId) || workspaceId == Guid.Empty)
        {
            await WriteAsync(context, problems, AuthorizationDecision.NotFound(AuthorizationReasons.WorkspaceNotFound)).ConfigureAwait(false);
            return;
        }

        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        AuthorizationDecision decision;
        if (endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>() is { } required)
        {
            decision = await authorization.AuthorizeAsync(principal, workspaceId, required.Permission, context.RequestAborted).ConfigureAwait(false);
        }
        else if (endpoint.Metadata.GetMetadata<BreakGlassHolderMetadata>() is not null)
        {
            decision = await authorization.AuthorizeBreakGlassHolderAsync(principal, workspaceId, context.RequestAborted).ConfigureAwait(false);
        }
        else if (endpoint.Metadata.GetMetadata<WorkspaceMembershipMetadata>() is not null)
        {
            decision = await authorization.AuthorizeMembershipAsync(principal, workspaceId, context.RequestAborted).ConfigureAwait(false);
        }
        else
        {
            LogUndeclared(logger, endpoint.DisplayName);
            decision = AuthorizationDecision.Deny("EndpointPolicyMissing");
        }

        if (!decision.IsAllowed)
        {
            await WriteAsync(context, problems, decision).ConfigureAwait(false);
            return;
        }

        context.Features.Set(new WorkspaceAccess(workspaceId, principal));
        await next(context).ConfigureAwait(false);
    }

    internal static async Task WriteAsync(HttpContext context, IProblemDetailsService problems, AuthorizationDecision decision)
    {
        var (status, code, detail) = decision.Outcome == AuthorizationOutcome.NotFound
            ? (StatusCodes.Status404NotFound, ProblemCodes.NotFound, "The resource does not exist.")
            : (StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "You do not have permission for this operation.");
        context.Response.StatusCode = status;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = status, Detail = detail, Extensions = { ["code"] = code } },
        }).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Workspace endpoint {Endpoint} declares no permission; request refused.")]
    private static partial void LogUndeclared(ILogger logger, string? endpoint);
}

public static class AuthorizationResults
{
    /// <summary>The 404 or 403 problem for a non-allow decision, identical to PEP-1's (the reason is never disclosed).</summary>
    public static ProblemHttpResult Problem(AuthorizationDecision decision) =>
        decision.Outcome == AuthorizationOutcome.NotFound
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, detail: "The resource does not exist.",
                extensions: new Dictionary<string, object?> { ["code"] = ProblemCodes.NotFound })
            : TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "You do not have permission for this operation.",
                extensions: new Dictionary<string, object?> { ["code"] = ProblemCodes.Forbidden });
}
