using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Contracts.Messaging;

namespace Opportunity.UnitTests.Messaging;

[MessageContract("test.sample", 1, 0)]
public sealed record SampleV1
{
    public required Guid ItemId { get; init; }
}

[MessageContract("test.sample", 2, 1)]
public sealed record SampleV2
{
    public required Guid ItemId { get; init; }

    public required SampleKind Kind { get; init; }

    public string? Note { get; init; }
}

public enum SampleKind
{
    Plain,
    Special,
}

/// <summary>Tolerant reader and N/N-1 negotiation (ADR-019 §3.3–3.5) on a test contract with two majors.</summary>
public sealed class MessageSerializerTests
{
    private static readonly Guid ItemId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly MessageSerializer _serializer = new(new MessageTypeRegistry()
        .Register<SampleV2>()
        .RegisterPrevious<SampleV1, SampleV2>(v1 => new SampleV2 { ItemId = v1.ItemId, Kind = SampleKind.Plain }));

    [Fact]
    public void Current_major_round_trips()
    {
        var encoded = _serializer.EncodePayload(new SampleV2 { ItemId = ItemId, Kind = SampleKind.Special, Note = "n" });
        encoded.MessageType.Should().Be("test.sample");
        encoded.SchemaVersion.Should().Be(new SchemaVersion(2, 1));

        var result = _serializer.Read(MessageSerializer.Serialize(Envelope(encoded.SchemaVersion, encoded.Payload)));

        result.Status.Should().Be(MessageReadStatus.Ok);
        result.Payload.Should().Be(new SampleV2 { ItemId = ItemId, Kind = SampleKind.Special, Note = "n" });
        result.Contract!.Upgrade.Should().BeNull();
    }

    [Fact]
    public void Previous_major_is_accepted_and_upgraded()
    {
        var result = Read(new SchemaVersion(1, 0), new JsonObject { ["itemId"] = ItemId });

        result.Status.Should().Be(MessageReadStatus.Ok, result.Error);
        result.Payload.Should().Be(new SampleV2 { ItemId = ItemId, Kind = SampleKind.Plain });
        result.Contract!.Wire.PayloadType.Should().Be<SampleV1>();
    }

    [Fact]
    public void Newer_minor_with_unknown_fields_is_accepted()
    {
        var result = Read(
            new SchemaVersion(2, 7),
            new JsonObject { ["itemId"] = ItemId, ["kind"] = "plain", ["addedInMinor7"] = new JsonArray(1, 2) });

        result.Status.Should().Be(MessageReadStatus.Ok, result.Error);
        result.Payload.Should().Be(new SampleV2 { ItemId = ItemId, Kind = SampleKind.Plain });
    }

    [Fact]
    public void Missing_optional_payload_field_takes_its_default()
    {
        var result = Read(new SchemaVersion(2, 0), new JsonObject { ["itemId"] = ItemId, ["kind"] = "special" });

        result.Status.Should().Be(MessageReadStatus.Ok, result.Error);
        ((SampleV2)result.Payload!).Note.Should().BeNull();
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 2)]
    public void Newer_major_is_unsupported(int major, int minor)
    {
        Read(new SchemaVersion(major, minor), new JsonObject { ["itemId"] = ItemId })
            .Status.Should().Be(MessageReadStatus.UnsupportedSchemaVersion);
    }

    [Fact]
    public void Major_older_than_n_minus_1_is_unsupported()
    {
        var serializer = new MessageSerializer(new MessageTypeRegistry().Register<SampleV2>());

        var result = serializer.Read(MessageSerializer.Serialize(Envelope(new SchemaVersion(1, 0), Json(new JsonObject { ["itemId"] = ItemId }))));

        result.Status.Should().Be(MessageReadStatus.UnsupportedSchemaVersion);
        result.Envelope!.MessageType.Should().Be("test.sample", "parking keeps the envelope for diagnostics");
    }

    [Fact]
    public void Unknown_message_type_is_reported_with_its_envelope()
    {
        var result = _serializer.Read(MessageSerializer.Serialize(
            Envelope(new SchemaVersion(1, 0), Json(new JsonObject())) with { MessageType = "test.other" }));

        result.Status.Should().Be(MessageReadStatus.UnknownMessageType);
        result.Envelope!.MessageType.Should().Be("test.other");
    }

    [Fact]
    public void Unknown_enum_value_is_an_invalid_payload_not_a_crash()
    {
        var result = Read(new SchemaVersion(2, 3), new JsonObject { ["itemId"] = ItemId, ["kind"] = "addedLater" });

        result.Status.Should().Be(MessageReadStatus.InvalidPayload);
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Missing_required_payload_field_is_an_invalid_payload()
    {
        Read(new SchemaVersion(2, 0), new JsonObject { ["kind"] = "plain" }).Status.Should().Be(MessageReadStatus.InvalidPayload);
    }

    [Fact]
    public void Numbers_are_not_coerced_from_strings()
    {
        var serializer = new MessageSerializer(MessageContracts.CreateRegistry());
        var payload = Json(new JsonObject
        {
            ["outboxId"] = "12",
            ["documentId"] = ItemId,
            ["documentVersion"] = 1,
        });

        serializer.Read(MessageSerializer.Serialize(Envelope(new SchemaVersion(1, 0), payload) with { MessageType = MessageTypes.SearchOutbox }))
            .Status.Should().Be(MessageReadStatus.InvalidPayload);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"messageId":"x","messageType":"test.sample","schemaVersion":"2.0","correlationId":"c","idempotencyKey":"k","createdAt":"2026-01-01T00:00:00Z","payload":{}}""")]
    [InlineData("""{"messageId":"11111111-2222-4333-8444-555555555555","messageType":"test.sample","schemaVersion":"two","correlationId":"c","idempotencyKey":"k","createdAt":"2026-01-01T00:00:00Z","payload":{}}""")]
    [InlineData("""{"messageId":"11111111-2222-4333-8444-555555555555","messageType":"test.sample","schemaVersion":"2.0","correlationId":"c","idempotencyKey":"k","createdAt":"2026-01-01T00:00:00Z","payload":[]}""")]
    [InlineData("""{"messageId":"11111111-2222-4333-8444-555555555555","messageType":"test.sample","schemaVersion":"2.0","idempotencyKey":"k","createdAt":"2026-01-01T00:00:00Z","payload":{}}""")]
    public void Malformed_envelopes_are_rejected(string body)
    {
        _serializer.Read(Encoding.UTF8.GetBytes(body)).Status.Should().Be(MessageReadStatus.Malformed);
    }

    [Fact]
    public void Envelope_reader_is_tolerant_of_missing_optional_and_unknown_fields()
    {
        const string body = """
            {"messageId":"11111111-2222-4333-8444-555555555555","messageType":"test.sample","schemaVersion":"2.0",
             "correlationId":"c","idempotencyKey":"k","createdAt":"2026-01-01T00:00:00.5+02:00",
             "headers":{"traceparent":"t","numeric":5},"futureEnvelopeField":true,
             "payload":{"itemId":"11111111-2222-4333-8444-555555555555","kind":"plain"}}
            """;

        var result = _serializer.Read(Encoding.UTF8.GetBytes(body));

        result.Status.Should().Be(MessageReadStatus.Ok, result.Error);
        var envelope = result.Envelope!;
        envelope.WorkspaceId.Should().BeNull();
        envelope.JobId.Should().BeNull();
        envelope.CausationId.Should().BeNull();
        envelope.Attempt.Should().Be(0);
        envelope.Headers.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("traceparent", "t"));
        envelope.CreatedAt.Should().Be(new DateTimeOffset(2025, 12, 31, 22, 0, 0, 500, TimeSpan.Zero));
    }

    [Fact]
    public void Created_at_is_written_as_utc_milliseconds_with_z()
    {
        var envelope = Envelope(new SchemaVersion(2, 0), Json(new JsonObject())) with
        {
            CreatedAt = new DateTimeOffset(2026, 10, 2, 16, 3, 22, 123, TimeSpan.FromHours(2)).AddTicks(4567),
        };

        using var document = JsonDocument.Parse(MessageSerializer.Serialize(envelope));

        document.RootElement.GetProperty("createdAt").GetString().Should().Be("2026-10-02T14:03:22.123Z");
    }

    [Fact]
    public void Registry_rejects_a_previous_major_that_is_not_n_minus_1()
    {
        var registry = new MessageTypeRegistry().Register<SampleV2>();

        var register = () => registry.RegisterPrevious<SampleV2, SampleV2>(v => v);

        register.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("1.0", 1, 0)]
    [InlineData("12.34", 12, 34)]
    public void Schema_version_parses_major_dot_minor(string text, int major, int minor)
    {
        var version = SchemaVersion.Parse(text);
        version.Should().Be(new SchemaVersion(major, minor));
        version.ToString().Should().Be(text);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0.1")]
    [InlineData("1.x")]
    [InlineData("-1.0")]
    [InlineData("1.0.0")]
    [InlineData("")]
    public void Schema_version_rejects_other_forms(string text)
    {
        SchemaVersion.TryParse(text, out _).Should().BeFalse();
    }

    private MessageReadResult Read(SchemaVersion version, JsonObject payload) =>
        _serializer.Read(MessageSerializer.Serialize(Envelope(version, Json(payload))));

    private static JsonElement Json(JsonNode node) => JsonSerializer.SerializeToElement(node);

    private static MessageEnvelope Envelope(SchemaVersion version, JsonElement payload) => new()
    {
        MessageId = Guid.CreateVersion7(),
        MessageType = "test.sample",
        SchemaVersion = version,
        CorrelationId = "corr",
        IdempotencyKey = "idem",
        CreatedAt = DateTimeOffset.UnixEpoch,
        Payload = payload,
    };
}
