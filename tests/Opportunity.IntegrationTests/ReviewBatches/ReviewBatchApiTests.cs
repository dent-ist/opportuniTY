using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Opportunity.Application.Coding;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.ReviewBatches;

/// <summary>
/// E10-T05 review batches through the real API host: Batch Sets cut from a ReviewBatch snapshot (families and threads
/// kept together, at most N per batch, frozen membership, counts and members filtered for the caller per Q-52), check-out,
/// check-in and assignment with If-Match and audit, and first-pass vs QC conflicts from CodingEvent provenance.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ReviewBatchApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Batch_sets_keep_families_and_threads_together_freeze_membership_and_count_only_visible_documents()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var manager = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var reviewer = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var docs = await h.DocumentsAsync(w.Id, 10, "BAT");
        await FamilyAsync(h, w.Id, docs[0], docs[1], docs[2]);
        await FamilyAsync(h, w.Id, docs[5], docs[6]);
        await ThreadAsync(h, w.Id, docs[3], docs[8]);

        await using var factory = Factory(h);
        using var client = factory.CreateClient();
        var api = new Api(client, w.Id);
        var snapshotId = await api.SnapshotAsync(manager, docs);

        var created = await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager,
            SetBody("First pass", snapshotId, "FirstPass", 4), key: "create-1");
        created.Status.Should().Be(HttpStatusCode.Created, created.Text);
        var set = created.Json;
        var setId = set.GetProperty("batchSetId").GetGuid();
        created.Location.Should().Be($"/api/v1/workspaces/{w.Id}/review-batch-sets/{setId}");
        set.GetProperty("documentCount").GetInt64().Should().Be(10);
        set.GetProperty("keepFamiliesTogether").GetBoolean().Should().BeTrue();
        set.GetProperty("reviewPass").GetString().Should().Be("firstPass");
        var batchCount = set.GetProperty("batchCount").GetInt32();
        set.GetProperty("statusCounts").GetProperty("available").GetInt32().Should().Be(batchCount);

        var batches = await api.BatchesAsync(reviewer, setId);
        batches.Select(b => b.GetProperty("name").GetString()).Should().Equal(
            Enumerable.Range(1, batchCount).Select(i => $"FirstPass_{i:D4}"));
        var members = new Dictionary<string, List<Guid>>();
        foreach (var batch in batches)
        {
            var listed = await api.MembersAsync(reviewer, batch.GetProperty("batchId").GetGuid());
            listed.Select(m => m.GetProperty("position").GetInt32()).Should().Equal(Enumerable.Range(1, listed.Count));
            members[batch.GetProperty("name").GetString()!] = [.. listed.Select(m => m.GetProperty("documentId").GetGuid())];
            batch.GetProperty("documentCount").GetInt32().Should().Be(listed.Count);
            batch.GetProperty("status").GetString().Should().Be("available");
            batch.GetProperty("assignee").ValueKind.Should().Be(JsonValueKind.Null);
        }

        members.Values.SelectMany(m => m).Should().BeEquivalentTo(docs, "every member of the snapshot is in exactly one batch");
        members.Values.Should().OnlyContain(m => m.Count <= 4, "no family is larger than the batch size");
        members.Values.Should().ContainSingle(m => m.Contains(docs[0])).Which.Should().Contain([docs[1], docs[2]], "a family is never split");
        members.Values.Should().ContainSingle(m => m.Contains(docs[5])).Which.Should().Contain(docs[6]);

        var audit = await h.Db.Core.ColumnAsync(
            $"SELECT action || '|' || (details->>'documentCount') FROM audit.audit_event WHERE workspace_id = '{w.Id}' AND resource_id = '{setId}'");
        audit.Should().Equal("ReviewBatchSet.Created|10");

        // Keep threads together: an email thread spans two families; with one group per batch every group is a batch.
        var threads = await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager,
            SetBody("By thread", snapshotId, "Threads", 1, keepThreads: true), key: "create-2");
        threads.Status.Should().Be(HttpStatusCode.Created, threads.Text);
        var threadBatches = await api.BatchesAsync(manager, threads.Json.GetProperty("batchSetId").GetGuid());
        threadBatches.Should().HaveCount(6, "groups: the 3-document family, the 2-document family, the thread of documents 3 and 8, and three singletons");
        var threadMembers = new List<List<Guid>>();
        foreach (var batch in threadBatches)
        {
            threadMembers.Add([.. (await api.MembersAsync(manager, batch.GetProperty("batchId").GetGuid())).Select(m => m.GetProperty("documentId").GetGuid())]);
        }

        threadMembers.Should().ContainSingle(m => m.Contains(docs[3])).Which.Should().BeEquivalentTo([docs[3], docs[8]]);

        // Membership is frozen: a later family change moves nothing.
        await h.Db.Core.ExecuteAsync(
            "UPDATE opportunity.document SET family_id = @parent, parent_document_id = @parent, family_sequence = 9 WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", w.Id), ("parent", docs[0]), ("doc", docs[9]));
        foreach (var batch in batches)
        {
            (await api.MembersAsync(reviewer, batch.GetProperty("batchId").GetGuid())).Select(m => m.GetProperty("documentId").GetGuid())
                .Should().Equal(members[batch.GetProperty("name").GetString()!]);
        }

        // Q-52: a document the caller may not see is left out of members and of every count.
        await h.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, @class)",
            ("ws", w.Id), ("doc", docs[1]), ("class", RestrictionClasses.AttorneysEyesOnly));
        var holder = batches.Single(b => members[b.GetProperty("name").GetString()!].Contains(docs[1]));
        var holderId = holder.GetProperty("batchId").GetGuid();
        var visible = await api.SendAsync(HttpMethod.Get, $"/review-batches/{holderId}", reviewer);
        visible.Json.GetProperty("documentCount").GetInt32().Should().Be(holder.GetProperty("documentCount").GetInt32() - 1);
        (await api.MembersAsync(reviewer, holderId)).Select(m => m.GetProperty("documentId").GetGuid()).Should().NotContain(docs[1]);
        (await api.SendAsync(HttpMethod.Get, $"/review-batch-sets/{setId}", reviewer)).Json.GetProperty("documentCount").GetInt64().Should().Be(9);
        (await api.SendAsync(HttpMethod.Get, "/review-batch-sets", reviewer)).Text.Should().Contain("\"documentCount\":9");
        visible.Text.Should().NotContain(docs[1].ToString());

        // Validation: the prefix is unique, the snapshot must be the caller's own ReviewBatch set, Reviewers cannot create sets.
        (await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager, SetBody("Again", snapshotId, "firstpass", 4), key: "create-3"))
            .Status.Should().Be(HttpStatusCode.Conflict, "batch prefixes are unique in the workspace, ignoring case");
        var bulkSnapshot = await h.SnapshotAsync(w.Id, manager, [docs[4]]);
        var wrongPurpose = await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager, SetBody("Bulk", bulkSnapshot.SnapshotId, "Bulk", 4), key: "create-4");
        wrongPurpose.Status.Should().Be(HttpStatusCode.BadRequest, wrongPurpose.Text);
        wrongPurpose.Text.Should().Contain("snapshotId");
        (await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager, SetBody("Missing", Guid.CreateVersion7(), "Missing", 4), key: "create-5"))
            .Status.Should().Be(HttpStatusCode.NotFound);
        (await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager, SetBody("Empty", snapshotId, "Zero", 0), key: "create-6"))
            .Status.Should().Be(HttpStatusCode.BadRequest);
        (await api.SendAsync(HttpMethod.Post, "/review-batch-sets", reviewer, SetBody("Not mine", snapshotId, "Reviewer", 4), key: "create-7"))
            .Status.Should().Be(HttpStatusCode.Forbidden);
        var other = await h.MemberAsync(w.Id, WorkspaceRole.WorkspaceAdmin);
        (await api.SendAsync(HttpMethod.Post, "/review-batch-sets", other, SetBody("Someone else's", snapshotId, "Other", 4), key: "create-8"))
            .Status.Should().Be(HttpStatusCode.NotFound, "a snapshot is used only by its creator");
        (await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager, SetBody("No key", snapshotId, "NoKey", 4)))
            .Status.Should().Be(HttpStatusCode.BadRequest, "creating a Batch Set needs an Idempotency-Key");
    }

    [Fact]
    public async Task Batches_are_checked_out_checked_in_and_assigned_with_if_match_and_audited()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var manager = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var reviewer = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var reviewer2 = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var outsider = await h.Db.CreateUserAsync();
        var docs = await h.DocumentsAsync(w.Id, 4, "CHK");

        await using var factory = Factory(h);
        using var client = factory.CreateClient();
        var api = new Api(client, w.Id);
        var snapshotId = await api.SnapshotAsync(manager, docs);
        var set = await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager, SetBody("Responsiveness", snapshotId, "Resp", 2), key: "k1");
        set.Status.Should().Be(HttpStatusCode.Created, set.Text);
        var batchId = (await api.BatchesAsync(reviewer, set.Json.GetProperty("batchSetId").GetGuid()))[0].GetProperty("batchId").GetGuid();
        var batchUrl = $"/review-batches/{batchId}";

        var read = await api.SendAsync(HttpMethod.Get, batchUrl, reviewer);
        read.ETag.Should().Be("\"1\"");
        (await api.SendAsync(HttpMethod.Post, batchUrl + "/check-out", reviewer)).Status.Should().Be(HttpStatusCode.PreconditionRequired);
        (await api.SendAsync(HttpMethod.Post, batchUrl + "/check-out", reviewer, ifMatch: "\"7\"")).Status.Should().Be(HttpStatusCode.PreconditionFailed);

        var checkedOut = await api.SendAsync(HttpMethod.Post, batchUrl + "/check-out", reviewer, ifMatch: read.ETag);
        checkedOut.Status.Should().Be(HttpStatusCode.OK, checkedOut.Text);
        checkedOut.Json.GetProperty("status").GetString().Should().Be("checkedOut");
        checkedOut.Json.GetProperty("assignee").GetProperty("userId").GetGuid().Should().Be(reviewer);
        checkedOut.ETag.Should().Be("\"2\"");

        (await api.SendAsync(HttpMethod.Post, batchUrl + "/check-out", reviewer2, ifMatch: "*")).Status.Should().Be(HttpStatusCode.Conflict,
            "one reviewer holds a batch at a time");
        (await api.SendAsync(HttpMethod.Post, batchUrl + "/check-in", reviewer2, Completed(true), ifMatch: "*")).Status.Should().Be(HttpStatusCode.Forbidden,
            "only the holder (or a manager) checks a batch in");

        var mine = await api.SendAsync(HttpMethod.Get, "/review-batches?assigneeId=me&status=checkedOut", reviewer);
        mine.Json.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("batchId").GetGuid()).Should().Equal(batchId);
        (await api.SendAsync(HttpMethod.Get, "/review-batches?assigneeId=me", reviewer2)).Json.GetProperty("items").GetArrayLength().Should().Be(0);
        (await api.SendAsync(HttpMethod.Get, "/review-batches?status=bogus", reviewer)).Status.Should().Be(HttpStatusCode.BadRequest);

        var completed = await api.SendAsync(HttpMethod.Post, batchUrl + "/check-in", reviewer, Completed(true), ifMatch: checkedOut.ETag);
        completed.Status.Should().Be(HttpStatusCode.OK, completed.Text);
        completed.Json.GetProperty("status").GetString().Should().Be("completed");
        completed.Json.GetProperty("assignee").GetProperty("userId").GetGuid().Should().Be(reviewer, "the reviewer who completed it stays recorded");
        (await api.SendAsync(HttpMethod.Post, batchUrl + "/check-in", reviewer, Completed(false), ifMatch: "*")).Status.Should().Be(HttpStatusCode.Conflict);

        // Assignment is a manager's: reviewers are refused by PEP-1; the assignee must be a member who may code.
        (await api.SendAsync(HttpMethod.Put, batchUrl + "/assignment", reviewer, Assign(reviewer2), ifMatch: "*")).Status.Should().Be(HttpStatusCode.Forbidden);
        var refused = await api.SendAsync(HttpMethod.Put, batchUrl + "/assignment", manager, Assign(outsider), ifMatch: "*");
        refused.Status.Should().Be(HttpStatusCode.BadRequest, refused.Text);
        refused.Text.Should().Contain("assigneeId");
        (await api.SendAsync(HttpMethod.Put, batchUrl + "/assignment", manager, Assign(reviewer2))).Status.Should().Be(HttpStatusCode.PreconditionRequired);

        var reopened = await api.SendAsync(HttpMethod.Put, batchUrl + "/assignment", manager, Assign(reviewer2), ifMatch: completed.ETag);
        reopened.Status.Should().Be(HttpStatusCode.OK, reopened.Text);
        reopened.Json.GetProperty("status").GetString().Should().Be("checkedOut", "assigning reopens a completed batch for re-review");
        reopened.Json.GetProperty("assignee").GetProperty("userId").GetGuid().Should().Be(reviewer2);
        reopened.Json.GetProperty("statusChangedBy").GetGuid().Should().Be(manager);

        var stale = await api.SendAsync(HttpMethod.Put, batchUrl + "/assignment", manager, Assign(null), ifMatch: completed.ETag);
        stale.Status.Should().Be(HttpStatusCode.PreconditionFailed);
        var released = await api.SendAsync(HttpMethod.Put, batchUrl + "/assignment", manager, Assign(null), ifMatch: reopened.ETag);
        released.Status.Should().Be(HttpStatusCode.OK, released.Text);
        released.Json.GetProperty("status").GetString().Should().Be("available");
        released.Json.GetProperty("assignee").ValueKind.Should().Be(JsonValueKind.Null);
        released.ETag.Should().Be("\"5\"");

        var audit = await h.Db.Core.ColumnAsync(
            $"SELECT action || '|' || coalesce(details->>'statusBefore', '') || '>' || coalesce(details->>'statusAfter', '') || '|' || actor_id FROM audit.audit_event WHERE workspace_id = '{w.Id}' AND resource_id = '{batchId}' ORDER BY occurred_at, event_id");
        audit.Should().Equal(
            $"ReviewBatch.CheckedOut|Available>CheckedOut|{reviewer}",
            $"ReviewBatch.CheckedIn|CheckedOut>Completed|{reviewer}",
            $"ReviewBatch.Assigned|Completed>CheckedOut|{manager}",
            $"ReviewBatch.Assigned|CheckedOut>Available|{manager}");
        (await h.Db.Core.ColumnAsync(
            $"SELECT user_id || '|' || end_kind FROM opportunity.review_batch_checkout WHERE workspace_id = '{w.Id}' AND batch_id = '{batchId}' ORDER BY checked_out_at"))
            .Should().Equal($"{reviewer}|2", $"{reviewer2}|3");

        // A reviewer group limits who may check batches out of a set.
        var grouped = await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager,
            SetBody("QC team only", snapshotId, "Team", 4, reviewerGroup: "qc-team"), key: "k2");
        grouped.Status.Should().Be(HttpStatusCode.Created, grouped.Text);
        var teamBatch = (await api.BatchesAsync(reviewer, grouped.Json.GetProperty("batchSetId").GetGuid()))[0].GetProperty("batchId").GetGuid();
        (await api.SendAsync(HttpMethod.Post, $"/review-batches/{teamBatch}/check-out", reviewer, ifMatch: "*")).Status.Should().Be(HttpStatusCode.Forbidden);
        (await api.SendAsync(HttpMethod.Put, $"/review-batches/{teamBatch}/assignment", manager, Assign(reviewer2), ifMatch: "*"))
            .Status.Should().Be(HttpStatusCode.BadRequest, "the assignee is not in the reviewer group");
        (await api.SendAsync(HttpMethod.Post, $"/review-batches/{teamBatch}/check-out", reviewer, ifMatch: "*", groups: "qc-team"))
            .Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Conflicting_first_pass_and_qc_calls_are_detectable_without_hidden_fields_or_documents()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var admin = await h.MemberAsync(w.Id, WorkspaceRole.WorkspaceAdmin);
        var manager = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var firstReviewer = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var qcReviewer = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var docs = await h.DocumentsAsync(w.Id, 4, "QC");
        var (a, b, c) = (docs[0], docs[1], docs[2]);

        await using var factory = Factory(h);
        using var client = factory.CreateClient();
        var api = new Api(client, w.Id);
        var firstPass = await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager,
            SetBody("First pass", await api.SnapshotAsync(manager, docs), "FP", 10), key: "fp");
        firstPass.Status.Should().Be(HttpStatusCode.Created, firstPass.Text);
        var firstPassId = firstPass.Json.GetProperty("batchSetId").GetGuid();
        var qcBody = SetBody("QC", await api.SnapshotAsync(manager, [a, b, c]), "QC", 10);
        qcBody["reviewPass"] = "qc";
        qcBody["qcOfBatchSetId"] = firstPassId.ToString();
        var qc = await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager, qcBody, key: "qc");
        qc.Status.Should().Be(HttpStatusCode.Created, qc.Text);
        var qcId = qc.Json.GetProperty("batchSetId").GetGuid();
        qc.Json.GetProperty("qcOfBatchSetId").GetGuid().Should().Be(firstPassId);

        // First pass: the reviewer's calls while holding the batch...
        var fpBatch = (await api.BatchesAsync(firstReviewer, firstPassId)).Single().GetProperty("batchId").GetGuid();
        var held = await api.SendAsync(HttpMethod.Post, $"/review-batches/{fpBatch}/check-out", firstReviewer, ifMatch: "*");
        held.Status.Should().Be(HttpStatusCode.OK, held.Text);
        foreach (var doc in new[] { a, b, c, docs[3] })
        {
            await h.InteractiveAsync(w.Id, firstReviewer, doc, CodingFieldOperation.Set(w.Responsive, true));
        }

        await h.InteractiveAsync(w.Id, firstReviewer, a, CodingFieldOperation.Set(w.Notes, "first pass note"));
        (await api.SendAsync(HttpMethod.Post, $"/review-batches/{fpBatch}/check-in", firstReviewer, Completed(true), ifMatch: "*")).Status
            .Should().Be(HttpStatusCode.OK);

        // ...and an edit after check-in, which is not a first-pass call.
        await h.InteractiveAsync(w.Id, firstReviewer, c, CodingFieldOperation.Set(w.Responsive, false));

        // QC: overturns A and B, agrees with the first pass on C, codes a field the first pass did not call, changes A's note.
        var qcBatch = (await api.BatchesAsync(qcReviewer, qcId)).Single().GetProperty("batchId").GetGuid();
        (await api.SendAsync(HttpMethod.Post, $"/review-batches/{qcBatch}/check-out", qcReviewer, ifMatch: "*")).Status.Should().Be(HttpStatusCode.OK);
        await h.InteractiveAsync(w.Id, qcReviewer, a, CodingFieldOperation.Set(w.Responsive, false));
        await h.InteractiveAsync(w.Id, qcReviewer, b, CodingFieldOperation.Set(w.Responsive, false));
        await h.InteractiveAsync(w.Id, qcReviewer, c, CodingFieldOperation.Set(w.Responsive, true));
        await h.InteractiveAsync(w.Id, qcReviewer, b, CodingFieldOperation.Set(w.Issues, new JsonArray(w.IssueB)));
        await h.InteractiveAsync(w.Id, qcReviewer, a, CodingFieldOperation.Set(w.Notes, "qc note"));

        // Reviewer Notes is visible to Workspace Admins only.
        await h.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.field_security (workspace_id, field_id, visible_roles, editable_roles) VALUES (@ws, @field, '{WorkspaceAdmin}', '{WorkspaceAdmin}')",
            ("ws", w.Id), ("field", w.Notes));

        var conflictsUrl = $"/review-batch-sets/{qcId}/conflicts";
        var all = await api.ConflictsAsync(admin, conflictsUrl);
        all.Select(x => (x.GetProperty("documentId").GetGuid(), x.GetProperty("fieldId").GetInt32())).Should().BeEquivalentTo(
            [(a, w.Responsive), (a, w.Notes), (b, w.Responsive)],
            "C agrees with the first-pass call made while the batch was held (the later edit outside it is no call), and Issues had no first-pass call");
        var aResponsive = all.Single(x => x.GetProperty("documentId").GetGuid() == a && x.GetProperty("fieldId").GetInt32() == w.Responsive);
        aResponsive.GetProperty("controlNumber").GetString().Should().StartWith("QC");
        aResponsive.GetProperty("fieldName").GetString().Should().Be("Responsive");
        aResponsive.GetProperty("firstPass").GetProperty("value").GetBoolean().Should().BeTrue();
        aResponsive.GetProperty("firstPass").GetProperty("reviewer").GetProperty("userId").GetGuid().Should().Be(firstReviewer);
        aResponsive.GetProperty("firstPass").GetProperty("batchName").GetString().Should().Be("FP_0001");
        aResponsive.GetProperty("qc").GetProperty("value").GetBoolean().Should().BeFalse();
        aResponsive.GetProperty("qc").GetProperty("reviewer").GetProperty("userId").GetGuid().Should().Be(qcReviewer);
        aResponsive.GetProperty("qc").GetProperty("batchId").GetGuid().Should().Be(qcBatch);
        var aNotes = all.Single(x => x.GetProperty("fieldId").GetInt32() == w.Notes);
        aNotes.GetProperty("firstPass").GetProperty("value").GetString().Should().Be("first pass note");
        aNotes.GetProperty("qc").GetProperty("value").GetString().Should().Be("qc note");

        // The QC manager may not see Reviewer Notes: that conflict is left out.
        (await api.ConflictsAsync(manager, conflictsUrl)).Select(x => (x.GetProperty("documentId").GetGuid(), x.GetProperty("fieldId").GetInt32()))
            .Should().BeEquivalentTo([(a, w.Responsive), (b, w.Responsive)]);

        // A document the manager may not see is left out entirely (Q-52); paging still reaches everything for the admin.
        await h.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, @class)",
            ("ws", w.Id), ("doc", b), ("class", RestrictionClasses.AttorneysEyesOnly));
        var managerView = await api.SendAsync(HttpMethod.Get, conflictsUrl, manager);
        managerView.Json.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("documentId").GetGuid()).Should().Equal(a);
        managerView.Text.Should().NotContain(b.ToString());
        (await api.ConflictsAsync(admin, conflictsUrl, limit: 1)).Should().HaveCount(3);

        (await api.SendAsync(HttpMethod.Get, $"/review-batch-sets/{firstPassId}/conflicts", manager)).Status.Should().Be(HttpStatusCode.Conflict,
            "conflicts are read on the QC set");
        (await api.SendAsync(HttpMethod.Get, conflictsUrl, qcReviewer)).Status.Should().Be(HttpStatusCode.Forbidden, "reading conflicts needs ReviewBatch.Manage");
    }

    private static WebApplicationFactory<Program> Factory(BulkCodingHarness h) =>
        CodingApiHarness.Factory(h.Db.Core.AppConnectionString, b => b.UseSetting("Snapshots:BackgroundEnabled", "false"));

    private static JsonObject SetBody(string name, Guid snapshotId, string prefix, int size, bool keepThreads = false, string? reviewerGroup = null) => new()
    {
        ["name"] = name,
        ["snapshotId"] = snapshotId.ToString(),
        ["batchPrefix"] = prefix,
        ["maxBatchSize"] = size,
        ["keepThreadsTogether"] = keepThreads,
        ["reviewerGroup"] = reviewerGroup,
    };

    private static JsonObject Completed(bool completed) => new() { ["completed"] = completed };

    private static JsonObject Assign(Guid? assignee) => new() { ["assigneeId"] = assignee?.ToString() };

    private static async Task FamilyAsync(BulkCodingHarness h, Guid ws, Guid parent, params Guid[] attachments)
    {
        for (var i = 0; i < attachments.Length; i++)
        {
            await h.Db.Core.ExecuteAsync(
                "UPDATE opportunity.document SET family_id = @parent, parent_document_id = @parent, family_sequence = @seq WHERE workspace_id = @ws AND document_id = @doc",
                ("ws", ws), ("parent", parent), ("seq", i + 1), ("doc", attachments[i]));
        }
    }

    private static async Task ThreadAsync(BulkCodingHarness h, Guid ws, params Guid[] documents)
    {
        var thread = Guid.CreateVersion7();
        await h.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.email_thread (workspace_id, email_thread_id, source, thread_key) VALUES (@ws, @id, 1, @key)",
            ("ws", ws), ("id", thread), ("key", "thread-" + thread.ToString("N")));
        await h.Db.Core.ExecuteAsync(
            "UPDATE opportunity.document SET email_thread_id = @thread, email_thread_source = 1 WHERE workspace_id = @ws AND document_id = ANY (@docs)",
            ("ws", ws), ("thread", thread), ("docs", documents));
    }

    private sealed record Reply(HttpStatusCode Status, string Text, string? ETag, string? Location)
    {
        public JsonElement Json => JsonDocument.Parse(Text).RootElement.Clone();
    }

    private sealed class Api(HttpClient client, Guid workspaceId)
    {
        private readonly string _prefix = $"/api/v1/workspaces/{workspaceId}";

        public async Task<Reply> SendAsync(
            HttpMethod method, string path, Guid user, JsonNode? body = null, string? ifMatch = null, string? key = null, string? groups = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(_prefix + path, UriKind.Relative));
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
            if (groups is not null)
            {
                request.Headers.Add(TestAuthentication.GroupsHeader, groups);
            }

            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            if (key is not null)
            {
                request.Headers.Add("Idempotency-Key", key);
            }

            using var response = await client.SendAsync(request, Ct);
            return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response.Headers.ETag?.ToString(),
                response.Headers.Location?.ToString());
        }

        public async Task<Guid> SnapshotAsync(Guid user, IEnumerable<Guid> documents)
        {
            var reply = await SendAsync(HttpMethod.Post, "/snapshots", user, new JsonObject
            {
                ["purpose"] = "ReviewBatch",
                ["documentIds"] = new JsonArray([.. documents.Select(d => (JsonNode)d.ToString())]),
            }, key: Guid.NewGuid().ToString("N"));
            reply.Status.Should().Be(HttpStatusCode.Created, reply.Text);
            return reply.Json.GetProperty("snapshotId").GetGuid();
        }

        public async Task<List<JsonElement>> BatchesAsync(Guid user, Guid batchSetId)
        {
            var reply = await SendAsync(HttpMethod.Get, $"/review-batches?batchSetId={batchSetId}&limit=500", user);
            reply.Status.Should().Be(HttpStatusCode.OK, reply.Text);
            return [.. reply.Json.GetProperty("items").EnumerateArray()];
        }

        public async Task<List<JsonElement>> MembersAsync(Guid user, Guid batchId)
        {
            var reply = await SendAsync(HttpMethod.Get, $"/review-batches/{batchId}/documents?limit=500", user);
            reply.Status.Should().Be(HttpStatusCode.OK, reply.Text);
            return [.. reply.Json.GetProperty("items").EnumerateArray()];
        }

        /// <summary>Every conflict, following the cursor.</summary>
        public async Task<List<JsonElement>> ConflictsAsync(Guid user, string path, int limit = 50)
        {
            var items = new List<JsonElement>();
            string? cursor = null;
            for (var page = 0; page < 20; page++)
            {
                var reply = await SendAsync(HttpMethod.Get, $"{path}?limit={limit}" + (cursor is null ? string.Empty : "&cursor=" + cursor), user);
                reply.Status.Should().Be(HttpStatusCode.OK, reply.Text);
                items.AddRange(reply.Json.GetProperty("items").EnumerateArray());
                cursor = reply.Json.GetProperty("nextCursor").GetString();
                if (cursor is null)
                {
                    break;
                }
            }

            return items;
        }
    }
}
