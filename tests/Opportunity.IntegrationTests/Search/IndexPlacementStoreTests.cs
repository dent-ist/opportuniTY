using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Search.Indexing;
using Opportunity.Data.Search;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Search;

/// <summary>E07-T01: the PostgreSQL placement record (V0008, ADR-006 R2/R6), as the app role under RLS.</summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class IndexPlacementStoreTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Inserts_once_reads_back_and_updates_optimistically()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var store = new IndexPlacementStore(db.AppDataSource);
        var ws = await db.CreateWorkspaceAsync();
        var pool = await store.CreateSharedPoolAsync(generation: 1, primaryShards: 1, Ct);

        var (created, isNew) = await store.InsertAsync(Shared(ws, pool.PoolNumber), Ct);
        var (again, isNewAgain) = await store.InsertAsync(Shared(ws, pool.PoolNumber) with { EstimatedBytes = 99 }, Ct);

        isNew.Should().BeTrue();
        isNewAgain.Should().BeFalse();
        again.Should().BeEquivalentTo(created);
        created.RowVersion.Should().Be(1);
        (await store.GetAsync(ws, Ct)).Should().BeEquivalentTo(created);

        var moving = created with
        {
            State = IndexPlacementState.Moving,
            PendingKind = IndexPlacementKind.Dedicated,
            PendingGeneration = 1,
            PendingPrimaryShards = 2,
        };
        var updated = await store.UpdateAsync(moving, Ct);
        updated.Should().NotBeNull();
        updated!.RowVersion.Should().Be(2);
        updated.PendingPrimaryShards.Should().Be(2);

        (await store.UpdateAsync(moving, Ct)).Should().BeNull("the row version moved on");
    }

    [Fact]
    public async Task Placements_are_workspace_isolated_by_row_level_security()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var store = new IndexPlacementStore(db.AppDataSource);
        var ws = await db.CreateWorkspaceAsync();
        var other = await db.CreateWorkspaceAsync();
        await store.InsertAsync(Dedicated(ws), Ct);

        (await store.GetAsync(other, Ct)).Should().BeNull();
        await db.InWorkspaceAsync(other, async tx =>
        {
            await using var command = tx.Command("SELECT count(*) FROM opportunity.workspace_index_placement");
            ((long)(await command.ExecuteScalarAsync(Ct))!).Should().Be(0);
        });
    }

    [Fact]
    public async Task Pools_are_numbered_counted_and_closed_at_the_threshold()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var store = new IndexPlacementStore(db.AppDataSource);

        var first = await store.CreateSharedPoolAsync(1, 3, Ct);
        var second = await store.CreateSharedPoolAsync(2, 1, Ct);
        await store.AdjustSharedPoolAsync(first.PoolNumber, 1, 600, closeAtBytes: 1000, Ct);
        await store.AdjustSharedPoolAsync(first.PoolNumber, 1, 500, closeAtBytes: 1000, Ct);
        await store.AdjustSharedPoolAsync(first.PoolNumber, -1, -500, closeAtBytes: 1000, Ct);

        (first.PoolNumber, second.PoolNumber).Should().Be((1, 2));
        var pools = await store.ListSharedPoolsAsync(Ct);
        pools.Should().BeEquivalentTo(
        [
            new SharedIndexPool { PoolNumber = 1, Generation = 1, PrimaryShards = 3, Closed = true, WorkspaceCount = 1, AssignedBytes = 600 },
            new SharedIndexPool { PoolNumber = 2, Generation = 2, PrimaryShards = 1 },
        ], "a closed pool stays closed after workspaces leave");

        var missing = () => store.AdjustSharedPoolAsync(99, 1, 1, 1000, Ct);
        await missing.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task The_schema_rejects_inconsistent_placements()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var store = new IndexPlacementStore(db.AppDataSource);
        var ws = await db.CreateWorkspaceAsync();

        var sharedWithoutPool = () => store.InsertAsync(Dedicated(ws) with { Kind = IndexPlacementKind.Shared }, Ct);
        (await sharedWithoutPool.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("workspace_index_placement_kind_ck");

        var buildingWithoutTarget = () => store.InsertAsync(Dedicated(ws) with { State = IndexPlacementState.Building }, Ct);
        (await buildingWithoutTarget.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("workspace_index_placement_pending_ck");
    }

    private static WorkspaceIndexPlacement Shared(Guid ws, int pool) => new()
    {
        WorkspaceId = ws,
        Kind = IndexPlacementKind.Shared,
        SharedPool = pool,
        Generation = 1,
        EstimatedDocuments = 10,
        EstimatedBytes = 1234,
    };

    private static WorkspaceIndexPlacement Dedicated(Guid ws) => new()
    {
        WorkspaceId = ws,
        Kind = IndexPlacementKind.Dedicated,
        Generation = 1,
        DedicatedRequested = true,
    };
}
