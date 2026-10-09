using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Core.Coding;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Migrations;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// E10-T05 coding history through the real API host: field, old and new value, actor, time and the job or "interactive"
/// per change, newest first and cursor-paged; a field hidden from the caller (E05-T06) is never listed and answers like an
/// unknown field as a filter; a document the caller may not see answers like a missing one.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class CodingHistoryApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task History_lists_field_values_actor_time_and_job_or_interactive_and_never_hidden_fields_or_documents()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var admin = await h.MemberAsync(w.Id, WorkspaceRole.WorkspaceAdmin);
        var reviewer = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var other = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var docs = await h.DocumentsAsync(w.Id, 2, "HIST");
        var doc = docs[0];

        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        await h.InteractiveAsync(w.Id, reviewer, doc, CodingFieldOperation.Set(w.Responsive, true));
        await h.InteractiveAsync(w.Id, other, doc, CodingFieldOperation.Set(w.Responsive, false));
        await h.InteractiveAsync(w.Id, admin, doc, CodingFieldOperation.Set(w.Notes, "privileged reviewer note"));
        var snapshot = await h.SnapshotAsync(w.Id, admin, [doc]);
        var job = await h.StartAsync(w.Id, admin, snapshot.SnapshotId, new CodingChange(w.Issues, CodingOperationKind.AddChoices, new JsonArray(w.IssueA)));
        (await h.RunAsync(w.Id, job.JobId)).Status.Should().Be(Core.Jobs.JobStatus.Completed);

        // Reviewer Notes is visible to Workspace Admins only (field-level restriction, E05-T06).
        await h.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.field_security (workspace_id, field_id, visible_roles, editable_roles) VALUES (@ws, @field, '{WorkspaceAdmin}', '{WorkspaceAdmin}')",
            ("ws", w.Id), ("field", w.Notes));

        await using var factory = Factory(h.Db.Core.AppConnectionString);
        using var client = factory.CreateClient();
        var url = $"/api/v1/workspaces/{w.Id}/documents/{doc}/coding-history";

        string reviewerText;
        using (var response = await GetAsync(client, url, reviewer))
        {
            reviewerText = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, reviewerText);
        }

        var items = JsonDocument.Parse(reviewerText).RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("fieldId").GetInt32()).Should().Equal([w.Issues, w.Responsive, w.Responsive], "newest first; the hidden field is left out");
        reviewerText.Should().NotContain("privileged reviewer note").And.NotContain($"\"fieldId\":{w.Notes}");

        var bulk = items[0];
        bulk.GetProperty("fieldName").GetString().Should().Be("Issues");
        bulk.GetProperty("kind").GetString().Should().Be("changed");
        bulk.GetProperty("oldValue").ValueKind.Should().Be(JsonValueKind.Null);
        bulk.GetProperty("newValue").EnumerateArray().Select(v => v.GetInt32()).Should().Equal(w.IssueA);
        bulk.GetProperty("source").GetString().Should().Be("job");
        bulk.GetProperty("jobId").GetGuid().Should().Be(job.JobId);
        bulk.GetProperty("actorType").GetString().Should().Be("bulkHuman");
        bulk.GetProperty("actor").GetProperty("userId").GetGuid().Should().Be(admin);

        var change = items[1];
        change.GetProperty("oldValue").GetBoolean().Should().BeTrue();
        change.GetProperty("newValue").GetBoolean().Should().BeFalse();
        change.GetProperty("source").GetString().Should().Be("interactive");
        change.GetProperty("jobId").ValueKind.Should().Be(JsonValueKind.Null);
        change.GetProperty("actorType").GetString().Should().Be("human");
        change.GetProperty("actor").GetProperty("userId").GetGuid().Should().Be(other);
        change.GetProperty("occurredAt").GetDateTimeOffset().Should().BeAfter(before);
        change.GetProperty("documentVersion").GetInt64().Should().BeLessThan(bulk.GetProperty("documentVersion").GetInt64());
        items[2].GetProperty("oldValue").ValueKind.Should().Be(JsonValueKind.Null);
        items[2].GetProperty("actor").GetProperty("userId").GetGuid().Should().Be(reviewer);

        // A Workspace Admin sees the restricted field's change too.
        using (var response = await GetAsync(client, url, admin))
        {
            var all = (await JsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
            all.Select(i => i.GetProperty("fieldId").GetInt32()).Should().Equal(w.Issues, w.Notes, w.Responsive, w.Responsive);
            all[1].GetProperty("newValue").GetString().Should().Be("privileged reviewer note");
        }

        // One field; a hidden field answers exactly like a field that does not exist.
        using (var response = await GetAsync(client, url + $"?fieldId={w.Responsive}", reviewer))
        {
            (await JsonAsync(response)).GetProperty("items").GetArrayLength().Should().Be(2);
        }

        using (var hidden = await GetAsync(client, url + $"?fieldId={w.Notes}", reviewer))
        using (var unknown = await GetAsync(client, url + "?fieldId=987654", reviewer))
        {
            hidden.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            Normalize(await hidden.Content.ReadAsStringAsync(Ct)).Should().Be(Normalize(await unknown.Content.ReadAsStringAsync(Ct)));
        }

        // Cursor paging, newest first; the cursor is bound to its user.
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            using var response = await GetAsync(client, url + "?limit=1" + (cursor is null ? string.Empty : "&cursor=" + cursor), reviewer);
            var page = await JsonAsync(response);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("eventId").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
            if (cursor is not null)
            {
                using var stolen = await GetAsync(client, url + "?limit=1&cursor=" + cursor, other);
                stolen.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a cursor is bound to the user it was served to");
            }
        }
        while (cursor is not null && pages < 10);

        seen.Should().Equal(items.Select(i => i.GetProperty("eventId").GetGuid()));

        // A document hidden from the reviewer (Attorneys' Eyes Only) is the document 404, exactly like a missing one.
        await h.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, @class)",
            ("ws", w.Id), ("doc", docs[1]), ("class", RestrictionClasses.AttorneysEyesOnly));
        using (var hiddenDoc = await GetAsync(client, $"/api/v1/workspaces/{w.Id}/documents/{docs[1]}/coding-history", reviewer))
        using (var missingDoc = await GetAsync(client, $"/api/v1/workspaces/{w.Id}/documents/{Guid.CreateVersion7()}/coding-history", reviewer))
        {
            hiddenDoc.StatusCode.Should().Be(HttpStatusCode.NotFound);
            Normalize(await hiddenDoc.Content.ReadAsStringAsync(Ct)).Should().Be(Normalize(await missingDoc.Content.ReadAsStringAsync(Ct)));
        }

        using (var visibleToAdmin = await GetAsync(client, $"/api/v1/workspaces/{w.Id}/documents/{docs[1]}/coding-history", admin))
        {
            var page = await JsonAsync(visibleToAdmin);
            visibleToAdmin.StatusCode.Should().Be(HttpStatusCode.OK);
            page.GetProperty("items").GetArrayLength().Should().Be(0);
            page.GetProperty("total").GetProperty("relation").GetString().Should().Be("eq");
        }
    }

    /// <summary>The problem body without its per-request trace and correlation identifiers.</summary>
    private static string Normalize(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("traceId");
        node.Remove("correlationId");
        node.Remove("instance");
        return node.ToJsonString();
    }
}
