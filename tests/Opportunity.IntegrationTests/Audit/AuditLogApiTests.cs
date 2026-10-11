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

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Data.Audit;
using Opportunity.Data.Jobs;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>
/// E14-T04 through the real API host, the PostgreSQL PDP and audit store: the audit log viewer's filters combine and its
/// results come one cursor page at a time (never all at once), newest first; every page read is itself audited; search
/// text needs Audit.ReadSearchText; walls apply to the reader (ADR-013 §8.3); and the audit export runs as a job whose
/// CSV is downloaded through the gateway by its requester, each download audited.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class AuditLogApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Filters_combine_pages_come_by_cursor_newest_first_reads_are_audited_and_walls_hide_document_events()
    {
        await using var world = await WorldAsync();
        var (db, ws) = (world.Db, world.Workspace);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}/audit-events?from={Q(world.Start)}&to={Q(world.Start.AddMinutes(1))}";

        // Six events in the window: two pages of 2 and one of 2, newest first, each page a fresh cursor; never all at once.
        var seen = new List<JsonElement>();
        string? cursor = null;
        string? firstCursor = null;
        var pages = 0;
        do
        {
            using var page = await JsonAsync(await SendAsync(client, HttpMethod.Get, root + "&limit=2" + (cursor is null ? string.Empty : "&cursor=" + cursor), world.Auditor));
            var items = page.RootElement.GetProperty("items");
            items.GetArrayLength().Should().BeLessThanOrEqualTo(2);
            seen.AddRange(items.EnumerateArray().Select(i => i.Clone()));
            cursor = page.RootElement.GetProperty("nextCursor").GetString();
            firstCursor ??= cursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.Should().Be(3);
        seen.Select(e => e.GetProperty("eventId").GetGuid()).Should().Equal(world.Events.AsEnumerable().Reverse().Select(e => e.EventId));
        var viewed = seen.Single(e => e.GetProperty("eventId").GetGuid() == world.Events[0].EventId);
        viewed.GetProperty("resource").GetProperty("controlNumber").GetString().Should().Be(world.ControlNumber1, "audit stores IDs; the viewer resolves them");
        seen.Single(e => e.GetProperty("action").GetString() == "Executed").GetProperty("searchText").ValueKind.Should().Be(JsonValueKind.Null);

        // A cursor is bound to its filters.
        await (await SendAsync(client, HttpMethod.Get, root + "&outcome=denied&limit=2&cursor=" + firstCursor, world.Auditor))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        // Filters combine (AND).
        (await IdsAsync(client, root + "&category=Document&action=Viewed&actorId=" + world.Reviewer, world.Auditor))
            .Should().Equal(world.Events[1].EventId, world.Events[0].EventId);
        (await IdsAsync(client, root + "&category=Document&action=Viewed&controlNumber=" + Uri.EscapeDataString(world.ControlNumber1.ToLowerInvariant()), world.Auditor))
            .Should().Equal(world.Events[0].EventId);
        (await IdsAsync(client, root + "&documentId=" + world.Document1 + "&outcome=denied", world.Auditor)).Should().Equal(world.Events[5].EventId);
        (await IdsAsync(client, root + "&jobId=" + world.JobId, world.Auditor)).Should().Equal(world.Events[3].EventId);
        (await IdsAsync(client, root + "&jobId=" + world.JobId + "&category=Document", world.Auditor)).Should().BeEmpty();
        await (await SendAsync(client, HttpMethod.Get, root + "&action=Viewed", world.Auditor)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await SendAsync(client, HttpMethod.Get, root + "&jobId=" + Guid.CreateVersion7(), world.Auditor)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        // Search text (Q-16) only on request, with Audit.ReadSearchText.
        using (var page = await JsonAsync(await SendAsync(client, HttpMethod.Get, root + "&category=Search&includeSearchText=true", world.Auditor)))
        {
            page.RootElement.GetProperty("items")[0].GetProperty("searchText").GetProperty("query").GetString().Should().Be("privileged AND memo");
        }

        // Walls apply to the reader: the walled auditor never sees document 2's events, nor finds it by Control Number.
        (await IdsAsync(client, root, world.WalledAuditor)).Should().NotContain(world.Events[1].EventId).And.Contain(world.Events[0].EventId);
        await (await SendAsync(client, HttpMethod.Get, root + "&controlNumber=" + world.ControlNumber2, world.WalledAuditor))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        // Reviewers cannot read audit; non-members get the usual 404.
        await (await SendAsync(client, HttpMethod.Get, root, world.Reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Get, root, await db.CreateUserAsync())).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        // Break-glass may read audit, but neither search text nor export it (Q-45).
        var glass = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.BreakGlass, glass);
        await db.ActivateBreakGlassAsync(ws, glass, TimeSpan.FromMinutes(30));
        (await IdsAsync(client, root, glass)).Should().Contain(world.Events[1].EventId);
        await (await SendAsync(client, HttpMethod.Get, root + "&includeSearchText=true", glass)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/audit-exports", glass, "{}"))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Reading the audit trail is itself audited: one Audit.Queried per page, with the filters; break-glass reads say so.
        var events = await AuditSamples.ReadAllAsync(db.Core, ws);
        var queried = events.Where(e => e.Event is { Category: "Audit", Action: "Queried" }).Select(e => e.Event).ToList();
        queried.Count(e => e.ActorId == world.Auditor.ToString()).Should().BeGreaterThanOrEqualTo(pages + 6);
        queried.Should().Contain(e => e.ActorId == world.Auditor.ToString() && e.Details.GetValueOrDefault("JobId") == world.JobId.ToString());
        queried.Should().Contain(e => e.ActorId == glass.ToString() && e.AccessPath == AuditAccessPath.BreakGlass);
    }

    [Fact]
    public async Task An_audit_export_runs_as_a_job_and_its_csv_is_downloaded_by_its_requester_with_both_steps_audited()
    {
        await using var world = await WorldAsync();
        var (db, ws) = (world.Db, world.Workspace);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}";
        var body = JsonSerializer.Serialize(new { from = world.Start, to = world.Start.AddMinutes(1), includeSearchText = true });

        Guid exportId, jobId;
        using (var created = await SendAsync(client, HttpMethod.Post, root + "/audit-exports", world.WalledAuditor, body))
        {
            created.StatusCode.Should().Be(HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync(Ct));
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
            exportId = json.RootElement.GetProperty("exportId").GetGuid();
            jobId = json.RootElement.GetProperty("jobId").GetGuid();
            created.Headers.Location!.ToString().Should().Be($"{root}/jobs/{jobId}");
            json.RootElement.GetProperty("status").GetString().Should().Be("queued");
        }

        // Not downloadable before it completes; another user's export does not exist for the caller.
        await (await SendAsync(client, HttpMethod.Get, $"{root}/audit-exports/{exportId}/content", world.WalledAuditor))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");
        await (await SendAsync(client, HttpMethod.Get, $"{root}/audit-exports/{exportId}", world.Auditor)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<AuditExportRunner>().RunToEndAsync(ws, exportId, cancellationToken: Ct))
                .Should().Be(AuditExportStatus.Completed);
        }

        // Five of the six events (document 2 is walled from the requester), in windows of two: three chunks.
        using (var job = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"{root}/jobs/{jobId}", world.WalledAuditor)))
        {
            job.RootElement.GetProperty("jobType").GetString().Should().Be("auditExport");
            job.RootElement.GetProperty("status").GetString().Should().Be("completed");
            job.RootElement.GetProperty("committed").GetProperty("chunksTotal").GetInt64().Should().Be(3);
        }

        string sha;
        using (var export = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"{root}/audit-exports/{exportId}", world.WalledAuditor)))
        {
            export.RootElement.GetProperty("status").GetString().Should().Be("completed");
            export.RootElement.GetProperty("rowCount").GetInt64().Should().Be(5);
            sha = export.RootElement.GetProperty("contentSha256").GetString()!;
        }

        using (var list = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"{root}/audit-exports", world.WalledAuditor)))
        {
            list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("exportId").GetGuid()).Should().Equal(exportId);
        }

        (await JsonAsync(await SendAsync(client, HttpMethod.Get, $"{root}/audit-exports", world.Auditor))).RootElement
            .GetProperty("items").GetArrayLength().Should().Be(0, "exports are their requester's");
        await (await SendAsync(client, HttpMethod.Get, $"{root}/audit-exports/{exportId}/content", world.Auditor))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        using (var download = await SendAsync(client, HttpMethod.Get, $"{root}/audit-exports/{exportId}/content", world.WalledAuditor))
        {
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            download.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
            download.Headers.CacheControl!.NoStore.Should().BeTrue();
            download.Headers.Contains("X-Opportunity-Retrieval-Id").Should().BeTrue();
            var bytes = await download.Content.ReadAsByteArrayAsync(Ct);
            Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(sha);
            bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
            var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            lines.Should().HaveCount(6);
            lines[0].Should().StartWith("Occurred At (UTC),Event ID,Category,Action,").And.EndWith(",Search Text");
            lines.Skip(1).Select(l => Guid.Parse(l.Split(',')[1])).Should().Equal(
                world.Events.Where(e => e.ResourceId != world.Document2.ToString()).Select(e => e.EventId), "oldest first, without walled documents");
            lines.Should().Contain(l => l.Contains(world.ControlNumber1, StringComparison.Ordinal));
            lines.Should().Contain(l => l.Contains("privileged AND memo", StringComparison.Ordinal));
            lines.Should().NotContain(l => l.Contains(world.ControlNumber2, StringComparison.Ordinal));
        }

        // Requesting the export and each download are audited (and the job is the export's).
        var events = (await AuditSamples.ReadAllAsync(db.Core, ws)).Select(e => e.Event).ToList();
        events.Should().ContainSingle(e => e.Category == "Audit" && e.Action == "Exported" && e.ResourceId == exportId.ToString()
            && e.JobId == jobId && e.Details["IncludeSearchText"] == "true" && e.ActorId == world.WalledAuditor.ToString());
        events.Should().ContainSingle(e => e.Category == "Audit" && e.Action == "ExportDownloaded" && e.ResourceId == exportId.ToString()
            && e.Details["Sha256"] == sha && e.Details["Rows"] == "5");
        events.Should().Contain(e => e.Category == "Job" && e.Action == "Created" && e.JobId == jobId);
    }

    /// <summary>A workspace with an auditor, a walled auditor, a reviewer, two documents and six events a minute apart from now - 1 h.</summary>
    private async Task<AuditWorld> WorldAsync()
    {
        var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var auditor = await db.CreateUserAsync();
        var walled = await db.CreateUserAsync();
        var reviewer = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Auditor, auditor);
        await db.AssignAsync(ws, WorkspaceRole.Auditor, walled);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        var document1 = await db.DocumentAsync(ws);
        var document2 = await db.DocumentAsync(ws);
        await db.WallAsync(ws, [walled], [], [document2]);
        var job = await new JobRepository(db.Core.AppDataSource).CreateAsync(
            new NewJob { WorkspaceId = ws, JobType = JobType.BulkCoding, InitiatedBy = reviewer }, Ct);

        var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
        AuditEvent Event(int second, string category, string action, string? resourceType, Guid? resource) => new()
        {
            WorkspaceId = ws,
            OccurredAt = start.AddSeconds(second),
            Category = category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = reviewer.ToString(),
            ActorDisplay = "Rev Iewer",
            ResourceType = resourceType,
            ResourceId = resource?.ToString(),
            Outcome = AuditOutcome.Success,
        };

        AuditEvent[] events =
        [
            Event(1, "Document", "Viewed", "Document", document1),
            Event(2, "Document", "Viewed", "Document", document2),
            Event(3, "Coding", "Changed", "Document", document1),
            Event(4, "Coding", "BulkChunkApplied", "Job", job.Job.JobId) with { JobId = job.Job.JobId, ChunkSequence = 1 },
            Event(5, "Search", "Executed", "Search", Guid.CreateVersion7()) with
            {
                RestrictedDetails = new Dictionary<string, string?> { ["query"] = "privileged AND memo" },
            },
            Event(6, "AuthZ", "Denied", "Document", document1) with { Outcome = AuditOutcome.Denied, ReasonCode = "PermissionNotGranted" },
        ];
        var writer = new PostgresAuditEventWriter(db.Core.AppDataSource);
        foreach (var e in events)
        {
            await writer.WriteAsync(e, Ct);
        }

        var cn1 = await db.Core.ScalarAsync<string>("SELECT control_number FROM opportunity.document WHERE workspace_id = @ws AND document_id = @id",
            ("ws", ws), ("id", document1));
        var cn2 = await db.Core.ScalarAsync<string>("SELECT control_number FROM opportunity.document WHERE workspace_id = @ws AND document_id = @id",
            ("ws", ws), ("id", document2));
        return new AuditWorld(db, ws, auditor, walled, reviewer, document1, document2, cn1, cn2, job.Job.JobId, start, events);
    }

    private sealed record AuditWorld(
        AuthorizationDatabase Db, Guid Workspace, Guid Auditor, Guid WalledAuditor, Guid Reviewer, Guid Document1, Guid Document2,
        string ControlNumber1, string ControlNumber2, Guid JobId, DateTimeOffset Start, IReadOnlyList<AuditEvent> Events) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static string Q(DateTimeOffset time) => Uri.EscapeDataString(time.ToString("O"));

    private static async Task<List<Guid>> IdsAsync(HttpClient client, string url, Guid user)
    {
        using var page = await JsonAsync(await SendAsync(client, HttpMethod.Get, url + "&limit=200", user));
        return [.. page.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("eventId").GetGuid())];
    }

    private static WebApplicationFactory<Program> Factory(AuthorizationDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            builder.UseSetting("AuditExports:BackgroundEnabled", "false");
            builder.UseSetting("AuditExports:EventsPerChunk", "2");
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
