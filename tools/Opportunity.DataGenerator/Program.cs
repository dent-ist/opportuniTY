using System.CommandLine;
using System.Diagnostics;
using System.Globalization;

using Opportunity.DataGenerator.Corpus;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;

var seedOption = new Option<ulong>("--seed") { Description = "Corpus seed (required; recorded in the manifest).", Required = true };
var profileOption = new Option<FileInfo?>("--profile") { Description = "Profile JSON file. Defaults to the built-in enterprise-reference profile." };
var documentsOption = new Option<long?>("--documents") { Description = "Override the profile's documentCount (the override is part of the profile hash)." };
var outOption = new Option<DirectoryInfo>("--out") { Description = "Output directory.", DefaultValueFactory = _ => new DirectoryInfo("corpus") };
var threadsOption = new Option<int>("--threads") { Description = "Worker threads. Output is identical for any value.", DefaultValueFactory = _ => Environment.ProcessorCount };
var inlineTextOption = new Option<bool>("--inline-text") { Description = "Embed extracted text in documents.jsonl (very large for big corpora)." };
var noGroundTruthOption = new Option<bool>("--no-ground-truth") { Description = "Skip ground-truth.jsonl." };

var generate = new Command("generate", "Generate a corpus: documents.jsonl, ground-truth.jsonl and corpus-manifest.json.")
{
    seedOption, profileOption, documentsOption, outOption, threadsOption, inlineTextOption, noGroundTruthOption,
};
generate.SetAction(parse =>
{
    CorpusProfile profile = LoadProfile(parse.GetValue(profileOption));
    long? documents = parse.GetValue(documentsOption);
    if (documents is { } count)
    {
        profile = ProfileSerializer.WithDocumentCount(profile, count);
    }

    ulong seed = parse.GetValue(seedOption);
    DirectoryInfo output = parse.GetValue(outOption)!;
    int threads = Math.Max(1, parse.GetValue(threadsOption));
    Console.Error.WriteLine($"{GeneratorInfo.Name} {GeneratorInfo.Version}: seed={seed} profile={profile.Name} ({ProfileSerializer.ComputeHash(profile)}) documents={profile.DocumentCount:N0} threads={threads} -> {output.FullName}");
    var stopwatch = Stopwatch.StartNew();
    long nextReport = 0;
    var progress = new Progress(docs =>
    {
        if (stopwatch.ElapsedMilliseconds >= nextReport)
        {
            nextReport = stopwatch.ElapsedMilliseconds + 5000;
            Console.Error.WriteLine($"  {docs:N0} documents ({docs / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds):N0}/s)");
        }
    });

    CorpusStatistics stats = CorpusRunner.Run(profile, seed, output.FullName, new CorpusRunOptions
    {
        Threads = threads,
        InlineText = parse.GetValue(inlineTextOption),
        WriteGroundTruth = !parse.GetValue(noGroundTruthOption),
        Progress = progress,
    });
    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"Done in {stopwatch.Elapsed.TotalSeconds:F1}s: {stats.Documents:N0} docs, {stats.Families:N0} families (mean {stats.MeanFamilySize:F3}), duplicates {stats.DuplicateRate:P2}, text p50/p99/max {stats.Text.Quantile(0.5):N0}/{stats.Text.Quantile(0.99):N0}/{stats.Text.Max:N0} bytes"));
    return 0;
});

var initOutOption = new Option<FileInfo>("--out") { Description = "Where to write the default profile.", DefaultValueFactory = _ => new FileInfo("profile.json") };
var initProfile = new Command("init-profile", "Write the default (enterprise-reference) profile as JSON.") { initOutOption };
initProfile.SetAction(parse =>
{
    FileInfo file = parse.GetValue(initOutOption)!;
    File.WriteAllText(file.FullName, ProfileSerializer.ToJson(new CorpusProfile()) + "\n");
    Console.WriteLine(file.FullName);
    return 0;
});

var hashProfileOption = new Option<FileInfo?>("--profile") { Description = "Profile JSON file (default: built-in profile)." };
var hashProfile = new Command("profile-hash", "Validate a profile and print its canonical hash.") { hashProfileOption };
hashProfile.SetAction(parse =>
{
    Console.WriteLine(ProfileSerializer.ComputeHash(LoadProfile(parse.GetValue(hashProfileOption))));
    return 0;
});

var root = new RootCommand("opportuniTY synthetic eDiscovery corpus generator (E17-T01). Output is synthetic only.")
{
    generate, initProfile, hashProfile,
};

try
{
    return root.Parse(args).Invoke(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (ProfileValidationException ex)
{
    foreach (string error in ex.Errors)
    {
        Console.Error.WriteLine("profile error: " + error);
    }

    return 2;
}

static CorpusProfile LoadProfile(FileInfo? file) =>
    file is null ? new CorpusProfile() : ProfileSerializer.Load(file.FullName);

internal sealed class Progress(Action<long> report) : IProgress<long>
{
    public void Report(long value) => report(value);
}
