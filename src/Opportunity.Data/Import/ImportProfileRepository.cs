using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Import;

namespace Opportunity.Data.Import;

/// <summary>PostgreSQL implementation of <see cref="IImportProfileRepository"/> over <c>import_profile</c> (V0008).</summary>
public sealed class ImportProfileRepository(NpgsqlDataSource dataSource) : IImportProfileRepository
{
    private const string Columns =
        "workspace_id, profile_id, name, description, definition::text, version, created_by, created_at, updated_by, updated_at";

    private const string NameConstraint = "import_profile_name_uq";

    public async Task<IReadOnlyList<ImportProfileRecord>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var result = new List<ImportProfileRecord>();
        await using (var command = tx.Command($"SELECT {Columns} FROM opportunity.import_profile WHERE workspace_id = @ws ORDER BY name_norm, profile_id"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(Read(reader));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ImportProfileRecord?> GetAsync(Guid workspaceId, Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var record = await GetAsync(tx, workspaceId, profileId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<ImportProfileWriteResult> CreateAsync(
        Guid workspaceId, string name, string? description, string definitionJson, Guid? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(definitionJson);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        ImportProfileRecord? created = null;
        await using (var command = tx.Command(
            $"""
            INSERT INTO opportunity.import_profile (workspace_id, profile_id, name, description, definition, created_by, updated_by)
            VALUES (@ws, @id, @name, @description, @definition::jsonb, @user, @user)
            ON CONFLICT (workspace_id, name_norm) DO NOTHING
            RETURNING {Columns}
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("name", name.Trim());
            command.Parameters.Add(new NpgsqlParameter("description", NpgsqlDbType.Text) { Value = (object?)description ?? DBNull.Value });
            command.Parameters.AddWithValue("definition", definitionJson);
            command.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    created = Read(reader);
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return new ImportProfileWriteResult(ImportProfileWriteOutcome.NotFound, null);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created is null ? new ImportProfileWriteResult(ImportProfileWriteOutcome.NameConflict, null) : ImportProfileWriteResult.Ok(created);
    }

    public async Task<ImportProfileWriteResult> UpdateAsync(
        Guid workspaceId, Guid profileId, long expectedVersion, string name, string? description, string definitionJson, Guid? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(definitionJson);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = await GetAsync(tx, workspaceId, profileId, cancellationToken, forUpdate: true).ConfigureAwait(false);
        if (current is null)
        {
            return new ImportProfileWriteResult(ImportProfileWriteOutcome.NotFound, null);
        }

        if (current.Version != expectedVersion)
        {
            return new ImportProfileWriteResult(ImportProfileWriteOutcome.VersionConflict, current);
        }

        ImportProfileRecord updated;
        try
        {
            await using var command = tx.Command(
                $"""
                UPDATE opportunity.import_profile
                SET name = @name, description = @description, definition = @definition::jsonb, version = version + 1,
                    updated_by = @user, updated_at = now()
                WHERE workspace_id = @ws AND profile_id = @id
                RETURNING {Columns}
                """);
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", profileId);
            command.Parameters.AddWithValue("name", name.Trim());
            command.Parameters.Add(new NpgsqlParameter("description", NpgsqlDbType.Text) { Value = (object?)description ?? DBNull.Value });
            command.Parameters.AddWithValue("definition", definitionJson);
            command.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            updated = Read(reader);
        }
        catch (PostgresException ex) when (ex.ConstraintName == NameConstraint)
        {
            return new ImportProfileWriteResult(ImportProfileWriteOutcome.NameConflict, null);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ImportProfileWriteResult.Ok(updated);
    }

    public async Task<ImportProfileWriteOutcome> DeleteAsync(Guid workspaceId, Guid profileId, long? expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = await GetAsync(tx, workspaceId, profileId, cancellationToken, forUpdate: true).ConfigureAwait(false);
        if (current is null)
        {
            return ImportProfileWriteOutcome.NotFound;
        }

        if (expectedVersion is { } expected && expected != current.Version)
        {
            return ImportProfileWriteOutcome.VersionConflict;
        }

        await using (var command = tx.Command("DELETE FROM opportunity.import_profile WHERE workspace_id = @ws AND profile_id = @id"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", profileId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ImportProfileWriteOutcome.Ok;
    }

    private static async Task<ImportProfileRecord?> GetAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid profileId, CancellationToken cancellationToken, bool forUpdate = false)
    {
        await using var command = tx.Command(
            $"SELECT {Columns} FROM opportunity.import_profile WHERE workspace_id = @ws AND profile_id = @id" + (forUpdate ? " FOR UPDATE" : string.Empty));
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static ImportProfileRecord Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5),
        reader.IsDBNull(6) ? null : reader.GetGuid(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetGuid(8),
        reader.GetFieldValue<DateTimeOffset>(9));
}
