using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// E10-T04 through the real API host: <c>POST …/bulk-coding</c> answers 202 with the job and its Location, enforces
/// Idempotency-Key, Coding.Bulk and request validation; progress is the job resource; the report pages per outcome.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class BulkCodingApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Submit_answers_202_with_the_job_and_the_report_pages_each_outcome()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var qc = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var reviewer = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var docs = await h.DocumentsAsync(w.Id, 5);
        var snapshot = await h.SnapshotAsync(w.Id, qc, docs);
        await using var factory = Factory(h.Db.Core.AppConnectionString);
        using var client = factory.CreateClient();
        var url = $"/api/v1/workspaces/{w.Id}/bulk-coding";
        var body = new JsonObject
        {
            ["snapshotId"] = snapshot.SnapshotId,
            ["changes"] = new JsonArray(
                new JsonObject { ["fieldId"] = w.Responsive, ["operation"] = "set", ["value"] = true },
                new JsonObject { ["fieldId"] = w.Issues, ["operation"] = "addChoices", ["value"] = new JsonArray(w.IssueA) },
                new JsonObject { ["fieldId"] = w.Notes, ["operation"] = "clear" }),
        };

        using (var missingKey = await PostAsync(client, url, qc, body, null))
        {
            missingKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        using (var forbidden = await PostAsync(client, url, reviewer, body, "k-reviewer"))
        {
            forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a reviewer has no Coding.Bulk");
        }

        var badValue = body.DeepClone().AsObject();
        badValue["changes"]![2]!["value"] = "x";
        using (var invalid = await PostAsync(client, url, qc, badValue, "k-invalid"))
        {
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, "clear takes no value");
        }

        var unknown = body.DeepClone().AsObject();
        unknown["snapshotId"] = Guid.CreateVersion7();
        using (var notFound = await PostAsync(client, url, qc, unknown, "k-unknown"))
        {
            notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        Guid jobId;
        using (var accepted = await PostAsync(client, url, qc, body, "k-1"))
        {
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(Ct));
            var job = await JsonAsync(accepted);
            jobId = job.GetProperty("jobId").GetGuid();
            accepted.Headers.Location!.ToString().Should().Be($"/api/v1/workspaces/{w.Id}/jobs/{jobId}");
            job.GetProperty("jobType").GetString().Should().Be("bulkCoding");
            job.GetProperty("status").GetString().Should().Be("running");
            job.GetProperty("targetSnapshotId").GetGuid().Should().Be(snapshot.SnapshotId);
            job.GetProperty("committed").GetProperty("chunksTotal").GetInt64().Should().Be(1);
        }

        using (var replay = await PostAsync(client, url, qc, body, "k-1"))
        {
            replay.StatusCode.Should().Be(HttpStatusCode.Accepted);
            (await JsonAsync(replay)).GetProperty("jobId").GetGuid().Should().Be(jobId, "a retry with the same key returns the same job");
        }

        // A reviewer edits one document after the snapshot; the job skips it (Q-07).
        await h.InteractiveAsync(w.Id, reviewer, docs[1], Application.Coding.CodingFieldOperation.Set(w.Responsive, false));
        (await h.RunAsync(w.Id, jobId)).Status.Should().Be(Core.Jobs.JobStatus.Completed);

        using (var progress = await GetAsync(client, $"/api/v1/workspaces/{w.Id}/jobs/{jobId}", qc))
        {
            var job = await JsonAsync(progress);
            job.GetProperty("status").GetString().Should().Be("completed");
            job.GetProperty("committed").GetProperty("itemsApplied").GetInt64().Should().Be(4);
            job.GetProperty("committed").GetProperty("itemsSkippedConcurrentEdit").GetInt64().Should().Be(1);
            job.GetProperty("indexed").GetProperty("indexTasksTotal").GetInt64().Should().Be(1);
        }

        var report = $"{url}/{jobId}/report";
        var applied = new List<Guid>();
        string? cursor = null;
        do
        {
            using var page = await GetAsync(client, report + "?limit=3" + (cursor is null ? string.Empty : "&cursor=" + cursor), qc);
            page.StatusCode.Should().Be(HttpStatusCode.OK, await page.Content.ReadAsStringAsync(Ct));
            var json = await JsonAsync(page);
            json.GetProperty("total").GetProperty("relation").GetString().Should().Be("gte", "a partially skipped document is counted as skipped but listed as applied");
            applied.AddRange(json.GetProperty("items").EnumerateArray().Select(i =>
            {
                i.GetProperty("outcome").GetString().Should().Be("applied");
                return i.GetProperty("documentId").GetGuid();
            }));
            cursor = json.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        applied.Should().BeEquivalentTo(docs, "doc 1 kept its interactive Responsive value but still got the Issues choice (per-field Q-07 rule)");

        using (var skipped = await GetAsync(client, report + "?outcome=skippedChanged", qc))
        {
            var item = (await JsonAsync(skipped)).GetProperty("items").EnumerateArray().Single();
            item.GetProperty("documentId").GetGuid().Should().Be(docs[1]);
            item.GetProperty("outcome").GetString().Should().Be("skippedChanged");
            item.GetProperty("reasonCode").GetString().Should().Be("ConcurrentEdit");
            item.GetProperty("fieldIds").EnumerateArray().Select(f => f.GetInt32()).Should().Equal(w.Responsive);
        }

        using (var hidden = await GetAsync(client, report + "?outcome=skippedHidden", qc))
        {
            (await JsonAsync(hidden)).GetProperty("items").GetArrayLength().Should().Be(0);
        }

        using (var otherUser = await GetAsync(client, report, reviewer))
        {
            otherUser.StatusCode.Should().Be(HttpStatusCode.NotFound, "another user's job needs Job.ViewAll");
        }
    }

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
