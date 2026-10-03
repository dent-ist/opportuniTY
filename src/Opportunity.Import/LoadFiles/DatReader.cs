using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

using Utf8Unicode = System.Text.Unicode.Utf8;

namespace Opportunity.Import.LoadFiles;

/// <summary>
/// Streaming Concordance DAT / CSV reader (E08-T01). Records are framed on the raw bytes (qualifier-aware, so
/// line terminators inside qualified values are handled in the file encoding, including UTF-16), then each record is
/// decoded on its own — which is what lets a mis-encoded row be detected, located and re-decoded without affecting
/// its neighbours — and split into fields. Memory is one buffer of at most
/// <see cref="DatReaderOptions.MaxRecordBytes"/> plus one decoded record, whatever the file size.
/// Malformed rows are reported (row, line, byte offset, key, column, reason) and rejected without stopping the read
/// until <see cref="DatReaderOptions.MaxRejectedRows"/> / <see cref="DatReaderOptions.MaxRejectedRate"/> is exceeded.
/// </summary>
public sealed class DatReader : IAsyncDisposable, IDisposable
{
    private const int Lookahead = 16;

    private static readonly UnicodeEncoding StrictUtf16LE = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16BE = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly DatReaderOptions _o;
    private readonly List<DatIssue> _issues = [];
    private readonly List<DatIssue> _preflight = [];
    private readonly List<string> _fields = [];
    private readonly StringBuilder _value = new();
    private readonly List<DatIssue> _rowIssues = [];

    private byte[] _buf;
    private int _start;
    private int _end;
    private int _scan;
    private long _bufferOffset;
    private bool _eof;
    private bool _inQuote;
    private bool _atFieldStart = true;
    private int _contentEnd;
    private int _next;

    private bool _skipping;
    private long _skipStartOffset;
    private long _skipEndOffset;

    private long _line = 1;
    private long _row;
    private bool _completed;
    private bool _disposed;

    private int _unit = 1;
    private byte[] _q = [];
    private byte[] _col = [];
    private byte[] _cr = [];
    private byte[] _lf = [];
    private SearchValues<byte> _firstBytes = SearchValues.Create(ReadOnlySpan<byte>.Empty);
    private bool _allowBreaks;
    private bool _fallback;
    private bool _asciiTentative;
    private char[] _chars = [];
    private int _keyIndex;

    private DatReader(Stream stream, DatReaderOptions options, bool leaveOpen)
    {
        _stream = stream;
        _o = options;
        _leaveOpen = leaveOpen;
        _buf = ArrayPool<byte>.Shared.Rent(options.BufferBytes);
        Header = new DatHeader([]);
        Encoding = new DetectedEncoding(LoadFileEncodingKind.Utf8, EncodingSource.Heuristic, 0);
    }

    public DelimiterProfile Profile => _o.Profile;

    public DetectedEncoding Encoding { get; private set; }

    public DatHeader Header { get; private set; }

    /// <summary>Raw bytes of the header record (file encoding, no terminator).</summary>
    public ReadOnlyMemory<byte> HeaderRecord { get; private set; }

    /// <summary>Header and file-level issues found by <see cref="OpenAsync"/>.</summary>
    public IReadOnlyList<DatIssue> PreflightIssues => _preflight;

    public bool HasPreflightErrors => _preflight.Any(i => i.Severity == DatIssueSeverity.Error);

    /// <summary>The first <see cref="DatReaderOptions.MaxRetainedIssues"/> issues (pre-flight included).</summary>
    public IReadOnlyList<DatIssue> Issues => _issues;

    public DatReadStatistics Statistics { get; } = new();

    /// <summary>Detects the encoding, reads the header and runs the pre-flight checks. Never throws for file content.</summary>
    public static async Task<DatReader> OpenAsync(Stream stream, DatReaderOptions? options = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new DatReaderOptions();
        options.Validate();
        var reader = new DatReader(stream, options, leaveOpen);
        try
        {
            await reader.InitAsync(cancellationToken).ConfigureAwait(false);
            return reader;
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Next accepted row (or rejected row when <see cref="DatReaderOptions.ReturnRejectedRecords"/>); null at end of file.</summary>
    /// <exception cref="DatPreflightException">The header failed pre-flight.</exception>
    /// <exception cref="DatErrorThresholdExceededException">Too many rows were rejected.</exception>
    public async ValueTask<DatRecord?> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (HasPreflightErrors)
        {
            throw new DatPreflightException(_preflight);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await NextFrameAsync(cancellationToken).ConfigureAwait(false))
            {
                case Frame.Record:
                    if (ProcessRecord() is { } record)
                    {
                        return record;
                    }

                    break;
                case Frame.Skipped:
                    if (ProcessTooLong() is { } tooLong)
                    {
                        return tooLong;
                    }

                    break;
                default:
                    Complete();
                    return null;
            }
        }
    }

    public async IAsyncEnumerable<DatRecord> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await ReadAsync(cancellationToken).ConfigureAwait(false) is { } record)
        {
            yield return record;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buf);
        _buf = [];
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buf);
        _buf = [];
    }

    // ---- set-up ----------------------------------------------------------------------------------------------

    private async Task InitAsync(CancellationToken ct)
    {
        while (!_eof && _end < _o.BufferBytes)
        {
            await FillAsync(ct).ConfigureAwait(false);
        }

        Encoding = EncodingDetector.Detect(_buf.AsSpan(0, _end), _eof, _o.EncodingOverride);
        _start = _scan = Encoding.PreambleLength;
        _fallback = _o.RowEncodingFallback ?? (Encoding.Source != EncodingSource.Override);
        _asciiTentative = Encoding.Source == EncodingSource.Heuristic && Encoding.AsciiOnly;
        _allowBreaks = _o.AllowLineBreaksInQualifiedValues ?? (_o.Profile.Newline == null);

        IReadOnlyList<string> profileErrors = _o.Profile.ValidateFor(Encoding.Kind);
        if (profileErrors.Count > 0)
        {
            foreach (string error in profileErrors)
            {
                AddPreflight(DatIssueKind.InvalidProfile, DatIssueSeverity.Error, error);
            }

            return;
        }

        SetUpTokens();
        Frame first = await NextFrameAsync(ct).ConfigureAwait(false);
        if (first == Frame.End)
        {
            AddPreflight(DatIssueKind.MissingHeader, DatIssueSeverity.Error, "The file is empty: no header record.");
            return;
        }

        if (first == Frame.Skipped)
        {
            AddPreflight(DatIssueKind.RecordTooLong, DatIssueSeverity.Error, $"The first record exceeds the {_o.MaxRecordBytes}-byte record limit.");
            return;
        }

        ReadOnlySpan<byte> raw = _buf.AsSpan(_start, _contentEnd - _start);
        _rowIssues.Clear();
        int charCount = Decode(raw, 0, 1, _bufferOffset + _start);
        SplitResult split = Split(_chars.AsSpan(0, charCount));
        if (_o.HasHeader)
        {
            HeaderRecord = raw.ToArray();
            foreach (DatIssue issue in _rowIssues)
            {
                AddPreflight(issue.Kind, DatIssueSeverity.Warning, "Header: " + issue.Message);
            }

            BuildHeader(split);
            _line += 1 + CountLineFeeds(_chars.AsSpan(0, charCount));
            Consume();
        }
        else
        {
            Header = new DatHeader([.. Enumerable.Range(1, _fields.Count).Select(i => "Column" + i)]);
            Rewind();
        }

        _keyIndex = 0;
        if (_o.KeyColumn != null)
        {
            _keyIndex = Header.IndexOf(_o.KeyColumn);
            if (_keyIndex < 0)
            {
                AddPreflight(DatIssueKind.InvalidProfile, DatIssueSeverity.Warning, $"Key column '{_o.KeyColumn}' is not in the header; row issues will not name a control number.");
            }
        }

        var context = new DatFileContext(_o.Profile, Encoding, Header.Names, HeaderRecord);
        foreach (IDatIssueSink sink in _o.Sinks)
        {
            sink.Start(context);
        }
    }

    private void SetUpTokens()
    {
        Encoding enc = LoadFileEncodings.Get(Encoding.Kind);
        _unit = LoadFileEncodings.UnitSize(Encoding.Kind);
        _col = enc.GetBytes(_o.Profile.Column.ToString());
        _q = _o.Profile.Quote is { } q ? enc.GetBytes(q.ToString()) : [];
        _cr = enc.GetBytes("\r");
        _lf = enc.GetBytes("\n");
        byte[] first = _q.Length > 0 ? [_col[0], _cr[0], _lf[0], _q[0]] : [_col[0], _cr[0], _lf[0]];
        _firstBytes = SearchValues.Create([.. first.Distinct()]);
    }

    private void BuildHeader(SplitResult split)
    {
        if (split.StrayColumn >= 0 || split.UnterminatedColumn >= 0)
        {
            AddPreflight(split.StrayColumn >= 0 ? DatIssueKind.UnescapedQualifier : DatIssueKind.UnterminatedQualifier, DatIssueSeverity.Error,
                "The header record has a malformed qualifier; check the delimiter profile and encoding.");
        }

        if (split.TooManyFields)
        {
            AddPreflight(DatIssueKind.TooManyFields, DatIssueSeverity.Error, $"The header has more than {_o.MaxFieldCount} columns.");
        }

        var names = new string[_fields.Count];
        for (int i = 0; i < names.Length; i++)
        {
            string name = _fields[i].Trim().TrimStart('﻿').Trim();
            if (name.Length == 0)
            {
                name = "Column" + (i + 1);
                AddPreflight(DatIssueKind.EmptyHeaderName, DatIssueSeverity.Warning, $"Header column {i + 1} has no name; named '{name}'.", column: name);
            }

            names[i] = name;
        }

        foreach (IGrouping<string, int> group in Enumerable.Range(0, names.Length).GroupBy(i => names[i], StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            AddPreflight(DatIssueKind.DuplicateHeader, _o.AllowDuplicateHeaders ? DatIssueSeverity.Warning : DatIssueSeverity.Error,
                $"Header column '{group.Key}' appears {group.Count()} times (positions {string.Join(", ", group.Select(i => i + 1))}).", column: group.Key);
        }

        Header = new DatHeader(names);
    }

    private void AddPreflight(DatIssueKind kind, DatIssueSeverity severity, string message, string? column = null)
    {
        var issue = new DatIssue(severity, kind, 0, 1, Encoding.PreambleLength, message, Column: column);
        _preflight.Add(issue);
        Report(issue);
    }

    // ---- framing ---------------------------------------------------------------------------------------------

    private enum Frame
    {
        Record,
        Skipped,
        End,
        NeedMore,
    }

    private async ValueTask<Frame> NextFrameAsync(CancellationToken ct)
    {
        while (true)
        {
            Frame frame = _skipping ? TrySkip() : TryFrame();
            if (frame != Frame.NeedMore)
            {
                return frame;
            }

            if (!_skipping && _end - _start >= _o.MaxRecordBytes)
            {
                _skipping = true;
                _skipStartOffset = _bufferOffset + _start;
            }

            if (_skipping)
            {
                _start = _scan;
            }

            await FillAsync(ct).ConfigureAwait(false);
        }
    }

    private async ValueTask FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
            _scan -= _start;
            _end -= _start;
            _bufferOffset += _start;
            _contentEnd -= _start;
            _next -= _start;
            _start = 0;
        }

        if (_end == _buf.Length)
        {
            int size = (int)Math.Min((long)_buf.Length * 2, (long)_o.MaxRecordBytes + Lookahead + _o.BufferBytes);
            byte[] bigger = ArrayPool<byte>.Shared.Rent(size);
            Buffer.BlockCopy(_buf, 0, bigger, 0, _end);
            ArrayPool<byte>.Shared.Return(_buf);
            _buf = bigger;
        }

        int n = await _stream.ReadAsync(_buf.AsMemory(_end), ct).ConfigureAwait(false);
        if (n == 0)
        {
            _eof = true;
        }

        _end += n;
        Statistics.Bytes += n;
    }

    private int AlignDown(int position) => _start + ((position - _start) & ~(_unit - 1));

    private bool At(int position, byte[] token) =>
        token.Length > 0 && position + token.Length <= _end && _buf.AsSpan(position, token.Length).SequenceEqual(token);

    private bool AtLineEnd(int position, out int next)
    {
        if (At(position, _lf))
        {
            next = position + _lf.Length;
            return true;
        }

        if (At(position, _cr) && At(position + _cr.Length, _lf))
        {
            next = position + _cr.Length + _lf.Length;
            return true;
        }

        next = 0;
        return false;
    }

    /// <summary>Finds the end of the record starting at <see cref="_start"/>, resuming at <see cref="_scan"/>.</summary>
    private Frame TryFrame()
    {
        int pos = _scan;
        while (true)
        {
            int rel = _buf.AsSpan(pos, _end - pos).IndexOfAny(_firstBytes);
            if (rel < 0)
            {
                if (!_eof)
                {
                    int aligned = AlignDown(_end);
                    if (aligned > pos && !_inQuote)
                    {
                        _atFieldStart = false;
                    }

                    _scan = aligned;
                    return Frame.NeedMore;
                }

                if (_end == _start)
                {
                    return Frame.End;
                }

                _contentEnd = _end;
                _next = _end;
                return Frame.Record;
            }

            int candidate = pos + rel;
            if (_unit == 2 && ((candidate - _start) & 1) != 0)
            {
                // The unit at candidate - 1 is ordinary text.
                if (!_inQuote)
                {
                    _atFieldStart = false;
                }

                pos = candidate + 1;
                continue;
            }

            if (candidate > pos && !_inQuote)
            {
                _atFieldStart = false;
            }

            pos = candidate;
            if (!_eof && _end - pos < Lookahead)
            {
                _scan = pos;
                return Frame.NeedMore;
            }

            if (_inQuote)
            {
                if (At(pos, _q))
                {
                    int after = pos + _q.Length;
                    if (At(after, _q))
                    {
                        pos = after + _q.Length;
                    }
                    else
                    {
                        if (after >= _end || At(after, _col) || AtLineEnd(after, out _))
                        {
                            _inQuote = false;
                        }

                        pos = after;
                    }

                    continue;
                }

                if (!_allowBreaks && AtLineEnd(pos, out int breakNext))
                {
                    _contentEnd = pos;
                    _next = breakNext;
                    return Frame.Record;
                }

                pos = Math.Min(pos + _unit, _end);
                continue;
            }

            if (At(pos, _col))
            {
                _atFieldStart = true;
                pos += _col.Length;
                continue;
            }

            if (AtLineEnd(pos, out int next))
            {
                _contentEnd = pos;
                _next = next;
                return Frame.Record;
            }

            if (At(pos, _q))
            {
                _inQuote = _atFieldStart;
                _atFieldStart = false;
                pos += _q.Length;
                continue;
            }

            _atFieldStart = false;
            pos = Math.Min(pos + _unit, _end);
        }
    }

    /// <summary>Discards an over-long record up to its next line feed (qualifiers ignored), never buffering it.</summary>
    private Frame TrySkip()
    {
        int pos = _scan;
        while (true)
        {
            int rel = _buf.AsSpan(pos, _end - pos).IndexOf(_lf[0]);
            if (rel < 0)
            {
                if (_eof)
                {
                    _skipEndOffset = _bufferOffset + _end;
                    _start = _scan = _end;
                    ResetRecordState();
                    return Frame.Skipped;
                }

                _scan = AlignDown(_end);
                return Frame.NeedMore;
            }

            int candidate = pos + rel;
            if (_unit == 2 && ((candidate - _start) & 1) != 0)
            {
                pos = candidate + 1;
                continue;
            }

            if (!_eof && candidate + _lf.Length > _end)
            {
                _scan = candidate;
                return Frame.NeedMore;
            }

            if (At(candidate, _lf))
            {
                _skipEndOffset = _bufferOffset + candidate;
                _start = _scan = candidate + _lf.Length;
                ResetRecordState();
                return Frame.Skipped;
            }

            pos = Math.Min(candidate + _unit, _end);
        }
    }

    private void ResetRecordState()
    {
        _skipping = false;
        _inQuote = false;
        _atFieldStart = true;
    }

    private void Consume()
    {
        _start = _scan = _next;
        ResetRecordState();
    }

    private void Rewind()
    {
        _scan = _start;
        ResetRecordState();
    }

    // ---- records ---------------------------------------------------------------------------------------------

    private DatRecord? ProcessRecord()
    {
        int recordStart = _start;
        long offset = _bufferOffset + recordStart;
        long line = _line;
        int length = _contentEnd - recordStart;
        if (length == 0)
        {
            Statistics.BlankLines++;
            _line++;
            Consume();
            return null;
        }

        long row = ++_row;
        Statistics.Rows++;
        _rowIssues.Clear();
        ReadOnlySpan<byte> raw = _buf.AsSpan(recordStart, length);
        int charCount = Decode(raw, row, line, offset);
        ReadOnlySpan<char> chars = _chars.AsSpan(0, charCount);
        _line += 1 + CountLineFeeds(chars);
        SplitResult split = Split(chars);
        string? key = _keyIndex >= 0 && _keyIndex < _fields.Count ? _fields[_keyIndex] : null;
        if (split.StrayColumn >= 0)
        {
            _rowIssues.Add(new DatIssue(_o.RejectStrayQualifiers ? DatIssueSeverity.Error : DatIssueSeverity.Warning, DatIssueKind.UnescapedQualifier, row, line, offset,
                $"Unescaped qualifier '{_o.Profile.Quote}' inside a value ({split.StrayCount} found); a literal qualifier must be doubled.", Column: ColumnName(split.StrayColumn)));
        }

        if (split.UnterminatedColumn >= 0)
        {
            _rowIssues.Add(new DatIssue(DatIssueSeverity.Error, DatIssueKind.UnterminatedQualifier, row, line, offset,
                "A qualified value is not closed before the end of the record.", Column: ColumnName(split.UnterminatedColumn)));
        }

        if (split.TooManyFields)
        {
            _rowIssues.Add(new DatIssue(DatIssueSeverity.Error, DatIssueKind.TooManyFields, row, line, offset,
                $"The record has more than {_o.MaxFieldCount} fields.", ExpectedFieldCount: Header.Count));
        }
        else if (_fields.Count != Header.Count)
        {
            _rowIssues.Add(new DatIssue(DatIssueSeverity.Error, DatIssueKind.FieldCountMismatch, row, line, offset,
                $"Expected {Header.Count} fields (header), found {_fields.Count}.", ExpectedFieldCount: Header.Count, ObservedFieldCount: _fields.Count));
        }

        DatIssue[] issues = _rowIssues.Count == 0 ? [] : [.. _rowIssues.Select(i => i with { ControlNumber = key })];
        bool rejected = false;
        foreach (DatIssue issue in issues)
        {
            Report(issue);
            rejected |= issue.Severity == DatIssueSeverity.Error;
        }

        DatRecord? result = null;
        if (rejected)
        {
            Statistics.RejectedRows++;
            foreach (IDatIssueSink sink in _o.Sinks)
            {
                sink.RejectedRecord(raw, issues);
            }

            if (_o.ReturnRejectedRecords)
            {
                result = new DatRecord(Header, row, line, offset, length, [.. _fields], issues, true, key);
            }
        }
        else
        {
            Statistics.AcceptedRows++;
            if (issues.Length > 0)
            {
                Statistics.RowsWithWarnings++;
            }

            result = new DatRecord(Header, row, line, offset, length, [.. _fields], issues, false, key);
        }

        Consume();
        if (rejected)
        {
            CheckThresholds();
        }

        return result;
    }

    private DatRecord? ProcessTooLong()
    {
        long row = ++_row;
        long line = _line++;
        Statistics.Rows++;
        Statistics.RejectedRows++;
        long length = _skipEndOffset - _skipStartOffset;
        DatIssue issue = new(DatIssueSeverity.Error, DatIssueKind.RecordTooLong, row, line, _skipStartOffset,
            $"The record is {length} bytes or longer, over the {_o.MaxRecordBytes}-byte limit; it was skipped up to the next line feed.");
        Report(issue);
        foreach (IDatIssueSink sink in _o.Sinks)
        {
            sink.RejectedRecord([], [issue]);
        }

        DatRecord? result = _o.ReturnRejectedRecords ? new DatRecord(Header, row, line, _skipStartOffset, 0, [], [issue], true, null) : null;
        CheckThresholds();
        return result;
    }

    private string ColumnName(int index) => index < Header.Count ? Header.Names[index] : "#" + (index + 1);

    private void Report(DatIssue issue)
    {
        Statistics.Add(issue.Kind);
        if (_issues.Count < _o.MaxRetainedIssues)
        {
            _issues.Add(issue);
        }

        foreach (IDatIssueSink sink in _o.Sinks)
        {
            sink.Issue(issue);
        }
    }

    private void CheckThresholds()
    {
        if (_o.MaxRejectedRows is { } max && Statistics.RejectedRows > max)
        {
            throw new DatErrorThresholdExceededException(
                $"Stopped at row {_row}: {Statistics.RejectedRows} rows rejected, over the limit of {max}.", Statistics);
        }

        if (_o.MaxRejectedRate is { } rate && Statistics.Rows >= _o.MinRowsForRejectedRate && Statistics.RejectedRows > rate * Statistics.Rows)
        {
            throw new DatErrorThresholdExceededException(
                $"Stopped at row {_row}: {Statistics.RejectedRows} of {Statistics.Rows} rows rejected, over the {rate:P1} limit.", Statistics);
        }
    }

    private void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        foreach (IDatIssueSink sink in _o.Sinks)
        {
            sink.Complete(Statistics);
        }
    }

    // ---- decoding --------------------------------------------------------------------------------------------

    private int Decode(ReadOnlySpan<byte> raw, long row, long line, long offset)
    {
        if (_chars.Length < raw.Length + 1)
        {
            _chars = new char[Math.Max(raw.Length + 1, _chars.Length * 2)];
        }

        Span<char> chars = _chars;
        switch (Encoding.Kind)
        {
            case LoadFileEncodingKind.Utf8:
                {
                    if (Utf8Unicode.ToUtf16(raw, chars, out int read, out int written, replaceInvalidSequences: false) == OperationStatus.Done)
                    {
                        _asciiTentative &= written == raw.Length;
                        return written;
                    }

                    if (_asciiTentative)
                    {
                        // The sample was pure ASCII; the first non-ASCII row decides between UTF-8 and Windows-1252.
                        _asciiTentative = false;
                        Encoding = Encoding with { Kind = LoadFileEncodingKind.Windows1252, AsciiOnly = false };
                        SetUpTokens();
                        return Windows1252Encoding.Instance.GetChars(raw, chars);
                    }

                    if (_fallback)
                    {
                        _rowIssues.Add(new DatIssue(DatIssueSeverity.Warning, DatIssueKind.MixedEncodingRow, row, line, offset,
                            $"Row is not valid UTF-8 (first invalid byte at file offset {offset + read}); decoded as Windows-1252."));
                        return Windows1252Encoding.Instance.GetChars(raw, chars);
                    }

                    _rowIssues.Add(new DatIssue(DatIssueSeverity.Warning, DatIssueKind.InvalidEncoding, row, line, offset,
                        $"Row is not valid UTF-8 (first invalid byte at file offset {offset + read}); invalid bytes replaced with U+FFFD."));
                    Utf8Unicode.ToUtf16(raw, chars, out _, out written, replaceInvalidSequences: true);
                    return written;
                }

            case LoadFileEncodingKind.Windows1252:
                {
                    if (raw.IndexOfAnyExceptInRange((byte)0, (byte)0x7F) >= 0 && Utf8Unicode.IsValid(raw))
                    {
                        if (_fallback)
                        {
                            _rowIssues.Add(new DatIssue(DatIssueSeverity.Warning, DatIssueKind.MixedEncodingRow, row, line, offset,
                                "Row is UTF-8 in a Windows-1252 file; decoded as UTF-8."));
                            Utf8Unicode.ToUtf16(raw, chars, out _, out int utf8Written);
                            return utf8Written;
                        }

                        _rowIssues.Add(new DatIssue(DatIssueSeverity.Warning, DatIssueKind.SuspectedMisdecode, row, line, offset,
                            "Row looks like UTF-8 decoded as Windows-1252 (e.g. 'Ã¾' for 'þ'); check the encoding."));
                    }

                    return Windows1252Encoding.Instance.GetChars(raw, chars);
                }

            default:
                {
                    UnicodeEncoding strict = Encoding.Kind == LoadFileEncodingKind.Utf16LE ? StrictUtf16LE : StrictUtf16BE;
                    if ((raw.Length & 1) == 0)
                    {
                        try
                        {
                            return strict.GetChars(raw, chars);
                        }
                        catch (DecoderFallbackException)
                        {
                            // Reported below.
                        }
                    }

                    _rowIssues.Add(new DatIssue(DatIssueSeverity.Warning, DatIssueKind.InvalidEncoding, row, line, offset,
                        "Row is not valid UTF-16; invalid code units replaced with U+FFFD."));
                    return LoadFileEncodings.Get(Encoding.Kind).GetChars(raw, chars);
                }
        }
    }

    private static int CountLineFeeds(ReadOnlySpan<char> chars) => chars.Count('\n');

    // ---- field splitting -------------------------------------------------------------------------------------

    private readonly record struct SplitResult(int StrayColumn, int StrayCount, int UnterminatedColumn, bool TooManyFields);

    private SplitResult Split(ReadOnlySpan<char> c)
    {
        _fields.Clear();
        char col = _o.Profile.Column;
        char? quote = _o.Profile.Quote;
        int strayColumn = -1;
        int strayCount = 0;
        int unterminated = -1;
        int i = 0;
        int n = c.Length;
        while (true)
        {
            if (_fields.Count >= _o.MaxFieldCount)
            {
                return new SplitResult(strayColumn, strayCount, unterminated, true);
            }

            string value;
            if (quote is { } q && i < n && c[i] == q)
            {
                i++;
                int rel = c[i..].IndexOf(q);
                if (rel >= 0 && (i + rel + 1 == n || c[i + rel + 1] == col))
                {
                    value = Finish(c.Slice(i, rel));
                    i += rel + 1;
                }
                else
                {
                    _value.Clear();
                    bool closed = false;
                    while (i < n)
                    {
                        rel = c[i..].IndexOf(q);
                        if (rel < 0)
                        {
                            _value.Append(c[i..]);
                            i = n;
                            break;
                        }

                        _value.Append(c.Slice(i, rel));
                        i += rel;
                        if (i + 1 < n && c[i + 1] == q)
                        {
                            _value.Append(q);
                            i += 2;
                        }
                        else if (i + 1 == n || c[i + 1] == col)
                        {
                            i++;
                            closed = true;
                            break;
                        }
                        else
                        {
                            if (strayCount++ == 0)
                            {
                                strayColumn = _fields.Count;
                            }

                            _value.Append(q);
                            i++;
                        }
                    }

                    if (!closed && unterminated < 0)
                    {
                        unterminated = _fields.Count;
                    }

                    value = Finish(_value);
                }
            }
            else
            {
                int rel = c[i..].IndexOf(col);
                int stop = rel < 0 ? n : i + rel;
                ReadOnlySpan<char> span = c[i..stop];
                if (quote is { } sq && span.Contains(sq))
                {
                    if (strayCount == 0)
                    {
                        strayColumn = _fields.Count;
                    }

                    strayCount += span.Count(sq);
                }

                value = Finish(span);
                i = stop;
            }

            _fields.Add(value);
            if (i < n && c[i] == col)
            {
                i++;
                continue;
            }

            return new SplitResult(strayColumn, strayCount, unterminated, false);
        }
    }

    private string Finish(ReadOnlySpan<char> span)
    {
        string value = span.Length == 0 ? "" : new string(span);
        return _o.ConvertNewlineCharacter && _o.Profile.Newline is { } nl ? value.Replace(nl, '\n') : value;
    }

    private string Finish(StringBuilder sb)
    {
        string value = sb.ToString();
        return _o.ConvertNewlineCharacter && _o.Profile.Newline is { } nl ? value.Replace(nl, '\n') : value;
    }
}
