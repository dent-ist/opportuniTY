using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging;

using RabbitMQ.Client;

namespace Opportunity.Messaging;

/// <summary>
/// Turns a dead-lettered delivery into a <see cref="DeadLetterMessage"/> (ADR-010 §7.3). Tolerant by design: the body
/// may be anything (malformed envelopes are a reason to dead-letter), so the envelope is read field by field and every
/// missing piece falls back. The workspace and job the envelope names only decide where the record is kept; nothing
/// here is trusted for authorization.
/// </summary>
public static class DeadLetterMessageReader
{
    /// <summary>Longest message id kept as is; longer ids are replaced by a hash.</summary>
    public const int MaxMessageIdLength = 64;

    /// <summary>Headers JSON above this size keeps only the envelope's routing fields (the record's limit is 64 KiB).</summary>
    public const int MaxHeadersJsonLength = 32 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <param name="exchange">The dead-letter exchange the delivery came through.</param>
    /// <param name="routingKey">Its routing key: the work queue's name, or <c>parking</c>.</param>
    /// <param name="messageId">The AMQP <c>message-id</c> property, if any.</param>
    /// <param name="headers">The AMQP headers as the RabbitMQ client decodes them.</param>
    /// <param name="maxBodyBytes">The body is cut to this many bytes.</param>
    public static DeadLetterMessage Read(
        string exchange, string routingKey, string? messageId, IDictionary<string, object?>? headers, ReadOnlyMemory<byte> body,
        int maxBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(routingKey);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBodyBytes);

        var envelope = EnvelopeFields.Read(body);
        var death = FirstDeath(headers);
        var queue = TransportHeaders.ReadString(headers, TransportHeaders.Queue)
            ?? death?.Queue
            ?? TransportHeaders.ReadString(headers, "x-first-death-queue")
            ?? routingKey;
        var reason = TransportHeaders.ReadString(headers, TransportHeaders.FailureReason)
            ?? TransportHeaders.ReadString(headers, "x-last-death-reason")
            ?? death?.Reason
            ?? "unknown";

        return new DeadLetterMessage(
            NormalizeId(messageId) ?? NormalizeId(envelope.MessageId) ?? "body-" + Hash(body.Span),
            queue,
            exchange,
            routingKey,
            envelope.WorkspaceId is { } ws && ws != Guid.Empty ? ws : null,
            envelope.JobId is { } job && job != Guid.Empty ? job : null,
            envelope.SubjectId is { } subject && subject != Guid.Empty ? subject : null,
            envelope.MessageType,
            envelope.CorrelationId,
            reason,
            (int)Math.Clamp(death?.Count ?? 1, 1, int.MaxValue),
            TransportHeaders.ReadString(headers, TransportHeaders.ErrorType),
            TransportHeaders.ReadString(headers, TransportHeaders.Error),
            death?.Time,
            HeadersJson(headers, envelope) is { Length: <= MaxHeadersJsonLength } json ? json : TruncatedHeadersJson(envelope),
            body.Length > maxBodyBytes ? body[..maxBodyBytes] : body,
            body.Length);
    }

    private static string? NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        id = id.Trim();
        return id.Length <= MaxMessageIdLength && id.All(c => c is > ' ' and < (char)0x7f)
            ? id
            : "id-" + Hash(Encoding.UTF8.GetBytes(id));
    }

    /// <summary>128 bits of SHA-256 as lowercase hex.</summary>
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes)[..16]);

    /// <summary>The newest entry of <c>x-death</c> (the broker puts the latest death first).</summary>
    private static Death? FirstDeath(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue("x-death", out var value) || value is not IList { Count: > 0 } deaths
            || deaths[0] is not IDictionary<string, object?> entry)
        {
            return null;
        }

        var count = entry.TryGetValue("count", out var c) ? c switch { long l => l, int i => i, _ => 1L } : 1L;
        DateTimeOffset? time = entry.TryGetValue("time", out var t) && t is AmqpTimestamp ts
            ? DateTimeOffset.FromUnixTimeSeconds(ts.UnixTime)
            : null;
        return new Death(TransportHeaders.ReadString(entry, "queue"), TransportHeaders.ReadString(entry, "reason"), count, time);
    }

    private static string HeadersJson(IDictionary<string, object?>? headers, EnvelopeFields envelope)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("amqp");
            foreach (var (key, value) in (headers ?? new Dictionary<string, object?>()).OrderBy(h => h.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                WriteValue(writer, value, depth: 0);
            }

            writer.WriteEndObject();
            writer.WritePropertyName("envelope");
            if (envelope.Fields is { } fields)
            {
                fields.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string TruncatedHeadersJson(EnvelopeFields envelope)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("truncated", true);
            writer.WriteStartObject("envelope");
            writer.WriteString("messageId", Cap(envelope.MessageId));
            writer.WriteString("messageType", Cap(envelope.MessageType));
            writer.WriteString("correlationId", Cap(envelope.CorrelationId));
            writer.WriteString("workspaceId", envelope.WorkspaceId?.ToString());
            writer.WriteString("jobId", envelope.JobId?.ToString());
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());

        static string? Cap(string? value) => value is { Length: > 255 } ? value[..255] : value;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, int depth)
    {
        if (depth > 8)
        {
            writer.WriteStringValue("…");
            return;
        }

        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case byte[] bytes:
                writer.WriteStringValue(Encoding.UTF8.GetString(bytes));
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case AmqpTimestamp timestamp:
                writer.WriteStringValue(MessageSerializer.FormatTimestamp(DateTimeOffset.FromUnixTimeSeconds(timestamp.UnixTime)));
                break;
            case IDictionary<string, object?> table:
                writer.WriteStartObject();
                foreach (var (key, item) in table.OrderBy(h => h.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item, depth + 1);
                }

                writer.WriteEndObject();
                break;
            case IList list:
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    WriteValue(writer, item, depth + 1);
                }

                writer.WriteEndArray();
                break;
            case sbyte or byte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private sealed record Death(string? Queue, string? Reason, long Count, DateTimeOffset? Time);

    /// <summary>The envelope's routing fields, read leniently; <see cref="Fields"/> is the envelope without its payload.</summary>
    private sealed record EnvelopeFields(
        string? MessageId, string? MessageType, Guid? WorkspaceId, Guid? JobId, Guid? SubjectId, string? CorrelationId, JsonElement? Fields)
    {
        private static readonly EnvelopeFields None = new(null, null, null, null, null, null, null);

        public static EnvelopeFields Read(ReadOnlyMemory<byte> body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return None;
                }

                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
                {
                    writer.WriteStartObject();
                    foreach (var property in root.EnumerateObject().Where(p => p.Name != "payload"))
                    {
                        property.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }

                using var fields = JsonDocument.Parse(buffer.ToArray());
                var payload = root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
                var subject = payload.ValueKind == JsonValueKind.Object ? Id(payload, "chunkId") ?? Id(payload, "taskId") : null;
                return new EnvelopeFields(
                    Text(root, "messageId"), Text(root, "messageType"), Id(root, "workspaceId"), Id(root, "jobId"), subject,
                    Text(root, "correlationId"), fields.RootElement.Clone());
            }
            catch (JsonException)
            {
                return None;
            }
        }

        private static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static Guid? Id(JsonElement root, string name) =>
            Guid.TryParse(Text(root, name), out var id) ? id : null;
    }
}
