using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Opportunity.Import.Jobs;

/// <summary>
/// The DocumentId a data row of an import creates: a UUIDv7 whose timestamp is the import's own (its v7 id) and whose
/// random bits are derived from (import, row). A re-run of a chunk therefore names the same document, so the artifacts it
/// stores under the document's content-addressed keys before its transaction (page images, E08-T05; natives and text,
/// E08-T04) land on the same keys and nothing new is uploaded (ADR-011 §2.4).
/// </summary>
public static class ImportDocumentIds
{
    public static Guid For(Guid importBatchId, long rowNo)
    {
        Span<byte> input = stackalloc byte[24];
        importBatchId.TryWriteBytes(input[..16], bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(input[16..], rowNo);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        Span<byte> id = stackalloc byte[16];
        importBatchId.TryWriteBytes(id, bigEndian: true, out _);
        // Keep the 48-bit unix-ms timestamp of the import id (a v7 id); everything else comes from the hash.
        hash[..10].CopyTo(id[6..]);
        id[6] = (byte)(0x70 | (id[6] & 0x0F));
        id[8] = (byte)(0x80 | (id[8] & 0x3F));
        return new Guid(id, bigEndian: true);
    }
}
