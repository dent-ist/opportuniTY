using System.Text.Json.Nodes;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Infrastructure;

using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Opportunity.Benchmarks.Gates;

#pragma warning disable CA2227 // YamlDotNet populates collections through setters.

/// <summary>The parsed <c>gates.yaml</c> (§26 decision gates, Q-04/Q-44 policy).</summary>
public sealed class GatesDocument
{
    public int SchemaVersion { get; set; }

    public string GatesVersion { get; set; } = string.Empty;

    /// <summary>draft until the product owner and lead architect sign off; then frozen (tighten-only).</summary>
    public string Status { get; set; } = string.Empty;

    public SignOff SignOff { get; set; } = new();

    public GatesPolicy Policy { get; set; } = new();

    public List<GateDefinition> Gates { get; set; } = [];
}

public sealed class SignOff
{
    public string? ProductOwner { get; set; }

    public string? LeadArchitect { get; set; }

    public string? FrozenUtc { get; set; }
}

public sealed class GatesPolicy
{
    public bool TightenOnly { get; set; }

    public bool CorrectnessBeforePerformance { get; set; }

    public RepetitionPolicy Repetitions { get; set; } = new();

    public bool CacheStateStated { get; set; }

    public CalibrationPolicy Calibration { get; set; } = new();

    public string OfferedBulkRate { get; set; } = string.Empty;

    public ValidityPolicy Validity { get; set; } = new();

    public Dictionary<string, string> AbsoluteGatesByProfile { get; set; } = [];

    public MaterialAdvantagePolicy MaterialAdvantage { get; set; } = new();

    [YamlMember(Alias = "materialFailureAt10M")]
    public MaterialFailurePolicy MaterialFailureAt10M { get; set; } = new();

    public string Preference { get; set; } = string.Empty;
}

public sealed class RepetitionPolicy
{
    public int Minimum { get; set; }

    public string Rule { get; set; } = string.Empty;
}

public sealed class CalibrationPolicy
{
    public double IdleBaselineP95MaxCoefficientOfVariation { get; set; }

    public int Runs { get; set; }
}

public sealed class ValidityPolicy
{
    public double MaxDroppedIterationsRatio { get; set; }

    public double MaxLoadGeneratorCpuPercent { get; set; }
}

public sealed class MaterialAdvantagePolicy
{
    public double P95ImprovementMin { get; set; }

    public double BulkThroughputRatioMin { get; set; }

    public string Consistency { get; set; } = string.Empty;
}

public sealed class MaterialFailurePolicy
{
    public bool AnyMissedGate { get; set; }

    [YamlMember(Alias = "p95GrowthFrom1MMax")]
    public double P95GrowthFrom1MMax { get; set; }

    public bool SuperLinearDiskOrMergeTrend { get; set; }
}

public sealed class GateDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Enforcement { get; set; } = string.Empty;

    public string? Statistic { get; set; }

    public string? Unit { get; set; }

    public string? Baseline { get; set; }

    public List<string> Sources { get; set; } = [];

    public string? Histogram { get; set; }

    public GateThreshold? Threshold { get; set; }

    public double? Target { get; set; }

    public GateRequirement? Requires { get; set; }

    /// <summary>Every bundle path this gate reads.</summary>
    public IEnumerable<string> Paths()
    {
        foreach (string source in Sources)
        {
            yield return source;
        }

        if (Baseline is not null)
        {
            yield return Baseline;
        }

        if (Histogram is not null)
        {
            yield return Histogram;
        }

        if (Requires?.Oracle is not null)
        {
            yield return Requires.Oracle;
        }
    }
}

public sealed class GateThreshold
{
    public double? Max { get; set; }

    public double? Min { get; set; }
}

public sealed class GateRequirement
{
    public string? Oracle { get; set; }
}

#pragma warning restore CA2227

/// <summary>Loads, hashes and checks <c>gates.yaml</c>.</summary>
public sealed class GatesFile
{
    public const string FileName = "gates.yaml";

    public static IReadOnlySet<string> Kinds { get; } = new HashSet<string>(StringComparer.Ordinal) { "max", "min", "relative-degradation", "report" };

    public static IReadOnlySet<string> Enforcements { get; } = new HashSet<string>(StringComparer.Ordinal) { "hard", "by-profile", "informational" };

    public static IReadOnlySet<string> AbsoluteModes { get; } = new HashSet<string>(StringComparer.Ordinal) { "hard", "comparative" };

    private GatesFile(string path, string sha256, GatesDocument document)
    {
        Path = path;
        Sha256 = sha256;
        Document = document;
    }

    public string Path { get; }

    /// <summary>SHA-256 of the file bytes, as stated in every report and bundle.</summary>
    public string Sha256 { get; }

    public GatesDocument Document { get; }

    public bool IsFrozen => Document.Status == "frozen";

    public static GatesFile Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        GatesDocument document;
        try
        {
            document = deserializer.Deserialize<GatesDocument>(System.Text.Encoding.UTF8.GetString(bytes))
                ?? throw new FormatException($"{path} is empty.");
        }
        catch (YamlException ex)
        {
            throw new FormatException($"{path}: {ex.Message}", ex);
        }

        return new GatesFile(path, BenchJson.Sha256Hex(bytes), document);
    }

    /// <summary>Locates tools/Opportunity.Benchmarks/gates.yaml from the repository, else next to the binaries.</summary>
    public static string? Locate()
    {
        string? versions = Capture.VersionsEnvFile.Locate();
        if (versions is not null)
        {
            string candidate = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(versions)!, "tools", "Opportunity.Benchmarks", FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string local = System.IO.Path.Combine(AppContext.BaseDirectory, FileName);
        return File.Exists(local) ? local : null;
    }

    /// <summary>The bundle's reference to this file, copied into the bundle as <paramref name="bundledPath"/>.</summary>
    public GatesReference ToReference(string bundledPath = FileName) => new()
    {
        Path = bundledPath,
        Sha256 = Sha256,
        GatesVersion = Document.GatesVersion,
        Status = IsFrozen ? GatesStatus.Frozen : GatesStatus.Draft,
    };

    /// <summary>Structural rules: unique ids, known kinds, thresholds present, sign-off complete when frozen, Q-44 modes.</summary>
    public IReadOnlyList<string> Check()
    {
        var errors = new List<string>();
        GatesDocument d = Document;
        if (d.SchemaVersion != 1)
        {
            errors.Add($"schemaVersion {d.SchemaVersion} is not supported (expected 1).");
        }

        if (d.Status is not ("draft" or "frozen"))
        {
            errors.Add($"status '{d.Status}' must be draft or frozen.");
        }

        if (IsFrozen && (string.IsNullOrWhiteSpace(d.SignOff.ProductOwner) || string.IsNullOrWhiteSpace(d.SignOff.LeadArchitect) || string.IsNullOrWhiteSpace(d.SignOff.FrozenUtc)))
        {
            errors.Add("a frozen gates file needs signOff.productOwner, signOff.leadArchitect and signOff.frozenUtc (Q-04).");
        }

        if (!d.Policy.TightenOnly)
        {
            errors.Add("policy.tightenOnly must be true (Q-04).");
        }

        foreach (string profile in new[] { "developer-regression", "enterprise-reference" })
        {
            if (!d.Policy.AbsoluteGatesByProfile.TryGetValue(profile, out string? mode) || !AbsoluteModes.Contains(mode))
            {
                errors.Add($"policy.absoluteGatesByProfile.{profile} must be one of: {string.Join(", ", AbsoluteModes)} (Q-44).");
            }
        }

        if (d.Gates.Count == 0)
        {
            errors.Add("no gates defined.");
        }

        foreach (IGrouping<string, GateDefinition> duplicate in d.Gates.GroupBy(g => g.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"gate id '{duplicate.Key}' is defined {duplicate.Count()} times.");
        }

        foreach (GateDefinition gate in d.Gates)
        {
            string where = $"gate '{gate.Id}'";
            if (string.IsNullOrWhiteSpace(gate.Id))
            {
                errors.Add("a gate has no id.");
            }

            if (!Kinds.Contains(gate.Kind))
            {
                errors.Add($"{where}: kind '{gate.Kind}' is not one of {string.Join(", ", Kinds)}.");
            }

            if (!Enforcements.Contains(gate.Enforcement))
            {
                errors.Add($"{where}: enforcement '{gate.Enforcement}' is not one of {string.Join(", ", Enforcements)}.");
            }

            if (gate.Sources.Count == 0)
            {
                errors.Add($"{where}: no sources.");
            }

            if (gate.Kind == "report")
            {
                if (gate.Enforcement != "informational")
                {
                    errors.Add($"{where}: report gates must be informational.");
                }
            }
            else if (gate.Threshold is null || (gate.Threshold.Max is null && gate.Threshold.Min is null))
            {
                errors.Add($"{where}: threshold.max or threshold.min is required.");
            }

            if (gate.Kind == "relative-degradation" && gate.Baseline is null)
            {
                errors.Add($"{where}: relative-degradation needs a baseline path.");
            }

            if (gate.Category is "correctness" or "security" && gate.Enforcement != "hard")
            {
                errors.Add($"{where}: correctness and security gates are absolute (enforcement: hard) on every profile (§26, Q-44).");
            }

            foreach (string path in gate.Paths())
            {
                try
                {
                    BundlePath.Parse(path);
                }
                catch (FormatException ex)
                {
                    errors.Add($"{where}: {ex.Message}");
                }
            }
        }

        return errors;
    }

    /// <summary>Every gate path must be declared by the result-bundle schema (selector values must satisfy its enums).</summary>
    public IReadOnlyList<string> CheckAgainstSchema(JsonNode bundleSchema)
    {
        ArgumentNullException.ThrowIfNull(bundleSchema);
        var errors = new List<string>();
        foreach (GateDefinition gate in Document.Gates)
        {
            foreach (string path in gate.Paths())
            {
                if (SchemaPaths.Explain(bundleSchema, path) is { } problem)
                {
                    errors.Add($"gate '{gate.Id}': {path}: {problem}");
                }
            }
        }

        return errors;
    }

    /// <summary>Every gate path must select at least one value in <paramref name="bundle"/>.</summary>
    public IReadOnlyList<string> CheckAgainstBundle(JsonNode bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return [.. Document.Gates
            .SelectMany(gate => gate.Paths().Select(path => (gate.Id, Path: path)))
            .Where(x => BundlePath.Resolve(bundle, x.Path).Count == 0)
            .Select(x => $"gate '{x.Id}': {x.Path} selects nothing in the bundle.")];
    }
}
