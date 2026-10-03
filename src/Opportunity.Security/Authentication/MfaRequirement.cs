using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Opportunity.Application.Identity;
using Opportunity.Contracts.Api;

namespace Opportunity.Security.Authentication;

/// <summary>
/// MFA enforcement (ADR-015 D3.6). <see cref="RequireWorkspaceMfa{TBuilder}"/> goes on the workspace route group and
/// applies when the workspace requires MFA; <see cref="RequireMfa{TBuilder}"/> is for endpoints that always need it
/// (installation administration, break-glass activation). An unsatisfied requirement is a 403
/// <c>step-up-required</c> problem whose <c>stepUpUrl</c> starts an OIDC re-authentication with the MFA <c>acr</c>.
/// </summary>
public static class MfaEndpointConventions
{
    public const string WorkspaceIdRouteValue = "workspaceId";
    public const string StepUpPath = "/bff/login?stepUp=true";

    public static TBuilder RequireWorkspaceMfa<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEndpointFilter(new MfaRequirementFilter(always: false));
        return builder;
    }

    public static TBuilder RequireMfa<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEndpointFilter(new MfaRequirementFilter(always: true));
        return builder;
    }

    /// <summary>True when the principal's <c>acr</c>/<c>amr</c> satisfy the installation's MFA definition.</summary>
    public static bool HasMfa(this ClaimsPrincipal principal, MfaPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(policy);
        return policy.IsSatisfiedBy(
            principal.FindFirst(OpportunityClaimTypes.Acr)?.Value,
            principal.FindAll(OpportunityClaimTypes.Amr).Select(c => c.Value));
    }
}

internal sealed class MfaRequirementFilter(bool always) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        Guid? workspaceId = http.GetRouteValue(MfaEndpointConventions.WorkspaceIdRouteValue) is string raw && Guid.TryParse(raw, out var id)
            ? id
            : null;

        var required = always
            || (workspaceId is { } ws
                && await http.RequestServices.GetRequiredService<IWorkspaceAuthenticationPolicy>()
                    .RequiresMfaAsync(ws, http.RequestAborted).ConfigureAwait(false));
        if (!required)
        {
            return await next(context).ConfigureAwait(false);
        }

        var policy = http.RequestServices.GetRequiredService<IOptions<OpportunityAuthenticationOptions>>().Value.Mfa.ToPolicy();
        if (http.User.HasMfa(policy))
        {
            return await next(context).ConfigureAwait(false);
        }

        if (http.GetUserSession() is { } session)
        {
            await http.RequestServices.GetRequiredService<AuthenticationAudit>()
                .DeniedAsync(http, session, workspaceId, "MfaRequired").ConfigureAwait(false);
        }

        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            detail: "Multi-factor authentication is required. Re-authenticate through stepUpUrl.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = ProblemCodes.StepUpRequired,
                ["stepUpUrl"] = MfaEndpointConventions.StepUpPath,
            });
    }
}
