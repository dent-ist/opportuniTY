using System.Net;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;

using Npgsql;

using Opportunity.Testing.Postgres;

namespace Opportunity.IntegrationTests.Auth;

/// <summary>One test's world: a migrated database, a fake IdP, the API host and helpers to sign in.</summary>
public sealed class AuthTestContext : IAsyncDisposable
{
    private readonly PostgresDatabase _database;

    private AuthTestContext(PostgresDatabase database, Action<IWebHostBuilder>? configure)
    {
        _database = database;
        Factory = new AuthApiFactory(database.ConnectionString, Idp, configure);
    }

    public FakeOidcProvider Idp { get; } = new();

    public AuthApiFactory Factory { get; }

    public string ConnectionString => _database.ConnectionString;

    public static async Task<AuthTestContext> CreateAsync(IdentityPostgresFixture postgres, Action<IWebHostBuilder>? configure = null) =>
        new(await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken), configure);

    /// <summary>Runs the authorization-code flow and returns the callback response (a redirect to <paramref name="returnUrl"/>).</summary>
    public async Task<HttpResponseMessage> SignInAsync(TestBrowser browser, FakeIdpUser user, string returnUrl = "/", bool stepUp = false)
    {
        var login = await browser.GetAsync($"/bff/login?returnUrl={Uri.EscapeDataString(returnUrl)}" + (stepUp ? "&stepUp=true" : string.Empty));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var callback = Idp.Authorize(login.Headers.Location!, user);
        return await browser.GetAsync(callback);
    }

    public async Task<(TestBrowser Browser, HttpResponseMessage Callback)> SignedInBrowserAsync(FakeIdpUser user)
    {
        var browser = Factory.CreateBrowser();
        var callback = await SignInAsync(browser, user);
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        browser.Cookies.Should().ContainKey("__Host-opp-session");
        return (browser, callback);
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // Test SQL constants.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // Test SQL constants.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is DBNull or null ? default : (T)result;
    }

    public async Task<Guid> CreateWorkspaceAsync(bool requireMfa)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO opportunity.workspace (workspace_id, name, display_time_zone, require_mfa) VALUES (@id, 'Matter', 'UTC', @mfa)",
            ("id", id), ("mfa", requireMfa));
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        Idp.Dispose();
        NpgsqlConnection.ClearAllPools();
        await _database.DisposeAsync();
    }
}
