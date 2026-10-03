using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Opportunity.DataGenerator.Corpus.Profiles;

/// <summary>Reads, writes, validates and hashes <see cref="CorpusProfile"/> JSON files.</summary>
public static class ProfileSerializer
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions WriteOptions = new(ReadOptions) { WriteIndented = true };

    private static readonly JsonSerializerOptions CanonicalOptions = new(ReadOptions) { WriteIndented = false };

    public static CorpusProfile Parse(string json)
    {
        CorpusProfile profile = JsonSerializer.Deserialize<CorpusProfile>(json, ReadOptions)
            ?? throw new ProfileValidationException(["Profile document is empty."]);
        Validate(profile);
        return profile;
    }

    public static CorpusProfile Load(string path) => Parse(File.ReadAllText(path));

    public static string ToJson(CorpusProfile profile) => JsonSerializer.Serialize(profile, WriteOptions);

    /// <summary>Canonical (defaults filled, whitespace and comments removed) serialization used for hashing.</summary>
    public static string ToCanonicalJson(CorpusProfile profile) => JsonSerializer.Serialize(profile, CanonicalOptions);

    /// <summary>SHA-256 of the canonical JSON: two files that resolve to the same effective profile hash equally.</summary>
    public static string ComputeHash(CorpusProfile profile)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ToCanonicalJson(profile)));
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    public static CorpusProfile WithDocumentCount(CorpusProfile profile, long documentCount)
    {
        string json = ToCanonicalJson(profile);
        using JsonDocument doc = JsonDocument.Parse(json);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (property.NameEquals("documentCount"))
                {
                    writer.WriteNumber("documentCount", documentCount);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Parse(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static void Validate(CorpusProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var errors = new List<string>();

        void Check(bool ok, string message)
        {
            if (!ok)
            {
                errors.Add(message);
            }
        }

        static bool Probability(double v) => v >= 0 && v <= 1;

        Check(p.ProfileVersion == CorpusProfile.CurrentProfileVersion, $"profileVersion must be {CorpusProfile.CurrentProfileVersion}.");
        Check(p.DocumentCount >= 0, "documentCount must be >= 0.");
        Check(p.ControlNumberDigits is >= 1 and <= 18, "controlNumberDigits must be 1..18.");
        Check(p.ControlNumberDigits >= 18 || p.DocumentCount < Pow10(p.ControlNumberDigits), "controlNumberDigits too small for documentCount.");
        Check(p.UnitsPerChunk is >= 1 and <= 1_000_000, "unitsPerChunk must be 1..1000000.");
        Check(Probability(p.Mix.EmailShare), "mix.emailShare must be 0..1.");

        FamilyProfile f = p.Families;
        Check(f.MeanFamilySize >= 1, "families.meanFamilySize must be >= 1.");
        Check(f.MaxAttachmentDepth is >= 1 and <= 10, "families.maxAttachmentDepth must be 1..10.");
        Check(Probability(f.NestingProbability), "families.nestingProbability must be 0..1.");
        Check(f.EdocMeanEmbeddedChildren >= 0, "families.edocMeanEmbeddedChildren must be >= 0.");
        Check(Probability(f.EmailContainerShare) && f.EmailContainerShare < 1, "families.emailContainerShare must be 0..<1.");
        Check(Probability(f.EdocContainerShare), "families.edocContainerShare must be 0..1.");
        Check(f.ContainerMinChildren >= 2 && f.ContainerMaxChildren >= f.ContainerMinChildren && f.ContainerMaxChildren <= 100_000,
            "families.containerMinChildren/containerMaxChildren must satisfy 2 <= min <= max <= 100000.");
        Check(f.ContainerSizeExponent > 0, "families.containerSizeExponent must be > 0.");

        DuplicateProfile d = p.Duplicates;
        Check(d.Rate is >= 0 and < 0.95, "duplicates.rate must be 0..0.95.");
        Check(d.CopiesWhenDuplicatedMean >= 1, "duplicates.copiesWhenDuplicatedMean must be >= 1.");
        Check(Probability(d.SameCustodianShare), "duplicates.sameCustodianShare must be 0..1.");
        Check(Probability(d.WithinFamilyRate), "duplicates.withinFamilyRate must be 0..1.");

        ThreadProfile t = p.Threads;
        Check(Probability(t.SingleMessageShare), "threads.singleMessageShare must be 0..1.");
        Check(t.MinLength >= 2 && t.MaxLength >= t.MinLength && t.MaxLength <= 10_000, "threads.minLength/maxLength must satisfy 2 <= min <= max <= 10000.");
        Check(t.LengthExponent > 0, "threads.lengthExponent must be > 0.");
        Check(Probability(t.BranchProbability) && Probability(t.ForwardProbability), "threads branch/forward probabilities must be 0..1.");
        Check(t.MeanReplyGapHours > 0, "threads.meanReplyGapHours must be > 0.");
        Check(t.MaxQuotedShare is >= 0 and <= 0.9, "threads.maxQuotedShare must be 0..0.9.");

        Check(p.Custodians.Count >= 1, "custodians.count must be >= 1.");
        Check(p.Custodians.ZipfExponent >= 0, "custodians.zipfExponent must be >= 0.");
        Check(Probability(p.Custodians.CopyToParticipantProbability), "custodians.copyToParticipantProbability must be 0..1.");

        PeopleProfile people = p.People;
        Check(people.InternalCount >= p.Custodians.Count, "people.internalCount must be >= custodians.count.");
        Check(people.ExternalCount >= 1 && people.ExternalDomainCount >= 1, "people.externalCount and externalDomainCount must be >= 1.");
        Check(people.InternalCount + (long)people.ExternalCount <= 5_000_000, "people.internalCount + externalCount must be <= 5000000.");
        Check(!string.IsNullOrWhiteSpace(people.InternalDomain), "people.internalDomain is required.");
        Check(people.AccentedShare + people.CjkShare + people.RtlShare <= 1 && people.AccentedShare >= 0 && people.CjkShare >= 0 && people.RtlShare >= 0,
            "people script shares must be >= 0 and sum to <= 1.");

        RecipientProfile r = p.Recipients;
        Check(r.MaxRecipients is >= 1 and <= 100_000, "recipients.maxRecipients must be 1..100000.");
        Check(r.ToExponent > 0 && r.CopyExponent > 0, "recipients exponents must be > 0.");
        Check(Probability(r.CcEmptyShare) && Probability(r.BccEmptyShare), "recipients empty shares must be 0..1.");

        DateProfile dates = p.Dates;
        Check(dates.End >= dates.Start, "dates.end must be >= dates.start.");
        Check(dates.WeekendWeight is >= 0 and <= 1, "dates.weekendWeight must be 0..1.");
        Check(Probability(dates.MissingEdocDateShare), "dates.missingEdocDateShare must be 0..1.");
        Check(dates.TimeZones.Count > 0 && dates.TimeZones.All(z => z.Weight >= 0) && dates.TimeZones.Sum(z => z.Weight) > 0,
            "dates.timeZones must be non-empty with non-negative weights.");
        foreach (TimeZoneWeight zone in dates.TimeZones)
        {
            Check(TryParseOffset(zone.Offset, out _), $"dates.timeZones offset '{zone.Offset}' must look like +HH:MM or -HH:MM.");
        }

        TextProfile text = p.Text;
        Check(text.MinBytes >= 1 && text.MedianBytes >= text.MinBytes && text.P99Bytes > text.MedianBytes && text.MaxBytes >= text.P99Bytes,
            "text sizes must satisfy 1 <= minBytes <= medianBytes < p99Bytes <= maxBytes.");
        Check(text.MaxBytes <= 2L * 1024 * 1024 * 1024, "text.maxBytes must be <= 2 GiB.");
        Check(text.VocabularySize is >= 1000 and <= 1_000_000, "text.vocabularySize must be 1000..1000000.");
        Check(text.VocabularyZipfExponent > 0, "text.vocabularyZipfExponent must be > 0.");
        Check(text.MinNeedleBodyBytes >= 2048, "text.minNeedleBodyBytes must be >= 2048 (needles are planted in the first 64 words).");

        Check(Probability(p.NearDuplicates.Rate), "nearDuplicates.rate must be 0..1.");
        Check(p.NearDuplicates.MeanClusterSize >= 2, "nearDuplicates.meanClusterSize must be >= 2.");
        Check(Probability(p.NearDuplicates.WordChangeRate), "nearDuplicates.wordChangeRate must be 0..1.");

        Check(p.Fields.ExtraCustomFields is >= 0 and <= 5000, "fields.extraCustomFields must be 0..5000.");
        Check(Probability(p.Fields.HazardousValueShare) && Probability(p.Fields.UnicodeValueShare), "fields value shares must be 0..1.");

        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (NeedleProfile n in p.Needles)
        {
            Check(IsNeedleTerm(n.Term), $"needle term '{n.Term}' must be 4..24 chars of lowercase a-z, '-' or '''.");
            Check(terms.Add(n.Term), $"needle term '{n.Term}' is duplicated.");
            Check(Probability(n.DocRate), $"needle '{n.Term}' docRate must be 0..1.");
            Check(n.MaxOccurrences is >= 1 and <= 16, $"needle '{n.Term}' maxOccurrences must be 1..16.");
        }

        foreach (ProximityPairProfile pair in p.ProximityPairs)
        {
            Check(IsNeedleTerm(pair.First) && IsNeedleTerm(pair.Second), $"proximity pair '{pair.First}'/'{pair.Second}' terms are invalid.");
            Check(terms.Add(pair.First) && terms.Add(pair.Second), $"proximity pair '{pair.First}'/'{pair.Second}' reuses a needle term.");
            Check(pair.NearDistance >= 1 && pair.FarDistance > pair.NearDistance && pair.FarDistance <= 32,
                $"proximity pair '{pair.First}'/'{pair.Second}' needs 1 <= nearDistance < farDistance <= 32.");
            Check(Probability(pair.NearDocRate) && Probability(pair.FarDocRate) && pair.NearDocRate + pair.FarDocRate <= 1,
                $"proximity pair '{pair.First}'/'{pair.Second}' rates must be 0..1 and sum to <= 1.");
        }

        Check(p.Needles.Sum(n => (double)n.MaxOccurrences) + (2.0 * p.ProximityPairs.Count) <= 48, "too many needle slots: total occurrences must fit in 48 word positions.");

        if (errors.Count > 0)
        {
            throw new ProfileValidationException(errors);
        }
    }

    public static bool TryParseOffset(string value, out TimeSpan offset)
    {
        offset = default;
        if (value is not { Length: 6 } || (value[0] != '+' && value[0] != '-') || value[3] != ':'
            || !int.TryParse(value.AsSpan(1, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(value.AsSpan(4, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int minutes)
            || hours > 14 || minutes > 59)
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, 0);
        if (value[0] == '-')
        {
            offset = -offset;
        }

        return true;
    }

    private static bool IsNeedleTerm(string term) =>
        term is { Length: >= 4 and <= 24 } && term.All(c => c is (>= 'a' and <= 'z') or '-' or '\'') && char.IsAsciiLetterLower(term[0]);

    private static long Pow10(int digits)
    {
        long value = 1;
        for (int i = 0; i < digits; i++)
        {
            value *= 10;
        }

        return value;
    }
}

public sealed class ProfileValidationException(IReadOnlyList<string> errors)
    : Exception("Invalid corpus profile: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
