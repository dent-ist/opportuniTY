using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Workspaces;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Workspaces;

/// <summary>
/// PostgreSQL <see cref="IPreservationLockStore"/> and <see cref="IPreservationLockGuard"/> (E20-T01, V0048). Every write
/// locks the workspace row first, so it serializes with the FOR SHARE lock the delete guards take: a delete either
/// commits before the hold or sees it. The active count on the workspace row is kept by a trigger.
/// </summary>
public sealed class PreservationLockStore(NpgsqlDataSource dataSource) : IPreservationLockStore, IPreservationLockGuard
{
    private const string Columns =
        """
        l.workspace_id, l.lock_id, l.reason, l.matter_reference, l.release_requires_approval, l.placed_by, l.placed_at,
        l.release_requested_by, l.release_requested_at, l.release_reason, l.release_approved_by, l.released_at, l.version,
        (SELECT coalesce(u.display_name, u.email) FROM opportunity.app_user u WHERE u.user_id = l.placed_by),
        (SELECT coalesce(u.display_name, u.email) FROM opportunity.app_user u WHERE u.user_id = l.release_requested_by),
        (SELECT coalesce(u.display_name, u.email) FROM opportunity.app_user u WHERE u.user_id = l.release_approved_by)
        """;

    public async Task<IReadOnlyList<PreservationLock>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns}
            FROM opportunity.preservation_lock l
            WHERE l.workspace_id = @ws
            ORDER BY l.released_at IS NOT NULL, l.placed_at DESC, l.lock_id DESC
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        var locks = await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return locks;
    }

    public async Task<PreservationLock?> GetAsync(Guid workspaceId, Guid lockId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var found = await ReadOneAsync(tx, lockId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    public async Task<PreservationLock> PlaceAsync(PreservationLock newLock, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newLock);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, newLock.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await LockWorkspaceAsync(tx, cancellationToken).ConfigureAwait(false);
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.preservation_lock
                (workspace_id, lock_id, reason, matter_reference, release_requires_approval, placed_by, placed_at)
            VALUES (@ws, @id, @reason, @matter, @approval, @by, @at)
            """))
        {
            insert.Parameters.AddWithValue("ws", newLock.WorkspaceId);
            insert.Parameters.AddWithValue("id", newLock.LockId);
            insert.Parameters.AddWithValue("reason", newLock.Reason);
            insert.Parameters.Add(new NpgsqlParameter("matter", NpgsqlDbType.Text) { Value = (object?)newLock.MatterReference ?? DBNull.Value });
            insert.Parameters.AddWithValue("approval", newLock.ReleaseRequiresApproval);
            insert.Parameters.AddWithValue("by", newLock.PlacedBy);
            insert.Parameters.AddWithValue("at", newLock.PlacedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = (await ReadOneAsync(tx, newLock.LockId, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<PreservationLockWriteResult> UpdateReleaseAsync(
        Guid workspaceId, Guid lockId, long expectedVersion, PreservationLockRelease release, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await LockWorkspaceAsync(tx, cancellationToken).ConfigureAwait(false);
        int updated;
        await using (var update = tx.Command(
            """
            UPDATE opportunity.preservation_lock
               SET release_requested_by = @requested_by, release_requested_at = @requested_at, release_reason = @reason,
                   release_approved_by = @approved_by, released_at = @released_at, version = version + 1
             WHERE workspace_id = @ws AND lock_id = @id AND version = @version AND released_at IS NULL
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", lockId);
            update.Parameters.AddWithValue("version", expectedVersion);
            update.Parameters.Add(new NpgsqlParameter("requested_by", NpgsqlDbType.Uuid) { Value = (object?)release.RequestedBy ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("requested_at", NpgsqlDbType.TimestampTz) { Value = (object?)release.RequestedAt ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)release.Reason ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("approved_by", NpgsqlDbType.Uuid) { Value = (object?)release.ApprovedBy ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("released_at", NpgsqlDbType.TimestampTz) { Value = (object?)release.ReleasedAt ?? DBNull.Value });
            updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (updated == 0)
        {
            var current = await ReadOneAsync(tx, lockId, cancellationToken).ConfigureAwait(false);
            return new PreservationLockWriteResult(current is null ? PreservationLockWriteOutcome.NotFound : PreservationLockWriteOutcome.VersionConflict, current);
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var saved = await ReadOneAsync(tx, lockId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PreservationLockWriteResult(PreservationLockWriteOutcome.Ok, saved);
    }

    public async Task<bool> IsLockedAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        // The registry is read-open (ADR-015 D7.1); the count is kept by V0048's trigger.
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT active_preservation_locks > 0 FROM opportunity.workspace WHERE workspace_id = @ws");
        command.Parameters.AddWithValue("ws", workspaceId);
        var locked = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return locked;
    }

    private static async Task LockWorkspaceAsync(WorkspaceTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = tx.Command("SELECT 1 FROM opportunity.workspace WHERE workspace_id = @ws FOR NO KEY UPDATE");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PreservationLock?> ReadOneAsync(WorkspaceTransaction tx, Guid lockId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.preservation_lock l WHERE l.workspace_id = @ws AND l.lock_id = @id");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", lockId);
        var found = await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
        return found.Count == 0 ? null : found[0];
    }

    private static async Task<List<PreservationLock>> ReadAllAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var locks = new List<PreservationLock>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            locks.Add(new PreservationLock
            {
                WorkspaceId = reader.GetGuid(0),
                LockId = reader.GetGuid(1),
                Reason = reader.GetString(2),
                MatterReference = reader.IsDBNull(3) ? null : reader.GetString(3),
                ReleaseRequiresApproval = reader.GetBoolean(4),
                PlacedBy = reader.GetGuid(5),
                PlacedAt = reader.GetFieldValue<DateTimeOffset>(6),
                ReleaseRequestedBy = reader.IsDBNull(7) ? null : reader.GetGuid(7),
                ReleaseRequestedAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                ReleaseReason = reader.IsDBNull(9) ? null : reader.GetString(9),
                ReleaseApprovedBy = reader.IsDBNull(10) ? null : reader.GetGuid(10),
                ReleasedAt = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                Version = reader.GetInt64(12),
                PlacedByName = reader.IsDBNull(13) ? null : reader.GetString(13),
                ReleaseRequestedByName = reader.IsDBNull(14) ? null : reader.GetString(14),
                ReleaseApprovedByName = reader.IsDBNull(15) ? null : reader.GetString(15),
            });
        }

        return locks;
    }
}

/// <summary>
/// Recognizes the database's preservation-lock refusal (SQLSTATE <c>O0423</c>, V0048) anywhere in an exception chain, so
/// the API can answer 423 for every delete path, including ones written after E20-T01.
/// </summary>
public static class PreservationLockViolation
{
    public const string SqlState = "O0423";

    public static bool TryGet(Exception? exception, out PreservationLockedException? violation)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case PreservationLockedException locked:
                    violation = locked;
                    return true;
                case PostgresException { SqlState: SqlState } pg:
                    // Npgsql redacts DETAIL unless configured otherwise; the message always starts "workspace <id> is under …".
                    var words = pg.MessageText.Split(' ', 3);
                    violation = new PreservationLockedException(
                        words.Length > 1 && Guid.TryParse(words[1], out var ws) ? ws : Guid.Empty, string.IsNullOrEmpty(pg.Hint) ? "data" : pg.Hint, pg);
                    return true;
            }
        }

        violation = null;
        return false;
    }

    /// <summary>
    /// The E20-T02 hook: refuses (with <see cref="PreservationLockedException"/>) unless the transaction's workspace has no
    /// active lock, holding the workspace row FOR SHARE until the transaction ends.
    /// </summary>
    internal static async Task EnsureNotLockedAsync(WorkspaceTransaction tx, string target, CancellationToken cancellationToken)
    {
        await using var command = tx.Command("SELECT opportunity.assert_workspace_not_preserved(@ws, @target)");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("target", target);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == SqlState)
        {
            throw new PreservationLockedException(tx.WorkspaceId, target, ex);
        }
    }
}

public static class PreservationLockStoreRegistration
{
    /// <summary>Registers the PostgreSQL preservation-lock store and guard (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresPreservationLocks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<PreservationLockStore>();
        services.TryAddSingleton<IPreservationLockStore>(sp => sp.GetRequiredService<PreservationLockStore>());
        services.TryAddSingleton<IPreservationLockGuard>(sp => sp.GetRequiredService<PreservationLockStore>());
        return services;
    }
}
