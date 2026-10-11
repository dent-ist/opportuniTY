using System.Diagnostics;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Telemetry;

namespace Opportunity.Data.Audit;

/// <summary>
/// Inserts and reads <c>audit.audit_event</c> (V0010) inside a caller's <see cref="WorkspaceTransaction"/>, so a state
/// change and its event commit or roll back together (ADR-013 §2.1). RLS admits an event only into the chain of the
/// transaction's context: the workspace's, or the system chain for an installation transaction.
/// </summary>
internal static class AuditSql
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string Prefixed =
        """
        e.event_id, e.schema_version, e.workspace_id, e.occurred_at, e.category, e.action, e.actor_type, e.actor_id, e.actor_display,
        e.on_behalf_of, e.access_path, e.client_ip, e.user_agent, e.session_id_hash, e.resource_type, e.resource_id, e.outcome,
        e.reason_code, e.correlation_id, e.causation_id, e.job_id, e.chunk_sequence, e.snapshot_id, e.search_generation, e.details
        """;

    private const string Columns =
        """
        event_id, schema_version, workspace_id, occurred_at, category, action, actor_type, actor_id, actor_display,
        on_behalf_of, access_path, client_ip, user_agent, session_id_hash, resource_type, resource_id, outcome,
        reason_code, correlation_id, causation_id, job_id, chunk_sequence, snapshot_id, search_generation, details
        """;

    /// <summary>
    /// Completes the envelope from the current <see cref="AuditRequestContext"/>, validates and inserts
    /// <paramref name="auditEvent"/>; a retried insert of the same EventId is a no-op.
    /// </summary>
    public static async Task InsertAsync(WorkspaceTransaction tx, AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tx);
        // Client, session and correlation come from the request when the caller left them empty (E14-T02).
        var e = AuditEventRules.Normalize(AuditRequestContext.Complete(auditEvent));
        AuditEventRules.EnsureValid(e);
        var scope = tx.WorkspaceId == Guid.Empty ? (Guid?)null : tx.WorkspaceId;
        if (e.WorkspaceId != scope)
        {
            throw new ArgumentException(
                $"An event of workspace '{e.WorkspaceId?.ToString() ?? "(system)"}' cannot be written in the transaction of '{scope?.ToString() ?? "(system)"}'.",
                nameof(auditEvent));
        }

        await using var command = tx.Command(
            $"""
            INSERT INTO audit.audit_event ({Columns}, restricted_details)
            VALUES (@event_id, @schema_version, @workspace_id, @occurred_at, @category, @action, @actor_type, @actor_id,
                    @actor_display, @on_behalf_of, @access_path, @client_ip, @user_agent, @session_id_hash, @resource_type,
                    @resource_id, @outcome, @reason_code, @correlation_id, @causation_id, @job_id, @chunk_sequence,
                    @snapshot_id, @search_generation, @details, @restricted_details)
            ON CONFLICT (event_id, occurred_at) DO NOTHING
            """);
        var p = command.Parameters;
        p.AddWithValue("event_id", e.EventId);
        p.AddWithValue("schema_version", e.SchemaVersion);
        p.Add(Nullable("workspace_id", NpgsqlDbType.Uuid, e.WorkspaceId));
        p.AddWithValue("occurred_at", e.OccurredAt.ToUniversalTime());
        p.AddWithValue("category", e.Category);
        p.AddWithValue("action", e.Action);
        p.AddWithValue("actor_type", e.ActorType.ToString());
        p.AddWithValue("actor_id", e.ActorId);
        p.AddWithValue("actor_display", e.ActorDisplay);
        p.Add(Nullable("on_behalf_of", NpgsqlDbType.Uuid, e.OnBehalfOf));
        p.AddWithValue("access_path", e.AccessPath.ToString());
        p.Add(Nullable("client_ip", NpgsqlDbType.Inet, e.ClientIp is { } ip && IPAddress.TryParse(ip, out var address) ? address : null));
        p.Add(Nullable("user_agent", NpgsqlDbType.Text, e.UserAgent));
        p.Add(Nullable("session_id_hash", NpgsqlDbType.Bytea, e.SessionIdHash?.ToArray()));
        p.Add(Nullable("resource_type", NpgsqlDbType.Text, e.ResourceType));
        p.Add(Nullable("resource_id", NpgsqlDbType.Text, e.ResourceId));
        p.AddWithValue("outcome", e.Outcome.ToString());
        p.Add(Nullable("reason_code", NpgsqlDbType.Text, e.ReasonCode));
        p.AddWithValue("correlation_id", e.CorrelationId ?? AmbientCorrelationId());
        p.Add(Nullable("causation_id", NpgsqlDbType.Text, e.CausationId));
        p.Add(Nullable("job_id", NpgsqlDbType.Uuid, e.JobId));
        p.Add(Nullable("chunk_sequence", NpgsqlDbType.Integer, e.ChunkSequence));
        p.Add(Nullable("snapshot_id", NpgsqlDbType.Uuid, e.SnapshotId));
        p.Add(Nullable("search_generation", NpgsqlDbType.Bigint, e.SearchGeneration));
        p.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(e.Details, Json) });
        p.Add(Nullable("restricted_details", NpgsqlDbType.Jsonb,
            e.RestrictedDetails is null ? null : JsonSerializer.Serialize(e.RestrictedDetails, Json)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<AuditEventPage> QueryAsync(WorkspaceTransaction tx, AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var command = tx.Command(string.Empty);
        var where = Filter(command, query);
        var descending = query.NewestFirst;
        if (query.After is { } after)
        {
            where.Add(descending ? "(e.occurred_at, e.event_id) < (@after_at, @after_id)" : "(e.occurred_at, e.event_id) > (@after_at, @after_id)");
            command.Parameters.AddWithValue("after_at", after.OccurredAt.ToUniversalTime());
            command.Parameters.AddWithValue("after_id", after.EventId);
        }

        var direction = descending ? "DESC" : "ASC";
        command.CommandText = Select(query) +
            $"""

            WHERE {string.Join(" AND ", where)}
            ORDER BY e.occurred_at {direction}, e.event_id {direction}
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("restricted", query.IncludeRestrictedDetails);
        command.Parameters.AddWithValue("limit", query.Limit + 1);

        var events = new List<StoredAuditEvent>(Math.Min(query.Limit + 1, 256));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(ReadWithDocument(reader));
            }
        }

        AuditCursor? next = null;
        if (events.Count > query.Limit)
        {
            events.RemoveAt(events.Count - 1);
            next = new AuditCursor(events[^1].Event.OccurredAt, events[^1].Event.EventId);
        }

        return new AuditEventPage(events, next);
    }

    /// <summary>
    /// The events of <paramref name="query"/> (its filters and visibility; its time bounds, cursor and limit are ignored)
    /// that occurred in <paramref name="from"/>…<paramref name="to"/> (inclusive), oldest first, streamed to
    /// <paramref name="onEvent"/>. Returns how many were read.
    /// </summary>
    public static async Task<long> ForEachAsync(
        WorkspaceTransaction tx, AuditQuery query, DateTimeOffset from, DateTimeOffset to, Func<StoredAuditEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(onEvent);
        await using var command = tx.Command(string.Empty);
        var where = Filter(command, query with { From = null, To = null });
        where.Add("e.occurred_at BETWEEN @window_from AND @window_to");
        command.Parameters.AddWithValue("window_from", from.ToUniversalTime());
        command.Parameters.AddWithValue("window_to", to.ToUniversalTime());
        command.CommandText = Select(query) +
            $"""

            WHERE {string.Join(" AND ", where)}
            ORDER BY e.occurred_at, e.event_id
            """;
        command.Parameters.AddWithValue("restricted", query.IncludeRestrictedDetails);
        command.CommandTimeout = 0;
        long count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await onEvent(ReadWithDocument(reader)).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    /// <summary>
    /// The occurrence times (Unix microseconds) of the events matching <paramref name="query"/>'s filters and visibility,
    /// oldest first, streamed to <paramref name="onTime"/>; at most <paramref name="max"/> + 1 are read.
    /// </summary>
    public static async Task ForEachTimeAsync(
        WorkspaceTransaction tx, AuditQuery query, long max, Action<long> onTime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(onTime);
        await using var command = tx.Command(string.Empty);
        var where = Filter(command, query);
        command.CommandText =
            $"""
            SELECT (extract(epoch FROM e.occurred_at) * 1000000)::bigint
            FROM audit.audit_event e
            WHERE {string.Join(" AND ", where)}
            ORDER BY e.occurred_at, e.event_id
            LIMIT @max
            """;
        command.Parameters.AddWithValue("max", max + 1);
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            onTime(reader.GetInt64(0));
        }
    }

    /// <summary>A document event's resource as a uuid, or null (the CASE keeps the cast from failing on other ids).</summary>
    private const string DocumentResourceId =
        "(CASE WHEN e.resource_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN e.resource_id::uuid END)";

    private static string Select(AuditQuery query) => query.DocumentVisibility is null
        ? $"""
          SELECT {Prefixed}, CASE WHEN @restricted THEN e.restricted_details::text END, e.recorded_at, e.sequence, e.sealed_at, NULL::text
          FROM audit.audit_event e
          """
        : $"""
          SELECT {Prefixed}, CASE WHEN @restricted THEN e.restricted_details::text END, e.recorded_at, e.sequence, e.sealed_at, doc.control_number
          FROM audit.audit_event e
          LEFT JOIN LATERAL (
              SELECT d.control_number FROM opportunity.document d
              WHERE e.resource_type = 'Document' AND d.workspace_id = e.workspace_id AND d.document_id = {DocumentResourceId}
          ) doc ON true
          """;

    private static StoredAuditEvent ReadWithDocument(NpgsqlDataReader reader) =>
        Read(reader) with { DocumentControlNumber = reader.IsDBNull(29) ? null : reader.GetString(29) };

    /// <summary>The WHERE clauses of the filters that are set (only those, so each filter shape gets its own plan).</summary>
    private static List<string> Filter(NpgsqlCommand command, AuditQuery query)
    {
        var where = new List<string>();
        var p = command.Parameters;
        void Add(string clause, string name, object value)
        {
            where.Add(clause);
            p.AddWithValue(name, value);
        }

        if (query.WorkspaceId is { } ws)
        {
            Add("e.workspace_id = @ws", "ws", ws);
        }
        else
        {
            where.Add("e.workspace_id IS NULL");
        }

        if (query.From is { } from)
        {
            Add("e.occurred_at >= @from", "from", from.ToUniversalTime());
        }

        if (query.To is { } to)
        {
            Add("e.occurred_at < @to", "to", to.ToUniversalTime());
        }

        if (query.Category is { } category)
        {
            Add("e.category = @category", "category", category);
        }

        if (query.Action is { } action)
        {
            Add("e.action = @action", "action", action);
        }

        if (query.ActorId is { } actor)
        {
            Add("e.actor_id = @actor", "actor", actor);
        }

        if (query.ResourceType is { } resourceType)
        {
            Add("e.resource_type = @resource_type", "resource_type", resourceType);
        }

        if (query.ResourceId is { } resourceId)
        {
            Add("e.resource_id = @resource_id", "resource_id", resourceId);
        }

        if (query.CorrelationId is { } correlation)
        {
            Add("e.correlation_id = @correlation", "correlation", correlation);
        }

        if (query.Outcome is { } outcome)
        {
            Add("e.outcome = @outcome", "outcome", outcome.ToString());
        }

        if (query.JobId is { } job)
        {
            Add("(e.job_id = @job OR (e.resource_type = 'Job' AND e.resource_id = @job_text))", "job", job);
            p.AddWithValue("job_text", job.ToString());
        }

        // ADR-013 §8.3: events about a document the reader may not see are left out entirely.
        if (query.DocumentVisibility is { HidesAny: true } visibility)
        {
            if (visibility.HideAllDocuments)
            {
                where.Add("e.resource_type IS DISTINCT FROM 'Document'");
            }
            else
            {
                where.Add(
                    $"""
                    (e.resource_type IS DISTINCT FROM 'Document'
                     OR (NOT EXISTS (SELECT FROM opportunity.document_restriction r
                                     WHERE r.workspace_id = e.workspace_id AND r.document_id = {DocumentResourceId} AND r.class_key = ANY (@denied))
                         AND NOT EXISTS (SELECT FROM opportunity.document_wall w
                                         WHERE w.workspace_id = e.workspace_id AND w.document_id = {DocumentResourceId} AND w.wall_id = ANY (@walls))))
                    """);
                p.AddWithValue("denied", visibility.DeniedClasses.ToArray());
                p.AddWithValue("walls", visibility.WallIds.ToArray());
            }
        }

        return where;
    }

    /// <summary>The request's correlation (the API tags the current activity), else its trace ID, else a new ID.</summary>
    private static string AmbientCorrelationId() =>
        Activity.Current?.GetTagItem(TelemetryAttributes.CorrelationId) as string
        ?? (Activity.Current is { } activity ? activity.TraceId.ToHexString() : Guid.CreateVersion7().ToString("N"));

    private static StoredAuditEvent Read(NpgsqlDataReader r)
    {
        var e = new AuditEvent
        {
            EventId = r.GetGuid(0),
            SchemaVersion = r.GetInt16(1),
            WorkspaceId = NullableGuid(r, 2),
            OccurredAt = r.GetFieldValue<DateTimeOffset>(3),
            Category = r.GetString(4),
            Action = r.GetString(5),
            ActorType = Enum.Parse<AuditActorType>(r.GetString(6)),
            ActorId = r.GetString(7),
            ActorDisplay = r.GetString(8),
            OnBehalfOf = NullableGuid(r, 9),
            AccessPath = Enum.Parse<AuditAccessPath>(r.GetString(10)),
            ClientIp = r.IsDBNull(11) ? null : r.GetFieldValue<IPAddress>(11).ToString(),
            UserAgent = NullableString(r, 12),
            SessionIdHash = r.IsDBNull(13) ? null : r.GetFieldValue<byte[]>(13),
            ResourceType = NullableString(r, 14),
            ResourceId = NullableString(r, 15),
            Outcome = Enum.Parse<AuditOutcome>(r.GetString(16)),
            ReasonCode = NullableString(r, 17),
            CorrelationId = r.GetString(18),
            CausationId = NullableString(r, 19),
            JobId = NullableGuid(r, 20),
            ChunkSequence = r.IsDBNull(21) ? null : r.GetInt32(21),
            SnapshotId = NullableGuid(r, 22),
            SearchGeneration = r.IsDBNull(23) ? null : r.GetInt64(23),
            Details = JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(24), Json) ?? [],
            RestrictedDetails = r.IsDBNull(25) ? null : JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(25), Json),
        };
        return new StoredAuditEvent(
            e,
            r.GetFieldValue<DateTimeOffset>(26),
            r.IsDBNull(27) ? null : r.GetInt64(27),
            r.IsDBNull(28) ? null : r.GetFieldValue<DateTimeOffset>(28));
    }

    private static Guid? NullableGuid(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };
}
