using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.KnownAnswer;

/// <summary>What a planted snippet makes true of a document's text, as the search language reads it.</summary>
public enum StrFact
{
    /// <summary>The token <c>zarqon</c> (any case, or accented <c>zàrqon</c>, which ASCII folding indexes as <c>zarqon</c>).</summary>
    Zarqon,

    /// <summary>The exact token <c>quexate</c>.</summary>
    QuexateExact,

    /// <summary>A stem variant (<c>quexated</c>, <c>quexating</c>…): no stemming, so only <c>quexat*</c> finds it.</summary>
    QuexatVariant,

    /// <summary>The token sequence <c>zib quor</c> (written <c>zib-quor</c> or <c>zib quor</c>).</summary>
    ZibQuor,

    /// <summary>The single token <c>qo'zarn</c> (straight or typographic apostrophe, which ASCII folding maps to <c>'</c>).</summary>
    Qozarn,

    /// <summary>The token <c>vexil</c>.</summary>
    Vexil,

    /// <summary><c>vexil</c> and <c>zondra</c> at most 5 positions apart, in either order.</summary>
    VexilNearZondra,

    /// <summary>The token <c>quillon</c> (planted in attachments only).</summary>
    Quillon,
}

/// <summary>One snippet planted in a document's text.</summary>
/// <param name="Kind">The plant kind; every kind is planted at least once. Kinds ending in <c>Decoy</c> plant nothing a term matches.</param>
/// <param name="Text">The exact snippet.</param>
/// <param name="Facts">What it makes true; empty for a decoy that must not hit.</param>
/// <param name="Occurrences">Occurrences of the planted word in the snippet (term frequency).</param>
/// <param name="Distance">For proximity plants: the position difference of the two words (1 = adjacent).</param>
public sealed record StrPlant(string Kind, string Text, IReadOnlyList<StrFact> Facts, int Occurrences, int? Distance = null);

/// <summary>A document of the STR fixture. <see cref="FamilyControlNumber"/> is the parent's control number (its own when standalone).</summary>
public sealed record StrDocument(
    string ControlNumber, string FamilyControlNumber, string? ParentControlNumber, int FamilySequence, string Text, IReadOnlyList<StrPlant> Plants);

/// <summary>A search term of the fixture's report.</summary>
public sealed record StrTerm(string Name, string Expression);

public sealed record StrFixture(string Generator, string Version, ulong Seed, StrFixtureOptions Options, IReadOnlyList<StrTerm> Terms, IReadOnlyList<StrDocument> Documents);

/// <summary>Expected report numbers of one term, with the documents behind them (control numbers in load order).</summary>
public sealed record StrTermGroundTruth(
    string Name,
    string Expression,
    long DocumentsWithHits,
    long DocumentsWithHitsIncludingFamily,
    long UniqueHits,
    long UniqueHitsIncludingFamily,
    long PlantedOccurrences,
    IReadOnlyList<string> HitDocuments,
    IReadOnlyList<string> FamilyDocuments);

public sealed record StrTotals(long DocumentsInScope, long DocumentsWithHits, long DocumentsWithHitsIncludingFamily, long DocumentsWithoutHits);

/// <summary>How often each plant kind was planted (every kind at least once).</summary>
public sealed record StrPlantSummary(string Kind, bool Decoy, long Documents, long Plants);

public sealed record StrGroundTruth(
    string Generator, string Version, ulong Seed, StrTotals Totals, IReadOnlyList<StrTermGroundTruth> Terms, IReadOnlyList<StrPlantSummary> Plants);

public sealed record StrFixtureOptions
{
    /// <summary>Families with a parent and 0–3 attachments (the first four always have attachments).</summary>
    public int Families { get; init; } = 10;

    public int Standalone { get; init; } = 8;

    public string ControlNumberPrefix { get; init; } = "KAS";

    public void Validate()
    {
        if (Families < 4 || Families > 1_000 || Standalone < 2 || Standalone > 1_000)
        {
            throw new ArgumentException("The STR fixture needs 4–1,000 families and 2–1,000 standalone documents.");
        }

        if (string.IsNullOrWhiteSpace(ControlNumberPrefix) || !ControlNumberPrefix.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("The control number prefix must be ASCII letters and digits.");
        }
    }
}

/// <summary>
/// E17-T09: a seeded, synthetic corpus for search term reports whose answers are known by construction. Each document's
/// text is filler (<see cref="FillerWords"/>) with planted snippets: term frequencies, a stemming family (exact form
/// vs variants), hyphenated and apostrophe tokens with near-miss decoys, <c>W/5</c> pairs exactly at and just past the
/// limit in both orders, and a term planted in attachments only. Plants are at least 12 filler words apart, so no two
/// plants can form a phrase or a proximity hit together. The ground truth follows the report's definitions (E07-T10):
/// documents with hits, with family (every document of a family with a hit), unique hits (documents no other term hits)
/// and unique hits with family (documents whose family no other term hits).
/// </summary>
public static class StrFixtureGenerator
{
    public const string Version = "1.0.0";

    private const int MinGap = 12;

    /// <summary>The report's terms and the fact each needs; the last one is never planted.</summary>
    private static readonly (StrTerm Term, Func<IReadOnlySet<StrFact>, bool> Matches)[] TermDefinitions =
    [
        (new("Term frequency", "zarqon"), f => f.Contains(StrFact.Zarqon)),
        (new("Exact form", "quexate"), f => f.Contains(StrFact.QuexateExact)),
        (new("Stem variants", "quexat*"), f => f.Contains(StrFact.QuexateExact) || f.Contains(StrFact.QuexatVariant)),
        (new("Hyphenated", "zib-quor"), f => f.Contains(StrFact.ZibQuor)),
        (new("Apostrophe", "qo'zarn"), f => f.Contains(StrFact.Qozarn)),
        (new("Within 5", "vexil W/5 zondra"), f => f.Contains(StrFact.VexilNearZondra)),
        (new("Proximity word", "vexil"), f => f.Contains(StrFact.Vexil)),
        (new("Attachment only", "quillon"), f => f.Contains(StrFact.Quillon)),
        (new("Never planted", "zyxqovant"), _ => false),
    ];

    private static readonly string[] QuexatVariants = ["quexated", "quexates", "quexating", "quexation", "Quexated"];

    /// <summary>Every plant kind; each is planted at least once. Decoys plant nothing a term matches.</summary>
    private static readonly PlantKind[] Kinds =
    [
        new("termFrequency", false, rng =>
        {
            var occurrences = rng.NextInt(1, 4);
            var words = Enumerable.Range(0, occurrences).Select(_ => rng.Pick(["zarqon", "Zarqon", "ZARQON"]));
            return Plant("termFrequency", string.Join(' ', words.Select((w, i) => i == 0 ? w : FillerWords.Run(rng, rng.NextInt(1, 3)) + " " + w)),
                occurrences, StrFact.Zarqon);
        }),
        new("termAccented", false, _ => Plant("termAccented", "zàrqon", 1, StrFact.Zarqon)),
        new("stemExact", false, rng => Plant("stemExact", rng.Pick(["quexate", "Quexate"]), 1, StrFact.QuexateExact)),
        new("stemVariant", false, rng => Plant("stemVariant", rng.Pick(QuexatVariants), 1, StrFact.QuexatVariant)),
        new("hyphenated", false, rng => Plant("hyphenated", rng.Pick(["zib-quor", "ZIB-QUOR"]), 1, StrFact.ZibQuor)),
        new("hyphenSpaced", false, _ => Plant("hyphenSpaced", "zib quor", 1, StrFact.ZibQuor)),
        new("hyphenJoinedDecoy", false, _ => Plant("hyphenJoinedDecoy", "zibquor", 1)),
        new("hyphenReversedDecoy", false, _ => Plant("hyphenReversedDecoy", "quor zib", 1)),
        new("hyphenPartDecoy", false, rng => Plant("hyphenPartDecoy", rng.Pick(["zib", "quor"]), 1)),
        new("apostrophe", false, rng => Plant("apostrophe", rng.Pick(["qo'zarn", "Qo'zarn"]), 1, StrFact.Qozarn)),
        new("apostropheTypographic", false, _ => Plant("apostropheTypographic", "qo’zarn", 1, StrFact.Qozarn)),
        new("apostropheDroppedDecoy", false, _ => Plant("apostropheDroppedDecoy", "qozarn", 1)),
        new("apostrophePossessiveDecoy", false, _ => Plant("apostrophePossessiveDecoy", "qo'zarn's", 1)),
        new("proximity5Forward", false, rng => Proximity("proximity5Forward", rng, 5, reversed: false)),
        new("proximity5Reversed", false, rng => Proximity("proximity5Reversed", rng, 5, reversed: true)),
        new("proximity6Forward", false, rng => Proximity("proximity6Forward", rng, 6, reversed: false)),
        new("proximity6Reversed", false, rng => Proximity("proximity6Reversed", rng, 6, reversed: true)),
        new("proximityNear", false, rng => Proximity("proximityNear", rng, rng.NextInt(1, 4), rng.Chance(0.5))),
        new("proximityFar", false, rng => Proximity("proximityFar", rng, rng.NextInt(7, 15), rng.Chance(0.5))),
        new("proximityWordAlone", false, _ => Plant("proximityWordAlone", "vexil", 1, StrFact.Vexil)),
        new("proximityPartnerDecoy", false, _ => Plant("proximityPartnerDecoy", "zondra", 1)),
        new("attachmentOnly", true, rng =>
        {
            var occurrences = rng.NextInt(1, 2);
            return Plant("attachmentOnly", occurrences == 1 ? "quillon" : "quillon " + FillerWords.Run(rng, 2) + " Quillon", occurrences, StrFact.Quillon);
        }),
    ];

    public static IReadOnlyList<StrTerm> Terms => [.. TermDefinitions.Select(t => t.Term)];

    public static StrFixture Generate(ulong seed, StrFixtureOptions? options = null)
    {
        options ??= new StrFixtureOptions();
        options.Validate();
        var rng = Rng.For(seed, StreamTag.KnownAnswerStr);

        // Units: families (parent + attachments) and standalone documents, in a seeded order.
        var units = new List<int>();
        for (var f = 0; f < options.Families; f++)
        {
            units.Add(f < 4 ? rng.NextInt(1, 3) : rng.NextInt(0, 3));
        }

        units.AddRange(Enumerable.Repeat(-1, options.Standalone));
        rng.Shuffle(units);
        var shapes = new List<(string ControlNumber, string Family, string? Parent, int Sequence)>();
        var digits = Math.Max(4, (options.Families * 4 + options.Standalone).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        string Next() => options.ControlNumberPrefix + (shapes.Count + 1).ToString("D" + digits, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var attachments in units)
        {
            var parent = Next();
            shapes.Add((parent, parent, null, 0));
            for (var a = 1; a <= attachments; a++)
            {
                shapes.Add((Next(), parent, parent, a));
            }
        }

        // A few documents stay without any plant; every kind is planted once on a seeded eligible document, then extras.
        var plantKinds = shapes.Select(_ => new List<PlantKind>()).ToList();
        var empty = new HashSet<int>();
        var emptyCount = Math.Max(2, shapes.Count / 7);
        var candidates = Enumerable.Range(0, shapes.Count).ToList();
        rng.Shuffle(candidates);
        foreach (var index in candidates.Where(i => shapes[i].Parent is null).Take(emptyCount))
        {
            empty.Add(index);
        }

        foreach (var kind in Kinds)
        {
            var eligible = Enumerable.Range(0, shapes.Count).Where(i => !empty.Contains(i) && (!kind.AttachmentsOnly || shapes[i].Parent is not null)).ToList();
            plantKinds[rng.Pick(eligible)].Add(kind);
        }

        for (var i = 0; i < shapes.Count; i++)
        {
            if (empty.Contains(i))
            {
                continue;
            }

            var extras = rng.Chance(0.4) ? (rng.Chance(0.3) ? 2 : 1) : 0;
            for (var e = 0; e < extras; e++)
            {
                var kind = rng.Pick(Kinds);
                if (!kind.AttachmentsOnly || shapes[i].Parent is not null)
                {
                    plantKinds[i].Add(kind);
                }
            }
        }

        var documents = new List<StrDocument>(shapes.Count);
        for (var i = 0; i < shapes.Count; i++)
        {
            var docRng = Rng.For(seed, StreamTag.KnownAnswerStr, 1, (ulong)i);
            var plants = plantKinds[i].Select(k => k.Make(docRng)).ToList();
            docRng.Shuffle(plants);
            var parts = new List<string> { Gap(docRng, docRng.NextInt(3, 8)) };
            foreach (var plant in plants)
            {
                parts.Add(plant.Text);
                parts.Add(Gap(docRng, docRng.NextInt(MinGap, MinGap + 8)));
            }

            var (controlNumber, family, parent, sequence) = shapes[i];
            documents.Add(new StrDocument(controlNumber, family, parent, sequence, string.Join(' ', parts), plants));
        }

        return new StrFixture(GeneratorInfo.Name, Version, seed, options, Terms, documents);
    }

    public static StrGroundTruth GroundTruth(StrFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var docs = fixture.Documents;
        var hits = docs.Select(d =>
        {
            var facts = d.Plants.SelectMany(p => p.Facts).ToHashSet();
            return Enumerable.Range(0, TermDefinitions.Length).Where(t => TermDefinitions[t].Matches(facts)).ToHashSet();
        }).ToList();
        var familyTerms = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var i = 0; i < docs.Count; i++)
        {
            if (!familyTerms.TryGetValue(docs[i].FamilyControlNumber, out var set))
            {
                familyTerms[docs[i].FamilyControlNumber] = set = [];
            }

            set.UnionWith(hits[i]);
        }

        var terms = new List<StrTermGroundTruth>();
        for (var t = 0; t < TermDefinitions.Length; t++)
        {
            var hitDocs = new List<string>();
            var familyDocs = new List<string>();
            long unique = 0, uniqueFamily = 0, occurrences = 0;
            for (var i = 0; i < docs.Count; i++)
            {
                var expanded = familyTerms[docs[i].FamilyControlNumber];
                if (hits[i].Contains(t))
                {
                    hitDocs.Add(docs[i].ControlNumber);
                    unique += hits[i].Count == 1 ? 1 : 0;
                    occurrences += docs[i].Plants
                        .Where(p => p.Facts.Count > 0 && TermDefinitions[t].Matches(p.Facts.ToHashSet()))
                        .Sum(p => (long)p.Occurrences);
                }

                if (expanded.Contains(t))
                {
                    familyDocs.Add(docs[i].ControlNumber);
                    uniqueFamily += expanded.Count == 1 ? 1 : 0;
                }
            }

            var term = TermDefinitions[t].Term;
            terms.Add(new StrTermGroundTruth(term.Name, term.Expression, hitDocs.Count, familyDocs.Count, unique, uniqueFamily, occurrences, hitDocs, familyDocs));
        }

        var withHits = hits.LongCount(h => h.Count > 0);
        var withFamily = docs.LongCount(d => familyTerms[d.FamilyControlNumber].Count > 0);
        var plants = Kinds.Select(k => new StrPlantSummary(
            k.Name,
            k.Name.EndsWith("Decoy", StringComparison.Ordinal),
            docs.LongCount(d => d.Plants.Any(p => p.Kind == k.Name)),
            docs.Sum(d => (long)d.Plants.Count(p => p.Kind == k.Name)))).ToList();
        return new StrGroundTruth(fixture.Generator, fixture.Version, fixture.Seed,
            new StrTotals(docs.Count, withHits, withFamily, docs.Count - withHits), terms, plants);
    }

    private static StrPlant Plant(string kind, string text, int occurrences, params StrFact[] facts) => new(kind, text, facts, occurrences);

    private static StrPlant Proximity(string kind, Rng rng, int distance, bool reversed)
    {
        var (first, second) = reversed ? ("zondra", "vexil") : ("vexil", "zondra");
        var between = distance > 1 ? " " + FillerWords.Run(rng, distance - 1) + " " : " ";
        var facts = distance <= 5 ? new[] { StrFact.Vexil, StrFact.VexilNearZondra } : [StrFact.Vexil];
        return new StrPlant(kind, first + between + second, facts, 1, distance);
    }

    /// <summary>Filler with an occasional full stop (punctuation is not a token, so it moves no positions).</summary>
    private static string Gap(Rng rng, int count)
    {
        var words = Enumerable.Range(0, count).Select(_ => FillerWords.Next(rng)).ToArray();
        for (var i = 1; i < words.Length - 1; i++)
        {
            if (rng.Chance(0.12))
            {
                words[i] += ".";
            }
        }

        return string.Join(' ', words);
    }

    private sealed record PlantKind(string Name, bool AttachmentsOnly, Func<Rng, StrPlant> Make);
}
