using Npgsql;

namespace Opportunity.Data;

/// <summary>
/// A connection and transaction bound to one workspace: <c>app.workspace_id</c> is set transaction-locally as the
/// first statement (ADR-015 D7.2), so RLS policies (E05-T03) apply unchanged once they are enabled.
/// </summary>
internal sealed class WorkspaceTransaction : IAsyncDisposable
{
    private WorkspaceTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction { get; }

    public static async Task<WorkspaceTransaction> BeginAsync(
        NpgsqlDataSource dataSource, Guid workspaceId, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT set_config('app.workspace_id', @ws, true)", connection, transaction);
            command.Parameters.AddWithValue("ws", workspaceId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new WorkspaceTransaction(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public NpgsqlCommand Command(string sql) => new(sql, Connection, Transaction);

    public Task CommitAsync(CancellationToken cancellationToken) => Transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
