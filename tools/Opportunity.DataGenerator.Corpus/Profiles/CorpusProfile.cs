namespace Opportunity.DataGenerator.Corpus.Profiles;

/// <summary>
/// Versioned generator profile. Every property has a default; the defaults reproduce the architecture-baseline
/// §29 enterprise reference profile plus the eDiscovery practitioner defaults of E17-T01 (decision Q-06).
/// Property meanings are documented in tools/Opportunity.DataGenerator/README.md.
/// </summary>
public sealed class CorpusProfile
{
    public const int CurrentProfileVersion = 1;

    public int ProfileVersion { get; init; } = CurrentProfileVersion;

    public string Name { get; init; } = "enterprise-reference";

    public long DocumentCount { get; init; } = 1_000_000;

    public string ControlNumberPrefix { get; init; } = "OPP";

    public int ControlNumberDigits { get; init; } = 10;

    /// <summary>Generation units per chunk: the unit of parallel work and of in-chunk load-order shuffling.</summary>
    public int UnitsPerChunk { get; init; } = 256;

    public MixProfile Mix { get; init; } = new();

    public FamilyProfile Families { get; init; } = new();

    public DuplicateProfile Duplicates { get; init; } = new();

    public ThreadProfile Threads { get; init; } = new();

    public CustodianProfile Custodians { get; init; } = new();

    public PeopleProfile People { get; init; } = new();

    public RecipientProfile Recipients { get; init; } = new();

    public DateProfile Dates { get; init; } = new();

    public TextProfile Text { get; init; } = new();

    public NearDuplicateProfile NearDuplicates { get; init; } = new();

    public FieldProfile Fields { get; init; } = new();

    public IReadOnlyList<NeedleProfile> Needles { get; init; } =
    [
        new() { Term = "zyqvoltran", DocRate = 0.001, MaxOccurrences = 3 },
        new() { Term = "plimsorbex", DocRate = 0.0005, Scope = NeedleScope.AttachmentsOnly },
        new() { Term = "quandrillex", DocRate = 0.0002, Scope = NeedleScope.ParentsOnly },
        new() { Term = "vex-tomarin", DocRate = 0.0005 },
        new() { Term = "o'bralvix", DocRate = 0.0005 },
    ];

    public IReadOnlyList<ProximityPairProfile> ProximityPairs { get; init; } =
    [
        new() { First = "kelvatron", Second = "brisomund", NearDistance = 3, FarDistance = 12, NearDocRate = 0.0005, FarDocRate = 0.0005 },
    ];
}

public sealed class MixProfile
{
    /// <summary>Share of families (top-level items) whose parent is an email; the rest are loose e-docs.</summary>
    public double EmailShare { get; init; } = 0.60;
}

public sealed class FamilyProfile
{
    /// <summary>Mean family size (parent + all descendants) over all families, standalone documents included.</summary>
    public double MeanFamilySize { get; init; } = 3.0;

    public int MaxAttachmentDepth { get; init; } = 3;

    /// <summary>Probability that a later attachment nests under an earlier attachment instead of the parent.</summary>
    public double NestingProbability { get; init; } = 0.15;

    /// <summary>Mean embedded children of a non-container loose e-doc (geometric).</summary>
    public double EdocMeanEmbeddedChildren { get; init; } = 0.25;

    /// <summary>Share of email families carrying a container attachment (zip) with many members.</summary>
    public double EmailContainerShare { get; init; } = 0.004;

    /// <summary>Share of loose e-docs that are containers (zip/pst-like) with many members.</summary>
    public double EdocContainerShare { get; init; } = 0.006;

    public int ContainerMinChildren { get; init; } = 20;

    public int ContainerMaxChildren { get; init; } = 1000;

    /// <summary>Power-law exponent of container sizes over [min, max].</summary>
    public double ContainerSizeExponent { get; init; } = 2.0;
}

public sealed class DuplicateProfile
{
    /// <summary>Target share of documents that are MD5 duplicates of an earlier document in load order.</summary>
    public double Rate { get; init; } = 0.20;

    /// <summary>Mean number of extra whole-family copies for a family that is duplicated (at least 1).</summary>
    public double CopiesWhenDuplicatedMean { get; init; } = 1.5;

    /// <summary>Share of extra copies held by the same custodian (exact-MD5 same-custodian duplicates).</summary>
    public double SameCustodianShare { get; init; } = 0.15;

    /// <summary>Probability that a family with at least two attachments carries one attachment twice.</summary>
    public double WithinFamilyRate { get; init; } = 0.05;
}

public sealed class ThreadProfile
{
    /// <summary>Share of email units that are a single message rather than a multi-message thread.</summary>
    public double SingleMessageShare { get; init; } = 0.55;

    public int MinLength { get; init; } = 2;

    public int MaxLength { get; init; } = 50;

    public double LengthExponent { get; init; } = 1.3;

    /// <summary>Probability that a reply answers an earlier message rather than the latest one (branching).</summary>
    public double BranchProbability { get; init; } = 0.2;

    public double ForwardProbability { get; init; } = 0.15;

    public double MeanReplyGapHours { get; init; } = 10;

    /// <summary>Upper bound on the share of an email's text taken by quoted prior-message text.</summary>
    public double MaxQuotedShare { get; init; } = 0.5;
}

public sealed class CustodianProfile
{
    public int Count { get; init; } = 250;

    /// <summary>Zipf exponent of documents per custodian.</summary>
    public double ZipfExponent { get; init; } = 1.0;

    /// <summary>Probability that a cross-custodian copy goes to an internal participant of the message.</summary>
    public double CopyToParticipantProbability { get; init; } = 0.7;
}

public sealed class PeopleProfile
{
    /// <summary>Internal people; the first Custodians.Count of them are the custodians.</summary>
    public int InternalCount { get; init; } = 2000;

    public int ExternalCount { get; init; } = 8000;

    public int ExternalDomainCount { get; init; } = 300;

    public string InternalDomain { get; init; } = "corp.example";

    public double AccentedShare { get; init; } = 0.08;

    public double CjkShare { get; init; } = 0.05;

    public double RtlShare { get; init; } = 0.05;
}

public sealed class RecipientProfile
{
    public int MaxRecipients { get; init; } = 500;

    /// <summary>Power-law exponent of To counts over [1, MaxRecipients].</summary>
    public double ToExponent { get; init; } = 1.9;

    public double CcEmptyShare { get; init; } = 0.55;

    public double BccEmptyShare { get; init; } = 0.92;

    public double CopyExponent { get; init; } = 1.7;
}

public sealed class DateProfile
{
    public DateOnly Start { get; init; } = new(2015, 1, 1);

    public DateOnly End { get; init; } = new(2024, 12, 31);

    /// <summary>Relative weight of a weekend day versus a weekday.</summary>
    public double WeekendWeight { get; init; } = 0.15;

    /// <summary>Share of loose e-docs without a creation date.</summary>
    public double MissingEdocDateShare { get; init; } = 0.02;

    /// <summary>Fixed UTC offsets (no tz database, so output is identical on every machine).</summary>
    public IReadOnlyList<TimeZoneWeight> TimeZones { get; init; } =
    [
        new() { Offset = "-05:00", Weight = 0.30 },
        new() { Offset = "-08:00", Weight = 0.15 },
        new() { Offset = "-06:00", Weight = 0.08 },
        new() { Offset = "+00:00", Weight = 0.12 },
        new() { Offset = "+01:00", Weight = 0.12 },
        new() { Offset = "+05:30", Weight = 0.06 },
        new() { Offset = "+08:00", Weight = 0.07 },
        new() { Offset = "+09:00", Weight = 0.05 },
        new() { Offset = "-03:00", Weight = 0.03 },
        new() { Offset = "+10:00", Weight = 0.01 },
        new() { Offset = "+05:45", Weight = 0.01 },
    ];
}

public sealed class TimeZoneWeight
{
    public string Offset { get; init; } = "+00:00";

    public double Weight { get; init; } = 1;
}

public sealed class TextProfile
{
    /// <summary>Median extracted-text size in UTF-8 bytes (log-normal body of the distribution).</summary>
    public long MedianBytes { get; init; } = 5 * 1024;

    /// <summary>99th percentile extracted-text size in UTF-8 bytes; sets the log-normal tail.</summary>
    public long P99Bytes { get; init; } = 1024 * 1024;

    public long MinBytes { get; init; } = 64;

    /// <summary>Hard cap; the log-normal tail beyond it is clamped.</summary>
    public long MaxBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Vocabulary is keyed by this seed, not the corpus seed, so query workloads can rely on it.</summary>
    public long VocabularySeed { get; init; } = 1;

    public int VocabularySize { get; init; } = 50_000;

    public double VocabularyZipfExponent { get; init; } = 1.07;

    /// <summary>Minimum body bytes for a document to be eligible for planted needle terms.</summary>
    public long MinNeedleBodyBytes { get; init; } = 2048;
}

public sealed class NearDuplicateProfile
{
    /// <summary>Share of e-documents whose text is a perturbed variant of a near-duplicate cluster's base text.</summary>
    public double Rate { get; init; } = 0.03;

    public double MeanClusterSize { get; init; } = 4;

    public double WordChangeRate { get; init; } = 0.02;
}

public sealed class FieldProfile
{
    /// <summary>Additional generic custom fields (Custom001..) beyond the standard catalog.</summary>
    public int ExtraCustomFields { get; init; }

    /// <summary>Share of free-text values (subject, title, file name, comments) containing load-file hazards.</summary>
    public double HazardousValueShare { get; init; } = 0.03;

    /// <summary>Share of free-text values containing CJK/accented/RTL words.</summary>
    public double UnicodeValueShare { get; init; } = 0.04;
}

public enum NeedleScope
{
    Any,
    ParentsOnly,
    AttachmentsOnly,
}

public sealed class NeedleProfile
{
    public string Term { get; init; } = "";

    /// <summary>Probability that an eligible document receives the term.</summary>
    public double DocRate { get; init; }

    public int MaxOccurrences { get; init; } = 1;

    public NeedleScope Scope { get; init; } = NeedleScope.Any;
}

public sealed class ProximityPairProfile
{
    public string First { get; init; } = "";

    public string Second { get; init; } = "";

    /// <summary>Word distance used for true cases (e.g. W/5 should match).</summary>
    public int NearDistance { get; init; } = 3;

    /// <summary>Word distance used for false cases (e.g. W/5 should not match).</summary>
    public int FarDistance { get; init; } = 12;

    public double NearDocRate { get; init; }

    public double FarDocRate { get; init; }
}