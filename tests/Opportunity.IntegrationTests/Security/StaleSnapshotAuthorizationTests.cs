using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Content;
using Opportunity.IntegrationTests.Exports;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Security;

/// <summary>
/// E05-T05 stale snapshots (ADR-015 D9.4, Q-07, Q-15): frozen sets are candidate sets, never grants. Documents that were
/// walled or restricted after a bulk coding set and an export set were frozen (through the API) are skipped by the bulk
/// coding job and excluded from the export: no coding is written for them, and not one byte of their native or text is
/// in the downloaded package. Their content routes answer 404.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class StaleSnapshotAuthorizationTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Documents_hidden_after_the_freeze_are_skipped_by_bulk_coding_and_excluded_from_the_export_package()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        await using var import = ImportHarness.Over(db.Core);
        var exports = ExportHarness.Over(import, documentsPerChunk: 2);
        var content = ContentDatabase.Over(db, import.StoreRoot, import.Store);
        await using var bulk = BulkCodingHarness.Over(db);
        var ws = await db.Core.CreateWorkspaceAsync();
        var fields = await CodingApiHarness.WorkspaceAsync(db.Core, ws);
        var manager = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.ProductionManager, manager);
        var docs = new List<StoredDocument>();
        for (var i = 0; i < 4; i++)
        {
            docs.Add(await content.DocumentAsync(ws));
        }

        var ids = docs.Select(d => d.DocumentId).ToList();
        await using var factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            builder.UseSetting("ObjectStorage:Provider", "FileSystem");
            builder.UseSetting("ObjectStorage:FileSystem:RootPath", import.StoreRoot);
            builder.UseSetting("Snapshots:BackgroundEnabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
            });
        });
        using var client = factory.CreateClient();
        var api = $"/api/v1/workspaces/{ws}";

        var bulkSet = await JsonAsync(client, HttpMethod.Post, api + "/snapshots", manager, HttpStatusCode.Created,
            new JsonObject { ["purpose"] = "BulkCoding", ["documentIds"] = new JsonArray([.. ids.Select(id => (JsonNode)id.ToString())]) });
        var exportSet = await JsonAsync(client, HttpMethod.Post, api + "/snapshots", manager, HttpStatusCode.Created,
            new JsonObject { ["purpose"] = "Export", ["documentIds"] = new JsonArray([.. ids.Select(id => (JsonNode)id.ToString())]) });
        bulkSet.GetProperty("documentCount").GetInt64().Should().Be(4);
        exportSet.GetProperty("documentCount").GetInt64().Should().Be(4);

        // After the freeze: a wall around document 1 naming the manager, an ungranted restriction class on document 2.
        await db.WallAsync(ws, [manager], [], [ids[1]]);
        await db.Core.ExecuteAsync("INSERT INTO opportunity.restriction_class (workspace_id, class_key, display_name) VALUES (@ws, 'Hot', 'Hot documents')", ("ws", ws));
        await db.Core.ExecuteAsync("INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, 'Hot')", ("ws", ws), ("doc", ids[2]));

        // Bulk coding over the stale set: the hidden documents are skipped and stay uncoded.
        var job = await JsonAsync(client, HttpMethod.Post, api + "/bulk-coding", manager, HttpStatusCode.Accepted, new JsonObject
        {
            ["snapshotId"] = bulkSet.GetProperty("snapshotId").GetString(),
            ["changes"] = new JsonArray(new JsonObject { ["fieldId"] = fields.Responsive, ["operation"] = "set", ["value"] = true }),
        });
        var jobId = job.GetProperty("jobId").GetGuid();
        await bulk.RunAsync(ws, jobId);
        var hidden = await JsonAsync(client, HttpMethod.Get, $"{api}/bulk-coding/{jobId}/report?outcome=skippedHidden", manager, HttpStatusCode.OK);
        hidden.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("documentId").GetGuid()).Should().BeEquivalentTo([ids[1], ids[2]]);
        var values = await bulk.ValuesAsync(ws, ids, fields.Responsive);
        ids.Where(id => values.GetValueOrDefault(id) is not null).Should().BeEquivalentTo([ids[0], ids[3]], "no coding is written for hidden documents");

        // Export over the stale set: the hidden documents are excluded and none of their bytes is in the package.
        var export = await JsonAsync(client, HttpMethod.Post, api + "/exports", manager, HttpStatusCode.Accepted, new JsonObject
        {
            ["snapshotId"] = exportSet.GetProperty("snapshotId").GetString(),
            ["fields"] = new JsonArray(new JsonObject { ["fieldId"] = Core.Fields.SystemFields.ControlNumber }),
        });
        var exportId = export.GetProperty("exportId").GetGuid();
        await exports.CoordinateAsync(ws);
        await exports.DeliverOpenChunksAsync(ws, export.GetProperty("job").GetProperty("jobId").GetGuid());
        await exports.CoordinateAsync(ws);
        var done = await JsonAsync(client, HttpMethod.Get, $"{api}/exports/{exportId}", manager, HttpStatusCode.OK);
        done.GetProperty("status").GetString().Should().BeEquivalentTo("completed");
        done.GetProperty("report").GetProperty("documentsExcluded").GetInt64().Should().Be(2);
        (await JsonAsync(client, HttpMethod.Get, $"{api}/exports/{exportId}/exclusions", manager, HttpStatusCode.OK))
            .GetProperty("items").GetArrayLength().Should().Be(2);

        using var package = await SendAsync(client, HttpMethod.Get, $"{api}/exports/{exportId}/package", manager);
        package.StatusCode.Should().Be(HttpStatusCode.OK);
        var zip = await package.Content.ReadAsByteArrayAsync(Ct);
        Contains(zip, docs[0].Text).Should().BeTrue("the package is stored uncompressed, so exported content is found verbatim");
        Contains(zip, docs[0].Native).Should().BeTrue();
        foreach (var excluded in new[] { docs[1], docs[2] })
        {
            Contains(zip, excluded.Text).Should().BeFalse("the text of an excluded document is not in the package");
            Contains(zip, excluded.Native).Should().BeFalse("the native of an excluded document is not in the package");
            using var native = await SendAsync(client, HttpMethod.Get, $"{api}/documents/{excluded.DocumentId}/native", manager);
            native.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, Guid user, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, HttpMethod method, string url, Guid user, HttpStatusCode expected, JsonNode? body = null)
    {
        using var response = await SendAsync(client, method, url, user, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
