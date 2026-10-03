using System.Text.Json;

using AwesomeAssertions;

using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Import.LoadFiles;

using GenEncoding = Opportunity.DataGenerator.Corpus.Volumes.LoadFileEncoding;
using GenEncodings = Opportunity.DataGenerator.Corpus.Volumes.LoadFileEncodings;
using GenProfile = Opportunity.DataGenerator.Corpus.Volumes.DelimiterProfile;
using ImportProfile = Opportunity.Import.LoadFiles.DelimiterProfile;

namespace Opportunity.UnitTests.Import;

/// <summary>
/// Parses volumes written by the test-data generator (E17-T02): clean volumes must round-trip every value for every
/// preset × encoding; defective volumes must yield exactly the DAT-level defects in the ground-truth manifest.
/// </summary>
public class GeneratedVolumeTests
{
    private const ulong Seed = 20261003;

    public static TheoryData<string, GenEncoding> PresetsByEncoding()
    {
        var data = new TheoryData<string, GenEncoding>();
        foreach (GenProfile p in GenProfile.Presets)
        {
            foreach (GenEncoding e in Enum.GetValues<GenEncoding>())
            {
                data.Add(p.Name, e);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PresetsByEncoding))]
    public async Task Clean_generated_volume_round_trips_every_value(string preset, GenEncoding encoding)
    {
        CorpusProfile profile = ProfileSerializer.WithDocumentCount(new CorpusProfile(), 300);
        var options = new VolumeOptions
        {
            Delimiters = GenProfile.Preset(preset),
            DatEncoding = encoding,
            IncludeNatives = false,
            IncludeImages = false,
            IncludeText = false,
        };
        using var run = GeneratedVolume.Create(profile, Seed, options);
        var context = new GenerationContext(profile, Seed);
        var formatter = new DatValueFormatter(options);
        List<GeneratedDocument> docs = [.. new CorpusGenerator(profile, Seed, 1).GenerateDocuments()];

        DatParse parse = await DatTestSupport.ParseAsync(run.DatBytes(), new DatReaderOptions { Profile = Map(options.Delimiters) });

        parse.Reader.Encoding.Kind.Should().Be(Map(encoding));
        parse.Issues.Should().BeEmpty();
        parse.Reader.Header.Names.Should().Equal([.. VolumeWriter.StructuralColumns, .. context.Catalog.Fields.Select(f => f.Name)]);
        parse.Records.Should().HaveCount(docs.Count);
        string Exp(string v) => Expected(v, options.Delimiters, encoding);
        for (int r = 0; r < docs.Count; r++)
        {
            GeneratedDocument doc = docs[r];
            string[] expected =
            [
                doc.ControlNumber, doc.BegAttach, doc.EndAttach, doc.ParentControlNumber ?? "", doc.FamilyId, Exp(doc.Custodian),
                Exp(formatter.Format(doc.AllCustodians.ToArray())), Exp(formatter.Format(doc.DuplicateCustodians.ToArray())),
                doc.DuplicateGroupId ?? "", doc.EmailThreadId ?? "", doc.Md5, doc.Content.Sha256, "", "",
                .. doc.Fields.Select(f => Exp(formatter.Format(f))),
            ];
            parse.Records[r].Values.Should().Equal(expected, $"row {r + 1} ({doc.ControlNumber})");
        }
    }

    public static TheoryData<string, GenEncoding> DefectMatrix() => new()
    {
        { "concordance", GenEncoding.Utf8 },
        { "concordance", GenEncoding.Utf8Bom },
        { "concordance", GenEncoding.Windows1252 },
        { "concordance", GenEncoding.Utf16Le },
        { "vendor", GenEncoding.Utf8Bom },
        { "vendor", GenEncoding.Windows1252 },
        { "csv", GenEncoding.Utf8 },
        { "csv", GenEncoding.Windows1252 },
    };

    [Theory]
    [MemberData(nameof(DefectMatrix))]
    public async Task Injected_defects_are_detected_and_match_the_ground_truth(string preset, GenEncoding encoding)
    {
        CorpusProfile profile = ProfileSerializer.WithDocumentCount(new CorpusProfile(), 600);
        var options = new VolumeOptions
        {
            Delimiters = GenProfile.Preset(preset),
            DatEncoding = encoding,
            TextEncoding = encoding,
            IncludeNatives = false,
            IncludeImages = false,
            DefectRates = VolumeOptions.AllDefects(0.04),
        };
        using var run = GeneratedVolume.Create(profile, Seed, options);
        List<JsonElement> truth = run.Defects();
        using var errorFile = new MemoryStream();
        var errorWriter = new DatErrorFileWriter(errorFile, leaveOpen: true);
        DatParse parse = await DatTestSupport.ParseAsync(run.DatBytes(), new DatReaderOptions
        {
            Profile = Map(options.Delimiters),
            MaxRetainedIssues = int.MaxValue,
            ReturnRejectedRecords = true,
            Sinks = [errorWriter],
        });
        errorWriter.Dispose();

        HashSet<(long Row, string Detail)> Truth(DefectType type, Func<JsonElement, string> detail) =>
            [.. truth.Where(d => d.GetProperty("type").GetString() == DefectNames.Name(type)).Select(d => (d.GetProperty("datRow").GetInt64(), detail(d)))];
        HashSet<(long Row, string Detail)> Found(DatIssueKind kind, Func<DatIssue, string> detail) =>
            [.. parse.Issues.Where(i => i.Kind == kind).Select(i => (i.RowNumber, detail(i)))];

        // Every DAT-level defect is found at the right row, with the right column / counts / encodings, and nothing else is reported.
        var fieldCount = Truth(DefectType.FieldCountMismatch, d => $"{d.GetProperty("expectedFields").GetInt32()}/{d.GetProperty("actualFields").GetInt32()}");
        var unescaped = Truth(DefectType.UnescapedQualifier, d => d.GetProperty("field").GetString()!);
        Dictionary<long, DatRecord> byRow = parse.Records.ToDictionary(r => r.RowNumber);
        byte[] dat = run.DatBytes();

        // A UTF-8 row whose non-ASCII characters are all outside Windows-1252 is written as pure ASCII ('?'): no byte-level signal.
        var rowEncoding = Truth(DefectType.DatRowEncoding, d => d.GetProperty("actualEncoding").GetString()!);
        rowEncoding.RemoveWhere(x => dat.AsSpan((int)byRow[x.Row].ByteOffset, byRow[x.Row].ByteLength).IndexOfAnyExceptInRange((byte)0, (byte)0x7F) < 0);
        fieldCount.Should().NotBeEmpty();
        unescaped.Should().NotBeEmpty();
        if (encoding != GenEncoding.Utf16Le)
        {
            rowEncoding.Should().NotBeEmpty();
        }

        Found(DatIssueKind.FieldCountMismatch, i => $"{i.ExpectedFieldCount}/{i.ObservedFieldCount}").Should().BeEquivalentTo(fieldCount);
        Found(DatIssueKind.UnescapedQualifier, i => i.Column!).Should().BeEquivalentTo(unescaped);
        Found(DatIssueKind.MixedEncodingRow, i => i.Message.Contains("decoded as UTF-8", StringComparison.Ordinal) ? "utf-8" : "windows-1252")
            .Should().BeEquivalentTo(rowEncoding);
        parse.Issues.Select(i => i.Kind).Distinct().Should().BeSubsetOf([DatIssueKind.FieldCountMismatch, DatIssueKind.UnescapedQualifier, DatIssueKind.MixedEncodingRow]);

        // Issues name the control number as written in the DAT.
        var keyByRow = truth.Where(d => d.TryGetProperty("datRow", out _)).GroupBy(d => d.GetProperty("datRow").GetInt64()).ToDictionary(g => g.Key, g => g.First().GetProperty("controlNumber").GetString());
        parse.Issues.Should().OnlyContain(i => i.ControlNumber == keyByRow[i.RowNumber]);

        // Rejected = rows with a field-count or qualifier defect; they and only they reach the error file.
        long rejected = fieldCount.Select(x => x.Row).Union(unescaped.Select(x => x.Row)).LongCount();
        parse.Reader.Statistics.RejectedRows.Should().Be(rejected);
        errorWriter.RowsWritten.Should().Be(rejected);
        DatParse reloaded = await DatTestSupport.ParseAsync(errorFile.ToArray(), new DatReaderOptions { Profile = Map(options.Delimiters), MaxRejectedRows = null });
        reloaded.Reader.Statistics.Rows.Should().Be(rejected);
        reloaded.Reader.Header.Names[^1].Should().Be(DatErrorFileWriter.ErrorColumn);

        // Mixed-encoding rows are recovered: the re-decoded row carries the same control number as the ground truth.
        foreach ((long row, _) in rowEncoding)
        {
            byRow[row].ControlNumber.Should().Be(keyByRow[row]);
            byRow[row].Values.Should().NotContain(v => v.Contains('�') || DatPreview.LooksMisdecoded(v));
        }

        // Value-level defects pass through verbatim for field mapping / import validation (E08-T02/T03/T06).
        foreach (JsonElement d in truth.Where(d => d.GetProperty("type").GetString() == DefectNames.Name(DefectType.BadDate)))
        {
            DatRecord record = byRow[d.GetProperty("datRow").GetInt64()];
            if (!record.IsRejected)
            {
                record[d.GetProperty("field").GetString()!].Should().Be(d.GetProperty("value").GetString());
            }
        }

        foreach (JsonElement d in truth.Where(d => d.GetProperty("type").GetString() == DefectNames.Name(DefectType.DuplicateControlNumber)))
        {
            byRow[d.GetProperty("datRow").GetInt64()].ControlNumber.Should().Be(d.GetProperty("controlNumber").GetString());
        }

        // Extracted text: each file's own detection exposes the files written in the wrong encoding.
        LoadFileEncodingKind declared = Map(encoding);
        HashSet<string> wrongText = [.. truth.Where(d => d.GetProperty("type").GetString() == DefectNames.Name(DefectType.TextEncoding)).Select(d => d.GetProperty("path").GetString()!)];
        wrongText.Should().NotBeEmpty();
        int detectedWrong = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(run.Volume, "TEXT"), "*.txt", SearchOption.AllDirectories))
        {
            string link = "TEXT\\" + Path.GetRelativePath(Path.Combine(run.Volume, "TEXT"), file).Replace(Path.DirectorySeparatorChar, '\\');
            byte[] bytes = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
            DetectedEncoding detected = EncodingDetector.Detect(bytes, isWholeFile: true);

            // A 1252 copy whose non-ASCII characters were all unrepresentable is pure ASCII: no byte-level signal exists.
            bool detectable = declared == LoadFileEncodingKind.Utf16LE || bytes.AsSpan().IndexOfAnyExceptInRange((byte)0, (byte)0x7F) >= 0;
            bool wrong = wrongText.Contains(link);
            detectedWrong += wrong && detectable ? 1 : 0;
            detected.IsCompatibleWith(declared).Should().Be(!(wrong && detectable), $"{link} detected as {detected.Name}");
        }

        detectedWrong.Should().BePositive();
    }

    [Fact]
    public async Task Generated_volume_parses_identically_through_a_tiny_trickling_buffer()
    {
        CorpusProfile profile = ProfileSerializer.WithDocumentCount(new CorpusProfile(), 150);
        var options = new VolumeOptions { DatEncoding = GenEncoding.Utf16Le, IncludeNatives = false, IncludeImages = false, IncludeText = false, DefectRates = VolumeOptions.AllDefects(0.05) };
        using var run = GeneratedVolume.Create(profile, Seed, options);
        byte[] dat = run.DatBytes();
        DatParse normal = await DatTestSupport.ParseAsync(dat);
        await using DatReader tiny = await DatReader.OpenAsync(new DatTestSupport.TricklingStream(dat, 5), new DatReaderOptions { BufferBytes = 64 }, cancellationToken: TestContext.Current.CancellationToken);
        var values = new List<List<string>>();
        await foreach (DatRecord r in tiny.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            values.Add([.. r.Values]);
        }

        values.Should().BeEquivalentTo(normal.Values, o => o.WithStrictOrdering());
        tiny.Issues.Should().BeEquivalentTo(normal.Issues);
    }

    internal static LoadFileEncodingKind Map(GenEncoding encoding) => encoding switch
    {
        GenEncoding.Utf16Le => LoadFileEncodingKind.Utf16LE,
        GenEncoding.Windows1252 => LoadFileEncodingKind.Windows1252,
        _ => LoadFileEncodingKind.Utf8,
    };

    internal static ImportProfile Map(GenProfile p) =>
        new(p.Name == "vendor" ? ImportProfile.ConcordancePilcrow.Name : p.Name, p.Column, p.Quote, p.Newline, p.MultiValue, p.NestedValue);

    /// <summary>What the reader should return for a generator value: 1252 round-trip loss, and line breaks as <c>\n</c> for DAT presets.</summary>
    private static string Expected(string value, GenProfile p, GenEncoding encoding)
    {
        if (encoding == GenEncoding.Windows1252)
        {
            value = GenEncodings.GetString(encoding, GenEncodings.GetBytes(encoding, value));
        }

        return p.Newline is { } nl
            ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace(nl, '\n')
            : value;
    }

    private sealed class GeneratedVolume : IDisposable
    {
        private GeneratedVolume(string root) => Root = root;

        public string Root { get; }

        public string Volume => Path.Combine(Root, "VOL001");

        public static GeneratedVolume Create(CorpusProfile profile, ulong seed, VolumeOptions options)
        {
            string root = Path.Combine(Path.GetTempPath(), "opp-dat-" + Guid.NewGuid().ToString("N"));
            CorpusRunner.Run(profile, seed, root, new CorpusRunOptions
            {
                WriteGroundTruth = false,
                SinkFactories = [context => new VolumeWriter(context, root, options)],
            });
            return new GeneratedVolume(root);
        }

        public byte[] DatBytes() => File.ReadAllBytes(Path.Combine(Volume, "DATA", "VOL001.dat"));

        public List<JsonElement> Defects() =>
            [.. File.ReadAllLines(Path.Combine(Root, VolumeWriter.DefectsFile)).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())];

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
