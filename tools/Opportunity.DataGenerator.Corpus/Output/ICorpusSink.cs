using System.Security.Cryptography;

using Opportunity.DataGenerator.Corpus.Model;

namespace Opportunity.DataGenerator.Corpus.Output;

/// <summary>An output file recorded in the corpus manifest.</summary>
public sealed record CorpusOutputFile(string Path, long Bytes, string Sha256);

/// <summary>
/// Extension point for corpus outputs. Sinks receive chunks strictly in load order on one thread and must stream
/// (no per-corpus buffering). Load-file volume writers (DAT/OPT/TXT/NATIVES/IMAGES with defect injection,
/// E17-T02) plug in here alongside <see cref="JsonlCorpusWriter"/> and <see cref="GroundTruthWriter"/>; use
/// <see cref="Text.TextSynthesizer"/> to stream each document's extracted text.
/// </summary>
public interface ICorpusSink : IDisposable
{
    void Write(GeneratedChunk chunk);

    /// <summary>Flushes and closes outputs; returns the files to list in the manifest (paths relative to the output directory).</summary>
    IReadOnlyList<CorpusOutputFile> Complete();
}

/// <summary>Write-only stream wrapper that SHA-256 hashes and counts every byte written.</summary>
public sealed class HashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => BytesWritten;

    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public string HashHex() => "sha256:" + Convert.ToHexStringLower(_hash.GetCurrentHash());

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override void WriteByte(byte value) => Write([value]);

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}