using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Opportunity.Data;

/// <summary>
/// The only way <c>Opportunity.Data</c> reaches tenant tables (ADR-015 D7.2): a connection and transaction bound to one
/// workspace, with <c>app.workspace_id</c> set transaction-locally as the first statement, so the RLS policies of V0005
/// confine raw SQL, Dapper, staged COPY and EF Core alike, and the setting cannot outlive the transaction on a pooled
/// connection. Session-level <c>SET</c> is never used.
/// </summary>
internal sealed class WorkspaceTransaction : IAsyncDisposable
{
    private WorkspaceTransaction(Guid workspaceId, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        WorkspaceId = workspaceId;
        Connection = connection;
        Transaction = transaction;
    }

    public Guid WorkspaceId { get; }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction { get; }

    public static async Task<WorkspaceTransaction> BeginAsync(
        NpgsqlDataSource dataSource, Guid workspaceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace context needs a workspace id.", nameof(workspaceId));
        }

        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT set_config('app.workspace_id', @ws, true)", connection, transaction);
            command.Parameters.AddWithValue("ws", workspaceId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new WorkspaceTransaction(workspaceId, connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A transaction with no workspace context, for installation-level tables only (the workspace registry,
    /// ADR-015 D7.1). RLS policies match no tenant row without <c>app.workspace_id</c>, so this cannot read tenant data.
    /// </summary>
    public static async Task<WorkspaceTransaction> BeginInstallationAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return new WorkspaceTransaction(Guid.Empty, connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public NpgsqlCommand Command(string sql) => new(sql, Connection, Transaction);

    /// <summary>Several statements in one round trip, inside this transaction.</summary>
    public NpgsqlBatch Batch() => new(Connection, Transaction);

    /// <summary>
    /// An EF Core context enlisted in this transaction. Its global query filters (the second layer) match this
    /// workspace; RLS still applies underneath when a query ignores them.
    /// </summary>
    public OpportunityDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OpportunityDbContext>().UseNpgsql(Connection).Options;
        var context = new OpportunityDbContext(options) { BoundWorkspaceId = WorkspaceId };
        context.Database.UseTransaction(Transaction);
        return context;
    }

    public Task CommitAsync(CancellationToken cancellationToken) => Transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
