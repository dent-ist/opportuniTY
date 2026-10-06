using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Core.Coding;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// E09-T05 through the real API host: the related-items view (hidden members never listed, restriction-class denials
/// counted, walled ones not), the propagation preview (targets, conflicts, mode), the interactive apply (SystemRule
/// events referencing the originating edit, Coding.FamilyApplied, Idempotency-Key, PREVIEW_STALE) and the job apply
/// above the threshold (BulkHuman events, Q-07 skip, a reviewer's Coding.Write is enough).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class CodingPropagationApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Relationships_and_interactive_propagation_show_only_what_the_reviewer_may_see_and_reference_the_origin()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var reviewer = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var other = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var docs = await h.DocumentsAsync(w.Id, 5, "PROP");
        await FamilyAsync(h, w.Id, docs[0], docs[1], docs[2], docs[3]);
        await DuplicatesAsync(h, w.Id, docs[0], docs[4]);
        await h.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, @class)",
            ("ws", w.Id), ("doc", docs[2]), ("class", RestrictionClasses.AttorneysEyesOnly));
        await h.Db.WallAsync(w.Id, [reviewer], [], [docs[3]]);

        var source = await h.InteractiveAsync(w.Id, reviewer, docs[0], CodingFieldOperation.Set(w.Responsive, true));
        await h.InteractiveAsync(w.Id, other, docs[1], CodingFieldOperation.Set(w.Responsive, false));
        var origin = (await h.Coding.GetEventsAsync(new CodingEventQuery(w.Id) { DocumentId = docs[0], FieldId = w.Responsive }, Ct)).Events.Single();
        source.Outcome.Should().Be(CodingWriteOutcome.Applied);

        await using var factory = Factory(h.Db.Core.AppConnectionString);
        using var client = factory.CreateClient();
        var ws = $"/api/v1/workspaces/{w.Id}";

        using (var response = await GetAsync(client, $"{ws}/documents/{docs[0]}/relationships?fields=responsive", reviewer))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var body = await JsonAsync(response);
            var family = body.GetProperty("family");
            family.GetProperty("familyId").GetGuid().Should().Be(docs[0]);
            family.GetProperty("parent").GetProperty("documentId").GetGuid().Should().Be(docs[0]);
            family.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("documentId").GetGuid()).Should().Equal(docs[0], docs[1]);
            family.GetProperty("restrictedCount").GetInt32().Should().Be(1, "the AEO attachment is counted; the walled one is hidden entirely (Q-13)");
            var self = family.GetProperty("members")[0];
            self.GetProperty("isSelf").GetBoolean().Should().BeTrue();
            self.GetProperty("isParent").GetBoolean().Should().BeTrue();
            self.GetProperty("coding").GetProperty("responsive").EnumerateArray().Select(v => v.GetString()).Should().Equal("Yes");
            family.GetProperty("members")[1].GetProperty("coding").GetProperty("responsive")[0].GetString().Should().Be("No");
            var duplicates = body.GetProperty("duplicates");
            duplicates.GetProperty("primaryDocumentId").GetGuid().Should().Be(docs[0]);
            duplicates.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("documentId").GetGuid()).Should().Equal(docs[0], docs[4]);
            body.GetProperty("thread").GetProperty("emailThreadId").ValueKind.Should().Be(JsonValueKind.Null);
            body.GetRawText().Should().NotContain(docs[2].ToString()).And.NotContain(docs[3].ToString());
        }

        using (var hidden = await GetAsync(client, $"{ws}/documents/{docs[3]}/relationships", reviewer))
        {
            hidden.StatusCode.Should().Be(HttpStatusCode.NotFound, "a walled anchor is the document 404");
        }

        using (var forbidden = await PostAsync(client, $"{ws}/coding-propagations/preview", reviewer, Preview(docs[0], "family", w.Confidentiality), null))
        {
            forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a confidentiality field needs Coding.WritePrivilege");
        }

        Guid previewId;
        using (var response = await PostAsync(client, $"{ws}/coding-propagations/preview", reviewer, Preview(docs[0], "familyAndDuplicates", w.Responsive), null))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var preview = await JsonAsync(response);
            previewId = preview.GetProperty("previewId").GetGuid();
            preview.GetProperty("targetCount").GetInt32().Should().Be(2);
            preview.GetProperty("conflictCount").GetInt32().Should().Be(1);
            preview.GetProperty("restrictedCount").GetInt32().Should().Be(1);
            preview.GetProperty("skippedCount").GetInt32().Should().Be(0);
            preview.GetProperty("mode").GetString().Should().Be("interactive");
            preview.GetProperty("threshold").GetInt32().Should().Be(1_000);
            var conflict = preview.GetProperty("conflicts").EnumerateArray().Single();
            conflict.GetProperty("documentId").GetGuid().Should().Be(docs[1]);
            conflict.GetProperty("currentValues")[0].GetString().Should().Be("No");
            conflict.GetProperty("newValues")[0].GetString().Should().Be("Yes");
        }

        var apply = $"{ws}/coding-propagations";
        using (var missingKey = await PostAsync(client, apply, reviewer, new JsonObject { ["previewId"] = previewId }, null))
        {
            missingKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        using (var notYours = await PostAsync(client, apply, other, new JsonObject { ["previewId"] = previewId }, "k-other"))
        {
            notYours.StatusCode.Should().Be(HttpStatusCode.NotFound, "a preview belongs to its creator");
        }

        using (var response = await PostAsync(client, apply, reviewer, new JsonObject { ["previewId"] = previewId }, "k-1"))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var result = await JsonAsync(response);
            result.GetProperty("mode").GetString().Should().Be("interactive");
            result.GetProperty("applied").GetInt32().Should().Be(2);
            result.GetProperty("skipped").GetInt32().Should().Be(0);
        }

        foreach (var target in new[] { docs[1], docs[4] })
        {
            var propagated = (await h.Coding.GetEventsAsync(
                new CodingEventQuery(w.Id) { DocumentId = target, FieldId = w.Responsive, ActorType = CodingActorType.SystemRule }, Ct)).Events.Single();
            propagated.OriginEventId.Should().Be(origin.EventId, "a propagated change references the originating edit");
            propagated.ActorId.Should().Be(reviewer);
        }

        (await h.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Coding' AND action = 'FamilyApplied' AND resource_id = @doc",
            ("ws", w.Id), ("doc", docs[0].ToString()))).Should().Be(1);

        // The source changes after the preview: applying it again is refused.
        await h.InteractiveAsync(w.Id, reviewer, docs[0], CodingFieldOperation.Set(w.Responsive, false));
        using (var stale = await PostAsync(client, apply, reviewer, new JsonObject { ["previewId"] = previewId }, "k-2"))
        {
            stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await JsonAsync(stale)).GetProperty("code").GetString().Should().Be("PREVIEW_STALE");
        }
    }

    [Fact]
    public async Task Above_the_threshold_a_propagation_is_a_bulk_job_with_the_Q07_skip_rule()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var reviewer = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var other = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var docs = await h.DocumentsAsync(w.Id, 3, "PJOB");
        await FamilyAsync(h, w.Id, docs[0], docs[1], docs[2]);
        await h.InteractiveAsync(w.Id, reviewer, docs[0], CodingFieldOperation.Set(w.Responsive, true));
        var origin = (await h.Coding.GetEventsAsync(new CodingEventQuery(w.Id) { DocumentId = docs[0] }, Ct)).Events.Single();

        await using var factory = Factory(h.Db.Core.AppConnectionString, b =>
        {
            b.UseSetting("Coding:Propagation:InteractiveThreshold", "1");
            b.UseSetting("Snapshots:BackgroundEnabled", "false");
        });
        using var client = factory.CreateClient();
        var ws = $"/api/v1/workspaces/{w.Id}";
        Guid previewId;
        using (var response = await PostAsync(client, $"{ws}/coding-propagations/preview", reviewer, Preview(docs[0], "family", w.Responsive), null))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var preview = await JsonAsync(response);
            preview.GetProperty("mode").GetString().Should().Be("job");
            preview.GetProperty("targetCount").GetInt32().Should().Be(2);
            previewId = preview.GetProperty("previewId").GetGuid();
        }

        Guid jobId;
        using (var response = await PostAsync(client, $"{ws}/coding-propagations", reviewer, new JsonObject { ["previewId"] = previewId }, "k-job"))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
            var result = await JsonAsync(response);
            result.GetProperty("mode").GetString().Should().Be("job");
            jobId = result.GetProperty("job").GetProperty("jobId").GetGuid();
            result.GetProperty("job").GetProperty("jobType").GetString().Should().Be("bulkCoding");
            response.Headers.Location!.ToString().Should().Be($"{ws}/jobs/{jobId}", "the job monitor follows it");
        }

        // Someone else codes a target after the job froze its set: the job leaves it alone (Q-07).
        await h.InteractiveAsync(w.Id, other, docs[2], CodingFieldOperation.Set(w.Responsive, false));
        var job = await h.RunAsync(w.Id, jobId);
        job.Status.Should().Be(Core.Jobs.JobStatus.Completed, "the reviewer's Coding.Write runs a propagation job (no Coding.Bulk needed)");
        job.Counters.ItemsApplied.Should().Be(1);
        job.Counters.ItemsSkippedConcurrentEdit.Should().Be(1);

        var values = await h.ValuesAsync(w.Id, [docs[1], docs[2]], w.Responsive);
        values[docs[1]]!.GetValue<bool>().Should().BeTrue();
        values[docs[2]]!.GetValue<bool>().Should().BeFalse();
        var propagated = (await h.Coding.GetEventsAsync(new CodingEventQuery(w.Id) { JobId = jobId, Kind = CodingEventKind.ValueChanged }, Ct)).Events.Single();
        propagated.DocumentId.Should().Be(docs[1]);
        propagated.ActorType.Should().Be(CodingActorType.BulkHuman);
        propagated.OriginEventId.Should().Be(origin.EventId);
        (await h.AuditActionsAsync(w.Id, jobId)).Should().ContainKey("Coding.FamilyApplied");
    }

    /// <summary><paramref name="parent"/> heads a family of the given attachments (sequence 1…n).</summary>
    private static async Task FamilyAsync(BulkCodingHarness h, Guid ws, Guid parent, params Guid[] attachments)
    {
        for (var i = 0; i < attachments.Length; i++)
        {
            await h.Db.Core.ExecuteAsync(
                "UPDATE opportunity.document SET family_id = @parent, parent_document_id = @parent, family_sequence = @seq WHERE workspace_id = @ws AND document_id = @doc",
                ("ws", ws), ("parent", parent), ("seq", i + 1), ("doc", attachments[i]));
        }
    }

    private static async Task DuplicatesAsync(BulkCodingHarness h, Guid ws, Guid primary, params Guid[] duplicates)
    {
        var group = Guid.CreateVersion7();
        await h.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.duplicate_group (workspace_id, duplicate_group_id, source, hash_kind, hash_value) VALUES (@ws, @group, 1, 1, @value)",
            ("ws", ws), ("group", group), ("value", "DUP-" + group.ToString("N")));
        await h.Db.Core.ExecuteAsync(
            "UPDATE opportunity.document SET duplicate_group_id = @group, is_duplicate_primary = (document_id = @primary) WHERE workspace_id = @ws AND document_id = ANY(@docs)",
            ("ws", ws), ("group", group), ("primary", primary), ("docs", duplicates.Append(primary).ToArray()));
    }

    private static JsonObject Preview(Guid source, string scope, int fieldId) => new()
    {
        ["sourceDocumentId"] = source,
        ["scope"] = scope,
        ["fields"] = new JsonArray(fieldId),
    };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Guid user, JsonObject body, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }
}
