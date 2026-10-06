using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Search;
using Opportunity.Contracts.Search;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>E09-T04: the grid's duplicate columns come with every hit (duplicate group and primary flag).</summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class DuplicateHitFieldTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hits_carry_the_duplicate_group_and_the_primary_flag()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        await h.Fields.InitializeWorkspaceAsync(ws, Ct);
        var user = await h.MemberAsync(ws);
        var group = Guid.CreateVersion7();
        var primary = await h.DocumentAsync(ws, "DUP-001", "quarterly forecast");
        var copy = await h.DocumentAsync(ws, "DUP-002", "quarterly forecast");
        var single = await h.DocumentAsync(ws, "DUP-003", "quarterly forecast");
        await ProjectAsync(h, ws, primary, "DUP-001", group, true);
        await ProjectAsync(h, ws, copy, "DUP-002", group, false);

        var outcome = await h.SearchAsync(ws, user, "forecast");
        outcome.Status.Should().Be(SearchStatus.Ok);
        var items = outcome.Page!.Items.ToDictionary(i => i.DocumentId);
        items[primary].Should().Match<SearchHit>(i => i.DuplicateGroupId == group.ToString("D") && i.IsDuplicatePrimary == true);
        items[copy].Should().Match<SearchHit>(i => i.DuplicateGroupId == group.ToString("D") && i.IsDuplicatePrimary == false);
        items[single].Should().Match<SearchHit>(i => i.DuplicateGroupId == null && i.IsDuplicatePrimary == null);

        // The grid's "Duplicate Primary" column (Columns dialog, a field column) asks for the system field by query name.
        var columns = await h.InScopeSearchAsync(ws, user, new SearchRequest(
            "forecast", [new SearchSortKey("controlNumber")], Fields: ["duplicate_primary", "duplicate_group"]));
        columns.Status.Should().Be(SearchStatus.Ok, string.Join("; ", columns.RequestErrors.SelectMany(e => e.Value)));
        var fields = columns.Page!.Items.ToDictionary(i => i.DocumentId, i => i.Fields!);
        fields[primary]["duplicate_primary"].Should().Equal("true");
        fields[copy]["duplicate_primary"].Should().Equal("false");
        fields[copy]["duplicate_group"].Should().Equal(group.ToString("D"));
        fields[single].Should().NotContainKey("duplicate_group");
    }

    private static Task ProjectAsync(SearchHarness h, Guid ws, Guid id, string controlNumber, Guid group, bool isPrimary) =>
        h.ProjectAsync(ws, new JsonObject
        {
            ["workspaceId"] = ws.ToString("D"),
            ["documentId"] = id.ToString("D"),
            ["controlNumber"] = controlNumber,
            ["fileName"] = controlNumber + ".msg",
            ["duplicateGroupId"] = group.ToString("D"),
            ["isDuplicatePrimary"] = isPrimary,
            ["securityTags"] = new JsonArray(),
            ["text"] = "quarterly forecast",
        }, id);
}
