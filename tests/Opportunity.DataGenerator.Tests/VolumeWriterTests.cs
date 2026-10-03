using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using AwesomeAssertions;

using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Text;
using Opportunity.DataGenerator.Corpus.Volumes;

// MD5 is verified as an eDiscovery load-file field, not used for security.
#pragma warning disable CA5351

namespace Opportunity.DataGenerator.Tests;

public class VolumeWriterTests
{
    private const ulong Seed = 21;

    public static TheoryData<string, LoadFileEncoding, LoadFileEncoding> Formats => new()
    {
        { "concordance", LoadFileEncoding.Utf8Bom, LoadFileEncoding.Utf8 },
        { "concordance", LoadFileEncoding.Utf8, LoadFileEncoding.Utf8Bom },
        { "vendor", LoadFileEncoding.Utf16Le, LoadFileEncoding.Utf16Le },
        { "csv", LoadFileEncoding.Utf8Bom, LoadFileEncoding.Utf8 },
        { "concordance", LoadFileEncoding.Windows1252, LoadFileEncoding.Windows1252 },
    };

    [Fact]
    public void Volumes_are_byte_identical_for_the_same_seed_and_profile_across_runs_and_thread_counts()
    {
        CorpusProfile profile = TestCorpus.SmallText(500);
        var options = new VolumeOptions { DefectRates = VolumeOptions.AllDefects(0.05), OverlayRate = 0.1, DocumentsPerVolume = 200, FilesPerFolder = 50 };
        using VolumeRun a = VolumeRun.Create(profile, Seed, options, threads: 1);
        using VolumeRun b = VolumeRun.Create(profile, Seed, options, threads: 4);
        using VolumeRun c = VolumeRun.Create(profile, Seed + 1, options, threads: 2);

        Dictionary<string, string> filesA = Snapshot(a.Root);
        filesA.Count.Should().BeGreaterThan(1000);
        Snapshot(b.Root).Should().Equal(filesA);
        Snapshot(c.Root)[Path.Combine("VOL001", "DATA", "VOL001.dat")].Should().NotBe(filesA[Path.Combine("VOL001", "DATA", "VOL001.dat")]);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_clean_volume_round_trips_every_value_link_hash_and_page(string preset, LoadFileEncoding datEncoding, LoadFileEncoding textEncoding)
    {
        CorpusProfile profile = TestCorpus.SmallText(400);
        var options = new VolumeOptions
        {
            Delimiters = DelimiterProfile.Preset(preset),
            DatEncoding = datEncoding,
            TextEncoding = textEncoding,
            FilesPerFolder = 100,
        };
        using VolumeRun run = VolumeRun.Create(profile, Seed, options);
        var context = new GenerationContext(profile, Seed);
        List<GeneratedDocument> docs = TestCorpus.Documents(profile, Seed);
        var synth = new TextSynthesizer(context.Vocabulary);
        var formatter = new DatValueFormatter(options);
        var writerHeader = VolumeWriter.StructuralColumns.Concat(context.Catalog.Fields.Select(f => f.Name)).ToList();

        byte[] dat = run.Dat();
        dat.AsSpan().StartsWith(LoadFileEncodings.Preamble(datEncoding)).Should().BeTrue();
        List<List<string>> rows = run.ParseDat();
        rows[0].Should().Equal(writerHeader);
        rows.Should().HaveCount(docs.Count + 1);
        File.ReadAllText(Path.Combine(run.Root, VolumeWriter.DefectsFile)).Should().BeEmpty();

        Func<string, int> col = writerHeader.IndexOf;
        string Exp(string v) => DatTestReader.Expected(v, options.Delimiters, datEncoding);
        var md5ByContent = new Dictionary<string, string>();
        var contentByMd5 = new Dictionary<string, string>();
        for (int r = 0; r < docs.Count; r++)
        {
            GeneratedDocument doc = docs[r];
            List<string> row = rows[r + 1];
            row.Should().HaveCount(writerHeader.Count, doc.ControlNumber);
            row[col("ControlNumber")].Should().Be(doc.ControlNumber);
            row[col("BegAttach")].Should().Be(doc.BegAttach);
            row[col("EndAttach")].Should().Be(doc.EndAttach);
            row[col("ParentID")].Should().Be(doc.ParentControlNumber ?? "");
            row[col("GroupIdentifier")].Should().Be(doc.FamilyId);
            row[col("Custodian")].Should().Be(Exp(doc.Custodian));
            row[col("Subject")].Should().Be(Exp(doc.Fields[FieldCatalog.Subject] as string ?? ""));
            row[col("FileName")].Should().Be(Exp((string)doc.Fields[FieldCatalog.FileName]!));
            if (doc.Fields[FieldCatalog.DateSent] is DateTimeOffset sent)
            {
                DateTimeOffset.Parse(row[col("DateSent")], CultureInfo.InvariantCulture).Should().Be(sent);
            }

            for (int f = 0; f < context.Catalog.Count; f++)
            {
                if (f != FieldCatalog.FileSize)
                {
                    row[VolumeWriter.StructuralColumns.Count + f].Should().Be(Exp(formatter.Format(doc.Fields[f])), $"{doc.ControlNumber} {context.Catalog.Fields[f].Name}");
                }
            }

            // Natives: the DAT hashes and size are those of the bytes on disk; copies of a content share them.
            byte[] native = File.ReadAllBytes(run.Resolve(row[col("NativeLink")]));
            string md5 = Convert.ToHexStringLower(MD5.HashData(native));
            row[col("MD5Hash")].Should().Be(md5);
            row[col("SHA256Hash")].Should().Be(Convert.ToHexStringLower(SHA256.HashData(native)));
            row[col("FileSize")].Should().Be(native.Length.ToString(CultureInfo.InvariantCulture));
            md5ByContent.TryAdd(doc.Md5, md5);
            md5ByContent[doc.Md5].Should().Be(md5, "duplicates share native bytes");
            contentByMd5.TryAdd(md5, doc.Md5);
            contentByMd5[md5].Should().Be(doc.Md5, "different contents never share native bytes");

            // Extracted text in the configured encoding.
            byte[] text = File.ReadAllBytes(run.Resolve(row[col("TextLink")]));
            text.AsSpan().StartsWith(LoadFileEncodings.Preamble(textEncoding)).Should().BeTrue();
            string expectedText = DatTestReader.Expected(synth.Render(doc.Content.Text), DelimiterProfile.Csv, textEncoding);
            LoadFileEncodings.GetString(textEncoding, text).Should().Be(expectedText);
        }

        // OPT: one row per page, Y + page count on the first row, every image on disk.
        List<string[]> opt = run.Opt();
        int line = 0;
        foreach (GeneratedDocument doc in docs.Where(d => d.Fields[FieldCatalog.PageCount] is long))
        {
            long pages = (long)doc.Fields[FieldCatalog.PageCount]!;
            opt[line][0].Should().Be(doc.ControlNumber);
            opt[line][1].Should().Be("VOL001");
            opt[line][3].Should().Be("Y");
            opt[line][6].Should().Be(pages.ToString(CultureInfo.InvariantCulture));
            for (int p = 0; p < pages; p++)
            {
                string[] o = opt[line + p];
                o.Should().HaveCount(7);
                if (p > 0)
                {
                    o[0].Should().Be(VolumeWriter.PageKey(doc.ControlNumber, p + 1));
                    o[3].Should().BeEmpty();
                }

                File.Exists(run.Resolve(o[2])).Should().BeTrue(o[2]);
            }

            line += (int)pages;
        }

        line.Should().Be(opt.Count);
        Directory.GetFiles(Path.Combine(run.Volume(), "NATIVES", "NATIVE0001")).Should().HaveCount(100);
    }

    [Fact]
    public void Every_defect_type_is_injected_at_the_configured_rate_and_recorded_accurately()
    {
        CorpusProfile profile = TestCorpus.SmallText(2500);
        var options = new VolumeOptions { DefectRates = VolumeOptions.AllDefects(0.04), OverlayRate = 0.2 };
        using VolumeRun run = VolumeRun.Create(profile, Seed, options);
        List<GeneratedDocument> docs = TestCorpus.Documents(profile, Seed);
        var context = new GenerationContext(profile, Seed);
        var synth = new TextSynthesizer(context.Vocabulary);
        var header = VolumeWriter.StructuralColumns.Concat(context.Catalog.Fields.Select(f => f.Name)).ToList();
        Func<string, int> col = header.IndexOf;
        List<JsonElement> defects = run.Defects();

        // Rates: injected = eligible × rate (±1), and the defects file lists exactly the injected count.
        foreach (JsonElement entry in run.Manifest().GetProperty("defects").EnumerateArray())
        {
            string type = entry.GetProperty("type").GetString()!;
            long eligible = entry.GetProperty("eligible").GetInt64();
            long injected = entry.GetProperty("injected").GetInt64();
            eligible.Should().BeGreaterThan(type is "textEncoding" or "overlayUnknownKey" or "orphanAttachments" or "brokenFamilyRange" ? 10 : 100, type);
            injected.Should().BeInRange((long)Math.Floor((eligible * 0.04) - 1), (long)Math.Ceiling((eligible * 0.04) + 1), type);
            defects.Count(d => d.GetProperty("type").GetString() == type).Should().Be((int)injected, type);
        }

        // Rows: split on CRLF (Concordance has no literal line breaks in values) and decode each row with the
        // encoding the ground truth says it was written in.
        byte[] dat = run.Dat();
        var encodingByRow = defects.Where(d => Is(d, "datRowEncoding"))
            .ToDictionary(d => d.GetProperty("datRow").GetInt32(), d => Enc(d.GetProperty("actualEncoding").GetString()!));
        List<List<string>> rows = SplitRows(dat[3..]).Select((bytes, i) =>
            DatTestReader.ParseAll(LoadFileEncodings.GetString(encodingByRow.GetValueOrDefault(i, LoadFileEncoding.Utf8), bytes), options.Delimiters)[0]).ToList();
        rows[0].Should().Equal(header);
        foreach (int row in encodingByRow.Keys)
        {
            LoadFileEncodings.GetString(LoadFileEncoding.Utf8, SplitRows(dat[3..])[row]).Should().Contain("�", "a 1252 row is not valid UTF-8");
        }

        List<List<string>> data = rows.Skip(1).ToList();
        ILookup<string, List<string>> byCn = data.ToLookup(r => r[0]);
        List<string[]> opt = run.Opt();
        Dictionary<string, GeneratedDocument> docByCn = docs.ToDictionary(d => d.ControlNumber);

        foreach (JsonElement d in defects)
        {
            string cn = d.GetProperty("controlNumber").GetString()!;
            string type = d.GetProperty("type").GetString()!;
            d.GetProperty("volume").GetString().Should().Be("VOL001");
            switch (type)
            {
                case "missingNative":
                case "missingText":
                    string path = d.GetProperty("path").GetString()!;
                    File.Exists(run.Resolve(path)).Should().BeFalse(path);
                    byCn[cn].Should().Contain(r => r[col(type == "missingNative" ? "NativeLink" : "TextLink")] == path);
                    break;
                case "missingImage":
                    string image = d.GetProperty("path").GetString()!;
                    File.Exists(run.Resolve(image)).Should().BeFalse(image);
                    opt.Should().Contain(o => o[2] == image);
                    break;
                case "hashMismatch":
                    byte[] native = File.ReadAllBytes(run.Resolve(d.GetProperty("path").GetString()!));
                    string actual = Convert.ToHexStringLower(MD5.HashData(native));
                    d.GetProperty("actualMd5").GetString().Should().Be(actual);
                    d.GetProperty("actualSha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(native)));
                    d.GetProperty("datMd5").GetString().Should().NotBe(actual);
                    byCn[cn].Should().Contain(r => r[col("MD5Hash")] == d.GetProperty("datMd5").GetString());
                    break;
                case "textEncoding":
                    byte[] text = File.ReadAllBytes(run.Resolve(d.GetProperty("path").GetString()!));
                    string rendered = synth.Render(docByCn[cn].Content.Text);
                    LoadFileEncoding actualEncoding = Enc(d.GetProperty("actualEncoding").GetString()!);
                    LoadFileEncodings.GetString(actualEncoding, text).Should().Be(DatTestReader.Expected(rendered, DelimiterProfile.Csv, actualEncoding));
                    text.Should().NotEqual(LoadFileEncodings.GetBytes(LoadFileEncoding.Utf8, rendered), "the file is observably mis-encoded");
                    break;
                case "optPageCountMismatch":
                    int optLine = d.GetProperty("optLine").GetInt32();
                    string[] breakRow = opt[optLine - 1];
                    breakRow[0].Should().Be(cn);
                    breakRow[3].Should().Be("Y");
                    breakRow[6].Should().Be(d.GetProperty("declaredPages").GetInt32().ToString(CultureInfo.InvariantCulture));
                    int pages = 1 + opt.Skip(optLine).TakeWhile(o => o[3] != "Y").Count();
                    pages.Should().Be(d.GetProperty("actualPages").GetInt32()).And.NotBe(d.GetProperty("declaredPages").GetInt32());
                    break;
                case "duplicateControlNumber":
                    byCn[cn].Count().Should().BeGreaterThanOrEqualTo(2);
                    data[d.GetProperty("datRow").GetInt32() - 1][0].Should().Be(cn);
                    byCn.Contains(d.GetProperty("originalControlNumber").GetString()!).Should().BeFalse();
                    break;
                case "orphanAttachments":
                    byCn.Contains(cn).Should().BeFalse("the parent row is withheld");
                    foreach (JsonElement orphan in d.GetProperty("orphans").EnumerateArray())
                    {
                        List<string> child = byCn[orphan.GetString()!].Should().ContainSingle().Subject;
                        child[col("GroupIdentifier")].Should().Be(cn);
                        docByCn[orphan.GetString()!].FamilyId.Should().Be(cn);
                    }

                    opt.Should().NotContain(o => o[0] == cn);
                    break;
                case "brokenFamilyRange":
                    List<List<string>> members = data.Where(r => r[col("GroupIdentifier")] == cn).ToList();
                    members.Count.Should().BeGreaterThanOrEqualTo(d.GetProperty("familySize").GetInt32() - 1);
                    members.Should().OnlyContain(r => r[col("BegAttach")] == d.GetProperty("begAttach").GetString() && r[col("EndAttach")] == d.GetProperty("endAttach").GetString());
                    (d.GetProperty("begAttach").GetString() != d.GetProperty("expectedBegAttach").GetString()
                        || d.GetProperty("endAttach").GetString() != d.GetProperty("expectedEndAttach").GetString()).Should().BeTrue();
                    break;
                case "badDate":
                    List<string> dated = data[d.GetProperty("datRow").GetInt32() - 1];
                    dated[0].Should().Be(cn);
                    dated[col(d.GetProperty("field").GetString()!)].Should().Be(d.GetProperty("value").GetString());
                    DateTimeOffset.TryParseExact(dated[col(d.GetProperty("field").GetString()!)], ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _).Should().BeFalse();
                    break;
                case "unescapedQualifier":
                    List<string> raw = data[d.GetProperty("datRow").GetInt32() - 1];
                    raw[0].Should().Be(cn);
                    GeneratedDocument source = docByCn[cn];
                    string original = source.Fields[d.GetProperty("field").GetString() == "Subject" ? FieldCatalog.Subject : FieldCatalog.FileName] as string ?? "";
                    raw[col(d.GetProperty("field").GetString()!)].Count(ch => ch == 'þ').Should().Be(original.Count(ch => ch == 'þ') + 1);
                    break;
                case "fieldCountMismatch":
                    List<string> counted = data[d.GetProperty("datRow").GetInt32() - 1];
                    counted[0].Should().Be(cn);
                    counted.Should().HaveCount(d.GetProperty("actualFields").GetInt32());
                    d.GetProperty("expectedFields").GetInt32().Should().Be(header.Count);
                    break;
                case "datRowEncoding":
                    data[d.GetProperty("datRow").GetInt32() - 1][0].Should().Be(cn);
                    break;
                case "overlayUnknownKey":
                    byCn.Contains(cn).Should().BeFalse();
                    File.ReadAllText(Path.Combine(run.Volume(), "DATA", "VOL001_OVERLAY.dat")).Should().Contain("þ" + cn + "þ");
                    break;
                default:
                    throw new InvalidOperationException("untested defect type " + type);
            }
        }

        defects.Select(d => d.GetProperty("type").GetString()).Distinct().Should().HaveCount(Enum.GetValues<DefectType>().Length);

        // Rows without row-level defects keep the header's field count.
        var damaged = defects.Where(d => Is(d, "fieldCountMismatch")).Select(d => d.GetProperty("datRow").GetInt32() - 1).ToHashSet();
        data.Where((r, i) => !damaged.Contains(i)).Should().OnlyContain(r => r.Count == header.Count);

        // Overlay rows: ground truth matches the overlay DAT, keys exist (except injected unknown keys).
        List<List<string>> overlay = DatTestReader.ParseAll(LoadFileEncodings.GetString(LoadFileEncoding.Utf8Bom,
            File.ReadAllBytes(Path.Combine(run.Volume(), "DATA", "VOL001_OVERLAY.dat"))), options.Delimiters);
        overlay[0].Should().Equal(VolumeWriter.OverlayColumns);
        string[] truth = File.ReadAllLines(Path.Combine(run.Root, VolumeWriter.OverlaysFile));
        JsonElement overlaySummary = run.Manifest().GetProperty("overlay");
        truth.Length.Should().Be(overlaySummary.GetProperty("documents").GetInt32());
        truth.Length.Should().BeInRange((int)(overlaySummary.GetProperty("eligible").GetInt32() * 0.2) - 1, (int)(overlaySummary.GetProperty("eligible").GetInt32() * 0.2) + 1);
        foreach (string t in truth)
        {
            JsonElement o = JsonDocument.Parse(t).RootElement;
            List<string> orow = overlay[o.GetProperty("overlayRow").GetInt32()];
            orow[0].Should().Be(o.GetProperty("controlNumber").GetString());
            orow[1].Should().Be(o.GetProperty("fields").GetProperty("Confidentiality").GetString()).And.NotBe(o.GetProperty("previous").GetProperty("Confidentiality").GetString());
            orow[2].Should().BeEmpty();
            byCn[orow[0]].Should().NotBeEmpty();
        }
    }

    [Fact]
    public void Defect_schedule_hits_the_configured_rate_within_one_and_is_deterministic()
    {
        foreach (double rate in new[] { 0.0, 0.001, 0.013, 0.05, 0.25, 0.5, 0.9, 1.0 })
        {
            var a = new DefectSchedule(5, 3, rate);
            var b = new DefectSchedule(5, 3, rate);
            var other = new DefectSchedule(5, 4, rate);
            var picksA = new List<int>();
            bool differs = false;
            for (int i = 0; i < 100_000; i++)
            {
                bool x = a.Next();
                b.Next().Should().Be(x);
                differs |= other.Next() != x;
                if (x)
                {
                    picksA.Add(i);
                }
            }

            a.Selected.Should().BeInRange((long)Math.Floor((100_000 * rate) - 1), (long)Math.Ceiling((100_000 * rate) + 1), rate.ToString(CultureInfo.InvariantCulture));
            if (rate is > 0 and < 0.5)
            {
                differs.Should().BeTrue();
            }

            if (rate is > 0 and <= 0.05)
            {
                picksA.Zip(picksA.Skip(1), (p, q) => q - p).Distinct().Count().Should().BeGreaterThan(10, "positions are jittered, not periodic");
            }
        }
    }

    [Fact]
    public void Volumes_split_at_family_boundaries_and_folders_respect_the_file_limit()
    {
        CorpusProfile profile = TestCorpus.SmallText(600);
        var options = new VolumeOptions { DocumentsPerVolume = 150, FilesPerFolder = 40, ImageFormat = PageImageFormat.MultiPageTiff, IncludeText = false };
        using VolumeRun run = VolumeRun.Create(profile, Seed, options);
        string[] volumes = [.. Directory.GetDirectories(run.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        volumes.Length.Should().BeGreaterThanOrEqualTo(3);
        volumes[0].Should().Be("VOL001");
        var all = new List<List<string>>();
        foreach (string volume in volumes)
        {
            List<List<string>> data = run.ParseDat(volume).Skip(1).ToList();
            var families = data.GroupBy(r => r[4]).ToList();
            foreach (IGrouping<string, List<string>> family in families)
            {
                family.Should().Contain(r => r[0] == family.Key, "the family parent is in the same volume");
                family.Should().Contain(r => r[0] == family.First()[2], "the last family member is in the same volume");
            }

            all.AddRange(data);
            run.Opt(volume).Should().OnlyContain(o => o[1] == volume && o[3] == "Y", "multi-page TIFF uses one OPT row per document");
            foreach (string folder in Directory.GetDirectories(Path.Combine(run.Volume(volume), "NATIVES")))
            {
                Directory.GetFiles(folder).Length.Should().BeLessThanOrEqualTo(40);
            }

            Directory.Exists(Path.Combine(run.Volume(volume), "TEXT")).Should().BeFalse();
        }

        all.Should().HaveCount(600);
        all.Select(r => r[0]).Should().OnlyHaveUniqueItems();
        all.Should().OnlyContain(r => r[13] == "", "no text files were written");
        run.Manifest().GetProperty("volumes").GetArrayLength().Should().Be(volumes.Length);
    }

    [Theory]
    [InlineData(DatDateFormat.Us, "-05:00")]
    [InlineData(DatDateFormat.Eu, "+01:00")]
    public void Us_and_eu_dates_are_rendered_in_the_configured_zone(DatDateFormat format, string offset)
    {
        CorpusProfile profile = TestCorpus.SmallText(150);
        TimeSpan zone = TimeSpan.Parse(offset.TrimStart('+'), CultureInfo.InvariantCulture);
        using VolumeRun run = VolumeRun.Create(profile, Seed, new VolumeOptions { DateFormat = format, TimeZoneOffset = zone, IncludeImages = false, IncludeNatives = false, IncludeText = false });
        List<List<string>> rows = run.ParseDat();
        int sentCol = rows[0].IndexOf("DateSent");
        string pattern = format == DatDateFormat.Us ? "MM/dd/yyyy hh:mm:ss tt" : "dd/MM/yyyy HH:mm:ss";
        List<GeneratedDocument> docs = TestCorpus.Documents(profile, Seed);
        int checkedDates = 0;
        for (int i = 0; i < docs.Count; i++)
        {
            if (docs[i].Fields[FieldCatalog.DateSent] is DateTimeOffset sent)
            {
                DateTime local = DateTime.ParseExact(rows[i + 1][sentCol], pattern, CultureInfo.InvariantCulture);
                new DateTimeOffset(local, zone).Should().Be(sent);
                checkedDates++;
            }
        }

        checkedDates.Should().BeGreaterThan(20);
    }

    private static bool Is(JsonElement defect, string type) => defect.GetProperty("type").GetString() == type;

    private static LoadFileEncoding Enc(string name) => LoadFileEncodings.TryParse(name, out LoadFileEncoding e) ? e : throw new ArgumentException(name);

    private static List<byte[]> SplitRows(byte[] bytes)
    {
        var rows = new List<byte[]>();
        int start = 0;
        for (int i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n')
            {
                rows.Add(bytes[start..i]);
                start = i + 2;
                i++;
            }
        }

        return rows;
    }

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));
}
