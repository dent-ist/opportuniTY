using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Opportunity.Application.Audit.Chain;

/// <summary>
/// The audit export package (E14-T06): one JSON Lines file that carries a checkpoint-aligned range of a workspace's hash
/// chain with everything needed to verify it offline (<c>audit verify-export</c>): the manifest (range, anchor, counts
/// and the verification result at generation), the public keys the server knew, the signed checkpoints (the anchor
/// checkpoint that ends just before the range included), purge records inside the range, and every sealed event as its
/// canonical (JCS) envelope with its stored hash. Record order is fixed: manifest, publicKey*, checkpoint*, gap*, event*.
/// docs/operations/audit-chain.md documents the format for third-party verifiers.
/// </summary>
public static class AuditExportFormat
{
    public const string Format = "opportunity.audit.export.v1";

    public const string ContentType = "application/x-ndjson";

    public const string ManifestRecord = "manifest";
    public const string PublicKeyRecord = "publicKey";
    public const string CheckpointRecord = "checkpoint";
    public const string GapRecord = "gap";
    public const string EventRecord = "event";

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    internal static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FileName(Guid workspaceId, DateTimeOffset generatedAt) =>
        $"audit-export-{workspaceId:D}-{generatedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.jsonl";

    public static string ManifestLine(AuditExportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var node = JsonSerializer.SerializeToNode(manifest, ManifestJson)!.AsObject();
        node.Insert(0, "record", ManifestRecord);
        return node.ToJsonString(ManifestJson);
    }

    public static string PublicKeyLine(string keyId, string pem) => Line(w =>
    {
        w.WriteString("record", PublicKeyRecord);
        w.WriteString("keyId", keyId);
        w.WriteString("pem", pem);
    });

    /// <summary>The signed payload exactly as signed, plus the key id, algorithm and base64 IEEE P1363 signature.</summary>
    public static string CheckpointLine(AuditCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return Line(w =>
        {
            w.WriteString("record", CheckpointRecord);
            w.WriteString("keyId", checkpoint.KeyId);
            w.WriteString("algorithm", checkpoint.Algorithm);
            w.WriteString("signature", Convert.ToBase64String(checkpoint.Signature));
            w.WritePropertyName("payload");
            w.WriteRawValue(AuditChainFormat.CheckpointPayload(checkpoint.Data), skipInputValidation: true);
        });
    }

    public static string GapLine(AuditChainGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        return Line(w =>
        {
            w.WriteString("record", GapRecord);
            w.WriteString("firstSequence", Int64(gap.FirstSequence));
            w.WriteString("lastSequence", Int64(gap.LastSequence));
            w.WriteString("lastEventHash", AuditChainFormat.Hex(gap.LastEventHash));
            w.WriteString("partition", gap.Partition);
        });
    }

    /// <param name="withheld">The envelope's RestrictedDetails were left out (the caller may not read search text, Q-16).</param>
    public static string EventLine(AuditChainEnvelope envelope, byte[]? eventHash, bool withheld)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var canonical = Encoding.UTF8.GetBytes(AuditChainFormat.CanonicalEnvelope(envelope));
        return Line(w =>
        {
            w.WriteString("record", EventRecord);
            if (eventHash is null)
            {
                w.WriteNull("eventHash");
            }
            else
            {
                w.WriteString("eventHash", AuditChainFormat.Hex(eventHash));
            }

            w.WriteBoolean("withheld", withheld);
            w.WritePropertyName("envelope");
            w.WriteRawValue(canonical, skipInputValidation: true);
        });
    }

    /// <summary>The envelope of an <c>event</c> record (the inverse of <see cref="AuditChainFormat.CanonicalEnvelope"/>).</summary>
    public static AuditChainEnvelope ParseEnvelope(JsonElement e) => new()
    {
        AccessPath = Required(e, "AccessPath"),
        Action = Required(e, "Action"),
        ActorDisplay = Required(e, "ActorDisplay"),
        ActorId = Required(e, "ActorId"),
        ActorType = Required(e, "ActorType"),
        Category = Required(e, "Category"),
        CausationId = Optional(e, "CausationId"),
        ChunkSequence = e.GetProperty("ChunkSequence").ValueKind == JsonValueKind.Null ? null : e.GetProperty("ChunkSequence").GetInt32(),
        ClientIp = Optional(e, "ClientIp"),
        CorrelationId = Required(e, "CorrelationId"),
        Details = e.GetProperty("Details").GetRawText(),
        EventId = Guid.Parse(Required(e, "EventId"), CultureInfo.InvariantCulture),
        JobId = OptionalGuid(e, "JobId"),
        OccurredAt = ParseTimestamp(Required(e, "OccurredAt")),
        OnBehalfOf = OptionalGuid(e, "OnBehalfOf"),
        Outcome = Required(e, "Outcome"),
        PrevHash = Convert.FromHexString(Required(e, "PrevHash")),
        ReasonCode = Optional(e, "ReasonCode"),
        RecordedAt = ParseTimestamp(Required(e, "RecordedAt")),
        ResourceId = Optional(e, "ResourceId"),
        ResourceType = Optional(e, "ResourceType"),
        RestrictedDetails = e.GetProperty("RestrictedDetails").ValueKind == JsonValueKind.Null ? null : e.GetProperty("RestrictedDetails").GetRawText(),
        SchemaVersion = e.GetProperty("SchemaVersion").GetInt16(),
        SearchGeneration = Optional(e, "SearchGeneration") is { } generation ? long.Parse(generation, CultureInfo.InvariantCulture) : null,
        Sequence = long.Parse(Required(e, "Sequence"), CultureInfo.InvariantCulture),
        SessionIdHash = Optional(e, "SessionIdHash") is { } session ? Convert.FromHexString(session) : null,
        SnapshotId = OptionalGuid(e, "SnapshotId"),
        UserAgent = Optional(e, "UserAgent"),
        WorkspaceId = OptionalGuid(e, "WorkspaceId"),
    };

    /// <summary>The checkpoint of a <c>checkpoint</c> record; the payload must be exactly what <see cref="AuditChainFormat.CheckpointPayload"/> produces.</summary>
    public static AuditCheckpoint ParseCheckpoint(JsonElement record)
    {
        var payload = record.GetProperty("payload");
        if (Required(payload, "Format") != AuditChainFormat.CheckpointFormat)
        {
            throw new FormatException("Unknown checkpoint format.");
        }

        var data = new AuditCheckpointData(
            Guid.Parse(Required(payload, "ChainId"), CultureInfo.InvariantCulture),
            long.Parse(Required(payload, "FromSequence"), CultureInfo.InvariantCulture),
            long.Parse(Required(payload, "Sequence"), CultureInfo.InvariantCulture),
            Convert.FromHexString(Required(payload, "EventHash")),
            Convert.FromHexString(Required(payload, "MerkleRoot")),
            Enum.Parse<AuditCheckpointReason>(Required(payload, "Reason")),
            ParseTimestamp(Required(payload, "CreatedAt")));
        if (long.Parse(Required(payload, "EventCount"), CultureInfo.InvariantCulture) != data.EventCount)
        {
            throw new FormatException($"Checkpoint {data.Sequence}: EventCount disagrees with its range.");
        }

        return new AuditCheckpoint(data, Required(record, "keyId"), Required(record, "algorithm"), Convert.FromBase64String(Required(record, "signature")));
    }

    public static AuditChainGap ParseGap(JsonElement record, Guid chainId) => new(
        chainId,
        long.Parse(Required(record, "firstSequence"), CultureInfo.InvariantCulture),
        long.Parse(Required(record, "lastSequence"), CultureInfo.InvariantCulture),
        Convert.FromHexString(Required(record, "lastEventHash")),
        Required(record, "partition"));

    public static AuditExportManifest ParseManifest(JsonElement record)
    {
        var manifest = record.Deserialize<AuditExportManifest>(ManifestJson) ?? throw new FormatException("Empty manifest.");
        return manifest.Format == Format ? manifest : throw new FormatException($"Not an {Format} file (format '{manifest.Format}').");
    }

    public static DateTime ParseTimestamp(string value) =>
        DateTime.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string Line(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Int64(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Required(JsonElement e, string name) =>
        e.GetProperty(name).GetString() ?? throw new FormatException($"'{name}' is required.");

    private static string? Optional(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static Guid? OptionalGuid(JsonElement e, string name) =>
        Optional(e, name) is { } text ? Guid.Parse(text, CultureInfo.InvariantCulture) : null;
}

/// <summary>The first record of an audit export: what it covers and what the server found when generating it.</summary>
public sealed record AuditExportManifest
{
    public string Format { get; init; } = AuditExportFormat.Format;

    public required Guid WorkspaceId { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required string GeneratedBy { get; init; }

    public required string GeneratedByName { get; init; }

    /// <summary>The <c>Audit.Exported</c> event recorded for this export.</summary>
    public Guid? AuditEventId { get; init; }

    public DateTimeOffset? RequestedFrom { get; init; }

    public DateTimeOffset? RequestedTo { get; init; }

    /// <summary>The first chain position in the file (the position after the anchor checkpoint, or 1).</summary>
    public required long FromSequence { get; init; }

    /// <summary>The last chain position in the file (a checkpoint where one covers the range); 0 when the file has no events.</summary>
    public required long ToSequence { get; init; }

    /// <summary>Hash of the event before <see cref="FromSequence"/>, signed by the anchor checkpoint (32 zero bytes from genesis).</summary>
    public required string AnchorHash { get; init; }

    public long Events { get; init; }

    public int Checkpoints { get; init; }

    public int Gaps { get; init; }

    /// <summary>Events whose search text (RestrictedDetails) was left out because the exporter may not read it (Q-16).</summary>
    public long WithheldEvents { get; init; }

    /// <summary>Events of the requested period the sealer had not chained yet; they are not in the file.</summary>
    public long UnsealedEvents { get; init; }

    /// <summary>Events after the last checkpoint in the file: linked, but not yet covered by a signature.</summary>
    public long EventsAfterLastCheckpoint { get; init; }

    public required AuditExportServerVerification Verification { get; init; }
}

/// <summary>The verification of the exported range when it was generated (the same walk <c>audit verify-export</c> repeats).</summary>
public sealed record AuditExportServerVerification
{
    public required bool Intact { get; init; }

    public long IssueCount { get; init; }

    public IReadOnlyList<AuditExportIssue> Issues { get; init; } = [];

    /// <summary>Checkpoint signatures checked with the installation's public keys.</summary>
    public int SignaturesVerified { get; init; }

    /// <summary>Signatures the server could not check (no public key available to the API); check them offline.</summary>
    public int SignaturesUnchecked { get; init; }
}

public sealed record AuditExportIssue(AuditChainIssueKind Kind, long? Sequence, string Message);

/// <summary>The result of verifying an export file offline.</summary>
public sealed record AuditExportVerification(
    AuditExportManifest Manifest,
    AuditChainVerification Chain,
    bool UsedEmbeddedKeys,
    int SignaturesUnchecked,
    long WithheldEvents)
{
    public bool Intact => Chain.Intact;
}

/// <summary>
/// Verifies an audit export file without the database (E14-T06, <c>audit verify-export</c>): the anchor checkpoint must
/// sign the anchor hash, every event hash is recomputed and linked from the anchor, every checkpoint's head hash, Merkle
/// root, continuity and signature are checked, and the file must reach the last position its manifest promises.
/// </summary>
public static class AuditExportVerifier
{
    public const int MaxIssuesInManifest = 20;

    /// <param name="keys">The exported public keys; null uses the keys embedded in the file (reported in the result).</param>
    public static async Task<AuditExportVerification> VerifyAsync(TextReader reader, IAuditCheckpointKeys? keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var first = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? throw new FormatException("The file is empty.");
        AuditExportManifest manifest;
        using (var document = JsonDocument.Parse(first))
        {
            if (RecordOf(document.RootElement) != AuditExportFormat.ManifestRecord)
            {
                throw new FormatException("The first record must be the manifest.");
            }

            manifest = AuditExportFormat.ParseManifest(document.RootElement);
        }

        var anchorHash = Convert.FromHexString(manifest.AnchorHash);
        var walk = new AuditChainWalk(manifest.WorkspaceId, manifest.FromSequence, anchorHash);
        var embedded = new List<string>();
        AuditCheckpoint? anchor = null;
        var stage = 0;
        string? line;
        var lineNumber = 1;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            var kind = RecordOf(record);
            var order = kind switch
            {
                AuditExportFormat.PublicKeyRecord => 1,
                AuditExportFormat.CheckpointRecord => 2,
                AuditExportFormat.GapRecord => 3,
                AuditExportFormat.EventRecord => 4,
                _ => throw new FormatException($"Line {lineNumber}: unknown record '{kind}'."),
            };
            if (order < stage)
            {
                throw new FormatException($"Line {lineNumber}: a {kind} record after the events or out of order.");
            }

            if (order == 4 && stage < 4)
            {
                // Everything the walk needs is read: check the anchor and the signatures before the first event.
                keys = await PrepareAsync(walk, manifest, anchor, keys, embedded, cancellationToken).ConfigureAwait(false);
            }

            stage = order;
            switch (kind)
            {
                case AuditExportFormat.PublicKeyRecord:
                    embedded.Add($"# {record.GetProperty("keyId").GetString()} ES256\n{record.GetProperty("pem").GetString()}");
                    break;
                case AuditExportFormat.CheckpointRecord:
                    var checkpoint = AuditExportFormat.ParseCheckpoint(record);
                    if (checkpoint.ChainId != manifest.WorkspaceId)
                    {
                        throw new FormatException($"Line {lineNumber}: a checkpoint of another chain.");
                    }

                    if (checkpoint.Sequence < manifest.FromSequence)
                    {
                        anchor = checkpoint;
                    }
                    else
                    {
                        walk.Checkpoints.Add(checkpoint);
                    }

                    break;
                case AuditExportFormat.GapRecord:
                    walk.Gaps.Add(AuditExportFormat.ParseGap(record, manifest.WorkspaceId));
                    break;
                default:
                    var envelope = AuditExportFormat.ParseEnvelope(record.GetProperty("envelope"));
                    if (envelope.WorkspaceId != manifest.WorkspaceId)
                    {
                        throw new FormatException($"Line {lineNumber}: an event of another workspace.");
                    }

                    var hash = record.GetProperty("eventHash").ValueKind == JsonValueKind.Null
                        ? null
                        : Convert.FromHexString(record.GetProperty("eventHash").GetString()!);
                    walk.Visit(envelope, envelope.Sequence, envelope.PrevHash, hash, record.GetProperty("withheld").GetBoolean());
                    break;
            }
        }

        if (stage < 4)
        {
            keys = await PrepareAsync(walk, manifest, anchor, keys, embedded, cancellationToken).ConfigureAwait(false);
        }

        walk.Finish(Math.Max(manifest.ToSequence, walk.Checkpoints.Count > 0 ? walk.Checkpoints[^1].Sequence : 0));
        if (walk.Events != manifest.Events)
        {
            walk.Issue(AuditChainIssueKind.EventsMissing, null, $"the manifest lists {manifest.Events} event(s), the file holds {walk.Events}");
        }

        return new AuditExportVerification(
            manifest, walk.Result(manifest.UnsealedEvents), keys is null, walk.UncheckedSignatures, walk.Withheld);
    }

    /// <summary>The issues of a walk as the manifest records them.</summary>
    public static AuditExportServerVerification Summarize(AuditChainWalk walk, int signaturesVerified)
    {
        ArgumentNullException.ThrowIfNull(walk);
        return new AuditExportServerVerification
        {
            Intact = walk.IssueCount == 0,
            IssueCount = walk.IssueCount,
            Issues = [.. walk.Issues.Take(MaxIssuesInManifest).Select(i => new AuditExportIssue(i.Kind, i.Sequence, i.Message))],
            SignaturesVerified = signaturesVerified,
            SignaturesUnchecked = walk.UncheckedSignatures,
        };
    }

    /// <summary>Checks the anchor and every checkpoint signature; returns the keys used (null when the file's own keys were used).</summary>
    private static async Task<IAuditCheckpointKeys?> PrepareAsync(
        AuditChainWalk walk, AuditExportManifest manifest, AuditCheckpoint? anchor, IAuditCheckpointKeys? keys, List<string> embedded,
        CancellationToken cancellationToken)
    {
        var effective = keys ?? (embedded.Count > 0 ? new PemCheckpointKeys(embedded) : new PemCheckpointKeys([]));
        await VerifyAnchorAsync(walk, manifest, anchor, effective, cancellationToken: cancellationToken).ConfigureAwait(false);
        await walk.VerifySignaturesAsync(effective, cancellationToken: cancellationToken).ConfigureAwait(false);
        return keys;
    }

    /// <summary>The anchor hash must be genesis (from position 1) or the head hash a valid anchor checkpoint signed.</summary>
    public static async Task VerifyAnchorAsync(
        AuditChainWalk walk, AuditExportManifest manifest, AuditCheckpoint? anchor, IAuditCheckpointKeys keys, bool missingKeyIsIssue = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(walk);
        ArgumentNullException.ThrowIfNull(manifest);
        var anchorHash = Convert.FromHexString(manifest.AnchorHash);
        if (manifest.FromSequence <= 1)
        {
            if (!anchorHash.AsSpan().SequenceEqual(AuditChainFormat.Genesis))
            {
                walk.Issue(AuditChainIssueKind.ChainLinkBroken, 1, "an export from position 1 must start at the genesis hash");
            }

            return;
        }

        if (anchor is null || anchor.Sequence != manifest.FromSequence - 1)
        {
            walk.Issue(AuditChainIssueKind.CheckpointMissing, manifest.FromSequence - 1,
                $"no signed checkpoint at position {manifest.FromSequence - 1} anchors the exported range");
            return;
        }

        if (!anchor.Data.EventHash.AsSpan().SequenceEqual(anchorHash))
        {
            walk.Issue(AuditChainIssueKind.CheckpointMismatch, anchor.Sequence, "the anchor checkpoint signed a different hash than the manifest's anchor");
        }

        await walk.VerifySignatureAsync(anchor, keys, missingKeyIsIssue, cancellationToken).ConfigureAwait(false);
    }

    private static string? RecordOf(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("record", out var record) ? record.GetString() : null;
}

/// <summary>PEM text for an exported public key, in the <c>keys public-key</c> form (a <c># key-id ES256</c> line, then the PEM).</summary>
public static class AuditExportKeys
{
    public static string Pem(byte[] subjectPublicKeyInfo) => new(PemEncoding.Write("PUBLIC KEY", subjectPublicKeyInfo));
}
