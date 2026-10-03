using System.Text.Json;

using Opportunity.Benchmarks.Infrastructure;
using Opportunity.DataGenerator.Corpus;
using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Profiles;

namespace Opportunity.Benchmarks.Workloads;

/// <summary>Identity of the corpus a query set was generated from (copied into the query file).</summary>
public sealed record QuerySetCorpus
{
    public required string ManifestSha256 { get; init; }

    public required string GeneratorName { get; init; }

    public required string GeneratorVersion { get; init; }

    public required ulong Seed { get; init; }

    public required string ProfileName { get; init; }

    public required string ProfileHash { get; init; }

    public required long DocumentCount { get; init; }

    public required string ControlNumberPrefix { get; init; }

    public required int ControlNumberDigits { get; init; }
}

/// <summary>
/// Re-creates a corpus from its <c>corpus-manifest.json</c> (seed + effective profile) with the generator library, so
/// queries are built from, and counted against, exactly the documents that were loaded — without reading the
/// multi-gigabyte JSONL. Refuses a manifest written by another generator version or whose profile hash does not match.
/// </summary>
public sealed class QueryCorpus
{
    private QueryCorpus(GenerationContext context, QuerySetCorpus identity, int threads)
    {
        Context = context;
        Identity = identity;
        Threads = threads;
    }

    public GenerationContext Context { get; }

    public QuerySetCorpus Identity { get; }

    public int Threads { get; }

    public long DocumentCount => Identity.DocumentCount;

    public static QueryCorpus Open(string manifestPath, int threads = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        byte[] bytes = File.ReadAllBytes(manifestPath);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        JsonElement generator = root.GetProperty("generator");
        string name = generator.GetProperty("name").GetString()!;
        string version = generator.GetProperty("version").GetString()!;
        if (name != GeneratorInfo.Name || version != GeneratorInfo.Version)
        {
            throw new InvalidOperationException(
                $"{manifestPath} was written by {name} {version}; this harness regenerates corpora with {GeneratorInfo.Name} {GeneratorInfo.Version}. Generate the query set with the matching harness version.");
        }

        CorpusProfile profile = ProfileSerializer.Parse(root.GetProperty("profile").GetRawText());
        string profileHash = root.GetProperty("profileHash").GetString()!;
        if (ProfileSerializer.ComputeHash(profile) != profileHash)
        {
            throw new InvalidOperationException($"{manifestPath}: the embedded profile does not hash to {profileHash}.");
        }

        ulong seed = root.GetProperty("seed").GetUInt64();
        var context = new GenerationContext(profile, seed);
        var identity = new QuerySetCorpus
        {
            ManifestSha256 = BenchJson.Sha256Hex(bytes),
            GeneratorName = name,
            GeneratorVersion = version,
            Seed = seed,
            ProfileName = profile.Name,
            ProfileHash = profileHash,
            DocumentCount = root.GetProperty("counts").GetProperty("documents").GetInt64(),
            ControlNumberPrefix = profile.ControlNumberPrefix,
            ControlNumberDigits = profile.ControlNumberDigits,
        };
        return new QueryCorpus(context, identity, threads > 0 ? threads : Math.Min(Environment.ProcessorCount, 8));
    }

    public IEnumerable<GeneratedFamily> Families() => new CorpusGenerator(Context, Threads).GenerateFamilies();
}

/// <summary>What the evaluator knows about one document: searchable metadata, fixture coding and planted terms.</summary>
internal sealed class DocumentFacts
{
    public DocumentFacts(GeneratedDocument document, CodingFixture fixture)
    {
        Document = document;
        Coding = fixture.ValuesFor(document.DocIndex);
        object?[] f = document.Fields;
        DocumentDate = DateOf(f[FieldCatalog.DateSent]) ?? DateOf(f[FieldCatalog.DateReceived]) ?? DateOf(f[FieldCatalog.DateLastModified]) ?? DateOf(f[FieldCatalog.DateCreated]);
    }

    public GeneratedDocument Document { get; }

    public CodingValues Coding { get; }

    /// <summary>ADR-009 R25 DocumentDate as a calendar date in the query zone (UTC).</summary>
    public DateOnly? DocumentDate { get; }

    /// <summary>Token positions of the referenced terms; null when the text was not synthesised for this document.</summary>
    public IReadOnlyDictionary<int, List<int>>? Positions { get; set; }

    public string? Field(int ordinal) => Document.Fields[ordinal] as string;

    private static DateOnly? DateOf(object? value) => value is DateTimeOffset dto ? DateOnly.FromDateTime(dto.UtcDateTime) : null;
}
