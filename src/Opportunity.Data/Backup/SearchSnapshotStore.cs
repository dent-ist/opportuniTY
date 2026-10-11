using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Backup;
using Opportunity.Core.Workspaces;

namespace Opportunity.Data.Backup;

/// <summary>
/// PostgreSQL implementation of <see cref="ISearchSnapshotStore"/> (V0070). The snapshot row is installation-level;
/// each anchor is written in its workspace's own transaction (RLS), all of them committed before
/// <see cref="BeginAsync"/> returns.
/// </summary>
public sealed class SearchSnapshotStore(NpgsqlDataSource dataSource) : ISearchSnapshotStore
{
    private const string Columns = "snapshot_id, snapshot_name, repository, requested_at, state, started_at, ended_at, workspaces";

    public async Task<SearchSnapshotRecord> BeginAsync(string name, string repository, CancellationToken cancellationToken = default)
    {
        if (!SearchSnapshotNames.IsValid(name) || !SearchSnapshotNames.IsValidRepository(repository))
        {
            throw new ArgumentException("Snapshot and repository names must be lowercase [a-z0-9._-].");
        }

        var snapshotId = Guid.CreateVersion7();
        DateTimeOffset requestedAt;
        var workspaces = new List<Guid>();
        await using (var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false))
        {
            await using (var insert = tx.Command(
                """
                INSERT INTO opportunity.search_snapshot (snapshot_id, snapshot_name, repository) VALUES (@id, @name, @repository)
                RETURNING requested_at
                """))
            {
                insert.Parameters.AddWithValue("id", snapshotId);
                insert.Parameters.AddWithValue("name", name);
                insert.Parameters.AddWithValue("repository", repository);
                requestedAt = new DateTimeOffset(DateTime.SpecifyKind(
                    (DateTime)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!, DateTimeKind.Utc));
            }

            await using (var list = tx.Command(
                $"SELECT workspace_id FROM opportunity.workspace WHERE status <> '{nameof(WorkspaceStatus.Purged)}' ORDER BY workspace_id"))
            await using (var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    workspaces.Add(reader.GetGuid(0));
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // The watermark only rises, so an anchor read here is at most what the index holds when the snapshot starts.
        foreach (var ws in workspaces)
        {
            await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, ws, cancellationToken).ConfigureAwait(false);
            await using var command = tx.Command(
                """
                INSERT INTO opportunity.search_snapshot_anchor
                    (workspace_id, snapshot_id, indexed_through_generation, search_generation, placement_kind, placement_state, placement_generation)
                SELECT @ws, @id,
                       coalesce((SELECT indexed_through_generation FROM opportunity.workspace_search_watermark WHERE workspace_id = @ws), 0),
                       (SELECT value FROM opportunity.workspace_search_generation WHERE workspace_id = @ws),
                       p.kind, p.state, p.generation
                FROM (SELECT 1) AS one
                LEFT JOIN opportunity.workspace_index_placement p ON p.workspace_id = @ws
                """);
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("id", snapshotId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false))
        {
            await using var update = tx.Command("UPDATE opportunity.search_snapshot SET workspaces = @n, updated_at = now() WHERE snapshot_id = @id");
            update.Parameters.AddWithValue("id", snapshotId);
            update.Parameters.AddWithValue("n", workspaces.Count);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SearchSnapshotRecord(snapshotId, name, repository, requestedAt, SearchSnapshotState.Requested, null, null, workspaces.Count);
    }

    public async Task CompleteAsync(Guid snapshotId, SearchSnapshotInfo outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            UPDATE opportunity.search_snapshot
            SET state = @state, started_at = @started, ended_at = @ended, indices = @indices, error = @error, updated_at = now()
            WHERE snapshot_id = @id
            """);
        command.Parameters.AddWithValue("id", snapshotId);
        command.Parameters.AddWithValue("state", outcome.State == SearchSnapshotState.InProgress ? nameof(SearchSnapshotState.Requested) : outcome.State.ToString());
        command.Parameters.Add(new NpgsqlParameter("started", NpgsqlDbType.TimestampTz) { Value = (object?)outcome.StartTime ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("ended", NpgsqlDbType.TimestampTz) { Value = (object?)outcome.EndTime ?? DBNull.Value });
        command.Parameters.AddWithValue("indices", outcome.Indices.Count);
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text)
        {
            Value = outcome.Failure is { } f ? f.Length <= 2000 ? f : f[..2000] : DBNull.Value,
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchSnapshotRecord?> FindAsync(string repository, string name, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.search_snapshot WHERE repository = @repository AND snapshot_name = @name");
        command.Parameters.AddWithValue("repository", repository);
        command.Parameters.AddWithValue("name", name);
        SearchSnapshotRecord? record = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                record = Read(reader);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<SearchSnapshotRecord>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.search_snapshot ORDER BY requested_at DESC LIMIT @limit");
        command.Parameters.AddWithValue("limit", limit);
        var records = new List<SearchSnapshotRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(Read(reader));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return records;
    }

    public async Task<IReadOnlyDictionary<Guid, SearchSnapshotAnchor>> GetAnchorsAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var workspaces = new List<Guid>();
        await using (var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false))
        {
            await using (var list = tx.Command("SELECT workspace_id FROM opportunity.workspace ORDER BY workspace_id"))
            await using (var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    workspaces.Add(reader.GetGuid(0));
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var anchors = new Dictionary<Guid, SearchSnapshotAnchor>();
        foreach (var ws in workspaces)
        {
            await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, ws, cancellationToken).ConfigureAwait(false);
            await using var command = tx.Command(
                """
                SELECT indexed_through_generation, search_generation, placement_kind, placement_state, placement_generation
                FROM opportunity.search_snapshot_anchor WHERE workspace_id = @ws AND snapshot_id = @id
                """);
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("id", snapshotId);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    anchors[ws] = new SearchSnapshotAnchor(
                        ws,
                        reader.GetInt64(0),
                        reader.IsDBNull(1) ? null : reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetInt16(2),
                        reader.IsDBNull(3) ? null : reader.GetInt16(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4));
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return anchors;
    }

    public async Task<bool> DeleteAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        // The anchors go with the row (ON DELETE CASCADE; referential actions are not subject to row-level security).
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("DELETE FROM opportunity.search_snapshot WHERE snapshot_id = @id");
        command.Parameters.AddWithValue("id", snapshotId);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private static SearchSnapshotRecord Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetFieldValue<DateTimeOffset>(3),
        Enum.TryParse<SearchSnapshotState>(reader.GetString(4), out var state) ? state : SearchSnapshotState.Failed,
        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetInt32(7));
}

public static class BackupStoreRegistration
{
    /// <summary>Registers <see cref="ISearchSnapshotStore"/> and <see cref="IRestoreStore"/> (need an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresBackupStores(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISearchSnapshotStore, SearchSnapshotStore>();
        services.TryAddSingleton<IRestoreStore, RestoreStore>();
        return services;
    }
}
