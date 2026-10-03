using System.Security.Claims;
using System.Text.Json;

using Opportunity.Application.Identity;

namespace Opportunity.Security.Authentication;

/// <summary>Maps IdP claims to the opportuniTY identity and request principal (ADR-015 D3.3, D3.4).</summary>
internal static class ClaimMapping
{
    /// <summary>Upper bound on stored groups; larger sets need the directory lookup adapter (D3.4, e.g. Entra overage).</summary>
    public const int MaxGroups = 1000;

    public static IReadOnlyList<string> Groups(IEnumerable<Claim> claims, string groupsClaim) =>
        Normalize(claims.Where(c => c.Type == groupsClaim).Select(c => c.Value));

    public static IReadOnlyList<string> Groups(JsonElement userInfo, string groupsClaim)
    {
        if (!userInfo.TryGetProperty(groupsClaim, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => Normalize(value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)),
            JsonValueKind.String => Normalize([value.GetString()!]),
            _ => [],
        };
    }

    public static IReadOnlyList<string> Values(IEnumerable<Claim> claims, string type) =>
        [.. claims.Where(c => c.Type == type).Select(c => c.Value).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The identity from a validated ID token principal (MapInboundClaims off, so claim types are the JWT names).
    /// Returns null when <c>sub</c> or <c>iss</c> is missing.
    /// </summary>
    public static (ExternalIdentity Identity, AuthenticationContext Context)? FromIdToken(ClaimsPrincipal principal, string groupsClaim)
    {
        var subject = principal.FindFirst(OpportunityClaimTypes.Subject);
        if (subject is null || string.IsNullOrEmpty(subject.Value))
        {
            return null;
        }

        // The handler keeps "iss" as a claim (see OidcConfiguration); every claim also records its validated issuer.
        var issuer = principal.FindFirst(OpportunityClaimTypes.Issuer)?.Value ?? subject.Issuer;
        if (string.IsNullOrEmpty(issuer))
        {
            return null;
        }

        var claims = principal.Claims.ToList();
        var displayName = First(claims, "name") ?? First(claims, "preferred_username");
        var identity = new ExternalIdentity(issuer, subject.Value, displayName, First(claims, "email"), Groups(claims, groupsClaim));
        var context = new AuthenticationContext(First(claims, OpportunityClaimTypes.Acr), Values(claims, OpportunityClaimTypes.Amr), First(claims, "sid"));
        return (identity, context);
    }

    /// <summary>The request principal for an active session.</summary>
    public static ClaimsPrincipal ToPrincipal(UserSession session, string authenticationType)
    {
        var claims = new List<Claim>
        {
            new(OpportunityClaimTypes.UserId, session.UserId.ToString()),
            new(OpportunityClaimTypes.SessionId, session.SessionId.ToString()),
            new(OpportunityClaimTypes.Issuer, session.Issuer),
            new(OpportunityClaimTypes.Subject, session.Subject),
            new(OpportunityClaimTypes.Name, session.DisplayName ?? session.Subject),
        };
        if (session.Email is not null)
        {
            claims.Add(new Claim(OpportunityClaimTypes.Email, session.Email));
        }

        if (session.Acr is not null)
        {
            claims.Add(new Claim(OpportunityClaimTypes.Acr, session.Acr));
        }

        claims.AddRange(session.Amr.Select(value => new Claim(OpportunityClaimTypes.Amr, value)));
        claims.AddRange(session.Groups.Select(group => new Claim(OpportunityClaimTypes.Group, group)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, OpportunityClaimTypes.Name, OpportunityClaimTypes.Group));
    }

    private static string? First(List<Claim> claims, string type) =>
        claims.FirstOrDefault(c => c.Type == type && c.Value.Length > 0)?.Value;

    private static string[] Normalize(IEnumerable<string> groups) =>
        [.. groups.Where(g => g.Length is > 0 and <= 1024).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaxGroups)];
}
