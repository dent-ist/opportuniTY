using System.Buffers;
using System.Security.Cryptography;
using System.Text;

using Opportunity.Application.Import;
using Opportunity.Application.Storage;
using Opportunity.Import.LoadFiles;

using CoreArea = Opportunity.Core.Storage.ObjectArea;
using CoreScheme = Opportunity.Core.Storage.EncryptionScheme;

namespace Opportunity.Import.Volumes;

/// <summary>A volume file stored for a document, with the digests a load file may be checked against.</summary>
/// <param name="Uploaded">False when the content-addressed key already held the bytes (a retried chunk).</param>
public sealed record StoredVolumeFile(ImportObject Stored, byte[] Md5, byte[] Sha1, bool Uploaded);

/// <summary>Extracted text stored for a document as UTF-8.</summary>
/// <param name="Chars">Full length in UTF-16 code units (the unit of the indexed-text cap, Q-29).</param>
/// <param name="EncodingWarning">Invalid byte sequences were replaced (ADR-009 <c>TextEncodingWarning</c>).</param>
public sealed record StoredVolumeText(ImportObject Stored, long Chars, bool EncodingWarning, bool Uploaded);

/// <summary>
/// Streams volume files into object storage under content-addressed document keys (ADR-011 §1.5, §2): natives and
/// page images byte for byte, extracted text decoded (its own encoding detection or override) and stored as UTF-8, the
/// form the projection text loader and the viewer read. The digest is computed before the upload, so the key is known
/// and an object that already exists (a retried chunk) is not uploaded again. Shared by DAT natives/text (E08-T04) and
/// OPT page images (E08-T05).
/// </summary>
public sealed class VolumeObjectWriter(IObjectStore store)
{
    public const string TextContentType = "text/plain; charset=utf-8";

    private const int BufferSize = 81_920;

    /// <summary>Stores a file byte for byte as a <see cref="CoreArea.Native"/> or <see cref="CoreArea.Image"/> object.</summary>
    public async Task<StoredVolumeFile> StoreFileAsync(
        Guid workspaceId, Guid documentId, CoreArea area, string fullPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
#pragma warning disable CA5350, CA5351 // MD5 and SHA-1 are load-file verification values (ADR-009 R17), not security controls.
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5350, CA5351
        var head = new byte[ContentSniffer.SampleBytes];
        var headLength = 0;
        long length = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var file = Open(fullPath);
            await using (file.ConfigureAwait(false))
            {
                int n;
                while ((n = await file.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    sha256.AppendData(buffer, 0, n);
                    md5.AppendData(buffer, 0, n);
                    sha1.AppendData(buffer, 0, n);
                    if (headLength < head.Length)
                    {
                        var take = Math.Min(head.Length - headLength, n);
                        buffer.AsSpan(0, take).CopyTo(head.AsSpan(headLength));
                        headLength += take;
                    }

                    length += n;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var digest = Sha256Digest.FromBytes(sha256.GetHashAndReset());
        var key = area switch
        {
            CoreArea.Native => ObjectKeys.Native(workspaceId, documentId, digest),
            CoreArea.Image => ObjectKeys.Image(workspaceId, documentId, digest),
            _ => throw new ArgumentOutOfRangeException(nameof(area), area, "Volume files are stored as natives or images."),
        };
        var contentType = ContentSniffer.Sniff(head.AsSpan(0, headLength));
        var (stored, uploaded) = await PutIfAbsentAsync(key, () => Open(fullPath), length, contentType, cancellationToken).ConfigureAwait(false);
        return new StoredVolumeFile(
            new ImportObject(documentId, area, key.Value, digest.ToBytes(), length, contentType, stored.KeyId, stored.Scheme),
            md5.GetHashAndReset(),
            sha1.GetHashAndReset(),
            uploaded);
    }

    /// <summary>Decodes an extracted-text file and stores it as UTF-8 (without byte-order mark).</summary>
    public async Task<StoredVolumeText> StoreTextFileAsync(
        Guid workspaceId, Guid documentId, string fullPath, LoadFileEncodingKind? encoding, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);

        // Decoded once to a private temporary file (texts can exceed 100 MB); it disappears when closed.
        var temp = new FileStream(
            Path.Combine(Path.GetTempPath(), "opp-text-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await using (temp.ConfigureAwait(false))
        {
            ExtractedTextResult decoded;
            Sha256Digest digest;
            var hashing = new HashingWriteStream(temp);
            await using (hashing.ConfigureAwait(false))
            {
                var writer = new StreamWriter(hashing, new UTF8Encoding(false), BufferSize, leaveOpen: true);
                await using (writer.ConfigureAwait(false))
                {
                    var input = Open(fullPath);
                    await using (input.ConfigureAwait(false))
                    {
                        decoded = await ExtractedTextDecoder.DecodeAsync(input, writer, encoding, cancellationToken).ConfigureAwait(false);
                    }

                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                digest = hashing.Digest();
            }

            var length = temp.Length;
            var key = ObjectKeys.Text(workspaceId, documentId, digest);
            var (stored, uploaded) = await PutIfAbsentAsync(key, () =>
            {
                temp.Position = 0;
                return new NonClosingStream(temp);
            }, length, TextContentType, cancellationToken).ConfigureAwait(false);
            return new StoredVolumeText(
                new ImportObject(documentId, CoreArea.Text, key.Value, digest.ToBytes(), length, TextContentType, stored.KeyId, stored.Scheme),
                decoded.Chars,
                decoded.TextEncodingWarning,
                uploaded);
        }
    }

    /// <summary>Stores extracted text given in the load file itself (text-in-DAT mode) as UTF-8.</summary>
    public async Task<StoredVolumeText> StoreTextAsync(Guid workspaceId, Guid documentId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        var digest = Sha256Digest.Compute(bytes);
        var key = ObjectKeys.Text(workspaceId, documentId, digest);
        var (stored, uploaded) = await PutIfAbsentAsync(key, () => new MemoryStream(bytes, writable: false), bytes.Length, TextContentType, cancellationToken)
            .ConfigureAwait(false);
        return new StoredVolumeText(
            new ImportObject(documentId, CoreArea.Text, key.Value, digest.ToBytes(), bytes.Length, TextContentType, stored.KeyId, stored.Scheme),
            text.Length,
            false,
            uploaded);
    }

    /// <summary>
    /// Uploads unless the content-addressed key already holds the object: a retried chunk then sends no bytes. The
    /// store's own write-once check still guards a racing writer.
    /// </summary>
    private async Task<((string KeyId, CoreScheme Scheme) Stored, bool Uploaded)> PutIfAbsentAsync(
        ObjectKey key, Func<Stream> open, long length, string contentType, CancellationToken cancellationToken)
    {
        if (await store.HeadAsync(key, cancellationToken).ConfigureAwait(false) is { KeyId: { } existingKeyId } existing && existing.Length == length)
        {
            return ((existingKeyId, CoreScheme.ProviderSse), false);
        }

        var content = open();
        await using (content.ConfigureAwait(false))
        {
            var result = await store.PutAsync(key, content, new PutObjectOptions { ContentType = contentType, ExpectedLength = length }, cancellationToken)
                .ConfigureAwait(false);
            var scheme = result.EncryptionScheme == EncryptionScheme.Envelope ? CoreScheme.Envelope : CoreScheme.ProviderSse;
            return ((result.KeyId, scheme), result.Outcome == PutOutcome.Created);
        }
    }

    private static FileStream Open(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

    /// <summary>Hashes (SHA-256) what passes through to the inner stream.</summary>
    private sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public Sha256Digest Digest() => Sha256Digest.FromBytes(_hash.GetHashAndReset());

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Reads the temporary file without closing it (the put disposes what it is given).</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
