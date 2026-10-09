using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Opportunity.Application.Audit.Chain;

/// <summary>
/// One sealed or to-be-sealed audit row as the chain hashes it: every envelope column of <c>audit.audit_event</c>
/// except <c>EventHash</c> and <c>SealedAt</c> (ADR-013 §3.2), in the database's own text forms where a type has
/// several (<see cref="ClientIp"/> is <c>inet::text</c>, the JSON columns are <c>jsonb::text</c>).
/// </summary>
public sealed record AuditChainEnvelope
{
    public required Guid EventId { get; init; }

    public required short SchemaVersion { get; init; }

    public Guid? WorkspaceId { get; init; }

    public required long Sequence { get; init; }

    public required DateTime OccurredAt { get; init; }

    public required DateTime RecordedAt { get; init; }

    public required string Category { get; init; }

    public required string Action { get; init; }

    public required string ActorType { get; init; }

    public required string ActorId { get; init; }

    public required string ActorDisplay { get; init; }

    public Guid? OnBehalfOf { get; init; }

    public required string AccessPath { get; init; }

    public string? ClientIp { get; init; }

    public string? UserAgent { get; init; }

    public byte[]? SessionIdHash { get; init; }

    public string? ResourceType { get; init; }

    public string? ResourceId { get; init; }

    public required string Outcome { get; init; }

    public string? ReasonCode { get; init; }

    public required string CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public Guid? JobId { get; init; }

    public int? ChunkSequence { get; init; }

    public Guid? SnapshotId { get; init; }

    public long? SearchGeneration { get; init; }

    public required string Details { get; init; }

    public string? RestrictedDetails { get; init; }

    public required byte[] PrevHash { get; init; }
}

/// <summary>Inputs of one signed checkpoint (ADR-013 §3.4); the signature covers <see cref="AuditChainFormat.CheckpointPayload"/>.</summary>
public sealed record AuditCheckpointData(
    Guid ChainId,
    long FromSequence,
    long Sequence,
    byte[] EventHash,
    byte[] MerkleRoot,
    AuditCheckpointReason Reason,
    DateTime CreatedAt)
{
    public long EventCount => Sequence - FromSequence;
}

/// <summary>One run of chain positions removed by the retention purge, with the hash of its last event.</summary>
public sealed record AuditChainGap(Guid ChainId, long FirstSequence, long LastSequence, byte[] LastEventHash, string Partition);

/// <summary>
/// The hash-chain and checkpoint formats (version 1). <c>EventHash = SHA-256(PrevHash ‖ JCS(envelope))</c>; genesis
/// <c>PrevHash</c> is 32 zero bytes; checkpoints sign the JCS of <see cref="AuditCheckpointData"/> with ES256.
/// Byte strings are lowercase hex, uuids lowercase with hyphens, timestamps UTC with microseconds
/// (<c>2026-10-09T12:34:56.123456Z</c>), 64-bit integers decimal strings (beyond the 2^53 JSON-safe range).
/// docs/operations/audit-chain.md documents the format for third-party verifiers.
/// </summary>
public static class AuditChainFormat
{
    public const int HashSize = 32;

    public const string CheckpointFormat = "opportunity.audit.checkpoint.v1";

    /// <summary>The chain id of installation-level events (<c>WorkspaceId</c> null): the system chain.</summary>
    public static readonly Guid SystemChainId = Guid.Empty;

    public static byte[] Genesis => new byte[HashSize];

    public static Guid ChainIdOf(Guid? workspaceId) => workspaceId ?? SystemChainId;

    public static Guid? WorkspaceIdOf(Guid chainId) => chainId == SystemChainId ? null : chainId;

    public static string DescribeChain(Guid chainId) => chainId == SystemChainId ? "system" : chainId.ToString("D");

    /// <summary>The canonical (JCS) envelope text that is hashed.</summary>
    public static string CanonicalEnvelope(AuditChainEnvelope e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var members = new List<(string Name, string Json)>(29)
        {
            ("AccessPath", Str(e.AccessPath)),
            ("Action", Str(e.Action)),
            ("ActorDisplay", Str(e.ActorDisplay)),
            ("ActorId", Str(e.ActorId)),
            ("ActorType", Str(e.ActorType)),
            ("Category", Str(e.Category)),
            ("CausationId", Str(e.CausationId)),
            ("ChunkSequence", e.ChunkSequence is { } chunk ? chunk.ToString(CultureInfo.InvariantCulture) : "null"),
            ("ClientIp", Str(e.ClientIp)),
            ("CorrelationId", Str(e.CorrelationId)),
            ("Details", CanonicalJson.Canonicalize(e.Details)),
            ("EventId", Str(Uuid(e.EventId))),
            ("JobId", Str(Uuid(e.JobId))),
            ("OccurredAt", Str(Timestamp(e.OccurredAt))),
            ("OnBehalfOf", Str(Uuid(e.OnBehalfOf))),
            ("Outcome", Str(e.Outcome)),
            ("PrevHash", Str(Hex(e.PrevHash))),
            ("ReasonCode", Str(e.ReasonCode)),
            ("RecordedAt", Str(Timestamp(e.RecordedAt))),
            ("ResourceId", Str(e.ResourceId)),
            ("ResourceType", Str(e.ResourceType)),
            ("RestrictedDetails", e.RestrictedDetails is null ? "null" : CanonicalJson.Canonicalize(e.RestrictedDetails)),
            ("SchemaVersion", e.SchemaVersion.ToString(CultureInfo.InvariantCulture)),
            ("SearchGeneration", Str(Int64(e.SearchGeneration))),
            ("Sequence", Str(Int64(e.Sequence))),
            ("SessionIdHash", Str(e.SessionIdHash is null ? null : Hex(e.SessionIdHash))),
            ("SnapshotId", Str(Uuid(e.SnapshotId))),
            ("UserAgent", Str(e.UserAgent)),
            ("WorkspaceId", Str(Uuid(e.WorkspaceId))),
        };
        return Object(members);
    }

    /// <summary><c>SHA-256(PrevHash ‖ JCS(envelope))</c>.</summary>
    public static byte[] EventHash(AuditChainEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.PrevHash.Length != HashSize)
        {
            throw new ArgumentException("PrevHash must be 32 bytes.", nameof(envelope));
        }

        var canonical = Encoding.UTF8.GetBytes(CanonicalEnvelope(envelope));
        var input = new byte[HashSize + canonical.Length];
        envelope.PrevHash.CopyTo(input, 0);
        canonical.CopyTo(input, HashSize);
        return SHA256.HashData(input);
    }

    /// <summary>The exact bytes a checkpoint signature covers.</summary>
    public static byte[] CheckpointPayload(AuditCheckpointData checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var members = new List<(string Name, string Json)>
        {
            ("ChainId", Str(Uuid(checkpoint.ChainId))),
            ("CreatedAt", Str(Timestamp(checkpoint.CreatedAt))),
            ("EventCount", Str(Int64(checkpoint.EventCount))),
            ("EventHash", Str(Hex(checkpoint.EventHash))),
            ("Format", Str(CheckpointFormat)),
            ("FromSequence", Str(Int64(checkpoint.FromSequence))),
            ("MerkleRoot", Str(Hex(checkpoint.MerkleRoot))),
            ("Reason", Str(checkpoint.Reason.ToString())),
            ("Sequence", Str(Int64(checkpoint.Sequence))),
            ("WorkspaceId", Str(Uuid(WorkspaceIdOf(checkpoint.ChainId)))),
        };
        return Encoding.UTF8.GetBytes(Object(members));
    }

    /// <summary>
    /// The digest the purge records in its <c>Audit.Purged</c> event over every gap of one dropped partition:
    /// SHA-256 of the lines <c>chain:first:last:hash</c>, ordered by chain id text and first sequence, joined by LF.
    /// </summary>
    public static string GapDigest(IEnumerable<AuditChainGap> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        var lines = gaps
            .OrderBy(g => Uuid(g.ChainId), StringComparer.Ordinal)
            .ThenBy(g => g.FirstSequence)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{Uuid(g.ChainId)}:{g.FirstSequence}:{g.LastSequence}:{Hex(g.LastEventHash)}"));
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    /// <summary>The timestamp form used in hashes and payloads; PostgreSQL keeps microseconds.</summary>
    public static string Timestamp(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Truncates to the microsecond precision PostgreSQL stores.</summary>
    public static DateTime ToMicroseconds(DateTime value) =>
        new(value.Ticks - (value.Ticks % 10), DateTimeKind.Utc);

    public static string Hex(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(value);

    private static string? Uuid(Guid? value) => value?.ToString("D");

    private static string? Int64(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Str(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        var builder = new StringBuilder(value.Length + 2);
        CanonicalJson.WriteString(builder, value);
        return builder.ToString();
    }

    private static string Object(List<(string Name, string Json)> members)
    {
        members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var builder = new StringBuilder(512);
        builder.Append('{');
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            CanonicalJson.WriteString(builder, members[i].Name);
            builder.Append(':').Append(members[i].Json);
        }

        return builder.Append('}').ToString();
    }
}

/// <summary>
/// RFC 6962 Merkle tree hash over event hashes in sequence order (leaf <c>SHA-256(0x00 ‖ EventHash)</c>, node
/// <c>SHA-256(0x01 ‖ left ‖ right)</c>), built incrementally in O(log n) memory, so a checkpoint over millions of events
/// streams them.
/// </summary>
public sealed class AuditMerkleTree
{
    private readonly List<(long Size, byte[] Hash)> _stack = [];

    public long Count { get; private set; }

    public void Add(ReadOnlySpan<byte> eventHash)
    {
        Span<byte> leaf = stackalloc byte[1 + AuditChainFormat.HashSize];
        leaf[0] = 0x00;
        eventHash.CopyTo(leaf[1..]);
        var node = SHA256.HashData(leaf[..(1 + eventHash.Length)]);
        long size = 1;
        while (_stack.Count > 0 && _stack[^1].Size == size)
        {
            node = Node(_stack[^1].Hash, node);
            size *= 2;
            _stack.RemoveAt(_stack.Count - 1);
        }

        _stack.Add((size, node));
        Count++;
    }

    /// <summary>The tree hash of the leaves so far (SHA-256 of nothing when empty).</summary>
    public byte[] Root()
    {
        if (_stack.Count == 0)
        {
            return SHA256.HashData([]);
        }

        var root = _stack[^1].Hash;
        for (var i = _stack.Count - 2; i >= 0; i--)
        {
            root = Node(_stack[i].Hash, root);
        }

        return root;
    }

    public static byte[] Node(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        Span<byte> input = stackalloc byte[1 + (2 * AuditChainFormat.HashSize)];
        input[0] = 0x01;
        left.CopyTo(input[1..]);
        right.CopyTo(input[(1 + AuditChainFormat.HashSize)..]);
        return SHA256.HashData(input);
    }
}
