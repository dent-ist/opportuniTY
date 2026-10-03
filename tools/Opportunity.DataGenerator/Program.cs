using System.CommandLine;
using System.Diagnostics;
using System.Globalization;

using Opportunity.DataGenerator.Corpus;
using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;

var seedOption = new Option<ulong>("--seed") { Description = "Corpus seed (required; recorded in the manifest).", Required = true };
var profileOption = new Option<FileInfo?>("--profile") { Description = "Profile JSON file. Defaults to the built-in enterprise-reference profile." };
var documentsOption = new Option<long?>("--documents") { Description = "Override the profile's documentCount (the override is part of the profile hash)." };
var outOption = new Option<DirectoryInfo>("--out") { Description = "Output directory.", DefaultValueFactory = _ => new DirectoryInfo("corpus") };
var threadsOption = new Option<int>("--threads") { Description = "Worker threads. Output is identical for any value.", DefaultValueFactory = _ => Environment.ProcessorCount };
var inlineTextOption = new Option<bool>("--inline-text") { Description = "Embed extracted text in documents.jsonl (very large for big corpora)." };
var noGroundTruthOption = new Option<bool>("--no-ground-truth") { Description = "Skip ground-truth.jsonl." };

var volumesOption = new Option<bool>("--volumes") { Description = "Also write load-file volumes (VOL001/DATA|IMAGES|NATIVES|TEXT) with defect ground truth." };
var datPresetOption = new Option<string>("--dat-preset") { Description = "DAT delimiter preset: concordance, vendor or csv.", DefaultValueFactory = _ => "concordance" };
var datEncodingOption = new Option<string>("--dat-encoding") { Description = "DAT encoding: utf-8, utf-8-bom, utf-16le or windows-1252.", DefaultValueFactory = _ => "utf-8-bom" };
var textEncodingOption = new Option<string>("--text-encoding") { Description = "Extracted-text encoding: utf-8, utf-8-bom, utf-16le or windows-1252.", DefaultValueFactory = _ => "utf-8" };
var dateFormatOption = new Option<string>("--date-format") { Description = "DAT date format: iso (with offset), us (MM/dd/yyyy) or eu (dd/MM/yyyy).", DefaultValueFactory = _ => "iso" };
var timeZoneOption = new Option<string>("--time-zone-offset") { Description = "Zone us/eu dates are rendered in, e.g. -05:00.", DefaultValueFactory = _ => "+00:00" };
var imageFormatOption = new Option<string>("--image-format") { Description = "Page images: auto (TIFF G4, JPG for image natives), tiff, jpg, png or multipage-tiff.", DefaultValueFactory = _ => "auto" };
var volumePrefixOption = new Option<string>("--volume-prefix") { Description = "Volume folder prefix.", DefaultValueFactory = _ => "VOL" };
var docsPerVolumeOption = new Option<long>("--docs-per-volume") { Description = "Documents per volume (families are not split); 0 = one volume." };
var filesPerFolderOption = new Option<int>("--files-per-folder") { Description = "Maximum files per IMAGES/NATIVES/TEXT subfolder.", DefaultValueFactory = _ => 1000 };
var noNativesOption = new Option<bool>("--no-natives") { Description = "Do not write natives." };
var noTextOption = new Option<bool>("--no-text") { Description = "Do not write extracted text files." };
var noImagesOption = new Option<bool>("--no-images") { Description = "Do not write page images or OPT rows." };
var nativeTextCharsOption = new Option<int>("--native-text-chars") { Description = "Characters of extracted text embedded in each native.", DefaultValueFactory = _ => 2000 };
var overlayRateOption = new Option<double>("--overlay-rate") { Description = "Share of documents listed in the overlay DAT (<VOL>_OVERLAY.dat)." };
var defectRateOption = new Option<double>("--defect-rate") { Description = "Injection rate for every defect type (share of the type's eligible items)." };
var defectOption = new Option<string[]>("--defect") { Description = "Per-type rate override, e.g. --defect missingNative=0.02 (repeatable).", AllowMultipleArgumentsPerToken = true };

var generate = new Command("generate", "Generate a corpus: documents.jsonl, ground-truth.jsonl and corpus-manifest.json (plus load-file volumes with --volumes).")
{
    seedOption, profileOption, documentsOption, outOption, threadsOption, inlineTextOption, noGroundTruthOption,
    volumesOption, datPresetOption, datEncodingOption, textEncodingOption, dateFormatOption, timeZoneOption, imageFormatOption,
    volumePrefixOption, docsPerVolumeOption, filesPerFolderOption, noNativesOption, noTextOption, noImagesOption,
    nativeTextCharsOption, overlayRateOption, defectRateOption, defectOption,
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

    var sinkFactories = new List<Func<GenerationContext, ICorpusSink>>();
    if (parse.GetValue(volumesOption))
    {
        VolumeOptions volumeOptions = BuildVolumeOptions(parse);
        volumeOptions.Validate();
        sinkFactories.Add(context => new VolumeWriter(context, output.FullName, volumeOptions));
    }

    CorpusStatistics stats = CorpusRunner.Run(profile, seed, output.FullName, new CorpusRunOptions
    {
        Threads = threads,
        InlineText = parse.GetValue(inlineTextOption),
        WriteGroundTruth = !parse.GetValue(noGroundTruthOption),
        SinkFactories = sinkFactories,
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
catch (ArgumentException ex) when (ex is not ArgumentNullException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 2;
}
catch (ProfileValidationException ex)
{
    foreach (string error in ex.Errors)
    {
        Console.Error.WriteLine("profile error: " + error);
    }

    return 2;
}

VolumeOptions BuildVolumeOptions(ParseResult parse)
{
    Dictionary<DefectType, double> rates = VolumeOptions.AllDefects(parse.GetValue(defectRateOption));
    foreach (string spec in parse.GetValue(defectOption) ?? [])
    {
        string[] parts = spec.Split('=', 2);
        if (parts.Length != 2 || !DefectNames.TryParse(parts[0].Trim(), out DefectType type)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
        {
            throw new ArgumentException($"Invalid --defect '{spec}'. Expected <type>=<rate>; types: {string.Join(", ", Enum.GetValues<DefectType>().Select(DefectNames.Name))}.");
        }

        rates[type] = rate;
    }

    return new VolumeOptions
    {
        Delimiters = DelimiterProfile.Preset(parse.GetValue(datPresetOption)!),
        DatEncoding = ParseEncoding(parse.GetValue(datEncodingOption)!),
        TextEncoding = ParseEncoding(parse.GetValue(textEncodingOption)!),
        DateFormat = ParseEnum<DatDateFormat>(parse.GetValue(dateFormatOption)!, "--date-format"),
        TimeZoneOffset = ParseOffset(parse.GetValue(timeZoneOption)!),
        ImageFormat = ParseEnum<PageImageFormat>(parse.GetValue(imageFormatOption)!.Replace("-", "", StringComparison.Ordinal), "--image-format"),
        VolumePrefix = parse.GetValue(volumePrefixOption)!,
        DocumentsPerVolume = parse.GetValue(docsPerVolumeOption),
        FilesPerFolder = parse.GetValue(filesPerFolderOption),
        IncludeNatives = !parse.GetValue(noNativesOption),
        IncludeText = !parse.GetValue(noTextOption),
        IncludeImages = !parse.GetValue(noImagesOption),
        NativeTextChars = parse.GetValue(nativeTextCharsOption),
        OverlayRate = parse.GetValue(overlayRateOption),
        DefectRates = rates,
    };
}

static LoadFileEncoding ParseEncoding(string value) =>
    LoadFileEncodings.TryParse(value, out LoadFileEncoding encoding)
        ? encoding
        : throw new ArgumentException($"Unknown encoding '{value}' (expected utf-8, utf-8-bom, utf-16le or windows-1252).");

static T ParseEnum<T>(string value, string option)
    where T : struct, Enum =>
    Enum.TryParse(value, ignoreCase: true, out T result) && Enum.IsDefined(result)
        ? result
        : throw new ArgumentException($"Unknown {option} value '{value}'.");

static TimeSpan ParseOffset(string value)
{
    string trimmed = value.Trim();
    bool negative = trimmed.StartsWith('-');
    if (TimeSpan.TryParseExact(trimmed.TrimStart('+', '-'), @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan offset))
    {
        return negative ? -offset : offset;
    }

    throw new ArgumentException($"Invalid --time-zone-offset '{value}' (expected ±hh:mm).");
}

static CorpusProfile LoadProfile(FileInfo? file) =>
    file is null ? new CorpusProfile() : ProfileSerializer.Load(file.FullName);

internal sealed class Progress(Action<long> report) : IProgress<long>
{
    public void Report(long value) => report(value);
}
