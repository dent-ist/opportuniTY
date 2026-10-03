namespace Opportunity.Data.Migrations;

/// <summary>Settings for <see cref="PostgresMigrator"/> (configuration section <c>Migrator</c>).</summary>
public sealed class MigratorOptions
{
    public const string SectionName = "Migrator";

    /// <summary>How long to wait for another migrator holding the advisory lock before giving up.</summary>
    public TimeSpan LockWaitTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan LockPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Per-statement timeout in seconds; 0 waits indefinitely (large DDL, concurrent index builds).</summary>
    public int CommandTimeoutSeconds { get; set; }

    /// <summary>Reject any script that leaves a tenant table whose primary key does not lead with <see cref="TenantKeyColumn"/>.</summary>
    public bool EnforceTenantKeyLint { get; set; } = true;

    public IList<string> TenantSchemas { get; } = [SchemaHistory.ApplicationSchema];

    public string TenantKeyColumn { get; set; } = "workspace_id";

    /// <summary>
    /// Reject any script (from <see cref="RowLevelSecurityLint.EffectiveFromVersion"/> on) that leaves a tenant table
    /// without forced RLS, a directly accessible partition, a definer view or an unlisted SECURITY DEFINER function.
    /// </summary>
    public bool EnforceRowLevelSecurityLint { get; set; } = true;

    /// <summary>Roles subject to RLS that must never hold privileges on a child partition (V0001).</summary>
    public IList<string> RuntimeRoles { get; } = ["opportunity_app", "opportunity_readonly"];
}
