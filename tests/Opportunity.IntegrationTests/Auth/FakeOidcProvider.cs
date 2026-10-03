using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Opportunity.IntegrationTests.Auth;

/// <summary>A user known to <see cref="FakeOidcProvider"/>. Mutable so tests can change groups or disable the account.</summary>
public sealed class FakeIdpUser(string subject, string name, params string[] groups)
{
    public string Subject { get; } = subject;

    public string Name { get; } = name;

    public string Email => $"{Subject}@example.test";

    public List<string> Groups { get; set; } = [.. groups];

    public bool Disabled { get; set; }

    /// <summary><c>acr</c> asserted on the next authorization (e.g. after MFA).</summary>
    public string Acr { get; set; } = "urn:test:password";

    public List<string> Amr { get; set; } = ["pwd"];
}

/// <summary>
/// A small standards-shaped OIDC provider for integration tests, served as the OIDC handler's back-channel
/// <see cref="HttpMessageHandler"/>: discovery, JWKS (RSA, RS256), authorization code + PKCE (S256 verified), refresh
/// tokens (rejected once the user is disabled), userinfo, RFC 7009 revocation and back-channel logout tokens. The
/// browser leg (<see cref="Authorize"/>) is called by the test directly. Tokens use the real clock.
/// </summary>
public sealed class FakeOidcProvider : HttpMessageHandler
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "opportunity-web";
    public const string ClientSecret = "not-a-secret";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new();
    private readonly ConcurrentDictionary<string, Grant> _refreshTokens = new();
    private readonly ConcurrentDictionary<string, Grant> _accessTokens = new();

    public FakeOidcProvider()
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = "test-key-1" };
    }

    public ConcurrentQueue<IReadOnlyDictionary<string, string>> AuthorizeRequests { get; } = new();

    public ConcurrentQueue<string> RevokedTokens { get; } = new();

    public int RefreshGrants => _refreshGrants;

    private int _refreshGrants;

    public SigningCredentials SigningCredentials => new(_key, SecurityAlgorithms.RsaSha256);

    /// <summary>
    /// The browser at the IdP: validates the authorize request, "authenticates" <paramref name="user"/> and returns the
    /// relative callback path (with <c>code</c>, <c>state</c> and <c>iss</c>) the browser is redirected to.
    /// </summary>
    public string Authorize(Uri authorizeUrl, FakeIdpUser user, string? error = null)
    {
        ArgumentNullException.ThrowIfNull(authorizeUrl);
        ArgumentNullException.ThrowIfNull(user);

        var query = QueryHelpers.ParseQuery(authorizeUrl.Query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        AuthorizeRequests.Enqueue(query);
        if (!authorizeUrl.GetLeftPart(UriPartial.Path).Equals(Issuer + "/authorize", StringComparison.Ordinal)
            || query["client_id"] != ClientId || query["response_type"] != "code" || query["code_challenge_method"] != "S256")
        {
            throw new InvalidOperationException("Unexpected authorize request: " + authorizeUrl);
        }

        var redirect = new Uri(query["redirect_uri"]);
        if (error is not null)
        {
            return QueryHelpers.AddQueryString(redirect.PathAndQuery, new Dictionary<string, string?> { ["error"] = error, ["state"] = query["state"] });
        }

        var code = Random();
        var sid = "idp-session-" + Random()[..8];
        _codes[code] = new PendingCode(user, query["redirect_uri"], query["code_challenge"], query["nonce"], sid);
        return QueryHelpers.AddQueryString(redirect.PathAndQuery, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = query["state"],
            ["iss"] = Issuer,
        });
    }

    /// <summary>The IdP session ID (<c>sid</c>) of the user's latest login.</summary>
    public string? LastSessionId { get; private set; }

    /// <summary>A back-channel logout token (OpenID Connect Back-Channel Logout 1.0 §2.4), optionally tampered.</summary>
    public string CreateLogoutToken(
        string? sid,
        string? subject,
        string audience = ClientId,
        bool includeEvent = true,
        string? nonce = null,
        SigningCredentials? signingCredentials = null)
    {
        var claims = new Dictionary<string, object> { ["jti"] = Random() };
        if (sid is not null)
        {
            claims["sid"] = sid;
        }

        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (includeEvent)
        {
            claims["events"] = new Dictionary<string, object> { ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>() };
        }

        if (nonce is not null)
        {
            claims["nonce"] = nonce;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(2),
            TokenType = "logout+jwt",
            SigningCredentials = signingCredentials ?? SigningCredentials,
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var form = request.Content is null
            ? new Dictionary<string, string>()
            : QueryHelpers.ParseQuery(await request.Content.ReadAsStringAsync(cancellationToken)).ToDictionary(p => p.Key, p => p.Value.ToString());

        return path switch
        {
            "/.well-known/openid-configuration" => Json(Discovery()),
            "/jwks" => Json(new { keys = new[] { Jwk() } }),
            "/token" when form.GetValueOrDefault("grant_type") == "authorization_code" => CodeGrant(form),
            "/token" when form.GetValueOrDefault("grant_type") == "refresh_token" => RefreshGrant(form),
            "/userinfo" => UserInfo(request),
            "/revoke" => Revoke(form),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rsa.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Dictionary<string, object> Discovery() => new Dictionary<string, object>
    {
        ["issuer"] = Issuer,
        ["authorization_endpoint"] = Issuer + "/authorize",
        ["token_endpoint"] = Issuer + "/token",
        ["userinfo_endpoint"] = Issuer + "/userinfo",
        ["jwks_uri"] = Issuer + "/jwks",
        ["end_session_endpoint"] = Issuer + "/logout",
        ["revocation_endpoint"] = Issuer + "/revoke",
        ["response_types_supported"] = new[] { "code" },
        ["subject_types_supported"] = new[] { "public" },
        ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
        ["code_challenge_methods_supported"] = new[] { "S256" },
        ["backchannel_logout_supported"] = true,
        ["backchannel_logout_session_supported"] = true,
    };

    private Dictionary<string, string> Jwk()
    {
        var parameters = _rsa.ExportParameters(false);
        return new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["kid"] = _key.KeyId,
            ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
            ["e"] = Base64UrlEncoder.Encode(parameters.Exponent),
        };
    }

    private HttpResponseMessage CodeGrant(Dictionary<string, string> form)
    {
        if (!ClientAuthenticated(form) || !_codes.TryRemove(form.GetValueOrDefault("code") ?? string.Empty, out var pending)
            || form.GetValueOrDefault("redirect_uri") != pending.RedirectUri
            || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form.GetValueOrDefault("code_verifier") ?? string.Empty))) != pending.CodeChallenge)
        {
            return OAuthError("invalid_grant");
        }

        LastSessionId = pending.Sid;
        return Tokens(new Grant(pending.User, pending.Sid, pending.User.Acr, [.. pending.User.Amr]), pending.Nonce);
    }

    private HttpResponseMessage RefreshGrant(Dictionary<string, string> form)
    {
        Interlocked.Increment(ref _refreshGrants);
        if (!ClientAuthenticated(form) || !_refreshTokens.TryRemove(form.GetValueOrDefault("refresh_token") ?? string.Empty, out var grant)
            || grant.User.Disabled)
        {
            return OAuthError("invalid_grant");
        }

        return Tokens(grant, nonce: null);
    }

    private HttpResponseMessage UserInfo(HttpRequestMessage request)
    {
        var token = request.Headers.Authorization?.Parameter;
        if (token is null || !_accessTokens.TryGetValue(token, out var grant) || grant.User.Disabled)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        return Json(new Dictionary<string, object> { ["sub"] = grant.User.Subject, ["groups"] = grant.User.Groups });
    }

    private HttpResponseMessage Revoke(Dictionary<string, string> form)
    {
        if (form.TryGetValue("token", out var token))
        {
            RevokedTokens.Enqueue(token);
            _refreshTokens.TryRemove(token, out _);
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private HttpResponseMessage Tokens(Grant grant, string? nonce)
    {
        var user = grant.User;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Subject,
            ["sid"] = grant.Sid,
            ["name"] = user.Name,
            ["email"] = user.Email,
            ["groups"] = user.Groups.ToArray(),
            ["acr"] = grant.Acr,
            ["amr"] = grant.Amr.ToArray(),
        };
        if (nonce is not null)
        {
            claims["nonce"] = nonce;
        }

        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            Claims = claims,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = SigningCredentials,
        });
        var accessToken = "at-" + Random();
        var refreshToken = "rt-" + Random();
        _accessTokens[accessToken] = grant;
        _refreshTokens[refreshToken] = grant;
        return Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = 300,
            ["refresh_token"] = refreshToken,
            ["id_token"] = idToken,
        });
    }

    private static bool ClientAuthenticated(Dictionary<string, string> form) =>
        form.GetValueOrDefault("client_id") == ClientId && form.GetValueOrDefault("client_secret") == ClientSecret;

    private static HttpResponseMessage OAuthError(string error) =>
        new(HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(new { error }), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));

    private sealed record PendingCode(FakeIdpUser User, string RedirectUri, string CodeChallenge, string Nonce, string Sid);

    private sealed record Grant(FakeIdpUser User, string Sid, string Acr, List<string> Amr);
}
