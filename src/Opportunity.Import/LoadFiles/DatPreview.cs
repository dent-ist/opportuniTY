namespace Opportunity.Import.LoadFiles;

/// <summary>Pre-flight preview of a DAT: detected encoding, header and the first rows as parsed (rejected rows included).</summary>
/// <param name="MisdecodeSuspected">
/// Values look like UTF-8 read as Windows-1252 (<c>Ã¾</c> for <c>þ</c>, <c>Â¶</c> for <c>¶</c>): the encoding or
/// override is probably wrong. The raw values are shown as decoded so the problem is visible.
/// </param>
public sealed record DatPreviewResult(
    DetectedEncoding Encoding,
    DelimiterProfile Profile,
    IReadOnlyList<string> Header,
    IReadOnlyList<DatRecord> Rows,
    IReadOnlyList<DatIssue> Issues,
    bool HasPreflightErrors,
    bool MisdecodeSuspected);

public static class DatPreview
{
    public const int DefaultRows = 20;

    public static async Task<DatPreviewResult> ReadAsync(Stream stream, DatReaderOptions? options = null, int rows = DefaultRows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new DatReaderOptions();
        DatReaderOptions preview = new()
        {
            Profile = options.Profile,
            EncodingOverride = options.EncodingOverride,
            RowEncodingFallback = options.RowEncodingFallback,
            HasHeader = options.HasHeader,
            AllowDuplicateHeaders = options.AllowDuplicateHeaders,
            KeyColumn = options.KeyColumn,
            ConvertNewlineCharacter = options.ConvertNewlineCharacter,
            AllowLineBreaksInQualifiedValues = options.AllowLineBreaksInQualifiedValues,
            RejectStrayQualifiers = options.RejectStrayQualifiers,
            MaxRecordBytes = options.MaxRecordBytes,
            MaxFieldCount = options.MaxFieldCount,
            BufferBytes = options.BufferBytes,
            MaxRejectedRows = null,
            MaxRejectedRate = null,
            ReturnRejectedRecords = true,
        };

        await using DatReader reader = await DatReader.OpenAsync(stream, preview, leaveOpen: true, cancellationToken).ConfigureAwait(false);
        var records = new List<DatRecord>();
        if (!reader.HasPreflightErrors)
        {
            while (records.Count < rows && await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is { } record)
            {
                records.Add(record);
            }
        }

        bool misdecode = reader.Issues.Any(i => i.Kind == DatIssueKind.SuspectedMisdecode)
            || reader.Header.Names.Any(LooksMisdecoded)
            || records.Any(r => r.Values.Any(LooksMisdecoded));
        return new DatPreviewResult(reader.Encoding, reader.Profile, reader.Header.Names, records, [.. reader.Issues], reader.HasPreflightErrors, misdecode);
    }

    /// <summary>UTF-8 lead byte C2/C3 shown as <c>Â</c>/<c>Ã</c> followed by a continuation byte shown as U+0080–U+00BF.</summary>
    public static bool LooksMisdecoded(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        for (int i = value.IndexOfAny(['Ã', 'Â']); i >= 0 && i + 1 < value.Length; i = value.IndexOfAny(['Ã', 'Â'], i + 1))
        {
            char next = value[i + 1];
            if (next is >= '\u0080' and <= '¿' || Windows1252Encoding.TryEncode(next, out byte b) && b is >= 0x80 and <= 0xBF)
            {
                return true;
            }
        }

        return false;
    }
}
