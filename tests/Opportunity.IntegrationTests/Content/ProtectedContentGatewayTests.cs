using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
using Opportunity.Core.Security;
using Opportunity.Core.Storage;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Content;

/// <summary>
/// E05-T04 through the real API host, the PostgreSQL PDP and a filesystem object store: every byte of a document is
/// served only after an authoritative decision and a durable <c>Document.*</c> audit event, with the D12.2 headers.
/// Negative cases: non-members, unknown / malformed / cross-workspace IDs, restriction classes, walls (also over
/// Workspace Admin), break-glass downloads, quarantine, security changes with indexing stopped, audit failure and
/// tampered stored bytes.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ProtectedContentGatewayTests(MigrationPostgresFixture postgres)
{
    private const string SandboxCsp = "sandbox; default-src 'none'";

    private readonly InMemoryAuditEventWriter _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reviewer_gets_text_page_images_and_thumbnails_with_protected_content_headers_and_each_retrieval_is_audited()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var doc = await db.DocumentAsync(ws);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var basePath = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}";

        var cases = new (string Path, string ContentType, byte[] Body, string Rendition, Guid ObjectId)[]
        {
            ($"{basePath}/text", "text/plain; charset=utf-8", doc.Text, "Text", doc.TextObjectId),
            ($"{basePath}/pages/1/image", "image/png", doc.PageImage, "PageImage", doc.PageImageObjectId),
            ($"{basePath}/pages/1/thumbnail", "image/webp", doc.Thumbnail, "Thumbnail", Guid.Empty),
        };
        foreach (var (path, contentType, body, rendition, objectId) in cases)
        {
            _audit.Clear();
            using var response = await GetAsync(client, path, reviewer);

            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            (await response.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(body);
            response.Content.Headers.ContentType!.ToString().Should().Be(contentType);
            response.Content.Headers.ContentLength.Should().Be(body.Length);
            AssertProtectedContentHeaders(response);
            response.Content.Headers.ContentDisposition.Should().BeNull("viewer renditions are displayed, never offered as files");

            var e = _audit.Events.Should().ContainSingle(path).Subject;
            (e.Category, e.Action, e.Outcome, e.WorkspaceId, e.ResourceId, e.ActorId).Should()
                .Be(("Document", "Retrieved", AuditOutcome.Success, (Guid?)ws, doc.DocumentId.ToString(), reviewer.ToString()));
            e.Details["rendition"].Should().Be(rendition);
            e.Details["purpose"].Should().Be("Display");
            e.Details["deliveryMode"].Should().Be("Stream");
            if (objectId != Guid.Empty)
            {
                e.Details["objectId"].Should().Be(objectId.ToString());
            }

            response.Headers.GetValues(ProtectedContentGateway.RetrievalIdHeader).Should().Equal(e.EventId.ToString());
        }
    }

    [Fact]
    public async Task Prefetch_is_audited_as_prefetch_and_only_the_view_beacon_records_a_view()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var doc = await db.DocumentAsync(ws);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var basePath = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}";

        using var prefetch = await GetAsync(client, $"{basePath}/pages/1/image?purpose=prefetch", reviewer);
        prefetch.StatusCode.Should().Be(HttpStatusCode.OK);
        var retrieval = prefetch.Headers.GetValues(ProtectedContentGateway.RetrievalIdHeader).Single();
        _audit.Events.Should().ContainSingle().Which.Details["purpose"].Should().Be("Prefetch");
        _audit.Events.Should().NotContain(e => e.Action == "Viewed", "prefetch alone never counts as viewed");

        using var view = await SendAsync(client, HttpMethod.Post, $"{basePath}/views", reviewer, $$"""{"retrievalId":"{{retrieval}}"}""");
        view.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var viewed = _audit.Events.Should().ContainSingle(e => e.Action == "Viewed").Subject;
        viewed.Details["retrievedEventId"].Should().Be(retrieval);
        viewed.ResourceId.Should().Be(doc.DocumentId.ToString());

        (await GetAsync(client, $"{basePath}/text?purpose=bogus", reviewer)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Native_download_and_print_need_permissions_distinct_from_view()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var manager = await Member(db, ws, WorkspaceRole.ProductionManager);
        var doc = await db.DocumentAsync(ws);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var basePath = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}";

        (await GetAsync(client, $"{basePath}/pages/1/image", reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);
        _audit.Clear();
        await (await GetAsync(client, $"{basePath}/native", reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await GetAsync(client, $"{basePath}/pages/1/image?purpose=print", reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        _audit.Events.Select(e => (e.Category, e.Action, e.Outcome, e.ReasonCode, e.ResourceId, e.Details["rendition"], e.Details["permission"])).Should().Equal(
            ("Document", "NativeDownloaded", AuditOutcome.Denied, AuthorizationReasons.PermissionNotGranted, doc.DocumentId.ToString(), "Native", "Document.DownloadNative"),
            ("Document", "Printed", AuditOutcome.Denied, AuthorizationReasons.PermissionNotGranted, doc.DocumentId.ToString(), "PageImage", "Document.Print"));

        _audit.Clear();
        using var native = await GetAsync(client, $"{basePath}/native", manager);
        native.StatusCode.Should().Be(HttpStatusCode.OK);
        (await native.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(doc.Native);
        native.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        native.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        native.Content.Headers.ContentDisposition.FileNameStar.Should().Be(doc.ControlNumber + ".docx");
        AssertProtectedContentHeaders(native);

        using var print = await GetAsync(client, $"{basePath}/pages/1/image?purpose=print", manager);
        print.StatusCode.Should().Be(HttpStatusCode.OK);
        _audit.Events.Select(e => (e.Action, e.Outcome)).Should().Equal(("NativeDownloaded", AuditOutcome.Success), ("Printed", AuditOutcome.Success));
    }

    [Fact]
    public async Task Native_downloads_support_a_single_byte_range()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var manager = await Member(db, ws, WorkspaceRole.ProductionManager);
        var doc = await db.DocumentAsync(ws);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var path = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}/native";

        using var partial = await GetAsync(client, path, manager, range: "bytes=2-5");
        partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(doc.Native[2..6]);
        partial.Content.Headers.ContentRange!.ToString().Should().Be($"bytes 2-5/{doc.Native.Length}");
        partial.Headers.AcceptRanges.Should().Contain("bytes");

        using var suffix = await GetAsync(client, path, manager, range: "bytes=-4");
        (await suffix.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(doc.Native[^4..]);

        using var beyond = await GetAsync(client, path, manager, range: $"bytes={doc.Native.Length}-");
        await beyond.ShouldBeProblemAsync(HttpStatusCode.RequestedRangeNotSatisfiable, "range-not-satisfiable");
        beyond.Content.Headers.ContentRange!.ToString().Should().Be($"bytes */{doc.Native.Length}");

        _audit.Events.Select(e => (e.Outcome, e.Details.GetValueOrDefault("range"))).Should().Equal(
            (AuditOutcome.Success, "bytes=2-5"), (AuditOutcome.Success, "bytes=-4"), (AuditOutcome.Failure, $"bytes={doc.Native.Length}-"));
    }

    [Fact]
    public async Task Hidden_unknown_malformed_and_foreign_documents_all_get_the_same_404_and_true_reasons_go_to_audit()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var other = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var outsider = await db.Security.CreateUserAsync();
        await db.Security.AssignAsync(other, WorkspaceRole.WorkspaceAdmin, reviewer);
        var aeo = await db.DocumentAsync(ws, [RestrictionClasses.AttorneysEyesOnly]);
        var walled = await db.DocumentAsync(ws);
        await db.Security.WallAsync(ws, [reviewer], [], [walled.DocumentId]);
        var foreign = await db.DocumentAsync(other);
        var visible = await db.DocumentAsync(ws);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        var targets = new (string Id, string? Reason)[]
        {
            (aeo.DocumentId.ToString(), AuthorizationReasons.RestrictionClass),
            (walled.DocumentId.ToString(), AuthorizationReasons.EthicalWall),
            (foreign.DocumentId.ToString(), AuthorizationReasons.DocumentNotFound),
            (Guid.CreateVersion7().ToString(), AuthorizationReasons.DocumentNotFound),
            ("not-a-guid", null),
            ("00000000-0000-0000-0000-000000000000", null),
        };
        foreach (var rendition in new[] { "text", "pages/1/image", "pages/1/thumbnail" })
        {
            var bodies = new HashSet<string>();
            foreach (var (id, reason) in targets)
            {
                _audit.Clear();
                using var response = await GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{id}/{rendition}", reviewer);
                bodies.Add(Comparable(await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found")));
                if (reason is null)
                {
                    _audit.Events.Should().BeEmpty();
                }
                else
                {
                    var e = _audit.Events.Should().ContainSingle($"{rendition} {id}").Subject;
                    (e.Category, e.Outcome, e.ReasonCode, e.ResourceId).Should().Be(("Document", AuditOutcome.Denied, reason, id));
                }
            }

            bodies.Should().ContainSingle("{0}: a reviewer cannot tell a hidden document from a missing one", rendition);
        }

        // Outside the workspace the route itself is 404 (PEP-1), even for a visible document.
        await (await GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{visible.DocumentId}/text", outsider))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        // A tampered page number of a visible document is just unavailable.
        await (await GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{visible.DocumentId}/pages/2/image", reviewer))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "content-unavailable");
        await (await GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{visible.DocumentId}/pages/-1/image", reviewer))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "content-unavailable");
    }

    [Fact]
    public async Task An_ethical_wall_overrides_workspace_admin_for_view_and_download()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var admin = await Member(db, ws, WorkspaceRole.WorkspaceAdmin);
        var doc = await db.DocumentAsync(ws);
        await db.Security.WallAsync(ws, [], ["cn=deal-team"], [doc.DocumentId]);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var basePath = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}";

        (await GetAsync(client, $"{basePath}/text", admin)).StatusCode.Should().Be(HttpStatusCode.OK, "the wall names a group the admin is not in");
        await (await GetAsync(client, $"{basePath}/text", admin, groups: "cn=deal-team")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await GetAsync(client, $"{basePath}/native", admin, groups: "cn=deal-team")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        _audit.Events.Where(e => e.Outcome == AuditOutcome.Denied).Select(e => e.ReasonCode).Should().Equal(AuthorizationReasons.EthicalWall, AuthorizationReasons.EthicalWall);
    }

    [Fact]
    public async Task Break_glass_is_read_only_and_audited_on_the_break_glass_path()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var responder = await db.Security.CreateUserAsync();
        await db.Security.AssignAsync(ws, WorkspaceRole.BreakGlass, responder);
        var walledAdmin = await Member(db, ws, WorkspaceRole.WorkspaceAdmin);
        await db.Security.AssignAsync(ws, WorkspaceRole.BreakGlass, walledAdmin);
        var doc = await db.DocumentAsync(ws, [RestrictionClasses.AttorneysEyesOnly]);
        await db.Security.WallAsync(ws, [responder, walledAdmin], [], [doc.DocumentId]);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var basePath = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}";

        await (await GetAsync(client, $"{basePath}/text", responder)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        await db.Security.ActivateBreakGlassAsync(ws, responder, TimeSpan.FromMinutes(60));
        await db.Security.ActivateBreakGlassAsync(ws, walledAdmin, TimeSpan.FromMinutes(60));
        _audit.Clear();
        using var view = await GetAsync(client, $"{basePath}/text", responder);
        view.StatusCode.Should().Be(HttpStatusCode.OK);
        (await view.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(doc.Text);
        await (await GetAsync(client, $"{basePath}/native", responder)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await GetAsync(client, $"{basePath}/pages/1/image?purpose=print", responder)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await GetAsync(client, $"{basePath}/native", walledAdmin)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        _audit.Events.Select(e => (e.ActorId, e.Action, e.Outcome, e.AccessPath)).Should().Equal(
            (responder.ToString(), "Retrieved", AuditOutcome.Success, AuditAccessPath.BreakGlass),
            (responder.ToString(), "NativeDownloaded", AuditOutcome.Denied, AuditAccessPath.BreakGlass),
            (responder.ToString(), "Printed", AuditOutcome.Denied, AuditAccessPath.BreakGlass),
            (walledAdmin.ToString(), "NativeDownloaded", AuditOutcome.Denied, AuditAccessPath.BreakGlass));

        // Ended activation: hidden again on the next request.
        await db.Security.Core.ExecuteAsync(
            "UPDATE opportunity.break_glass_activation SET ended_at = now(), ended_reason = 'Ended' WHERE user_id = @u", ("u", responder));
        (await GetAsync(client, $"{basePath}/text", responder)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_committed_security_affecting_change_denies_the_next_view_and_download_while_indexing_is_stopped()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var manager = await Member(db, ws, WorkspaceRole.ProductionManager);
        var doc = await db.DocumentAsync(ws);
        var fields = db.Security.Core.Fields;
        await fields.InitializeWorkspaceAsync(ws, Ct);
        var privilege = (await fields.CreateFieldAsync(new NewField(ws, "Privilege Status", FieldType.SingleChoice, FieldStorage.Coding,
            SecurityClass: SecurityClass.PrivilegeStatus), Ct)).Value!.FieldId;
        var privileged = (await fields.AddChoiceAsync(ws, privilege, "Attorneys' Eyes Only", Ct)).Value!.ChoiceId;
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var basePath = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}";

        (await GetAsync(client, $"{basePath}/pages/1/image", reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, $"{basePath}/native", manager)).StatusCode.Should().Be(HttpStatusCode.OK);

        // The security-affecting coding commits; no index worker runs in this test, so its search work stays pending.
        // The class it drives is committed with it (derivation from field values is E05-T06); the gateway reads only PG.
        var write = await db.Security.Core.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "privilege-" + doc.DocumentId,
            Actor = new CodingActor(manager, CodingActorType.Human),
            Documents = [new CodingTarget(doc.DocumentId)],
            Operations = [CodingFieldOperation.Set(privilege, JsonValue.Create(privileged))],
        }, Ct);
        write.Outcome.Should().Be(CodingWriteOutcome.Applied, string.Join(", ", write.Errors.Select(e => e.Code)));
        write.TouchesSecurityAffectingField.Should().BeTrue();
        await db.Security.Core.ExecuteAsync(
            "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, 'AttorneysEyesOnly')",
            ("ws", ws), ("doc", doc.DocumentId));
        (await db.Security.Core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND document_id = @doc AND status <> 4",
            ("ws", ws), ("doc", doc.DocumentId))).Should().BeGreaterThan(0, "the search projection has not caught up");

        await (await GetAsync(client, $"{basePath}/pages/1/image", reviewer)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await GetAsync(client, $"{basePath}/text?purpose=prefetch", reviewer)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        (await GetAsync(client, $"{basePath}/native", manager)).StatusCode.Should().Be(HttpStatusCode.OK, "AEO is granted to Production Manager");

        // A wall now covers the document for the manager: the very next download is refused too.
        await db.Security.WallAsync(ws, [manager], [], [doc.DocumentId]);
        await (await GetAsync(client, $"{basePath}/native", manager)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task Quarantined_natives_need_view_quarantined()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var manager = await Member(db, ws, WorkspaceRole.ProductionManager);
        var admin = await Member(db, ws, WorkspaceRole.WorkspaceAdmin);
        var doc = await db.DocumentAsync(ws, nativeState: StoredObjectState.Quarantined);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var path = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}/native";

        await (await GetAsync(client, path, manager)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var download = await GetAsync(client, path, admin);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        _audit.Events.Select(e => (e.Outcome, e.Details["permission"], e.Details["quarantined"])).Should().Equal(
            (AuditOutcome.Denied, "Document.ViewQuarantined", "true"), (AuditOutcome.Success, "Document.DownloadNative", "true"));
    }

    [Fact]
    public async Task Retrievals_are_stored_in_the_postgresql_audit_trail_before_content_is_returned()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var doc = await db.DocumentAsync(ws);
        await using var factory = Factory(db, realAuditStore: true);
        using var client = factory.CreateClient();
        var basePath = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}";

        (await GetAsync(client, $"{basePath}/text", reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, $"{basePath}/native", reviewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var stored = await db.Security.Core.ColumnAsync(
            """
            SELECT concat_ws('|', category, action, resource_type, resource_id, outcome, coalesce(reason_code, '-'), details->>'rendition', details->>'purpose')
            FROM audit.audit_event WHERE workspace_id IS NOT NULL ORDER BY recorded_at
            """);
        stored.Should().Equal(
            $"Document|Retrieved|Document|{doc.DocumentId}|Success|-|Text|Display",
            $"Document|NativeDownloaded|Document|{doc.DocumentId}|Denied|PermissionNotGranted|Native|Download");
    }

    [Fact]
    public async Task No_audit_no_content()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var doc = await db.DocumentAsync(ws);
        await using var factory = Factory(db).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IAuditEventWriter>();
            s.AddSingleton<IAuditEventWriter, FailingAuditWriter>();
        }));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}/text", reviewer);

        await response.ShouldBeProblemAsync(HttpStatusCode.InternalServerError, "internal-error");
    }

    [Fact]
    public async Task Stored_bytes_that_no_longer_match_the_registry_are_never_delivered_complete()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var manager = await Member(db, ws, WorkspaceRole.ProductionManager);
        var doc = await db.DocumentAsync(ws);
        var file = Directory.EnumerateFiles(db.StoreRoot, "*", SearchOption.AllDirectories)
            .Single(f => File.ReadAllBytes(f).AsSpan().SequenceEqual(doc.Native));
        var tampered = (byte[])doc.Native.Clone();
        tampered[0] ^= 0xFF;
        await File.WriteAllBytesAsync(file, tampered, Ct);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        var download = async () =>
        {
            using var response = await GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}/native", manager);
            return await response.Content.ReadAsByteArrayAsync(Ct);
        };

        await download.Should().ThrowAsync<Exception>("the response is aborted instead of completing with altered evidence");
    }

    [Fact]
    public async Task Without_object_storage_configured_content_routes_fail_closed()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var doc = await db.DocumentAsync(ws);
        await using var factory = Factory(db, configureStorage: false);
        using var client = factory.CreateClient();

        await (await GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}/text", reviewer))
            .ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, "service-unavailable");
        _audit.Events.Should().BeEmpty();
    }

    internal static void AssertProtectedContentHeaders(HttpResponseMessage response)
    {
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("Content-Security-Policy").Should().Equal(SandboxCsp);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("Cross-Origin-Resource-Policy").Should().Equal("same-origin");
        response.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
        response.Headers.Concat(response.Content.Headers).SelectMany(h => h.Value)
            .Should().NotContain(v => v.Contains("ws/", StringComparison.Ordinal), "object keys never leave the server");
    }

    private static string Comparable(JsonElement problem)
    {
        var node = JsonNode.Parse(problem.GetRawText())!.AsObject();
        node.Remove("traceId");
        return node.ToJsonString();
    }

    private static async Task<Guid> Member(ContentDatabase db, Guid ws, WorkspaceRole role)
    {
        var user = await db.Security.CreateUserAsync();
        await db.Security.AssignAsync(ws, role, user);
        return user;
    }

    private WebApplicationFactory<Program> Factory(ContentDatabase db, bool realAuditStore = false, bool configureStorage = true) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Security.Core.AppConnectionString);
            if (configureStorage)
            {
                builder.UseSetting("ObjectStorage:Provider", "FileSystem");
                builder.UseSetting("ObjectStorage:FileSystem:RootPath", db.StoreRoot);
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
                if (!realAuditStore)
                {
                    services.AddSingleton<IAuditEventWriter>(_audit);
                }
            });
        });

    internal static Task<HttpResponseMessage> GetAsync(HttpClient client, string url, Guid user, string? groups = null, string? range = null) =>
        SendAsync(client, HttpMethod.Get, url, user, groups: groups, range: range);

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid user, string? body = null, string? groups = null, string? range = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (groups is not null)
        {
            request.Headers.Add(TestAuthentication.GroupsHeader, groups);
        }

        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        return await client.SendAsync(request, Ct);
    }

    private sealed class FailingAuditWriter : IAuditEventWriter
    {
        public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("audit store unavailable");
    }
}
