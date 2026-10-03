using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;

using Opportunity.Application.Audit;
using Opportunity.IntegrationTests.Api;

namespace Opportunity.IntegrationTests.Auth;

/// <summary>
/// E05-T01 against <see cref="FakeOidcProvider"/>: the BFF login flow, cookie flags, claims mapping, CSRF, idle and
/// absolute timeouts, principal refresh, deactivation, back-channel logout, logout, per-workspace MFA and 401s.
/// </summary>
[Collection(IdentityPostgresGroup.Name)]
public sealed class SessionAuthenticationTests(IdentityPostgresFixture postgres)
{
    private const string SessionCookie = "__Host-opp-session";
    private const string XsrfCookie = "__Host-opp-xsrf";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeIdpUser Alice() => new("alice-sub", "Alice Reviewer", "reviewers", "wall-falcon");

    [Fact]
    public async Task Login_uses_code_flow_with_pkce_state_and_nonce_and_sets_a_hardened_session_cookie()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        using var browser = ctx.Factory.CreateBrowser();

        var login = await browser.GetAsync("/bff/login?returnUrl=%2Freview%2F42");

        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var authorize = QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        authorize["response_type"].ToString().Should().Be("code");
        authorize["code_challenge_method"].ToString().Should().Be("S256");
        authorize["code_challenge"].ToString().Should().NotBeNullOrEmpty();
        authorize["state"].ToString().Should().NotBeNullOrEmpty();
        authorize["nonce"].ToString().Should().NotBeNullOrEmpty();
        authorize["scope"].ToString().Split(' ').Should().Contain("openid");
        authorize["redirect_uri"].ToString().Should().Be("https://localhost/bff/callback");
        authorize.ContainsKey("client_secret").Should().BeFalse();

        var callback = await browser.GetAsync(ctx.Idp.Authorize(login.Headers.Location!, Alice()));

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.Should().Be("/review/42");
        var cookie = TestBrowser.SetCookies(callback).Single(c => c.Name == SessionCookie);
        cookie.HttpOnly.Should().BeTrue();
        cookie.Secure.Should().BeTrue();
        cookie.SameSite.Should().Be(SameSiteMode.Lax);
        cookie.Path.ToString().Should().Be("/");
        cookie.Domain.HasValue.Should().BeFalse();
        cookie.Expires.Should().BeNull("the server enforces the lifetime; the browser keeps a session cookie");
        WebEncoders.Base64UrlDecode(cookie.Value.ToString()).Should().HaveCount(32, "the session key is 256 bits");

        // The database holds only the SHA-256 of the key, and the IdP tokens only encrypted.
        var keyHash = SHA256.HashData(WebEncoders.Base64UrlDecode(cookie.Value.ToString()));
        var tokens = await ctx.ScalarAsync<byte[]>("SELECT tokens FROM opportunity.user_session WHERE key_hash = @h", ("h", keyHash));
        tokens.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(tokens!).Should().NotContain("rt-").And.NotContain("eyJ");
        (await ctx.ScalarAsync<long>("SELECT count(*) FROM opportunity.user_session WHERE key_hash = @k", ("k", WebEncoders.Base64UrlDecode(cookie.Value.ToString()))))
            .Should().Be(0);
    }

    [Fact]
    public async Task Claims_map_to_the_principal_and_a_user_keyed_by_issuer_and_subject()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var (browser, _) = await ctx.SignedInBrowserAsync(Alice());
        using var _browser = browser;

        var me = await browser.GetAsync("/api/v1/me");

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync(Ct));
        var root = body.RootElement;
        var userId = Guid.Parse(root.GetProperty("userId").GetString()!);
        root.GetProperty("displayName").GetString().Should().Be("Alice Reviewer");
        root.GetProperty("email").GetString().Should().Be("alice-sub@example.test");
        root.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).Should().Equal("reviewers", "wall-falcon");
        root.GetProperty("mfa").GetBoolean().Should().BeFalse();
        root.TryGetProperty("accessToken", out _).Should().BeFalse();

        (await ctx.ScalarAsync<Guid>(
                "SELECT user_id FROM opportunity.app_user WHERE issuer = @iss AND subject = @sub",
                ("iss", FakeOidcProvider.Issuer), ("sub", "alice-sub")))
            .Should().Be(userId);

        var signIn = ctx.Factory.Audit.Events.Single(e => e.Action == AuditTaxonomy.Auth.SignIn);
        signIn.Category.Should().Be(AuditTaxonomy.Auth.Category);
        signIn.ActorId.Should().Be(userId.ToString());
        signIn.Outcome.Should().Be(AuditOutcome.Success);
        signIn.WorkspaceId.Should().BeNull("Auth events go to the installation chain");
    }

    [Fact]
    public async Task Same_subject_signs_in_as_the_same_user_and_login_rotates_the_session()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var alice = Alice();
        var (browser, _) = await ctx.SignedInBrowserAsync(alice);
        using var _browser = browser;
        var firstKey = browser.Cookies[SessionCookie];
        var firstUser = await UserIdAsync(browser);

        (await ctx.SignInAsync(browser, alice)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        browser.Cookies[SessionCookie].Should().NotBe(firstKey);
        (await UserIdAsync(browser)).Should().Be(firstUser);
        using var stale = ctx.Factory.CreateBrowser();
        stale.Cookies[SessionCookie] = firstKey;
        await (await stale.GetAsync("/api/v1/me")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");
        (await ctx.ScalarAsync<string>("SELECT revoked_reason FROM opportunity.user_session ORDER BY created_at LIMIT 1"))
            .Should().Be("Replaced");
    }

    [Theory]
    [InlineData("/api/v1/me")]
    [InlineData("/api/v1/workspaces/0199a8a0-0000-7000-8000-00000000aaaa/auth-probe")]
    public async Task Unauthenticated_api_calls_get_401_problem_details(string path)
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        using var browser = ctx.Factory.CreateBrowser();

        await (await browser.GetAsync(path)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");

        browser.Cookies[SessionCookie] = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var forged = await browser.GetAsync(path);
        await forged.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");
        browser.Cookies.Should().NotContainKey(SessionCookie, "an unknown session cookie is cleared");

        // Unsafe methods are 401 as well, never a redirect to the IdP.
        var post = await browser.PostAsync("/api/v1/auth-probe", withXsrf: false);
        await post.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Unsafe_requests_need_the_antiforgery_header_and_the_public_origin()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var (browser, _) = await ctx.SignedInBrowserAsync(Alice());
        using var _browser = browser;

        // The first safe request issues the token pair: an HttpOnly cookie token and a readable request token.
        var me = await browser.GetAsync("/api/v1/me");
        var xsrf = TestBrowser.SetCookies(me).Single(c => c.Name == XsrfCookie);
        xsrf.HttpOnly.Should().BeFalse("Angular reads it to echo X-XSRF-TOKEN");
        xsrf.Secure.Should().BeTrue();
        xsrf.SameSite.Should().Be(SameSiteMode.Strict);
        var antiforgery = TestBrowser.SetCookies(me).Single(c => c.Name == "__Host-opp-csrf");
        antiforgery.HttpOnly.Should().BeTrue();
        antiforgery.Secure.Should().BeTrue();

        await (await browser.PostAsync("/api/v1/auth-probe", withXsrf: false))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "csrf-validation-failed");
        await (await browser.PostAsync("/api/v1/auth-probe", origin: "https://evil.example"))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "csrf-validation-failed");
        await (await browser.PostAsync("/api/v1/auth-probe", origin: null))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "csrf-validation-failed");
        var tampered = browser.Cookies[XsrfCookie];
        browser.Cookies[XsrfCookie] = tampered[..^4] + "AAAA";
        await (await browser.PostAsync("/api/v1/auth-probe"))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "csrf-validation-failed");
        browser.Cookies[XsrfCookie] = tampered;

        (await browser.PostAsync("/api/v1/auth-probe")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Antiforgery_tokens_are_bound_to_the_user()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var (alice, _) = await ctx.SignedInBrowserAsync(Alice());
        var (bob, _) = await ctx.SignedInBrowserAsync(new FakeIdpUser("bob-sub", "Bob"));
        using var _alice = alice;
        using var _bob = bob;
        await alice.GetAsync("/api/v1/me");
        await bob.GetAsync("/api/v1/me");

        bob.Cookies[XsrfCookie] = alice.Cookies[XsrfCookie];
        bob.Cookies["__Host-opp-csrf"] = alice.Cookies["__Host-opp-csrf"];

        await (await bob.PostAsync("/api/v1/auth-probe")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "csrf-validation-failed");
    }

    [Fact]
    public async Task Idle_timeout_of_30_minutes_is_enforced_server_side()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var (browser, _) = await ctx.SignedInBrowserAsync(Alice());
        using var _browser = browser;

        ctx.Factory.Time.Advance(TimeSpan.FromMinutes(29));
        (await browser.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK, "activity within the idle window keeps the session");
        ctx.Factory.Time.Advance(TimeSpan.FromMinutes(29));
        (await browser.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        ctx.Factory.Time.Advance(TimeSpan.FromMinutes(30));
        var expired = await browser.GetAsync("/api/v1/me");

        await expired.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");
        browser.Cookies.Should().NotContainKey(SessionCookie);
        (await ctx.ScalarAsync<string>("SELECT revoked_reason FROM opportunity.user_session")).Should().Be("IdleTimeout");
        var audit = ctx.Factory.Audit.Events.Single(e => e.Action == AuditTaxonomy.Auth.SessionExpired);
        audit.ReasonCode.Should().Be("IdleTimeout");
    }

    [Fact]
    public async Task Absolute_timeout_of_12_hours_ends_even_an_active_session()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var (browser, _) = await ctx.SignedInBrowserAsync(Alice());
        using var _browser = browser;

        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(12) - TimeSpan.FromMinutes(20); elapsed += TimeSpan.FromMinutes(20))
        {
            ctx.Factory.Time.Advance(TimeSpan.FromMinutes(20));
            (await browser.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK, "at {0} the session is still within 12 h", elapsed);
        }

        ctx.Factory.Time.Advance(TimeSpan.FromMinutes(21));
        await (await browser.GetAsync("/api/v1/me")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");
        (await ctx.ScalarAsync<string>("SELECT revoked_reason FROM opportunity.user_session")).Should().Be("AbsoluteTimeout");
    }

    [Fact]
    public async Task Principal_refresh_updates_groups_from_the_idp()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var alice = Alice();
        var (browser, _) = await ctx.SignedInBrowserAsync(alice);
        using var _browser = browser;

        alice.Groups = ["reviewers", "privilege-reviewers"];
        ctx.Factory.Time.Advance(TimeSpan.FromMinutes(10));
        (await GroupsAsync(browser)).Should().Equal("reviewers", "wall-falcon");

        ctx.Factory.Time.Advance(TimeSpan.FromMinutes(6));
        (await GroupsAsync(browser)).Should().Equal("privilege-reviewers", "reviewers");
        ctx.Idp.RefreshGrants.Should().Be(1);
        (await ctx.ScalarAsync<string[]>("SELECT groups FROM opportunity.app_user WHERE subject = 'alice-sub'"))
            .Should().Equal("privilege-reviewers", "reviewers");
    }

    [Fact]
    public async Task A_deactivated_idp_user_loses_access_within_the_refresh_window()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var alice = Alice();
        var (browser, _) = await ctx.SignedInBrowserAsync(alice);
        using var _browser = browser;

        alice.Disabled = true;
        ctx.Factory.Time.Advance(TimeSpan.FromMinutes(14));
        (await browser.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK, "inside the configured 15-minute window (AR-01)");

        ctx.Factory.Time.Advance(TimeSpan.FromMinutes(2));
        await (await browser.GetAsync("/api/v1/me")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");
        (await ctx.ScalarAsync<string>("SELECT revoked_reason FROM opportunity.user_session")).Should().Be("PrincipalRefreshFailed");
        ctx.Factory.Audit.Events.Should().Contain(e => e.Action == AuditTaxonomy.Auth.SessionRevoked && e.ReasonCode == "PrincipalRefreshFailed");
    }

    [Fact]
    public async Task Back_channel_logout_invalidates_the_server_session_immediately()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var (browser, _) = await ctx.SignedInBrowserAsync(Alice());
        using var _browser = browser;
        var (other, _) = await ctx.SignedInBrowserAsync(new FakeIdpUser("bob-sub", "Bob"));
        using var _other = other;
        using var idp = ctx.Factory.CreateBrowser();

        var response = await idp.PostAsync(
            "/bff/backchannel-logout",
            TestBrowser.Form(("logout_token", ctx.Idp.CreateLogoutToken(sid: null, subject: "alice-sub"))),
            withXsrf: false,
            origin: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        await (await browser.GetAsync("/api/v1/me")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");
        (await other.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK, "only the named user's sessions end");
        ctx.Factory.Audit.Events.Should().Contain(e => e.Action == AuditTaxonomy.Auth.SessionRevoked && e.ReasonCode == "BackChannelLogout");

        // By IdP session (sid): Bob's latest login.
        var bobSid = ctx.Idp.LastSessionId!;
        (await idp.PostAsync("/bff/backchannel-logout",
                TestBrowser.Form(("logout_token", ctx.Idp.CreateLogoutToken(sid: bobSid, subject: null))), withXsrf: false, origin: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await (await other.GetAsync("/api/v1/me")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");
    }

    public static TheoryData<string> InvalidLogoutTokens => new() { "wrong-audience", "no-event", "nonce", "foreign-key", "no-subject", "garbage" };

    [Theory]
    [MemberData(nameof(InvalidLogoutTokens))]
    public async Task Back_channel_logout_rejects_invalid_tokens(string kind)
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var (browser, _) = await ctx.SignedInBrowserAsync(Alice());
        using var _browser = browser;
        using var foreignKey = RSA.Create(2048);
        var token = kind switch
        {
            "wrong-audience" => ctx.Idp.CreateLogoutToken(null, "alice-sub", audience: "another-client"),
            "no-event" => ctx.Idp.CreateLogoutToken(null, "alice-sub", includeEvent: false),
            "nonce" => ctx.Idp.CreateLogoutToken(null, "alice-sub", nonce: "n-1"),
            "foreign-key" => ctx.Idp.CreateLogoutToken(null, "alice-sub",
                signingCredentials: new SigningCredentials(new RsaSecurityKey(foreignKey) { KeyId = "test-key-1" }, SecurityAlgorithms.RsaSha256)),
            "no-subject" => ctx.Idp.CreateLogoutToken(null, null),
            _ => "not-a-jwt",
        };
        using var idp = ctx.Factory.CreateBrowser();

        var response = await idp.PostAsync("/bff/backchannel-logout", TestBrowser.Form(("logout_token", token)), withXsrf: false, origin: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("invalid_request");
        (await browser.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_returns_the_idp_end_session_url()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var (browser, _) = await ctx.SignedInBrowserAsync(Alice());
        using var _browser = browser;
        await browser.GetAsync("/api/v1/me");

        await (await browser.PostAsync("/bff/logout", withXsrf: false))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "csrf-validation-failed");
        var logout = await browser.PostAsync("/bff/logout");

        logout.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await logout.Content.ReadAsStringAsync(Ct));
        var endSession = new Uri(body.RootElement.GetProperty("endSessionUrl").GetString()!);
        endSession.GetLeftPart(UriPartial.Path).Should().Be(FakeOidcProvider.Issuer + "/logout");
        var query = QueryHelpers.ParseQuery(endSession.Query);
        query["client_id"].ToString().Should().Be(FakeOidcProvider.ClientId);
        query["post_logout_redirect_uri"].ToString().Should().Be("https://localhost/");
        query.ContainsKey("id_token_hint").Should().BeFalse("no token reaches the browser");
        browser.Cookies.Should().NotContainKey(SessionCookie);
        ctx.Idp.RevokedTokens.Should().ContainSingle().Which.Should().StartWith("rt-");
        await (await browser.GetAsync("/api/v1/me")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");
        (await ctx.ScalarAsync<string>("SELECT revoked_reason FROM opportunity.user_session")).Should().Be("SignOut");
        (await ctx.ScalarAsync<byte[]>("SELECT tokens FROM opportunity.user_session")).Should().BeEmpty("ended sessions keep no tokens");
        ctx.Factory.Audit.Events.Should().Contain(e => e.Action == AuditTaxonomy.Auth.SignOut);
    }

    [Fact]
    public async Task Workspace_requiring_mfa_returns_403_step_up_until_the_user_re_authenticates_with_mfa()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var alice = Alice();
        var (browser, _) = await ctx.SignedInBrowserAsync(alice);
        using var _browser = browser;
        var open = await ctx.CreateWorkspaceAsync(requireMfa: false);
        var guarded = await ctx.CreateWorkspaceAsync(requireMfa: true);

        (await browser.GetAsync($"/api/v1/workspaces/{open}/auth-probe")).StatusCode.Should().Be(HttpStatusCode.OK);
        var denied = await (await browser.GetAsync($"/api/v1/workspaces/{guarded}/auth-probe"))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "step-up-required");
        var stepUpUrl = denied.GetProperty("stepUpUrl").GetString()!;
        stepUpUrl.Should().Be("/bff/login?stepUp=true");
        ctx.Factory.Audit.Events.Should().Contain(e => e.Category == "AuthZ" && e.ReasonCode == "MfaRequired" && e.WorkspaceId == guarded);
        await (await browser.GetAsync("/api/v1/admin-probe")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "step-up-required");

        alice.Acr = AuthApiFactory.MfaAcr;
        alice.Amr = ["pwd", "otp"];
        (await ctx.SignInAsync(browser, alice, returnUrl: $"/workspaces/{guarded}", stepUp: true)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        var stepUpRequest = ctx.Idp.AuthorizeRequests.Last();
        stepUpRequest["acr_values"].Should().Be(AuthApiFactory.MfaAcr);
        stepUpRequest["max_age"].Should().Be("0");
        (await browser.GetAsync($"/api/v1/workspaces/{guarded}/auth-probe")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await browser.GetAsync("/api/v1/admin-probe")).StatusCode.Should().Be(HttpStatusCode.OK);
        ctx.Factory.Audit.Events.Should().Contain(e => e.Action == AuditTaxonomy.Auth.StepUp);
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example/")]
    public async Task Return_url_must_be_local(string returnUrl)
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        using var browser = ctx.Factory.CreateBrowser();

        var callback = await ctx.SignInAsync(browser, Alice(), returnUrl);

        callback.Headers.Location!.OriginalString.Should().Be("/");
    }

    [Fact]
    public async Task Idp_error_on_callback_fails_sign_in_without_a_session()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        using var browser = ctx.Factory.CreateBrowser();

        var login = await browser.GetAsync("/bff/login");
        var callback = await browser.GetAsync(ctx.Idp.Authorize(login.Headers.Location!, Alice(), error: "access_denied"));

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.Should().Be("/?signin=failed");
        browser.Cookies.Should().NotContainKey(SessionCookie);
        ctx.Factory.Audit.Events.Should().Contain(e => e.Action == AuditTaxonomy.Auth.SignInFailed && e.Outcome == AuditOutcome.Failure);
    }

    [Fact]
    public async Task Replayed_callback_does_not_create_a_session()
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        using var browser = ctx.Factory.CreateBrowser();
        var login = await browser.GetAsync("/bff/login");
        var callbackPath = ctx.Idp.Authorize(login.Headers.Location!, Alice());

        // Another browser (without the correlation cookie) replays the callback URL: state validation fails.
        using var attacker = ctx.Factory.CreateBrowser();
        var replay = await attacker.GetAsync(callbackPath);

        replay.Headers.Location!.OriginalString.Should().Be("/?signin=failed");
        attacker.Cookies.Should().NotContainKey(SessionCookie);
    }

    [Theory]
    [InlineData("/api/v1/me", HttpStatusCode.OK)]
    [InlineData("/api/v1/me", HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/does-not-exist", HttpStatusCode.NotFound)]
    [InlineData("/bff/login", HttpStatusCode.Redirect)]
    public async Task Every_api_response_carries_the_security_headers(string path, HttpStatusCode expected)
    {
        await using var ctx = await AuthTestContext.CreateAsync(postgres);
        var (browser, _) = await ctx.SignedInBrowserAsync(Alice());
        using var _browser = browser;
        if (expected == HttpStatusCode.Unauthorized)
        {
            browser.Cookies.Clear();
        }

        var response = await browser.GetAsync(path);

        response.StatusCode.Should().Be(expected);
        SecurityHeaderAssertions.AssertApiHeaders(response);
    }

    private static async Task<string> UserIdAsync(TestBrowser browser)
    {
        using var body = JsonDocument.Parse(await (await browser.GetAsync("/api/v1/me")).Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("userId").GetString()!;
    }

    private static async Task<List<string?>> GroupsAsync(TestBrowser browser)
    {
        var response = await browser.GetAsync("/api/v1/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. body.RootElement.GetProperty("groups").EnumerateArray().Select(g => g.GetString())];
    }
}
