using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Keys;
using Opportunity.Application.Workspaces;
using Opportunity.Data.Audit;
using Opportunity.Data.Workspaces;

namespace Opportunity.Data.Keys;

/// <summary>
/// PostgreSQL <see cref="IWorkspaceDataKeyStore"/> over <c>opportunity.workspace_data_key</c> (V0052). Each change and its
/// audit event commit together; V0052's trigger refuses destruction under a preservation lock and any change of a
/// destroyed key.
/// </summary>
public sealed class WorkspaceDataKeyStore(NpgsqlDataSource dataSource) : IWorkspaceDataKeyStore
{
    private const string Columns =
        "workspace_id, key_version, state, kek_id, kek_version, wrapped_key, created_at, rewrapped_at, destroyed_at";

    public async Task<WorkspaceDataKeyRecord?> GetActiveAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var row = await ReadActiveAsync(tx, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    public async Task<WorkspaceDataKeyRecord?> GetAsync(Guid workspaceId, int version, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.workspace_data_key WHERE workspace_id = @ws AND key_version = @v");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("v", version);
        var rows = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.SingleOrDefault();
    }

    public async Task<IReadOnlyList<WorkspaceDataKeyRecord>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.workspace_data_key WHERE workspace_id = @ws ORDER BY key_version");
        command.Parameters.AddWithValue("ws", workspaceId);
        var rows = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<WorkspaceDataKeyRecord> CreateInitialAsync(
        Guid workspaceId, WrappedKey wrapped, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        int inserted;
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.workspace_data_key (workspace_id, key_version, state, kek_id, kek_version, wrapped_key)
            SELECT @ws, 1, 1, @kek, @kek_version, @wrapped
            WHERE NOT EXISTS (SELECT FROM opportunity.workspace_data_key WHERE workspace_id = @ws)
            ON CONFLICT DO NOTHING
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("kek", wrapped.KekId);
            insert.Parameters.AddWithValue("kek_version", wrapped.KekVersion);
            insert.Parameters.AddWithValue("wrapped", wrapped.Ciphertext);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (inserted == 1)
        {
            await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        }

        var active = await ReadActiveAsync(tx, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyUnavailableException("The workspace's data keys were destroyed (crypto-shredded); it cannot store new objects.");
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return active;
    }

    public async Task<bool> RotateAsync(
        Guid workspaceId, int expectedActiveVersion, WrappedKey wrapped, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var retire = tx.Command(
            "UPDATE opportunity.workspace_data_key SET state = 2 WHERE workspace_id = @ws AND key_version = @v AND state = 1"))
        {
            retire.Parameters.AddWithValue("ws", workspaceId);
            retire.Parameters.AddWithValue("v", expectedActiveVersion);
            if (await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.workspace_data_key (workspace_id, key_version, state, kek_id, kek_version, wrapped_key)
            VALUES (@ws, @v, 1, @kek, @kek_version, @wrapped)
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("v", expectedActiveVersion + 1);
            insert.Parameters.AddWithValue("kek", wrapped.KekId);
            insert.Parameters.AddWithValue("kek_version", wrapped.KekVersion);
            insert.Parameters.AddWithValue("wrapped", wrapped.Ciphertext);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RewrapAsync(
        Guid workspaceId,
        int version,
        string expectedKekId,
        int expectedKekVersion,
        WrappedKey replacement,
        AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var update = tx.Command(
            """
            UPDATE opportunity.workspace_data_key
               SET kek_id = @kek, kek_version = @kek_version, wrapped_key = @wrapped, rewrapped_at = now()
             WHERE workspace_id = @ws AND key_version = @v AND state <> 3
               AND kek_id = @expected_kek AND kek_version = @expected_kek_version
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("v", version);
            update.Parameters.AddWithValue("kek", replacement.KekId);
            update.Parameters.AddWithValue("kek_version", replacement.KekVersion);
            update.Parameters.AddWithValue("wrapped", replacement.Ciphertext);
            update.Parameters.AddWithValue("expected_kek", expectedKekId);
            update.Parameters.AddWithValue("expected_kek_version", expectedKekVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> DestroyAllAsync(Guid workspaceId, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        try
        {
            await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
            await PreservationLockViolation.EnsureNotLockedAsync(tx, "workspace_data_key", cancellationToken).ConfigureAwait(false);
            int destroyed;
            await using (var update = tx.Command(
                """
                UPDATE opportunity.workspace_data_key
                   SET state = 3, wrapped_key = NULL, destroyed_at = now()
                 WHERE workspace_id = @ws AND state <> 3
                """))
            {
                update.Parameters.AddWithValue("ws", workspaceId);
                destroyed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var details = new Dictionary<string, string?>(audit.Details) { ["dataKeysDestroyed"] = destroyed.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            await AuditSql.InsertAsync(tx, audit with { Details = details }, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return destroyed;
        }
        catch (PostgresException ex) when (PreservationLockViolation.TryGet(ex, out var locked))
        {
            throw locked!;
        }
    }

    public async Task<IReadOnlyList<Guid>> ListWorkspaceIdsAsync(CancellationToken cancellationToken = default)
    {
        // The workspace registry is installation-level (ADR-015 D7.1): readable without a workspace context.
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT workspace_id FROM opportunity.workspace ORDER BY workspace_id");
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    private static async Task<WorkspaceDataKeyRecord?> ReadActiveAsync(WorkspaceTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.workspace_data_key WHERE workspace_id = @ws AND state = 1");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        return (await ReadAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    private static async Task<List<WorkspaceDataKeyRecord>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<WorkspaceDataKeyRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new WorkspaceDataKeyRecord(
                reader.GetGuid(0),
                reader.GetInt32(1),
                (WorkspaceDataKeyState)reader.GetInt16(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return rows;
    }
}

public static class WorkspaceDataKeyStoreRegistration
{
    /// <summary>Registers the PostgreSQL workspace data key store (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresWorkspaceDataKeys(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IWorkspaceDataKeyStore, WorkspaceDataKeyStore>();
        return services;
    }
}
