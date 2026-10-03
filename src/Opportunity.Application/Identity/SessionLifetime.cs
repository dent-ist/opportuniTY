namespace Opportunity.Application.Identity;

/// <summary>Session timeouts (ADR-015 D4.2, D3.5).</summary>
/// <param name="Idle">Inactivity after which the session ends (default 30 min).</param>
/// <param name="Absolute">Maximum session age regardless of activity (default 12 h).</param>
/// <param name="PrincipalRefresh">How often the user is re-validated with the IdP (default 15 min).</param>
/// <param name="TouchInterval">
/// Activity is written at most this often, so the idle timeout is enforced to within this precision.
/// </param>
public sealed record SessionTimeouts(TimeSpan Idle, TimeSpan Absolute, TimeSpan PrincipalRefresh, TimeSpan TouchInterval)
{
    public static SessionTimeouts Default { get; } =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(12), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1));
}

public enum SessionStatus
{
    Active,
    IdleExpired,
    AbsoluteExpired,
}

/// <summary>Server-side session lifetime rules. Pure functions of the stored session and the current time.</summary>
public static class SessionLifetime
{
    public static SessionStatus Evaluate(UserSession session, DateTimeOffset now, SessionTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(timeouts);

        if (now >= session.AbsoluteExpiresAt)
        {
            return SessionStatus.AbsoluteExpired;
        }

        return now - session.LastSeenAt >= timeouts.Idle ? SessionStatus.IdleExpired : SessionStatus.Active;
    }

    public static bool NeedsPrincipalRefresh(UserSession session, DateTimeOffset now, SessionTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(timeouts);
        return now - session.PrincipalRefreshedAt >= timeouts.PrincipalRefresh;
    }

    public static bool ShouldTouch(UserSession session, DateTimeOffset now, SessionTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(timeouts);
        return now - session.LastSeenAt >= timeouts.TouchInterval;
    }

    /// <summary>When the session ends if there is no further activity.</summary>
    public static DateTimeOffset ExpiresAt(UserSession session, SessionTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(timeouts);
        var idle = session.LastSeenAt + timeouts.Idle;
        return idle < session.AbsoluteExpiresAt ? idle : session.AbsoluteExpiresAt;
    }

    public static SessionEndReason EndReason(SessionStatus status) => status switch
    {
        SessionStatus.IdleExpired => SessionEndReason.IdleTimeout,
        SessionStatus.AbsoluteExpired => SessionEndReason.AbsoluteTimeout,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "An active session has no end reason."),
    };
}
