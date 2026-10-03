using System.Text.Json;

using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Text;

namespace Opportunity.DataGenerator.Corpus.Output;

public sealed class CorpusRunOptions
{
    public int Threads { get; init; } = 1;

    /// <summary>Embed each document's extracted text in documents.jsonl (large: mean text is tens of KB).</summary>
    public bool InlineText { get; init; }

    public bool WriteGroundTruth { get; init; } = true;

    /// <summary>Additional sinks (e.g. load-file volume writers) fed alongside the built-in outputs.</summary>
    public IReadOnlyList<ICorpusSink> ExtraSinks { get; init; } = [];

    /// <summary>Sinks that need the run's <see cref="GenerationContext"/> (e.g. the load-file volume writer).</summary>
    public IReadOnlyList<Func<GenerationContext, ICorpusSink>> SinkFactories { get; init; } = [];

    public IProgress<long>? Progress { get; init; }
}

/// <summary>Runs a generator into the standard outputs and writes <c>corpus-manifest.json</c>.</summary>
public static class CorpusRunner
{
    public const string DocumentsFile = "documents.jsonl";
    public const string GroundTruthFile = "ground-truth.jsonl";
    public const string ManifestFile = "corpus-manifest.json";
    public const int ManifestVersion = 1;

    public static CorpusStatistics Run(CorpusProfile profile, ulong seed, string outputDirectory, CorpusRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outputDirectory);
        options ??= new CorpusRunOptions();
        Directory.CreateDirectory(outputDirectory);
        var context = new GenerationContext(profile, seed);
        var generator = new CorpusGenerator(context, options.Threads);
        var stats = new CorpusStatistics(context);
        var sinks = new List<ICorpusSink>
        {
            new JsonlCorpusWriter(outputDirectory, DocumentsFile, context.Catalog, options.InlineText ? new TextSynthesizer(context.Vocabulary) : null),
        };
        if (options.WriteGroundTruth)
        {
            sinks.Add(new GroundTruthWriter(outputDirectory, GroundTruthFile));
        }

        sinks.AddRange(options.ExtraSinks);
        try
        {
            sinks.AddRange(options.SinkFactories.Select(factory => factory(context)));
            foreach (GeneratedChunk chunk in generator.GenerateChunks())
            {
                stats.Write(chunk);
                foreach (ICorpusSink sink in sinks)
                {
                    sink.Write(chunk);
                }

                options.Progress?.Report(stats.Documents);
            }

            var outputs = sinks.SelectMany(s => s.Complete()).OrderBy(o => o.Path, StringComparer.Ordinal).ToList();
            WriteManifest(Path.Combine(outputDirectory, ManifestFile), context, stats, outputs, options);
        }
        finally
        {
            foreach (ICorpusSink sink in sinks)
            {
                sink.Dispose();
            }
        }

        return stats;
    }

    /// <summary>
    /// The manifest is itself deterministic: it records no wall-clock time, host or thread count, so two runs of
    /// the same (seed, profile, generator version, text mode) produce byte-identical manifests.
    /// </summary>
    public static void WriteManifest(string path, GenerationContext context, CorpusStatistics stats, IReadOnlyList<CorpusOutputFile> outputs, CorpusRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(options);
        using FileStream file = File.Create(path);
        using var w = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true, Encoder = JsonlCorpusWriter.WriterOptions.Encoder });
        Calibration cal = context.Calibration;
        w.WriteStartObject();
        w.WriteNumber("manifestVersion", ManifestVersion);
        w.WriteStartObject("generator");
        w.WriteString("name", GeneratorInfo.Name);
        w.WriteString("version", GeneratorInfo.Version);
        w.WriteEndObject();
        w.WriteNumber("seed", context.Seed);
        w.WriteString("profileName", context.Profile.Name);
        w.WriteString("profileHash", context.ProfileHash);
        w.WriteBoolean("inlineText", options.InlineText);
        w.WriteStartArray("outputs");
        foreach (CorpusOutputFile o in outputs)
        {
            w.WriteStartObject();
            w.WriteString("path", o.Path);
            w.WriteNumber("bytes", o.Bytes);
            w.WriteString("sha256", o.Sha256);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        stats.WriteTo(w);

        w.WriteStartObject("calibration");
        w.WriteNumber("emailUnitProbability", Round(cal.EmailUnitProbability));
        w.WriteNumber("meanConversationLength", Round(cal.MeanThreadLength));
        w.WriteNumber("emailMeanChildren", Round(cal.EmailMeanChildren));
        w.WriteNumber("emailGeometricMeanChildren", Round(cal.EmailGeometricMean));
        w.WriteNumber("edocMeanChildren", Round(cal.EdocMeanChildren));
        w.WriteNumber("containerMeanChildren", Round(cal.Container.Mean));
        w.WriteNumber("duplicatedFamilyProbability", Round(cal.DuplicatedFamilyProbability));
        w.WriteNumber("meanExtraCopies", Round(cal.MeanExtraCopies));
        w.WriteNumber("withinFamilyDuplicatesPerFamily", Round(cal.WithinFamilyDuplicatesPerFamily));
        w.WriteNumber("textLogMu", Round(cal.TextMu));
        w.WriteNumber("textLogSigma", Round(cal.TextSigma));
        w.WriteEndObject();

        w.WriteStartArray("fieldCatalog");
        foreach (FieldDefinition f in context.Catalog.Fields)
        {
            w.WriteStartObject();
            w.WriteString("name", f.Name);
            w.WriteString("type", f.Type.ToString());
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WritePropertyName("profile");
        using (JsonDocument profileJson = JsonDocument.Parse(ProfileSerializer.ToCanonicalJson(context.Profile)))
        {
            profileJson.WriteTo(w);
        }

        w.WriteEndObject();
    }

    private static double Round(double v) => Math.Round(v, 6, MidpointRounding.ToEven);
}
