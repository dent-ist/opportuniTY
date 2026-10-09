using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Keys;

namespace Opportunity.UnitTests.Audit;

/// <summary>E14-T03 / ADR-013 §3.2: the canonical form, hashes, Merkle tree and checkpoint payload of the audit chain.</summary>
public sealed class AuditChainFormatTests
{
    // RFC 8785 Appendix B: IEEE 754 bit patterns and their ECMAScript serializations.
    [Theory]
    [InlineData("0000000000000000", "0")]
    [InlineData("8000000000000000", "0")]
    [InlineData("0000000000000001", "5e-324")]
    [InlineData("8000000000000001", "-5e-324")]
    [InlineData("7fefffffffffffff", "1.7976931348623157e+308")]
    [InlineData("ffefffffffffffff", "-1.7976931348623157e+308")]
    [InlineData("4340000000000000", "9007199254740992")]
    [InlineData("c340000000000000", "-9007199254740992")]
    [InlineData("4430000000000000", "295147905179352830000")]
    [InlineData("44b52d02c7e14af5", "9.999999999999997e+22")]
    [InlineData("44b52d02c7e14af6", "1e+23")]
    [InlineData("44b52d02c7e14af7", "1.0000000000000001e+23")]
    [InlineData("444b1ae4d6e2ef4e", "999999999999999700000")]
    [InlineData("444b1ae4d6e2ef4f", "999999999999999900000")]
    [InlineData("444b1ae4d6e2ef50", "1e+21")]
    [InlineData("3eb0c6f7a0b5ed8c", "9.999999999999997e-7")]
    [InlineData("3eb0c6f7a0b5ed8d", "0.000001")]
    [InlineData("41b3de4355555553", "333333333.3333332")]
    [InlineData("41b3de4355555554", "333333333.33333325")]
    [InlineData("41b3de4355555555", "333333333.3333333")]
    [InlineData("41b3de4355555556", "333333333.3333334")]
    [InlineData("41b3de4355555557", "333333333.33333343")]
    [InlineData("becbf647612f3696", "-0.0000033333333333333333")]
    [InlineData("43143ff3c1cb0959", "1424953923781206.2")]
    public void Numbers_serialize_like_ecmascript(string bits, string expected)
    {
        var value = BitConverter.Int64BitsToDouble(long.Parse(bits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        CanonicalJson.FormatNumber(value).Should().Be(expected);
    }

    [Fact]
    public void Canonicalization_matches_the_rfc_8785_example()
    {
        const string input =
            """
            {
              "numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],
              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
              "literals": [null, true, false]
            }
            """;
        CanonicalJson.Canonicalize(input).Should().Be(
            "{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],\"string\":\"€$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}");
    }

    [Fact]
    public void Members_sort_by_utf16_code_units()
    {
        const string input =
            """{"\u20ac":"Euro Sign","\r":"Carriage Return","\ufb33":"Hebrew","1":"One","\ud83d\ude00":"Emoji","\u0080":"Control","\u00f6":"o"}""";
        CanonicalJson.Canonicalize(input).Should().Be(
            "{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"\u0080\":\"Control\",\"ö\":\"o\",\"€\":\"Euro Sign\",\"😀\":\"Emoji\",\"\ufb33\":\"Hebrew\"}");
    }

    [Fact]
    public void Every_hashed_column_changes_the_event_hash()
    {
        var baseline = Envelope();
        var hash = AuditChainFormat.EventHash(baseline);
        hash.Should().HaveCount(32);
        AuditChainFormat.EventHash(baseline with { }).Should().Equal(hash, "the hash is deterministic");

        var variants = new AuditChainEnvelope[]
        {
            baseline with { EventId = Guid.CreateVersion7() },
            baseline with { SchemaVersion = 2 },
            baseline with { WorkspaceId = null },
            baseline with { Sequence = 8 },
            baseline with { OccurredAt = baseline.OccurredAt.AddTicks(10) },
            baseline with { RecordedAt = baseline.RecordedAt.AddTicks(10) },
            baseline with { Category = "Auth" },
            baseline with { Action = "Viewed" },
            baseline with { ActorType = "Service" },
            baseline with { ActorId = "other" },
            baseline with { ActorDisplay = "Other" },
            baseline with { OnBehalfOf = null },
            baseline with { AccessPath = "BreakGlass" },
            baseline with { ClientIp = "203.0.113.8" },
            baseline with { UserAgent = null },
            baseline with { SessionIdHash = new byte[32] },
            baseline with { ResourceType = "Production" },
            baseline with { ResourceId = "doc-2" },
            baseline with { Outcome = "Denied", ReasonCode = "Walled" },
            baseline with { CorrelationId = "corr-2" },
            baseline with { CausationId = null },
            baseline with { JobId = null },
            baseline with { ChunkSequence = 4 },
            baseline with { SnapshotId = null },
            baseline with { SearchGeneration = 43 },
            baseline with { Details = """{"Purpose":"Prefetch"}""" },
            baseline with { RestrictedDetails = """{"Query":"x"}""" },
            baseline with { PrevHash = SHA256.HashData("prev"u8) },
        };
        variants.Select(v => Convert.ToHexString(AuditChainFormat.EventHash(v))).Should().OnlyHaveUniqueItems()
            .And.NotContain(Convert.ToHexString(hash));
    }

    [Fact]
    public void Details_hash_by_value_not_by_member_order_or_whitespace()
    {
        var a = Envelope() with { Details = """{"B":"2","A":"1"}""" };
        var b = Envelope() with { Details = """{ "A": "1", "B": "2" }""" };
        AuditChainFormat.EventHash(a).Should().Equal(AuditChainFormat.EventHash(b));
        AuditChainFormat.CanonicalEnvelope(a).Should().Contain("\"Details\":{\"A\":\"1\",\"B\":\"2\"}")
            .And.Contain("\"Sequence\":\"7\"").And.Contain("\"OccurredAt\":\"2026-10-09T08:15:30.123456Z\"");
    }

    [Fact]
    public void The_merkle_tree_matches_rfc_6962_for_every_size()
    {
        AuditMerkleTree empty = new();
        empty.Root().Should().Equal(SHA256.HashData([]));
        for (var n = 1; n <= 33; n++)
        {
            var leaves = Enumerable.Range(0, n).Select(i => SHA256.HashData(BitConverter.GetBytes(i))).ToArray();
            var tree = new AuditMerkleTree();
            foreach (var leaf in leaves)
            {
                tree.Add(leaf);
            }

            tree.Count.Should().Be(n);
            tree.Root().Should().Equal(Reference(leaves), $"n = {n}");
        }
    }

    [Fact]
    public async Task Checkpoint_signatures_verify_with_the_exported_public_key_only()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var data = new AuditCheckpointData(Guid.CreateVersion7(), 10, 25, SHA256.HashData("head"u8), SHA256.HashData("root"u8),
            AuditCheckpointReason.Scheduled, AuditChainFormat.ToMicroseconds(DateTime.UtcNow));
        var checkpoint = new AuditCheckpoint(data, "audit-checkpoint-v1", KeySignature.Es256,
            key.SignData(AuditChainFormat.CheckpointPayload(data), HashAlgorithmName.SHA256));
        var pem = "# audit-checkpoint-v1 ES256\n" + new string(PemEncoding.Write("PUBLIC KEY", key.ExportSubjectPublicKeyInfo()));
        var keys = new PemCheckpointKeys([pem]);
        keys.KeyIds.Should().Equal("audit-checkpoint-v1");
        var spki = (await keys.GetPublicKeyAsync("audit-checkpoint-v1", TestContext.Current.CancellationToken))!;

        AuditCheckpointSignatures.Verify(checkpoint, spki).Should().BeTrue();
        AuditCheckpointSignatures.Verify(checkpoint with { Data = data with { Sequence = 26 } }, spki).Should().BeFalse();
        AuditCheckpointSignatures.Verify(checkpoint with { Data = data with { Reason = AuditCheckpointReason.Manual } }, spki).Should().BeFalse();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        AuditCheckpointSignatures.Verify(checkpoint, other.ExportSubjectPublicKeyInfo()).Should().BeFalse();
        (await keys.GetPublicKeyAsync("audit-checkpoint-v2", TestContext.Current.CancellationToken)).Should().BeNull();

        Encoding.UTF8.GetString(AuditChainFormat.CheckpointPayload(data)).Should().StartWith("{\"ChainId\":")
            .And.Contain("\"EventCount\":\"15\"").And.Contain("\"Format\":\"opportunity.audit.checkpoint.v1\"");
    }

    [Fact]
    public void The_gap_digest_does_not_depend_on_input_order()
    {
        var a = new AuditChainGap(new Guid("0f000000-0000-0000-0000-000000000000"), 1, 5, SHA256.HashData("a"u8), "audit_event_p201001");
        var b = new AuditChainGap(new Guid("a0000000-0000-0000-0000-000000000000"), 3, 4, SHA256.HashData("b"u8), "audit_event_p201001");
        var c = new AuditChainGap(Guid.Empty, 9, 9, SHA256.HashData("c"u8), "audit_event_p201001");
        AuditChainFormat.GapDigest([a, b, c]).Should().Be(AuditChainFormat.GapDigest([c, b, a])).And.HaveLength(64);
        AuditChainFormat.GapDigest([a, b]).Should().NotBe(AuditChainFormat.GapDigest([a, b with { LastSequence = 5 }]));
    }

    private static AuditChainEnvelope Envelope() => new()
    {
        EventId = new Guid("01920000-0000-7000-8000-000000000001"),
        SchemaVersion = 1,
        WorkspaceId = new Guid("01920000-0000-7000-8000-0000000000aa"),
        Sequence = 7,
        OccurredAt = new DateTime(2026, 10, 9, 8, 15, 30, DateTimeKind.Utc).AddTicks(1234560),
        RecordedAt = new DateTime(2026, 10, 9, 8, 15, 30, DateTimeKind.Utc).AddTicks(1300000),
        Category = "Document",
        Action = "Retrieved",
        ActorType = "User",
        ActorId = "user-1",
        ActorDisplay = "Rev Iewer",
        OnBehalfOf = new Guid("01920000-0000-7000-8000-0000000000bb"),
        AccessPath = "Normal",
        ClientIp = "203.0.113.7",
        UserAgent = "test-agent",
        SessionIdHash = SHA256.HashData("session"u8),
        ResourceType = "Document",
        ResourceId = "doc-1",
        Outcome = "Success",
        CorrelationId = "corr-1",
        CausationId = "cause-1",
        JobId = new Guid("01920000-0000-7000-8000-0000000000cc"),
        ChunkSequence = 3,
        SnapshotId = new Guid("01920000-0000-7000-8000-0000000000dd"),
        SearchGeneration = 42,
        Details = """{"Purpose":"Display"}""",
        PrevHash = AuditChainFormat.Genesis,
    };

    private static byte[] Reference(ReadOnlySpan<byte[]> leaves)
    {
        if (leaves.Length == 1)
        {
            return SHA256.HashData([0x00, .. leaves[0]]);
        }

        var k = 1;
        while (k * 2 < leaves.Length)
        {
            k *= 2;
        }

        return SHA256.HashData([0x01, .. Reference(leaves[..k]), .. Reference(leaves[k..])]);
    }
}
