namespace Opportunity.Application.Identity;

/// <summary>
/// A server-side browser session (ADR-015 D4.1). The browser holds only a random session key; the store keeps its
/// SHA-256 hash, never the key. <see cref="ProtectedTokens"/> is the IdP token set, already encrypted by the caller.
/// </summary>
public sealed record UserSession(
    Guid SessionId,
    Guid UserId,
    string Issuer,
    string Subject,
    string? IdpSessionId,
    string? DisplayName,
    string? Email,
    IReadOnlyList<string> Groups,
    string? Acr,
    IReadOnlyList<string> Amr,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset AbsoluteExpiresAt,
    DateTimeOffset PrincipalRefreshedAt,
    ReadOnlyMemory<byte> ProtectedTokens);

/// <summary>Why a session ended. Stored with the revoked session and used as the audit reason code.</summary>
public enum SessionEndReason
{
    SignOut,
    IdleTimeout,
    AbsoluteTimeout,
    BackChannelLogout,
    PrincipalRefreshFailed,
    Replaced,
}
