namespace Opportunity.Application.Identity;

/// <summary>
/// Server-side session store (ADR-015 D4.1: PostgreSQL, no Redis per baseline §16). Sessions are looked up by the
/// SHA-256 hash of the browser's session key. Revoked and ended sessions are never returned.
/// </summary>
public interface ISessionStore
{
    Task CreateAsync(ReadOnlyMemory<byte> keyHash, UserSession session, CancellationToken cancellationToken = default);

    Task<UserSession?> FindActiveAsync(ReadOnlyMemory<byte> keyHash, CancellationToken cancellationToken = default);

    /// <summary>Records activity for the idle timeout.</summary>
    Task TouchAsync(Guid sessionId, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>Stores the result of a principal refresh (D3.5): groups and the new token set.</summary>
    Task UpdatePrincipalAsync(
        Guid sessionId,
        IReadOnlyList<string> groups,
        ReadOnlyMemory<byte> protectedTokens,
        DateTimeOffset refreshedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes one session; false when it was already revoked or does not exist.</summary>
    Task<bool> RevokeAsync(Guid sessionId, SessionEndReason reason, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Revokes every active session created from the IdP session <paramref name="idpSessionId"/> (OIDC <c>sid</c>).</summary>
    Task<IReadOnlyList<UserSession>> RevokeByIdpSessionAsync(
        string issuer, string idpSessionId, SessionEndReason reason, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Revokes every active session of the user (<c>iss</c>, <c>sub</c>).</summary>
    Task<IReadOnlyList<UserSession>> RevokeBySubjectAsync(
        string issuer, string subject, SessionEndReason reason, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Deletes sessions that ended (revoked, or past their absolute expiry) before <paramref name="endedBefore"/>.</summary>
    Task<int> DeleteEndedAsync(DateTimeOffset endedBefore, CancellationToken cancellationToken = default);
}
