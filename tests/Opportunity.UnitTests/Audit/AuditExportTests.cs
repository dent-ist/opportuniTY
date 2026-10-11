using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Keys;

namespace Opportunity.UnitTests.Audit;

/// <summary>
/// E14-T06: an audit export file verifies offline, from genesis or from a signed anchor checkpoint, and any change to an
/// event, a missing event, a forged anchor or a foreign key is reported.
/// </summary>
public sealed class AuditExportTests : IDisposable
{
    private static readonly Guid Workspace = Guid.Parse("0199c3a0-0000-7000-8000-000000000001");

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _key.Dispose();

    [Fact]
    public async Task A_whole_chain_export_verifies_with_the_exported_key()
    {
        var chain = Chain(12);
        var file = Export(chain, fromSequence: 1, checkpointsAt: [5, 10]);

        var result = await VerifyAsync(file, Keys(_key));

        result.Intact.Should().BeTrue(string.Join("; ", result.Chain.Issues.Select(i => i.Message)));
        result.Chain.Events.Should().Be(12);
        result.Chain.Checkpoints.Should().Be(2);
        result.Chain.EventsAfterLastCheckpoint.Should().Be(2);
        result.UsedEmbeddedKeys.Should().BeFalse();

        var embedded = await VerifyAsync(file, keys: null);
        embedded.Intact.Should().BeTrue();
        embedded.UsedEmbeddedKeys.Should().BeTrue();
    }

    [Fact]
    public async Task A_range_export_is_anchored_by_the_checkpoint_that_ends_before_it()
    {
        var chain = Chain(12);
        var file = Export(chain, fromSequence: 6, checkpointsAt: [5, 10]);

        (await VerifyAsync(file, Keys(_key))).Intact.Should().BeTrue();

        // A forged anchor (the manifest claims another predecessor hash) is caught by the signed anchor checkpoint.
        var manifestEnd = file.IndexOf('\n', StringComparison.Ordinal);
        var forged = file[..manifestEnd].Replace(AuditChainFormat.Hex(chain[4].Hash), new string('1', 64), StringComparison.Ordinal) + file[manifestEnd..];
        var result = await VerifyAsync(forged, Keys(_key));
        result.Intact.Should().BeFalse();
        result.Chain.Issues.Select(i => i.Kind).Should().Contain([AuditChainIssueKind.CheckpointMismatch, AuditChainIssueKind.ChainLinkBroken]);
    }

    [Fact]
    public async Task Changed_missing_and_foreign_signed_content_is_reported()
    {
        var chain = Chain(8);
        var file = Export(chain, fromSequence: 1, checkpointsAt: [4, 8]);

        var changed = await VerifyAsync(file.Replace("\"ActorDisplay\":\"Reviewer 3\"", "\"ActorDisplay\":\"Someone Else\"", StringComparison.Ordinal), Keys(_key));
        changed.Chain.Issues.Should().ContainSingle(i => i.Kind == AuditChainIssueKind.EventModified && i.Sequence == 3);

        var lines = file.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        lines.RemoveAt(lines.FindIndex(l => l.Contains("\"Sequence\":\"6\"", StringComparison.Ordinal)));
        var missing = await VerifyAsync(string.Join('\n', lines), Keys(_key));
        missing.Chain.Issues.Select(i => i.Kind).Should().Contain(AuditChainIssueKind.EventsMissing);

        var truncated = await VerifyAsync(string.Join('\n', file.Split('\n', StringSplitOptions.RemoveEmptyEntries).SkipLast(1)), Keys(_key));
        truncated.Chain.Issues.Select(i => i.Kind).Should().Contain(AuditChainIssueKind.TailMissing);

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var foreign = await VerifyAsync(file, Keys(other));
        foreign.Chain.Issues.Select(i => i.Kind).Should().Contain(AuditChainIssueKind.CheckpointSignatureInvalid);
    }

    [Fact]
    public async Task Withheld_search_text_is_verified_through_the_links_and_merkle_roots()
    {
        var chain = Chain(6, restrictedAt: 2);
        var file = Export(chain, fromSequence: 1, checkpointsAt: [6], withhold: true);
        file.Should().NotContain("privileged memo");

        var result = await VerifyAsync(file, Keys(_key));
        result.Intact.Should().BeTrue(string.Join("; ", result.Chain.Issues.Select(i => i.Message)));
        result.WithheldEvents.Should().Be(1);

        // A withheld event whose stored hash was swapped breaks the next link and the signed Merkle root.
        var swapped = await VerifyAsync(file.Replace(AuditChainFormat.Hex(chain[1].Hash), new string('2', 64), StringComparison.Ordinal), Keys(_key));
        swapped.Intact.Should().BeFalse();
    }

    private static Task<AuditExportVerification> VerifyAsync(string file, IAuditCheckpointKeys? keys) =>
        AuditExportVerifier.VerifyAsync(new StringReader(file), keys, Ct);

    private static PemCheckpointKeys Keys(ECDsa key) =>
        new(["# audit-checkpoint-v1 ES256\n" + new string(PemEncoding.Write("PUBLIC KEY", key.ExportSubjectPublicKeyInfo()))]);

    private static List<(AuditChainEnvelope Envelope, byte[] Hash)> Chain(int count, int? restrictedAt = null)
    {
        var chain = new List<(AuditChainEnvelope, byte[])>();
        var prev = AuditChainFormat.Genesis;
        var start = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= count; i++)
        {
            var envelope = new AuditChainEnvelope
            {
                EventId = Guid.CreateVersion7(),
                SchemaVersion = 1,
                WorkspaceId = Workspace,
                Sequence = i,
                OccurredAt = start.AddMinutes(i),
                RecordedAt = start.AddMinutes(i).AddTicks(1230),
                Category = i == restrictedAt ? "Search" : "Document",
                Action = i == restrictedAt ? "Executed" : "Retrieved",
                ActorType = "User",
                ActorId = Guid.CreateVersion7().ToString(),
                ActorDisplay = "Reviewer " + i,
                AccessPath = "Normal",
                ClientIp = "203.0.113.7",
                ResourceType = "Document",
                ResourceId = Guid.CreateVersion7().ToString(),
                Outcome = i % 2 == 0 ? "Denied" : "Success",
                ReasonCode = i % 2 == 0 ? "EthicalWall" : null,
                CorrelationId = "corr-" + i,
                Details = "{\"rendition\":\"Text\",\"n\":" + i + "}",
                RestrictedDetails = i == restrictedAt ? "{\"QueryText\":\"privileged memo\"}" : null,
                SessionIdHash = SHA256.HashData(Encoding.UTF8.GetBytes("s" + i)),
                SearchGeneration = i == restrictedAt ? 9_007_199_254_740_993 : null,
                PrevHash = prev,
            };
            var hash = AuditChainFormat.EventHash(envelope);
            chain.Add((envelope, hash));
            prev = hash;
        }

        return chain;
    }

    private string Export(List<(AuditChainEnvelope Envelope, byte[] Hash)> chain, long fromSequence, long[] checkpointsAt, bool withhold = false)
    {
        var checkpoints = new List<AuditCheckpoint>();
        long previous = 0;
        foreach (var at in checkpointsAt)
        {
            var tree = new AuditMerkleTree();
            for (var s = previous + 1; s <= at; s++)
            {
                tree.Add(chain[(int)s - 1].Hash);
            }

            var data = new AuditCheckpointData(Workspace, previous, at, chain[(int)at - 1].Hash, tree.Root(), AuditCheckpointReason.Scheduled,
                new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc).AddTicks(at * 10));
            checkpoints.Add(new AuditCheckpoint(data, "audit-checkpoint-v1", KeySignature.Es256,
                _key.SignData(AuditChainFormat.CheckpointPayload(data), HashAlgorithmName.SHA256)));
            previous = at;
        }

        var anchor = fromSequence > 1 ? checkpoints.Single(c => c.Sequence == fromSequence - 1) : null;
        var included = checkpoints.Where(c => c.Sequence >= fromSequence).ToList();
        var events = chain.Skip((int)fromSequence - 1).ToList();
        var manifest = new AuditExportManifest
        {
            WorkspaceId = Workspace,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            GeneratedBy = Guid.Empty.ToString(),
            GeneratedByName = "Unit Test",
            FromSequence = fromSequence,
            ToSequence = chain.Count,
            AnchorHash = AuditChainFormat.Hex(anchor?.Data.EventHash ?? AuditChainFormat.Genesis),
            Events = events.Count,
            Checkpoints = included.Count + (anchor is null ? 0 : 1),
            Verification = new AuditExportServerVerification { Intact = true },
        };

        var text = new StringBuilder();
        text.Append(AuditExportFormat.ManifestLine(manifest)).Append('\n');
        text.Append(AuditExportFormat.PublicKeyLine("audit-checkpoint-v1", new string(PemEncoding.Write("PUBLIC KEY", _key.ExportSubjectPublicKeyInfo()))))
            .Append('\n');
        foreach (var checkpoint in anchor is null ? included : [anchor, .. included])
        {
            text.Append(AuditExportFormat.CheckpointLine(checkpoint)).Append('\n');
        }

        foreach (var (envelope, hash) in events)
        {
            var withheld = withhold && envelope.RestrictedDetails is not null;
            text.Append(AuditExportFormat.EventLine(withheld ? envelope with { RestrictedDetails = null } : envelope, hash, withheld)).Append('\n');
        }

        return text.ToString();
    }
}
