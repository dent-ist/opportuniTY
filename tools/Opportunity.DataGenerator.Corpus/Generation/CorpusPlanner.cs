using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.Generation;

public enum UnitKind
{
    EmailConversation,
    EDocument,

    /// <summary>A single unique e-doc used only to land exactly on documentCount (see <see cref="CorpusPlanner"/>).</summary>
    Filler,
}

public sealed record FamilyPlan(int ChildCount, bool IsContainer, int ExtraCopies)
{
    public long DocumentCount => (1L + ChildCount) * (1L + ExtraCopies);
}

/// <summary>
/// Shape of one generation unit: an email conversation (1..n message families) or a loose e-doc family, each with
/// its whole-family duplicate copies. A pure function of (seed, profile, unit index).
/// </summary>
public sealed record UnitPlan(long UnitIndex, UnitKind Kind, IReadOnlyList<FamilyPlan> Families)
{
    public long DocumentCount { get; } = Families.Sum(f => f.DocumentCount);
}

public sealed record ChunkPlan(long Index, long FirstDocIndex, IReadOnlyList<UnitPlan> Units)
{
    public long DocumentCount => Units.Sum(u => u.DocumentCount);
}

/// <summary>
/// Sequential, cheap planning pass. Candidate unit i is planned from hash(seed, i); a unit is included only if it
/// fits in the remaining document budget, so the corpus lands exactly on documentCount without truncating any
/// family or duplicate group. Candidates are grouped into fixed-size chunks, so chunk contents never depend on
/// thread count.
/// </summary>
public sealed class CorpusPlanner(GenerationContext context)
{
    /// <summary>After this many consecutive candidates that do not fit, the remainder is filled with fillers.</summary>
    public const int SkipLimit = 1000;

    private const int MaxGeometricChildren = 100_000;

    public UnitPlan PlanUnit(long unitIndex)
    {
        CorpusProfile profile = context.Profile;
        Calibration cal = context.Calibration;
        var rng = Rng.For(context.Seed, StreamTag.UnitPlan, (ulong)unitIndex);
        bool isEmail = rng.Chance(cal.EmailUnitProbability);
        int messages = isEmail && !rng.Chance(profile.Threads.SingleMessageShare) ? cal.ThreadLength.Sample(rng) : 1;

        var families = new FamilyPlan[messages];
        for (int i = 0; i < messages; i++)
        {
            bool container = rng.Chance(isEmail ? profile.Families.EmailContainerShare : profile.Families.EdocContainerShare);
            int children = container
                ? cal.Container.Sample(rng)
                : Math.Min(MaxGeometricChildren, rng.Geometric(isEmail ? cal.EmailGeometricMean : profile.Families.EdocMeanEmbeddedChildren));
            int extra = rng.Chance(cal.DuplicatedFamilyProbability)
                ? 1 + rng.Geometric(profile.Duplicates.CopiesWhenDuplicatedMean - 1)
                : 0;
            families[i] = new FamilyPlan(children, container, extra);
        }

        return new UnitPlan(unitIndex, isEmail ? UnitKind.EmailConversation : UnitKind.EDocument, families);
    }

    public static UnitPlan Filler(long unitIndex) => new(unitIndex, UnitKind.Filler, [new FamilyPlan(0, false, 0)]);

    public IEnumerable<ChunkPlan> Chunks()
    {
        long target = context.Profile.DocumentCount;
        int unitsPerChunk = context.Profile.UnitsPerChunk;
        long documents = 0;
        long candidate = 0;
        int consecutiveSkips = 0;
        bool fill = false;
        for (long chunk = 0; documents < target; chunk++)
        {
            long first = documents;
            var units = new List<UnitPlan>();
            for (int i = 0; i < unitsPerChunk && documents < target; i++, candidate++)
            {
                UnitPlan plan = fill ? Filler(candidate) : PlanUnit(candidate);
                if (documents + plan.DocumentCount <= target)
                {
                    units.Add(plan);
                    documents += plan.DocumentCount;
                    consecutiveSkips = 0;
                }
                else if (++consecutiveSkips >= SkipLimit)
                {
                    fill = true;
                }
            }

            if (units.Count > 0)
            {
                yield return new ChunkPlan(chunk, first, units);
            }
        }
    }
}