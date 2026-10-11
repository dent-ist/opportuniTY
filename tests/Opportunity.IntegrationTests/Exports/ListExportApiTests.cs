using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Application.ListExports;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Audit;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Production.ListExports;

namespace Opportunity.IntegrationTests.Exports;

/// <summary>
/// #181 through the real API host, the PostgreSQL PDP and audit store: "Export list" freezes the checked rows, writes
/// Control Number and the view's columns to CSV (UTF-8 with BOM, formula-neutralized) in a background job of several
/// chunks, re-authorizes every document when its chunk runs (Q-15: a wall placed after the request leaves the document
/// out, counted and audited), and serves the file through the gateway to its requester only, each step audited.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ListExportApiTests(MigrationPostgresFixture postgres)
{
    private static readonly int[] UnknownField = [999_999];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_list_is_exported_by_a_job_rechecked_at_execution_and_downloaded_by_its_requester_with_every_step_audited()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        await db.Core.Fields.InitializeWorkspaceAsync(ws, Ct);
        var manager = await db.CreateUserAsync();
        var otherManager = await db.CreateUserAsync();
        var reviewer = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.ProductionManager, manager);
        await db.AssignAsync(ws, WorkspaceRole.ProductionManager, otherManager);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        var a = await db.Core.InsertDocumentAsync(ws, "LX0001", d => (d.FileName, d.FileSize) = ("=HYPERLINK(\"x\"), memo.msg", 2048));
        var b = await db.Core.InsertDocumentAsync(ws, "LX0002", d => d.FileName = "walled.docx");
        var c = await db.Core.InsertDocumentAsync(ws, "LX0003", d => d.FileName = "Zoë plan.pdf");

        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}";
        var body = JsonSerializer.Serialize(new
        {
            fieldIds = new[] { SystemFields.FileName, SystemFields.FileSize },
            documentIds = new[] { a.DocumentId, b.DocumentId, c.DocumentId },
        });

        // The permission is required (Reviewer lacks it, Q-18), and unknown columns are refused.
        await (await SendAsync(client, HttpMethod.Post, root + "/list-exports", reviewer, body)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Post, root + "/list-exports", manager,
            JsonSerializer.Serialize(new { fieldIds = UnknownField, documentIds = new[] { a.DocumentId } }))).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        Guid exportId, jobId;
        using (var created = await SendAsync(client, HttpMethod.Post, root + "/list-exports", manager, body))
        {
            var text = await created.Content.ReadAsStringAsync(Ct);
            created.StatusCode.Should().Be(HttpStatusCode.Accepted, text);
            using var json = JsonDocument.Parse(text);
            exportId = json.RootElement.GetProperty("exportId").GetGuid();
            jobId = json.RootElement.GetProperty("jobId").GetGuid();
            created.Headers.Location!.ToString().Should().Be($"{root}/jobs/{jobId}");
            json.RootElement.GetProperty("status").GetString().Should().Be("queued");
            json.RootElement.GetProperty("documentCount").GetInt64().Should().Be(3);
            json.RootElement.GetProperty("columns").EnumerateArray().Select(col => col.GetProperty("header").GetString())
                .Should().Equal("Control Number", "File Name", "File Size");
        }

        // Not downloadable before it completes; another user's export does not exist for the caller.
        await (await SendAsync(client, HttpMethod.Get, $"{root}/list-exports/{exportId}/content", manager)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");
        await (await SendAsync(client, HttpMethod.Get, $"{root}/list-exports/{exportId}", otherManager)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        // Q-15: access changes after the request are applied when the job runs.
        await db.WallAsync(ws, [manager], [], [b.DocumentId]);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ListExportRunner>().RunToEndAsync(ws, exportId, cancellationToken: Ct))
                .Should().Be(ListExportStatus.Completed);
        }

        using (var job = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"{root}/jobs/{jobId}", manager)))
        {
            job.RootElement.GetProperty("jobType").GetString().Should().Be("listExport");
            job.RootElement.GetProperty("status").GetString().Should().Be("completed");
            job.RootElement.GetProperty("committed").GetProperty("chunksTotal").GetInt64().Should().Be(2, "two documents per chunk");
            job.RootElement.GetProperty("committed").GetProperty("itemsExcludedNoAccess").GetInt64().Should().Be(1);
        }

        string sha;
        using (var export = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"{root}/list-exports/{exportId}", manager)))
        {
            export.RootElement.GetProperty("status").GetString().Should().Be("completed");
            export.RootElement.GetProperty("rowCount").GetInt64().Should().Be(2);
            export.RootElement.GetProperty("documentsExcluded").GetInt64().Should().Be(1);
            sha = export.RootElement.GetProperty("contentSha256").GetString()!;
        }

        using (var list = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"{root}/list-exports", manager)))
        {
            list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("exportId").GetGuid()).Should().Equal(exportId);
        }

        (await JsonAsync(await SendAsync(client, HttpMethod.Get, $"{root}/list-exports", otherManager))).RootElement
            .GetProperty("items").GetArrayLength().Should().Be(0, "exports are their requester's");
        await (await SendAsync(client, HttpMethod.Get, $"{root}/list-exports/{exportId}/content", otherManager))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        using (var download = await SendAsync(client, HttpMethod.Get, $"{root}/list-exports/{exportId}/content", manager))
        {
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            download.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
            download.Content.Headers.ContentDisposition!.FileName.Should().Contain("export-list-");
            download.Headers.CacheControl!.NoStore.Should().BeTrue();
            download.Headers.Contains("X-Opportunity-Retrieval-Id").Should().BeTrue();
            var bytes = await download.Content.ReadAsByteArrayAsync(Ct);
            Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(sha);
            bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
            Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Should().Be(
                "Control Number,File Name,File Size\r\n" +
                "LX0001,\"'=HYPERLINK(\"\"x\"\"), memo.msg\",2048\r\n" +
                "LX0003,Zoë plan.pdf,\r\n");
        }

        // Requesting, the Q-15 exclusion (precise reason, in audit only) and each download are audited.
        var events = (await AuditSamples.ReadAllAsync(db.Core, ws)).Select(e => e.Event).ToList();
        events.Should().ContainSingle(e => e.Category == "Export" && e.Action == "ListRequested" && e.ResourceId == exportId.ToString()
            && e.ResourceType == "ListExport" && e.JobId == jobId && e.ActorId == manager.ToString() && e.Details["Documents"] == "3");
        events.Should().ContainSingle(e => e.Category == "Export" && e.Action == "DocumentsExcluded" && e.ResourceId == exportId.ToString()
            && e.OnBehalfOf == manager && e.Details["Documents"]!.StartsWith(b.DocumentId.ToString("N") + ":", StringComparison.Ordinal));
        events.Should().ContainSingle(e => e.Category == "Export" && e.Action == "ListDownloaded" && e.ResourceId == exportId.ToString()
            && e.Details["Sha256"] == sha && e.Details["Rows"] == "2");
    }

    [Fact]
    public async Task A_requester_who_lost_the_permission_gets_a_failed_export_and_break_glass_cannot_export()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        await db.Core.Fields.InitializeWorkspaceAsync(ws, Ct);
        var manager = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.ProductionManager, manager);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, manager);
        var document = await db.DocumentAsync(ws);

        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}";
        Guid exportId;
        using (var created = await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/list-exports", manager,
            JsonSerializer.Serialize(new { fieldIds = Array.Empty<int>(), documentIds = new[] { document } }))))
        {
            exportId = created.RootElement.GetProperty("exportId").GetGuid();
        }

        await db.Core.ExecuteAsync(
            "DELETE FROM opportunity.workspace_role_assignment WHERE workspace_id = @ws AND user_id = @user AND role = 'ProductionManager'",
            ("ws", ws), ("user", manager));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ListExportRunner>().RunToEndAsync(ws, exportId, cancellationToken: Ct))
                .Should().Be(ListExportStatus.Failed);
            (await scope.ServiceProvider.GetRequiredService<IListExportStore>().GetAsync(ws, exportId, Ct))!.StatusReason
                .Should().Contain("may no longer export");
        }

        // Without the permission the export's routes are refused, its file included.
        await (await SendAsync(client, HttpMethod.Get, $"{root}/list-exports/{exportId}/content", manager)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        var glass = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.BreakGlass, glass);
        await db.ActivateBreakGlassAsync(ws, glass, TimeSpan.FromMinutes(30));
        await (await SendAsync(client, HttpMethod.Post, root + "/list-exports", glass,
            JsonSerializer.Serialize(new { fieldIds = Array.Empty<int>(), query = "" }))).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    private static WebApplicationFactory<Program> Factory(AuthorizationDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            builder.UseSetting("ListExports:BackgroundEnabled", "false");
            builder.UseSetting("ListExports:DocumentsPerChunk", "2");
            builder.UseSetting("AuditExports:BackgroundEnabled", "false");
            builder.UseSetting("SearchTermReports:BackgroundEnabled", "false");
            builder.UseSetting("Snapshots:BackgroundEnabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
            });
        });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, Guid user, string? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.IsSuccessStatusCode.Should().BeTrue(text);
            return JsonDocument.Parse(text);
        }
    }
}
