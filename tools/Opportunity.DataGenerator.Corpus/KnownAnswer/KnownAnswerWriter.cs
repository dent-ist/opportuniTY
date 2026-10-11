using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Opportunity.DataGenerator.Corpus.KnownAnswer;

/// <summary>A written fixture file: path relative to the output directory, size and SHA-256.</summary>
public sealed record KnownAnswerFile(string Path, long Bytes, string Sha256);

/// <summary>
/// Writes the E17-T09 known-answer fixtures: <c>str-fixture.json</c> (terms and documents with their text and plants),
/// <c>str-ground-truth.json</c> (expected report numbers and hit sets), <c>qc-fixture.json</c> (documents and the state
/// to seed), <c>qc-expected.json</c> (expected check results, blocking and warning counts) and
/// <c>known-answer-manifest.json</c> (seed, versions, file hashes). Output is a pure function of the seed and options:
/// stable property order, invariant formatting, <c>\n</c> line ends, no timestamps.
/// </summary>
public static class KnownAnswerWriter
{
    public const string StrFixtureFile = "str-fixture.json";
    public const string StrGroundTruthFile = "str-ground-truth.json";
    public const string QcFixtureFile = "qc-fixture.json";
    public const string QcExpectedFile = "qc-expected.json";
    public const string ManifestFile = "known-answer-manifest.json";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IReadOnlyList<KnownAnswerFile> Write(string outputDirectory, ulong seed, StrFixtureOptions? str = null, QcFixtureOptions? qc = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var strFixture = StrFixtureGenerator.Generate(seed, str);
        var qcFixture = QcFixtureGenerator.Generate(seed, qc);
        var files = new List<KnownAnswerFile>
        {
            WriteJson(outputDirectory, StrFixtureFile, strFixture),
            WriteJson(outputDirectory, StrGroundTruthFile, StrFixtureGenerator.GroundTruth(strFixture)),
            WriteJson(outputDirectory, QcFixtureFile, qcFixture),
            WriteJson(outputDirectory, QcExpectedFile, QcFixtureGenerator.Expected(qcFixture)),
        };
        var manifest = new KnownAnswerManifest(GeneratorInfo.Name, GeneratorInfo.Version, StrFixtureGenerator.Version, QcFixtureGenerator.Version, seed, files);
        files.Add(WriteJson(outputDirectory, ManifestFile, manifest));
        return files;
    }

    public static byte[] Serialize<T>(T value) => [.. JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), (byte)'\n'];

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException($"{path} is empty.");

    private static KnownAnswerFile WriteJson<T>(string directory, string name, T value)
    {
        var bytes = Serialize(value);
        File.WriteAllBytes(Path.Combine(directory, name), bytes);
        return new KnownAnswerFile(name, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private sealed record KnownAnswerManifest(
        string Generator, string GeneratorVersion, string StrFixtureVersion, string QcFixtureVersion, ulong Seed, IReadOnlyList<KnownAnswerFile> Files);
}
