using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Randomness;
using Opportunity.DataGenerator.Corpus.Text;

namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>
/// Writes production-style load-file volumes (<c>VOL001/DATA|IMAGES|NATIVES|TEXT</c>) from the corpus stream, with
/// deterministic defect injection recorded in <c>volume-defects.jsonl</c> and summarised in
/// <c>volume-manifest.json</c>. Streams: memory is bounded by the largest family, never by corpus size.
/// </summary>
public sealed class VolumeWriter : ICorpusSink
{
    public const string Version = "1.0.0";
    public const string ManifestFile = "volume-manifest.json";
    public const string DefectsFile = "volume-defects.jsonl";
    public const string OverlaysFile = "volume-overlays.jsonl";

    public const string NativeHashRule =
        "MD5Hash and SHA256Hash are computed over the exact bytes written to NATIVES; native bytes are a pure function of the document content " +
        "(content key, file type, content metadata, first nativeTextChars characters of extracted text), so all copies of a content share hashes " +
        "and DAT hash groups equal the corpus duplicate groups. FileSize is the written native's byte length. The md5/sha256 in documents.jsonl " +
        "remain synthetic identity hashes. Without natives, the synthetic hashes and simulated FileSize are written.";

    public static readonly IReadOnlyList<string> StructuralColumns =
    [
        "ControlNumber", "BegAttach", "EndAttach", "ParentID", "GroupIdentifier", "Custodian", "AllCustodians", "DuplicateCustodians",
        "DuplicateGroupID", "EmailThreadGroup", "MD5Hash", "SHA256Hash", "NativeLink", "TextLink",
    ];

    public static readonly IReadOnlyList<string> OverlayColumns = ["ControlNumber", "Confidentiality", "ProjectCode"];

    private const int ColControlNumber = 0;
    private const int ColBegAttach = 1;
    private const int ColEndAttach = 2;
    private const int ColMd5 = 10;
    private const int ColSha256 = 11;

    private static readonly string[] BadDateValues = ["2019-02-30", "13/32/2018", "N/A", "31.31.2020", "2018-13-45T99:99:99", "Febtober 3, 2019"];
    private static readonly string[] ConfidentialityValues = ["Public", "Internal", "Confidential", "Highly Confidential"];
    private static readonly int[] DateFields = [FieldCatalog.DateSent, FieldCatalog.DateReceived, FieldCatalog.DateCreated, FieldCatalog.DateLastModified, FieldCatalog.RecordDate];

    private readonly GenerationContext _ctx;
    private readonly VolumeOptions _options;
    private readonly string _root;
    private readonly TextSynthesizer _synthesizer;
    private readonly DatValueFormatter _formatter;
    private readonly IReadOnlyList<FieldDefinition> _fields;
    private readonly string[] _header;
    private readonly Dictionary<DefectType, DefectSchedule> _schedules;
    private readonly DefectSchedule _overlaySchedule;
    private readonly HashingStream _defects;
    private readonly Utf8JsonWriter _defectJson;
    private readonly HashingStream? _overlays;
    private readonly Utf8JsonWriter? _overlayJson;
    private readonly List<VolumeSummary> _volumes = [];
    private readonly List<CorpusOutputFile> _outputs = [];
    private readonly StringBuilder _row = new();
    private Volume? _current;
    private string? _lastStandalone;
    private bool _completed;

    public VolumeWriter(GenerationContext context, string outputDirectory, VolumeOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _ctx = context;
        _options = options;
        _root = outputDirectory;
        Directory.CreateDirectory(_root);
        _synthesizer = new TextSynthesizer(context.Vocabulary);
        _formatter = new DatValueFormatter(options);
        _fields = context.Catalog.Fields;
        _header = [.. StructuralColumns, .. _fields.Select(f => f.Name)];
        _schedules = Enum.GetValues<DefectType>().ToDictionary(t => t, t => new DefectSchedule(context.Seed, (ulong)t, options.RateOf(t)));
        _overlaySchedule = new DefectSchedule(context.Seed, 1000, options.OverlayRate);
        _defects = new HashingStream(File.Create(Path.Combine(_root, DefectsFile), 1 << 16));
        _defectJson = new Utf8JsonWriter(_defects, JsonlCorpusWriter.WriterOptions);
        if (options.OverlayRate > 0)
        {
            _overlays = new HashingStream(File.Create(Path.Combine(_root, OverlaysFile), 1 << 16));
            _overlayJson = new Utf8JsonWriter(_overlays, JsonlCorpusWriter.WriterOptions);
        }
    }

    public IReadOnlyList<string> Header => _header;

    public void Write(GeneratedChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        foreach (GeneratedFamily family in chunk.Families)
        {
            WriteFamily(family);
        }
    }

    public IReadOnlyList<CorpusOutputFile> Complete()
    {
        if (_completed)
        {
            return _outputs;
        }

        _completed = true;
        CloseVolume();
        _defectJson.Flush();
        _defects.Flush();
        _outputs.Add(new CorpusOutputFile(DefectsFile, _defects.BytesWritten, _defects.HashHex()));
        if (_overlays != null)
        {
            _overlayJson!.Flush();
            _overlays.Flush();
            _outputs.Add(new CorpusOutputFile(OverlaysFile, _overlays.BytesWritten, _overlays.HashHex()));
        }

        _outputs.Add(WriteManifest());
        return _outputs;
    }

    public void Dispose()
    {
        _current?.Dispose();
        _defectJson.Dispose();
        _defects.Dispose();
        _overlayJson?.Dispose();
        _overlays?.Dispose();
    }

    // ---- families and documents ------------------------------------------------------------------------------

    private void WriteFamily(GeneratedFamily family)
    {
        if (_current == null || (_options.DocumentsPerVolume > 0 && _current.Summary.Documents >= _options.DocumentsPerVolume))
        {
            OpenNextVolume();
        }

        Volume volume = _current!;
        IReadOnlyList<GeneratedDocument> docs = family.Documents;
        volume.Summary.Documents += docs.Count;
        bool withholdParent = false;
        string? begOverride = null;
        string? endOverride = null;
        if (docs.Count > 1)
        {
            withholdParent = Selected(DefectType.OrphanAttachments);
            if (withholdParent)
            {
                Record(DefectType.OrphanAttachments, volume, docs[0].ControlNumber, w =>
                {
                    w.WriteNumber("orphanCount", docs.Count - 1);
                    w.WriteStartArray("orphans");
                    for (int i = 1; i < docs.Count; i++)
                    {
                        w.WriteStringValue(docs[i].ControlNumber);
                    }

                    w.WriteEndArray();
                });
            }

            if (Selected(DefectType.BrokenFamilyRange))
            {
                Rng rng = Choice(DefectType.BrokenFamilyRange, docs[0].DocIndex);
                string variant;
                switch (rng.NextInt(3))
                {
                    case 0:
                        variant = "truncated";
                        begOverride = docs[0].ControlNumber;
                        endOverride = docs[^2].ControlNumber;
                        break;
                    case 1:
                        variant = "reversed";
                        begOverride = docs[^1].ControlNumber;
                        endOverride = docs[0].ControlNumber;
                        break;
                    default:
                        variant = "prefixMismatch";
                        begOverride = docs[0].ControlNumber;
                        endOverride = "X" + docs[^1].ControlNumber;
                        break;
                }

                Record(DefectType.BrokenFamilyRange, volume, docs[0].ControlNumber, w =>
                {
                    w.WriteString("variant", variant);
                    w.WriteString("expectedBegAttach", docs[0].ControlNumber);
                    w.WriteString("expectedEndAttach", docs[^1].ControlNumber);
                    w.WriteString("begAttach", begOverride);
                    w.WriteString("endAttach", endOverride);
                    w.WriteNumber("familySize", docs.Count);
                });
            }
        }

        for (int i = withholdParent ? 1 : 0; i < docs.Count; i++)
        {
            WriteDocument(volume, docs[i], docs.Count == 1, begOverride, endOverride);
        }
    }

    private void WriteDocument(Volume volume, GeneratedDocument doc, bool standalone, string? begOverride, string? endOverride)
    {
        string cn = doc.ControlNumber;
        string datCn = cn;
        if (standalone && _lastStandalone != null && Selected(DefectType.DuplicateControlNumber))
        {
            datCn = _lastStandalone;
            Record(DefectType.DuplicateControlNumber, volume, datCn, w =>
            {
                w.WriteString("originalControlNumber", cn);
                w.WriteNumber("datRow", volume.Summary.DatRows + 1);
            });
        }

        // Extracted text (also feeds the native's excerpt).
        string? textLink = null;
        var textSink = new EncodingTextSink(_options.NativeTextChars);
        if (_options.IncludeText)
        {
            string folder = volume.Text.Allocate(1);
            textLink = "TEXT\\" + folder + "\\" + cn + ".txt";
            bool missing = Selected(DefectType.MissingText);
            LoadFileEncoding encoding = _options.TextEncoding;
            bool writePreamble = true;
            if (missing)
            {
                RecordPath(DefectType.MissingText, volume, datCn, textLink);
            }
            else if (TextEncodingEligible(doc.Content.Text) && Selected(DefectType.TextEncoding))
            {
                encoding = _options.TextEncoding == LoadFileEncoding.Windows1252 ? LoadFileEncoding.Utf8 : LoadFileEncoding.Windows1252;
                writePreamble = false;
                Record(DefectType.TextEncoding, volume, datCn, w =>
                {
                    w.WriteString("path", textLink);
                    w.WriteString("declaredEncoding", LoadFileEncodings.Name(_options.TextEncoding));
                    w.WriteString("actualEncoding", LoadFileEncodings.Name(encoding));
                });
            }

            if (!missing)
            {
                using FileStream file = volume.CreateFile(textLink);
                if (writePreamble)
                {
                    file.Write(LoadFileEncodings.Preamble(encoding));
                }

                textSink.Output = file;
                textSink.Encoding = encoding;
                _synthesizer.Write(doc.Content.Text, textSink);
                textSink.Output = null;
                volume.Summary.TextFiles++;
            }
            else if (_options.IncludeNatives)
            {
                RenderExcerpt(doc.Content.Text, textSink);
            }
        }
        else if (_options.IncludeNatives)
        {
            RenderExcerpt(doc.Content.Text, textSink);
        }

        // Native.
        string md5 = doc.Md5;
        string sha256 = doc.Content.Sha256;
        string? nativeLink = null;
        object? fileSize = doc.Fields[FieldCatalog.FileSize];
        if (_options.IncludeNatives)
        {
            NativeFile native = NativeFactory.Build(doc.Content, textSink.Excerpt);
#pragma warning disable CA5351 // MD5 is an eDiscovery load-file field (dedupe/verification), not a security control.
            md5 = Convert.ToHexStringLower(MD5.HashData(native.Bytes));
#pragma warning restore CA5351
            sha256 = Convert.ToHexStringLower(SHA256.HashData(native.Bytes));
            fileSize = (long)native.Bytes.Length;
            string folder = volume.Natives.Allocate(1);
            nativeLink = "NATIVES\\" + folder + "\\" + cn + "." + native.Extension;
            if (Selected(DefectType.MissingNative))
            {
                RecordPath(DefectType.MissingNative, volume, datCn, nativeLink);
            }
            else
            {
                using (FileStream file = volume.CreateFile(nativeLink))
                {
                    file.Write(native.Bytes);
                }

                volume.Summary.Natives++;
                if (native.IsPlaceholder)
                {
                    volume.Summary.PlaceholderNatives++;
                }

                if (Selected(DefectType.HashMismatch))
                {
                    string actualMd5 = md5;
                    string actualSha = sha256;
                    md5 = Corrupt(md5);
                    sha256 = Corrupt(sha256);
                    Record(DefectType.HashMismatch, volume, datCn, w =>
                    {
                        w.WriteString("path", nativeLink);
                        w.WriteString("datMd5", md5);
                        w.WriteString("actualMd5", actualMd5);
                        w.WriteString("datSha256", sha256);
                        w.WriteString("actualSha256", actualSha);
                    });
                }
            }
        }

        WriteImages(volume, doc, cn, datCn);

        // DAT row.
        var values = new string[_header.Length];
        values[ColControlNumber] = datCn;
        values[ColBegAttach] = begOverride ?? doc.BegAttach;
        values[ColEndAttach] = endOverride ?? doc.EndAttach;
        values[3] = doc.ParentControlNumber ?? "";
        values[4] = doc.FamilyId;
        values[5] = doc.Custodian;
        values[6] = _formatter.Format(doc.AllCustodians.ToArray());
        values[7] = _formatter.Format(doc.DuplicateCustodians.ToArray());
        values[8] = doc.DuplicateGroupId ?? "";
        values[9] = doc.EmailThreadId ?? "";
        values[ColMd5] = md5;
        values[ColSha256] = sha256;
        values[12] = nativeLink ?? "";
        values[13] = textLink ?? "";
        int offset = StructuralColumns.Count;
        for (int i = 0; i < _fields.Count; i++)
        {
            values[offset + i] = _formatter.Format(i == FieldCatalog.FileSize ? fileSize : doc.Fields[i]);
        }

        int datRow = volume.Summary.DatRows + 1;
        if (DateFields.Any(f => doc.Fields[f] != null) && Selected(DefectType.BadDate))
        {
            Rng rng = Choice(DefectType.BadDate, doc.DocIndex);
            int[] present = [.. DateFields.Where(f => doc.Fields[f] != null)];
            int field = present[rng.NextInt(present.Length)];
            string bad = BadDateValues[rng.NextInt(BadDateValues.Length)];
            string original = values[offset + field];
            values[offset + field] = bad;
            Record(DefectType.BadDate, volume, datCn, w =>
            {
                w.WriteNumber("datRow", datRow);
                w.WriteString("field", _fields[field].Name);
                w.WriteString("value", bad);
                w.WriteString("expected", original);
            });
        }

        int unescapedColumn = -1;
        if (Selected(DefectType.UnescapedQualifier))
        {
            unescapedColumn = offset + (string.IsNullOrEmpty(doc.Fields[FieldCatalog.Subject] as string) ? FieldCatalog.FileName : FieldCatalog.Subject);
            string column = _header[unescapedColumn];
            Record(DefectType.UnescapedQualifier, volume, datCn, w =>
            {
                w.WriteNumber("datRow", datRow);
                w.WriteString("field", column);
            });
        }

        int fieldCount = _header.Length;
        if (Selected(DefectType.FieldCountMismatch))
        {
            fieldCount += Choice(DefectType.FieldCountMismatch, doc.DocIndex).Chance(0.5) ? 1 : -1;
            int observed = fieldCount;
            Record(DefectType.FieldCountMismatch, volume, datCn, w =>
            {
                w.WriteNumber("datRow", datRow);
                w.WriteNumber("expectedFields", _header.Length);
                w.WriteNumber("actualFields", observed);
            });
        }

        BuildRow(values, fieldCount, unescapedColumn);
        LoadFileEncoding rowEncoding = _options.DatEncoding;
        if (_options.DatEncoding != LoadFileEncoding.Utf16Le && HasNonAscii(_row) && Selected(DefectType.DatRowEncoding))
        {
            rowEncoding = _options.DatEncoding == LoadFileEncoding.Windows1252 ? LoadFileEncoding.Utf8 : LoadFileEncoding.Windows1252;
            LoadFileEncoding actual = rowEncoding;
            Record(DefectType.DatRowEncoding, volume, datCn, w =>
            {
                w.WriteNumber("datRow", datRow);
                w.WriteString("declaredEncoding", LoadFileEncodings.Name(_options.DatEncoding));
                w.WriteString("actualEncoding", LoadFileEncodings.Name(actual));
            });
        }

        volume.WriteDat(_row, rowEncoding);
        volume.Summary.DatRows++;
        if (standalone)
        {
            // The control number actually in the DAT, so a later duplicate always collides with a real row.
            _lastStandalone = datCn;
        }

        if (volume.HasOverlay && datCn == cn && _overlaySchedule.Next())
        {
            WriteOverlay(volume, doc, cn);
        }
    }

    private void WriteImages(Volume volume, GeneratedDocument doc, string cn, string datCn)
    {
        if (!_options.IncludeImages || doc.Fields[FieldCatalog.PageCount] is not long pages || pages <= 0)
        {
            return;
        }

        PageImageFormat format = _options.ImageFormat == PageImageFormat.Auto
            ? (doc.Content.FileType is "jpg" or "png" ? PageImageFormat.Jpg : PageImageFormat.Tiff)
            : _options.ImageFormat;
        bool multi = format == PageImageFormat.MultiPageTiff;
        string ext = format switch
        {
            PageImageFormat.Jpg => "jpg",
            PageImageFormat.Png => "png",
            _ => "tif",
        };
        int pageCount = (int)pages;
        int files = multi ? 1 : pageCount;
        string folder = "IMAGES\\" + volume.Images.Allocate(files) + "\\";

        int missingPage = 0;
        if (Selected(DefectType.MissingImage))
        {
            missingPage = multi ? -1 : 1 + Choice(DefectType.MissingImage, doc.DocIndex).NextInt(pageCount);
        }

        int declared = pageCount;
        if (Selected(DefectType.OptPageCountMismatch))
        {
            Rng rng = Choice(DefectType.OptPageCountMismatch, doc.DocIndex);
            int delta = rng.NextInt(1, 3);
            declared = pageCount > delta && rng.Chance(0.5) ? pageCount - delta : pageCount + delta;
            int declaredCount = declared;
            Record(DefectType.OptPageCountMismatch, volume, datCn, w =>
            {
                w.WriteNumber("optLine", volume.Summary.OptRows + 1);
                w.WriteNumber("declaredPages", declaredCount);
                w.WriteNumber("actualPages", pageCount);
            });
        }

        if (multi)
        {
            string path = folder + cn + "." + ext;
            if (missingPage == -1)
            {
                RecordMissingImage(volume, datCn, path, 0);
            }
            else
            {
                var descriptions = new string[pageCount];
                for (int p = 0; p < pageCount; p++)
                {
                    descriptions[p] = PageKey(datCn, p + 1) + " page " + (p + 1).ToString(CultureInfo.InvariantCulture) + " of " + pageCount.ToString(CultureInfo.InvariantCulture);
                }

                WriteBytes(volume, path, PageImages.Tiff(descriptions));
                volume.Summary.ImageFiles++;
            }

            volume.WriteOpt(datCn, path, true, declared);
            volume.Summary.Pages += pageCount;
            return;
        }

        for (int p = 1; p <= pageCount; p++)
        {
            string key = PageKey(datCn, p);
            string path = folder + PageKey(cn, p) + "." + ext;
            if (p == missingPage)
            {
                RecordMissingImage(volume, datCn, path, p);
            }
            else
            {
                string description = key + " page " + p.ToString(CultureInfo.InvariantCulture) + " of " + pageCount.ToString(CultureInfo.InvariantCulture);
                byte[] bytes = format switch
                {
                    PageImageFormat.Jpg => PageImages.Jpeg(description),
                    PageImageFormat.Png => PageImages.Png(description),
                    _ => PageImages.Tiff([description]),
                };
                WriteBytes(volume, path, bytes);
                volume.Summary.ImageFiles++;
            }

            volume.WriteOpt(key, path, p == 1, p == 1 ? declared : null);
        }

        volume.Summary.Pages += pageCount;
    }

    private void RecordMissingImage(Volume volume, string datCn, string path, int page) =>
        Record(DefectType.MissingImage, volume, datCn, w =>
        {
            w.WriteString("path", path);
            w.WriteNumber("page", page);
        });

    /// <summary>Page 1 is keyed by the control number (E08-T05 matches the break row to the document); later pages get a <c>.0002</c> suffix.</summary>
    public static string PageKey(string controlNumber, int page) =>
        page == 1 ? controlNumber : controlNumber + "." + page.ToString("D4", CultureInfo.InvariantCulture);

    private void WriteOverlay(Volume volume, GeneratedDocument doc, string cn)
    {
        Rng rng = Choice(DefectType.OverlayUnknownKey, doc.DocIndex ^ (1L << 62));
        string? previous = doc.Fields[FieldCatalog.Confidentiality] as string;
        string[] candidates = [.. ConfidentialityValues.Where(v => v != previous)];
        string confidentiality = candidates[rng.NextInt(candidates.Length)];
        int row = ++volume.Summary.OverlayRows;
        volume.WriteOverlayRow([cn, confidentiality, ""], _row);
        _overlayJson!.WriteStartObject();
        _overlayJson.WriteString("volume", volume.Summary.Name);
        _overlayJson.WriteString("controlNumber", cn);
        _overlayJson.WriteNumber("overlayRow", row);
        _overlayJson.WriteStartObject("fields");
        _overlayJson.WriteString("Confidentiality", confidentiality);
        _overlayJson.WriteString("ProjectCode", "");
        _overlayJson.WriteEndObject();
        _overlayJson.WriteStartObject("previous");
        WriteNullable(_overlayJson, "Confidentiality", previous);
        WriteNullable(_overlayJson, "ProjectCode", doc.Fields[FieldCatalog.ProjectCode] as string);
        _overlayJson.WriteEndObject();
        _overlayJson.WriteEndObject();
        EndLine(_overlayJson, _overlays!);

        if (Selected(DefectType.OverlayUnknownKey))
        {
            string unknown = cn + "X";
            int unknownRow = ++volume.Summary.OverlayRows;
            volume.WriteOverlayRow([unknown, confidentiality, ""], _row);
            Record(DefectType.OverlayUnknownKey, volume, unknown, w => w.WriteNumber("overlayRow", unknownRow));
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private bool Selected(DefectType type) => _schedules[type].Next();

    private Rng Choice(DefectType type, long docIndex) => Rng.For(_ctx.Seed, StreamTag.VolumeChoice, (ulong)type, (ulong)docIndex);

    private bool TextEncodingEligible(TextSpec text)
    {
        if (text.TotalBytes == 0)
        {
            return false;
        }

        if (_options.TextEncoding == LoadFileEncoding.Utf16Le)
        {
            return true;
        }

        // Body and quoted words are ASCII, so only the header prefix (within the byte budget) can be non-ASCII.
        if (text.Prefix == null)
        {
            return false;
        }

        long budget = text.TotalBytes;
        foreach (Rune rune in text.Prefix.EnumerateRunes())
        {
            budget -= rune.Utf8SequenceLength;
            if (budget < 0)
            {
                return false;
            }

            if (rune.Value >= 0x80)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNonAscii(StringBuilder sb)
    {
        foreach (ReadOnlyMemory<char> chunk in sb.GetChunks())
        {
            foreach (char c in chunk.Span)
            {
                if (c >= 0x80)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Corrupt(string hex) => string.Concat(hex.AsSpan(0, hex.Length - 1), hex[^1] == '0' ? "1" : "0");

    private void BuildRow(string[] values, int fieldCount, int unescapedColumn)
    {
        DelimiterProfile d = _options.Delimiters;
        _row.Clear();
        for (int i = 0; i < fieldCount; i++)
        {
            if (i > 0)
            {
                _row.Append(d.Column);
            }

            int start = _row.Length;
            d.AppendQualified(_row, i < values.Length ? values[i] : "");
            if (i == unescapedColumn)
            {
                int inner = _row.Length - start - 2;
                _row.Insert(start + 1 + (inner / 2), d.Quote);
            }
        }

        _row.Append("\r\n");
    }

    private static void WriteBytes(Volume volume, string relativePath, byte[] bytes)
    {
        using FileStream file = volume.CreateFile(relativePath);
        file.Write(bytes);
    }

    private void Record(DefectType type, Volume volume, string controlNumber, Action<Utf8JsonWriter> details)
    {
        Utf8JsonWriter w = _defectJson;
        w.WriteStartObject();
        w.WriteString("type", DefectNames.Name(type));
        w.WriteString("volume", volume.Summary.Name);
        w.WriteString("controlNumber", controlNumber);
        details(w);
        w.WriteEndObject();
        EndLine(w, _defects);
    }

    private void RecordPath(DefectType type, Volume volume, string controlNumber, string path) =>
        Record(type, volume, controlNumber, w => w.WriteString("path", path));

    private static void EndLine(Utf8JsonWriter w, Stream stream)
    {
        w.Flush();
        stream.WriteByte((byte)'\n');
        w.Reset();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value == null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    // ---- volumes ---------------------------------------------------------------------------------------------

    private void OpenNextVolume()
    {
        CloseVolume();
        string name = _options.VolumePrefix + (_volumes.Count + 1).ToString("D3", CultureInfo.InvariantCulture);
        _current = new Volume(Path.Combine(_root, name), name, _options, _header);
    }

    private void CloseVolume()
    {
        if (_current == null)
        {
            return;
        }

        _outputs.AddRange(_current.Close());
        _volumes.Add(_current.Summary);
        _current.Dispose();
        _current = null;
    }

    private CorpusOutputFile WriteManifest()
    {
        string path = Path.Combine(_root, ManifestFile);
        using (var stream = new HashingStream(File.Create(path)))
        {
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JsonlCorpusWriter.WriterOptions.Encoder }))
            {
                w.WriteStartObject();
                w.WriteString("volumeWriterVersion", Version);
                w.WriteStartObject("generator");
                w.WriteString("name", GeneratorInfo.Name);
                w.WriteString("version", GeneratorInfo.Version);
                w.WriteEndObject();
                w.WriteNumber("seed", _ctx.Seed);
                w.WriteString("profileHash", _ctx.ProfileHash);
                WriteSettings(w);
                w.WriteString("nativeHashRule", NativeHashRule);
                w.WriteStartArray("datColumns");
                foreach (string column in _header)
                {
                    w.WriteStringValue(column);
                }

                w.WriteEndArray();
                w.WriteStartArray("volumes");
                foreach (VolumeSummary v in _volumes)
                {
                    v.WriteTo(w);
                }

                w.WriteEndArray();
                w.WriteStartArray("defects");
                foreach ((DefectType type, DefectSchedule s) in _schedules)
                {
                    w.WriteStartObject();
                    w.WriteString("type", DefectNames.Name(type));
                    w.WriteNumber("targetRate", s.Rate);
                    w.WriteNumber("eligible", s.Eligible);
                    w.WriteNumber("injected", s.Selected);
                    w.WriteNumber("actualRate", s.Eligible == 0 ? 0 : Math.Round((double)s.Selected / s.Eligible, 6));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteStartObject("overlay");
                w.WriteNumber("targetRate", _overlaySchedule.Rate);
                w.WriteNumber("eligible", _overlaySchedule.Eligible);
                w.WriteNumber("documents", _overlaySchedule.Selected);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            stream.WriteByte((byte)'\n');
            stream.Flush();
            return new CorpusOutputFile(ManifestFile, stream.BytesWritten, stream.HashHex());
        }
    }

    private void WriteSettings(Utf8JsonWriter w)
    {
        DelimiterProfile d = _options.Delimiters;
        w.WriteStartObject("settings");
        w.WriteStartObject("delimiters");
        w.WriteString("preset", d.Name);
        w.WriteString("column", Codepoint(d.Column));
        w.WriteString("quote", Codepoint(d.Quote));
        w.WriteString("newline", d.Newline is { } n ? Codepoint(n) : "literal");
        w.WriteString("multiValue", Codepoint(d.MultiValue));
        w.WriteString("nestedValue", Codepoint(d.NestedValue));
        w.WriteString("escape", "doubled quote");
        w.WriteString("rowTerminator", "CRLF");
        w.WriteEndObject();
        w.WriteString("datEncoding", LoadFileEncodings.Name(_options.DatEncoding));
        w.WriteString("textEncoding", LoadFileEncodings.Name(_options.TextEncoding));
        w.WriteString("optEncoding", "ascii");
        w.WriteString("dateFormat", _options.DateFormat.ToString().ToLowerInvariant());
        w.WriteString("timeZoneOffset", ContentFactory.FormatOffset(_options.TimeZoneOffset));
        w.WriteString("booleans", "Y/N");
        w.WriteString("imageFormat", _options.ImageFormat.ToString().ToLowerInvariant());
        w.WriteString("volumePrefix", _options.VolumePrefix);
        w.WriteNumber("documentsPerVolume", _options.DocumentsPerVolume);
        w.WriteNumber("filesPerFolder", _options.FilesPerFolder);
        w.WriteBoolean("natives", _options.IncludeNatives);
        w.WriteBoolean("text", _options.IncludeText);
        w.WriteBoolean("images", _options.IncludeImages);
        w.WriteNumber("nativeTextChars", _options.NativeTextChars);
        w.WriteNumber("overlayRate", _options.OverlayRate);
        w.WriteEndObject();
    }

    private void RenderExcerpt(TextSpec text, EncodingTextSink sink)
    {
        try
        {
            _synthesizer.Write(text, sink);
        }
        catch (ExcerptCompleteException)
        {
            // The excerpt is all that is needed; skip synthesising the rest of a possibly huge text.
        }
    }

    private static string Codepoint(char c) => "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>Streams synthesised text to an encoded file and keeps the first characters as the native excerpt.</summary>
    private sealed class EncodingTextSink(int excerptChars) : ITextSink
    {
        private readonly StringBuilder _excerpt = new();
        private bool _full;

        public Stream? Output { get; set; }

        public LoadFileEncoding Encoding { get; set; }

        public string Excerpt => _excerpt.ToString();

        public void Write(ReadOnlySpan<char> chars)
        {
            int take = _full ? 0 : Math.Min(chars.Length, excerptChars - _excerpt.Length);
            if (take > 0)
            {
                // Keep surrogate pairs whole.
                if (take < chars.Length && char.IsHighSurrogate(chars[take - 1]))
                {
                    take--;
                    _full = true;
                }

                _excerpt.Append(chars[..take]);
            }

            if (Output != null)
            {
                LoadFileEncodings.Write(Encoding, chars, Output);
            }
            else if (_full || _excerpt.Length >= excerptChars)
            {
                throw new ExcerptCompleteException();
            }
        }
    }

    private sealed class ExcerptCompleteException : Exception;
}
