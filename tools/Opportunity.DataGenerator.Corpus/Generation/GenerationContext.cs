using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Profiles;

namespace Opportunity.DataGenerator.Corpus.Generation;

/// <summary>Immutable, thread-safe state shared by all generation work for one (seed, profile).</summary>
public sealed class GenerationContext
{
    public GenerationContext(CorpusProfile profile, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ProfileSerializer.Validate(profile);
        Profile = profile;
        Seed = seed;
        ProfileHash = ProfileSerializer.ComputeHash(profile);
        Calibration = new Calibration(profile);
        Catalog = new FieldCatalog(profile.Fields);
        NeedleTerms = [.. profile.Needles.Select(n => n.Term), .. profile.ProximityPairs.SelectMany(p => new[] { p.First, p.Second })];
        Vocabulary = new Vocabulary(profile.Text, NeedleTerms);
        People = new PeopleDirectory(profile, seed);
        double expectedNearDuplicateDocs = profile.DocumentCount * (1 - profile.Mix.EmailShare) * profile.NearDuplicates.Rate;
        NearDuplicateClusterCount = Math.Max(1, (long)Math.Ceiling(expectedNearDuplicateDocs / profile.NearDuplicates.MeanClusterSize));
    }

    public CorpusProfile Profile { get; }

    public ulong Seed { get; }

    public string ProfileHash { get; }

    public Calibration Calibration { get; }

    public FieldCatalog Catalog { get; }

    public IReadOnlyList<string> NeedleTerms { get; }

    public Vocabulary Vocabulary { get; }

    public PeopleDirectory People { get; }

    public long NearDuplicateClusterCount { get; }
}
