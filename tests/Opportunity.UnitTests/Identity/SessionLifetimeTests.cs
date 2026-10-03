using AwesomeAssertions;

using Opportunity.Application.Identity;

namespace Opportunity.UnitTests.Identity;

public class SessionLifetimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);
    private static readonly SessionTimeouts Timeouts = SessionTimeouts.Default;

    private static UserSession Session(DateTimeOffset? lastSeen = null, DateTimeOffset? refreshed = null) => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "https://idp.test", "sub-1", "sid-1", "Alice", null, ["reviewers"], null, [],
        CreatedAt: Start,
        LastSeenAt: lastSeen ?? Start,
        AbsoluteExpiresAt: Start + Timeouts.Absolute,
        PrincipalRefreshedAt: refreshed ?? Start,
        ProtectedTokens: ReadOnlyMemory<byte>.Empty);

    [Fact]
    public void Defaults_match_adr_015()
    {
        Timeouts.Idle.Should().Be(TimeSpan.FromMinutes(30));
        Timeouts.Absolute.Should().Be(TimeSpan.FromHours(12));
        Timeouts.PrincipalRefresh.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData(0, SessionStatus.Active)]
    [InlineData(29, SessionStatus.Active)]
    [InlineData(30, SessionStatus.IdleExpired)]
    public void Idle_timeout_counts_from_the_last_activity(int idleMinutes, SessionStatus expected)
    {
        var session = Session(lastSeen: Start.AddHours(2));

        SessionLifetime.Evaluate(session, Start.AddHours(2).AddMinutes(idleMinutes), Timeouts).Should().Be(expected);
    }

    [Fact]
    public void Absolute_timeout_wins_over_recent_activity()
    {
        var session = Session(lastSeen: Start.AddHours(12).AddMinutes(-1));

        SessionLifetime.Evaluate(session, Start.AddHours(12), Timeouts).Should().Be(SessionStatus.AbsoluteExpired);
        SessionLifetime.EndReason(SessionStatus.AbsoluteExpired).Should().Be(SessionEndReason.AbsoluteTimeout);
        SessionLifetime.EndReason(SessionStatus.IdleExpired).Should().Be(SessionEndReason.IdleTimeout);
    }

    [Fact]
    public void Principal_refresh_is_due_after_the_interval()
    {
        var session = Session(refreshed: Start);

        SessionLifetime.NeedsPrincipalRefresh(session, Start.AddMinutes(14), Timeouts).Should().BeFalse();
        SessionLifetime.NeedsPrincipalRefresh(session, Start.AddMinutes(15), Timeouts).Should().BeTrue();
    }

    [Fact]
    public void Activity_writes_are_throttled()
    {
        var session = Session(lastSeen: Start);

        SessionLifetime.ShouldTouch(session, Start.AddSeconds(30), Timeouts).Should().BeFalse();
        SessionLifetime.ShouldTouch(session, Start.AddMinutes(1), Timeouts).Should().BeTrue();
    }

    [Fact]
    public void Expiry_is_the_earlier_of_idle_and_absolute()
    {
        SessionLifetime.ExpiresAt(Session(lastSeen: Start), Timeouts).Should().Be(Start.AddMinutes(30));
        SessionLifetime.ExpiresAt(Session(lastSeen: Start.AddHours(11).AddMinutes(50)), Timeouts).Should().Be(Start.AddHours(12));
    }
}

public class MfaPolicyTests
{
    private static readonly MfaPolicy Policy = new(["urn:mfa", "gold"], ["otp", "hwk"]);

    [Theory]
    [InlineData("gold", new string[0], true)]
    [InlineData("urn:password", new[] { "pwd", "otp" }, true)]
    [InlineData("urn:password", new[] { "pwd" }, false)]
    [InlineData(null, new string[0], false)]
    [InlineData("GOLD", new string[0], false)]
    public void Acr_or_amr_satisfies_mfa(string? acr, string[] amr, bool expected) =>
        Policy.IsSatisfiedBy(acr, amr).Should().Be(expected);

    [Fact]
    public void An_unconfigured_policy_is_never_satisfied()
    {
        var none = new MfaPolicy([], []);

        none.IsConfigured.Should().BeFalse();
        none.IsSatisfiedBy("anything", ["otp"]).Should().BeFalse();
    }
}
