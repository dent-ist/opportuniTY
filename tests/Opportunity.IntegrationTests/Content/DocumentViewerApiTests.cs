using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Content;
using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Coding;
using Opportunity.Application.Content;
using Opportunity.Application.Fields;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Pages;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Content;

/// <summary>
/// E11-T01 through the real API host, the PostgreSQL PDP and a filesystem object store: the viewer's metadata, page
/// list and chunked text, each decided per document and audited as <c>Document.Retrieved</c> before anything is
/// returned; hidden documents answer the same 404; a committed security change hides a document on the next request
/// while indexing is stopped; a 10 MB text streams in bounded chunks; large natives are served in ranges.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class DocumentViewerApiTests(MigrationPostgresFixture postgres)
{
    private readonly InMemoryAuditEventWriter _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Metadata_shows_system_imported_and_coding_fields_with_display_values_and_artifacts_and_is_audited()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var fields = db.Security.Core.Fields;
        await fields.InitializeWorkspaceAsync(ws, Ct);
        var custodian = (await fields.CreateFieldAsync(new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata), Ct)).Value!.FieldId;
        var received = (await fields.CreateFieldAsync(
            new NewField(ws, "Received On", FieldType.Date, FieldStorage.Metadata, DatePrecision: DatePrecision.DateTime), Ct)).Value!.FieldId;
        var responsiveness = (await fields.CreateFieldAsync(new NewField(ws, "Responsiveness", FieldType.SingleChoice, FieldStorage.Coding), Ct)).Value!.FieldId;
        var responsive = (await fields.AddChoiceAsync(ws, responsiveness, "Responsive", Ct)).Value!.ChoiceId;
        var doc = await db.DocumentAsync(ws);
        await db.Security.Core.ExecuteAsync(
            """
            UPDATE opportunity.document
            SET metadata = jsonb_build_object('f' || @custodian, 'Smith, Alex', 'f' || @received, '2024-07-01T13:30:00Z'),
                metadata_raw = jsonb_build_object('f' || @received, jsonb_build_object('raw', '07/01/2024 09:30', 'fmt', 'M/d/yyyy H:mm')),
                file_size = 1234567, date_sent = '2024-03-01T14:05:00Z', text_truncated = true
            WHERE workspace_id = @ws AND document_id = @doc
            """,
            ("custodian", custodian), ("received", received), ("ws", ws), ("doc", doc.DocumentId));
        var coding = await db.Security.Core.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "responsive-" + doc.DocumentId,
            Actor = new CodingActor(reviewer, CodingActorType.Human),
            Documents = [new CodingTarget(doc.DocumentId)],
            Operations = [CodingFieldOperation.Set(responsiveness, JsonValue.Create(responsive))],
        }, Ct);
        coding.Outcome.Should().Be(CodingWriteOutcome.Applied);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        using var response = await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}", reviewer);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertJsonHeaders(response);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        body["documentId"]!.GetValue<Guid>().Should().Be(doc.DocumentId);
        body["controlNumber"]!.GetValue<string>().Should().Be(doc.ControlNumber);
        body["displayTimeZone"]!.GetValue<string>().Should().Be("America/New_York");
        body["text"]!.ToJsonString().Should().Be(new JsonObject
        {
            ["available"] = true,
            ["missing"] = false,
            ["truncated"] = true,
            ["encodingWarning"] = false,
            ["sizeBytes"] = doc.Text.Length,
            ["length"] = null,
            ["chunkSizeBytes"] = TextChunks.ChunkBytes,
            ["chunkCount"] = 1,
        }.ToJsonString());
        body["native"]!["available"]!.GetValue<bool>().Should().BeTrue();
        body["native"]!["sizeBytes"]!.GetValue<long>().Should().Be(doc.Native.Length);
        body["images"]!["available"]!.GetValue<bool>().Should().BeTrue();
        body["images"]!["pageCount"]!.GetValue<int>().Should().Be(1);
        body["images"]!["source"]!.GetValue<string>().Should().Be("imported");

        var byId = body["fields"]!.AsArray().ToDictionary(f => f!["fieldId"]!.GetValue<int>(), f => f!);
        Field(byId[SystemFields.ControlNumber], "Short Text", "text", doc.ControlNumber);
        Field(byId[SystemFields.FileSize], "Whole Number", "bytes", "1,234,567 bytes");
        Field(byId[SystemFields.DateSent], "Date", "dateTime", "2024-03-01 09:05:00 -05:00");
        byId[SystemFields.DateSent]["value"]!.GetValue<string>().Should().Be("2024-03-01T14:05:00Z");
        Field(byId[SystemFields.TextTruncated], "Yes/No", "boolean", "Yes");
        Field(byId[custodian], "Short Text", "text", "Smith, Alex");
        Field(byId[received], "Date", "dateTime", "2024-07-01 09:30:00 -04:00");
        byId[received]["rawValue"]!.GetValue<string>().Should().Be("07/01/2024 09:30");
        Field(byId[responsiveness], "Single Choice", "choice", "Responsive");
        byId[responsiveness]["choices"]!.AsArray().Select(c => c!["choiceId"]!.GetValue<int>()).Should().Equal(responsive);
        byId[SystemFields.BegBates]["value"].Should().BeNull("fields without a value are listed with a null value");

        var e = _audit.Events.Should().ContainSingle().Subject;
        (e.Category, e.Action, e.Outcome, e.ResourceId).Should().Be(("Document", "Retrieved", AuditOutcome.Success, doc.DocumentId.ToString()));
        (e.Details["rendition"], e.Details["purpose"], e.Details["permission"]).Should().Be(("Metadata", "Display", "Document.View"));
        response.Headers.GetValues(ProtectedContentGateway.RetrievalIdHeader).Should().Equal(e.EventId.ToString());

        _audit.Clear();
        (await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}?purpose=prefetch", reviewer))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        _audit.Events.Should().ContainSingle().Which.Details["purpose"].Should().Be("Prefetch");
        (await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}?purpose=print", reviewer))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Restricted_fields_are_left_out_of_the_metadata()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        await db.Security.Core.Fields.InitializeWorkspaceAsync(ws, Ct);
        var doc = await db.DocumentAsync(ws);
        await using var factory = Factory(db).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IFieldAccessFilter>();
            s.AddSingleton<IFieldAccessFilter>(new HidingFilter(SystemFields.Md5));
        }));
        using var client = factory.CreateClient();

        using var response = await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}", reviewer);

        var ids = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["fields"]!.AsArray().Select(f => f!["fieldId"]!.GetValue<int>()).ToList();
        ids.Should().Contain(SystemFields.ControlNumber).And.NotContain(SystemFields.Md5);
    }

    [Fact]
    public async Task A_10_mb_text_streams_in_bounded_chunks_that_reassemble_exactly_and_each_chunk_is_audited()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var text = LargeText(10 * 1024 * 1024);
        var bytes = Encoding.UTF8.GetBytes(text);
        var documentId = await db.ArtifactDocumentAsync(ws, bytes);
        var warmup = await db.ArtifactDocumentAsync(ws, Encoding.UTF8.GetBytes("warm-up"));
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var basePath = $"/api/v1/workspaces/{ws}/documents/{documentId}";
        (await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{warmup}/text/chunks/0", reviewer))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        _audit.Clear();

        var clock = Stopwatch.StartNew();
        using var first = await ProtectedContentGatewayTests.GetAsync(client, $"{basePath}/text/chunks/0", reviewer);
        var firstBody = await first.Content.ReadAsStringAsync(Ct);
        clock.Stop();

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertJsonHeaders(first);
        firstBody.Length.Should().BeLessThan(2 * TextChunks.ChunkBytes, "a chunk is bounded, whatever the size of the text");
        TestContext.Current.TestOutputHelper?.WriteLine($"first chunk of a {bytes.Length:N0}-byte text: {clock.Elapsed.TotalMilliseconds:F0} ms");
        if (Environment.GetEnvironmentVariable("OPPORTUNITY_STRICT_LATENCY") == "1")
        {
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(300), "the first chunk of a 10 MB text returns in < 300 ms (E11-T01)");
        }

        var chunk = JsonNode.Parse(firstBody)!;
        var count = chunk["chunkCount"]!.GetValue<int>();
        count.Should().Be(TextChunks.Count(bytes.Length)).And.BeGreaterThan(30);
        chunk["totalBytes"]!.GetValue<long>().Should().Be(bytes.Length);
        chunk["chunkSizeBytes"]!.GetValue<int>().Should().Be(TextChunks.ChunkBytes);
        var assembled = new StringBuilder(chunk["text"]!.GetValue<string>());
        var end = chunk["byteEnd"]!.GetValue<long>();
        for (var n = 1; n < count; n++)
        {
            using var next = await ProtectedContentGatewayTests.GetAsync(client, $"{basePath}/text/chunks/{n}?purpose=prefetch", reviewer);
            var body = JsonNode.Parse(await next.Content.ReadAsStringAsync(Ct))!;
            body["byteStart"]!.GetValue<long>().Should().Be(end, "chunk {0} continues where the previous one ended", n);
            body["isLast"]!.GetValue<bool>().Should().Be(n == count - 1);
            end = body["byteEnd"]!.GetValue<long>();
            assembled.Append(body["text"]!.GetValue<string>());
        }

        end.Should().Be(bytes.Length);
        assembled.ToString().Should().Be(text, "chunks never split a character and concatenate to the whole text");
        _audit.Events.Should().HaveCount(count).And.OnlyContain(e => e.Action == "Retrieved" && e.Details["rendition"] == "Text");
        _audit.Events.Select(e => e.Details["chunk"]).Should().Equal(Enumerable.Range(0, count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        _audit.Events.Skip(1).Should().OnlyContain(e => e.Details["purpose"] == "Prefetch");

        await (await ProtectedContentGatewayTests.GetAsync(client, $"{basePath}/text/chunks/{count}", reviewer))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "content-unavailable");
        await (await ProtectedContentGatewayTests.GetAsync(client, $"{basePath}/text/chunks/-1", reviewer))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task Missing_and_truncated_text_are_reported_as_flags()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var noText = await db.ArtifactDocumentAsync(ws, null);
        await db.Security.Core.ExecuteAsync(
            "UPDATE opportunity.document SET text_missing = true WHERE workspace_id = @ws AND document_id = @doc", ("ws", ws), ("doc", noText));
        var truncated = await db.ArtifactDocumentAsync(ws, Encoding.UTF8.GetBytes("Only the start of this text is searchable."));
        await db.Security.Core.ExecuteAsync(
            "UPDATE opportunity.document SET text_truncated = true WHERE workspace_id = @ws AND document_id = @doc", ("ws", ws), ("doc", truncated));
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        using var missing = await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{noText}/text/chunks/0", reviewer);
        missing.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await missing.Content.ReadAsStringAsync(Ct))!;
        (body["missing"]!.GetValue<bool>(), body["chunkCount"]!.GetValue<int>(), body["text"]!.GetValue<string>()).Should().Be((true, 0, string.Empty));
        missing.Headers.CacheControl!.NoStore.Should().BeTrue();
        await (await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{noText}/text/chunks/1", reviewer))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "content-unavailable");

        using var metadata = await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{noText}", reviewer);
        var info = JsonNode.Parse(await metadata.Content.ReadAsStringAsync(Ct))!;
        (info["text"]!["available"]!.GetValue<bool>(), info["text"]!["missing"]!.GetValue<bool>(), info["text"]!["chunkCount"]!.GetValue<int>())
            .Should().Be((false, true, 0));
        (info["native"]!["available"]!.GetValue<bool>(), info["images"]!["available"]!.GetValue<bool>()).Should().Be((false, false));

        using var cut = await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{truncated}/text/chunks/0", reviewer);
        var cutBody = JsonNode.Parse(await cut.Content.ReadAsStringAsync(Ct))!;
        (cutBody["truncated"]!.GetValue<bool>(), cutBody["missing"]!.GetValue<bool>(), cutBody["isLast"]!.GetValue<bool>()).Should().Be((true, false, true));
        cutBody["text"]!.GetValue<string>().Should().Be("Only the start of this text is searchable.");
    }

    [Fact]
    public async Task The_page_list_gives_geometry_and_servable_images_in_cursor_pages()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var other = await Member(db, ws, WorkspaceRole.Reviewer);
        var doc = await db.DocumentAsync(ws);
        await db.AddPageAsync(ws, doc.DocumentId, 2, PageImagePurpose.Original, PageImageFormat.TiffG4);
        await db.AddPageAsync(ws, doc.DocumentId, 3, imageMissing: true);
        var noPages = await db.ArtifactDocumentAsync(ws, null);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var path = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}/pages";

        using var firstPage = await ProtectedContentGatewayTests.GetAsync(client, path + "?limit=2", reviewer);
        firstPage.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertJsonHeaders(firstPage);
        var first = JsonNode.Parse(await firstPage.Content.ReadAsStringAsync(Ct))!;
        first["total"]!["value"]!.GetValue<long>().Should().Be(3);
        var items = first["items"]!.AsArray();
        items.Select(p => p!["pageNumber"]!.GetValue<int>()).Should().Equal(1, 2);
        (items[0]!["hasImage"]!.GetValue<bool>(), items[0]!["imageContentType"]!.GetValue<string>(), items[0]!["hasThumbnail"]!.GetValue<bool>())
            .Should().Be((true, "image/png", true));
        (items[0]!["widthPt"]!.GetValue<decimal>(), items[0]!["heightPt"]!.GetValue<decimal>(), items[0]!["colorMode"]!.GetValue<string>())
            .Should().Be((612m, 792m, "color"));
        (items[1]!["hasImage"]!.GetValue<bool>(), items[1]!["imageContentType"], items[1]!["rotation"]!.GetValue<int>())
            .Should().Be((false, (JsonNode?)null, 90), "a TIFF original is not servable until it is rendered");
        var cursor = first["nextCursor"]!.GetValue<string>();

        using var secondPage = await ProtectedContentGatewayTests.GetAsync(client, $"{path}?limit=2&cursor={cursor}", reviewer);
        var second = JsonNode.Parse(await secondPage.Content.ReadAsStringAsync(Ct))!;
        second["items"]!.AsArray().Select(p => p!["pageNumber"]!.GetValue<int>()).Should().Equal(3);
        second["items"]![0]!["imageMissing"]!.GetValue<bool>().Should().BeTrue();
        second["nextCursor"].Should().BeNull();

        await (await ProtectedContentGatewayTests.GetAsync(client, $"{path}?limit=2&cursor={cursor}", other))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await ProtectedContentGatewayTests.GetAsync(client, $"{path}?limit=0", reviewer)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        _audit.Events.Should().HaveCount(2).And.OnlyContain(e => e.Action == "Retrieved" && e.Details["rendition"] == "PageList");

        using var none = await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{noPages}/pages", reviewer);
        var empty = JsonNode.Parse(await none.Content.ReadAsStringAsync(Ct))!;
        (empty["items"]!.AsArray().Count, empty["total"]!["value"]!.GetValue<long>()).Should().Be((0, 0L));
    }

    [Fact]
    public async Task Hidden_unknown_malformed_and_foreign_documents_get_the_same_404_on_every_viewer_route()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var other = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        await db.Security.AssignAsync(other, WorkspaceRole.WorkspaceAdmin, reviewer);
        var aeo = await db.DocumentAsync(ws, [RestrictionClasses.AttorneysEyesOnly]);
        var walled = await db.DocumentAsync(ws);
        await db.Security.WallAsync(ws, [reviewer], [], [walled.DocumentId]);
        var foreign = await db.DocumentAsync(other);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        var targets = new (string Id, string? Reason)[]
        {
            (aeo.DocumentId.ToString(), AuthorizationReasons.RestrictionClass),
            (walled.DocumentId.ToString(), AuthorizationReasons.EthicalWall),
            (foreign.DocumentId.ToString(), AuthorizationReasons.DocumentNotFound),
            (Guid.CreateVersion7().ToString(), AuthorizationReasons.DocumentNotFound),
            ("not-a-guid", null),
        };
        var bodies = new HashSet<string>();
        foreach (var route in new[] { string.Empty, "/pages", "/text/chunks/0" })
        {
            foreach (var (id, reason) in targets)
            {
                _audit.Clear();
                using var response = await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{id}{route}", reviewer);
                var problem = JsonNode.Parse((await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found")).GetRawText())!.AsObject();
                problem.Remove("traceId");
                bodies.Add(problem.ToJsonString());
                if (reason is null)
                {
                    _audit.Events.Should().BeEmpty();
                }
                else
                {
                    var e = _audit.Events.Should().ContainSingle($"{route} {id}").Subject;
                    (e.Category, e.Action, e.Outcome, e.ReasonCode).Should().Be(("Document", "Retrieved", AuditOutcome.Denied, reason));
                }
            }
        }

        bodies.Should().ContainSingle("a reviewer cannot tell a hidden document from a missing one on any route");
    }

    [Fact]
    public async Task A_stale_search_hit_for_a_newly_privileged_document_cannot_be_opened_while_indexing_is_stopped()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var manager = await Member(db, ws, WorkspaceRole.ProductionManager);
        var doc = await db.DocumentAsync(ws);
        var fields = db.Security.Core.Fields;
        await fields.InitializeWorkspaceAsync(ws, Ct);
        var privilege = (await fields.CreateFieldAsync(new NewField(ws, "Privilege Call", FieldType.SingleChoice, FieldStorage.Coding,
            SecurityClass: SecurityClass.PrivilegeStatus), Ct)).Value!.FieldId;
        var privileged = (await fields.AddChoiceAsync(ws, privilege, "Attorneys' Eyes Only", Ct)).Value!.ChoiceId;
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var basePath = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}";
        string[] routes = [basePath, $"{basePath}/pages", $"{basePath}/text/chunks/0", $"{basePath}/pages/1/image"];
        foreach (var route in routes)
        {
            (await ProtectedContentGatewayTests.GetAsync(client, route, reviewer)).StatusCode.Should().Be(HttpStatusCode.OK, route);
        }

        // The search hit the reviewer holds is now stale: the privilege coding commits, no index worker runs, so the
        // projection still shows the document as unrestricted. The restriction class is committed with it (E05-T06).
        var write = await db.Security.Core.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "privilege-" + doc.DocumentId,
            Actor = new CodingActor(manager, CodingActorType.Human),
            Documents = [new CodingTarget(doc.DocumentId)],
            Operations = [CodingFieldOperation.Set(privilege, JsonValue.Create(privileged))],
        }, Ct);
        write.Outcome.Should().Be(CodingWriteOutcome.Applied);
        await db.Security.Core.ExecuteAsync(
            "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, 'AttorneysEyesOnly')",
            ("ws", ws), ("doc", doc.DocumentId));
        (await db.Security.Core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND document_id = @doc AND status <> 4",
            ("ws", ws), ("doc", doc.DocumentId))).Should().BeGreaterThan(0, "the search projection has not caught up");

        _audit.Clear();
        foreach (var route in routes)
        {
            await (await ProtectedContentGatewayTests.GetAsync(client, route + "?purpose=prefetch", reviewer)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        }

        _audit.Events.Should().HaveCount(routes.Length).And.OnlyContain(e => e.Outcome == AuditOutcome.Denied && e.ReasonCode == AuthorizationReasons.RestrictionClass);
        (await ProtectedContentGatewayTests.GetAsync(client, basePath, manager)).StatusCode.Should().Be(HttpStatusCode.OK, "AEO is granted to Production Manager");
    }

    [Fact]
    public async Task Large_natives_are_served_in_ranges_and_every_range_is_audited_as_a_download()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var manager = await Member(db, ws, WorkspaceRole.ProductionManager);
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var native = new byte[6 * 1024 * 1024];
        new Random(95).NextBytes(native);
        var documentId = await db.ArtifactDocumentAsync(ws, null, native);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var path = $"/api/v1/workspaces/{ws}/documents/{documentId}/native";

        using var middle = await ProtectedContentGatewayTests.GetAsync(client, path, manager, range: "bytes=3000000-4048575");
        middle.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await middle.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(native[3_000_000..4_048_576]);
        middle.Content.Headers.ContentRange!.ToString().Should().Be($"bytes 3000000-4048575/{native.Length}");
        ProtectedContentGatewayTests.AssertProtectedContentHeaders(middle);
        middle.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");

        using var whole = await ProtectedContentGatewayTests.GetAsync(client, path, manager);
        (await whole.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(native);

        await (await ProtectedContentGatewayTests.GetAsync(client, path, reviewer, range: "bytes=0-99")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        _audit.Events.Select(e => (e.Action, e.Outcome, e.Details.GetValueOrDefault("range"))).Should().Equal(
            ("NativeDownloaded", AuditOutcome.Success, "bytes=3000000-4048575"),
            ("NativeDownloaded", AuditOutcome.Success, null),
            ("NativeDownloaded", AuditOutcome.Denied, null));
    }

    private static void Field(JsonNode field, string typeLabel, string format, string? display)
    {
        var name = field["displayName"]!.GetValue<string>();
        field["typeLabel"]!.GetValue<string>().Should().Be(typeLabel, name);
        field["format"]!.GetValue<string>().Should().Be(format, name);
        (field["displayValue"]?.GetValue<string>()).Should().Be(display, name);
    }

    private static void AssertJsonHeaders(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().StartWith("default-src 'none'");
        response.Headers.GetValues(ProtectedContentGateway.RetrievalIdHeader).Should().ContainSingle();
    }

    /// <summary>Synthetic text of about <paramref name="bytes"/> UTF-8 bytes mixing 1- to 4-byte characters and line breaks.</summary>
    private static string LargeText(int bytes)
    {
        var words = new[] { "Quarterly", "forecast", "réunion", "überprüfen", "合同", "条款", "😀", "memo", "pricing", "\n" };
        var builder = new StringBuilder(bytes);
        var size = 0;
        for (var i = 0; size < bytes; i++)
        {
            var word = words[(i * 31 + (i / 7)) % words.Length];
            builder.Append(word).Append(' ');
            size += Encoding.UTF8.GetByteCount(word) + 1;
        }

        return builder.ToString();
    }

    private static async Task<Guid> Member(ContentDatabase db, Guid ws, WorkspaceRole role)
    {
        var user = await db.Security.CreateUserAsync();
        await db.Security.AssignAsync(ws, role, user);
        return user;
    }

    private WebApplicationFactory<Program> Factory(ContentDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Security.Core.AppConnectionString);
            builder.UseSetting("ObjectStorage:Provider", "FileSystem");
            builder.UseSetting("ObjectStorage:FileSystem:RootPath", db.StoreRoot);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
                services.AddSingleton<IAuditEventWriter>(_audit);
            });
        });

    private sealed class HidingFilter(int fieldId) : IFieldAccessFilter
    {
        public ValueTask<IReadOnlySet<int>> RestrictedFieldIdsAsync(
            Guid workspaceId, SecurityPrincipal principal, FieldCatalog catalog, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlySet<int>>(new HashSet<int> { fieldId });
    }
}
