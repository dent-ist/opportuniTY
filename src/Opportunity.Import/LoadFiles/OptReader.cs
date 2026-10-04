using System.Globalization;
using System.Text;

namespace Opportunity.Import.LoadFiles;

/// <summary>
/// One row of an Opticon OPT image cross-reference: <c>ImageKey,Volume,Path,DocBreak,FolderBreak,BoxBreak,PageCount</c>,
/// one row per page image, <c>Y</c> in column 4 and the page count on the first page of a document only.
/// </summary>
/// <param name="RowNumber">1-based row (blank lines are not rows).</param>
/// <param name="LineNumber">1-based physical line.</param>
/// <param name="PageCount">Column 7 when it holds a number.</param>
/// <param name="Problem">Why the row cannot be used (too few columns, no key or path); its page counts as missing.</param>
public sealed record OptRecord(
    long RowNumber,
    long LineNumber,
    string ImageKey,
    string Volume,
    string Path,
    bool DocumentBreak,
    int? PageCount,
    string? Problem);

/// <summary>
/// Streaming OPT reader. OPT files are ASCII in practice; a UTF-8 or UTF-16 byte-order mark is honoured, otherwise each
/// line is read as UTF-8 when valid and as Windows-1252 when not. A path may itself contain commas: the last four
/// columns are the break and count columns and everything between the volume and them is the path. Lines longer than
/// <see cref="MaxLineBytes"/> are reported as a problem row, never buffered whole.
/// </summary>
public sealed class OptReader : IAsyncDisposable
{
    public const int MaxLineBytes = 32 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly MemoryStream _line = new();
    private int _position;
    private int _length;
    private bool _eof;
    private bool _started;
    private Encoding? _forced;
    private MemoryStream? _replacement;
    private long _lines;
    private long _rows;

    private OptReader(Stream stream, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    public static OptReader Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new OptReader(stream, leaveOpen);
    }

    /// <summary>The next row, or null at the end of the file.</summary>
    public async ValueTask<OptRecord?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            _started = true;
            await DetectPreambleAsync(cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            var (bytes, tooLong) = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return null;
            }

            _lines++;
            if (tooLong)
            {
                return new OptRecord(++_rows, _lines, string.Empty, string.Empty, string.Empty, false, null,
                    $"The row is longer than {MaxLineBytes} bytes.");
            }

            var text = Decode(bytes);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            return Parse(++_rows, _lines, text);
        }
    }

    internal static OptRecord Parse(long row, long line, string text)
    {
        var fields = text.Split(',');
        if (fields.Length < 3)
        {
            return new OptRecord(row, line, fields[0].Trim(), string.Empty, string.Empty, false, null,
                $"The row has {fields.Length} column(s); an OPT row has ImageKey, Volume, Path, DocBreak, FolderBreak, BoxBreak and PageCount.");
        }

        // Path may contain commas: when there are more than 7 columns, the surplus belongs to the path.
        var tail = Math.Min(4, fields.Length - 3);
        var pathEnd = fields.Length - tail;
        var path = string.Join(',', fields[2..pathEnd]).Trim();
        var key = fields[0].Trim();
        var docBreak = tail >= 1 && string.Equals(fields[pathEnd].Trim(), "Y", StringComparison.OrdinalIgnoreCase);
        int? pageCount = null;
        string? problem = null;
        if (tail >= 4 && fields[^1].Trim() is { Length: > 0 } count)
        {
            if (int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                pageCount = n;
            }
            else
            {
                problem = $"The page count '{Shorten(count)}' is not a number.";
            }
        }

        if (key.Length == 0)
        {
            problem = "The row has no image key.";
        }
        else if (path.Length == 0)
        {
            problem = "The row has no image path.";
        }

        return new OptRecord(row, line, key, fields[1].Trim(), path, docBreak, pageCount, problem);
    }

    private static string Shorten(string value) => value.Length <= 40 ? value : value[..40] + "…";

    private string Decode(byte[] bytes)
    {
        if (_forced is not null)
        {
            return _forced.GetString(bytes);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Windows1252Encoding.Instance.GetString(bytes);
        }
    }

    private async ValueTask DetectPreambleAsync(CancellationToken cancellationToken)
    {
        await FillAsync(cancellationToken).ConfigureAwait(false);
        var head = _buffer.AsSpan(_position, _length - _position);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            _position += 3;
            _forced = Encoding.UTF8;
        }
        else if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            // Rare for OPT: decode the rest as UTF-16 by transcoding into this reader's 8-bit line scanner.
            var bigEndian = head[0] == 0xFE;
            _position += 2;
            var rest = new MemoryStream();
            rest.Write(_buffer, _position, _length - _position);
            await _stream.CopyToAsync(rest, cancellationToken).ConfigureAwait(false);
            var text = (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(rest.GetBuffer(), 0, (int)rest.Length);
            var utf8 = Encoding.UTF8.GetBytes(text);
            _forced = Encoding.UTF8;
            _replacement = new MemoryStream(utf8, writable: false);
            _position = _length = 0;
        }
    }

    private async ValueTask<(byte[]? Line, bool TooLong)> ReadLineAsync(CancellationToken cancellationToken)
    {
        _line.SetLength(0);
        var tooLong = false;
        var any = false;
        while (true)
        {
            if (_position >= _length)
            {
                await FillAsync(cancellationToken).ConfigureAwait(false);
                if (_position >= _length)
                {
                    return any ? (Trim(_line), tooLong) : (null, false);
                }
            }

            any = true;
            var span = _buffer.AsSpan(_position, _length - _position);
            var newline = span.IndexOf((byte)'\n');
            var take = newline < 0 ? span.Length : newline;
            if (!tooLong)
            {
                if (_line.Length + take > MaxLineBytes)
                {
                    tooLong = true;
                    _line.SetLength(0);
                }
                else
                {
                    _line.Write(span[..take]);
                }
            }

            _position += newline < 0 ? take : take + 1;
            if (newline >= 0)
            {
                return (Trim(_line), tooLong);
            }
        }
    }

    private static byte[] Trim(MemoryStream line)
    {
        var length = (int)line.Length;
        var buffer = line.GetBuffer();
        if (length > 0 && buffer[length - 1] == '\r')
        {
            length--;
        }

        return buffer.AsSpan(0, length).ToArray();
    }

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        if (_eof)
        {
            return;
        }

        _position = 0;
        var source = (Stream?)_replacement ?? _stream;
        _length = await source.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
        if (_length == 0)
        {
            _eof = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_replacement is not null)
        {
            await _replacement.DisposeAsync().ConfigureAwait(false);
        }

        await _line.DisposeAsync().ConfigureAwait(false);
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
