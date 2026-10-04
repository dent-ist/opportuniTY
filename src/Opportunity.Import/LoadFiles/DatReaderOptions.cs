namespace Opportunity.Import.LoadFiles;

/// <summary>Settings of <see cref="DatReader"/>. Defaults suit a Concordance DAT as exported by common review/processing platforms.</summary>
public sealed class DatReaderOptions
{
    public DelimiterProfile Profile { get; init; } = DelimiterProfile.Concordance;

    /// <summary>Explicit encoding; null detects it (byte-order mark, then heuristics).</summary>
    public LoadFileEncodingKind? EncodingOverride { get; init; }

    /// <summary>
    /// Re-decode a row that is invalid in the file encoding with the other 8-bit encoding (Windows-1252 rows in a
    /// UTF-8 file, UTF-8 rows in a 1252 file) and report it as a warning. Null: on when the encoding was detected,
    /// off under an explicit override (the user's choice is honoured and suspected mis-decodes are only reported).
    /// </summary>
    public bool? RowEncodingFallback { get; init; }

    /// <summary>First record is the header. When false, columns are named <c>Column1…N</c> from the first row.</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>Duplicate header names (case-insensitive) are a pre-flight error unless allowed (then a warning).</summary>
    public bool AllowDuplicateHeaders { get; init; }

    /// <summary>Header column holding the record key, reported with every row issue. Null: the first column.</summary>
    public string? KeyColumn { get; init; }

    /// <summary>Convert the profile's newline character (<c>®</c>) to <c>\n</c>; false keeps it literally.</summary>
    public bool ConvertNewlineCharacter { get; init; } = true;

    /// <summary>
    /// Literal CR/LF inside a qualified value continue the record. Null: only when the profile has no newline
    /// character (CSV). For DATs every physical line is a record, so one broken qualifier cannot swallow later rows.
    /// </summary>
    public bool? AllowLineBreaksInQualifiedValues { get; init; }

    /// <summary>A row with an unescaped (stray) qualifier is rejected; false accepts it with the qualifier kept literally.</summary>
    public bool RejectStrayQualifiers { get; init; } = true;

    /// <summary>Largest record in bytes (SEC-15). Longer records are skipped without buffering and rejected.</summary>
    public int MaxRecordBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Most fields per record (SEC-15).</summary>
    public int MaxFieldCount { get; init; } = 2048;

    /// <summary>Abort with <see cref="DatErrorThresholdExceededException"/> once more rows than this are rejected; null = no limit.</summary>
    public long? MaxRejectedRows { get; init; } = 10_000;

    /// <summary>Abort once the rejected share of data rows exceeds this (checked after <see cref="MinRowsForRejectedRate"/> rows); null = no limit.</summary>
    public double? MaxRejectedRate { get; init; }

    public long MinRowsForRejectedRate { get; init; } = 1_000;

    /// <summary>Issues kept in <see cref="DatReader.Issues"/>; all issues still reach the sinks and the counters.</summary>
    public int MaxRetainedIssues { get; init; } = 1_000;

    /// <summary>Also return rejected rows from <see cref="DatReader.ReadAsync"/> (flagged <see cref="DatRecord.IsRejected"/>), e.g. for preview.</summary>
    public bool ReturnRejectedRecords { get; init; }

    /// <summary>Copy each returned record's raw bytes into <see cref="DatRecord.RawRecord"/> (re-loadable error file, E08-T06).</summary>
    public bool CaptureRawRecords { get; init; }

    /// <summary>Receivers of issues and rejected rows (error file, error report).</summary>
    public IReadOnlyList<IDatIssueSink> Sinks { get; init; } = [];

    /// <summary>Bytes read before decoding anything, used for encoding detection; also the initial buffer size.</summary>
    public int BufferBytes { get; init; } = EncodingDetector.DefaultSampleBytes;

    internal void Validate()
    {
        var errors = new List<string>(Profile.Validate());
        if (MaxRecordBytes < 16)
        {
            errors.Add("MaxRecordBytes must be ≥ 16");
        }

        if (MaxFieldCount < 1)
        {
            errors.Add("MaxFieldCount must be ≥ 1");
        }

        if (BufferBytes < 64)
        {
            errors.Add("BufferBytes must be ≥ 64");
        }

        if (MaxRejectedRate is { } rate && (rate < 0 || double.IsNaN(rate)))
        {
            errors.Add("MaxRejectedRate must be ≥ 0");
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid DAT reader options: " + string.Join("; ", errors));
        }
    }
}
