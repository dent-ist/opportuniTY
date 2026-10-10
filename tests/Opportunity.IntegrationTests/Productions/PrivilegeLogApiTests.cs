using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Redactions;
using Opportunity.Contracts.Api;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Redactions;
using Opportunity.Core.Security;
using Opportunity.Data.Redactions;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Production.Productions;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// E13-T03 through the real API host: a privilege log of a finalized production lists every document withheld or
/// redacted for privilege exactly once and nothing produced in full (AC 1); versions carry SHA-256, the production and
/// snapshot IDs, and render byte-identically again (AC 2); recorded exclusion rules are in the metadata with their counts
/// (AC 3). Documents and fields the caller may not see leave no trace (Q-52, field-level restrictions); files go through
/// the protected-content gateway with an audit event.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class PrivilegeLogApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_production_log_lists_each_withheld_or_redacted_document_once_versions_reproducibly_and_records_exclusions()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var db = h.Db;
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeNewWorkspaceAsync(ws, Ct);
        var admin = await MemberAsync(db, ws, WorkspaceRole.WorkspaceAdmin, "Pat Privilege");
        var walled = await MemberAsync(db, ws, WorkspaceRole.WorkspaceAdmin, "Wally Walled");
        var manager = await MemberAsync(db, ws, WorkspaceRole.ProductionManager, "Morgan Manager");

        // A: 0 produced with a privilege redaction, 1 withheld (not produced), 2 produced in full. B: 3 coded Redact, produced in
        // full. C: 4 produced with a privacy redaction only. D: 5 produced as a placeholder and coded Withhold. E/F: 6 and 7 in the
        // review set, withheld, 6 dated after the complaint. G: 8 produced in full, 9 withheld and walled from Wally.
        var docs = await h.FamiliesAsync(ws, "PLOG",
            [(1, "pdf"), (1, "pdf"), (1, "pdf")], [(1, "pdf")], [(1, "pdf")], [(1, "msg")], [(1, "pdf")], [(1, "pdf")], [(1, "pdf"), (1, "pdf")]);
        await db.ExecuteAsync(
            """
            UPDATE opportunity.document SET file_name = control_number || '.' || file_extension, document_date_source = 1,
                   document_date = CASE WHEN document_id = @late THEN timestamptz '2026-03-02 15:00:00+00' ELSE timestamptz '2025-06-01 09:30:00+00' END
             WHERE workspace_id = @ws
            """,
            ("ws", ws), ("late", docs[6]));
        var catalog = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        int Status(string key) => PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, key)!.Value;
        var attorneyClient = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.AttorneyClient)!.Value;
        var workProduct = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.WorkProduct)!.Value;
        async Task CodeAsync(Guid document, params CodingFieldOperation[] operations) => (await db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "plog-" + Guid.CreateVersion7().ToString("N"),
            Actor = new CodingActor(admin, CodingActorType.Human),
            Documents = [new CodingTarget(document)],
            Operations = operations,
        }, Ct)).Outcome.Should().Be(CodingWriteOutcome.Applied);
        CodingFieldOperation StatusOf(string key) => CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(Status(key)));

        await CodeAsync(docs[0], StatusOf(PrivilegeFields.Keys.Redact), CodingFieldOperation.AddChoices(PrivilegeFields.Basis, attorneyClient));
        await CodeAsync(docs[1], StatusOf(PrivilegeFields.Keys.Withhold), CodingFieldOperation.AddChoices(PrivilegeFields.Basis, attorneyClient),
            CodingFieldOperation.Set(PrivilegeFields.Description, JsonValue.Create("Email from counsel providing legal advice on the supply agreement")));
        await CodeAsync(docs[3], StatusOf(PrivilegeFields.Keys.Redact), CodingFieldOperation.AddChoices(PrivilegeFields.Basis, workProduct));
        await CodeAsync(docs[6], StatusOf(PrivilegeFields.Keys.Withhold), CodingFieldOperation.AddChoices(PrivilegeFields.Basis, attorneyClient));
        await CodeAsync(docs[7], StatusOf(PrivilegeFields.Keys.Withhold), CodingFieldOperation.AddChoices(PrivilegeFields.Basis, workProduct));
        await CodeAsync(docs[9], StatusOf(PrivilegeFields.Keys.Withhold), CodingFieldOperation.AddChoices(PrivilegeFields.Basis, attorneyClient));

        var redactions = new RedactionStore(db.AppDataSource);
        var set = (await redactions.ListSetsAsync(ws, Ct)).Single();
        async Task RedactAsync(Guid document, string reason)
        {
            var pageSet = await db.ScalarAsync<Guid>("SELECT active_page_set_id FROM opportunity.document WHERE workspace_id = @ws AND document_id = @doc",
                ("ws", ws), ("doc", document));
            await db.ExecuteAsync(
                "INSERT INTO opportunity.page (workspace_id, page_set_id, ordinal, document_id, width_pt, height_pt, color_mode) VALUES (@ws, @ps, 1, @doc, 612, 792, 1)",
                ("ws", ws), ("ps", pageSet), ("doc", document));
            (await redactions.SaveAsync(ws, document, set.RedactionSetId, 0, null, admin,
                [new PlannedRevision(Guid.CreateVersion7(), RedactionOperation.Add, pageSet, 1, new NormalizedRect(100_000, 100_000, 300_000, 100_000),
                    RedactionType.Black, reason, null)], [], Ct)).Should().Be(RedactionWriteStatus.Ok);
        }

        await RedactAsync(docs[0], "AttorneyClient");
        await RedactAsync(docs[4], "PII");

        // The production: A's parent and 2, B, C, D (msg files as placeholders), G's parent; finalized over the family conflicts.
        var snapshot = await h.SnapshotAsync(ws, admin, [docs[0], docs[2], docs[3], docs[4], docs[5], docs[8]]);
        var draft = await h.CreateOkAsync(ws, admin, snapshot.SnapshotId, ProductionHarness.Spec("PLG") with
        {
            FileTypeRules = [new ProductionFileTypeRule(["msg"], ProductionOutputResource.Placeholder)],
        });
        var allocated = await h.AllocateAsync(ws, admin, draft.ProductionId);
        var finalized = await h.Service().FinalizeAsync(ProductionHarness.Principal(admin), ws, draft.ProductionId, allocated.RowVersion,
            ProductionHarness.Unimaged(
                new ProductionQcOverride(ProductionQcCheck.PrivilegeConflicts, "Withheld attachments are logged on the privilege log."),
                new ProductionQcOverride(ProductionQcCheck.RedactWithoutRedactions, "The work-product redactions are drawn on the produced copy.")), Ct);
        finalized.Status.Should().Be(ProductionOutcomeStatus.Ok, "{0} {1}", finalized.Reason, JsonSerializer.Serialize(finalized.Errors));
        await CodeAsync(docs[5], StatusOf(PrivilegeFields.Keys.Withhold), CodingFieldOperation.AddChoices(PrivilegeFields.Basis, attorneyClient));
        var reviewSet = await h.SnapshotAsync(ws, admin, [docs[6], docs[7], docs[1]]);
        var another = await h.CreateOkAsync(ws, admin, snapshot.SnapshotId, ProductionHarness.Spec("PLGD"));

        await using var factory = Factory(db.AppConnectionString);
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}";
        await db.ExecuteAsync(
            """
            INSERT INTO opportunity.ethical_wall (workspace_id, wall_id, name) VALUES (@ws, @wall, 'Wall');
            INSERT INTO opportunity.ethical_wall_member (workspace_id, wall_id, member_id, user_id) VALUES (@ws, @wall, @member, @user);
            INSERT INTO opportunity.ethical_wall_scope (workspace_id, wall_id, scope_id, kind, document_id) VALUES (@ws, @wall, @scope, 1, @doc);
            INSERT INTO opportunity.document_wall (workspace_id, document_id, wall_id) VALUES (@ws, @doc, @wall);
            """,
            ("ws", ws), ("wall", Guid.CreateVersion7()), ("member", Guid.CreateVersion7()), ("user", walled), ("scope", Guid.CreateVersion7()),
            ("doc", docs[9]));

        // A template with explicit columns and a recorded exclusion rule.
        var template = await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-log-templates", admin, new JsonObject
        {
            ["name"] = "Supply case log",
            ["definition"] = new JsonObject
            {
                ["columns"] = new JsonArray(
                    Column("privId"), Column("begBates"), Column("controlNumber"), Column("field", SystemFields.DocumentDate, "Date"),
                    Column("subjectOrFileName"), Column("basis"), Column("field", PrivilegeFields.Description, "Description"), Column("familyRange"),
                    Column("treatment")),
                ["dateFormat"] = "MM/dd/yyyy",
                ["exclusionRules"] = new JsonArray(new JsonObject
                {
                    ["label"] = "Communications with outside litigation counsel after the complaint was filed",
                    ["onOrAfter"] = "2026-01-15",
                }),
            },
        }), HttpStatusCode.Created);
        var templateId = template.GetProperty("templateId").GetGuid();
        template.GetProperty("definition").GetProperty("privIdPrefix").GetString().Should().Be("PRIV", "absent members take their defaults");

        // Generate (AC 1): the redacted parent and the placeholder carry their Bates; the others get Priv IDs in log order.
        var generate = new JsonObject
        {
            ["productionId"] = draft.ProductionId.ToString(),
            ["snapshotId"] = reviewSet.SnapshotId.ToString(),
            ["templateId"] = templateId.ToString(),
        };
        var first = await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", admin, generate), HttpStatusCode.Created);
        first.GetProperty("unchanged").GetBoolean().Should().BeFalse();
        var log = first.GetProperty("log");
        var logId = log.GetProperty("logId").GetGuid();
        log.GetProperty("version").GetInt32().Should().Be(1);
        log.GetProperty("productionId").GetGuid().Should().Be(draft.ProductionId, "AC 2: linked to the production");
        log.GetProperty("snapshotId").GetGuid().Should().Be(snapshot.SnapshotId, "AC 2: linked to the production's frozen set");
        log.GetProperty("reviewSetSnapshotId").GetGuid().Should().Be(reviewSet.SnapshotId);
        log.GetProperty("contentSha256").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        var metadata = log.GetProperty("metadata");
        metadata.GetProperty("entries").GetInt32().Should().Be(5);
        metadata.GetProperty("withheld").GetInt32().Should().Be(4);
        metadata.GetProperty("redacted").GetInt32().Should().Be(1);
        metadata.GetProperty("privacyRedactionsIncluded").GetBoolean().Should().BeFalse();
        metadata.GetProperty("excludedByRules").GetInt32().Should().Be(1);
        var rule = metadata.GetProperty("exclusionRules").EnumerateArray().Single();
        rule.GetProperty("label").GetString().Should().StartWith("Communications with outside litigation counsel");
        rule.GetProperty("dateField").GetString().Should().Be("Document Date");
        rule.GetProperty("onOrAfter").GetString().Should().Be("2026-01-15");
        rule.GetProperty("excludedDocuments").GetInt32().Should().Be(1, "AC 3: the rule and what it excluded are recorded");

        var entries = await EntriesAsync(client, root, logId, admin);
        entries.Select(e => e.DocumentId).Should().Equal([docs[0], docs[1], docs[5], docs[9], docs[7]],
            "each withheld or redacted document exactly once, in production order; never 2, 3, 4 or 8 (produced in full or privacy only)");
        entries.Select(e => e.Treatment).Should().Equal("redacted", "withheld", "withheld", "withheld", "withheld");
        entries[0].Cells.Should().Equal("PLG0000001", "PLG0000001", "PLOG0001", "06/01/2025", "PLOG0001.pdf", "Attorney-Client", "",
            "PLG0000001 - PLG0000002", "Redacted");
        entries[1].Cells.Should().Equal("PRIV0001", "", "PLOG0002", "06/01/2025", "PLOG0002.pdf", "Attorney-Client",
            "Email from counsel providing legal advice on the supply agreement", "PLG0000001 - PLG0000002", "Withheld");
        entries[2].Cells[0].Should().Be("PLG0000005", "a placeholder's Bates is its log identifier");
        entries[3].Cells[0].Should().Be("PRIV0002");
        entries[3].Cells[7].Should().Be("PLG0000006 - PRIV0002");
        entries[4].Cells[0].Should().Be("PRIV0003");

        // AC 2: the files are verified against the recorded SHA-256 and are byte-identical on every download; audited.
        var csv = await DownloadAsync(client, root, logId, "csv", admin);
        var xlsx = await DownloadAsync(client, root, logId, "xlsx", admin);
        Convert.ToHexStringLower(SHA256.HashData(csv)).Should().Be(FileSha(log, "csv"));
        Convert.ToHexStringLower(SHA256.HashData(xlsx)).Should().Be(FileSha(log, "xlsx"));
        (await DownloadAsync(client, root, logId, "csv", admin)).Should().Equal(csv);
        (await DownloadAsync(client, root, logId, "xlsx", admin)).Should().Equal(xlsx);
        var text = Encoding.UTF8.GetString(csv.AsSpan(3));
        text.Should().StartWith("Priv ID,Beg Bates,Control Number,Date,Subject/File Name,Privilege Basis,Description,Family Range,Withheld/Redacted\r\n");
        text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(6);
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Privilege' AND action = 'LogDownloaded' AND resource_id = @id AND reason_code IS NULL",
            ("ws", ws), ("id", logId.ToString()))).Should().BeGreaterThanOrEqualTo(4);
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Privilege' AND action = 'LogGenerated' AND resource_id = @id",
            ("ws", ws), ("id", logId.ToString()))).Should().Be(1);

        // Same production and template, nothing changed: the same version, not a new one.
        var again = await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", admin, generate), HttpStatusCode.OK);
        again.GetProperty("unchanged").GetBoolean().Should().BeTrue();
        again.GetProperty("log").GetProperty("logId").GetGuid().Should().Be(logId);

        // A changed description is a new version with new hashes; version 1 still renders exactly as before.
        await CodeAsync(docs[1], CodingFieldOperation.Set(PrivilegeFields.Description, JsonValue.Create("Legal advice on supply agreement terms")));
        var second = await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", admin, generate), HttpStatusCode.Created);
        second.GetProperty("log").GetProperty("version").GetInt32().Should().Be(2);
        second.GetProperty("log").GetProperty("contentSha256").GetString().Should().NotBe(log.GetProperty("contentSha256").GetString());
        (await DownloadAsync(client, root, logId, "csv", admin)).Should().Equal(csv);
        (await DownloadAsync(client, root, logId, "xlsx", admin)).Should().Equal(xlsx);
        var listed = await JsonAsync(await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs?productionId={draft.ProductionId}", admin), HttpStatusCode.OK);
        listed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("version").GetInt32()).Should().Equal(2, 1);

        // Q-52: a version listing a document Wally may not see does not exist for him; his own log leaves no trace of it.
        (await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{logId}", walled)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{logId}/content", walled)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonAsync(await SendAsync(client, HttpMethod.Get, root + "/privilege-logs", walled), HttpStatusCode.OK))
            .GetProperty("items").GetArrayLength().Should().Be(0);
        var wallys = (await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", walled, generate), HttpStatusCode.Created))
            .GetProperty("log");
        wallys.GetProperty("metadata").GetProperty("withheld").GetInt32().Should().Be(3);
        var wallyEntries = await EntriesAsync(client, root, wallys.GetProperty("logId").GetGuid(), walled);
        wallyEntries.Select(e => e.DocumentId).Should().Equal(docs[0], docs[1], docs[5], docs[7]);
        wallyEntries.Select(e => e.Cells[0]).Should().Equal("PLG0000001", "PRIV0001", "PLG0000005", "PRIV0002");
        wallyEntries.Should().OnlyContain(e => !e.Cells.Contains("PLG0000006 - PRIV0002"));

        // A frozen-set log (preset, no exclusion rules): the set's withheld documents, Priv IDs only.
        var setLog = await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", admin, new JsonObject
        {
            ["snapshotId"] = reviewSet.SnapshotId.ToString(),
            ["preset"] = "metadataOnly",
        }), HttpStatusCode.Created);
        setLog.GetProperty("log").GetProperty("source").GetString().Should().Be("snapshot");
        setLog.GetProperty("log").GetProperty("metadata").GetProperty("columns").EnumerateArray().Select(c => c.GetString())
            .Should().NotContain("Description");
        var setEntries = await EntriesAsync(client, root, setLog.GetProperty("log").GetProperty("logId").GetGuid(), admin);
        setEntries.Select(e => e.DocumentId).Should().BeEquivalentTo([docs[6], docs[7], docs[1]], "the set's withheld documents, in frozen-set order");
        setEntries.Select(e => e.Cells[0]).Should().Equal("PRIV0001", "PRIV0002", "PRIV0003");

        // Privacy-only redactions are listed only when the template says so.
        var privacy = await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-log-templates", admin, new JsonObject
        {
            ["name"] = "With privacy redactions",
            ["definition"] = new JsonObject { ["includePrivacyRedactions"] = true, ["preset"] = "metadataOnly" },
        }), HttpStatusCode.Created);
        var privacyLog = await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", admin, new JsonObject
        {
            ["productionId"] = draft.ProductionId.ToString(),
            ["templateId"] = privacy.GetProperty("templateId").GetString(),
        }), HttpStatusCode.Created);
        var privacyEntries = await EntriesAsync(client, root, privacyLog.GetProperty("log").GetProperty("logId").GetGuid(), admin);
        privacyEntries.Single(e => e.DocumentId == docs[4]).Treatment.Should().Be("redactedPrivacy");
        privacyEntries.Select(e => e.DocumentId).Should().NotContain(docs[2]).And.NotContain(docs[3]).And.NotContain(docs[8]);

        // Field-level restrictions: a log reading a field Morgan may not see is not shown to him, and he cannot use it.
        var note = (await db.Fields.CreateFieldAsync(new NewField(ws, "Log Note", FieldType.Text, FieldStorage.Coding), Ct)).Value!.FieldId;
        await db.ExecuteAsync(
            "INSERT INTO opportunity.field_security (workspace_id, field_id, visible_roles, editable_roles) VALUES (@ws, @field, '{WorkspaceAdmin}', '{WorkspaceAdmin}')",
            ("ws", ws), ("field", note));
        var noted = await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-log-templates", admin, new JsonObject
        {
            ["name"] = "With notes",
            ["definition"] = new JsonObject { ["columns"] = new JsonArray(Column("privId"), Column("field", note)) },
        }), HttpStatusCode.Created);
        var notedRequest = new JsonObject { ["productionId"] = draft.ProductionId.ToString(), ["templateId"] = noted.GetProperty("templateId").GetString() };
        var notedLog = (await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", admin, notedRequest), HttpStatusCode.Created))
            .GetProperty("log").GetProperty("logId").GetGuid();
        (await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{notedLog}", manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{logId}", manager)).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var refused = await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", manager, notedRequest))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("template.columns[1].fieldId").And.NotContain("Log Note");
        }

        // Sources in the wrong state, or none.
        (await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", admin, new JsonObject { ["productionId"] = another.ProductionId.ToString() }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "a draft production has nothing frozen to log");
        (await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", admin, new JsonObject { ["productionId"] = Guid.CreateVersion7().ToString() }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", admin, new JsonObject())).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Templates are replaced with If-Match; names are unique.
        var templateUrl = root + $"/privilege-log-templates/{templateId}";
        var rename = new JsonObject { ["name"] = "Supply case log (served)", ["definition"] = new JsonObject() };
        (await SendAsync(client, HttpMethod.Put, templateUrl, admin, rename)).StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        (await SendAsync(client, HttpMethod.Put, templateUrl, admin, rename, "\"7\"")).StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        (await JsonAsync(await SendAsync(client, HttpMethod.Put, templateUrl, admin, rename, "\"1\""), HttpStatusCode.OK))
            .GetProperty("version").GetInt64().Should().Be(2);
        (await SendAsync(client, HttpMethod.Post, root + "/privilege-log-templates", admin, new JsonObject { ["name"] = "supply case log (SERVED)" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonAsync(await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{logId}", admin), HttpStatusCode.OK))
            .GetProperty("templateName").GetString().Should().Be("Supply case log", "a version keeps the template it used");

        // A stored row that no longer matches the recorded SHA-256 is never sent.
        await db.ExecuteAsync(
            "UPDATE opportunity.privilege_log_entry SET cells[1] = 'PRIV9999' WHERE workspace_id = @ws AND log_id = @id AND ordinal = 2",
            ("ws", ws), ("id", logId));
        (await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{logId}/content", admin)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND action = 'LogDownloaded' AND resource_id = @id AND reason_code = 'sha256-mismatch'",
            ("ws", ws), ("id", logId.ToString()))).Should().Be(1);
    }

    private static JsonObject Column(string kind, int? fieldId = null, string? header = null)
    {
        var column = new JsonObject { ["kind"] = kind };
        if (fieldId is { } id)
        {
            column["fieldId"] = id;
        }

        if (header is not null)
        {
            column["header"] = header;
        }

        return column;
    }

    private static string FileSha(JsonElement log, string format) =>
        log.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("format").GetString() == format).GetProperty("sha256").GetString()!;

    private sealed record Entry(Guid DocumentId, string Treatment, string[] Cells);

    private static async Task<List<Entry>> EntriesAsync(HttpClient client, string root, Guid logId, Guid user)
    {
        var page = await JsonAsync(await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{logId}/entries?limit=500", user), HttpStatusCode.OK);
        return
        [
            .. page.GetProperty("items").EnumerateArray().Select(e => new Entry(e.GetProperty("documentId").GetGuid(), e.GetProperty("treatment").GetString()!,
                [.. e.GetProperty("cells").EnumerateArray().Select(c => c.GetString()!)])),
        ];
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, string root, Guid logId, string format, Guid user)
    {
        using var response = await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{logId}/content?format={format}", user);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(expected, text);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, Guid user, JsonNode? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
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
}
