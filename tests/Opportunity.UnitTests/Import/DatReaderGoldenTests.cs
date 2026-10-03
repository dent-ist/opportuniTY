using System.Text;

using AwesomeAssertions;

using Opportunity.Import.LoadFiles;

using static Opportunity.UnitTests.Import.DatTestSupport;

namespace Opportunity.UnitTests.Import;

public class DatReaderGoldenTests
{
    public static TheoryData<string, string, LoadFileEncodingKind, bool> GoldenMatrix()
    {
        var data = new TheoryData<string, string, LoadFileEncodingKind, bool>();
        foreach (DelimiterProfile p in DelimiterProfile.Presets)
        {
            foreach (string eol in new[] { "CRLF", "LF" })
            {
                data.Add(p.Name, eol, LoadFileEncodingKind.Utf8, false);
                data.Add(p.Name, eol, LoadFileEncodingKind.Utf8, true);
                data.Add(p.Name, eol, LoadFileEncodingKind.Utf16LE, true);
                data.Add(p.Name, eol, LoadFileEncodingKind.Windows1252, false);
            }
        }

        return data;
    }

    /// <summary>Golden case: empty fields, qualifier at value start/end, embedded separators, newline-in-value, non-ASCII.</summary>
    private static (string Text, List<List<string>> Expected) Golden(DelimiterProfile p, string eol)
    {
        char q = p.Quote!.Value;
        string newlineInValue = p.Newline is { } nl ? "first" + nl + "second" : "first\r\nsecond";
        string[][] rows =
        [
            ["ControlNumber", "Subject", "Custodian"],
            ["ABC0001", "", ""],
            ["ABC0002", q + "starts with qualifier", "ends with qualifier" + q],
            ["ABC0003", "has" + p.Column + "column" + p.Column, "a,b;c\\d"],
            ["ABC0004", newlineInValue, "Müller, Zoë – “quoted” ¶ þ"],
            ["ABC0005", " padded ", q.ToString()],
        ];
        string text = Dat(p, rows);
        if (eol == "LF")
        {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        string expectedNewline = p.Newline != null ? "first\nsecond" : (eol == "LF" ? "first\nsecond" : "first\r\nsecond");
        List<List<string>> expected =
        [
            ["ABC0001", "", ""],
            ["ABC0002", q + "starts with qualifier", "ends with qualifier" + q],
            ["ABC0003", "has" + p.Column + "column" + p.Column, "a,b;c\\d"],
            ["ABC0004", expectedNewline, "Müller, Zoë – “quoted” ¶ þ"],
            ["ABC0005", " padded ", q.ToString()],
        ];
        return (text, expected);
    }

    [Theory]
    [MemberData(nameof(GoldenMatrix))]
    public async Task Golden_case_parses_per_preset_line_ending_and_encoding(string preset, string eol, LoadFileEncodingKind kind, bool bom)
    {
        DelimiterProfile.TryGetPreset(preset, out DelimiterProfile p).Should().BeTrue();
        (string text, List<List<string>> expected) = Golden(p, eol);
        byte[] bytes = Encode(text, kind, bom);
        foreach (int buffer in new[] { 64, 4096 })
        {
            var options = new DatReaderOptions { Profile = p, BufferBytes = buffer };
            await using DatReader reader = await DatReader.OpenAsync(new TricklingStream(bytes, 7), options, cancellationToken: TestContext.Current.CancellationToken);
            reader.Header.Names.Should().Equal("ControlNumber", "Subject", "Custodian");
            var values = new List<List<string>>();
            await foreach (DatRecord r in reader.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                values.Add([.. r.Values]);
            }

            reader.Encoding.Kind.Should().Be(kind);
            reader.Issues.Should().BeEmpty($"{preset} {eol} {kind} buffer {buffer}");
            values.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering(), $"{preset} {eol} {kind} buffer {buffer}");
            reader.Statistics.AcceptedRows.Should().Be(5);
        }
    }

    [Fact]
    public async Task Identical_content_in_every_encoding_parses_to_identical_values()
    {
        (string text, _) = Golden(DelimiterProfile.Concordance, "CRLF");
        var results = new List<List<List<string>>>();
        foreach ((LoadFileEncodingKind kind, bool bom) in new[]
                 {
                     (LoadFileEncodingKind.Utf8, false), (LoadFileEncodingKind.Utf8, true), (LoadFileEncodingKind.Utf16LE, true),
                     (LoadFileEncodingKind.Windows1252, false), (LoadFileEncodingKind.Utf16BE, true),
                 })
        {
            results.Add((await ParseAsync(text, kind: kind, bom: bom)).Values);
        }

        foreach (List<List<string>> r in results.Skip(1))
        {
            r.Should().BeEquivalentTo(results[0], o => o.WithStrictOrdering());
        }

        // Byte-identical once re-encoded as UTF-8.
        results.Select(r => Convert.ToHexString(Encoding.UTF8.GetBytes(string.Join('|', r.SelectMany(x => x))))).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task Newline_character_converts_to_line_feed_unless_preserved()
    {
        string text = Dat(DelimiterProfile.Concordance, ["A", "B"], ["1", "x®y"]);
        (await ParseAsync(text)).Values[0][1].Should().Be("x\ny");
        (await ParseAsync(text, new DatReaderOptions { ConvertNewlineCharacter = false })).Values[0][1].Should().Be("x®y");
    }

    [Fact]
    public async Task Field_count_mismatch_is_rejected_with_row_line_offset_key_and_counts()
    {
        DelimiterProfile p = DelimiterProfile.Concordance;
        string header = Dat(p, ["ControlNumber", "B", "C"]);
        string row1 = Dat(p, ["ABC1", "b", "c"]);
        string text = header + row1 + Dat(p, ["ABC2", "b", "c", "extra"]) + Dat(p, ["ABC3", "b"]) + Dat(p, ["ABC4", "b", "c"]);
        DatParse parse = await ParseAsync(text, kind: LoadFileEncodingKind.Utf8, bom: true);

        parse.Records.Select(r => r.ControlNumber).Should().Equal("ABC1", "ABC4");
        parse.Records[1].RowNumber.Should().Be(4);
        parse.Issues.Should().HaveCount(2);
        DatIssue first = parse.Issues[0];
        first.Kind.Should().Be(DatIssueKind.FieldCountMismatch);
        first.Severity.Should().Be(DatIssueSeverity.Error);
        first.RowNumber.Should().Be(2);
        first.LineNumber.Should().Be(3);
        first.ByteOffset.Should().Be(3 + Encoding.UTF8.GetByteCount(header + row1));
        first.ControlNumber.Should().Be("ABC2");
        (first.ExpectedFieldCount, first.ObservedFieldCount).Should().Be((3, 4));
        parse.Issues[1].ObservedFieldCount.Should().Be(2);
        parse.Reader.Statistics.RejectedRows.Should().Be(2);
        parse.Reader.Statistics.Count(DatIssueKind.FieldCountMismatch).Should().Be(2);
    }

    [Fact]
    public async Task Duplicate_headers_are_a_preflight_error_unless_allowed()
    {
        string text = Dat(DelimiterProfile.Concordance, ["ControlNumber", "Custodian", " custodian "], ["1", "a", "b"]);
        await using DatReader reader = await DatReader.OpenAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), cancellationToken: TestContext.Current.CancellationToken);
        reader.HasPreflightErrors.Should().BeTrue();
        DatIssue issue = reader.PreflightIssues.Should().ContainSingle().Subject;
        (issue.Kind, issue.Column).Should().Be((DatIssueKind.DuplicateHeader, "Custodian"));
        Func<Task> read = async () => await reader.ReadAsync(TestContext.Current.CancellationToken);
        await read.Should().ThrowAsync<DatPreflightException>();

        DatParse allowed = await ParseAsync(text, new DatReaderOptions { AllowDuplicateHeaders = true });
        allowed.Records.Should().ContainSingle();
        allowed.Issues.Should().ContainSingle(i => i.Kind == DatIssueKind.DuplicateHeader && i.Severity == DatIssueSeverity.Warning);
    }

    [Fact]
    public async Task Empty_file_is_a_preflight_error()
    {
        await using DatReader reader = await DatReader.OpenAsync(new MemoryStream([0xEF, 0xBB, 0xBF]), cancellationToken: TestContext.Current.CancellationToken);
        reader.PreflightIssues.Should().ContainSingle(i => i.Kind == DatIssueKind.MissingHeader);
    }

    [Fact]
    public async Task Over_long_record_is_skipped_safely_and_reading_continues()
    {
        DelimiterProfile p = DelimiterProfile.Concordance;
        string giant = new('x', 50_000);
        string text = Dat(p, ["A", "B"], ["1", "ok"], ["2", giant], ["3", "ok"]);
        foreach (LoadFileEncodingKind kind in new[] { LoadFileEncodingKind.Utf8, LoadFileEncodingKind.Utf16LE })
        {
            DatParse parse = await ParseAsync(Encode(text, kind, true), new DatReaderOptions { MaxRecordBytes = 1_000, BufferBytes = 256 });
            parse.Values.Select(v => v[0]).Should().Equal("1", "3");
            DatIssue issue = parse.Issues.Should().ContainSingle().Subject;
            (issue.Kind, issue.RowNumber, issue.LineNumber).Should().Be((DatIssueKind.RecordTooLong, 2L, 3L));
            parse.Records[1].RowNumber.Should().Be(3);
        }
    }

    [Fact]
    public async Task Too_many_fields_are_rejected()
    {
        string text = Dat(DelimiterProfile.Concordance, ["A", "B"], ["1", "2"], [.. Enumerable.Range(0, 50).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))], ["3", "4"]);
        DatParse parse = await ParseAsync(text, new DatReaderOptions { MaxFieldCount = 10 });
        parse.Values.Select(v => v[0]).Should().Equal("1", "3");
        parse.Issues.Should().ContainSingle(i => i.Kind == DatIssueKind.TooManyFields && i.RowNumber == 2);
    }

    [Fact]
    public async Task Unescaped_qualifier_is_reported_with_column_and_does_not_shift_later_rows()
    {
        DelimiterProfile p = DelimiterProfile.Concordance;
        string text = Dat(p, ["ControlNumber", "Subject", "Other"]) + "þABC1þ\u0014þRe: þhello þworldþ\u0014þxþ\r\n" + Dat(p, ["ABC2", "fine", "y"]);
        DatParse parse = await ParseAsync(text);
        parse.Values.Should().ContainSingle().Which.Should().Equal("ABC2", "fine", "y");
        DatIssue issue = parse.Issues.Should().ContainSingle().Subject;
        (issue.Kind, issue.Column, issue.RowNumber, issue.ControlNumber).Should().Be((DatIssueKind.UnescapedQualifier, "Subject", 1L, "ABC1"));

        DatParse lenient = await ParseAsync(text, new DatReaderOptions { RejectStrayQualifiers = false });
        lenient.Values[0].Should().Equal("ABC1", "Re: þhello þworld", "x");
        lenient.Records[0].Issues.Should().ContainSingle(i => i.Severity == DatIssueSeverity.Warning);
    }

    [Fact]
    public async Task Unterminated_qualifier_in_a_dat_does_not_swallow_following_rows()
    {
        string text = Dat(DelimiterProfile.Concordance, ["A", "B"]) + "þ1þ\u0014þnever closed\r\n" + Dat(DelimiterProfile.Concordance, ["2", "ok"]);
        DatParse parse = await ParseAsync(text);
        parse.Values.Should().ContainSingle().Which.Should().Equal("2", "ok");
        parse.Issues.Should().ContainSingle(i => i.Kind == DatIssueKind.UnterminatedQualifier && i.Column == "B");
    }

    [Fact]
    public async Task Csv_multi_line_values_keep_line_breaks_and_line_numbers_stay_physical()
    {
        string text = "Id,Body\r\n1,\"line one\r\nline two\nline three\"\r\n2,\"x\"\"y\"\r\n3,plain\r\n";
        DatParse parse = await ParseAsync(text, new DatReaderOptions { Profile = DelimiterProfile.Csv });
        parse.Values.Should().BeEquivalentTo(new List<List<string>> { new() { "1", "line one\r\nline two\nline three" }, new() { "2", "x\"y" }, new() { "3", "plain" } }, o => o.WithStrictOrdering());
        parse.Records.Select(r => r.LineNumber).Should().Equal(2, 5, 6);
    }

    [Fact]
    public async Task Mixed_encoding_rows_are_redecoded_and_reported()
    {
        DelimiterProfile p = DelimiterProfile.Concordance;
        byte[] utf8File = [.. Encoding.UTF8.GetBytes(Dat(p, ["A", "B"], ["1", "Zoë"])), .. Windows1252Encoding.Instance.GetBytes(Dat(p, ["2", "Zoë"])), .. Encoding.UTF8.GetBytes(Dat(p, ["3", "é"]))];
        DatParse utf8 = await ParseAsync(utf8File);
        utf8.Values.Should().BeEquivalentTo(new List<List<string>> { new() { "1", "Zoë" }, new() { "2", "Zoë" }, new() { "3", "é" } }, o => o.WithStrictOrdering());
        utf8.Issues.Should().ContainSingle(i => i.Kind == DatIssueKind.MixedEncodingRow && i.RowNumber == 2 && i.Severity == DatIssueSeverity.Warning);

        byte[] ansiFile = [.. Windows1252Encoding.Instance.GetBytes(Dat(p, ["A", "B"], ["1", "Zoë"], ["2", "x"])), .. Encoding.UTF8.GetBytes(Dat(p, ["3", "Zoë"]))];
        DatParse ansi = await ParseAsync(ansiFile);
        ansi.Reader.Encoding.Kind.Should().Be(LoadFileEncodingKind.Windows1252);
        ansi.Values[2].Should().Equal("3", "Zoë");
        ansi.Issues.Should().ContainSingle(i => i.Kind == DatIssueKind.MixedEncodingRow && i.RowNumber == 3);
    }

    [Fact]
    public async Task Misdetected_thorn_is_visibly_obvious_in_preview()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(Dat(DelimiterProfile.Concordance, ["ControlNumber", "Custodian"], ["ABC1", "Smith"]));
        DatPreviewResult wrong = await DatPreview.ReadAsync(new MemoryStream(utf8), new DatReaderOptions { EncodingOverride = LoadFileEncodingKind.Windows1252 }, cancellationToken: TestContext.Current.CancellationToken);
        wrong.Header[0].Should().Be("Ã¾ControlNumberÃ¾");
        wrong.MisdecodeSuspected.Should().BeTrue();
        wrong.Issues.Should().Contain(i => i.Kind == DatIssueKind.SuspectedMisdecode);

        DatPreviewResult right = await DatPreview.ReadAsync(new MemoryStream(utf8), cancellationToken: TestContext.Current.CancellationToken);
        right.Header.Should().Equal("ControlNumber", "Custodian");
        right.Rows.Should().ContainSingle().Which.Values.Should().Equal("ABC1", "Smith");
        right.MisdecodeSuspected.Should().BeFalse();
        (right.Encoding.Kind, right.Encoding.Source).Should().Be((LoadFileEncodingKind.Utf8, EncodingSource.Heuristic));
    }

    [Fact]
    public async Task Preview_returns_the_first_twenty_rows_including_rejected_ones()
    {
        DelimiterProfile p = DelimiterProfile.Concordance;
        var sb = new StringBuilder(Dat(p, ["A", "B"]));
        for (int i = 1; i <= 50; i++)
        {
            sb.Append(i == 3 ? Dat(p, ["3"]) : Dat(p, [i.ToString(System.Globalization.CultureInfo.InvariantCulture), "x"]));
        }

        DatPreviewResult preview = await DatPreview.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())), cancellationToken: TestContext.Current.CancellationToken);
        preview.Rows.Should().HaveCount(20);
        preview.Rows[2].IsRejected.Should().BeTrue();
        preview.Rows[2].Issues.Should().ContainSingle(i => i.Kind == DatIssueKind.FieldCountMismatch);
    }

    [Fact]
    public async Task Error_threshold_stops_the_read()
    {
        DelimiterProfile p = DelimiterProfile.Concordance;
        string text = Dat(p, ["A", "B"]) + string.Concat(Enumerable.Range(0, 10).Select(i => Dat(p, ["only one field"])));
        Func<Task> read = () => ParseAsync(text, new DatReaderOptions { MaxRejectedRows = 3 });
        (await read.Should().ThrowAsync<DatErrorThresholdExceededException>()).Which.Statistics.RejectedRows.Should().Be(4);

        Func<Task> rate = () => ParseAsync(text, new DatReaderOptions { MaxRejectedRows = null, MaxRejectedRate = 0.5, MinRowsForRejectedRate = 5 });
        (await rate.Should().ThrowAsync<DatErrorThresholdExceededException>()).Which.Statistics.Rows.Should().Be(5);
    }

    [Fact]
    public async Task Reading_honours_cancellation()
    {
        DelimiterProfile p = DelimiterProfile.Concordance;
        string text = Dat(p, ["A"]) + string.Concat(Enumerable.Range(0, 1000).Select(i => Dat(p, ["v"])));
        using var cts = new CancellationTokenSource();
        await using DatReader reader = await DatReader.OpenAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), cancellationToken: TestContext.Current.CancellationToken);
        (await reader.ReadAsync(cts.Token)).Should().NotBeNull();
        await cts.CancelAsync();
        Func<Task> read = async () => await reader.ReadAsync(cts.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Headerless_file_gets_positional_names_and_blank_lines_are_skipped()
    {
        string text = "þ1þ\u0014þaþ\r\n\r\nþ2þ\u0014þbþ";
        DatParse parse = await ParseAsync(text, new DatReaderOptions { HasHeader = false });
        parse.Reader.Header.Names.Should().Equal("Column1", "Column2");
        parse.Values.Should().BeEquivalentTo(new List<List<string>> { new() { "1", "a" }, new() { "2", "b" } }, o => o.WithStrictOrdering());
        parse.Reader.Statistics.BlankLines.Should().Be(1);
        parse.Records[1].LineNumber.Should().Be(3);
    }

    [Fact]
    public async Task Unqualified_and_custom_profiles_parse()
    {
        var pipe = new DelimiterProfile("pipe", '|', null, null);
        DatParse parse = await ParseAsync("A|B\n1|x y\n", new DatReaderOptions { Profile = pipe });
        parse.Values.Should().ContainSingle().Which.Should().Equal("1", "x y");

        var tab = new DelimiterProfile("tab", '\t', '"', '~', '|', '/');
        (await ParseAsync("A\tB\r\n\"1\"\t\"a~b\"\r\n", new DatReaderOptions { Profile = tab })).Values[0].Should().Equal("1", "a\nb");
    }

    [Fact]
    public async Task Profile_characters_must_be_representable_in_the_encoding()
    {
        var odd = new DelimiterProfile("odd", '☃', 'þ', '®');
        byte[] bytes = Windows1252Encoding.Instance.GetBytes("þAþ\r\n");
        await using DatReader reader = await DatReader.OpenAsync(new MemoryStream(bytes), new DatReaderOptions { Profile = odd, EncodingOverride = LoadFileEncodingKind.Windows1252 }, cancellationToken: TestContext.Current.CancellationToken);
        reader.PreflightIssues.Should().ContainSingle(i => i.Kind == DatIssueKind.InvalidProfile && i.Severity == DatIssueSeverity.Error);
    }

    [Fact]
    public async Task Error_file_reproduces_rejected_rows_in_the_source_profile_and_encoding_and_reloads()
    {
        DelimiterProfile p = DelimiterProfile.ConcordancePilcrow;
        string text = Dat(p, ["ControlNumber", "Subject"], ["ABC1", "ok"], ["ABC2", "too", "many"]) + "þABC3þ¶þbad þquoteþ\r\n" + Dat(p, ["ABC4", "Zoë"]);
        byte[] source = Encode(text, LoadFileEncodingKind.Utf16LE, bom: true);
        using var errorDat = new MemoryStream();
        using var report = new MemoryStream();
        var errorWriter = new DatErrorFileWriter(errorDat, leaveOpen: true);
        var reportWriter = DatIssueReportWriter.Create(report, leaveOpen: true);
        DatParse parse = await ParseAsync(source, new DatReaderOptions { Profile = p, Sinks = [errorWriter, reportWriter] });
        errorWriter.Dispose();
        parse.Values.Select(v => v[0]).Should().Equal("ABC1", "ABC4");
        errorWriter.RowsWritten.Should().Be(2);

        byte[] errors = errorDat.ToArray();
        errors.AsSpan(0, 2).ToArray().Should().Equal(0xFF, 0xFE);
        string errorText = Encoding.Unicode.GetString(errors, 2, errors.Length - 2);
        errorText.Should().StartWith("þControlNumberþ¶þSubjectþ¶þImportErrorþ\r\nþABC2þ¶þtooþ¶þmanyþ¶þFieldCountMismatch: Expected 2 fields (header), found 3. (row 2, line 3, byte ");

        DatParse reloaded = await ParseAsync(errors, new DatReaderOptions { Profile = p, RejectStrayQualifiers = false, MaxRejectedRows = null });
        reloaded.Reader.Header.Names.Should().Equal("ControlNumber", "Subject", DatErrorFileWriter.ErrorColumn);
        reloaded.Reader.Statistics.Rows.Should().Be(2);
        reloaded.Records.Should().ContainSingle().Which.Values.Take(2).Should().Equal("ABC3", "bad þquote");

        string csv = Encoding.UTF8.GetString(report.ToArray());
        csv.Should().StartWith("﻿Severity,Kind,Row,Line,ByteOffset,ControlNumber,Column,ExpectedFields,ObservedFields,Message\r\n");
        csv.Should().Contain("Error,FieldCountMismatch,2,3,");
        csv.Should().Contain(",ABC3,Subject,,,");
    }

    [Fact]
    public async Task Extracted_text_is_decoded_with_its_own_detection_and_flags_invalid_bytes()
    {
        static async Task<(string Text, ExtractedTextResult Result)> Decode(byte[] bytes, LoadFileEncodingKind? forced = null)
        {
            using var writer = new StringWriter();
            ExtractedTextResult result = await ExtractedTextDecoder.DecodeAsync(new MemoryStream(bytes), writer, forced, TestContext.Current.CancellationToken);
            return (writer.ToString(), result);
        }

        string body = string.Concat(Enumerable.Repeat("Zoë Müller – ", 20_000));
        (string utf16, ExtractedTextResult r1) = await Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(body)]);
        (utf16, r1.Encoding.Kind, r1.TextEncodingWarning).Should().Be((body, LoadFileEncodingKind.Utf16LE, false));

        (string ansi, ExtractedTextResult r2) = await Decode(Windows1252Encoding.Instance.GetBytes(body));
        (ansi, r2.Encoding.Kind).Should().Be((body, LoadFileEncodingKind.Windows1252));

        (string forced, ExtractedTextResult r3) = await Decode(Windows1252Encoding.Instance.GetBytes("abc ë def"), LoadFileEncodingKind.Utf8);
        forced.Should().Be("abc � def");
        r3.TextEncodingWarning.Should().BeTrue();
        r3.InvalidSequences.Should().Be(1);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x41 }, LoadFileEncodingKind.Utf8, EncodingSource.ByteOrderMark, 3)]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, LoadFileEncodingKind.Utf16LE, EncodingSource.ByteOrderMark, 2)]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41 }, LoadFileEncodingKind.Utf16BE, EncodingSource.ByteOrderMark, 2)]
    [InlineData(new byte[] { 0x41, 0x00, 0x42, 0x00, 0x43, 0x00 }, LoadFileEncodingKind.Utf16LE, EncodingSource.Heuristic, 0)]
    [InlineData(new byte[] { 0x00, 0x41, 0x00, 0x42, 0x00, 0x43 }, LoadFileEncodingKind.Utf16BE, EncodingSource.Heuristic, 0)]
    [InlineData(new byte[] { 0x41, 0xC3, 0xBE, 0x42 }, LoadFileEncodingKind.Utf8, EncodingSource.Heuristic, 0)]
    [InlineData(new byte[] { 0x41, 0xFE, 0x42 }, LoadFileEncodingKind.Windows1252, EncodingSource.Heuristic, 0)]
    [InlineData(new byte[] { 0x41, 0x42 }, LoadFileEncodingKind.Utf8, EncodingSource.Heuristic, 0)]
    public void Encoding_detection_sniffs_boms_then_heuristics(byte[] sample, LoadFileEncodingKind kind, EncodingSource source, int preamble)
    {
        DetectedEncoding detected = EncodingDetector.Detect(sample, isWholeFile: true);
        (detected.Kind, detected.Source, detected.PreambleLength).Should().Be((kind, source, preamble));
    }

    [Fact]
    public void Explicit_override_wins_and_skips_only_a_matching_bom()
    {
        EncodingDetector.Detect([0xEF, 0xBB, 0xBF, 0x41], true, LoadFileEncodingKind.Utf8).Should().Be(new DetectedEncoding(LoadFileEncodingKind.Utf8, EncodingSource.Override, 3));
        EncodingDetector.Detect([0xEF, 0xBB, 0xBF, 0x41], true, LoadFileEncodingKind.Windows1252).PreambleLength.Should().Be(0);
        EncodingDetector.Detect([0x41, 0x42], true).IsCompatibleWith(LoadFileEncodingKind.Windows1252).Should().BeTrue();
    }

    [Fact]
    public void Windows1252_round_trips_every_defined_character()
    {
        byte[] all = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
        string decoded = Windows1252Encoding.Instance.GetString(all);
        decoded[0x80].Should().Be('€');
        decoded[0x81].Should().Be('\u0081');
        decoded[0xFE].Should().Be('þ');
        Windows1252Encoding.Instance.GetBytes(decoded).Should().Equal(all);
        Windows1252Encoding.Instance.GetBytes("☃").Should().Equal((byte)'?');
    }
}
