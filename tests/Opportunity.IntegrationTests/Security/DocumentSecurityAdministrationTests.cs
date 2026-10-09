using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;

using Opportunity.Application.Fields;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Content;
using Opportunity.IntegrationTests.Migrations;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Security;

/// <summary>
/// E05-T06 through the real API host on PostgreSQL, with no index worker running (the projection never catches up):
/// restriction classes bound to security-affecting choices, ethical walls (users, IdP groups, explicit documents,
/// custodians, wall choices), self-protection, break-glass and field-level restrictions. Every denial answers exactly
/// like a missing document or field and is audited.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class DocumentSecurityAdministrationTests(MigrationPostgresFixture postgres)
{
    private const string MatterGroup = "matter-b-team";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Classes_walls_and_break_glass_are_administered_through_the_api_and_enforced_on_the_next_request()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var core = db.Security.Core;
        var ws = await core.CreateWorkspaceAsync();
        var w = await WorkspaceAsync(core, ws);
        var wallField = (await core.Fields.CreateFieldAsync(
            new NewField(ws, "Matter Wall", FieldType.SingleChoice, FieldStorage.Coding, SecurityClass: SecurityClass.EthicalWall), Ct)).Value!.FieldId;
        var matterB = (await core.Fields.AddChoiceAsync(ws, wallField, "Matter B", Ct)).Value!.ChoiceId;

        var admin = await MemberAsync(core, ws, WorkspaceRole.WorkspaceAdmin, "Avery Admin");
        var walledAdmin = await MemberAsync(core, ws, WorkspaceRole.WorkspaceAdmin, "Wade Walled-Admin");
        var privilege = await MemberAsync(core, ws, WorkspaceRole.PrivilegeReviewer);
        var reviewer = await MemberAsync(core, ws, WorkspaceRole.Reviewer);
        var grouped = await MemberAsync(core, ws, WorkspaceRole.Reviewer);
        var glass = await MemberAsync(core, ws, WorkspaceRole.BreakGlass, "Gale Glass");
        await core.ExecuteAsync("DELETE FROM opportunity.workspace_role_assignment WHERE workspace_id = @ws AND user_id = @u AND role <> 'BreakGlass'",
            ("ws", ws), ("u", glass));

        var confidential = (await db.DocumentAsync(ws)).DocumentId; // coded Confidential below
        var explicitDoc = (await db.DocumentAsync(ws)).DocumentId;  // walled explicitly
        var custodianDoc = (await db.DocumentAsync(ws)).DocumentId; // walled through its custodian
        var taggedDoc = (await db.DocumentAsync(ws)).DocumentId;    // walled once coded "Matter B"
        var open = (await db.DocumentAsync(ws)).DocumentId;
        await core.ExecuteAsync(
            """UPDATE opportunity.document SET metadata = jsonb_build_object('f29', jsonb_build_array('Jane Doe ', 'Sam Roe')) WHERE workspace_id = @ws AND document_id = @d""",
            ("ws", ws), ("d", custodianDoc));

        await using var factory = Factory(core.AppConnectionString, b =>
        {
            b.UseSetting("ObjectStorage:Provider", "FileSystem");
            b.UseSetting("ObjectStorage:FileSystem:RootPath", db.StoreRoot);
            b.UseSetting("Authentication:Mfa:AmrValues:0", "mfa");
        });
        using var client = factory.CreateClient();
        var api = new Api(client);
        var security = $"/api/v1/workspaces/{ws}/security";
        var docs = $"/api/v1/workspaces/{ws}/documents";

        // A class bound to the Confidential choice; Reviewers lose it (the admin's own grant is untouched).
        var classes = await api.JsonAsync(HttpMethod.Get, security + "/restriction-classes", admin, HttpStatusCode.OK);
        var builtIn = classes.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("classKey").GetString() == "Confidential");
        (await api.SendAsync(HttpMethod.Get, security + "/restriction-classes", reviewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var put = await api.JsonAsync(HttpMethod.Put, security + "/restriction-classes/Confidential", admin, HttpStatusCode.OK, new JsonObject
        {
            ["displayName"] = "Confidential",
            ["roles"] = new JsonArray("WorkspaceAdmin", "PrivilegeReviewer", "ProductionManager"),
            ["rules"] = new JsonArray(new JsonObject { ["fieldId"] = w.Confidentiality, ["choiceId"] = w.Confidential }),
        }, ifMatch: $"\"{builtIn.GetProperty("version").GetInt64()}\"");
        put.GetProperty("rules").GetArrayLength().Should().Be(1);
        (await api.SendAsync(HttpMethod.Put, security + "/restriction-classes/Confidential", admin, new JsonObject
        {
            ["displayName"] = "Confidential",
            ["roles"] = new JsonArray("PrivilegeReviewer", "ProductionManager"),
            ["rules"] = new JsonArray(),
        }, ifMatch: "*")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "removing the grant of a role the caller holds is self-protected");

        // Coding the choice applies the class in the coding transaction: the next request of a Reviewer is a 404.
        (await PutAsync(client, CodingUrl(ws, confidential), privilege, Set(w.Confidentiality, w.Confidential), "\"1\"")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.document_restriction WHERE workspace_id = @ws AND document_id = @d AND class_key = 'Confidential'",
            ("ws", ws), ("d", confidential))).Should().Be(1);

        // A wall over an explicit document, a custodian and a wall choice; members: a user, an IdP group, an admin.
        (await api.SendAsync(HttpMethod.Post, security + "/walls", admin, Wall("Matter B", [admin], [], [explicitDoc])))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "a wall naming its author is self-protected");
        var unknownDoc = await api.ProblemAsync(HttpMethod.Post, security + "/walls", admin, Wall("Matter B", [reviewer], [], [Guid.CreateVersion7()]));
        unknownDoc.Status.Should().Be(HttpStatusCode.BadRequest);
        var wall = await api.JsonAsync(HttpMethod.Post, security + "/walls", admin, HttpStatusCode.Created,
            Wall("Matter B", [reviewer, walledAdmin], [MatterGroup], [explicitDoc], ["jane doe"], [(wallField, matterB)]));
        var wallId = wall.GetProperty("wallId").GetGuid();
        (await PutAsync(client, CodingUrl(ws, taggedDoc), privilege, Set(wallField, matterB), "\"1\"")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await core.ColumnAsync($"SELECT document_id::text FROM opportunity.document_wall WHERE workspace_id = '{ws}' AND wall_id = '{wallId}' ORDER BY 1"))
            .Should().BeEquivalentTo([explicitDoc.ToString(), custodianDoc.ToString(), taggedDoc.ToString()]);
        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND lane = 1 AND status <> 4 AND document_id = ANY(@d)",
            ("ws", ws), ("d", new[] { explicitDoc, custodianDoc, taggedDoc, confidential }))).Should().BeGreaterThanOrEqualTo(4,
            "every visibility change is queued on the security lane; no index worker runs");

        // Every content and coding path answers like a missing document for every walled principal; the open document still works.
        var missing = Guid.CreateVersion7();
        foreach (var (user, groups, document) in new[]
        {
            (reviewer, (string?)null, confidential), (reviewer, null, explicitDoc), (reviewer, null, custodianDoc), (reviewer, null, taggedDoc),
            (grouped, MatterGroup, explicitDoc), (walledAdmin, null, custodianDoc),
        })
        {
            foreach (var path in new[] { "", "/text", "/text/chunks/0", "/pages", "/pages/1/image", "/coding", "/relationships" })
            {
                var denied = await api.ProblemAsync(HttpMethod.Get, $"{docs}/{document}{path}", user, groups);
                var absent = await api.ProblemAsync(HttpMethod.Get, $"{docs}/{missing}{path}", user, groups);
                denied.Status.Should().Be(HttpStatusCode.NotFound, path);
                denied.Should().Be(absent, "{0}{1}: hidden is indistinguishable from missing", document, path);
            }
        }

        (await api.SendAsync(HttpMethod.Get, $"{docs}/{open}/text", reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Get, $"{docs}/{explicitDoc}/text", grouped)).StatusCode.Should().Be(HttpStatusCode.OK,
            "without the group claim the user is not a member of the wall");
        (await api.SendAsync(HttpMethod.Get, $"{docs}/{confidential}/text", admin)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Get, security + "/restriction-classes", walledAdmin)).StatusCode.Should().Be(HttpStatusCode.OK,
            "a walled admin keeps workspace administration (Q-59)");

        // The walled admin sees the wall without the documents hidden from them and may not change it.
        var seen = await api.JsonAsync(HttpMethod.Get, $"{security}/walls/{wallId}", walledAdmin, HttpStatusCode.OK);
        seen.GetProperty("scope").GetProperty("documentIds").GetArrayLength().Should().Be(0);
        (await api.SendAsync(HttpMethod.Delete, $"{security}/walls/{wallId}", walledAdmin, ifMatch: "*")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Break-glass: no membership before activation; activation needs MFA; then reads pass walls and classes, nothing else.
        (await api.SendAsync(HttpMethod.Get, $"{docs}/{open}/text", glass)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.SendAsync(HttpMethod.Post, security + "/break-glass/activations", reviewer, new JsonObject { ["reason"] = "x" }, amr: "mfa"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "only a BreakGlass assignment may activate");
        (await api.SendAsync(HttpMethod.Post, security + "/break-glass/activations", glass, new JsonObject { ["reason"] = "Incident 7" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "an MFA step-up is required");
        var activation = await api.JsonAsync(HttpMethod.Post, security + "/break-glass/activations", glass, HttpStatusCode.Created,
            new JsonObject { ["reason"] = "Incident 7: court order review", ["durationMinutes"] = 30 }, amr: "mfa");
        activation.GetProperty("active").GetBoolean().Should().BeTrue();
        (await api.SendAsync(HttpMethod.Get, $"{docs}/{custodianDoc}/text", glass)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Get, $"{docs}/{confidential}/text", glass)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Get, $"{docs}/{custodianDoc}/native", glass)).IsSuccessStatusCode.Should().BeFalse("no download (Q-45)");
        (await PutAsync(client, CodingUrl(ws, open), glass, Set(w.Responsive, true), "*")).IsSuccessStatusCode.Should().BeFalse("no coding (Q-45)");
        var report = await api.JsonAsync(HttpMethod.Get, security + "/break-glass/activations", admin, HttpStatusCode.OK);
        report.GetProperty("items").EnumerateArray().Should().ContainSingle(a => a.GetProperty("userId").GetGuid() == glass);
        (await api.SendAsync(HttpMethod.Post, $"{security}/break-glass/activations/{activation.GetProperty("activationId").GetGuid()}/end", admin))
            .StatusCode.Should().Be(HttpStatusCode.OK, "an administrator revokes it");
        (await api.SendAsync(HttpMethod.Get, $"{docs}/{custodianDoc}/text", glass)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Deleting the wall restores access at once and re-projects its documents.
        var etag = (await api.SendAsync(HttpMethod.Get, $"{security}/walls/{wallId}", admin)).Headers.ETag!.ToString();
        (await api.SendAsync(HttpMethod.Delete, $"{security}/walls/{wallId}", admin, ifMatch: etag)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.SendAsync(HttpMethod.Get, $"{docs}/{explicitDoc}/text", reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Audit: the administration, the break-glass activation (own access path) and the denials.
        (await core.ColumnAsync($"SELECT action FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Security' ORDER BY occurred_at, event_id"))
            .Should().ContainInOrder("RestrictionChanged", "WallCreated", "BreakGlassActivated", "BreakGlassEnded", "WallDeleted");
        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND access_path = 'BreakGlass' AND category = 'Document' AND outcome = 'Success'",
            ("ws", ws))).Should().BeGreaterThanOrEqualTo(2, "reads during the activation are audited on the break-glass path");
        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND outcome = 'Denied' AND reason_code IN ('EthicalWall', 'RestrictionClass')",
            ("ws", ws))).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Field_restrictions_hide_a_field_like_a_missing_one_and_make_it_read_only_for_other_roles()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var core = db.Security.Core;
        var ws = await core.CreateWorkspaceAsync();
        var w = await WorkspaceAsync(core, ws);
        var admin = await MemberAsync(core, ws, WorkspaceRole.WorkspaceAdmin);
        var privilege = await MemberAsync(core, ws, WorkspaceRole.PrivilegeReviewer);
        var reviewer = await MemberAsync(core, ws, WorkspaceRole.Reviewer);
        var doc = (await db.DocumentAsync(ws)).DocumentId;
        await using var factory = Factory(core.AppConnectionString, b =>
        {
            b.UseSetting("ObjectStorage:Provider", "FileSystem");
            b.UseSetting("ObjectStorage:FileSystem:RootPath", db.StoreRoot);
        });
        using var client = factory.CreateClient();
        var api = new Api(client);
        var url = $"/api/v1/workspaces/{ws}/security/field-restrictions/{w.Notes}";

        (await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/security/field-restrictions/1", admin,
            new JsonObject { ["visibleTo"] = new JsonArray("WorkspaceAdmin") })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "system fields stay visible");
        var created = await api.JsonAsync(HttpMethod.Put, url, admin, HttpStatusCode.Created, new JsonObject
        {
            ["visibleTo"] = new JsonArray("WorkspaceAdmin", "PrivilegeReviewer"),
            ["editableBy"] = new JsonArray("WorkspaceAdmin"),
        });
        created.GetProperty("fieldName").GetString().Should().Be("Reviewer Notes");

        // Hidden from Reviewers: field catalogue (search field list), coding view, and a write answers like an unknown field.
        var fields = await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/fields", reviewer, HttpStatusCode.OK);
        fields.GetRawText().Should().NotContain("Reviewer Notes");
        var coding = await JsonAsync(await GetAsync(client, CodingUrl(ws, doc), reviewer));
        coding.GetProperty("fields").EnumerateArray().Should().NotContain(f => f.GetProperty("fieldId").GetInt32() == w.Notes);
        var hiddenWrite = await PutAsync(client, CodingUrl(ws, doc), reviewer, Set(w.Notes, "x"), "\"1\"");
        var unknownWrite = await PutAsync(client, CodingUrl(ws, doc), reviewer, Set(987_654, "x"), "\"1\"");
        hiddenWrite.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Errors(hiddenWrite)).Replace(w.Notes.ToString(System.Globalization.CultureInfo.InvariantCulture), "ID", StringComparison.Ordinal)
            .Should().Be((await Errors(unknownWrite)).Replace("987654", "ID", StringComparison.Ordinal));

        // Read-only for Privilege Reviewers, editable by Workspace Admins.
        var asPrivilege = await JsonAsync(await GetAsync(client, CodingUrl(ws, doc), privilege));
        FieldOf(asPrivilege, w.Notes).GetProperty("editable").GetBoolean().Should().BeFalse();
        var readOnly = await PutAsync(client, CodingUrl(ws, doc), privilege, Set(w.Notes, "x"), "\"1\"");
        readOnly.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Errors(readOnly)).Should().Contain("read-only");
        (await PutAsync(client, CodingUrl(ws, doc), admin, Set(w.Notes, "Admin note"), "\"1\"")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Lifting the restriction is versioned and audited.
        (await api.SendAsync(HttpMethod.Delete, url, admin)).StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        (await api.SendAsync(HttpMethod.Delete, url, admin, ifMatch: "\"1\"")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/fields", reviewer, HttpStatusCode.OK)).GetRawText().Should().Contain("Reviewer Notes");
        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Security' AND action = 'PermissionChanged'", ("ws", ws)))
            .Should().Be(2);
    }

    [Fact]
    public async Task A_save_that_would_hide_the_document_from_its_author_needs_confirmation_and_writes_nothing_without_it()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var core = db.Security.Core;
        var ws = await core.CreateWorkspaceAsync();
        var w = await WorkspaceAsync(core, ws);
        var wallField = (await core.Fields.CreateFieldAsync(
            new NewField(ws, "Matter Wall", FieldType.SingleChoice, FieldStorage.Coding, SecurityClass: SecurityClass.EthicalWall), Ct)).Value!.FieldId;
        var matterB = (await core.Fields.AddChoiceAsync(ws, wallField, "Matter B", Ct)).Value!.ChoiceId;
        var admin = await MemberAsync(core, ws, WorkspaceRole.WorkspaceAdmin);
        var privilege = await MemberAsync(core, ws, WorkspaceRole.PrivilegeReviewer);
        var doc = (await db.DocumentAsync(ws)).DocumentId;
        var walled = (await db.DocumentAsync(ws)).DocumentId;
        var kept = (await db.DocumentAsync(ws)).DocumentId;
        await using var factory = Factory(core.AppConnectionString, b =>
        {
            b.UseSetting("ObjectStorage:Provider", "FileSystem");
            b.UseSetting("ObjectStorage:FileSystem:RootPath", db.StoreRoot);
        });
        using var client = factory.CreateClient();
        var api = new Api(client);
        var security = $"/api/v1/workspaces/{ws}/security";

        // Confidential is visible to admins only; a wall over "Matter B" names the privilege reviewer.
        (await api.SendAsync(HttpMethod.Put, security + "/restriction-classes/Confidential", admin, new JsonObject
        {
            ["displayName"] = "Confidential",
            ["roles"] = new JsonArray("WorkspaceAdmin"),
            ["rules"] = new JsonArray(new JsonObject { ["fieldId"] = w.Confidentiality, ["choiceId"] = w.Confidential }),
        }, ifMatch: "*")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.SendAsync(HttpMethod.Post, security + "/walls", admin, Wall("Matter B", [privilege], [], [], [], [(wallField, matterB)])))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Unconfirmed: 409 confirmation-required, and nothing of the save is left (no value, no event, no class, no audit).
        foreach (var (document, change) in new[] { (doc, Set(w.Confidentiality, w.Confidential)), (walled, Set(wallField, matterB)) })
        {
            using var refused = await PutAsync(client, CodingUrl(ws, document), privilege, change, "\"1\"", "save-1");
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var problem = await JsonAsync(refused);
            problem.GetProperty("code").GetString().Should().Be("confirmation-required");
            problem.GetProperty("reason").GetString().Should().Be("removes-own-access");
            using var after = await GetAsync(client, CodingUrl(ws, document), privilege);
            after.StatusCode.Should().Be(HttpStatusCode.OK);
            ETag(after).Should().Be("\"1\"", "the refused save wrote nothing");
        }

        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.coding_event WHERE workspace_id = @ws", ("ws", ws))).Should().Be(0);
        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.document_restriction WHERE workspace_id = @ws", ("ws", ws))).Should().Be(0);
        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.document_wall WHERE workspace_id = @ws", ("ws", ws))).Should().Be(0);

        // Confirmed: the save stands, the answer lists no fields, and the document now answers like a missing one.
        var confirmed = Set(w.Confidentiality, w.Confidential);
        confirmed["confirmAccessLoss"] = true;
        using (var saved = await PutAsync(client, CodingUrl(ws, doc), privilege, confirmed, "\"1\"", "save-1"))
        {
            saved.StatusCode.Should().Be(HttpStatusCode.OK, "the refused attempt left its idempotency key unused");
            var body = await JsonAsync(saved);
            body.GetProperty("accessRetained").GetBoolean().Should().BeFalse();
            body.GetProperty("fields").GetArrayLength().Should().Be(0);
            body.GetProperty("lastEditor").ValueKind.Should().Be(JsonValueKind.Null);
        }

        (await api.ProblemAsync(HttpMethod.Get, CodingUrl(ws, doc), privilege))
            .Should().Be(await api.ProblemAsync(HttpMethod.Get, CodingUrl(ws, Guid.CreateVersion7()), privilege));
        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Coding' AND action = 'Changed'", ("ws", ws)))
            .Should().Be(1, "only the confirmed save is audited");

        // A security-affecting change the author keeps access through needs no confirmation.
        using var keptSave = await PutAsync(client, CodingUrl(ws, kept), admin, Set(w.Confidentiality, w.Confidential), "\"1\"");
        keptSave.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(keptSave)).GetProperty("accessRetained").GetBoolean().Should().BeTrue();
    }

    private static async Task<string> Errors(HttpResponseMessage response)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        json.Remove("traceId");
        return json.ToJsonString();
    }

    private static JsonObject Wall(
        string name, Guid[] users, string[] groups, Guid[] documents, string[]? custodians = null, (int FieldId, int ChoiceId)[]? choices = null) => new()
        {
            ["name"] = name,
            ["members"] = new JsonObject
            {
                ["userIds"] = new JsonArray([.. users.Select(u => (JsonNode)u.ToString())]),
                ["groups"] = new JsonArray([.. groups.Select(g => (JsonNode)g)]),
            },
            ["scope"] = new JsonObject
            {
                ["documentIds"] = new JsonArray([.. documents.Select(d => (JsonNode)d.ToString())]),
                ["custodians"] = new JsonArray([.. (custodians ?? []).Select(c => (JsonNode)c)]),
                ["choices"] = new JsonArray([.. (choices ?? []).Select(c => (JsonNode)new JsonObject { ["fieldId"] = c.FieldId, ["choiceId"] = c.ChoiceId })]),
            },
        };

    private sealed record Problem(HttpStatusCode Status, string Body);

    private sealed class Api(HttpClient client)
    {
        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method, string url, Guid user, JsonNode? body = null, string? ifMatch = null, string? amr = null, string? groups = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
            request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
            if (groups is not null)
            {
                request.Headers.Add(TestAuthentication.GroupsHeader, groups);
            }

            if (amr is not null)
            {
                request.Headers.Add(TestAuthentication.AmrHeader, amr);
            }

            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            return await client.SendAsync(request, Ct);
        }

        public async Task<JsonElement> JsonAsync(
            HttpMethod method, string url, Guid user, HttpStatusCode expected, JsonNode? body = null, string? ifMatch = null, string? amr = null)
        {
            using var response = await SendAsync(method, url, user, body, ifMatch, amr);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(expected, text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public async Task<Problem> ProblemAsync(HttpMethod method, string url, Guid user, JsonNode? body = null) =>
            await ProblemAsync(method, url, user, null, body);

        public async Task<Problem> ProblemAsync(HttpMethod method, string url, Guid user, string? groups, JsonNode? body = null)
        {
            using var response = await SendAsync(method, url, user, body, groups: groups);
            var text = await response.Content.ReadAsStringAsync(Ct);
            if (JsonNode.Parse(text) is JsonObject json)
            {
                json.Remove("traceId");
                text = json.ToJsonString();
            }

            return new Problem(response.StatusCode, text);
        }
    }
}
