using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Content;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Redactions;

/// <summary>
/// E11-T04 through the real API host and the PostgreSQL PDP: Redaction Sets and reasons (seeded per workspace,
/// Workspace.ManageFields, If-Match), versioned saves with one audit event per rectangle in the transaction of the
/// save, removed redactions kept in the history and in "as of" reads, 412 with the current state on a concurrent edit
/// (never a silent overwrite), Redaction.Apply / Redaction.Remove per ADR-012 §3.8, "Redaction requires rendered
/// images", and an insert-only revision table for the application role.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class RedactionApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Redactions_are_versioned_audited_and_removed_ones_stay_in_the_history()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var admin = await Member(db, ws, WorkspaceRole.WorkspaceAdmin);
        var document = await db.DocumentAsync(ws);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        var sets = await JsonAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/redaction-sets", admin, HttpStatusCode.OK);
        var set = sets["items"]!.AsArray().Should().ContainSingle().Subject!;
        set["name"]!.GetValue<string>().Should().Be("Default");
        var setId = set["redactionSetId"]!.GetValue<Guid>();
        var reasons = await JsonAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/redaction-reasons", admin, HttpStatusCode.OK);
        reasons["items"]!.AsArray().Select(r => (r!["code"]!.GetValue<string>(), r["category"]!.GetValue<string>())).Should().Equal(
            ("AttorneyClient", "privilege"), ("WorkProduct", "privilege"), ("CommonInterest", "privilege"), ("PII", "privacy"), ("PHI", "privacy"),
            ("PersonalDataGdpr", "privacy"), ("TradeSecret", "other"), ("NonResponsive", "other"), ("Other", "other"));
        reasons["items"]![5]!["name"]!.GetValue<string>().Should().Be("Personal Data – GDPR");

        var path = $"/api/v1/workspaces/{ws}/documents/{document.DocumentId}/redaction-sets/{setId}";
        using (var empty = await SendAsync(client, HttpMethod.Get, path, admin))
        {
            empty.StatusCode.Should().Be(HttpStatusCode.OK);
            empty.Headers.ETag!.Tag.Should().Be("\"0\"");
            var body = JsonNode.Parse(await empty.Content.ReadAsStringAsync(Ct))!;
            body["redactable"]!.GetValue<bool>().Should().BeTrue();
            body["unavailableReason"].Should().BeNull();
            body["redactions"]!.AsArray().Should().BeEmpty();
        }

        // Version 1: two boxes (one full-page). Version 2: one moved and relabelled. Version 3: the other removed.
        var v1 = await SaveAsync(client, path, admin, "\"0\"", HttpStatusCode.OK,
            Add(1, 100_000, 200_000, 300_000, 40_000, "black", "PII", "account number"),
            Add(1, 0, 0, 1_000_000, 1_000_000, "labelled", "AttorneyClient"));
        v1["version"]!.GetValue<long>().Should().Be(1);
        var boxes = v1["redactions"]!.AsArray();
        boxes.Should().HaveCount(2);
        var first = boxes.Single(b => b!["reasonCode"]!.GetValue<string>() == "PII")!;
        var full = boxes.Single(b => b!["reasonCode"]!.GetValue<string>() == "AttorneyClient")!;
        first["reasonName"]!.GetValue<string>().Should().Be("PII");
        first["reasonCategory"]!.GetValue<string>().Should().Be("privacy");
        first["createdBy"]!["userId"]!.GetValue<Guid>().Should().Be(admin);
        first["note"]!.GetValue<string>().Should().Be("account number");
        first["onActivePageSet"]!.GetValue<bool>().Should().BeTrue();
        var firstId = first["redactionId"]!.GetValue<Guid>();
        var fullId = full["redactionId"]!.GetValue<Guid>();

        var v2 = await SaveAsync(client, path, admin, "\"1\"", HttpStatusCode.OK, new JsonObject
        {
            ["operation"] = "modify",
            ["redactionId"] = firstId.ToString(),
            ["rect"] = Rect(110_000, 210_000, 300_000, 40_000),
            ["reasonCode"] = "PHI",
        });
        var moved = v2["redactions"]!.AsArray().Single(b => b!["redactionId"]!.GetValue<Guid>() == firstId)!;
        moved["rect"]!["x"]!.GetValue<int>().Should().Be(110_000);
        moved["reasonCode"]!.GetValue<string>().Should().Be("PHI");
        moved["changedAtVersion"]!.GetValue<long>().Should().Be(2);
        moved["createdAt"]!.GetValue<DateTimeOffset>().Should().Be(first["createdAt"]!.GetValue<DateTimeOffset>(), "the author and time of the Add stay");

        var v3 = await SaveAsync(client, path, admin, "\"2\"", HttpStatusCode.OK,
            new JsonObject { ["operation"] = "remove", ["redactionId"] = fullId.ToString() });
        v3["redactions"]!.AsArray().Should().ContainSingle();
        v3["lastChange"]!["version"]!.GetValue<long>().Should().Be(3);

        // As of version 1 the original two boxes, unmoved.
        var asOf = await JsonAsync(client, HttpMethod.Get, path + "?version=1", admin, HttpStatusCode.OK);
        asOf["version"]!.GetValue<long>().Should().Be(1);
        asOf["currentVersion"]!.GetValue<long>().Should().Be(3);
        asOf["redactions"]!.AsArray().Select(r => r!["rect"]!["x"]!.GetValue<int>()).Should().BeEquivalentTo([0, 100_000]);
        (await SendAsync(client, HttpMethod.Get, path + "?version=9", admin)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The history keeps the removed redaction.
        var history = await JsonAsync(client, HttpMethod.Get, path + "/history", admin, HttpStatusCode.OK);
        history["items"]!.AsArray().Select(i => (i!["version"]!.GetValue<long>(), i["operation"]!.GetValue<string>())).Should().Equal(
            (3, "remove"), (2, "modify"), (1, "add"), (1, "add"));
        history["items"]![0]!["redactionId"]!.GetValue<Guid>().Should().Be(fullId);

        // One audit event per rectangle, in the transaction of the save, without the note.
        (await db.Security.Core.ColumnAsync(
            $"SELECT action || ':' || resource_id FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Redaction' AND resource_type = 'Redaction' ORDER BY occurred_at, action"))
            .Should().BeEquivalentTo([$"Added:{firstId}", $"Added:{fullId}", $"Modified:{firstId}", $"Removed:{fullId}"]);
        (await db.Security.Core.ColumnAsync(
            $"SELECT details::text FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Redaction' AND action = 'Modified'"))
            .Should().ContainSingle().Which.Should().Contain("\"redactionVersion\": \"2\"").And.Contain("110000,210000,300000,40000")
            .And.Contain(document.DocumentId.ToString()).And.NotContain("account number");
    }

    [Fact]
    public async Task A_concurrent_edit_answers_412_with_the_current_redactions_and_never_overwrites()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var one = await Member(db, ws, WorkspaceRole.QcReviewer);
        var two = await Member(db, ws, WorkspaceRole.QcReviewer);
        var document = await db.DocumentAsync(ws);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var setId = await DefaultSetAsync(client, ws, one);
        var path = $"/api/v1/workspaces/{ws}/documents/{document.DocumentId}/redaction-sets/{setId}";

        (await SendAsync(client, HttpMethod.Post, path + "/revisions", one, Changes(Add(1, 0, 0, 500_000, 50_000, "black", "PII"))))
            .StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);

        // Both read version 0; the first save wins, the second gets 412 with the first one's redaction.
        await SaveAsync(client, path, one, "\"0\"", HttpStatusCode.OK, Add(1, 0, 0, 500_000, 50_000, "black", "PII"));
        using (var stale = await SendAsync(client, HttpMethod.Post, path + "/revisions", two,
            Changes(Add(1, 0, 600_000, 500_000, 50_000, "black", "PHI")), "\"0\""))
        {
            var text = await stale.Content.ReadAsStringAsync(Ct);
            stale.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed, text);
            stale.Headers.ETag!.Tag.Should().Be("\"1\"");
            var problem = JsonNode.Parse(text)!;
            problem["code"]!.GetValue<string>().Should().Be("version-conflict");
            problem["currentVersion"]!.GetValue<long>().Should().Be(1);
            problem["lastChange"]!["actor"]!["userId"]!.GetValue<Guid>().Should().Be(one);
            problem["current"]!["redactions"]!.AsArray().Should().ContainSingle().Which!["reasonCode"]!.GetValue<string>().Should().Be("PII");
        }

        // Saves racing on the same version: exactly one is written.
        var racing = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            using var response = await SendAsync(client, HttpMethod.Post, path + "/revisions", i % 2 == 0 ? one : two,
                Changes(Add(1, 10_000 * i, 100_000, 50_000, 50_000, "black", "Other")), "\"1\"");
            return response.StatusCode;
        }));
        racing.Count(s => s == HttpStatusCode.OK).Should().Be(1);
        racing.Count(s => s == HttpStatusCode.PreconditionFailed).Should().Be(5);
        var now = await JsonAsync(client, HttpMethod.Get, path, one, HttpStatusCode.OK);
        now["version"]!.GetValue<long>().Should().Be(2);
        now["redactions"]!.AsArray().Should().HaveCount(2);
        (await db.Security.Core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.redaction_revision WHERE workspace_id = @ws", ("ws", ws))).Should().Be(2);
    }

    [Fact]
    public async Task Apply_adds_and_changes_own_redactions_Remove_is_needed_for_removal_and_others_and_hidden_documents_are_not_found()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var admin = await Member(db, ws, WorkspaceRole.WorkspaceAdmin);
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        var auditor = await Member(db, ws, WorkspaceRole.Auditor);
        var outsider = await db.Security.CreateUserAsync();
        var document = await db.DocumentAsync(ws);
        var walled = await db.DocumentAsync(ws);
        await db.Security.WallAsync(ws, [reviewer], [], [walled.DocumentId]);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var setId = await DefaultSetAsync(client, ws, admin);
        var path = $"/api/v1/workspaces/{ws}/documents/{document.DocumentId}/redaction-sets/{setId}";

        var v1 = await SaveAsync(client, path, admin, "\"0\"", HttpStatusCode.OK, Add(1, 0, 0, 200_000, 50_000, "black", "WorkProduct"));
        var adminBox = v1["redactions"]![0]!["redactionId"]!.GetValue<Guid>();
        var v2 = await SaveAsync(client, path, reviewer, "\"1\"", HttpStatusCode.OK, Add(1, 0, 500_000, 200_000, 50_000, "labelled", "PII"));
        var own = v2["redactions"]!.AsArray().Single(r => r!["redactionId"]!.GetValue<Guid>() != adminBox)!["redactionId"]!.GetValue<Guid>();

        // Own: moving is Redaction.Apply; another user's or removing needs Redaction.Remove (Reviewer has Apply only).
        await SaveAsync(client, path, reviewer, "\"2\"", HttpStatusCode.OK,
            new JsonObject { ["operation"] = "modify", ["redactionId"] = own.ToString(), ["rect"] = Rect(0, 520_000, 200_000, 50_000) });
        await SaveAsync(client, path, reviewer, "\"3\"", HttpStatusCode.Forbidden,
            new JsonObject { ["operation"] = "modify", ["redactionId"] = adminBox.ToString(), ["type"] = "labelled" });
        await SaveAsync(client, path, reviewer, "\"3\"", HttpStatusCode.Forbidden,
            new JsonObject { ["operation"] = "remove", ["redactionId"] = own.ToString() });
        await SaveAsync(client, path, admin, "\"3\"", HttpStatusCode.OK, new JsonObject { ["operation"] = "remove", ["redactionId"] = own.ToString() });

        // The auditor reads but cannot redact; outsiders and walled documents look missing; set admin is ManageFields.
        (await SendAsync(client, HttpMethod.Get, path, auditor)).StatusCode.Should().Be(HttpStatusCode.OK);
        await SaveAsync(client, path, auditor, "\"4\"", HttpStatusCode.Forbidden, Add(1, 0, 0, 100_000, 100_000, "black", "PII"));
        (await SendAsync(client, HttpMethod.Get, path, outsider)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var walledPath = $"/api/v1/workspaces/{ws}/documents/{walled.DocumentId}/redaction-sets/{setId}";
        using (var hidden = await SendAsync(client, HttpMethod.Get, walledPath, reviewer))
        using (var unknown = await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/documents/{Guid.CreateVersion7()}/redaction-sets/{setId}", reviewer))
        {
            hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await hidden.Content.ReadAsStringAsync(Ct)).Should().Contain("detail").And.Contain(ProblemDetail(await unknown.Content.ReadAsStringAsync(Ct)));
        }

        await SaveAsync(client, walledPath, reviewer, "\"0\"", HttpStatusCode.NotFound, Add(1, 0, 0, 100_000, 100_000, "black", "PII"));
        (await SendAsync(client, HttpMethod.Get, $"{walledPath}/history", reviewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/redaction-sets", reviewer, new JsonObject { ["name"] = "Mine" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Redaction_requires_rendered_images_and_geometry_and_reasons_are_validated()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var admin = await Member(db, ws, WorkspaceRole.WorkspaceAdmin);
        var noImages = await db.ArtifactDocumentAsync(ws, Encoding.UTF8.GetBytes("text only"));
        var document = await db.DocumentAsync(ws);
        await db.AddPageAsync(ws, document.DocumentId, 2);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var setId = await DefaultSetAsync(client, ws, admin);

        var textOnly = $"/api/v1/workspaces/{ws}/documents/{noImages}/redaction-sets/{setId}";
        var state = await JsonAsync(client, HttpMethod.Get, textOnly, admin, HttpStatusCode.OK);
        state["redactable"]!.GetValue<bool>().Should().BeFalse();
        state["unavailableReason"]!.GetValue<string>().Should().Be("Redaction requires rendered images");
        var refused = await SaveAsync(client, textOnly, admin, "\"0\"", HttpStatusCode.Conflict, Add(1, 0, 0, 100_000, 100_000, "black", "PII"));
        refused["code"]!.GetValue<string>().Should().Be("redaction-requires-images");
        refused["detail"]!.GetValue<string>().Should().Be("Redaction requires rendered images.");

        var path = $"/api/v1/workspaces/{ws}/documents/{document.DocumentId}/redaction-sets/{setId}";
        var errors = await SaveAsync(client, path, admin, "\"0\"", HttpStatusCode.BadRequest,
            Add(2, 0, 0, 100_000, 100_000, "black", "PII"),
            Add(3, 0, 0, 100_000, 100_000, "black", "PII"),
            Add(1, 900_000, 0, 200_000, 100_000, "black", "PII"),
            Add(1, 0, 0, 100, 100, "black", "PII"),
            Add(1, 0, 0, 100_000, 100_000, "black", "NoSuchReason"));
        errors["errors"]!.AsObject().Select(e => e.Key).Should().BeEquivalentTo(
            ["changes[0].pageNumber", "changes[1].pageNumber", "changes[2].rect", "changes[3].rect", "changes[4].reasonCode"]);
        errors["errors"]!["changes[0].pageNumber"]![0]!.GetValue<string>().Should().Contain("Redaction requires rendered images");

        // An inactive reason cannot be chosen for new redactions.
        var reasons = $"/api/v1/workspaces/{ws}/redaction-reasons";
        await PutAsync(client, $"{reasons}/TradeSecret", admin, "\"1\"", HttpStatusCode.OK,
            new JsonObject { ["name"] = "Trade Secret", ["category"] = "other", ["boxLabel"] = "Redacted – Confidential", ["active"] = false });
        await SaveAsync(client, path, admin, "\"0\"", HttpStatusCode.BadRequest, Add(1, 0, 0, 100_000, 100_000, "black", "TradeSecret"));
        (await db.Security.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.redaction_revision WHERE workspace_id = @ws", ("ws", ws)))
            .Should().Be(0, "a refused save writes nothing");
    }

    [Fact]
    public async Task Sets_and_reasons_are_administered_with_If_Match_and_audited_and_a_retired_set_is_read_only()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var admin = await Member(db, ws, WorkspaceRole.WorkspaceAdmin);
        var document = await db.DocumentAsync(ws);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var sets = $"/api/v1/workspaces/{ws}/redaction-sets";

        var created = await JsonAsync(client, HttpMethod.Post, sets, admin, HttpStatusCode.Created, new JsonObject { ["name"] = "Privacy pass" });
        var setId = created["redactionSetId"]!.GetValue<Guid>();
        (await SendAsync(client, HttpMethod.Post, sets, admin, new JsonObject { ["name"] = "privacy PASS" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await PutAsync(client, $"{sets}/{setId}", admin, null, HttpStatusCode.PreconditionRequired, new JsonObject { ["name"] = "Privacy" });
        await PutAsync(client, $"{sets}/{setId}", admin, "\"7\"", HttpStatusCode.PreconditionFailed, new JsonObject { ["name"] = "Privacy" });
        var renamed = await PutAsync(client, $"{sets}/{setId}", admin, "\"1\"", HttpStatusCode.OK, new JsonObject { ["name"] = "Privacy" });
        renamed["version"]!.GetValue<long>().Should().Be(2);
        var list = await JsonAsync(client, HttpMethod.Get, sets, admin, HttpStatusCode.OK);
        list["items"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()).Should().Equal("Default", "Privacy");

        var path = $"/api/v1/workspaces/{ws}/documents/{document.DocumentId}/redaction-sets/{setId}";
        await SaveAsync(client, path, admin, "\"0\"", HttpStatusCode.OK, Add(1, 0, 0, 100_000, 100_000, "black", "PII"));
        await PutAsync(client, $"{sets}/{setId}", admin, "\"2\"", HttpStatusCode.OK, new JsonObject { ["name"] = "Privacy", ["retired"] = true });
        await SaveAsync(client, path, admin, "\"1\"", HttpStatusCode.Conflict, Add(1, 0, 200_000, 100_000, 100_000, "black", "PII"));
        var retired = await JsonAsync(client, HttpMethod.Get, path, admin, HttpStatusCode.OK);
        retired["setRetired"]!.GetValue<bool>().Should().BeTrue();
        retired["redactions"]!.AsArray().Should().ContainSingle("retiring keeps the set's redactions");

        var reasons = $"/api/v1/workspaces/{ws}/redaction-reasons";
        var reason = await JsonAsync(client, HttpMethod.Post, reasons, admin, HttpStatusCode.Created, new JsonObject
        {
            ["code"] = "BankSecrecy",
            ["name"] = "Bank Secrecy",
            ["category"] = "privacy",
            ["boxLabel"] = "Redacted – Bank Secrecy",
        });
        reason["sortOrder"]!.GetValue<int>().Should().Be(100);
        (await SendAsync(client, HttpMethod.Post, reasons, admin, new JsonObject
        {
            ["code"] = "BankSecrecy",
            ["name"] = "Another",
            ["category"] = "privacy",
            ["boxLabel"] = "Redacted",
        })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SendAsync(client, HttpMethod.Post, reasons, admin, new JsonObject
        {
            ["code"] = "has space",
            ["name"] = "Bad",
            ["category"] = "privacy",
            ["boxLabel"] = "Redacted",
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await PutAsync(client, $"{reasons}/NoSuchCode", admin, "\"1\"", HttpStatusCode.NotFound,
            new JsonObject { ["name"] = "X", ["category"] = "other", ["boxLabel"] = "X" });

        (await db.Security.Core.ColumnAsync(
            $"SELECT action FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Redaction' AND resource_type <> 'Redaction' ORDER BY occurred_at"))
            .Should().Equal("RedactionSet.Created", "RedactionSet.Modified", "RedactionSet.Modified", "Reason.Created");
    }

    [Fact]
    public async Task The_application_role_cannot_update_or_delete_redaction_revisions()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var admin = await Member(db, ws, WorkspaceRole.WorkspaceAdmin);
        var document = await db.DocumentAsync(ws);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var setId = await DefaultSetAsync(client, ws, admin);
        await SaveAsync(client, $"/api/v1/workspaces/{ws}/documents/{document.DocumentId}/redaction-sets/{setId}", admin, "\"0\"", HttpStatusCode.OK,
            Add(1, 0, 0, 100_000, 100_000, "black", "PII"));

        foreach (var sql in new[]
        {
            "UPDATE opportunity.redaction_revision SET x = 0",
            "DELETE FROM opportunity.redaction_revision",
            "TRUNCATE opportunity.redaction_revision",
        })
        {
            var act = () => db.Security.Core.InWorkspaceAsync(ws, async tx =>
            {
                await using var command = tx.Command(sql);
                await command.ExecuteNonQueryAsync(Ct);
            });
            (await act.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    private static JsonObject Add(int page, int x, int y, int w, int h, string type, string reason, string? note = null)
    {
        var change = new JsonObject { ["operation"] = "add", ["pageNumber"] = page, ["rect"] = Rect(x, y, w, h), ["type"] = type, ["reasonCode"] = reason };
        if (note is not null)
        {
            change["note"] = note;
        }

        return change;
    }

    private static JsonObject Rect(int x, int y, int w, int h) => new() { ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h };

    private static JsonObject Changes(params JsonObject[] changes) => new() { ["changes"] = new JsonArray([.. changes]) };

    private static string ProblemDetail(string problem) => JsonNode.Parse(problem)!["detail"]!.GetValue<string>();

    private static async Task<Guid> DefaultSetAsync(HttpClient client, Guid ws, Guid user) =>
        (await JsonAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/redaction-sets", user, HttpStatusCode.OK))["items"]![0]!["redactionSetId"]!
            .GetValue<Guid>();

    private static async Task<JsonNode> SaveAsync(HttpClient client, string path, Guid user, string ifMatch, HttpStatusCode expected, params JsonObject[] changes)
    {
        using var response = await SendAsync(client, HttpMethod.Post, path + "/revisions", user, Changes(changes), ifMatch);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(expected, text);
        if (expected == HttpStatusCode.OK)
        {
            response.Headers.ETag!.Tag.Should().Be($"\"{JsonNode.Parse(text)!["currentVersion"]!.GetValue<long>()}\"");
        }

        return JsonNode.Parse(text)!;
    }

    private static async Task<JsonNode> PutAsync(HttpClient client, string url, Guid user, string? ifMatch, HttpStatusCode expected, JsonNode body)
    {
        using var response = await SendAsync(client, HttpMethod.Put, url, user, body, ifMatch);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(expected, text);
        return JsonNode.Parse(text)!;
    }

    private static async Task<Guid> Member(ContentDatabase db, Guid ws, WorkspaceRole role)
    {
        var user = await db.Security.CreateUserAsync();
        await db.Security.AssignAsync(ws, role, user);
        return user;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid user, JsonNode? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonNode> JsonAsync(HttpClient client, HttpMethod method, string url, Guid user, HttpStatusCode expected, JsonNode? body = null)
    {
        using var response = await SendAsync(client, method, url, user, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(expected, text);
        return JsonNode.Parse(text)!;
    }

    private static WebApplicationFactory<Program> Factory(ContentDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Security.Core.AppConnectionString);
            builder.UseSetting("ObjectStorage:Provider", "FileSystem");
            builder.UseSetting("ObjectStorage:FileSystem:RootPath", db.StoreRoot);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
            });
        });
}
