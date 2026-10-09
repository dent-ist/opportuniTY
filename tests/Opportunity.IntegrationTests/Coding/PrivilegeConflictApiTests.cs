using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;

using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// E13-T02 through the real API host: the privilege conflict report (families with a withheld member next to members
/// that are not, differing responsiveness calls in a family, duplicates with differing privilege calls; values and the
/// reviewers who set them; Q-52: nothing the caller may not see, not even as a count; the audited CSV) and "propagate
/// privilege call to duplicates" as one bulk coding job with provenance.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class PrivilegeConflictApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_report_lists_conflicts_with_values_and_reviewers_and_nothing_the_caller_may_not_see()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var core = h.Db.Core;
        var admin = await MemberAsync(core, w.Id, WorkspaceRole.WorkspaceAdmin, "Ada Admin");
        var alex = await MemberAsync(core, w.Id, WorkspaceRole.PrivilegeReviewer, "Alex Privilege");
        var sam = await MemberAsync(core, w.Id, WorkspaceRole.PrivilegeReviewer, "Sam Second");
        var walled = await MemberAsync(core, w.Id, WorkspaceRole.PrivilegeReviewer, "Wally Walled");
        var reviewer = await MemberAsync(core, w.Id, WorkspaceRole.Reviewer, "Rae Reviewer");
        var p = await PrivilegeChoicesAsync(core, w.Id);
        var responsiveness = (await core.Fields.CreateFieldAsync(new NewField(w.Id, "Responsiveness", FieldType.SingleChoice, FieldStorage.Coding), Ct)).Value!.FieldId;
        var responsive = (await core.Fields.AddChoiceAsync(w.Id, responsiveness, "Responsive", Ct)).Value!.ChoiceId;
        var notResponsive = (await core.Fields.AddChoiceAsync(w.Id, responsiveness, "Not Responsive", Ct)).Value!.ChoiceId;

        // F: 0 (+1 later); G: duplicates 2, 3, 4; H: duplicates 5 (+6 later); K: 7 and 8 both withheld; R: 9 and 10.
        var docs = await h.DocumentsAsync(w.Id, 11, "PCON");
        var g = await DuplicatesAsync(h, w.Id, docs[2], docs[3], docs[4]);
        var hGroup = await DuplicatesAsync(h, w.Id, docs[5]);
        await FamilyAsync(h, w.Id, docs[7], docs[8]);
        await FamilyAsync(h, w.Id, docs[9], docs[10]);
        await h.Db.WallAsync(w.Id, [walled], [], [docs[1], docs[6]]);
        await CodeAsync(h, w.Id, alex, docs[0], p.Withhold, p.AttorneyClient);
        await CodeAsync(h, w.Id, alex, docs[2], p.Withhold, p.AttorneyClient);
        await CodeAsync(h, w.Id, sam, docs[3], p.NotPrivileged);
        await CodeAsync(h, w.Id, alex, docs[5], p.Redact, p.WorkProduct);
        await CodeAsync(h, w.Id, alex, docs[7], p.Withhold, p.AttorneyClient);
        await CodeAsync(h, w.Id, sam, docs[8], p.Withhold, p.WorkProduct);
        await h.InteractiveAsync(w.Id, sam, docs[9], CodingFieldOperation.Set(responsiveness, JsonValue.Create(responsive)));
        await h.InteractiveAsync(w.Id, sam, docs[10], CodingFieldOperation.Set(responsiveness, JsonValue.Create(notResponsive)));

        await using var factory = Factory(core.AppConnectionString);
        using var client = factory.CreateClient();
        var url = $"/api/v1/workspaces/{w.Id}/privilege-conflicts";

        // Q-52: what the walled reviewer gets before the hidden documents join F and H with conflicting calls...
        var before = WithoutTime(await TextAsync(await GetAsync(client, url, walled)));
        await FamilyAsync(h, w.Id, docs[0], docs[1]);
        await CodeAsync(h, w.Id, sam, docs[1], p.NotPrivileged);
        await core.ExecuteAsync("UPDATE opportunity.document SET duplicate_group_id = @g WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", w.Id), ("g", hGroup), ("doc", docs[6]));
        await CodeAsync(h, w.Id, sam, docs[6], p.NotPrivileged);

        // ...is exactly what they get after: those conflicts exist only through documents they may not see.
        var walledText = await TextAsync(await GetAsync(client, url, walled));
        WithoutTime(walledText).Should().Be(before, "conflicts that need a hidden document leave no trace, not even a count (Q-52)");
        using (var report = JsonDocument.Parse(walledText))
        {
            var root = report.RootElement;
            root.GetProperty("familyConflictCount").GetInt32().Should().Be(0);
            root.GetProperty("duplicateConflictCount").GetInt32().Should().Be(1);
            root.GetProperty("groups").EnumerateArray().Single().GetProperty("groupId").GetGuid().Should().Be(g);
        }

        walledText.Should().NotContain(docs[1].ToString()).And.NotContain(docs[6].ToString()).And.NotContain(hGroup.ToString());

        // The administrator sees every conflict: values, choice names and who set them (AC 1).
        using (var report = JsonDocument.Parse(await TextAsync(await GetAsync(client, url, admin))))
        {
            var root = report.RootElement;
            root.GetProperty("truncated").GetBoolean().Should().BeFalse();
            root.GetProperty("familyConflictCount").GetInt32().Should().Be(1, "a family whose members are all withheld is consistent");
            root.GetProperty("duplicateConflictCount").GetInt32().Should().Be(2);
            var groups = root.GetProperty("groups").EnumerateArray().ToList();
            groups.Select(x => (x.GetProperty("kind").GetString(), x.GetProperty("groupId").GetGuid()))
                .Should().Equal(("family", docs[0]), ("duplicates", g), ("duplicates", hGroup));

            var family = groups[0];
            family.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).Should().Equal("withheldMember");
            var members = family.GetProperty("members").EnumerateArray().ToList();
            members.Select(m => m.GetProperty("documentId").GetGuid()).Should().Equal(docs[0], docs[1]);
            members[0].GetProperty("controlNumber").GetString().Should().Be("PCON0000001");
            var status = members[0].GetProperty("privilegeStatus");
            status.GetProperty("values").EnumerateArray().Select(v => v.GetString()).Should().Equal("Withhold");
            status.GetProperty("choiceIds")[0].GetInt32().Should().Be(p.Withhold);
            status.GetProperty("changedBy").GetProperty("userId").GetGuid().Should().Be(alex);
            status.GetProperty("changedBy").GetProperty("displayName").GetString().Should().Be("Alex Privilege");
            status.GetProperty("changedAt").ValueKind.Should().Be(JsonValueKind.String);
            members[0].GetProperty("privilegeBasis").GetProperty("values")[0].GetString().Should().Be("Attorney-Client");
            members[0].GetProperty("responsiveness").ValueKind.Should().Be(JsonValueKind.Null);
            members[1].GetProperty("privilegeStatus").GetProperty("values")[0].GetString().Should().Be("Not Privileged");
            members[1].GetProperty("privilegeStatus").GetProperty("changedBy").GetProperty("displayName").GetString().Should().Be("Sam Second");

            var duplicates = groups[1];
            duplicates.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).Should().Equal("privilegeCallsDiffer");
            var dupMembers = duplicates.GetProperty("members").EnumerateArray().ToList();
            dupMembers.Select(m => m.GetProperty("documentId").GetGuid()).Should().Equal(docs[2], docs[3], docs[4]);
            dupMembers[0].GetProperty("isPrimary").GetBoolean().Should().BeTrue();
            dupMembers[2].GetProperty("privilegeStatus").GetProperty("values").GetArrayLength().Should().Be(0);
            dupMembers[2].GetProperty("privilegeStatus").GetProperty("changedBy").ValueKind.Should().Be(JsonValueKind.Null);
        }

        // A responsiveness field adds families whose members' calls differ.
        using (var report = JsonDocument.Parse(await TextAsync(await GetAsync(client, $"{url}?responsivenessField={responsiveness}", admin))))
        {
            var root = report.RootElement;
            root.GetProperty("responsivenessFieldId").GetInt32().Should().Be(responsiveness);
            var split = root.GetProperty("groups").EnumerateArray().Single(x => x.GetProperty("groupId").GetGuid() == docs[9]);
            split.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).Should().Equal("responsivenessDiffers");
            split.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("responsiveness").GetProperty("values")[0].GetString())
                .Should().Equal("Responsive", "Not Responsive");
        }

        using (var invalid = await GetAsync(client, $"{url}?responsivenessField={w.Notes}", admin))
        {
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a text field is no responsiveness call");
        }

        using (var forbidden = await GetAsync(client, url, reviewer))
        {
            forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the report needs PrivilegeLog.Generate");
        }

        // The CSV holds the same rows, through the gateway, audited.
        using (var csv = await GetAsync(client, url + "/export", admin))
        {
            var text = await csv.Content.ReadAsStringAsync(Ct);
            csv.StatusCode.Should().Be(HttpStatusCode.OK, text);
            csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
            csv.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
            text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1 + 2 + 3 + 2);
            text.Should().Contain("PCON0000003").And.Contain("Alex Privilege");
        }

        using (var csv = await GetAsync(client, url + "/export", walled))
        {
            (await csv.Content.ReadAsStringAsync(Ct)).Should().NotContain("PCON0000002").And.NotContain("PCON0000007");
        }

        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Privilege' AND action = 'ConflictReportExported'",
            ("ws", w.Id))).Should().Be(2);
    }

    [Fact]
    public async Task Propagating_privilege_calls_to_duplicates_is_one_bulk_job_with_provenance_that_leaves_hidden_members_alone()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var core = h.Db.Core;
        var admin = await MemberAsync(core, w.Id, WorkspaceRole.WorkspaceAdmin, "Ada Admin");
        var alex = await MemberAsync(core, w.Id, WorkspaceRole.PrivilegeReviewer, "Alex Privilege");
        var qc = await MemberAsync(core, w.Id, WorkspaceRole.QcReviewer, "Quinn QC");
        var p = await PrivilegeChoicesAsync(core, w.Id);

        // G: 0 (source), 1, 2, 3 (hidden from Alex); H: 4 (source), 5.
        var docs = await h.DocumentsAsync(w.Id, 6, "PPROP");
        var g = await DuplicatesAsync(h, w.Id, docs[0], docs[1], docs[2], docs[3]);
        var hGroup = await DuplicatesAsync(h, w.Id, docs[4], docs[5]);
        await h.Db.WallAsync(w.Id, [alex], [], [docs[3]]);
        await CodeAsync(h, w.Id, alex, docs[0], p.Withhold, p.AttorneyClient);
        await CodeAsync(h, w.Id, admin, docs[1], p.NotPrivileged);
        await CodeAsync(h, w.Id, admin, docs[3], p.NotPrivileged);
        await CodeAsync(h, w.Id, alex, docs[4], p.Redact, p.WorkProduct);
        await CodeAsync(h, w.Id, admin, docs[5], p.NotPrivileged);
        var origin0 = await OriginAsync(h, w.Id, docs[0]);
        var origin4 = await OriginAsync(h, w.Id, docs[4]);

        await using var factory = Factory(core.AppConnectionString, b => b.UseSetting("Snapshots:BackgroundEnabled", "false"));
        using var client = factory.CreateClient();
        var url = $"/api/v1/workspaces/{w.Id}/privilege-conflicts/propagations";
        static JsonObject Body(params (Guid Group, Guid Source)[] groups) => new()
        {
            ["groups"] = new JsonArray([.. groups.Select(x => (JsonNode)new JsonObject { ["duplicateGroupId"] = x.Group, ["sourceDocumentId"] = x.Source })]),
        };

        using (var forbidden = await PostAsync(client, url, qc, Body((g, docs[0])), "k-qc"))
        {
            forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "privilege fields need Coding.WritePrivilege");
        }

        using (var wrongGroup = await PostAsync(client, url, alex, Body((g, docs[4])), "k-wrong"))
        {
            wrongGroup.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the source must be a member of its group");
        }

        using (var hiddenSource = await PostAsync(client, url, alex, Body((g, docs[3])), "k-hidden"))
        {
            hiddenSource.StatusCode.Should().Be(HttpStatusCode.NotFound, "a source the caller may not see is no document at all");
        }

        Guid jobId;
        using (var response = await PostAsync(client, url, alex, Body((g, docs[0]), (hGroup, docs[4])), "k-1"))
        {
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted, text);
            var job = JsonDocument.Parse(text).RootElement;
            jobId = job.GetProperty("jobId").GetGuid();
            job.GetProperty("jobType").GetString().Should().Be("bulkCoding");
            response.Headers.Location!.ToString().Should().Be($"/api/v1/workspaces/{w.Id}/jobs/{jobId}");
        }

        var done = await h.RunAsync(w.Id, jobId);
        done.Status.Should().Be(Core.Jobs.JobStatus.Completed);
        done.Counters.ItemsApplied.Should().Be(3, "documents 1, 2 and 5; document 3 is hidden from Alex and never a target");

        var status = await h.ValuesAsync(w.Id, docs, PrivilegeFields.Status);
        var basis = await h.ValuesAsync(w.Id, docs, PrivilegeFields.Basis);
        foreach (var target in new[] { docs[1], docs[2] })
        {
            FieldValues.ChoiceIds(status[target]).Should().Equal(p.Withhold);
            FieldValues.ChoiceIds(basis[target]).Should().Equal(p.AttorneyClient);
        }

        FieldValues.ChoiceIds(status[docs[5]]).Should().Equal(p.Redact);
        FieldValues.ChoiceIds(basis[docs[5]]).Should().Equal(p.WorkProduct);
        FieldValues.ChoiceIds(status[docs[3]]).Should().Equal([p.NotPrivileged], "a hidden member is left alone (Q-52)");

        // Provenance: every change is the job's (BulkHuman, on behalf of Alex) and references its group's source edit.
        var events = (await h.Coding.GetEventsAsync(new CodingEventQuery(w.Id) { JobId = jobId, Kind = CodingEventKind.ValueChanged }, Ct)).Events;
        events.Select(e => e.DocumentId).Distinct().Should().BeEquivalentTo(new[] { docs[1], docs[2], docs[5] });
        events.Should().OnlyContain(e => e.ActorType == CodingActorType.BulkHuman && e.ActorId == alex);
        events.Where(e => e.FieldId == PrivilegeFields.Status && e.DocumentId != docs[5]).Should().OnlyContain(e => e.OriginEventId == origin0);
        events.Single(e => e.FieldId == PrivilegeFields.Status && e.DocumentId == docs[5]).OriginEventId.Should().Be(origin4);
        (await h.AuditActionsAsync(w.Id, jobId)).Should().ContainKeys("Coding.FamilyApplied", "Coding.BulkChunkApplied");

        // Alex's duplicates are now consistent; the administrator still sees the hidden member's different call.
        var report = $"/api/v1/workspaces/{w.Id}/privilege-conflicts";
        using (var mine = JsonDocument.Parse(await TextAsync(await GetAsync(client, report, alex))))
        {
            mine.RootElement.GetProperty("duplicateConflictCount").GetInt32().Should().Be(0);
        }

        using (var all = JsonDocument.Parse(await TextAsync(await GetAsync(client, report, admin))))
        {
            all.RootElement.GetProperty("groups").EnumerateArray().Select(x => x.GetProperty("groupId").GetGuid()).Should().Equal(g);
        }
    }

    internal sealed record PrivilegeChoices(int NotPrivileged, int Withhold, int Redact, int AttorneyClient, int WorkProduct);

    internal static async Task<PrivilegeChoices> PrivilegeChoicesAsync(Documents.CoreSchemaDatabase core, Guid ws)
    {
        var catalog = await core.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        int Id(int field, string key) => PrivilegeFields.ChoiceId(catalog, field, key)!.Value;
        return new PrivilegeChoices(
            Id(PrivilegeFields.Status, PrivilegeFields.Keys.NotPrivileged),
            Id(PrivilegeFields.Status, PrivilegeFields.Keys.Withhold),
            Id(PrivilegeFields.Status, PrivilegeFields.Keys.Redact),
            Id(PrivilegeFields.Basis, PrivilegeFields.Keys.AttorneyClient),
            Id(PrivilegeFields.Basis, PrivilegeFields.Keys.WorkProduct));
    }

    private static Task<CodingWriteResult> CodeAsync(BulkCodingHarness h, Guid ws, Guid user, Guid documentId, int status, int? basis = null) =>
        h.InteractiveAsync(ws, user, documentId, basis is { } b
            ? [CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(status)), CodingFieldOperation.AddChoices(PrivilegeFields.Basis, b)]
            : [CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(status))]);

    private static async Task<Guid> OriginAsync(BulkCodingHarness h, Guid ws, Guid documentId) =>
        (await h.Coding.GetEventsAsync(new CodingEventQuery(ws) { DocumentId = documentId, FieldId = PrivilegeFields.Status }, Ct)).Events.Single().EventId;

    private static async Task FamilyAsync(BulkCodingHarness h, Guid ws, Guid parent, params Guid[] attachments)
    {
        for (var i = 0; i < attachments.Length; i++)
        {
            await h.Db.Core.ExecuteAsync(
                "UPDATE opportunity.document SET family_id = @parent, parent_document_id = @parent, family_sequence = @seq WHERE workspace_id = @ws AND document_id = @doc",
                ("ws", ws), ("parent", parent), ("seq", i + 1), ("doc", attachments[i]));
        }
    }

    private static async Task<Guid> DuplicatesAsync(BulkCodingHarness h, Guid ws, Guid primary, params Guid[] duplicates)
    {
        var group = Guid.CreateVersion7();
        await h.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.duplicate_group (workspace_id, duplicate_group_id, source, hash_kind, hash_value) VALUES (@ws, @group, 1, 1, @value)",
            ("ws", ws), ("group", group), ("value", "DUP-" + group.ToString("N")));
        await h.Db.Core.ExecuteAsync(
            "UPDATE opportunity.document SET duplicate_group_id = @group, is_duplicate_primary = (document_id = @primary) WHERE workspace_id = @ws AND document_id = ANY(@docs)",
            ("ws", ws), ("group", group), ("primary", primary), ("docs", duplicates.Append(primary).ToArray()));
        return group;
    }

    private static string WithoutTime(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("generatedAt");
        return node.ToJsonString();
    }

    private static async Task<string> TextAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, text);
            return text;
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Guid user, JsonObject body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, Ct);
    }
}
