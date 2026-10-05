using System.Data;
using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Core.Fields;

namespace Opportunity.Correctness.ShadowLedger;

/// <summary>One committed coding change from <c>coding_event</c> (kind ValueChanged).</summary>
public sealed record CommittedChange(Guid DocumentId, long DocumentVersion, DateTimeOffset OccurredAt, Guid EventId);

/// <summary>Authoritative state of one document in one PostgreSQL snapshot.</summary>
public sealed record PostgresDocumentState(Guid DocumentId, long? Version, bool Deleted, IReadOnlyDictionary<int, JsonNode?> Values, IReadOnlyDictionary<int, List<int>> Choices)
{
    /// <summary>No <c>document_projection_state</c> row: purged (or never existed).</summary>
    public bool Missing => Version is null;
}

/// <summary>
/// The ledger's PostgreSQL side (E17-T07). Committed changes are captured from <c>CodingEvent</c>, not from logical
/// decoding: CodingEvent is written in the same transaction as the change and carries the DocumentVersion it produced
/// (§27, ADR-010 §5.4), so it is exactly the committed ledger the oracle needs, without a replication slot,
/// <c>wal_level=logical</c> or replication privileges (the developer and Testcontainers topologies run
/// <c>wal_level=replica</c>; an abandoned slot would also pin WAL). Authoritative state is read in REPEATABLE READ
/// snapshots with the workspace's RLS setting, so the oracle works as the owner role or as an app login.
/// </summary>
public sealed class PostgresLedgerReader(NpgsqlDataSource dataSource, Guid workspaceId)
{
    public Guid WorkspaceId => workspaceId;

    /// <summary>The database clock, so the ledger's start never depends on the test host's clock.</summary>
    public async Task<DateTimeOffset> NowAsync(CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT clock_timestamp()");
        var utc = (DateTime)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    public async Task<IReadOnlyList<CodingFieldInfo>> CodingFieldsAsync(CancellationToken cancellationToken = default)
    {
        var fields = new List<CodingFieldInfo>();
        await using var tx = await BeginAsync(cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT field_id, field_type, search_slot, is_searchable, is_deleted, date_precision
            FROM opportunity.field_definition WHERE workspace_id = @ws AND storage = 3 ORDER BY field_id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fields.Add(new CodingFieldInfo(
                reader.GetInt32(0), (FieldType)reader.GetInt16(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3),
                reader.GetBoolean(4), reader.IsDBNull(5) ? null : (DatePrecision)reader.GetInt16(5)));
        }

        return fields;
    }

    /// <summary>
    /// ValueChanged events with <c>occurred_at ≥ from</c>, in (occurred_at, event_id) order after the keyset
    /// <paramref name="after"/> (callers de-duplicate by event id across overlapping windows).
    /// </summary>
    public async Task<IReadOnlyList<CommittedChange>> ChangesSinceAsync(
        DateTimeOffset from, CommittedChange? after, int limit, CancellationToken cancellationToken = default)
    {
        var changes = new List<CommittedChange>();
        await using var tx = await BeginAsync(cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT document_id, document_version, occurred_at, event_id FROM opportunity.coding_event
            WHERE workspace_id = @ws AND event_kind = 1 AND occurred_at >= @from
              AND (@after_at::timestamptz IS NULL OR (occurred_at, event_id) > (@after_at, @after_id))
            ORDER BY occurred_at, event_id LIMIT @limit
            """);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.Add(new NpgsqlParameter("after_at", NpgsqlDbType.TimestampTz) { Value = (object?)after?.OccurredAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Uuid) { Value = (object?)after?.EventId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new CommittedChange(reader.GetGuid(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetGuid(3)));
        }

        return changes;
    }

    /// <summary>
    /// One keyset page of the documents to reconcile, in id order: every document with a committed coding change since
    /// <paramref name="since"/> (<see cref="LedgerScope.Touched"/>) or every document of the workspace.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> DocumentPageAsync(LedgerScope scope, DateTimeOffset since, Guid? after, int limit, CancellationToken cancellationToken = default)
    {
        var ids = new List<Guid>(limit);
        await using var tx = await BeginAsync(cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(scope == LedgerScope.Touched
            ? """
              SELECT DISTINCT document_id FROM opportunity.coding_event
              WHERE workspace_id = @ws AND occurred_at >= @since AND (@after::uuid IS NULL OR document_id > @after)
              ORDER BY document_id LIMIT @limit
              """
            : """
              SELECT document_id FROM opportunity.document_projection_state
              WHERE workspace_id = @ws AND (@after::uuid IS NULL OR document_id > @after)
              ORDER BY document_id LIMIT @limit
              """);
        command.Parameters.AddWithValue("since", since);
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Uuid) { Value = (object?)after ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Current DocumentVersions (null for a purged document), in one snapshot.</summary>
    public async Task<Dictionary<Guid, (long Version, bool Deleted)>> VersionsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken).ConfigureAwait(false);
        return await VersionsAsync(tx, ids, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Versions, deletion and coding values of <paramref name="ids"/> in one REPEATABLE READ snapshot.</summary>
    public async Task<IReadOnlyList<PostgresDocumentState>> StatesAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginAsync(cancellationToken).ConfigureAwait(false);
        var versions = await VersionsAsync(tx, ids, cancellationToken).ConfigureAwait(false);
        var values = ids.ToDictionary(id => id, _ => new Dictionary<int, JsonNode?>());
        var choices = ids.ToDictionary(id => id, _ => new Dictionary<int, List<int>>());
        await using (var command = tx.Command(
            "SELECT document_id, field_id, value::text FROM opportunity.document_coding_field WHERE workspace_id = @ws AND document_id = ANY(@ids)"))
        {
            command.Parameters.AddWithValue("ids", ids.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                values[reader.GetGuid(0)][reader.GetInt32(1)] = reader.IsDBNull(2) ? null : JsonNode.Parse(reader.GetString(2));
            }
        }

        await using (var command = tx.Command(
            "SELECT document_id, field_id, choice_id FROM opportunity.document_coding_choice WHERE workspace_id = @ws AND document_id = ANY(@ids)"))
        {
            command.Parameters.AddWithValue("ids", ids.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var perField = choices[reader.GetGuid(0)];
                var field = reader.GetInt32(1);
                if (!perField.TryGetValue(field, out var list))
                {
                    perField[field] = list = [];
                }

                list.Add(reader.GetInt32(2));
            }
        }

        return [.. ids.Select(id => versions.TryGetValue(id, out var v)
            ? new PostgresDocumentState(id, v.Version, v.Deleted, values[id], choices[id])
            : new PostgresDocumentState(id, null, true, values[id], choices[id]))];
    }

    private static async Task<Dictionary<Guid, (long Version, bool Deleted)>> VersionsAsync(
        LedgerTransaction tx, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var versions = new Dictionary<Guid, (long, bool)>(ids.Count);
        await using var command = tx.Command(
            "SELECT document_id, document_version, is_deleted FROM opportunity.document_projection_state WHERE workspace_id = @ws AND document_id = ANY(@ids)");
        command.Parameters.AddWithValue("ids", ids.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions[reader.GetGuid(0)] = (reader.GetInt64(1), reader.GetBoolean(2));
        }

        return versions;
    }

    private async Task<LedgerTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            await using (var command = new NpgsqlCommand("SET TRANSACTION READ ONLY; SELECT set_config('app.workspace_id', @ws, true)", connection, transaction))
            {
                command.Parameters.AddWithValue("ws", workspaceId.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new LedgerTransaction(connection, transaction, workspaceId);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class LedgerTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspaceId) : IAsyncDisposable
    {
        public NpgsqlCommand Command(string sql)
        {
#pragma warning disable CA2100 // Fixed SQL text; values are parameters.
            var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("ws", workspaceId);
            return command;
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Which documents a reconciliation visits.</summary>
public enum LedgerScope
{
    /// <summary>Documents with a committed coding change since the ledger started (the §26 "touched docs").</summary>
    Touched,

    /// <summary>Every document of the workspace (imports, overlays, deletes and coding alike).</summary>
    Workspace,
}
