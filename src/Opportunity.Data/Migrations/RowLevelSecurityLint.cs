using Npgsql;

namespace Opportunity.Data.Migrations;

/// <summary>
/// Schema lint for ADR-015 D7, enforced by the migrator from <see cref="EffectiveFromVersion"/> on:
/// <list type="bullet">
/// <item>every table (and partition) with the tenant key column has RLS enabled and forced and a policy keyed on
/// <see cref="ContextSetting"/>;</item>
/// <item>runtime roles hold no privilege on a child partition (querying it directly would skip the parent's policy);</item>
/// <item>views are <c>security_invoker</c>, and no materialized view carries the tenant key (RLS cannot apply);</item>
/// <item><c>SECURITY DEFINER</c> functions pin <c>search_path</c> and are allow-listed with a
/// <see cref="SecurityDefinerMarker"/> comment.</item>
/// </list>
/// Tables marked <see cref="TenantKeyLint.GlobalTableMarker"/> (directly or through their partition root) are exempt.
/// </summary>
public static class RowLevelSecurityLint
{
    /// <summary>The migration that introduced RLS (E05-T03); earlier scripts predate the rule.</summary>
    public const int EffectiveFromVersion = 5;

    public const string ContextSetting = "app.workspace_id";

    public const string SecurityDefinerMarker = "@security-definer";

    private const string Sql = """
        WITH rel AS (
            SELECT c.oid, n.nspname, c.relname, c.relkind, c.relispartition, c.relrowsecurity, c.relforcerowsecurity,
                   c.reloptions,
                   coalesce(obj_description(coalesce(pg_partition_root(c.oid), c.oid), 'pg_class'), '') AS root_comment,
                   EXISTS (SELECT FROM pg_attribute a
                           WHERE a.attrelid = c.oid AND a.attname = @tenant_key AND a.attnum > 0 AND NOT a.attisdropped)
                       AS has_tenant_key
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = ANY(@schemas)
        )
        SELECT format('%I.%I', nspname, relname),
               CASE
                   WHEN relkind IN ('r', 'p') AND NOT relrowsecurity THEN 'has a ' || @tenant_key || ' column but row-level security is not enabled.'
                   WHEN relkind IN ('r', 'p') AND NOT relforcerowsecurity THEN 'has row-level security enabled but not forced.'
                   WHEN relkind IN ('r', 'p') AND NOT EXISTS (
                            SELECT FROM pg_policy p
                            WHERE p.polrelid = rel.oid
                              AND (coalesce(pg_get_expr(p.polqual, p.polrelid), '') LIKE '%' || @context || '%'
                                   OR coalesce(pg_get_expr(p.polwithcheck, p.polrelid), '') LIKE '%' || @context || '%'))
                       THEN 'has no row-level security policy keyed on ' || @context || '.'
                   WHEN relkind = 'm' THEN 'is a materialized view over tenant rows; row-level security cannot apply to it.'
               END
        FROM rel
        WHERE has_tenant_key AND relkind IN ('r', 'p', 'm') AND root_comment NOT LIKE @global_marker
        UNION ALL
        SELECT format('%I.%I', nspname, relname), 'is a partition that runtime role ' || r.rolname
               || ' can access directly; grant on the partitioned parent only.'
        FROM rel
        CROSS JOIN pg_roles r
        WHERE rel.relispartition AND rel.relkind IN ('r', 'p') AND r.rolname = ANY(@runtime_roles)
          AND has_table_privilege(r.oid, rel.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
        UNION ALL
        SELECT format('%I.%I', nspname, relname), 'is a view without security_invoker = true.'
        FROM rel
        WHERE relkind = 'v'
          AND NOT coalesce(reloptions && ARRAY['security_invoker=true', 'security_invoker=on', 'security_invoker=1'], false)
        UNION ALL
        SELECT format('%I.%I(%s)', n.nspname, p.proname, pg_get_function_identity_arguments(p.oid)),
               CASE
                   WHEN coalesce(obj_description(p.oid, 'pg_proc'), '') NOT LIKE @definer_marker
                       THEN 'is SECURITY DEFINER without a ''' || @definer_marker_text || ' <reason>'' comment (reviewed allow-list).'
                   ELSE 'is SECURITY DEFINER without a pinned search_path.'
               END
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = ANY(@schemas) AND p.prosecdef
          AND (coalesce(obj_description(p.oid, 'pg_proc'), '') NOT LIKE @definer_marker
               OR NOT EXISTS (SELECT FROM unnest(p.proconfig) cfg WHERE cfg LIKE 'search_path=%'))
        ORDER BY 1, 2
        """;

    /// <summary>Returns one message per offending object; empty when the schema is compliant.</summary>
    public static async Task<IReadOnlyList<string>> FindViolationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IEnumerable<string> tenantSchemas,
        string tenantKeyColumn,
        IEnumerable<string> runtimeRoles,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        command.Parameters.AddWithValue("schemas", tenantSchemas.ToArray());
        command.Parameters.AddWithValue("tenant_key", tenantKeyColumn);
        command.Parameters.AddWithValue("context", ContextSetting);
        command.Parameters.AddWithValue("global_marker", TenantKeyLint.GlobalTableMarker + "%");
        command.Parameters.AddWithValue("definer_marker", SecurityDefinerMarker + "%");
        command.Parameters.AddWithValue("definer_marker_text", SecurityDefinerMarker);
        command.Parameters.AddWithValue("runtime_roles", runtimeRoles.ToArray());

        var violations = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(1))
            {
                violations.Add($"{reader.GetString(0)} {reader.GetString(1)}");
            }
        }

        return violations;
    }
}
