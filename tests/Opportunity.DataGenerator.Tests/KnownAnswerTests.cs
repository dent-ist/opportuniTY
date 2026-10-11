using System.Security.Cryptography;

using AwesomeAssertions;

using Opportunity.DataGenerator.Corpus.KnownAnswer;

namespace Opportunity.DataGenerator.Tests;

/// <summary>
/// E17-T09: known-answer fixtures are deterministic by seed, every planted case is present for any seed, the STR ground
/// truth agrees with an independent reading of the generated text, and the expected QC result follows the gate's rules.
/// The integration suite runs the real features against these fixtures (KnownAnswerSearchTermReportTests,
/// KnownAnswerProductionQcTests).
/// </summary>
public sealed class KnownAnswerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opp-known-answer-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Files_are_byte_identical_for_a_seed_and_differ_between_seeds()
    {
        var first = KnownAnswerWriter.Write(Path.Combine(_root, "a"), 147);
        var again = KnownAnswerWriter.Write(Path.Combine(_root, "b"), 147);
        var other = KnownAnswerWriter.Write(Path.Combine(_root, "c"), 148);

        first.Select(f => f.Path).Should().Equal(
            KnownAnswerWriter.StrFixtureFile, KnownAnswerWriter.StrGroundTruthFile, KnownAnswerWriter.QcFixtureFile, KnownAnswerWriter.QcExpectedFile,
            KnownAnswerWriter.ManifestFile);
        again.Should().Equal(first);
        foreach (var file in first)
        {
            var bytes = File.ReadAllBytes(Path.Combine(_root, "a", file.Path));
            Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(file.Sha256);
            File.ReadAllBytes(Path.Combine(_root, "b", file.Path)).Should().Equal(bytes);
            bytes.Should().NotContain((byte)'\r');
        }

        other.Take(4).Select(f => f.Sha256).Should().NotIntersectWith(first.Select(f => f.Sha256), "another seed gives other documents and answers");
        File.ReadAllText(Path.Combine(_root, "a", KnownAnswerWriter.ManifestFile)).Should().Contain("\"seed\": 147").And.Contain(first[1].Sha256);

        // The files read back into the model they were written from.
        var fixture = KnownAnswerWriter.Read<StrFixture>(Path.Combine(_root, "a", KnownAnswerWriter.StrFixtureFile));
        KnownAnswerWriter.Serialize(StrFixtureGenerator.GroundTruth(fixture)).Should().Equal(File.ReadAllBytes(Path.Combine(_root, "a", KnownAnswerWriter.StrGroundTruthFile)));
        var qc = KnownAnswerWriter.Read<QcFixture>(Path.Combine(_root, "a", KnownAnswerWriter.QcFixtureFile));
        KnownAnswerWriter.Serialize(QcFixtureGenerator.Expected(qc)).Should().Equal(File.ReadAllBytes(Path.Combine(_root, "a", KnownAnswerWriter.QcExpectedFile)));
    }

    [Fact]
    public void Every_plant_kind_and_qc_scenario_is_present_for_any_seed()
    {
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var str = StrFixtureGenerator.GroundTruth(StrFixtureGenerator.Generate(seed));
            str.Plants.Should().OnlyContain(p => p.Documents > 0, $"seed {seed}");
            str.Terms.Single(t => t.Expression == "quillon").Should().Match<StrTermGroundTruth>(
                t => t.DocumentsWithHitsIncludingFamily > t.DocumentsWithHits, "an attachment-only term expands to the parents");
            str.Terms.Single(t => t.Expression == "zyxqovant").DocumentsWithHitsIncludingFamily.Should().Be(0);
            str.Totals.DocumentsWithoutHits.Should().BePositive();

            var qc = QcFixtureGenerator.Generate(seed);
            qc.Documents.Select(d => d.Scenario).Should().Contain(QcFixtureGenerator.Scenarios);
            var expected = QcFixtureGenerator.Expected(qc);
            expected.Outcome.Should().Be("blocked");
            foreach (var check in new[] { "withheldWithoutPlaceholder", "redactWithoutRedactions", "redactedNative", "renderFailure", "privilegeConflicts", "incompleteFamily", "textMissing" })
            {
                expected.Checks.Single(c => c.Check == check).Documents.Should().BePositive($"seed {seed} plants {check}");
            }

            expected.BlockingFailures.Should().Be(expected.Checks.Count(c => c.Status == "failed"));
            expected.Warnings.Should().Be(expected.Checks.Count(c => c.Status == "warning"));
            expected.BlockingExceptions.Should().Be(expected.Checks.Where(c => c.Severity == "blocking").Sum(c => c.Documents));
            expected.Members.Should().Be(qc.Documents.Count(d => d.Produced));
        }
    }

    [Fact]
    public void Str_ground_truth_agrees_with_an_independent_reading_of_the_text()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var fixture = StrFixtureGenerator.Generate(seed, new StrFixtureOptions { Families = 6 + (int)(seed % 5), Standalone = 2 + (int)(seed % 7) });
            var truth = StrFixtureGenerator.GroundTruth(fixture);
            var tokens = fixture.Documents.ToDictionary(d => d.ControlNumber, d => Tokens(d.Text), StringComparer.Ordinal);

            // Filler never produces anything a term could match: every token that is not part of a plant is filler.
            var planted = fixture.Documents.SelectMany(d => d.Plants).SelectMany(p => Tokens(p.Text)).ToHashSet(StringComparer.Ordinal);
            tokens.Values.SelectMany(t => t).Where(t => !planted.Contains(t)).Should().OnlyContain(t => FillerWords.IsFiller(t));

            var hits = fixture.Terms.Select(t => fixture.Documents.Where(d => Evaluate(t.Expression, tokens[d.ControlNumber])).Select(d => d.ControlNumber).ToList()).ToList();
            var byDoc = fixture.Documents.ToDictionary(d => d.ControlNumber, d => Enumerable.Range(0, hits.Count).Where(t => hits[t].Contains(d.ControlNumber)).ToHashSet(), StringComparer.Ordinal);
            var byFamily = fixture.Documents.GroupBy(d => d.FamilyControlNumber, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.SelectMany(d => byDoc[d.ControlNumber]).ToHashSet(), StringComparer.Ordinal);
            for (var t = 0; t < hits.Count; t++)
            {
                var family = fixture.Documents.Where(d => byFamily[d.FamilyControlNumber].Contains(t)).Select(d => d.ControlNumber).ToList();
                truth.Terms[t].Should().BeEquivalentTo(new
                {
                    HitDocuments = hits[t],
                    FamilyDocuments = family,
                    DocumentsWithHits = (long)hits[t].Count,
                    DocumentsWithHitsIncludingFamily = (long)family.Count,
                    UniqueHits = (long)hits[t].Count(cn => byDoc[cn].Count == 1),
                    UniqueHitsIncludingFamily = fixture.Documents.LongCount(d => byFamily[d.FamilyControlNumber].Count == 1 && byFamily[d.FamilyControlNumber].Contains(t)),
                }, o => o.WithStrictOrdering(), $"seed {seed} term {fixture.Terms[t].Expression}");
            }

            truth.Totals.Should().Be(new StrTotals(fixture.Documents.Count, byDoc.Values.Count(s => s.Count > 0),
                fixture.Documents.Count(d => byFamily[d.FamilyControlNumber].Count > 0), byDoc.Values.Count(s => s.Count == 0)));
            truth.Terms[0].PlantedOccurrences.Should().Be(tokens.Values.Sum(t => t.Count(w => w == "zarqon")));
            fixture.Documents.SelectMany(d => d.Plants).Where(p => p.Distance is not null)
                .Should().Contain(p => p.Distance == 5).And.Contain(p => p.Distance == 6);
        }
    }

    [Fact]
    public void Qc_expectations_follow_the_gate_rules_on_a_hand_built_production()
    {
        static QcDocument D(string cn, string family, string extension = "pdf", int pages = 1, int imaged = 1, bool native = true, bool text = true,
            QcPrivilege privilege = QcPrivilege.Uncoded, int redactions = 0, QcDesignation designation = QcDesignation.Blank, bool produced = true) =>
            new(cn, family, cn == family ? null : family, cn == family ? 0 : 1, extension, pages, imaged, native, text, privilege, redactions, designation, produced, "test");
        var fixture = new QcFixture("test", "1", 0, new QcFixtureOptions(), new QcProductionSettings("T", "block", true, true),
        [
            D("T1", "T1", designation: QcDesignation.Confidential), D("T2", "T1", privilege: QcPrivilege.Withhold), D("T3", "T1", produced: false),
            D("T4", "T4", "xlsx", redactions: 1, privilege: QcPrivilege.Redact, text: false),
            D("T5", "T5", "csv", native: false, designation: QcDesignation.None),
            D("T6", "T6", pages: 3, imaged: 1, privilege: QcPrivilege.Redact, text: false),
        ]);

        var expected = QcFixtureGenerator.Expected(fixture);

        expected.Checks.Where(c => c.Documents > 0).Select(c => (c.Check, string.Join(",", c.Exceptions.Select(e => e.ControlNumber + ":" + e.Detail))))
            .Should().Equal(
                ("withheldWithoutPlaceholder", "T2:"),
                ("redactWithoutRedactions", "T6:"),
                ("redactedNative", "T4:NativeShipped"),
                ("renderFailure", "T5:NativeMissing,T6:PagesWithoutImage:2"),
                ("privilegeConflicts", "T1:Family,T2:Family"),
                ("incompleteFamily", "T1:,T2:"),
                ("textMissing", "T6:"),
                ("blankConfidentiality", "T4:,T6:"));
        expected.Should().BeEquivalentTo(new { Outcome = "blocked", Members = 5L, BlockingFailures = 5, Warnings = 3, BlockingExceptions = 7L, WarningExceptions = 5L });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// The standard tokenizer and folding over this fixture's alphabet: whitespace and hyphens separate tokens, a full stop
    /// ends one, an apostrophe between letters stays inside, lower case, accents and typographic apostrophes folded.
    /// </summary>
    private static List<string> Tokens(string text) =>
        [.. text.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.TrimEnd('.').ToLowerInvariant().Replace('à', 'a').Replace('’', '\''))
            .Where(t => t.Length > 0)];

    private static bool Evaluate(string expression, List<string> tokens)
    {
        switch (expression)
        {
            case "vexil W/5 zondra":
                var vexil = tokens.Select((t, i) => (t, i)).Where(x => x.t == "vexil").Select(x => x.i).ToList();
                var zondra = tokens.Select((t, i) => (t, i)).Where(x => x.t == "zondra").Select(x => x.i).ToList();
                return vexil.Any(v => zondra.Any(z => Math.Abs(v - z) <= 5));
            case var prefix when prefix.EndsWith('*'):
                return tokens.Any(t => t.StartsWith(prefix[..^1], StringComparison.Ordinal));
            case var hyphenated when hyphenated.Contains('-', StringComparison.Ordinal):
                var parts = hyphenated.Split('-');
                return Enumerable.Range(0, tokens.Count - 1).Any(i => tokens[i] == parts[0] && tokens[i + 1] == parts[1]);
            default:
                return tokens.Contains(expression);
        }
    }
}
