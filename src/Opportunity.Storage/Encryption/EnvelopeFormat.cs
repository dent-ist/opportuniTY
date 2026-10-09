using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Opportunity.Application.Storage;

namespace Opportunity.Storage.Encryption;

/// <summary>
/// The envelope object format, version 1 (ADR-011 §6 as amended by <c>E05-T09</c>): a 64-byte header followed by the
/// plaintext in fixed-size chunks, each sealed with AES-256-GCM (16-byte tag).
/// <code>
/// 0..7   magic 89 'O' 'P' 'E' 'N' 'V' 0D 0A
/// 8      format version (1)
/// 9      log2 of the chunk size (12..24)
/// 10..11 zero
/// 12..15 workspace data key version (big-endian)
/// 16..31 workspace id (big-endian GUID bytes)
/// 32..63 random salt
/// </code>
/// The per-object key is HKDF-SHA256(data key, salt = the whole header, info = "opportunity/object/v1\0" + logical key),
/// so every object has its own key, a header or ciphertext copied to another key does not decrypt, and nonces can be
/// the chunk counter: nonce = 00 00 00 {1 if last chunk else 0} || chunk index (64-bit big-endian). The last-chunk flag
/// makes truncation at a chunk boundary detectable. Chunks allow byte-range reads without decrypting the whole object.
/// </summary>
internal static class EnvelopeFormat
{
    public const int HeaderSize = 64;
    public const int TagSize = 16;
    public const int NonceSize = 12;
    public const byte Version1 = 1;
    public const int MinChunkSizeLog2 = 12;
    public const int MaxChunkSizeLog2 = 24;
    public const int DefaultChunkSizeLog2 = 16;

    private static readonly byte[] InfoPrefix = Encoding.ASCII.GetBytes("opportunity/object/v1\0");

    public static ReadOnlySpan<byte> Magic => [0x89, (byte)'O', (byte)'P', (byte)'E', (byte)'N', (byte)'V', 0x0D, 0x0A];

    public static byte[] CreateHeader(Guid workspaceId, int keyVersion, int chunkSizeLog2)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[8] = Version1;
        header[9] = (byte)chunkSizeLog2;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), keyVersion);
        workspaceId.TryWriteBytes(header.AsSpan(16, 16), bigEndian: true, out _);
        RandomNumberGenerator.Fill(header.AsSpan(32, 32));
        return header;
    }

    public static bool HasMagic(ReadOnlySpan<byte> bytes) => bytes.Length >= Magic.Length && bytes[..Magic.Length].SequenceEqual(Magic);

    /// <summary>Parses a header whose magic matched; anything else about it being wrong is tampering or corruption.</summary>
    public static EnvelopeHeader Parse(ObjectKey key, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !HasMagic(bytes))
        {
            throw new ObjectIntegrityException(key, "the envelope header is incomplete.");
        }

        if (bytes[8] != Version1 || bytes[9] is < MinChunkSizeLog2 or > MaxChunkSizeLog2 || bytes[10] != 0 || bytes[11] != 0)
        {
            throw new ObjectIntegrityException(key, "the envelope header has an unknown version or layout.");
        }

        var keyVersion = BinaryPrimitives.ReadInt32BigEndian(bytes[12..16]);
        var workspaceId = new Guid(bytes[16..32], bigEndian: true);
        if (keyVersion < 1 || workspaceId != key.WorkspaceId)
        {
            throw new ObjectIntegrityException(key, "the envelope header does not belong to this workspace.");
        }

        return new EnvelopeHeader(bytes[..HeaderSize].ToArray(), 1 << bytes[9], keyVersion, workspaceId);
    }

    public static byte[] DeriveObjectKey(ReadOnlySpan<byte> dataKey, EnvelopeHeader header, ObjectKey key)
    {
        var info = new byte[InfoPrefix.Length + Encoding.UTF8.GetByteCount(key.Value)];
        InfoPrefix.CopyTo(info, 0);
        Encoding.UTF8.GetBytes(key.Value, info.AsSpan(InfoPrefix.Length));
        var objectKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, objectKey, header.Raw, info);
        return objectKey;
    }

    public static void Nonce(Span<byte> nonce, long chunkIndex, bool last)
    {
        nonce[..4].Clear();
        nonce[3] = last ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..12], chunkIndex);
    }

    public static long ChunkCount(long plaintextLength, int chunkSize) =>
        plaintextLength == 0 ? 1 : ((plaintextLength - 1) / chunkSize) + 1;

    public static long CiphertextLength(long plaintextLength, int chunkSize) =>
        HeaderSize + plaintextLength + (TagSize * ChunkCount(plaintextLength, chunkSize));

    /// <summary>The plaintext length of a stored object of <paramref name="ciphertextLength"/> bytes; false when no plaintext has that size.</summary>
    public static bool TryPlaintextLength(long ciphertextLength, int chunkSize, out long plaintextLength)
    {
        plaintextLength = 0;
        var body = ciphertextLength - HeaderSize;
        if (body < TagSize)
        {
            return false;
        }

        var sealedChunk = (long)chunkSize + TagSize;
        var chunks = ((body - 1) / sealedChunk) + 1;
        var last = body - ((chunks - 1) * sealedChunk);
        if (last < TagSize || (last == TagSize && chunks > 1))
        {
            return false;
        }

        plaintextLength = body - (chunks * TagSize);
        return true;
    }
}

internal sealed record EnvelopeHeader(byte[] Raw, int ChunkSize, int KeyVersion, Guid WorkspaceId);
