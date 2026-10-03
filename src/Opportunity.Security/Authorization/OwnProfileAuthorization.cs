using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Opportunity.Security.Authentication;

namespace Opportunity.Security.Authorization;

/// <summary>
/// Policy for installation-level resources that belong to the caller alone (<c>/api/v1/me/*</c>, e.g. UI preferences):
/// a signed-in user with an opportuniTY user id. The resource is always addressed through that id from the session,
/// never through a route or body value, so one user can never read or change another user's resource and there is
/// no workspace permission to check (ADR-015 D5.4: explicit policy for non-workspace endpoints).
/// </summary>
public static class OwnProfileAuthorization
{
    public const string PolicyName = "OwnProfile";

    public static AuthorizationPolicy Policy { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => TryGetUserId(context.User, out _))
        .Build();

    public static TBuilder RequireOwnProfile<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.RequireAuthorization(PolicyName);
    }

    /// <summary>The caller's user id; only call from endpoints that declare <see cref="RequireOwnProfile{TBuilder}"/>.</summary>
    public static Guid GetOwnUserId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryGetUserId(context.User, out var id)
            ? id
            : throw new InvalidOperationException("The endpoint does not declare the OwnProfile policy.");
    }

    private static bool TryGetUserId(System.Security.Claims.ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;
        return user.Identity?.IsAuthenticated == true
            && Guid.TryParse(user.FindFirst(OpportunityClaimTypes.UserId)?.Value, out userId)
            && userId != Guid.Empty;
    }
}
