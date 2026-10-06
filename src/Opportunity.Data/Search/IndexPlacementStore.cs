using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Search.Indexing;

namespace Opportunity.Data.Search;

/// <summary>
/// PostgreSQL implementation of <see cref="IIndexPlacementStore"/> over <c>workspace_index_placement</c> (tenant, RLS)
/// and <c>search_shared_index</c> (installation-level) from V0009.
/// </summary>
public sealed class IndexPlacementStore(NpgsqlDataSource dataSource) : IIndexPlacementStore
{
    private const string PlacementColumns =
        """
        workspace_id, kind, shared_pool, generation, primary_shards, state, pending_kind, pending_shared_pool,
        pending_generation, pending_primary_shards, dedicated_requested, estimated_documents, estimated_bytes,
        row_version, updated_at, revision, pending_revision, last_revision
        """;

    private const string PoolColumns = "pool_number, generation, primary_shards, closed, workspace_count, assigned_bytes";

    public async Task<WorkspaceIndexPlacement?> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"SELECT {PlacementColumns} FROM opportunity.workspace_index_placement WHERE workspace_id = @ws");
        command.Parameters.AddWithValue("ws", workspaceId);
        var placement = await ReadPlacementAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return placement;
    }

    public async Task<(WorkspaceIndexPlacement Placement, bool Created)> InsertAsync(
        WorkspaceIndexPlacement placement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, placement.WorkspaceId, cancellationToken).ConfigureAwait(false);
        WorkspaceIndexPlacement? stored;
        await using (var insert = tx.Command(
            $"""
            INSERT INTO opportunity.workspace_index_placement
                (workspace_id, kind, shared_pool, generation, primary_shards, state, pending_kind, pending_shared_pool,
                 pending_generation, pending_primary_shards, dedicated_requested, estimated_documents, estimated_bytes,
                 revision, pending_revision, last_revision)
            VALUES (@ws, @kind, @pool, @gen, @shards, @state, @pkind, @ppool, @pgen, @pshards, @dedicated, @docs, @bytes,
                    @revision, @prevision, @last_revision)
            ON CONFLICT (workspace_id) DO NOTHING
            RETURNING {PlacementColumns}
            """))
        {
            Bind(insert, placement);
            stored = await ReadPlacementAsync(insert, cancellationToken).ConfigureAwait(false);
        }

        var created = stored is not null;
        if (stored is null)
        {
            await using var select = tx.Command(
                $"SELECT {PlacementColumns} FROM opportunity.workspace_index_placement WHERE workspace_id = @ws");
            select.Parameters.AddWithValue("ws", placement.WorkspaceId);
            stored = await ReadPlacementAsync(select, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The placement vanished during insert.");
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (stored, created);
    }

    public async Task<WorkspaceIndexPlacement?> UpdateAsync(WorkspaceIndexPlacement placement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, placement.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.workspace_index_placement
               SET kind = @kind, shared_pool = @pool, generation = @gen, primary_shards = @shards, state = @state,
                   pending_kind = @pkind, pending_shared_pool = @ppool, pending_generation = @pgen,
                   pending_primary_shards = @pshards, dedicated_requested = @dedicated, estimated_documents = @docs,
                   estimated_bytes = @bytes, revision = @revision, pending_revision = @prevision, last_revision = @last_revision,
                   row_version = row_version + 1, updated_at = now()
             WHERE workspace_id = @ws AND row_version = @version
            RETURNING {PlacementColumns}
            """);
        Bind(command, placement);
        command.Parameters.AddWithValue("version", placement.RowVersion);
        var stored = await ReadPlacementAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task<IReadOnlyList<SharedIndexPool>> ListSharedPoolsAsync(CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command($"SELECT {PoolColumns} FROM opportunity.search_shared_index ORDER BY pool_number");
        var pools = new List<SharedIndexPool>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pools.Add(ReadPool(reader));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return pools;
    }

    public async Task<SharedIndexPool> CreateSharedPoolAsync(int generation, int primaryShards, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);

        // Serialises pool numbering; concurrent creators queue for the few milliseconds of this transaction.
        await using (var lockTable = tx.Command("LOCK TABLE opportunity.search_shared_index IN SHARE ROW EXCLUSIVE MODE"))
        {
            await lockTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        SharedIndexPool pool;
        await using (var command = tx.Command(
            $"""
            INSERT INTO opportunity.search_shared_index (pool_number, generation, primary_shards)
            SELECT COALESCE(max(pool_number), 0) + 1, @gen, @shards FROM opportunity.search_shared_index
            RETURNING {PoolColumns}
            """))
        {
            command.Parameters.AddWithValue("gen", generation);
            command.Parameters.AddWithValue("shards", primaryShards);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            pool = ReadPool(reader);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return pool;
    }

    public async Task AdjustSharedPoolAsync(
        int poolNumber, int workspaceDelta, long bytesDelta, long closeAtBytes, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            UPDATE opportunity.search_shared_index
               SET workspace_count = greatest(workspace_count + @count, 0),
                   assigned_bytes = greatest(assigned_bytes + @bytes, 0),
                   closed = closed OR greatest(assigned_bytes + @bytes, 0) >= @closeAt,
                   updated_at = now()
             WHERE pool_number = @pool
            """))
        {
            command.Parameters.AddWithValue("pool", poolNumber);
            command.Parameters.AddWithValue("count", workspaceDelta);
            command.Parameters.AddWithValue("bytes", bytesDelta);
            command.Parameters.AddWithValue("closeAt", closeAtBytes);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException($"Shared index pool {poolNumber} does not exist.");
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(NpgsqlCommand command, WorkspaceIndexPlacement p)
    {
        command.Parameters.AddWithValue("ws", p.WorkspaceId);
        command.Parameters.AddWithValue("kind", (short)p.Kind);
        command.Parameters.AddWithValue("pool", (object?)p.SharedPool ?? DBNull.Value);
        command.Parameters.AddWithValue("gen", p.Generation);
        command.Parameters.AddWithValue("shards", p.PrimaryShards);
        command.Parameters.AddWithValue("state", (short)p.State);
        command.Parameters.AddWithValue("pkind", p.PendingKind is { } k ? (short)k : DBNull.Value);
        command.Parameters.AddWithValue("ppool", (object?)p.PendingSharedPool ?? DBNull.Value);
        command.Parameters.AddWithValue("pgen", (object?)p.PendingGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue("pshards", (object?)p.PendingPrimaryShards ?? DBNull.Value);
        command.Parameters.AddWithValue("dedicated", p.DedicatedRequested);
        command.Parameters.AddWithValue("docs", p.EstimatedDocuments);
        command.Parameters.AddWithValue("bytes", p.EstimatedBytes);
        command.Parameters.AddWithValue("revision", p.Revision);
        command.Parameters.AddWithValue("prevision", (object?)p.PendingRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("last_revision", Math.Max(p.LastRevision, Math.Max(p.Revision, p.PendingRevision ?? 0)));
    }

    private static async Task<WorkspaceIndexPlacement?> ReadPlacementAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new WorkspaceIndexPlacement
        {
            WorkspaceId = reader.GetGuid(0),
            Kind = (IndexPlacementKind)reader.GetInt16(1),
            SharedPool = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            Generation = reader.GetInt32(3),
            PrimaryShards = reader.GetInt32(4),
            State = (IndexPlacementState)reader.GetInt16(5),
            PendingKind = reader.IsDBNull(6) ? null : (IndexPlacementKind)reader.GetInt16(6),
            PendingSharedPool = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            PendingGeneration = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            PendingPrimaryShards = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            DedicatedRequested = reader.GetBoolean(10),
            EstimatedDocuments = reader.GetInt64(11),
            EstimatedBytes = reader.GetInt64(12),
            RowVersion = reader.GetInt64(13),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(14),
            Revision = reader.GetInt32(15),
            PendingRevision = reader.IsDBNull(16) ? null : reader.GetInt32(16),
            LastRevision = reader.GetInt32(17),
        };
    }

    private static SharedIndexPool ReadPool(NpgsqlDataReader reader) => new()
    {
        PoolNumber = reader.GetInt32(0),
        Generation = reader.GetInt32(1),
        PrimaryShards = reader.GetInt32(2),
        Closed = reader.GetBoolean(3),
        WorkspaceCount = reader.GetInt32(4),
        AssignedBytes = reader.GetInt64(5),
    };
}

public static class IndexPlacementStoreRegistration
{
    /// <summary>Registers the PostgreSQL <see cref="IIndexPlacementStore"/> (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresIndexPlacementStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IIndexPlacementStore, IndexPlacementStore>();
        return services;
    }
}
