namespace Opportunity.Import.LoadFiles;

/// <summary>Column names of a DAT with case-insensitive lookup (first occurrence wins for duplicates).</summary>
public sealed class DatHeader
{
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    public DatHeader(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        Names = names;
        for (int i = 0; i < names.Count; i++)
        {
            _index.TryAdd(names[i], i);
        }
    }

    public IReadOnlyList<string> Names { get; }

    public int Count => Names.Count;

    public int IndexOf(string name) => _index.TryGetValue(name.Trim(), out int i) ? i : -1;
}

/// <summary>One parsed data row. Values are decoded, unescaped and newline-converted; no typing (E08-T02).</summary>
public sealed class DatRecord
{
    internal DatRecord(DatHeader header, long rowNumber, long lineNumber, long byteOffset, int byteLength, string[] values, IReadOnlyList<DatIssue> issues, bool rejected, string? controlNumber)
    {
        Header = header;
        RowNumber = rowNumber;
        LineNumber = lineNumber;
        ByteOffset = byteOffset;
        ByteLength = byteLength;
        Values = values;
        Issues = issues;
        IsRejected = rejected;
        ControlNumber = controlNumber;
    }

    public DatHeader Header { get; }

    /// <summary>1-based data row (header excluded).</summary>
    public long RowNumber { get; }

    /// <summary>1-based physical line where the record starts.</summary>
    public long LineNumber { get; }

    public long ByteOffset { get; }

    /// <summary>Bytes of the record without its line terminator.</summary>
    public int ByteLength { get; }

    public IReadOnlyList<string> Values { get; }

    /// <summary>Warnings on an accepted row; errors (and warnings) on a rejected one.</summary>
    public IReadOnlyList<DatIssue> Issues { get; }

    /// <summary>Only returned when <see cref="DatReaderOptions.ReturnRejectedRecords"/> is set.</summary>
    public bool IsRejected { get; }

    /// <summary>Value of the key column (<see cref="DatReaderOptions.KeyColumn"/>), when present.</summary>
    public string? ControlNumber { get; }

    public string? this[string column] => Header.IndexOf(column) is var i and >= 0 && i < Values.Count ? Values[i] : null;
}
