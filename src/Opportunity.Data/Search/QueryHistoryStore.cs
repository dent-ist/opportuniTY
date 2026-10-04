using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Search;

namespace Opportunity.Data.Search;

/// <summary>
/// PostgreSQL implementation of <see cref="IQueryHistoryStore"/> over <c>query_history</c> (V0019, tenant, forced
/// RLS). Writes for one user and workspace are serialized by a transaction-scoped advisory lock, so concurrent runs
/// cannot leave more than <see cref="IQueryHistoryStore.MaxEntries"/> rows.
/// </summary>
public sealed class QueryHistoryStore(NpgsqlDataSource dataSource) : IQueryHistoryStore
{
    public async Task<IReadOnlyList<QueryHistoryRecord>> ListAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT query_text, last_run_at FROM opportunity.query_history
             WHERE workspace_id = @ws AND user_id = @user
             ORDER BY last_run_at DESC, query_key
             LIMIT @max
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("max", IQueryHistoryStore.MaxEntries);
        var entries = new List<QueryHistoryRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new QueryHistoryRecord(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entries;
    }

    public async Task RecordAsync(Guid workspaceId, Guid userId, string queryText, DateTimeOffset ranAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryText);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            SELECT pg_advisory_xact_lock(hashtextextended('query_history:' || @ws::text || ':' || @user::text, 0));

            INSERT INTO opportunity.query_history (workspace_id, user_id, query_key, query_text, last_run_at)
            VALUES (@ws, @user, @key, @query, @ranAt)
            ON CONFLICT (workspace_id, user_id, query_key)
            DO UPDATE SET last_run_at = GREATEST(opportunity.query_history.last_run_at, EXCLUDED.last_run_at);

            DELETE FROM opportunity.query_history h
             WHERE h.workspace_id = @ws AND h.user_id = @user
               AND h.query_key NOT IN (SELECT k.query_key FROM opportunity.query_history k
                                        WHERE k.workspace_id = @ws AND k.user_id = @user
                                        ORDER BY k.last_run_at DESC, k.query_key
                                        LIMIT @max);
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("user", userId);
            command.Parameters.AddWithValue("key", SHA256.HashData(Encoding.UTF8.GetBytes(queryText)));
            command.Parameters.AddWithValue("query", queryText);
            command.Parameters.AddWithValue("ranAt", ranAt);
            command.Parameters.AddWithValue("max", IQueryHistoryStore.MaxEntries);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class QueryHistoryStoreRegistration
{
    /// <summary>Registers the PostgreSQL <see cref="IQueryHistoryStore"/> (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresQueryHistoryStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IQueryHistoryStore, QueryHistoryStore>();
        return services;
    }
}
