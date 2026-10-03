using System.Diagnostics;
using System.Net;
using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Application.Search.Indexing;
using Opportunity.IntegrationTests.Containers;
using Opportunity.Search.Indexing;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>E07-T01: index management against a real OpenSearch, isolated by the fixture's per-test index prefix.</summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class IndexManagerTests(OpenSearchFixture fixture)
{
    private const long GiB = 1024L * 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<IndexPlacementKind> Kinds => new() { IndexPlacementKind.Shared, IndexPlacementKind.Dedicated };

    /// <summary>The same search suite passes for shared and dedicated placements (acceptance criterion 2).</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Search_suite_isolates_workspaces_in_both_placements(IndexPlacementKind kind)
    {
        await using var harness = await IndexHarness.CreateAsync(fixture);
        var request = new WorkspacePlacementRequest(DedicatedIndex: kind == IndexPlacementKind.Dedicated);
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        var info = await harness.Placements.PlaceAsync(a, request, Ct);
        await harness.Placements.PlaceAsync(b, request, Ct);
        info.Should().Be(new WorkspaceSearchPlacementInfo(a, kind, 1, IndexPlacementState.Active));

        var pa = await harness.Manager.ResolveAsync(a, IndexPurpose.Write, Ct);
        var pb = await harness.Manager.ResolveAsync(b, IndexPurpose.Write, Ct);
        await harness.IndexAsync(pa, "a-1", "privileged memo about the merger");
        await harness.IndexAsync(pa, "a-2", "lunch order");
        await harness.IndexAsync(pb, "b-1", "privileged memo about the lawsuit");

        (await harness.SearchAsync(a, "privileged")).Should().Equal("a-1");
        (await harness.SearchAsync(b, "privileged")).Should().Equal("b-1");
        (await harness.SearchAsync(a, "MEMO")).Should().Equal("a-1");
        (await harness.SearchAsync(b, "lunch")).Should().BeEmpty();

        if (kind == IndexPlacementKind.Shared)
        {
            pa.Read.Index.Should().Be(pb.Read.Index, "small workspaces share one pool index");
            pa.Read.Routing.Should().Be(a.ToString("D"));
            pa.WriteTargets.Should().ContainSingle().Which.Routing.Should().Be(a.ToString("D"));
        }
        else
        {
            pa.Read.Index.Should().NotBe(pb.Read.Index);
            pa.Read.Routing.Should().BeNull();
        }
    }

    [Fact]
    public async Task Placement_is_idempotent_and_reads_of_an_unplaced_workspace_fail()
    {
        await using var harness = await IndexHarness.CreateAsync(fixture);
        var ws = Guid.CreateVersion7();

        var read = () => harness.Manager.ResolveAsync(ws, IndexPurpose.Read, Ct);
        await read.Should().ThrowAsync<WorkspaceNotPlacedException>();
        (await harness.Placements.GetAsync(ws, Ct)).Should().BeNull();

        var first = await harness.Manager.PlaceAsync(ws, WorkspacePlacementRequest.Default, Ct);
        var again = await harness.Manager.PlaceAsync(ws, new WorkspacePlacementRequest(DedicatedIndex: true), Ct);
        again.Should().BeEquivalentTo(first, "an existing placement is never changed by PlaceAsync");
        (await harness.Store.ListSharedPoolsAsync(Ct)).Should().ContainSingle().Which.WorkspaceCount.Should().Be(1);
    }

    [Fact]
    public async Task Writes_place_an_unplaced_workspace_with_default_sizing()
    {
        await using var harness = await IndexHarness.CreateAsync(fixture);
        var ws = Guid.CreateVersion7();

        var placement = await harness.Manager.ResolveAsync(ws, IndexPurpose.Write, Ct);

        placement.Kind.Should().Be(IndexPlacementKind.Shared);
        (await harness.IndexExistsAsync(placement.WriteTargets[0].Index)).Should().BeTrue();
    }

    [Fact]
    public async Task Indexes_come_from_the_strict_template_and_shared_indexes_require_routing()
    {
        await using var harness = await IndexHarness.CreateAsync(fixture);
        var ws = Guid.CreateVersion7();
        var placement = await harness.Manager.PlaceAsync(ws, WorkspacePlacementRequest.Default, Ct);
        var index = placement.WriteTargets[0].Index;
        var routing = placement.WriteTargets[0].Routing;

        using (var unknown = await harness.RawIndexAsync(index, "x", new { workspaceId = ws.ToString("D"), bogus = 1 }, routing))
        {
            unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await unknown.Content.ReadAsStringAsync(Ct)).Should().Contain("strict_dynamic_mapping_exception");
        }

        using (var unrouted = await harness.RawIndexAsync(index, "y", new { workspaceId = ws.ToString("D") }, routing: null))
        {
            unrouted.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await unrouted.Content.ReadAsStringAsync(Ct)).Should().Contain("routing_missing_exception");
        }

        var settings = await harness.GetJsonAsync($"{index}/_settings");
        var indexSettings = settings.GetProperty(index).GetProperty("settings").GetProperty("index");
        indexSettings.GetProperty("number_of_replicas").GetString().Should().Be("0");
        indexSettings.GetProperty("max_result_window").GetString().Should().Be("10000");
        indexSettings.GetProperty("mapping").GetProperty("total_fields").GetProperty("limit").GetString().Should().Be("2000");
        indexSettings.GetProperty("analysis").GetProperty("analyzer").TryGetProperty("opp_text", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Without_installed_templates_no_index_is_created()
    {
        await using var harness = await IndexHarness.CreateAsync(fixture, installTemplates: false);

        var place = () => harness.Manager.PlaceAsync(Guid.CreateVersion7(), new WorkspacePlacementRequest(DedicatedIndex: true), Ct);

        await place.Should().ThrowAsync<InvalidOperationException>().WithMessage("*run the migrator*");
        (await harness.GetJsonAsync($"_cat/indices/{harness.Scope.Prefix}*?format=json&expand_wildcards=all"))
            .GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Placement_thresholds_come_from_configuration_and_dedicated_indexes_get_their_shard_count()
    {
        await using var harness = await IndexHarness.CreateAsync(fixture, o =>
        {
            o.Placement.DedicatedDocuments = 1000;
            o.Placement.DedicatedBytes = 10 * GiB;
            o.Placement.MultiShardBytes = 10 * GiB;
            o.Placement.TargetShardBytes = 10 * GiB;
            o.Placement.SizeCalibrationFactor = 1.0;
        });

        var small = await harness.Manager.PlaceAsync(Guid.CreateVersion7(), new WorkspacePlacementRequest(ExpectedDocuments: 999), Ct);
        var many = await harness.Manager.PlaceAsync(Guid.CreateVersion7(), new WorkspacePlacementRequest(ExpectedDocuments: 1000), Ct);
        var large = await harness.Manager.PlaceAsync(Guid.CreateVersion7(), new WorkspacePlacementRequest(ExpectedIndexedBytes: 20 * GiB), Ct);

        small.Kind.Should().Be(IndexPlacementKind.Shared);
        many.Kind.Should().Be(IndexPlacementKind.Dedicated);
        large.Kind.Should().Be(IndexPlacementKind.Dedicated);

        var index = large.WriteTargets[0].Index;
        var settings = await harness.GetJsonAsync($"{index}/_settings");
        settings.GetProperty(index).GetProperty("settings").GetProperty("index").GetProperty("number_of_shards").GetString()
            .Should().Be("3", "ceil(1.5 x 20 GB / 10 GB) primaries");
    }

    [Fact]
    public async Task A_full_shared_index_closes_and_the_next_workspace_opens_a_new_pool()
    {
        await using var harness = await IndexHarness.CreateAsync(fixture, o =>
        {
            o.Placement.TargetShardBytes = 10 * GiB;
            o.Placement.SharedIndexCloseAtFraction = 0.1;
            o.Placement.SizeCalibrationFactor = 1.0;
        });

        var first = await harness.Manager.PlaceAsync(Guid.CreateVersion7(), new WorkspacePlacementRequest(ExpectedIndexedBytes: 2 * GiB), Ct);
        var second = await harness.Manager.PlaceAsync(Guid.CreateVersion7(), WorkspacePlacementRequest.Default, Ct);

        first.Read.Index.Should().NotBe(second.Read.Index);
        var pools = await harness.Store.ListSharedPoolsAsync(Ct);
        pools.Select(p => (p.PoolNumber, p.Closed, p.WorkspaceCount)).Should().Equal((1, true, 1), (2, false, 1));
    }

    [Fact]
    public async Task Max_shared_indexes_bounds_the_pool()
    {
        await using var harness = await IndexHarness.CreateAsync(fixture, o =>
        {
            o.Placement.TargetShardBytes = 10 * GiB;
            o.Placement.SharedIndexCloseAtFraction = 0.1;
            o.Placement.MaxSharedIndexes = 1;
        });
        await harness.Manager.PlaceAsync(Guid.CreateVersion7(), new WorkspacePlacementRequest(ExpectedIndexedBytes: 2 * GiB), Ct);

        var place = () => harness.Manager.PlaceAsync(Guid.CreateVersion7(), WorkspacePlacementRequest.Default, Ct);

        await place.Should().ThrowAsync<InvalidOperationException>().WithMessage("*MaxSharedIndexes*");
    }

    [Fact]
    public async Task Moving_a_shared_workspace_to_a_dedicated_index_dual_writes_then_switches_and_cleans_up()
    {
        await using var harness = await IndexHarness.CreateAsync(fixture);
        var ws = Guid.CreateVersion7();
        var neighbour = Guid.CreateVersion7();
        var shared = await harness.Manager.PlaceAsync(ws, WorkspacePlacementRequest.Default, Ct);
        var other = await harness.Manager.PlaceAsync(neighbour, WorkspacePlacementRequest.Default, Ct);
        await harness.IndexAsync(shared, "d-1", "privileged before the move");
        await harness.IndexAsync(other, "n-1", "privileged neighbour");

        var moving = await harness.Manager.BeginRebuildAsync(ws, new IndexRebuildRequest(Kind: IndexPlacementKind.Dedicated), Ct);

        moving.State.Should().Be(IndexPlacementState.Moving);
        moving.WriteTargets.Should().HaveCount(2);
        moving.Read.Should().Be(shared.Read, "reads stay on the current placement until the switch");
        var target = moving.WriteTargets[1];
        target.Routing.Should().BeNull();
        var settings = await harness.GetJsonAsync($"{target.Index}/_settings");
        settings.GetProperty(target.Index).GetProperty("settings").GetProperty("index").GetProperty("refresh_interval").GetString()
            .Should().Be("-1", "the target is built without refresh");

        // Backfill stand-in (E07-T11) plus a live write during the move: both land on both targets.
        await harness.IndexAsync(moving, "d-1", "privileged before the move");
        await harness.IndexAsync(moving, "d-2", "privileged during the move");

        var done = await harness.Manager.CompleteRebuildAsync(ws, Ct);

        done.Kind.Should().Be(IndexPlacementKind.Dedicated);
        done.State.Should().Be(IndexPlacementState.Active);
        done.WriteTargets.Should().Equal(target);
        (await harness.AliasTargetsAsync(done.Read.Index)).Should().Equal(target.Index);
        (await harness.SearchAsync(ws, "privileged")).Should().Equal("d-1", "d-2");
        (await harness.SearchAsync(neighbour, "privileged")).Should().Equal("n-1");
        (await harness.Manager.CompleteRebuildAsync(ws, Ct)).Should().BeEquivalentTo(done, "completion is retry-safe");

        // R13 step 7: the workspace's copy leaves the shared index; the neighbour stays.
        await WaitUntilAsync(async () => await harness.CountAsync(shared.WriteTargets[0].Index, ws) == 0);
        (await harness.CountAsync(shared.WriteTargets[0].Index, neighbour)).Should().Be(1);
        (await harness.Store.ListSharedPoolsAsync(Ct)).Single().WorkspaceCount.Should().Be(1);
    }

    [Fact]
    public async Task Aborting_a_move_drops_the_target_and_keeps_serving_the_current_placement()
    {
        await using var harness = await IndexHarness.CreateAsync(fixture);
        var ws = Guid.CreateVersion7();
        var shared = await harness.Manager.PlaceAsync(ws, WorkspacePlacementRequest.Default, Ct);
        await harness.IndexAsync(shared, "d-1", "privileged");
        var moving = await harness.Manager.BeginRebuildAsync(ws, new IndexRebuildRequest(Kind: IndexPlacementKind.Dedicated), Ct);

        var again = () => harness.Manager.BeginRebuildAsync(ws, new IndexRebuildRequest(Kind: IndexPlacementKind.Dedicated), Ct);
        await again.Should().ThrowAsync<IndexPlacementConflictException>();

        var aborted = await harness.Manager.AbortRebuildAsync(ws, Ct);

        aborted.Should().BeEquivalentTo(shared);
        (await harness.IndexExistsAsync(moving.WriteTargets[1].Index)).Should().BeFalse();
        (await harness.SearchAsync(ws, "privileged")).Should().Equal("d-1");
    }

    [Fact]
    public async Task Dedicated_placements_are_never_demoted()
    {
        await using var harness = await IndexHarness.CreateAsync(fixture);
        var ws = Guid.CreateVersion7();
        await harness.Manager.PlaceAsync(ws, new WorkspacePlacementRequest(DedicatedIndex: true), Ct);

        var demote = () => harness.Manager.BeginRebuildAsync(ws, new IndexRebuildRequest(Kind: IndexPlacementKind.Shared), Ct);

        await demote.Should().ThrowAsync<InvalidOperationException>().WithMessage("*never demoted*");
    }

    [Fact]
    public async Task Rolling_a_dedicated_index_to_a_new_generation_swaps_the_alias_atomically()
    {
        var mappingJson = JsonSerializer.Serialize(ProjectionMappings.Embedded.Load(1));
        await using var v1 = await IndexHarness.CreateAsync(
            fixture, mappings: ProjectionMappings.FromJson(new Dictionary<int, string> { [1] = mappingJson }));
        var ws = Guid.CreateVersion7();
        var g1 = await v1.Manager.PlaceAsync(ws, new WorkspacePlacementRequest(DedicatedIndex: true), Ct);
        await v1.IndexAsync(g1, "d-1", "privileged");

        // A later build ships projection.v2; the migrator installs its template and the reindex job rolls the workspace.
        await using var v2 = await IndexHarness.CreateAsync(
            fixture, mappings: ProjectionMappings.FromJson(new Dictionary<int, string> { [1] = mappingJson, [2] = mappingJson }), sharing: v1);
        var building = await v2.Manager.BeginRebuildAsync(ws, new IndexRebuildRequest(), Ct);

        building.State.Should().Be(IndexPlacementState.Building);
        building.WriteTargets.Select(t => t.Index).Should().Equal(g1.WriteTargets[0].Index, g1.Read.Index + "-g2");
        await v2.IndexAsync(building, "d-1", "privileged");

        var rolled = await v2.Manager.CompleteRebuildAsync(ws, Ct);

        rolled.Generation.Should().Be(2);
        (await v2.AliasTargetsAsync(rolled.Read.Index)).Should().Equal(g1.Read.Index + "-g2");
        (await v2.SearchAsync(ws, "privileged")).Should().Equal("d-1");

        // The previous generation is kept read-only for rollback.
        using var blocked = await v2.RawIndexAsync(g1.WriteTargets[0].Index, "late", new { workspaceId = ws.ToString("D") }, routing: null);
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await v2.CountAsync(g1.WriteTargets[0].Index, ws)).Should().Be(1);
    }

    [Fact]
    public async Task Template_bootstrap_is_idempotent_and_records_the_generation()
    {
        await using var harness = await IndexHarness.CreateAsync(fixture, installTemplates: false);

        await harness.TemplateStep.RunAsync(Ct);
        await harness.TemplateStep.RunAsync(Ct);

        var template = (await harness.GetJsonAsync($"_index_template/{harness.Scope.Prefix}-projection-g1"))
            .GetProperty("index_templates")[0].GetProperty("index_template");
        template.GetProperty("version").GetInt32().Should().Be(1);
        template.GetProperty("index_patterns").EnumerateArray().Select(p => p.GetString())
            .Should().Equal($"{harness.Scope.Prefix}-shared-*-g1", $"{harness.Scope.Prefix}-ws-*-g1");
        template.GetProperty("_meta").GetProperty("mapping_sha256").GetString().Should().Be(harness.Mappings.Checksum(1));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!await condition())
        {
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the asynchronous clean-up should finish");
            await Task.Delay(200, Ct);
        }
    }
}
