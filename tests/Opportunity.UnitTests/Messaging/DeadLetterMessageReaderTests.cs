using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Messaging;

using RabbitMQ.Client;

namespace Opportunity.UnitTests.Messaging;

/// <summary>The dead-letter recorder reads whatever reached a DLQ (ADR-010 §7.3), including bodies that are no envelope.</summary>
public sealed class DeadLetterMessageReaderTests
{
    private static readonly Guid Workspace = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly Guid Job = Guid.Parse("0199a000-0000-7000-8000-000000000002");
    private static readonly Guid MessageId = Guid.Parse("0199a000-0000-7000-8000-000000000003");
    private static readonly Guid Chunk = Guid.Parse("0199a000-0000-7000-8000-000000000004");

    private static byte[] Envelope(string? workspaceId = null, string? payload = null) => Encoding.UTF8.GetBytes(
        $$"""
        {"messageId":"{{MessageId}}","messageType":"job.chunk","schemaVersion":"1.0","workspaceId":"{{workspaceId ?? Workspace.ToString()}}",
         "jobId":"{{Job}}","correlationId":"corr-1","idempotencyKey":"k","createdAt":"2026-10-04T09:00:00.000Z","attempt":0,
         "headers":{"traceparent":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"},"payload":{{payload ?? $"{{\"chunkId\":\"{Chunk}\"}}"}}}
        """);

    [Fact]
    public void A_permanent_failure_published_by_the_consumer_names_its_queue_reason_and_error()
    {
        var headers = new Dictionary<string, object?>
        {
            [TransportHeaders.Queue] = "import.chunks"u8.ToArray(),
            [TransportHeaders.FailureReason] = "permanent"u8.ToArray(),
            [TransportHeaders.ErrorType] = "Opportunity.Application.Messaging.PermanentMessageException"u8.ToArray(),
            [TransportHeaders.Error] = "Envelope workspace differs from the chunk's"u8.ToArray(),
            [TransportHeaders.TransportRetry] = 0,
        };

        var message = DeadLetterMessageReader.Read("import.dlx", "import.chunks", MessageId.ToString(), headers, Envelope(), 65_536);

        message.MessageId.Should().Be(MessageId.ToString());
        message.Queue.Should().Be("import.chunks");
        message.Exchange.Should().Be("import.dlx");
        message.RoutingKey.Should().Be("import.chunks");
        message.WorkspaceId.Should().Be(Workspace);
        message.JobId.Should().Be(Job);
        message.SubjectId.Should().Be(Chunk, "the payload's chunk id ties the record to the failed chunk");
        message.MessageType.Should().Be("job.chunk");
        message.CorrelationId.Should().Be("corr-1");
        message.DeathReason.Should().Be("permanent");
        message.DeathCount.Should().Be(1);
        message.ErrorType.Should().EndWith("PermanentMessageException");
        message.Error.Should().Be("Envelope workspace differs from the chunk's");

        using var json = JsonDocument.Parse(message.HeadersJson);
        json.RootElement.GetProperty("amqp").GetProperty(TransportHeaders.FailureReason).GetString().Should().Be("permanent");
        var envelope = json.RootElement.GetProperty("envelope");
        envelope.GetProperty("headers").GetProperty("traceparent").GetString().Should().StartWith("00-");
        envelope.TryGetProperty("payload", out _).Should().BeFalse("the payload is in the body, not repeated in the headers");
    }

    [Fact]
    public void A_broker_dead_letter_takes_reason_count_queue_and_time_from_x_death()
    {
        var death = new Dictionary<string, object?>
        {
            ["count"] = 5L,
            ["reason"] = "delivery_limit"u8.ToArray(),
            ["queue"] = "index.bulk"u8.ToArray(),
            ["exchange"] = "opportunity.work"u8.ToArray(),
            ["routing-keys"] = new List<object?> { "index.bulk"u8.ToArray() },
            ["time"] = new AmqpTimestamp(1_790_000_000),
        };
        var headers = new Dictionary<string, object?> { ["x-death"] = new List<object?> { death } };

        var message = DeadLetterMessageReader.Read("index.dlx", "index.bulk", null, headers, Envelope(), 65_536);

        message.DeathReason.Should().Be("delivery_limit");
        message.DeathCount.Should().Be(5);
        message.Queue.Should().Be("index.bulk");
        message.FirstDeathAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000));
        message.MessageId.Should().Be(MessageId.ToString(), "without an AMQP message id the envelope's is used");
        using var json = JsonDocument.Parse(message.HeadersJson);
        var recorded = json.RootElement.GetProperty("amqp").GetProperty("x-death")[0];
        recorded.GetProperty("count").GetInt64().Should().Be(5);
        recorded.GetProperty("routing-keys")[0].GetString().Should().Be("index.bulk");
        recorded.GetProperty("time").GetString().Should().StartWith("2026-");
    }

    [Fact]
    public void A_body_that_is_no_envelope_is_recorded_at_installation_level_under_a_stable_hash_id()
    {
        var body = "not an envelope"u8.ToArray();
        var headers = new Dictionary<string, object?> { [TransportHeaders.FailureReason] = "malformed"u8.ToArray() };

        var first = DeadLetterMessageReader.Read("index.dlx", "index.bulk", null, headers, body, 65_536);
        var again = DeadLetterMessageReader.Read("index.dlx", "index.bulk", "", headers, body, 65_536);

        first.WorkspaceId.Should().BeNull();
        first.JobId.Should().BeNull();
        first.MessageId.Should().StartWith("body-").And.HaveLength(5 + 32).And.Be(again.MessageId, "a redelivery must hit the same record");
        first.Queue.Should().Be("index.bulk", "without headers the routing key names the queue");
        first.DeathReason.Should().Be("malformed");
        JsonDocument.Parse(first.HeadersJson).RootElement.GetProperty("envelope").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Bodies_are_cut_to_the_limit_and_keep_their_full_size()
    {
        var body = Envelope(payload: $$"""{"text":"{{new string('x', 10_000)}}"}""");

        var message = DeadLetterMessageReader.Read("index.dlx", "index.bulk", null, null, body, 1_024);

        message.Body.Length.Should().Be(1_024);
        message.BodySize.Should().Be(body.Length);
        message.WorkspaceId.Should().Be(Workspace, "the envelope is read from the whole body");
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    public void An_empty_or_invalid_workspace_id_is_no_workspace(string workspaceId)
    {
        DeadLetterMessageReader.Read("index.dlx", "index.bulk", null, null, Envelope(workspaceId), 65_536).WorkspaceId.Should().BeNull();
    }

    [Fact]
    public void Long_or_unprintable_message_ids_are_replaced_by_a_hash()
    {
        var longId = new string('a', 65);
        var message = DeadLetterMessageReader.Read("index.dlx", "index.bulk", longId, null, Envelope(), 65_536);

        message.MessageId.Should().StartWith("id-").And.HaveLength(3 + 32);
        DeadLetterMessageReader.Read("index.dlx", "index.bulk", "a b", null, Envelope(), 65_536).MessageId.Should().StartWith("id-");
        DeadLetterMessageReader.Read("index.dlx", "index.bulk", longId, null, Envelope(), 65_536).MessageId.Should().Be(message.MessageId);
    }

    [Fact]
    public void Oversized_headers_keep_only_the_envelope_routing_fields()
    {
        var headers = new Dictionary<string, object?> { ["x-huge"] = Encoding.UTF8.GetBytes(new string('h', 40_000)) };

        var message = DeadLetterMessageReader.Read("index.dlx", "index.bulk", null, headers, Envelope(), 65_536);

        message.HeadersJson.Length.Should().BeLessThan(DeadLetterMessageReader.MaxHeadersJsonLength);
        using var json = JsonDocument.Parse(message.HeadersJson);
        json.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("envelope").GetProperty("jobId").GetString().Should().Be(Job.ToString());
    }
}
