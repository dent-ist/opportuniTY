using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Opportunity.Api.Preferences;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Preferences;

/// <summary>E15-T03: <c>/api/v1/me/preferences</c> stores each user's UI preferences in PostgreSQL, for that user only.</summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class UserPreferenceApiTests(MigrationPostgresFixture postgres)
{
    private const string OtherUser = "0199a8a0-0000-7000-8000-000000000002";
    private static readonly Uri All = new("/api/v1/me/preferences", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Key(string key) => new($"/api/v1/me/preferences/{key}", UriKind.Relative);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Preferences_round_trip_per_key_and_survive_a_new_host()
    {
        await using var db = await CreateDatabaseAsync();
        await using (var factory = Factory(db))
        {
            using var client = factory.CreateClient();
            (await client.PutAsync(Key("shortcuts"), Json("""{"singleKey":false,"bindings":{"document.next":["Alt+Shift+KeyN"]}}"""), Ct))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await client.PutAsync(Key("ui"), Json("""{"theme":"dark"}"""), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await client.PutAsync(Key("ui"), Json("""{"theme":"light","density":"compact"}"""), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await client.PutAsync(Key("pane.review.coding"), Json("""{"size":32.5,"collapsed":false}"""), Ct))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await client.DeleteAsync(Key("pane.review.coding"), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await client.DeleteAsync(Key("never-set"), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // A new API host (a reload, another machine) reads the same profile.
        await using var again = Factory(db);
        using var reader = again.CreateClient();
        using var body = JsonDocument.Parse(await reader.GetStringAsync(All, Ct));
        var values = body.RootElement.GetProperty("values");
        values.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["shortcuts", "ui"]);
        values.GetProperty("ui").GetProperty("theme").GetString().Should().Be("light");
        values.GetProperty("ui").GetProperty("density").GetString().Should().Be("compact");
        values.GetProperty("shortcuts").GetProperty("singleKey").GetBoolean().Should().BeFalse();
        values.GetProperty("shortcuts").GetProperty("bindings").GetProperty("document.next")[0].GetString().Should().Be("Alt+Shift+KeyN");
    }

    [Fact]
    public async Task A_user_sees_and_changes_only_their_own_preferences()
    {
        await using var db = await CreateDatabaseAsync();
        await using var factory = Factory(db);
        using var owner = factory.CreateClient();
        using var other = factory.CreateClient();
        other.DefaultRequestHeaders.Add(TestAuthentication.UserHeader, OtherUser);

        (await owner.PutAsync(Key("ui"), Json("""{"theme":"dark"}"""), Ct)).EnsureSuccessStatusCode();
        (await other.DeleteAsync(Key("ui"), Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await other.PutAsync(Key("ui"), Json("""{"theme":"high-contrast"}"""), Ct)).EnsureSuccessStatusCode();

        using var mine = JsonDocument.Parse(await owner.GetStringAsync(All, Ct));
        mine.RootElement.GetProperty("values").GetProperty("ui").GetProperty("theme").GetString().Should().Be("dark");
        using var theirs = JsonDocument.Parse(await other.GetStringAsync(All, Ct));
        theirs.RootElement.GetProperty("values").GetProperty("ui").GetProperty("theme").GetString().Should().Be("high-contrast");
    }

    [Fact]
    public async Task Anonymous_callers_and_principals_without_a_user_id_are_refused()
    {
        await using var db = await CreateDatabaseAsync();
        await using var factory = Factory(db);
        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add(TestAuthentication.AnonymousHeader, "1");
        (await anonymous.GetAsync(All, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsync(Key("ui"), Json("{}"), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var noUserId = factory.CreateClient();
        noUserId.DefaultRequestHeaders.Add(TestAuthentication.UserHeader, "not-a-user-id");
        (await noUserId.GetAsync(All, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.user_preference")).Should().Be(0);
    }

    [Theory]
    [InlineData("1starts-with-digit", "{}", "key")]
    [InlineData("has%20space", "{}", "key")]
    [InlineData("ui", "null", "value")]
    public async Task Invalid_keys_and_values_are_validation_problems(string key, string json, string field)
    {
        await using var db = await CreateDatabaseAsync();
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        using var response = await client.PutAsync(Key(key), Json(json), Ct);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        problem.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Values_and_key_counts_are_bounded()
    {
        await using var db = await CreateDatabaseAsync();
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        var tooLarge = JsonSerializer.Serialize(new string('x', UserPreferenceEndpoints.MaxValueLength));
        await (await client.PutAsync(Key("big"), Json(tooLarge), Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        for (var i = 0; i < UserPreferenceEndpoints.MaxKeys; i++)
        {
            (await client.PutAsJsonAsync(Key($"k{i}"), i, Ct)).EnsureSuccessStatusCode();
        }

        await (await client.PutAsJsonAsync(Key("one-more"), 1, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");
        (await client.PutAsJsonAsync(Key("k0"), "replaced", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent, "replacing an existing key is always allowed");
    }

    private async Task<CoreSchemaDatabase> CreateDatabaseAsync()
    {
        var db = await CoreSchemaDatabase.CreateAsync(postgres);
        foreach (var user in new[] { TestAuthentication.UserId, OtherUser })
        {
            await db.ExecuteAsync(
                """
                INSERT INTO opportunity.app_user (user_id, issuer, subject, groups_refreshed_at, last_sign_in_at)
                VALUES (@id, 'https://idp.invalid', @id::text, now(), now())
                """,
                ("id", Guid.Parse(user)));
        }

        return db;
    }

    private static WebApplicationFactory<Program> Factory(CoreSchemaDatabase db) =>
        new ApiFactory().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:App", db.AppConnectionString));
}
