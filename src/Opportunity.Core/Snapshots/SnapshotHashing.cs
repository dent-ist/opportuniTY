using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Opportunity.Core.Snapshots;

/// <summary>
/// The verification hashes of a materialized snapshot (ADR-002 §5.2.5): SHA-256 per membership page and a root hash
/// over all pages. PostgreSQL computes them in the freeze transaction with the same byte layout
/// (<c>int8send(ordinal) || uuid_send(document_id) || int8send(baseline_version) || int2send(reason)</c>), so a
/// verifier can recompute them from the stored members alone.
/// </summary>
public static class SnapshotHashing
{
    /// <summary>Domain-separation prefix of the root hash; bump it (v2) if the layout ever changes.</summary>
    public const string RootPrefix = "opportunity.snapshot.v1";

    public const int MemberBytes = 8 + 16 + 8 + 2;

    public static byte[] PageHash(IReadOnlyList<SnapshotMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var buffer = new byte[members.Count * MemberBytes];
        var span = buffer.AsSpan();
        foreach (var member in members)
        {
            BinaryPrimitives.WriteInt64BigEndian(span, member.Ordinal);
            if (!member.DocumentId.TryWriteBytes(span[8..], bigEndian: true, out _))
            {
                throw new InvalidOperationException("A document ID did not fit its slot.");
            }

            BinaryPrimitives.WriteInt64BigEndian(span[24..], member.BaselineVersion);
            BinaryPrimitives.WriteInt16BigEndian(span[32..], (short)member.Reason);
            span = span[MemberBytes..];
        }

        return SHA256.HashData(buffer);
    }

    public static byte[] RootHash(long documentCount, IEnumerable<byte[]> pageHashesInOrder)
    {
        ArgumentNullException.ThrowIfNull(pageHashesInOrder);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(RootPrefix));
        Span<byte> count = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(count, documentCount);
        hash.AppendData(count);
        foreach (var page in pageHashesInOrder)
        {
            hash.AppendData(page);
        }

        return hash.GetHashAndReset();
    }
}
