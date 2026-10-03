using System.Globalization;
using System.Text;

namespace Opportunity.Import.LoadFiles;

/// <summary>
/// Re-loadable error file (eDiscovery review, Q-47): the original header plus a trailing <c>ImportError</c> column,
/// then every rejected row byte-for-byte as it was in the source, followed by its error text — same delimiter
/// profile, encoding and byte-order mark as the input, so it can be fixed in the user's usual tool and re-loaded.
/// Rows over the record-size limit were never buffered and cannot be reproduced; they are counted in
/// <see cref="UnreproducibleRows"/> and appear in the issue report only.
/// </summary>
public sealed class DatErrorFileWriter(Stream output, bool leaveOpen = false) : IDatIssueSink, IDisposable
{
    public const string ErrorColumn = "ImportError";

    private Encoding? _encoding;
    private DelimiterProfile? _profile;
    private byte[] _column = [];
    private byte[] _lineEnd = [];
    private bool _disposed;

    public long RowsWritten { get; private set; }

    public long UnreproducibleRows { get; private set; }

    public void Start(DatFileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _profile = context.Profile;
        _encoding = LoadFileEncodings.Get(context.Encoding.Kind);
        _column = _encoding.GetBytes(context.Profile.Column.ToString());
        _lineEnd = _encoding.GetBytes("\r\n");
        if (context.Encoding.PreambleLength > 0)
        {
            output.Write(LoadFileEncodings.Preamble(context.Encoding.Kind));
        }

        if (!context.HeaderRecord.IsEmpty)
        {
            WriteRow(context.HeaderRecord.Span, ErrorColumn);
        }
    }

    public void RejectedRecord(ReadOnlySpan<byte> rawRecord, IReadOnlyList<DatIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (_encoding == null)
        {
            return;
        }

        if (rawRecord.IsEmpty)
        {
            UnreproducibleRows++;
            return;
        }

        WriteRow(rawRecord, string.Join("; ", issues.Select(i => i.ToErrorText())));
        RowsWritten++;
    }

    public void Complete(DatReadStatistics statistics) => output.Flush();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        output.Flush();
        if (!leaveOpen)
        {
            output.Dispose();
        }
    }

    private void WriteRow(ReadOnlySpan<byte> raw, string error)
    {
        output.Write(raw);
        output.Write(_column);
        output.Write(_encoding!.GetBytes(Qualify(error)));
        output.Write(_lineEnd);
    }

    private string Qualify(string value)
    {
        DelimiterProfile p = _profile!;
        if (p.Quote is not { } q)
        {
            return value.Replace(p.Column, ' ');
        }

        return q + value.Replace(q.ToString(), new string(q, 2), StringComparison.Ordinal) + q;
    }
}

/// <summary>Admin-facing issue report as RFC 4180 CSV (UTF-8 with BOM, opens cleanly in Excel): one line per issue.</summary>
public sealed class DatIssueReportWriter(TextWriter output) : IDatIssueSink
{
    public static readonly IReadOnlyList<string> Columns =
        ["Severity", "Kind", "Row", "Line", "ByteOffset", "ControlNumber", "Column", "ExpectedFields", "ObservedFields", "Message"];

    private bool _headerWritten;

    /// <summary>Creates a writer over a UTF-8 (with BOM) stream.</summary>
    public static DatIssueReportWriter Create(Stream stream, bool leaveOpen = false) =>
        new(new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen));

    public void Start(DatFileContext context) => EnsureHeader();

    public void Issue(DatIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        EnsureHeader();
        WriteLine([
            issue.Severity.ToString(), issue.Kind.ToString(), Num(issue.RowNumber), Num(issue.LineNumber), Num(issue.ByteOffset),
            issue.ControlNumber ?? "", issue.Column ?? "", issue.ExpectedFieldCount?.ToString(CultureInfo.InvariantCulture) ?? "",
            issue.ObservedFieldCount?.ToString(CultureInfo.InvariantCulture) ?? "", issue.Message,
        ]);
    }

    public void Complete(DatReadStatistics statistics)
    {
        EnsureHeader();
        output.Flush();
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private void EnsureHeader()
    {
        if (!_headerWritten)
        {
            _headerWritten = true;
            WriteLine(Columns);
        }
    }

    private void WriteLine(IReadOnlyList<string> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                output.Write(',');
            }

            string v = values[i];
            if (v.AsSpan().IndexOfAny(",\"\r\n") >= 0)
            {
                output.Write('"');
                output.Write(v.Replace("\"", "\"\"", StringComparison.Ordinal));
                output.Write('"');
            }
            else
            {
                output.Write(v);
            }
        }

        output.Write("\r\n");
    }
}
