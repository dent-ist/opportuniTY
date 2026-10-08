namespace Opportunity.Data.Coding;

/// <summary>
/// E13-T01 AC 3: "changing to Withhold immediately blocks inclusion in any unfinalized production, independent of index
/// freshness". A coding transaction that changes Privilege Status holds this per-workspace advisory lock shared until it
/// commits; finalizing a production takes it exclusively before it checks its members' Privilege Status in PostgreSQL.
/// So a finalization either sees every committed status change, or finished first (and the change came after the
/// production was produced). Transaction-scoped; coding writes never block one another on it.
/// </summary>
internal static class PrivilegeGateSql
{
    public static Task EnterSharedAsync(WorkspaceTransaction tx, CancellationToken cancellationToken) =>
        LockAsync(tx, "SELECT pg_advisory_xact_lock_shared(hashtextextended('opportunity.privilege-gate ' || @ws::text, 0))", cancellationToken);

    public static Task EnterExclusiveAsync(WorkspaceTransaction tx, CancellationToken cancellationToken) =>
        LockAsync(tx, "SELECT pg_advisory_xact_lock(hashtextextended('opportunity.privilege-gate ' || @ws::text, 0))", cancellationToken);

    private static async Task LockAsync(WorkspaceTransaction tx, string sql, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
