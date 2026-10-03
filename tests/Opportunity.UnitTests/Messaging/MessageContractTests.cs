using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Contracts.Messaging;

namespace Opportunity.UnitTests.Messaging;

/// <summary>
/// Golden-JSON contract tests (ADR-019 §3.7, E06-T01): every readable contract (current major N and previous N-1) has a
/// golden envelope sample; the writer must still produce it byte-for-byte (a removed, renamed or re-typed field fails),
/// and the reader must accept it.
/// </summary>
public sealed class MessageContractTests
{
    private static readonly MessageTypeRegistry Registry = MessageContracts.CreateRegistry();
    private static readonly MessageSerializer Serializer = new(Registry);

    public static TheoryData<string, int> ReadableContracts()
    {
        var data = new TheoryData<string, int>();
        foreach (var contract in Registry.Readable.OrderBy(c => c.Wire.MessageType, StringComparer.Ordinal))
        {
            data.Add(contract.Wire.MessageType, contract.Wire.Version.Major);
        }

        return data;
    }

    [Fact]
    public void Every_message_type_has_a_golden_sample_for_each_readable_major()
    {
        var expected = Registry.Readable.Select(c => GoldenSamples.FileName(c.Wire.MessageType, c.Wire.Version.Major)).Order().ToList();
        var present = Directory.GetFiles(GoldenSamples.Path(string.Empty), "*.json")
            .Select(Path.GetFileName)
            .Where(name => name != "envelope.v1.json")
            .Order()
            .ToList();

        present.Should().Equal(expected, "each readable (messageType, major) needs exactly one golden file and sample");
        GoldenSamples.Payloads.Keys.Select(k => GoldenSamples.FileName(k.MessageType, k.Major)).Order()
            .Should().Equal(expected);
    }

    [Fact]
    public void Current_contracts_keep_their_previous_major_readable()
    {
        foreach (var current in Registry.Current.Where(c => c.Version.Major > 1))
        {
            Registry.TryResolve(current.MessageType, new SchemaVersion(current.Version.Major - 1, 0), out _)
                .Should().BeTrue($"consumers must accept N-1 of '{current.MessageType}' (ADR-019 §3.5)");
        }
    }

    [Theory]
    [MemberData(nameof(ReadableContracts))]
    public void Writer_still_produces_the_golden_sample(string messageType, int major)
    {
        Registry.TryResolve(messageType, new SchemaVersion(major, 0), out var contract).Should().BeTrue();
        var sample = GoldenSamples.Payloads[(messageType, major)];
        sample.GetType().Should().Be(contract.Wire.PayloadType);

        var payload = JsonSerializer.SerializeToElement(sample, contract.Wire.PayloadType, MessageJson.PayloadOptions);
        var written = MessageSerializer.Serialize(GoldenSamples.Envelope(messageType, contract.Wire.Version, payload));

        var golden = File.ReadAllBytes(GoldenSamples.Path(GoldenSamples.FileName(messageType, major)));
        GoldenSamples.Canonical(written).Should().Be(
            GoldenSamples.Canonical(golden),
            "removing, renaming or re-typing a field of '{0}' v{1} needs a new major and a new golden file", messageType, major);
    }

    [Theory]
    [MemberData(nameof(ReadableContracts))]
    public void Golden_payload_fields_are_exactly_the_contract_members(string messageType, int major)
    {
        Registry.TryResolve(messageType, new SchemaVersion(major, 0), out var contract).Should().BeTrue();
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenSamples.Path(GoldenSamples.FileName(messageType, major))));
        var goldenFields = golden.RootElement.GetProperty("payload").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal);
        var contractFields = MessageJson.PayloadOptions.GetTypeInfo(contract.Wire.PayloadType).Properties
            .Select(p => p.Name).Order(StringComparer.Ordinal);

        contractFields.Should().Equal(goldenFields, "a tolerant reader would silently ignore a removed field");
    }

    [Theory]
    [MemberData(nameof(ReadableContracts))]
    public void Reader_accepts_the_golden_sample(string messageType, int major)
    {
        var result = Serializer.Read(File.ReadAllBytes(GoldenSamples.Path(GoldenSamples.FileName(messageType, major))));

        result.Status.Should().Be(MessageReadStatus.Ok, result.Error);
        result.Envelope!.MessageType.Should().Be(messageType);
        result.Envelope.SchemaVersion.Major.Should().Be(major);
        result.Payload!.GetType().Should().Be(result.Contract!.Current.PayloadType, "N-1 payloads are upgraded to N");
        if (result.Contract.Upgrade is null)
        {
            result.Payload.Should().Be(GoldenSamples.Payloads[(messageType, major)]);
        }
    }

    [Fact]
    public void Envelope_v1_golden_has_exactly_the_baseline_fields_in_order()
    {
        var golden = File.ReadAllBytes(GoldenSamples.Path("envelope.v1.json"));
        using var document = JsonDocument.Parse(golden);

        document.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "messageId", "messageType", "schemaVersion", "workspaceId", "jobId", "correlationId", "causationId",
            "idempotencyKey", "createdAt", "attempt", "headers", "payload");

        var result = Serializer.Read(golden);
        result.Status.Should().Be(MessageReadStatus.Ok, result.Error);
        var envelope = result.Envelope!;
        envelope.MessageId.Should().Be(GoldenSamples.MessageId);
        envelope.WorkspaceId.Should().Be(GoldenSamples.WorkspaceId);
        envelope.JobId.Should().Be(GoldenSamples.JobId);
        envelope.CreatedAt.Should().Be(GoldenSamples.CreatedAt);
        envelope.Attempt.Should().Be(1);
        envelope.Headers[MessageHeaderNames.TraceParent].Should().Be(GoldenSamples.TraceParent);
        GoldenSamples.Canonical(MessageSerializer.Serialize(envelope)).Should().Be(GoldenSamples.Canonical(golden));
    }
}
