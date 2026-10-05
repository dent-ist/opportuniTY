using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Messaging;
using Opportunity.Core.Workspaces;

namespace Opportunity.Data.Messaging;

/// <summary>
/// PostgreSQL implementation of <see cref="IDeadLetterStore"/> over <c>dead_letter</c> (tenant, forced RLS) and
/// <c>dead_letter_installation</c> (installation-level), V0031. A record whose workspace does not exist (or no longer
/// accepts rows) falls back to the installation table with the claimed id, so a forged or stale envelope can neither
/// fail the recorder nor reach a workspace that is not there.
/// </summary>
public sealed class DeadLetterStore(NpgsqlDataSource dataSource) : IDeadLetterStore
{
    /// <summary>Rows deleted per statement during retention, so one sweep never holds long locks.</summary>
    private const int DeleteBatch = 1_000;

    private const int MaxError = 4_000;

    private const string Columns =
        """
        message_id, queue, exchange, routing_key, job_id, subject_id, message_type, correlation_id, death_reason, death_count,
        error_type, error, first_death_at, headers::text, body, body_size, recorded_at
        """;

    /// <summary>Stored instead of headers PostgreSQL cannot hold (e.g. a <c>\u0000</c> escape, which jsonb rejects).</summary>
    private const string UnstorableHeaders = "{\"unstorable\":true}";

    public async Task<DeadLetterWriteOutcome> RecordAsync(DeadLetterMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            return await InsertAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState.StartsWith("22", StringComparison.Ordinal) || ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            // A value PostgreSQL rejects must not block the recorder's queue: keep the record without its headers.
            return await InsertAsync(message with { HeadersJson = UnstorableHeaders }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DeadLetterWriteOutcome> InsertAsync(DeadLetterMessage message, CancellationToken cancellationToken)
    {
        if (message.WorkspaceId is { } workspaceId)
        {
            try
            {
                await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
                await using var command = tx.Command(
                    """
                    INSERT INTO opportunity.dead_letter (workspace_id, message_id, queue, exchange, routing_key, job_id, subject_id,
                        message_type, correlation_id, death_reason, death_count, error_type, error, first_death_at, headers, body, body_size)
                    VALUES (@ws, @id, @queue, @exchange, @key, @job, @subject, @type, @correlation, @reason, @count, @error_type, @error,
                        @first_death, @headers::jsonb, @body, @body_size)
                    ON CONFLICT (workspace_id, message_id) DO NOTHING
                    """);
                command.Parameters.AddWithValue("ws", workspaceId);
                AddParameters(command, message);
                var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return inserted == 0 ? DeadLetterWriteOutcome.Duplicate : DeadLetterWriteOutcome.Workspace;
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.InsufficientPrivilege)
            {
                // No such workspace (or RLS rejects it): keep the record at installation level with the claimed id.
            }
        }

        await using (var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false))
        {
            await using var command = tx.Command(
                """
                INSERT INTO opportunity.dead_letter_installation (message_id, claimed_workspace_id, queue, exchange, routing_key, job_id,
                    subject_id, message_type, correlation_id, death_reason, death_count, error_type, error, first_death_at, headers, body,
                    body_size)
                VALUES (@id, @claimed, @queue, @exchange, @key, @job, @subject, @type, @correlation, @reason, @count, @error_type, @error,
                    @first_death, @headers::jsonb, @body, @body_size)
                ON CONFLICT (message_id) DO NOTHING
                """);
            command.Parameters.Add(new NpgsqlParameter("claimed", NpgsqlDbType.Uuid) { Value = (object?)message.WorkspaceId ?? DBNull.Value });
            AddParameters(command, message);
            var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return inserted == 0 ? DeadLetterWriteOutcome.Duplicate : DeadLetterWriteOutcome.Installation;
        }
    }

    public async Task<IReadOnlyList<DeadLetterRecord>> ListAsync(
        Guid? workspaceId, Guid? jobId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var tx = await BeginAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns}, {(workspaceId is null ? "claimed_workspace_id" : "NULL::uuid")}
              FROM {Table(workspaceId)}
             WHERE {(workspaceId is null ? "true" : "workspace_id = @ws")} AND (@job::uuid IS NULL OR job_id = @job)
             ORDER BY recorded_at DESC, message_id
             LIMIT @limit
            """);
        if (workspaceId is { } ws)
        {
            command.Parameters.AddWithValue("ws", ws);
        }

        command.Parameters.Add(new NpgsqlParameter("job", NpgsqlDbType.Uuid) { Value = (object?)jobId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        var records = await ReadAsync(command, workspaceId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return records;
    }

    public async Task<DeadLetterRecord?> GetAsync(Guid? workspaceId, string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        await using var tx = await BeginAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            SELECT {Columns}, {(workspaceId is null ? "claimed_workspace_id" : "NULL::uuid")}
              FROM {Table(workspaceId)}
             WHERE {(workspaceId is null ? "true" : "workspace_id = @ws")} AND message_id = @id
            """);
        if (workspaceId is { } ws)
        {
            command.Parameters.AddWithValue("ws", ws);
        }

        command.Parameters.AddWithValue("id", messageId);
        var records = await ReadAsync(command, workspaceId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return records.Count == 0 ? null : records[0];
    }

    public async Task<int> DeleteRecordedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        foreach (var workspaceId in await WorkspacesAsync(cancellationToken).ConfigureAwait(false))
        {
            deleted += await DeleteBatchesAsync(workspaceId, cutoff, cancellationToken).ConfigureAwait(false);
        }

        return deleted + await DeleteBatchesAsync(null, cutoff, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> DeleteBatchesAsync(Guid? workspaceId, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            await using var tx = await BeginAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            await using var command = tx.Command(workspaceId is null
                ? """
                  DELETE FROM opportunity.dead_letter_installation WHERE message_id IN (
                      SELECT message_id FROM opportunity.dead_letter_installation WHERE recorded_at < @cutoff LIMIT @batch)
                  """
                : """
                  DELETE FROM opportunity.dead_letter WHERE workspace_id = @ws AND message_id IN (
                      SELECT message_id FROM opportunity.dead_letter WHERE workspace_id = @ws AND recorded_at < @cutoff LIMIT @batch)
                  """);
            if (workspaceId is { } ws)
            {
                command.Parameters.AddWithValue("ws", ws);
            }

            command.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());
            command.Parameters.AddWithValue("batch", DeleteBatch);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < DeleteBatch)
            {
                return total;
            }
        }
    }

    private async Task<IReadOnlyList<Guid>> WorkspacesAsync(CancellationToken cancellationToken)
    {
        // The workspace registry is installation-level (ADR-015 D7.1): readable without a workspace context.
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"SELECT workspace_id FROM opportunity.workspace WHERE status <> '{nameof(WorkspaceStatus.Purged)}' ORDER BY workspace_id");
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    private Task<WorkspaceTransaction> BeginAsync(Guid? workspaceId, CancellationToken cancellationToken) =>
        workspaceId is { } ws
            ? WorkspaceTransaction.BeginAsync(dataSource, ws, cancellationToken)
            : WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken);

    private static string Table(Guid? workspaceId) =>
        workspaceId is null ? "opportunity.dead_letter_installation" : "opportunity.dead_letter";

    private static void AddParameters(NpgsqlCommand command, DeadLetterMessage message)
    {
        command.Parameters.AddWithValue("id", Cap(message.MessageId, 64)!);
        command.Parameters.AddWithValue("queue", Cap(message.Queue, 255)!);
        command.Parameters.AddWithValue("exchange", Cap(message.Exchange, 255)!);
        command.Parameters.AddWithValue("key", Cap(message.RoutingKey, 255)!);
        command.Parameters.Add(new NpgsqlParameter("job", NpgsqlDbType.Uuid) { Value = (object?)message.JobId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("subject", NpgsqlDbType.Uuid) { Value = (object?)message.SubjectId ?? DBNull.Value });
        command.Parameters.Add(Text("type", Cap(message.MessageType, 255)));
        command.Parameters.Add(Text("correlation", Cap(message.CorrelationId, 255)));
        command.Parameters.AddWithValue("reason", Cap(message.DeathReason, 100)!);
        command.Parameters.AddWithValue("count", Math.Max(1, message.DeathCount));
        command.Parameters.Add(Text("error_type", Cap(message.ErrorType, 500)));
        command.Parameters.Add(Text("error", Cap(message.Error, MaxError)));
        command.Parameters.Add(new NpgsqlParameter("first_death", NpgsqlDbType.TimestampTz)
        {
            Value = message.FirstDeathAt is { } at ? at.ToUniversalTime() : DBNull.Value,
        });
        command.Parameters.AddWithValue("headers", message.HeadersJson);
        command.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Bytea) { Value = message.Body.ToArray() });
        command.Parameters.AddWithValue("body_size", Math.Max(message.BodySize, message.Body.Length));
    }

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    /// <summary>Cuts to <paramref name="length"/> and removes NUL characters, which PostgreSQL text cannot hold.</summary>
    private static string? Cap(string? value, int length)
    {
        if (value is null)
        {
            return null;
        }

        value = value.Replace("\0", string.Empty, StringComparison.Ordinal);
        return value.Length <= length ? value : value[..length];
    }

    private static async Task<IReadOnlyList<DeadLetterRecord>> ReadAsync(
        NpgsqlCommand command, Guid? workspaceId, CancellationToken cancellationToken)
    {
        var records = new List<DeadLetterRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new DeadLetterRecord(
                workspaceId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                reader.GetString(13),
                reader.GetFieldValue<byte[]>(14),
                reader.GetInt32(15),
                reader.GetFieldValue<DateTimeOffset>(16),
                reader.IsDBNull(17) ? null : reader.GetGuid(17)));
        }

        return records;
    }
}

public static class DeadLetterStoreRegistration
{
    /// <summary>Registers <see cref="IDeadLetterStore"/> over the registered <see cref="NpgsqlDataSource"/>.</summary>
    public static IServiceCollection AddPostgresDeadLetterStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDeadLetterStore>(sp => new DeadLetterStore(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
