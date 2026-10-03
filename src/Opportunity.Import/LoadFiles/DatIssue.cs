using System.Globalization;
using System.Text;

namespace Opportunity.Import.LoadFiles;

public enum DatIssueSeverity
{
    /// <summary>The row is kept; the importer may flag it.</summary>
    Warning,

    /// <summary>The row is rejected to the error stream (or, for a pre-flight issue, the file cannot be read).</summary>
    Error,
}

/// <summary>What went wrong. Names are part of the error-report contract.</summary>
public enum DatIssueKind
{
    /// <summary>The file has no header record.</summary>
    MissingHeader,

    /// <summary>Two header columns have the same name (case-insensitive, trimmed).</summary>
    DuplicateHeader,

    /// <summary>A header column has an empty name; it is named <c>Column{n}</c>.</summary>
    EmptyHeaderName,

    /// <summary>Delimiter profile or encoding unusable for this file.</summary>
    InvalidProfile,

    /// <summary>The row's field count differs from the header's.</summary>
    FieldCountMismatch,

    /// <summary>A qualifier inside a value is not doubled.</summary>
    UnescapedQualifier,

    /// <summary>A qualified value is not closed before the end of the record.</summary>
    UnterminatedQualifier,

    /// <summary>The record is longer than <see cref="DatReaderOptions.MaxRecordBytes"/>.</summary>
    RecordTooLong,

    /// <summary>The record has more fields than <see cref="DatReaderOptions.MaxFieldCount"/>.</summary>
    TooManyFields,

    /// <summary>The row's bytes are invalid in the file encoding; decoded with replacement characters.</summary>
    InvalidEncoding,

    /// <summary>The row is in a different encoding from the file and was re-decoded (mixed-encoding volume).</summary>
    MixedEncodingRow,

    /// <summary>The row looks like UTF-8 read as Windows-1252 (e.g. <c>Ã¾</c> for <c>þ</c>); kept as decoded.</summary>
    SuspectedMisdecode,
}

/// <summary>One problem found in a DAT, located for an admin: row, physical line, byte offset, key and column.</summary>
/// <param name="RowNumber">1-based data row (header excluded); 0 for header/file issues.</param>
/// <param name="LineNumber">1-based physical line where the record starts (header is line 1).</param>
/// <param name="ByteOffset">Offset of the record's first byte from the start of the file (byte-order mark included).</param>
/// <param name="ControlNumber">Value of the key column when it could be read.</param>
/// <param name="Column">Header column concerned, when known.</param>
public sealed record DatIssue(
    DatIssueSeverity Severity,
    DatIssueKind Kind,
    long RowNumber,
    long LineNumber,
    long ByteOffset,
    string Message,
    string? ControlNumber = null,
    string? Column = null,
    int? ExpectedFieldCount = null,
    int? ObservedFieldCount = null)
{
    /// <summary>One-line text for an <c>ImportError</c> column: ASCII only, so it survives any load-file encoding.</summary>
    public string ToErrorText()
    {
        var sb = new StringBuilder();
        sb.Append(Kind).Append(": ").Append(Message);
        if (Column != null)
        {
            sb.Append(" [column ").Append(Column).Append(']');
        }

        sb.Append(" (row ").Append(RowNumber.ToString(CultureInfo.InvariantCulture))
            .Append(", line ").Append(LineNumber.ToString(CultureInfo.InvariantCulture))
            .Append(", byte ").Append(ByteOffset.ToString(CultureInfo.InvariantCulture)).Append(')');
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] >= 0x7F || char.IsControl(sb[i]))
            {
                sb[i] = '?';
            }
        }

        return sb.ToString();
    }
}

/// <summary>Context handed to sinks once the header has been read.</summary>
/// <param name="HeaderRecord">Raw bytes of the header record (file encoding, without terminator); empty when <see cref="DatReaderOptions.HasHeader"/> is false.</param>
public sealed record DatFileContext(DelimiterProfile Profile, DetectedEncoding Encoding, IReadOnlyList<string> Header, ReadOnlyMemory<byte> HeaderRecord);

/// <summary>Receives every issue and every rejected row as the reader meets them (error report, re-loadable error file).</summary>
public interface IDatIssueSink
{
    void Start(DatFileContext context)
    {
    }

    void Issue(DatIssue issue)
    {
    }

    /// <param name="rawRecord">The row's bytes exactly as in the file, without its line terminator; empty when the row was too long to buffer.</param>
    /// <param name="issues">The issues that rejected it (and any warnings on the same row).</param>
    void RejectedRecord(ReadOnlySpan<byte> rawRecord, IReadOnlyList<DatIssue> issues)
    {
    }

    void Complete(DatReadStatistics statistics)
    {
    }
}

/// <summary>Counters of a read.</summary>
public sealed class DatReadStatistics
{
    private readonly long[] _byKind = new long[Enum.GetValues<DatIssueKind>().Length];

    /// <summary>Data rows framed (accepted + rejected), header excluded.</summary>
    public long Rows { get; internal set; }

    public long AcceptedRows { get; internal set; }

    public long RejectedRows { get; internal set; }

    public long RowsWithWarnings { get; internal set; }

    public long BlankLines { get; internal set; }

    /// <summary>Bytes consumed from the stream so far.</summary>
    public long Bytes { get; internal set; }

    public long Count(DatIssueKind kind) => _byKind[(int)kind];

    public IReadOnlyDictionary<DatIssueKind, long> IssueCounts =>
        Enum.GetValues<DatIssueKind>().Where(k => _byKind[(int)k] > 0).ToDictionary(k => k, k => _byKind[(int)k]);

    internal void Add(DatIssueKind kind) => _byKind[(int)kind]++;
}

/// <summary>The file cannot be read as a load file (e.g. duplicate headers); see <see cref="Issues"/>.</summary>
public sealed class DatPreflightException(IReadOnlyList<DatIssue> issues)
    : Exception("DAT pre-flight failed: " + string.Join("; ", issues.Where(i => i.Severity == DatIssueSeverity.Error).Select(i => i.Message)))
{
    public IReadOnlyList<DatIssue> Issues { get; } = issues;
}

/// <summary>Too many rows were rejected; the read stopped (see <see cref="Statistics"/>).</summary>
public sealed class DatErrorThresholdExceededException(string message, DatReadStatistics statistics) : Exception(message)
{
    public DatReadStatistics Statistics { get; } = statistics;
}
