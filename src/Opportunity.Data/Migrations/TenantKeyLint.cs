using Npgsql;

namespace Opportunity.Data.Migrations;

/// <summary>
/// Schema lint: every table in a tenant schema must have a primary key whose first column is the tenant key
/// (<c>workspace_id</c>). Installation-level tables opt out with a table comment starting with <c>@global</c>.
/// Partitions are checked through their parent.
/// </summary>
public static class TenantKeyLint
{
    public const string GlobalTableMarker = "@global";

    private const string Sql = """
        SELECT n.nspname, c.relname, a.attname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_constraint pk ON pk.conrelid = c.oid AND pk.contype = 'p'
        LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = pk.conkey[1]
        WHERE n.nspname = ANY(@schemas)
          AND c.relkind IN ('r', 'p')
          AND NOT c.relispartition
          AND coalesce(obj_description(c.oid, 'pg_class'), '') NOT LIKE @global_marker
          AND a.attname IS DISTINCT FROM @tenant_key
        ORDER BY 1, 2
        """;

    /// <summary>Returns one message per offending table; empty when the schema is compliant.</summary>
    public static async Task<IReadOnlyList<string>> FindViolationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IEnumerable<string> tenantSchemas,
        string tenantKeyColumn,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        command.Parameters.AddWithValue("schemas", tenantSchemas.ToArray());
        command.Parameters.AddWithValue("global_marker", GlobalTableMarker + "%");
        command.Parameters.AddWithValue("tenant_key", tenantKeyColumn);

        var violations = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var table = $"{reader.GetString(0)}.{reader.GetString(1)}";
            violations.Add(reader.IsDBNull(2)
                ? $"{table} has no primary key; tenant tables need PRIMARY KEY ({tenantKeyColumn}, ...)."
                : $"{table} primary key leads with '{reader.GetString(2)}' instead of '{tenantKeyColumn}'.");
        }

        return violations;
    }
}
