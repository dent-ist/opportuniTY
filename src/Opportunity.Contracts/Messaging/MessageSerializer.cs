using System.Globalization;
using System.Text.Json;

namespace Opportunity.Contracts.Messaging;

public enum MessageReadStatus
{
    /// <summary>Envelope and payload decoded; the payload is upgraded to the current contract.</summary>
    Ok,

    /// <summary>Not an envelope (bad JSON, missing or ill-typed §11 field): dead-letter.</summary>
    Malformed,

    /// <summary>No contract for <c>messageType</c> in this process: park, never ack silently.</summary>
    UnknownMessageType,

    /// <summary>Major other than N or N-1: park, never ack silently (ADR-019 §3.5).</summary>
    UnsupportedSchemaVersion,

    /// <summary>The payload does not satisfy its contract (missing required member, unknown enum value): dead-letter.</summary>
    InvalidPayload,
}

/// <summary>Result of <see cref="MessageSerializer.Read"/>; <see cref="Envelope"/> is set unless the status is Malformed.</summary>
public sealed record MessageReadResult(
    MessageReadStatus Status,
    MessageEnvelope? Envelope = null,
    object? Payload = null,
    SupportedMessageContract? Contract = null,
    string? Error = null)
{
    public bool IsOk => Status == MessageReadStatus.Ok;
}

/// <summary>A payload encoded against its current contract, ready to put into an envelope.</summary>
public sealed record EncodedPayload(string MessageType, SchemaVersion SchemaVersion, JsonElement Payload);

/// <summary>
/// Writes and reads the JSON envelope (ADR-019 §3). The writer is canonical (fixed property order, headers sorted,
/// <c>createdAt</c> as <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>) so golden samples are byte-comparable; the reader is
/// tolerant: unknown members are ignored, missing optional members take defaults, newer minors are accepted, and the
/// previous major is upgraded to the current one.
/// </summary>
public sealed class MessageSerializer(MessageTypeRegistry registry)
{
    public const string ContentType = "application/json";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = false };

    public MessageTypeRegistry Registry { get; } = registry ?? throw new ArgumentNullException(nameof(registry));

    public EncodedPayload EncodePayload<TPayload>(TPayload payload)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(payload);
        var contract = Registry.Describe(payload.GetType());
        var element = JsonSerializer.SerializeToElement(payload, contract.PayloadType, MessageJson.PayloadOptions);
        return new EncodedPayload(contract.MessageType, contract.Version, element);
    }

    public static byte[] Serialize(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Write(writer, envelope);
        }

        return buffer.ToArray();
    }

    public static void Write(Utf8JsonWriter writer, MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(envelope);

        writer.WriteStartObject();
        writer.WriteString(Fields.MessageId, envelope.MessageId);
        writer.WriteString(Fields.MessageType, envelope.MessageType);
        writer.WriteString(Fields.SchemaVersion, envelope.SchemaVersion.ToString());
        WriteGuid(writer, Fields.WorkspaceId, envelope.WorkspaceId);
        WriteGuid(writer, Fields.JobId, envelope.JobId);
        writer.WriteString(Fields.CorrelationId, envelope.CorrelationId);
        writer.WriteString(Fields.CausationId, envelope.CausationId);
        writer.WriteString(Fields.IdempotencyKey, envelope.IdempotencyKey);
        writer.WriteString(Fields.CreatedAt, FormatTimestamp(envelope.CreatedAt));
        writer.WriteNumber(Fields.Attempt, envelope.Attempt);
        writer.WriteStartObject(Fields.Headers);
        foreach (var (key, value) in envelope.Headers.OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            writer.WriteString(key, value);
        }

        writer.WriteEndObject();
        writer.WritePropertyName(Fields.Payload);
        envelope.Payload.WriteTo(writer);
        writer.WriteEndObject();
    }

    public MessageReadResult Read(ReadOnlyMemory<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            return new MessageReadResult(MessageReadStatus.Malformed, Error: $"Body is not JSON: {ex.Message}");
        }

        using (document)
        {
            if (!TryReadEnvelope(document.RootElement, out var envelope, out var error))
            {
                return new MessageReadResult(MessageReadStatus.Malformed, Error: error);
            }

            return DecodePayload(envelope);
        }
    }

    /// <summary>Decodes <paramref name="envelope"/>'s payload against the registered contracts.</summary>
    public MessageReadResult DecodePayload(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!Registry.IsKnown(envelope.MessageType))
        {
            return new MessageReadResult(
                MessageReadStatus.UnknownMessageType, envelope, Error: $"Unknown message type '{envelope.MessageType}'.");
        }

        if (!Registry.TryResolve(envelope.MessageType, envelope.SchemaVersion, out var contract))
        {
            return new MessageReadResult(
                MessageReadStatus.UnsupportedSchemaVersion,
                envelope,
                Error: $"'{envelope.MessageType}' schema version {envelope.SchemaVersion} is not supported by this reader.");
        }

        object? payload;
        try
        {
            payload = envelope.Payload.Deserialize(contract.Wire.PayloadType, MessageJson.PayloadOptions);
        }
        catch (JsonException ex)
        {
            return new MessageReadResult(MessageReadStatus.InvalidPayload, envelope, Contract: contract, Error: ex.Message);
        }

        if (payload is null)
        {
            return new MessageReadResult(MessageReadStatus.InvalidPayload, envelope, Contract: contract, Error: "Payload is null.");
        }

        if (contract.Upgrade is not null)
        {
            payload = contract.Upgrade(payload);
        }

        return new MessageReadResult(MessageReadStatus.Ok, envelope, payload, contract);
    }

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static bool TryReadEnvelope(JsonElement root, out MessageEnvelope envelope, out string? error)
    {
        envelope = null!;
        error = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Envelope must be a JSON object.";
            return false;
        }

        if (!TryGetGuid(root, Fields.MessageId, required: true, out var messageId, ref error)
            || !TryGetString(root, Fields.MessageType, required: true, out var messageType, ref error)
            || !TryGetString(root, Fields.SchemaVersion, required: true, out var schemaVersionText, ref error)
            || !TryGetGuid(root, Fields.WorkspaceId, required: false, out var workspaceId, ref error)
            || !TryGetGuid(root, Fields.JobId, required: false, out var jobId, ref error)
            || !TryGetString(root, Fields.CorrelationId, required: true, out var correlationId, ref error)
            || !TryGetString(root, Fields.CausationId, required: false, out var causationId, ref error)
            || !TryGetString(root, Fields.IdempotencyKey, required: true, out var idempotencyKey, ref error)
            || !TryGetString(root, Fields.CreatedAt, required: true, out var createdAtText, ref error))
        {
            return false;
        }

        if (!SchemaVersion.TryParse(schemaVersionText, out var schemaVersion))
        {
            error = $"'{Fields.SchemaVersion}' must be 'major.minor'.";
            return false;
        }

        if (!DateTimeOffset.TryParse(
                createdAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdAt))
        {
            error = $"'{Fields.CreatedAt}' must be an ISO-8601 timestamp.";
            return false;
        }

        var attempt = 0;
        if (root.TryGetProperty(Fields.Attempt, out var attemptElement) && attemptElement.ValueKind != JsonValueKind.Null
            && (attemptElement.ValueKind != JsonValueKind.Number || !attemptElement.TryGetInt32(out attempt) || attempt < 0))
        {
            error = $"'{Fields.Attempt}' must be a non-negative integer.";
            return false;
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty(Fields.Headers, out var headersElement) && headersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headersElement.EnumerateObject())
            {
                // Tolerant: only string-valued headers are meaningful; anything else is ignored.
                if (header.Value.ValueKind == JsonValueKind.String)
                {
                    headers[header.Name] = header.Value.GetString()!;
                }
            }
        }

        if (!root.TryGetProperty(Fields.Payload, out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            error = $"'{Fields.Payload}' must be a JSON object.";
            return false;
        }

        envelope = new MessageEnvelope
        {
            MessageId = messageId!.Value,
            MessageType = messageType!,
            SchemaVersion = schemaVersion,
            WorkspaceId = workspaceId,
            JobId = jobId,
            CorrelationId = correlationId!,
            CausationId = causationId,
            IdempotencyKey = idempotencyKey!,
            CreatedAt = createdAt,
            Attempt = attempt,
            Headers = headers,
            Payload = payload.Clone(),
        };
        return true;
    }

    private static bool TryGetString(JsonElement root, string name, bool required, out string? value, ref string? error)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                error = $"'{name}' is required.";
                return false;
            }

            return true;
        }

        if (element.ValueKind != JsonValueKind.String || (required && element.GetString()!.Length == 0))
        {
            error = $"'{name}' must be a non-empty string.";
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryGetGuid(JsonElement root, string name, bool required, out Guid? value, ref string? error)
    {
        value = null;
        if (!TryGetString(root, name, required, out var text, ref error))
        {
            return false;
        }

        if (text is null)
        {
            return true;
        }

        if (!Guid.TryParse(text, out var parsed))
        {
            error = $"'{name}' must be a UUID.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static void WriteGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } guid)
        {
            writer.WriteString(name, guid);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    /// <summary>Envelope property names (ADR-019 §3.1).</summary>
    public static class Fields
    {
        public const string MessageId = "messageId";
        public const string MessageType = "messageType";
        public const string SchemaVersion = "schemaVersion";
        public const string WorkspaceId = "workspaceId";
        public const string JobId = "jobId";
        public const string CorrelationId = "correlationId";
        public const string CausationId = "causationId";
        public const string IdempotencyKey = "idempotencyKey";
        public const string CreatedAt = "createdAt";
        public const string Attempt = "attempt";
        public const string Headers = "headers";
        public const string Payload = "payload";
    }
}
