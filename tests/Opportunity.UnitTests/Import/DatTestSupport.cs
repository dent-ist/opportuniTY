using Opportunity.Import.LoadFiles;

namespace Opportunity.UnitTests.Import;

internal sealed record DatParse(DatReader Reader, List<DatRecord> Records)
{
    public IReadOnlyList<DatIssue> Issues => Reader.Issues;

    public List<List<string>> Values => [.. Records.Select(r => r.Values.ToList())];
}

internal static class DatTestSupport
{
    public static byte[] Encode(string text, LoadFileEncodingKind kind, bool bom)
    {
        byte[] body = LoadFileEncodings.Get(kind).GetBytes(text);
        return bom ? [.. LoadFileEncodings.Preamble(kind), .. body] : body;
    }

    public static Task<DatParse> ParseAsync(string text, DatReaderOptions? options = null, LoadFileEncodingKind kind = LoadFileEncodingKind.Utf8, bool bom = false) =>
        ParseAsync(Encode(text, kind, bom), options);

    public static async Task<DatParse> ParseAsync(byte[] bytes, DatReaderOptions? options = null)
    {
        DatReader reader = await DatReader.OpenAsync(new MemoryStream(bytes), options, cancellationToken: TestContext.Current.CancellationToken);
        var records = new List<DatRecord>();
        await foreach (DatRecord record in reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            records.Add(record);
        }

        return new DatParse(reader, records);
    }

    /// <summary>Builds a DAT text: every value qualified (doubling the qualifier), CRLF row ends.</summary>
    public static string Dat(DelimiterProfile p, params string[][] rows) =>
        string.Concat(rows.Select(r => string.Join(p.Column, r.Select(v => p.Quote is { } q ? q + v.Replace(q.ToString(), new string(q, 2), StringComparison.Ordinal) + q : v)) + "\r\n"));

    /// <summary>A stream that yields at most <paramref name="chunk"/> bytes per read (exercises buffer boundaries).</summary>
    public sealed class TricklingStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, chunk), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
