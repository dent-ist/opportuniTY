using System.Globalization;
using System.Text;

using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.Generation;

/// <summary>Builds per-document content (metadata, hashes, text recipe, planted needles). Stateless; thread-safe.</summary>
internal sealed class ContentFactory
{
    public const string QuoteSeparator = "\n\n-----Original Message-----\n";
    private const int NeedleSlots = 64;

    private static readonly string[] TopLevelTypes = ["pdf", "docx", "xlsx", "pptx", "txt", "csv", "html", "jpg", "png"];
    private static readonly double[] TopLevelWeights = [0.24, 0.22, 0.14, 0.06, 0.05, 0.03, 0.04, 0.12, 0.06];
    private static readonly string[] AttachmentTypes = ["pdf", "docx", "xlsx", "pptx", "txt", "csv", "html", "jpg", "png", "msg"];
    private static readonly double[] AttachmentWeights = [0.22, 0.18, 0.12, 0.05, 0.03, 0.03, 0.02, 0.17, 0.10, 0.08];
    private static readonly string[] Languages = ["en", "de", "fr", "es", "ja", "zh", "ar", "he"];
    private static readonly double[] LanguageWeights = [0.86, 0.03, 0.03, 0.03, 0.02, 0.01, 0.01, 0.01];
    private static readonly string[] Confidentiality = ["Public", "Internal", "Confidential", "Highly Confidential"];
    private static readonly string[] KeywordOptions =
        ["Finance", "Legal", "HR", "Sales", "Operations", "Board", "Compliance", "Vendor", "Audit", "Tax", "Marketing", "Research"];
    private static readonly string[] Hazards = ["þ", "\u0014", "®", "\n", "\r\n", "|", ";", ",", "\"", "\t", "^", "~", "¶"];
    private static readonly string[] FileNameHazards = ["þ", "®", ",", ";", "&", "'", "¶", "~"];
    private static readonly double[] HourWeights =
        [0.2, 0.1, 0.1, 0.1, 0.1, 0.2, 0.5, 1.5, 4, 5, 5, 4.5, 3, 4.5, 5, 4.5, 4, 3, 1.5, 1, 0.8, 0.6, 0.4, 0.3];

    private static readonly AliasTable TopLevelTable = new(TopLevelWeights);
    private static readonly AliasTable AttachmentTable = new(AttachmentWeights);
    private static readonly AliasTable LanguageTable = new(LanguageWeights);
    private static readonly AliasTable HourTable = new(HourWeights);

    private readonly GenerationContext _ctx;
    private readonly CorpusProfile _profile;
    private readonly PowerLawRange _toCount;
    private readonly PowerLawRange _copyCount;

    public ContentFactory(GenerationContext ctx)
    {
        _ctx = ctx;
        _profile = ctx.Profile;
        _toCount = new PowerLawRange(1, _profile.Recipients.MaxRecipients, _profile.Recipients.ToExponent);
        _copyCount = new PowerLawRange(1, _profile.Recipients.MaxRecipients, _profile.Recipients.CopyExponent);
    }

    public static string SampleTopLevelType(Rng rng) => TopLevelTypes[TopLevelTable.Sample(rng)];

    public static string SampleAttachmentType(Rng rng) => AttachmentTypes[AttachmentTable.Sample(rng)];

    public static bool HasText(string fileType) => fileType is not ("jpg" or "png" or "zip");

    public UInt128 ContentKey(long unit, int family, int node) => new(
        StableHash.Of(_ctx.Seed, StreamTag.ContentKey, (ulong)unit, (ulong)family, (ulong)node),
        StableHash.Of(_ctx.Seed, StreamTag.ContentKey, (ulong)unit, (ulong)family, ((ulong)(uint)node) | (1UL << 63)));

    // ---- dates ---------------------------------------------------------------------------------------------

    public DateTimeOffset SampleDate(Rng rng, TimeSpan offset)
    {
        DateProfile d = _profile.Dates;
        int days = d.End.DayNumber - d.Start.DayNumber + 1;
        DateOnly day = d.Start;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            day = DateOnly.FromDayNumber(d.Start.DayNumber + rng.NextInt(days));
            bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (!weekend || rng.Chance(d.WeekendWeight))
            {
                break;
            }
        }

        var time = new TimeOnly(HourTable.Sample(rng), rng.NextInt(60), rng.NextInt(60));
        return new DateTimeOffset(day.ToDateTime(time), offset);
    }

    public static string FormatOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    // ---- free-text values -----------------------------------------------------------------------------------

    public string Phrase(Rng rng, int minWords, int maxWords, bool capitalize = true)
    {
        int count = rng.NextInt(minWords, maxWords);
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            string w = _ctx.Vocabulary.Sample(rng);
            sb.Append(capitalize && i == 0 ? Syllables.Capitalize(w) : w);
        }

        return sb.ToString();
    }

    /// <summary>Applies the configured share of Unicode words and load-file hazards (delimiters, ®, newlines).</summary>
    public string Decorate(Rng rng, string value, bool fileName = false)
    {
        if (rng.Chance(_profile.Fields.UnicodeValueShare))
        {
            value = value + " " + Syllables.UnicodeWord(rng);
        }

        if (rng.Chance(_profile.Fields.HazardousValueShare))
        {
            string hazard = fileName ? rng.Pick(FileNameHazards) : rng.Pick(Hazards);
            int at = rng.NextInt(value.Length + 1);
            value = string.Concat(value.AsSpan(0, at), hazard, value.AsSpan(at));
        }

        return value;
    }

    // ---- shared optional fields -----------------------------------------------------------------------------

    public void FillCommonFields(Rng rng, object?[] fields, string fileType, long textBytes, bool isEmail)
    {
        fields[FieldCatalog.FileExtension] = fileType;
        fields[FieldCatalog.FileSize] = FileSize(rng, fileType, textBytes);
        fields[FieldCatalog.PageCount] = PageCount(rng, fileType, textBytes, isEmail);
        fields[FieldCatalog.Language] = HasText(fileType) ? Languages[LanguageTable.Sample(rng)] : null;
        if (rng.Chance(0.6))
        {
            fields[FieldCatalog.Confidentiality] = rng.Pick(Confidentiality);
        }

        if (rng.Chance(0.35))
        {
            int n = rng.NextInt(1, 4);
            var set = new SortedSet<string>(StringComparer.Ordinal);
            while (set.Count < n)
            {
                set.Add(rng.Pick(KeywordOptions));
            }

            fields[FieldCatalog.Keywords] = set.ToArray();
        }

        if (rng.Chance(0.3))
        {
            string comment = Phrase(rng, 4, 20) + ".";
            if (rng.Chance(0.3))
            {
                comment += "\n" + Phrase(rng, 3, 12) + ".";
            }

            fields[FieldCatalog.Comments] = Decorate(rng, comment);
        }

        if (rng.Chance(0.5))
        {
            fields[FieldCatalog.ProjectCode] = string.Create(CultureInfo.InvariantCulture, $"PRJ-{1 + (int)Math.Min(999, rng.Exponential(40)):D3}");
        }

        if (fileType == "xlsx" ? rng.Chance(0.6) : rng.Chance(0.05))
        {
            long cents = (long)Math.Min(1e13, rng.LogNormal(Math.Log(250_000), 2.0));
            fields[FieldCatalog.Amount] = new decimal(cents) / 100m;
        }

        if (rng.Chance(0.25))
        {
            fields[FieldCatalog.RecordDate] = DateOnly.FromDateTime(SampleDate(rng, TimeSpan.Zero).DateTime);
        }

        FillExtraFields(rng, fields);
    }

    private void FillExtraFields(Rng rng, object?[] fields)
    {
        IReadOnlyList<FieldDefinition> all = _ctx.Catalog.Fields;
        for (int i = FieldCatalog.StandardCount; i < all.Count; i++)
        {
            if (!rng.Chance(0.5))
            {
                continue;
            }

            fields[i] = all[i].Type switch
            {
                FieldType.Text => Decorate(rng, Phrase(rng, 1, 5)),
                FieldType.WholeNumber => (long)rng.Geometric(500),
                FieldType.Date => DateOnly.FromDateTime(SampleDate(rng, TimeSpan.Zero).DateTime),
                FieldType.SingleChoice => "Option " + (char)('A' + rng.NextInt(8)),
                FieldType.Boolean => rng.Chance(0.3),
                FieldType.Currency => new decimal(rng.NextInt64(10_000_000)) / 100m,
                FieldType.MultiChoice => new[] { "Choice " + (char)('A' + rng.NextInt(4)), "Choice " + (char)('E' + rng.NextInt(4)) },
                _ => SampleDate(rng, TimeSpan.Zero),
            };
        }
    }

    private static long FileSize(Rng rng, string fileType, long text) => fileType switch
    {
        "pdf" => (text * 5 / 2) + 20_000 + rng.NextInt(20_000),
        "docx" => (text * 3 / 5) + 15_000 + rng.NextInt(10_000),
        "xlsx" => (text / 2) + 8_000 + rng.NextInt(10_000),
        "pptx" => (text * 3 / 2) + 60_000 + rng.NextInt(200_000),
        "jpg" or "png" => (long)Math.Min(50_000_000, rng.LogNormal(Math.Log(250_000), 1.0)),
        "zip" => (long)Math.Min(4_000_000_000, rng.LogNormal(Math.Log(2_000_000), 1.2)),
        "msg" => (text * 13 / 10) + 4_000 + rng.NextInt(4_000),
        _ => text + rng.NextInt(64),
    };

    private static long? PageCount(Rng rng, string fileType, long text, bool isEmail)
    {
        if (fileType == "zip")
        {
            return null;
        }

        if (fileType is "jpg" or "png")
        {
            return 1;
        }

        double perPage = isEmail ? 4000 : 3000;
        double pages = Math.Ceiling(text / perPage * rng.LogNormal(0, 0.25));
        return (long)Math.Clamp(pages, 1, 2000);
    }

    // ---- text -----------------------------------------------------------------------------------------------

    public long SampleTextBytes(Rng rng, string fileType)
    {
        if (!HasText(fileType))
        {
            return 0;
        }

        TextProfile t = _profile.Text;
        double v = rng.LogNormal(_ctx.Calibration.TextMu, _ctx.Calibration.TextSigma);
        return (long)Math.Clamp(Math.Round(v), t.MinBytes, t.MaxBytes);
    }

    /// <summary>Builds the text recipe and plants needle terms in the document's own body.</summary>
    public (TextSpec Text, string? NearDuplicateCluster, IReadOnlyList<NeedleHit> Needles, IReadOnlyList<ProximityHit> Proximity) BuildText(
        Rng rng,
        UInt128 key,
        long totalBytes,
        string? prefix,
        ulong quotedSeed,
        long quotedSourceBytes,
        bool isEdoc,
        int depth)
    {
        if (totalBytes == 0)
        {
            return (TextSpec.Empty, null, [], []);
        }

        long available = totalBytes;
        if (prefix != null)
        {
            available -= Encoding.UTF8.GetByteCount(prefix);
        }

        long quoted = 0;
        long sep = Encoding.UTF8.GetByteCount(QuoteSeparator);
        if (quotedSourceBytes > 0 && available > sep + 64)
        {
            quoted = Math.Min(quotedSourceBytes, (long)((available - sep) * _profile.Threads.MaxQuotedShare));
        }

        long body = Math.Max(0, available - (quoted > 0 ? sep + quoted : 0));
        ulong bodySeed = StableHash.Of(_ctx.Seed, StreamTag.TextBody, (ulong)key, (ulong)(key >> 64));
        ulong variantSeed = 0;
        string? cluster = null;
        if (isEdoc && rng.Chance(_profile.NearDuplicates.Rate))
        {
            long clusterIndex = rng.NextInt64(_ctx.NearDuplicateClusterCount);
            bodySeed = StableHash.Of(_ctx.Seed, StreamTag.NearDuplicateCluster, (ulong)clusterIndex);
            variantSeed = StableHash.Of(_ctx.Seed, StreamTag.TextVariant, (ulong)key, (ulong)(key >> 64)) | 1;
            cluster = string.Create(CultureInfo.InvariantCulture, $"ND-{clusterIndex:X8}");
        }

        var planted = new List<PlantedWord>();
        var needles = new List<NeedleHit>();
        var proximity = new List<ProximityHit>();
        if (body >= _profile.Text.MinNeedleBodyBytes)
        {
            PlantNeedles(key, depth, planted, needles, proximity);
        }

        var spec = new TextSpec
        {
            TotalBytes = totalBytes,
            Prefix = prefix,
            BodySeed = bodySeed,
            BodyBytes = body,
            VariantSeed = variantSeed,
            WordChangeRate = variantSeed == 0 ? 0 : _profile.NearDuplicates.WordChangeRate,
            QuotedSeed = quoted > 0 ? quotedSeed : 0,
            QuotedBytes = quoted,
            Planted = planted,
        };
        return (spec, cluster, needles, proximity);
    }

    private void PlantNeedles(UInt128 key, int depth, List<PlantedWord> planted, List<NeedleHit> needles, List<ProximityHit> proximity)
    {
        var rng = Rng.For(_ctx.Seed, StreamTag.Needles, (ulong)key, (ulong)(key >> 64));
        Span<bool> used = stackalloc bool[NeedleSlots];
        foreach (NeedleProfile n in _profile.Needles)
        {
            bool inScope = n.Scope switch
            {
                NeedleScope.ParentsOnly => depth == 0,
                NeedleScope.AttachmentsOnly => depth > 0,
                _ => true,
            };
            if (!inScope || !rng.Chance(n.DocRate))
            {
                continue;
            }

            int wanted = rng.NextInt(1, n.MaxOccurrences);
            int placed = 0;
            for (int attempt = 0; attempt < 64 && placed < wanted; attempt++)
            {
                int pos = rng.NextInt(NeedleSlots);
                if (!used[pos])
                {
                    used[pos] = true;
                    planted.Add(new PlantedWord(pos, n.Term));
                    placed++;
                }
            }

            if (placed > 0)
            {
                needles.Add(new NeedleHit(n.Term, placed));
            }
        }

        foreach (ProximityPairProfile p in _profile.ProximityPairs)
        {
            double u = rng.NextDouble();
            bool near = u < p.NearDocRate;
            if (!near && u >= p.NearDocRate + p.FarDocRate)
            {
                continue;
            }

            int distance = near ? p.NearDistance : p.FarDistance;
            for (int attempt = 0; attempt < 64; attempt++)
            {
                int a = rng.NextInt(NeedleSlots - distance);
                int b = a + distance;
                if (!used[a] && !used[b])
                {
                    used[a] = true;
                    used[b] = true;
                    planted.Add(new PlantedWord(a, p.First));
                    planted.Add(new PlantedWord(b, p.Second));
                    proximity.Add(new ProximityHit(p.First, p.Second, distance, near));
                    break;
                }
            }
        }

        planted.Sort(static (x, y) => x.Position.CompareTo(y.Position));
    }

    // ---- hashes ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Synthetic identity hashes of the simulated native file: stable per content and shared by every copy.
    /// They are not hashes of any bytes written by the JSONL writer.
    /// </summary>
    public static (string Md5, string Sha256) Hashes(UInt128 key)
    {
        ulong lo = (ulong)key;
        ulong hi = (ulong)(key >> 64);
        Span<byte> md5 = stackalloc byte[16];
        Span<byte> sha = stackalloc byte[32];
        for (int i = 0; i < 2; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(md5[(i * 8)..], StableHash.Combine(lo, StableHash.Combine(hi, (ulong)StreamTag.Md5 + (ulong)i)));
        }

        for (int i = 0; i < 4; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(sha[(i * 8)..], StableHash.Combine(lo, StableHash.Combine(hi, ((ulong)StreamTag.Sha256 << 8) + (ulong)i)));
        }

        return (Convert.ToHexStringLower(md5), Convert.ToHexStringLower(sha));
    }

    // ---- recipients -----------------------------------------------------------------------------------------

    public string[] Recipients(Rng rng, int count, IReadOnlyList<int> preferred, int exclude)
    {
        var ids = new List<int>(count);
        var seen = new HashSet<int> { exclude };
        foreach (int id in preferred)
        {
            if (ids.Count < count && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        int guard = 0;
        while (ids.Count < count && guard++ < count * 4)
        {
            int id = rng.Chance(0.6) ? _ctx.People.SampleInternal(rng) : _ctx.People.SampleExternal(rng);
            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return [.. ids.Select(id => _ctx.People[id].Formatted)];
    }

    public int SampleToCount(Rng rng) => _toCount.Sample(rng);

    public int SampleCopyCount(Rng rng, double emptyShare) => rng.Chance(emptyShare) ? 0 : _copyCount.Sample(rng);
}