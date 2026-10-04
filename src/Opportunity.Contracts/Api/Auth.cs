namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>GET /api/v1/me</c>: the signed-in user as the API sees them. IdP tokens are never returned (ADR-015 D4.1).
/// </summary>
/// <param name="UserId">The opportuniTY user ID.</param>
/// <param name="DisplayName">Display name from the IdP; display only.</param>
/// <param name="Email">Email from the IdP; display only, never used to match users.</param>
/// <param name="Groups">IdP groups from the latest principal refresh.</param>
/// <param name="Mfa">Whether this session satisfies the installation's MFA definition.</param>
/// <param name="SessionExpiresAt">When the session ends without further activity (idle or absolute timeout).</param>
/// <param name="InstallationPermissions">
/// Installation-level permissions the user holds (e.g. <c>Installation.ManageWorkspaces</c>), so the app shows only the
/// actions the API would allow. Display only: every installation endpoint authorizes the call itself.
/// </param>
public sealed record MeResponse(
    string UserId,
    string DisplayName,
    string? Email,
    IReadOnlyList<string> Groups,
    bool Mfa,
    DateTimeOffset? SessionExpiresAt,
    IReadOnlyList<string> InstallationPermissions);

/// <summary>
/// <c>POST /bff/logout</c>: the server session is gone; navigate to <see cref="EndSessionUrl"/> (when not null)
/// to end the IdP session too.
/// </summary>
public sealed record LogoutResponse(string? EndSessionUrl);
