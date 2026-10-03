using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Opportunity.Testing.Images;

namespace Opportunity.Testing.Oidc;

/// <summary>
/// Keycloak (version from <c>versions.env</c>) running the Compose developer realm
/// (<c>deploy/docker-compose/keycloak/opportunity-realm.json</c>), so tests exercise the exact IdP configuration
/// developers get. In-memory database; the realm is imported at start.
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "opportunity";
    public const string ClientId = "opportunity-web";

    /// <summary>The dev realm's client credential (public, development only).</summary>
    public const string ClientSecret = "opportunity-dev-client";

    /// <summary>Password of every demo user in the dev realm.</summary>
    public const string DemoPassword = "opportunity";

    private const int HttpPort = 8080;
    private const string AdminUser = "admin";
    private const string AdminPassword = "test-only-admin";

    private readonly IContainer _container;

    public KeycloakFixture()
    {
        var realmFile = Path.Combine(Path.GetDirectoryName(VersionsFile.Locate())!, "deploy", "docker-compose", "keycloak", "opportunity-realm.json");
        _container = new ContainerBuilder(ContainerImages.Keycloak)
            .WithCommand("start-dev", "--import-realm")
            .WithEnvironment("KC_DB", "dev-mem")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", AdminUser)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", AdminPassword)
            .WithResourceMapping(new FileInfo(realmFile), "/opt/keycloak/data/import/")
            .WithPortBinding(HttpPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                r => r.ForPort(HttpPort).ForPath($"/realms/{Realm}/.well-known/openid-configuration"),
                o => o.WithTimeout(TimeSpan.FromMinutes(8))))
            .Build();
    }

    /// <summary>Base URL as seen from the test process (browser and back-channel alike, so the issuer is stable).</summary>
    public Uri BaseUrl => new($"http://localhost:{_container.GetMappedPublicPort(HttpPort)}");

    public Uri Authority => new(BaseUrl, $"/realms/{Realm}");

    public async ValueTask InitializeAsync() => await _container.StartAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    /// <summary>Enables or disables a realm user through the admin REST API (simulates IdP-side deactivation).</summary>
    public async Task SetUserEnabledAsync(string username, bool enabled, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { BaseAddress = BaseUrl };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync(http, cancellationToken).ConfigureAwait(false));

        var users = await http.GetFromJsonAsync<JsonElement>(
            $"/admin/realms/{Realm}/users?exact=true&username={Uri.EscapeDataString(username)}", cancellationToken).ConfigureAwait(false);
        var id = users.EnumerateArray().Single().GetProperty("id").GetString();

        using var response = await http.PutAsJsonAsync($"/admin/realms/{Realm}/users/{id}", new { enabled }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> AdminTokenAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = AdminUser,
            ["password"] = AdminPassword,
        });
        using var response = await http.PostAsync(new Uri("/realms/master/protocol/openid-connect/token", UriKind.Relative), form, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        return body.GetProperty("access_token").GetString()!;
    }
}
