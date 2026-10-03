namespace Opportunity.Application.Identity;

/// <summary>
/// The user as asserted by the installation's OIDC provider (ADR-015 D3). (<see cref="Issuer"/>, <see cref="Subject"/>)
/// is the identity; <see cref="DisplayName"/> and <see cref="Email"/> are display attributes and are never matched.
/// </summary>
public sealed record ExternalIdentity(
    string Issuer,
    string Subject,
    string? DisplayName,
    string? Email,
    IReadOnlyList<string> Groups);

/// <summary>How the user authenticated at the IdP: <c>acr</c>, <c>amr</c> and the IdP session (<c>sid</c>).</summary>
public sealed record AuthenticationContext(string? Acr, IReadOnlyList<string> Amr, string? IdpSessionId)
{
    public static AuthenticationContext None { get; } = new(null, [], null);
}
