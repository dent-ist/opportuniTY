using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;

using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Api;

/// <summary>E09-T04 over HTTP: the dedupe policy (ETag/If-Match, validation) and a run submitted as 202 + job.</summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class DedupeApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_policy_is_versioned_validated_and_a_run_answers_202_with_the_job()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        await using var factory = new ApiFactory();
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:App", db.AppConnectionString)).CreateClient();
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var policy = new Uri($"/api/v1/workspaces/{ws}/dedupe-policy", UriKind.Relative);

        using (var initial = await client.GetAsync(policy, Ct))
        {
            initial.StatusCode.Should().Be(HttpStatusCode.OK);
            initial.Headers.ETag!.Tag.Should().Be("\"0\"");
            var body = JsonDocument.Parse(await initial.Content.ReadAsStringAsync(Ct)).RootElement;
            body.GetProperty("enabled").GetBoolean().Should().BeFalse();
            body.GetProperty("hashSource").GetString().Should().Be("auto");
            body.GetProperty("scope").GetString().Should().Be("global");
            body.GetProperty("lastRun").ValueKind.Should().Be(JsonValueKind.Null);
        }

        var write = new JsonObject { ["enabled"] = true, ["hashSource"] = "upstreamHash", ["scope"] = "global" };
        (await PutAsync(client, policy, write, null)).StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        using (var saved = await PutAsync(client, policy, write, "\"0\""))
        {
            saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
            saved.Headers.ETag!.Tag.Should().Be("\"1\"");
            JsonDocument.Parse(await saved.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("hashSource").GetString().Should().Be("upstreamHash");
        }

        (await PutAsync(client, policy, write, "\"0\"")).StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        using (var custodial = await PutAsync(client, policy, new JsonObject { ["enabled"] = true, ["scope"] = "custodial" }, "\"1\""))
        {
            custodial.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await custodial.Content.ReadAsStringAsync(Ct)).Should().Contain("custodianFieldId");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workspaces/{ws}/dedupe-runs", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var run = await client.SendAsync(request, Ct);
        var text = await run.Content.ReadAsStringAsync(Ct);
        run.StatusCode.Should().Be(HttpStatusCode.Accepted, text);
        var job = JsonDocument.Parse(text).RootElement;
        job.GetProperty("jobType").GetString().Should().Be("relationshipFixup");
        run.Headers.Location!.ToString().Should().Be($"/api/v1/workspaces/{ws}/jobs/{job.GetProperty("jobId").GetGuid()}");
        job.GetProperty("committed").GetProperty("chunksTotal").GetInt64().Should().Be(1);
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, Uri uri, JsonObject body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }
}
