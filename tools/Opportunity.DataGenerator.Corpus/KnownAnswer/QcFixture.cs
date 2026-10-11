using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.KnownAnswer;

/// <summary>Privilege Status coding (built-in choices).</summary>
public enum QcPrivilege
{
    Uncoded,
    NotPrivileged,
    Withhold,
    Redact,
}

/// <summary>Confidentiality Designation coding: none, the "None" choice (no legend) or CONFIDENTIAL.</summary>
public enum QcDesignation
{
    Blank,
    None,
    Confidential,
}

/// <summary>
/// A document of the production-QC fixture and the state to seed for it.
/// </summary>
/// <param name="Pages">Pages of its active page set (always at least one).</param>
/// <param name="ImagedPages">Pages that get a stored page image (the rest are render failures when produced as images).</param>
/// <param name="Native">Whether a committed native is stored.</param>
/// <param name="Text">Whether committed extracted text is stored.</param>
/// <param name="Redactions">Redactions in the workspace's Default Redaction Set (on page 1).</param>
/// <param name="Produced">Whether the document is in the production's frozen set.</param>
/// <param name="Scenario">The planted case this document carries, or <c>random</c>.</param>
public sealed record QcDocument(
    string ControlNumber,
    string FamilyControlNumber,
    string? ParentControlNumber,
    int FamilySequence,
    string Extension,
    int Pages,
    int ImagedPages,
    bool Native,
    bool Text,
    QcPrivilege Privilege,
    int Redactions,
    QcDesignation Designation,
    bool Produced,
    string Scenario)
{
    /// <summary>Spreadsheets are produced natively by the default file type rules; everything else as images.</summary>
    public bool ProducedNatively => QcFixtureGenerator.NativeExtensions.Contains(Extension);
}

/// <summary>
/// The production the fixture expects: Bates prefix, withheld documents stay members (Block), text is delivered and an
/// endorsement stamps the confidentiality designation, so designations are producible.
/// </summary>
public sealed record QcProductionSettings(string BatesPrefix, string WithheldDocuments, bool IncludeText, bool StampsConfidentiality);

public sealed record QcFixture(string Generator, string Version, ulong Seed, QcFixtureOptions Options, QcProductionSettings Production, IReadOnlyList<QcDocument> Documents);

/// <summary>An expected exception: the member's control number and the detail the check records.</summary>
public sealed record QcExpectedRow(string ControlNumber, string? Detail);

/// <summary>One QC check's expected result.</summary>
/// <param name="Check">The check's camelCase name (as in the API and the QC report).</param>
/// <param name="Severity"><c>blocking</c> or <c>warning</c>.</param>
/// <param name="Status"><c>passed</c>, <c>failed</c> (blocking) or <c>warning</c>.</param>
public sealed record QcExpectedCheck(string Check, int Code, string Severity, bool Overridable, string Status, long Documents, IReadOnlyList<QcExpectedRow> Exceptions);

/// <summary>The expected result of running the QC gate over the fixture's production.</summary>
public sealed record QcExpected(
    string Generator,
    string Version,
    ulong Seed,
    string Outcome,
    long Members,
    int BlockingFailures,
    int Warnings,
    long BlockingExceptions,
    long WarningExceptions,
    IReadOnlyList<QcExpectedCheck> Checks);

public sealed record QcFixtureOptions
{
    /// <summary>Random families (parent + 0–3 attachments) added to the planted cases.</summary>
    public int RandomFamilies { get; init; } = 4;

    /// <summary>Random standalone documents added to the planted cases.</summary>
    public int RandomStandalone { get; init; } = 4;

    public string ControlNumberPrefix { get; init; } = "KAQ";

    public void Validate()
    {
        if (RandomFamilies is < 0 or > 1_000 || RandomStandalone is < 0 or > 1_000)
        {
            throw new ArgumentException("The QC fixture takes 0–1,000 random families and standalone documents.");
        }

        if (string.IsNullOrWhiteSpace(ControlNumberPrefix) || !ControlNumberPrefix.All(char.IsAsciiLetterOrDigit) || ControlNumberPrefix.Length > 20)
        {
            throw new ArgumentException("The control number prefix must be 1–20 ASCII letters and digits.");
        }
    }
}

/// <summary>
/// E17-T09: a seeded production whose QC gate result (E12-T07) is known in advance. Planted cases: a family with a
/// withheld attachment (withheld member, privilege conflict for the whole family), a fully withheld family (no
/// conflict), a withheld standalone, Redact with and without redactions, a redacted spreadsheet (produced natively), a
/// spreadsheet with and without its native, render failures (some and all pages without images), missing text, a
/// family member left out of the production, and confidentiality designations under the highest-in-family rule; plus
/// random documents. <see cref="Expected"/> applies the gate's rules to the seeded state.
/// </summary>
public static class QcFixtureGenerator
{
    public const string Version = "1.0.0";

    internal static readonly HashSet<string> NativeExtensions = new(["csv", "xls", "xlsb", "xlsm", "xlsx"], StringComparer.Ordinal);

    private static readonly string[] ImageExtensions = ["pdf", "docx", "msg", "pptx"];

    /// <summary>The gate's checks: (code, name, blocking, overridable), in code order.</summary>
    public static IReadOnlyList<(int Code, string Name, bool Blocking, bool Overridable)> Checks { get; } =
    [
        (1, "withheldWithoutPlaceholder", true, false),
        (2, "placeholderNoLongerWithheld", true, false),
        (3, "redactWithoutRedactions", true, true),
        (4, "redactedNative", true, false),
        (5, "redactionsNotProducible", true, false),
        (6, "renderFailure", true, true),
        (7, "pageCountChanged", true, false),
        (8, "batesOverlap", true, false),
        (9, "privilegeConflicts", true, true),
        (10, "designationNotProducible", true, false),
        (11, "inaccessible", true, false),
        (12, "incompleteFamily", false, false),
        (13, "textMissing", false, false),
        (14, "blankConfidentiality", false, false),
    ];

    /// <summary>The planted scenarios; each is present in every fixture.</summary>
    public static IReadOnlyList<string> Scenarios { get; } =
    [
        "withheldFamilyMember", "withheldFamily", "withheldStandalone", "redactWithRedactions", "redactWithoutRedactions",
        "redactedSpreadsheet", "spreadsheetNative", "spreadsheetNativeMissing", "renderPartial", "renderNone", "textMissing",
        "excludedFamilyMember",
    ];

    public static QcFixture Generate(ulong seed, QcFixtureOptions? options = null)
    {
        options ??= new QcFixtureOptions();
        options.Validate();
        var rng = Rng.For(seed, StreamTag.KnownAnswerQc);
        var units = new List<List<QcDocument>>();
        foreach (var scenario in Scenarios)
        {
            units.Add(Scenario(scenario, rng));
        }

        for (var f = 0; f < options.RandomFamilies; f++)
        {
            units.Add([.. Enumerable.Range(0, 1 + rng.NextInt(0, 3)).Select(_ => Random(rng))]);
        }

        for (var s = 0; s < options.RandomStandalone; s++)
        {
            units.Add([Random(rng)]);
        }

        rng.Shuffle(units);
        var documents = new List<QcDocument>();
        foreach (var unit in units)
        {
            var parent = ControlNumber(options, documents.Count + 1);
            for (var i = 0; i < unit.Count; i++)
            {
                documents.Add(unit[i] with
                {
                    ControlNumber = ControlNumber(options, documents.Count + 1),
                    FamilyControlNumber = parent,
                    ParentControlNumber = i == 0 ? null : parent,
                    FamilySequence = i,
                });
            }
        }

        var production = new QcProductionSettings(options.ControlNumberPrefix + "PROD", "block", IncludeText: true, StampsConfidentiality: true);
        return new QcFixture(GeneratorInfo.Name, Version, seed, options, production, documents);
    }

    /// <summary>
    /// The gate's rules applied to the seeded state (members in control-number order, which is the frozen set's order):
    /// withheld members (1); Redact without redactions (3); redacted members produced natively (4); members produced as
    /// images with pages lacking an image, or natively without a native (6); families mixing withheld and other live
    /// documents (9, every member of such a family); families with a document left out (12); members without text that
    /// would ship their own (13); and families without any designation among their members (14, highest-in-family).
    /// </summary>
    public static QcExpected Expected(QcFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var all = fixture.Documents;
        var members = all.Where(d => d.Produced).ToList();
        var families = all.GroupBy(d => d.FamilyControlNumber, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var exceptions = Checks.ToDictionary(c => c.Code, _ => new List<QcExpectedRow>());
        foreach (var m in members)
        {
            var family = families[m.FamilyControlNumber];
            if (m.Privilege == QcPrivilege.Withhold)
            {
                exceptions[1].Add(new(m.ControlNumber, null));
            }

            if (m.Privilege == QcPrivilege.Redact && m.Redactions == 0)
            {
                exceptions[3].Add(new(m.ControlNumber, null));
            }

            if (m.ProducedNatively && m.Redactions > 0)
            {
                exceptions[4].Add(new(m.ControlNumber, "NativeShipped"));
            }

            if (!m.ProducedNatively && m.ImagedPages < m.Pages)
            {
                exceptions[6].Add(new(m.ControlNumber, "PagesWithoutImage:" + (m.Pages - m.ImagedPages).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            else if (m.ProducedNatively && !m.Native)
            {
                exceptions[6].Add(new(m.ControlNumber, "NativeMissing"));
            }

            if (family.Any(d => d.Privilege == QcPrivilege.Withhold) && family.Any(d => d.Privilege != QcPrivilege.Withhold))
            {
                exceptions[9].Add(new(m.ControlNumber, "Family"));
            }

            if (family.Any(d => !d.Produced))
            {
                exceptions[12].Add(new(m.ControlNumber, null));
            }

            if (fixture.Production.IncludeText && !m.Text && m.Redactions == 0)
            {
                exceptions[13].Add(new(m.ControlNumber, null));
            }

            if (family.All(d => !d.Produced || d.Designation == QcDesignation.Blank))
            {
                exceptions[14].Add(new(m.ControlNumber, null));
            }
        }

        var checks = Checks.Select(c =>
        {
            var rows = exceptions[c.Code];
            var status = rows.Count == 0 ? "passed" : c.Blocking ? "failed" : "warning";
            return new QcExpectedCheck(c.Name, c.Code, c.Blocking ? "blocking" : "warning", c.Overridable, status, rows.Count, rows);
        }).ToList();
        var blocking = checks.Where(c => c.Severity == "blocking" && c.Status == "failed").ToList();
        var warnings = checks.Where(c => c.Status == "warning").ToList();
        return new QcExpected(fixture.Generator, fixture.Version, fixture.Seed, blocking.Count > 0 ? "blocked" : "passed", members.Count,
            blocking.Count, warnings.Count, blocking.Sum(c => c.Documents), warnings.Sum(c => c.Documents), checks);
    }

    private static string ControlNumber(QcFixtureOptions options, int n) =>
        options.ControlNumberPrefix + n.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A planted case: one unit (first document = parent).</summary>
    private static List<QcDocument> Scenario(string scenario, Rng rng)
    {
        QcDocument Doc(string? extension = null) => Clean(rng, extension) with { Scenario = scenario };
        switch (scenario)
        {
            case "withheldFamilyMember":
                {
                    var unit = new List<QcDocument> { Doc() };
                    var attachments = rng.NextInt(1, 2);
                    var withheld = rng.NextInt(1, attachments);
                    for (var a = 1; a <= attachments; a++)
                    {
                        unit.Add(a == withheld ? Doc() with { Privilege = QcPrivilege.Withhold } : Doc());
                    }

                    return unit;
                }

            case "withheldFamily":
                return [.. Enumerable.Range(0, rng.NextInt(2, 3)).Select(_ => Doc() with { Privilege = QcPrivilege.Withhold })];
            case "withheldStandalone":
                return [Doc() with { Privilege = QcPrivilege.Withhold }];
            case "redactWithRedactions":
                return [Doc("pdf") with { Privilege = QcPrivilege.Redact, Redactions = 1 }];
            case "redactWithoutRedactions":
                return [Doc("pdf") with { Privilege = QcPrivilege.Redact }];
            case "redactedSpreadsheet":
                return [Doc("xlsx") with { Privilege = QcPrivilege.Redact, Redactions = 1 }];
            case "spreadsheetNative":
                return [Doc(rng.Pick(["xlsx", "csv"]))];
            case "spreadsheetNativeMissing":
                return [Doc(rng.Pick(["xlsx", "csv"])) with { Native = false }];
            case "renderPartial":
                {
                    var pages = rng.NextInt(2, 4);
                    return [Doc("pdf") with { Pages = pages, ImagedPages = rng.NextInt(1, pages - 1) }];
                }

            case "renderNone":
                return [Doc("pdf") with { ImagedPages = 0 }];
            case "textMissing":
                return [Doc() with { Text = false }];
            case "excludedFamilyMember":
                {
                    var unit = new List<QcDocument> { Doc() };
                    var attachments = rng.NextInt(1, 3);
                    var excluded = rng.NextInt(1, attachments);
                    for (var a = 1; a <= attachments; a++)
                    {
                        unit.Add(a == excluded ? Doc() with { Produced = false } : Doc());
                    }

                    return unit;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    /// <summary>A document without problems: rendered, with native and text, coded at most Not Privileged; a random designation.</summary>
    private static QcDocument Clean(Rng rng, string? extension = null)
    {
        var ext = extension ?? rng.Pick(ImageExtensions);
        var pages = NativeExtensions.Contains(ext) ? 1 : rng.NextInt(1, 3);
        var designation = rng.NextDouble() switch
        {
            < 0.5 => QcDesignation.Blank,
            < 0.7 => QcDesignation.None,
            _ => QcDesignation.Confidential,
        };
        return new QcDocument(string.Empty, string.Empty, null, 0, ext, pages, pages, Native: true, Text: true,
            rng.Chance(0.3) ? QcPrivilege.NotPrivileged : QcPrivilege.Uncoded, 0, designation, Produced: true, "clean");
    }

    /// <summary>A random member: mostly clean, sometimes with one problem a reviewer would cause.</summary>
    private static QcDocument Random(Rng rng)
    {
        var doc = Clean(rng, rng.Chance(0.2) ? "xlsx" : null) with { Scenario = "random" };
        return rng.NextInt(0, 9) switch
        {
            0 => doc with { Text = false },
            1 when !doc.ProducedNatively => doc with { ImagedPages = doc.Pages - 1 },
            2 => doc with { Privilege = QcPrivilege.Redact, Redactions = rng.NextInt(0, 1) },
            _ => doc,
        };
    }
}
