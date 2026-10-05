using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Search;

namespace Opportunity.Data.Search;

/// <summary>
/// PostgreSQL implementation of <see cref="ISearchSessionStore"/> over <c>search_session</c> and <c>search_cursor</c>
/// (V0013, tenant, forced RLS). Every read and write runs in the requested workspace's context, so another workspace's
/// handle cannot be found even if it is guessed.
/// </summary>
public sealed class SearchSessionStore(NpgsqlDataSource dataSource) : ISearchSessionStore
{
    private const string SessionColumns =
        """
        workspace_id, search_id, user_id, session_id, query_text, sort_keys::text, page_size, count_exact, highlight,
        pit_id, total_value, total_exact, created_at, expires_at, served_generation, pit_opened_at, scope::text
        """;

    /// <summary>Expired searches removed per create, so cleanup cost stays bounded.</summary>
    private const int CleanupBatch = 100;

    public async Task CreateAsync(SearchSessionRecord search, IReadOnlyList<SearchCursorRecord> cursors, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(cursors);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, search.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using (var cleanup = tx.Command(
            """
            DELETE FROM opportunity.search_session
             WHERE workspace_id = @ws
               AND search_id IN (SELECT search_id FROM opportunity.search_session
                                  WHERE workspace_id = @ws AND expires_at < now() LIMIT @batch)
            """))
        {
            cleanup.Parameters.AddWithValue("ws", search.WorkspaceId);
            cleanup.Parameters.AddWithValue("batch", CleanupBatch);
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.search_session
                (workspace_id, search_id, user_id, session_id, query_text, sort_keys, page_size, count_exact, highlight,
                 pit_id, total_value, total_exact, created_at, expires_at, served_generation, pit_opened_at, scope)
            VALUES (@ws, @id, @user, @session, @query, @sort, @size, @exact, @highlight, @pit, @total, @totalExact, @created, @expires,
                    @served, @pitOpened, @scope)
            """))
        {
            insert.Parameters.AddWithValue("ws", search.WorkspaceId);
            insert.Parameters.AddWithValue("id", search.SearchId);
            insert.Parameters.AddWithValue("user", search.UserId);
            insert.Parameters.AddWithValue("session", (object?)search.SessionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("query", search.QueryText);
            insert.Parameters.AddWithValue("sort", NpgsqlDbType.Jsonb, search.SortJson);
            insert.Parameters.AddWithValue("size", search.PageSize);
            insert.Parameters.AddWithValue("exact", search.CountExact);
            insert.Parameters.AddWithValue("highlight", search.Highlight);
            insert.Parameters.AddWithValue("pit", (object?)search.PointInTimeId ?? DBNull.Value);
            insert.Parameters.AddWithValue("total", search.TotalValue);
            insert.Parameters.AddWithValue("totalExact", search.TotalExact);
            insert.Parameters.AddWithValue("created", search.CreatedAt);
            insert.Parameters.AddWithValue("expires", search.ExpiresAt);
            insert.Parameters.AddWithValue("served", NpgsqlDbType.Bigint, (object?)search.ServedGeneration ?? DBNull.Value);
            insert.Parameters.AddWithValue("pitOpened", NpgsqlDbType.TimestampTz,
                search.PointInTimeId is null ? DBNull.Value : search.PointInTimeOpenedAt ?? search.CreatedAt);
            insert.Parameters.AddWithValue("scope", NpgsqlDbType.Jsonb, (object?)search.ScopeJson ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertCursorsAsync(tx, cursors, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchSessionRecord?> GetAsync(Guid workspaceId, Guid searchId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"SELECT {SessionColumns} FROM opportunity.search_session WHERE workspace_id = @ws AND search_id = @id AND expires_at > now()");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", searchId);
        SearchSessionRecord? record = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                record = new SearchSessionRecord(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetBoolean(7),
                    reader.GetBoolean(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetInt64(10),
                    reader.GetBoolean(11),
                    reader.GetFieldValue<DateTimeOffset>(12),
                    reader.GetFieldValue<DateTimeOffset>(13))
                {
                    ServedGeneration = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                    PointInTimeOpenedAt = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
                    ScopeJson = reader.IsDBNull(16) ? null : reader.GetString(16),
                };
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<SearchCursorRecord?> GetCursorAsync(
        Guid workspaceId, Guid searchId, Guid cursorId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT direction, sort_values::text, page_number, expires_at FROM opportunity.search_cursor
             WHERE workspace_id = @ws AND search_id = @search AND cursor_id = @cursor AND expires_at > now()
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("search", searchId);
        command.Parameters.AddWithValue("cursor", cursorId);
        SearchCursorRecord? record = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                record = new SearchCursorRecord(
                    workspaceId,
                    searchId,
                    cursorId,
                    (SearchCursorDirection)reader.GetInt16(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.GetFieldValue<DateTimeOffset>(3));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<SearchCursorBinding?> GetCursorBindingAsync(Guid workspaceId, Guid cursorId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT c.search_id, s.user_id, s.session_id
              FROM opportunity.search_cursor c
              JOIN opportunity.search_session s ON s.workspace_id = c.workspace_id AND s.search_id = c.search_id
             WHERE c.workspace_id = @ws AND c.cursor_id = @cursor
             LIMIT 1
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("cursor", cursorId);
        SearchCursorBinding? binding = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                binding = new SearchCursorBinding(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return binding;
    }

    public async Task TouchAsync(
        Guid workspaceId,
        Guid searchId,
        SearchReaderUpdate? reader,
        DateTimeOffset expiresAt,
        IReadOnlyList<SearchCursorRecord> cursors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursors);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var update = tx.Command(
            """
            UPDATE opportunity.search_session
               SET pit_id = COALESCE(@pit, pit_id),
                   pit_opened_at = CASE WHEN @pit IS NULL THEN pit_opened_at ELSE @opened END,
                   served_generation = CASE WHEN @pit IS NULL THEN served_generation ELSE @served END,
                   expires_at = GREATEST(expires_at, @expires)
             WHERE workspace_id = @ws AND search_id = @id
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", searchId);
            update.Parameters.AddWithValue("pit", NpgsqlDbType.Text, (object?)reader?.PointInTimeId ?? DBNull.Value);
            update.Parameters.AddWithValue("opened", NpgsqlDbType.TimestampTz, (object?)reader?.OpenedAt ?? DBNull.Value);
            update.Parameters.AddWithValue("served", NpgsqlDbType.Bigint, (object?)reader?.ServedGeneration ?? DBNull.Value);
            update.Parameters.AddWithValue("expires", expiresAt);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                // Removed concurrently (expired and cleaned up): the page is still served, its cursors just lead nowhere.
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await InsertCursorsAsync(tx, cursors, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> DetachReadersAsync(Guid workspaceId, Guid userId, int keep, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keep);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var detached = new List<string>();
        await using (var command = tx.Command(
            """
            WITH detached AS (
                SELECT search_id, pit_id FROM opportunity.search_session
                 WHERE workspace_id = @ws AND user_id = @user AND pit_id IS NOT NULL AND expires_at > now()
                 ORDER BY COALESCE(pit_opened_at, created_at) DESC, search_id DESC
                OFFSET @keep
                   FOR UPDATE)
            UPDATE opportunity.search_session s SET pit_id = NULL, pit_opened_at = NULL
              FROM detached d
             WHERE s.workspace_id = @ws AND s.search_id = d.search_id
            RETURNING d.pit_id
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("user", userId);
            command.Parameters.AddWithValue("keep", keep);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                detached.Add(reader.GetString(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return detached;
    }

    private static async Task InsertCursorsAsync(WorkspaceTransaction tx, IReadOnlyList<SearchCursorRecord> cursors, CancellationToken cancellationToken)
    {
        if (cursors.Count == 0)
        {
            return;
        }

        await using var batch = tx.Batch();
        foreach (var cursor in cursors)
        {
            if (cursor.WorkspaceId != tx.WorkspaceId)
            {
                throw new ArgumentException("A cursor belongs to the workspace of its search.", nameof(cursors));
            }

            var command = new NpgsqlBatchCommand(
                """
                INSERT INTO opportunity.search_cursor (workspace_id, search_id, cursor_id, direction, sort_values, page_number, expires_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                """);
            command.Parameters.Add(new NpgsqlParameter { Value = cursor.WorkspaceId });
            command.Parameters.Add(new NpgsqlParameter { Value = cursor.SearchId });
            command.Parameters.Add(new NpgsqlParameter { Value = cursor.CursorId });
            command.Parameters.Add(new NpgsqlParameter { Value = (short)cursor.Direction });
            command.Parameters.Add(new NpgsqlParameter { Value = cursor.SortValuesJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)cursor.PageNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
            command.Parameters.Add(new NpgsqlParameter { Value = cursor.ExpiresAt });
            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class SearchSessionStoreRegistration
{
    /// <summary>
    /// Registers the PostgreSQL <see cref="ISearchSessionStore"/> and the search freshness store
    /// (<see cref="ISearchFreshnessReader"/>, <see cref="ISearchWatermarkStore"/>); they need an <see cref="NpgsqlDataSource"/>.
    /// </summary>
    public static IServiceCollection AddPostgresSearchSessionStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISearchSessionStore, SearchSessionStore>();
        services.AddPostgresSearchWatermarkStore();
        return services;
    }

    /// <summary>Registers the PostgreSQL <see cref="ISearchFreshnessReader"/> and <see cref="ISearchWatermarkStore"/> (one instance).</summary>
    public static IServiceCollection AddPostgresSearchWatermarkStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<SearchWatermarkStore>();
        services.TryAddSingleton<ISearchWatermarkStore>(sp => sp.GetRequiredService<SearchWatermarkStore>());
        services.TryAddSingleton<ISearchFreshnessReader>(sp => sp.GetRequiredService<SearchWatermarkStore>());
        return services;
    }
}
