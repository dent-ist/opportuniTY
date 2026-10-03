using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;

using Opportunity.IntegrationTests.Api;
using Opportunity.Testing.Oidc;
using Opportunity.Testing.Postgres;

namespace Opportunity.IntegrationTests.Auth;

/// <summary>
/// The BFF against a real IdP: Keycloak with the Compose developer realm. Proves interoperability (discovery, PKCE,
/// RS256 ID tokens, refresh grants, end-session) and that the dev realm maps users and groups as documented.
/// </summary>
[Collection(IdentityPostgresGroup.Name)]
public sealed partial class KeycloakLoginTests(IdentityPostgresFixture postgres, KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Demo_user_signs_in_through_keycloak_with_groups_mapped_and_survives_a_principal_refresh()
    {
        await using var database = await postgres.CreateDatabaseAsync(Ct);
        await using var factory = CreateFactory(database);
        using var browser = factory.CreateBrowser();

        var callback = await SignInAsync(browser, "privilege.dev", "/documents");

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.Should().Be("/documents");
        browser.Cookies.Should().ContainKey("__Host-opp-session");
        var me = await MeAsync(browser);
        me.GetProperty("displayName").GetString().Should().Be("Priya Privilege");
        me.GetProperty("email").GetString().Should().Be("privilege.dev@example.test");
        me.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).Should().Equal("privilege-reviewers", "reviewers");

        // A refresh-token grant against Keycloak re-validates the user and keeps the session.
        factory.Time.Advance(TimeSpan.FromMinutes(16));
        (await MeAsync(browser)).GetProperty("groups").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task User_disabled_in_keycloak_loses_access_at_the_next_principal_refresh()
    {
        await using var database = await postgres.CreateDatabaseAsync(Ct);
        await using var factory = CreateFactory(database);
        using var browser = factory.CreateBrowser();
        (await SignInAsync(browser, "auditor.dev")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await browser.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        await keycloak.SetUserEnabledAsync("auditor.dev", enabled: false, Ct);
        try
        {
            factory.Time.Advance(TimeSpan.FromMinutes(16));
            await (await browser.GetAsync("/api/v1/me")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");
        }
        finally
        {
            await keycloak.SetUserEnabledAsync("auditor.dev", enabled: true, Ct);
        }
    }

    [Fact]
    public async Task Logout_returns_keycloak_end_session_url()
    {
        await using var database = await postgres.CreateDatabaseAsync(Ct);
        await using var factory = CreateFactory(database);
        using var browser = factory.CreateBrowser();
        (await SignInAsync(browser, "reviewer.dev")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        await browser.GetAsync("/api/v1/me");

        var logout = await browser.PostAsync("/bff/logout");

        logout.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await logout.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("endSessionUrl").GetString().Should()
            .StartWith($"{keycloak.Authority}/protocol/openid-connect/logout?client_id={KeycloakFixture.ClientId}&post_logout_redirect_uri=");
        await (await browser.GetAsync("/api/v1/me")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthorized");
    }

    private AuthApiFactory CreateFactory(PostgresDatabase database) =>
        new(database.ConnectionString, backchannel: null, (IWebHostBuilder builder) =>
        {
            builder.UseSetting("Authentication:Oidc:Authority", keycloak.Authority.ToString());
            builder.UseSetting("Authentication:Oidc:ClientId", KeycloakFixture.ClientId);
            builder.UseSetting("Authentication:Oidc:ClientSecret", KeycloakFixture.ClientSecret);
            builder.UseSetting("Authentication:Oidc:RequireHttpsMetadata", "false");
        });

    /// <summary>Drives Keycloak's login page like a browser and follows the redirect back to the API callback.</summary>
    private static async Task<HttpResponseMessage> SignInAsync(TestBrowser browser, string username, string returnUrl = "/")
    {
        var login = await browser.GetAsync($"/bff/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Own cookie jar: Keycloak marks its login cookies Secure even on this plain-HTTP test port.
        using var idpBrowser = new TestBrowser(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }));
        // Keycloak answers the (pushed) authorization request with a redirect to its login page.
        var pageResponse = await idpBrowser.GetAsync(login.Headers.Location!.ToString());
        for (var hops = 0; pageResponse.StatusCode == HttpStatusCode.Found && hops < 5; hops++)
        {
            pageResponse = await idpBrowser.GetAsync(pageResponse.Headers.Location!.ToString());
        }

        var page = await pageResponse.Content.ReadAsStringAsync(Ct);
        var action = WebUtility.HtmlDecode(LoginFormAction().Match(page).Groups["action"].Value);
        action.Should().NotBeNullOrEmpty("Keycloak should render its login form");

        using var credentials = TestBrowser.Form(("username", username), ("password", KeycloakFixture.DemoPassword), ("credentialId", string.Empty));
        var submitted = await idpBrowser.SendAsync(new HttpRequestMessage(HttpMethod.Post, action) { Content = credentials });
        submitted.StatusCode.Should().Be(HttpStatusCode.Found);
        var callback = submitted.Headers.Location!;
        callback.GetLeftPart(UriPartial.Path).Should().Be("https://localhost/bff/callback");

        return await browser.GetAsync(callback.PathAndQuery);
    }

    private static async Task<JsonElement> MeAsync(TestBrowser browser)
    {
        var response = await browser.GetAsync("/api/v1/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.Clone();
    }

    [GeneratedRegex("""action="(?<action>[^"]*login-actions/authenticate[^"]*)""")]
    private static partial Regex LoginFormAction();
}
