using System.Text.Json;

using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Profiles;

namespace Opportunity.DataGenerator.Corpus.Output;

/// <summary>Streaming distribution summary written to the manifest. Memory is O(custodians + configured needles).</summary>
public sealed class CorpusStatistics : ICorpusSink
{
    private static readonly (string Label, long Max)[] FamilySizeBuckets =
        [("1", 1), ("2", 2), ("3", 3), ("4-5", 5), ("6-10", 10), ("11-50", 50), ("51-200", 200), ("201+", long.MaxValue)];

    private static readonly (string Label, long Max)[] ThreadBuckets =
        [("1", 1), ("2-3", 3), ("4-10", 10), ("11-25", 25), ("26+", long.MaxValue)];

    private readonly GenerationContext _ctx;
    private readonly long[] _familySizeBuckets = new long[FamilySizeBuckets.Length];
    private readonly long[] _threadBuckets = new long[ThreadBuckets.Length];
    private readonly long[] _depth;
    private readonly long[] _duplicateTypes = new long[4];
    private readonly Dictionary<string, long> _custodianDocs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Docs, long Occurrences, long FamilyExpanded)> _needles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Near, long Far)> _pairs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _needleScratch = new(StringComparer.Ordinal);

    public CorpusStatistics(GenerationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ctx = context;
        _depth = new long[context.Profile.Families.MaxAttachmentDepth + 1];
        foreach (string term in context.Profile.Needles.Select(n => n.Term))
        {
            _needles[term] = default;
        }

        foreach (ProximityPairProfile p in context.Profile.ProximityPairs)
        {
            _pairs[p.First + "~" + p.Second] = default;
        }
    }

    public long Documents { get; private set; }

    public long Families { get; private set; }

    public long EmailFamilies { get; private set; }

    public long FillerFamilies { get; private set; }

    public long EmailDocuments { get; private set; }

    public long Attachments { get; private set; }

    public long DuplicateDocuments { get; private set; }

    public long DuplicateGroups { get; private set; }

    public long FamilyCopies { get; private set; }

    public long FamiliesOver200 { get; private set; }

    public long MaxFamilySize { get; private set; }

    public long Units { get; private set; }

    public long Threads { get; private set; }

    public long ThreadMessages { get; private set; }

    public long MaxThreadLength { get; private set; }

    public long InclusiveEmails { get; private set; }

    public long NoTextDocuments { get; private set; }

    public long TextAtLeast10MiB { get; private set; }

    public long TextAtLeast100MiB { get; private set; }

    public long NearDuplicateDocuments { get; private set; }

    public long HazardousValues { get; private set; }

    public LogHistogram Text { get; } = new();

    public LogHistogram ToRecipients { get; } = new();

    public double MeanFamilySize => Families == 0 ? 0 : (double)Documents / Families;

    public double DuplicateRate => Documents == 0 ? 0 : (double)DuplicateDocuments / Documents;

    public double EmailFamilyShare => Families == 0 ? 0 : (double)EmailFamilies / Families;

    public long DuplicatesOfType(DuplicateType type) => _duplicateTypes[(int)type];

    public IReadOnlyDictionary<string, long> CustodianDocuments => _custodianDocs;

    public void Write(GeneratedChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        Units += chunk.UnitCount;
        foreach (ThreadSummary t in chunk.Threads)
        {
            Bucket(_threadBuckets, ThreadBuckets, t.MessageCount);
            if (t.MessageCount >= 2)
            {
                Threads++;
                ThreadMessages += t.MessageCount;
            }

            MaxThreadLength = Math.Max(MaxThreadLength, t.MessageCount);
        }

        foreach (GeneratedFamily family in chunk.Families)
        {
            Observe(family);
        }
    }

    public IReadOnlyList<CorpusOutputFile> Complete() => [];

    public void Dispose()
    {
    }

    private void Observe(GeneratedFamily family)
    {
        int size = family.Documents.Count;
        Families++;
        MaxFamilySize = Math.Max(MaxFamilySize, size);
        Bucket(_familySizeBuckets, FamilySizeBuckets, size);
        if (size > 200)
        {
            FamiliesOver200++;
        }

        if (family.Parent.Kind == DocumentKind.Email)
        {
            EmailFamilies++;
        }

        if (family.IsFiller)
        {
            FillerFamilies++;
        }

        if (family.CopyIndex > 0)
        {
            FamilyCopies++;
        }

        _custodianDocs[family.Custodian] = _custodianDocs.GetValueOrDefault(family.Custodian) + size;
        _needleScratch.Clear();
        foreach (GeneratedDocument doc in family.Documents)
        {
            Documents++;
            _depth[doc.AttachmentDepth]++;
            if (doc.AttachmentDepth > 0)
            {
                Attachments++;
            }

            if (doc.Kind == DocumentKind.Email)
            {
                EmailDocuments++;
                if (doc.Fields[FieldCatalog.To] is string[] to && doc.FamilySequence == 0)
                {
                    ToRecipients.Add(to.Length);
                }
            }

            if (doc.Fields[FieldCatalog.InclusiveEmail] is true)
            {
                InclusiveEmails++;
            }

            if (doc.DuplicateType != DuplicateType.None)
            {
                DuplicateDocuments++;
                _duplicateTypes[(int)doc.DuplicateType]++;
            }
            else if (doc.DuplicateGroupId != null)
            {
                DuplicateGroups++;
            }

            long text = doc.TextBytes;
            if (text == 0)
            {
                NoTextDocuments++;
            }
            else
            {
                Text.Add(text);
                if (text >= 10L << 20)
                {
                    TextAtLeast10MiB++;
                }

                if (text >= 100L << 20)
                {
                    TextAtLeast100MiB++;
                }
            }

            if (doc.Content.NearDuplicateClusterId != null)
            {
                NearDuplicateDocuments++;
            }

            if (HasHazard(doc.Fields[FieldCatalog.Subject]) || HasHazard(doc.Fields[FieldCatalog.Comments]) || HasHazard(doc.Fields[FieldCatalog.FileName]))
            {
                HazardousValues++;
            }

            foreach (NeedleHit hit in doc.Content.Needles)
            {
                var n = _needles[hit.Term];
                _needles[hit.Term] = (n.Docs + 1, n.Occurrences + hit.Occurrences, n.FamilyExpanded);
                _needleScratch.Add(hit.Term);
            }

            foreach (ProximityHit hit in doc.Content.ProximityHits)
            {
                string key = hit.First + "~" + hit.Second;
                var p = _pairs[key];
                _pairs[key] = hit.IsNear ? (p.Near + 1, p.Far) : (p.Near, p.Far + 1);
            }
        }

        foreach (string term in _needleScratch)
        {
            var n = _needles[term];
            _needles[term] = (n.Docs, n.Occurrences, n.FamilyExpanded + size);
        }
    }

    private static bool HasHazard(object? value) =>
        value is string s && s.AsSpan().IndexOfAny(HazardChars) >= 0;

    private static readonly System.Buffers.SearchValues<char> HazardChars = System.Buffers.SearchValues.Create("þ\u0014®\n|;\"\t^~¶");

    private static void Bucket(long[] counts, (string Label, long Max)[] buckets, long value)
    {
        for (int i = 0; i < buckets.Length; i++)
        {
            if (value <= buckets[i].Max)
            {
                counts[i]++;
                return;
            }
        }
    }

    /// <summary>Writes the "counts", "distribution" and "groundTruth" manifest sections.</summary>
    public void WriteTo(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        Calibration cal = _ctx.Calibration;
        CorpusProfile p = _ctx.Profile;

        w.WriteStartObject("counts");
        w.WriteNumber("documents", Documents);
        w.WriteNumber("families", Families);
        w.WriteNumber("emailFamilies", EmailFamilies);
        w.WriteNumber("edocFamilies", Families - EmailFamilies);
        w.WriteNumber("emailDocuments", EmailDocuments);
        w.WriteNumber("edocDocuments", Documents - EmailDocuments);
        w.WriteNumber("attachments", Attachments);
        w.WriteNumber("units", Units);
        w.WriteNumber("emailThreads", Threads);
        w.WriteNumber("threadMessages", ThreadMessages);
        w.WriteNumber("inclusiveEmails", InclusiveEmails);
        w.WriteNumber("duplicateDocuments", DuplicateDocuments);
        w.WriteNumber("duplicateGroups", DuplicateGroups);
        w.WriteNumber("familyDuplicateCopies", FamilyCopies);
        w.WriteNumber("nearDuplicateDocuments", NearDuplicateDocuments);
        w.WriteNumber("fillerFamilies", FillerFamilies);
        w.WriteNumber("custodiansWithDocuments", _custodianDocs.Count);
        w.WriteEndObject();

        w.WriteStartObject("distribution");
        w.WriteStartObject("families");
        WriteCheck(w, "meanFamilySize", p.Families.MeanFamilySize, MeanFamilySize);
        WriteCheck(w, "emailFamilyShare", p.Mix.EmailShare, EmailFamilyShare);
        w.WriteNumber("emailDocumentShare", Round(Documents == 0 ? 0 : (double)EmailDocuments / Documents));
        w.WriteNumber("attachmentShare", Round(Documents == 0 ? 0 : (double)Attachments / Documents));
        w.WriteNumber("maxFamilySize", MaxFamilySize);
        w.WriteNumber("familiesOver200", FamiliesOver200);
        WriteBuckets(w, "familySizeHistogram", FamilySizeBuckets, _familySizeBuckets);
        w.WriteStartObject("attachmentDepthHistogram");
        for (int d = 0; d < _depth.Length; d++)
        {
            w.WriteNumber(d.ToString(System.Globalization.CultureInfo.InvariantCulture), _depth[d]);
        }

        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteStartObject("duplicates");
        WriteCheck(w, "duplicateRate", p.Duplicates.Rate, DuplicateRate);
        w.WriteStartObject("byType");
        w.WriteNumber("exactMd5", _duplicateTypes[(int)DuplicateType.ExactMd5]);
        w.WriteNumber("crossCustodian", _duplicateTypes[(int)DuplicateType.CrossCustodian]);
        w.WriteNumber("withinFamily", _duplicateTypes[(int)DuplicateType.WithinFamily]);
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteStartObject("text");
        WriteCheck(w, "p50Bytes", cal.TextQuantile(0), Text.Quantile(0.50));
        WriteCheck(w, "p95Bytes", cal.TextQuantile(1.6448536269514722), Text.Quantile(0.95));
        WriteCheck(w, "p99Bytes", cal.TextQuantile(2.3263478740408408), Text.Quantile(0.99));
        w.WriteNumber("p999Bytes", Text.Quantile(0.999));
        w.WriteNumber("maxBytes", Text.Max);
        w.WriteNumber("configuredMaxBytes", p.Text.MaxBytes);
        w.WriteNumber("meanBytes", Round(Text.Mean));
        w.WriteNumber("totalBytes", Text.Sum);
        w.WriteNumber("documentsWithoutText", NoTextDocuments);
        w.WriteNumber("documentsAtLeast10MiB", TextAtLeast10MiB);
        w.WriteNumber("documentsAtLeast100MiB", TextAtLeast100MiB);
        w.WriteEndObject();

        w.WriteStartObject("threads");
        w.WriteNumber("meanMessagesPerThread", Round(Threads == 0 ? 0 : (double)ThreadMessages / Threads));
        w.WriteNumber("maxMessages", MaxThreadLength);
        WriteBuckets(w, "conversationLengthHistogram", ThreadBuckets, _threadBuckets);
        w.WriteEndObject();

        w.WriteStartObject("recipients");
        w.WriteNumber("toP50", ToRecipients.Quantile(0.5));
        w.WriteNumber("toP99", ToRecipients.Quantile(0.99));
        w.WriteNumber("toMax", ToRecipients.Max);
        w.WriteEndObject();

        long[] perCustodian = [.. _custodianDocs.Values.Order().Reverse()];
        w.WriteStartObject("custodians");
        w.WriteNumber("configured", p.Custodians.Count);
        w.WriteNumber("withDocuments", perCustodian.Length);
        w.WriteNumber("maxDocuments", perCustodian.Length == 0 ? 0 : perCustodian[0]);
        w.WriteNumber("minDocuments", perCustodian.Length == 0 ? 0 : perCustodian[^1]);
        w.WriteNumber("topCustodianShare", Round(Documents == 0 ? 0 : (double)perCustodian.Take(1).Sum() / Documents));
        w.WriteNumber("top10CustodianShare", Round(Documents == 0 ? 0 : (double)perCustodian.Take(10).Sum() / Documents));
        w.WriteEndObject();

        w.WriteStartObject("fields");
        w.WriteNumber("catalogSize", _ctx.Catalog.Count);
        w.WriteNumber("documentsWithHazardousValues", HazardousValues);
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteStartObject("groundTruth");
        w.WriteStartArray("needles");
        foreach (NeedleProfile n in p.Needles)
        {
            var s = _needles[n.Term];
            w.WriteStartObject();
            w.WriteString("term", n.Term);
            w.WriteString("scope", n.Scope.ToString());
            w.WriteNumber("docsWithHits", s.Docs);
            w.WriteNumber("occurrences", s.Occurrences);
            w.WriteNumber("familyExpandedDocs", s.FamilyExpanded);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("proximityPairs");
        foreach (ProximityPairProfile pair in p.ProximityPairs)
        {
            var s = _pairs[pair.First + "~" + pair.Second];
            w.WriteStartObject();
            w.WriteString("first", pair.First);
            w.WriteString("second", pair.Second);
            w.WriteNumber("nearDistance", pair.NearDistance);
            w.WriteNumber("farDistance", pair.FarDistance);
            w.WriteNumber("nearDocs", s.Near);
            w.WriteNumber("farDocs", s.Far);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteCheck(Utf8JsonWriter w, string name, double target, double actual)
    {
        w.WriteStartObject(name);
        w.WriteNumber("target", Round(target));
        w.WriteNumber("actual", Round(actual));
        w.WriteNumber("relativeDeviation", Round(target == 0 ? 0 : (actual - target) / target));
        w.WriteEndObject();
    }

    private static void WriteBuckets(Utf8JsonWriter w, string name, (string Label, long Max)[] buckets, long[] counts)
    {
        w.WriteStartObject(name);
        for (int i = 0; i < buckets.Length; i++)
        {
            w.WriteNumber(buckets[i].Label, counts[i]);
        }

        w.WriteEndObject();
    }

    private static double Round(double v) => Math.Round(v, 6, MidpointRounding.ToEven);
}