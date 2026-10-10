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
        await using var command = tx.Command(
            $"""
            SELECT {Columns}, CASE WHEN @restricted THEN restricted_details::text END, recorded_at, sequence, sealed_at
            FROM audit.audit_event
            WHERE workspace_id IS NOT DISTINCT FROM @ws
              AND (@from::timestamptz IS NULL OR occurred_at >= @from)
              AND (@to::timestamptz IS NULL OR occurred_at < @to)
              AND (@category::text IS NULL OR category = @category)
              AND (@action::text IS NULL OR action = @action)
              AND (@actor::text IS NULL OR actor_id = @actor)
              AND (@resource_type::text IS NULL OR resource_type = @resource_type)
              AND (@resource_id::text IS NULL OR resource_id = @resource_id)
              AND (@correlation::text IS NULL OR correlation_id = @correlation)
              AND (@after_at::timestamptz IS NULL OR (occurred_at, event_id) > (@after_at, @after_id))
            ORDER BY occurred_at, event_id
            LIMIT @limit
            """);
        var p = command.Parameters;
        p.AddWithValue("restricted", query.IncludeRestrictedDetails);
        p.Add(Nullable("ws", NpgsqlDbType.Uuid, query.WorkspaceId));
        p.Add(Nullable("from", NpgsqlDbType.TimestampTz, query.From?.ToUniversalTime()));
        p.Add(Nullable("to", NpgsqlDbType.TimestampTz, query.To?.ToUniversalTime()));
        p.Add(Nullable("category", NpgsqlDbType.Text, query.Category));
        p.Add(Nullable("action", NpgsqlDbType.Text, query.Action));
        p.Add(Nullable("actor", NpgsqlDbType.Text, query.ActorId));
        p.Add(Nullable("resource_type", NpgsqlDbType.Text, query.ResourceType));
        p.Add(Nullable("resource_id", NpgsqlDbType.Text, query.ResourceId));
        p.Add(Nullable("correlation", NpgsqlDbType.Text, query.CorrelationId));
        p.Add(Nullable("after_at", NpgsqlDbType.TimestampTz, query.After?.OccurredAt.ToUniversalTime()));
        p.Add(Nullable("after_id", NpgsqlDbType.Uuid, query.After?.EventId));
        p.AddWithValue("limit", query.Limit + 1);

        var events = new List<StoredAuditEvent>(Math.Min(query.Limit + 1, 256));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(Read(reader));
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
