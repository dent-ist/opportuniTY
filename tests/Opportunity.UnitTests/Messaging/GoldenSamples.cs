using System.Text.Json;

using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Contracts.Messaging.Jobs;

namespace Opportunity.UnitTests.Messaging;

/// <summary>
/// The sample payload behind each golden file (<c>Golden/{messageType}.v{major}.json</c>). When a contract gets a new
/// major, add its sample here and keep the previous major's sample and file: CI then proves N and N-1 are readable.
/// Golden files are never edited to make a test pass except when a contract change is intended and reviewed.
/// </summary>
internal static class GoldenSamples
{
    public static readonly Guid MessageId = Guid.Parse("01923f6e-4c1a-7b2e-9a3d-5e6f7a8b9c0d");
    public static readonly Guid WorkspaceId = Guid.Parse("6f1c2b3a-4d5e-4f60-8a9b-0c1d2e3f4a5b");
    public static readonly Guid JobId = Guid.Parse("0b6c7d8e-9f01-4a23-b456-789abcdef012");
    public static readonly DateTimeOffset CreatedAt = new(2026, 10, 2, 14, 3, 22, 123, TimeSpan.Zero);

    // Low-entropy placeholder in the shape of an ADR-010 §5.1 key (64 lowercase hex characters).
    public const string IdempotencyKey = "aaaaaaaabbbbbbbbccccccccddddddddeeeeeeeeffffffff0000000011111111";

    // The W3C Trace Context specification's example traceparent.
    public const string TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    /// <summary>Sample payload per (messageType, major).</summary>
    public static IReadOnlyDictionary<(string MessageType, int Major), object> Payloads { get; } =
        new Dictionary<(string, int), object>
        {
            [(MessageTypes.SearchOutbox, 1)] = new SearchOutboxMessage
            {
                OutboxId = 4711,
                DocumentId = Guid.Parse("3d2c1b0a-9f8e-4d7c-8b6a-594837261504"),
                DocumentVersion = 3,
            },
            [(MessageTypes.IndexChunkTask, 1)] = new IndexChunkTaskMessage
            {
                TaskId = Guid.Parse("7a6b5c4d-3e2f-4a1b-9c0d-e1f2a3b4c5d6"),
            },
            [(MessageTypes.JobChunk, 1)] = new JobChunkMessage
            {
                ChunkId = Guid.Parse("5e4d3c2b-1a09-4f8e-b7d6-c5b4a3928170"),
                Sequence = 7,
                Operation = JobChunkOperation.BulkCodingChunk,
            },
        };

    public static string FileName(string messageType, int major) => $"{messageType}.v{major}.json";

    public static string Path(string fileName) => System.IO.Path.Combine(AppContext.BaseDirectory, "Messaging", "Golden", fileName);

    /// <summary>An envelope with every §11 field set, around <paramref name="payload"/>.</summary>
    public static MessageEnvelope Envelope(string messageType, SchemaVersion version, JsonElement payload) => new()
    {
        MessageId = MessageId,
        MessageType = messageType,
        SchemaVersion = version,
        WorkspaceId = WorkspaceId,
        JobId = JobId,
        CorrelationId = "req-7c9e6679",
        CausationId = "01923f6e-0000-7000-8000-000000000001",
        IdempotencyKey = IdempotencyKey,
        CreatedAt = CreatedAt,
        Attempt = 1,
        Headers = new Dictionary<string, string> { [MessageHeaderNames.TraceParent] = TraceParent },
        Payload = payload,
    };

    /// <summary>Compact JSON with the property order kept, for comparing an indented golden file with writer output.</summary>
    public static string Canonical(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            document.RootElement.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
