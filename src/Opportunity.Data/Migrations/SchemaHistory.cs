using Npgsql;

namespace Opportunity.Data.Migrations;

/// <summary>Names and queries for the migrator's bookkeeping in PostgreSQL.</summary>
public static class SchemaHistory
{
    /// <summary>Schema holding all application tables (created by V0001).</summary>
    public const string ApplicationSchema = "opportunity";

    public const string Schema = "opportunity_migrations";

    public const string Table = Schema + ".schema_history";

    /// <summary>Key for the session-level <c>pg_advisory_lock</c> that serializes migrators per database.</summary>
    public const long AdvisoryLockKey = 0x4F70_704D_6967_7231; // "OppMigr1"

    internal const string CreateSql = $"""
        CREATE SCHEMA IF NOT EXISTS {Schema};
        CREATE TABLE IF NOT EXISTS {Table} (
            version         integer     NOT NULL PRIMARY KEY,
            description     text        NOT NULL,
            script_name     text        NOT NULL,
            checksum        text        NOT NULL,
            transactional   boolean     NOT NULL,
            execution_ms    integer     NOT NULL,
            applied_by      text        NOT NULL DEFAULT current_user,
            applied_at      timestamptz NOT NULL DEFAULT now()
        );
        """;

    /// <summary>Highest applied version, or <c>null</c> when the database was never migrated.</summary>
    public static async Task<int?> GetCurrentVersionAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var exists = new NpgsqlCommand($"SELECT to_regclass('{Table}') IS NOT NULL", connection);
        if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
        {
            return null;
        }

        await using var max = new NpgsqlCommand($"SELECT max(version) FROM {Table}", connection);
        return await max.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int version ? version : null;
    }
}
