using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Search;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E10-T03 / ADR-002 §8 against real OpenSearch: interactive point-in-time readers are kept alive by every page, and
/// when one expires (keep-alive lapsed and reaped by OpenSearch), reaches its maximum age or is closed by the per-user
/// cap, the next page re-establishes a reader from the cursor position and says "results refreshed" (Q-33).
/// The shared OpenSearch container reaps expired readers every second (<c>search.keep_alive_interval=1s</c>).
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class PointInTimeLifecycleTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pages_keep_the_reader_alive_and_a_reader_that_expired_mid_session_is_reestablished_with_results_refreshed()
    {
        var keepAlive = TimeSpan.FromSeconds(3);
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres, o => o.Search.PointInTimeKeepAlive = keepAlive);
        var ws = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws);
        for (var i = 1; i <= 6; i++)
        {
            await h.DocumentAsync(ws, $"P-{i:000}", "pricing apple");
        }

        var first = Ok(await h.SearchAsync(ws, user, "apple", pageSize: 2));
        var second = Ok(await h.PageAsync(ws, user, first.SearchId, new SearchPageRequest(first.NextCursor)));
        var pit = await ReaderAsync(h, ws, first.SearchId!);

        // Keep-alive management: each page renews the reader, so a session that keeps paging outlives the keep-alive.
        var renewedUntil = DateTimeOffset.UtcNow + (keepAlive * 2);
        var cursor = second.PreviousCursor;
        while (DateTimeOffset.UtcNow < renewedUntil)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
            var page = Ok(await h.PageAsync(ws, user, first.SearchId, new SearchPageRequest(cursor)));
            page.ResultsRefreshed.Should().BeFalse("every page extends the reader's keep-alive");
            cursor = page.NextCursor ?? page.PreviousCursor;
        }

        (await ReaderAsync(h, ws, first.SearchId!)).Should().Be(pit);
        (await OpenReadersAsync(h)).Should().Contain(pit);

        // Mid-session: a new document arrives and the reviewer steps away longer than the keep-alive.
        await h.DocumentAsync(ws, "P-007", "pricing apple");
        await WaitUntilAsync(async () => !(await OpenReadersAsync(h)).Contains(pit), TimeSpan.FromSeconds(60));

        h.Recorder.Clear();
        h.Audit.Clear();
        var refreshed = Ok(await h.PageAsync(ws, user, first.SearchId, new SearchPageRequest(second.NextCursor)));
        refreshed.ResultsRefreshed.Should().BeTrue();
        refreshed.Items.Select(i => i.ControlNumber).Should().Equal("P-005", "P-006");
        refreshed.Total.Should().Be(new TotalCount(7, TotalRelation.Eq), "the re-established reader sees the new document");
        refreshed.Page.IsLast.Should().BeFalse();
        h.Recorder.Requests.Count(r => r.PathAndQuery.Contains("/_search/point_in_time", StringComparison.Ordinal)).Should().Be(1);
        h.Audit.Events.Should().ContainSingle(e => e.Action == "ResultsPageServed").Which.Details!["readerReestablished"].Should().Be("expired");

        // The cursor now lives on the new reader: paging on is not refreshed again, and reaches the new document.
        var newPit = await ReaderAsync(h, ws, first.SearchId!);
        newPit.Should().NotBe(pit).And.NotBeEmpty();
        var last = Ok(await h.PageAsync(ws, user, first.SearchId, new SearchPageRequest(refreshed.NextCursor)));
        last.ResultsRefreshed.Should().BeFalse();
        last.Items.Select(i => i.ControlNumber).Should().Equal("P-007");
        (await ReaderAsync(h, ws, first.SearchId!)).Should().Be(newPit);
    }

    [Fact]
    public async Task A_reader_past_its_maximum_age_is_closed_and_reestablished()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres, o => o.Search.PointInTimeMaxAge = TimeSpan.FromSeconds(2));
        var ws = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws);
        for (var i = 1; i <= 4; i++)
        {
            await h.DocumentAsync(ws, $"M-{i:000}", "merger memo");
        }

        var first = Ok(await h.SearchAsync(ws, user, "merger", pageSize: 2));
        var pit = await ReaderAsync(h, ws, first.SearchId!);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);

        h.Recorder.Clear();
        h.Audit.Clear();
        var aged = Ok(await h.PageAsync(ws, user, first.SearchId, new SearchPageRequest(first.NextCursor)));
        aged.ResultsRefreshed.Should().BeTrue();
        aged.Items.Select(i => i.ControlNumber).Should().Equal("M-003", "M-004");
        h.Recorder.Requests.Should().Contain(r => r.Method == HttpMethod.Delete && r.PathAndQuery == "/_search/point_in_time"
            && r.Body!.Contains(pit, StringComparison.Ordinal), "the aged reader is closed, not left to pin segments");
        h.Audit.Events.Should().ContainSingle(e => e.Action == "ResultsPageServed").Which.Details!["readerReestablished"].Should().Be("maxAge");
        (await ReaderAsync(h, ws, first.SearchId!)).Should().NotBe(pit);

        var again = Ok(await h.PageAsync(ws, user, first.SearchId, new SearchPageRequest(aged.PreviousCursor)));
        again.ResultsRefreshed.Should().BeFalse();
        again.Items.Select(i => i.ControlNumber).Should().Equal("M-001", "M-002");
    }

    [Fact]
    public async Task Each_user_keeps_at_most_the_configured_number_of_open_readers()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres, o => o.Search.MaxOpenPointInTimesPerUser = 2);
        var ws = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws);
        var other = await h.MemberAsync(ws);
        for (var i = 1; i <= 4; i++)
        {
            await h.DocumentAsync(ws, $"C-{i:000}", "capped search");
        }

        var theirs = Ok(await h.SearchAsync(ws, other, "capped", pageSize: 2));
        var oldest = Ok(await h.SearchAsync(ws, user, "capped", pageSize: 2));
        var oldestPit = await ReaderAsync(h, ws, oldest.SearchId!);
        await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        var middle = Ok(await h.SearchAsync(ws, user, "capped", pageSize: 2));
        (await ReaderAsync(h, ws, oldest.SearchId!)).Should().Be(oldestPit, "two readers are within the cap");

        h.Recorder.Clear();
        await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        var newest = Ok(await h.SearchAsync(ws, user, "capped", pageSize: 2));
        (await ReaderAsync(h, ws, oldest.SearchId!)).Should().BeEmpty("the oldest reader beyond the cap is detached");
        h.Recorder.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete && r.PathAndQuery == "/_search/point_in_time")
            .Which.Body.Should().Contain(oldestPit);
        (await OpenReadersAsync(h)).Should().NotContain(oldestPit);

        // The detached search still works: its next page re-establishes a reader and says so.
        var reopened = Ok(await h.PageAsync(ws, user, oldest.SearchId, new SearchPageRequest(oldest.NextCursor)));
        reopened.ResultsRefreshed.Should().BeTrue();
        reopened.Items.Select(i => i.ControlNumber).Should().Equal("C-003", "C-004");
        Ok(await h.PageAsync(ws, user, middle.SearchId, new SearchPageRequest(middle.NextCursor))).ResultsRefreshed.Should().BeFalse();
        Ok(await h.PageAsync(ws, user, newest.SearchId, new SearchPageRequest(newest.NextCursor))).ResultsRefreshed.Should().BeFalse();
        Ok(await h.PageAsync(ws, other, theirs.SearchId, new SearchPageRequest(theirs.NextCursor))).ResultsRefreshed
            .Should().BeFalse("the cap is per user");
    }

    /// <summary>The reader stored for a search (empty when detached), read as the admin login.</summary>
    private static async Task<string> ReaderAsync(SearchHarness h, Guid ws, string searchId) =>
        (await h.Db.Core.ColumnAsync(
            $"SELECT coalesce(pit_id, '') FROM opportunity.search_session WHERE workspace_id = '{ws}' AND search_id = '{Guid.ParseExact(searchId, "N")}'"))
        .Single();

    /// <summary>The IDs of every point-in-time reader open on the cluster.</summary>
    private static async Task<IReadOnlyList<string>> OpenReadersAsync(SearchHarness h)
    {
        using var response = await h.OpenSearchHttp.GetAsync("_search/point_in_time/_all", Ct);
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. (body?["pits"] as JsonArray ?? []).Select(p => p?["pit_id"]?.GetValue<string>()).OfType<string>()];
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > until)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }
    }

    private static SearchResultPage Ok(SearchOutcome outcome)
    {
        outcome.Status.Should().Be(SearchStatus.Ok, string.Join("; ", outcome.QueryErrors.Select(e => e.Code + " " + e.Message)
            .Concat(outcome.RequestErrors.SelectMany(e => e.Value))));
        return outcome.Page!;
    }
}
