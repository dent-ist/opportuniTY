using System.Globalization;
using System.Text;

namespace Opportunity.Messaging;

/// <summary>
/// AMQP headers the transport adds; the envelope body is never changed on retry, dead-letter or parking, so the
/// original envelope (incl. its headers and trace context) is what lands in the DLQ.
/// </summary>
public static class TransportHeaders
{
    /// <summary>The work queue the message was consumed from; routes retries back to it.</summary>
    public const string Queue = "opportunity-queue";

    /// <summary>Transport retries so far (0 on the first delivery).</summary>
    public const string TransportRetry = "opportunity-transport-retry";

    /// <summary>Why the message was dead-lettered or parked (<see cref="FailureReasons"/>).</summary>
    public const string FailureReason = "opportunity-failure-reason";

    /// <summary>Exception type of the last failure.</summary>
    public const string ErrorType = "opportunity-error-type";

    /// <summary>Last error message (truncated).</summary>
    public const string Error = "opportunity-error";

    /// <summary>UTC time of the last failure.</summary>
    public const string FailedAt = "opportunity-failed-at";

    /// <summary>Key id of the envelope HMAC (E05-T07, <see cref="EnvelopeSigner"/>).</summary>
    public const string SignatureKeyId = "opportunity-signature-kid";

    /// <summary>The envelope HMAC, <c>v1.</c> + base64 (<see cref="EnvelopeSigner"/>).</summary>
    public const string Signature = "opportunity-signature";

    /// <summary>Quorum queues count failed deliveries here (RabbitMQ 4).</summary>
    public const string DeliveryCount = "x-delivery-count";

    internal const int MaxErrorLength = 2_000;

    internal static int ReadInt(IDictionary<string, object?>? headers, string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var value))
        {
            return 0;
        }

        return value switch
        {
            int i => i,
            long l => (int)Math.Clamp(l, 0, int.MaxValue),
            short s => s,
            byte b => b,
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            string text when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    internal static string? ReadString(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                null => null,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            }
            : null;
}

public static class FailureReasons
{
    public const string Malformed = "malformed";
    public const string InvalidPayload = "invalid-payload";
    public const string Permanent = "permanent";
    public const string RetriesExhausted = "retries-exhausted";
    public const string UnknownMessageType = "unknown-message-type";
    public const string UnsupportedSchemaVersion = "unsupported-schema-version";

    /// <summary>E05-T07: signing is on and the message carries no signature.</summary>
    public const string SignatureMissing = "signature-missing";

    /// <summary>E05-T07: the signature does not match the envelope and destination.</summary>
    public const string SignatureInvalid = "signature-invalid";

    /// <summary>E05-T07: the signature names a key id the consumer does not accept.</summary>
    public const string SignatureKeyUnknown = "signature-key-unknown";
}
