using System.Text.RegularExpressions;

using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Opportunity.Benchmarks.Regression;

#pragma warning disable CA2227 // YamlDotNet populates collections through setters.

/// <summary>The parsed <c>scale-regression.yaml</c>: relative tolerance band, rolling baseline and alert rule (E18-T06).</summary>
public sealed class RegressionPolicy
{
    public const string FileName = "scale-regression.yaml";

    public int SchemaVersion { get; set; }

    public double Tolerance { get; set; } = 0.15;

    public BaselinePolicy Baseline { get; set; } = new();

    public int SustainedRuns { get; set; } = 2;

    public int RetentionDays { get; set; } = 90;

    public List<MetricRule> Metrics { get; set; } = [];

    public static RegressionPolicy Load(string path)
    {
        IDeserializer deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        try
        {
            RegressionPolicy policy = deserializer.Deserialize<RegressionPolicy>(File.ReadAllText(path)) ?? throw new FormatException($"{path} is empty.");
            policy.Check(path);
            return policy;
        }
        catch (YamlException ex)
        {
            throw new FormatException($"{path}: {ex.Message}", ex);
        }
    }

    /// <summary>The repository file (tools/Opportunity.Benchmarks/scale-regression.yaml), else the copy next to the binaries.</summary>
    public static string? Locate()
    {
        string? gates = Gates.GatesFile.Locate();
        string? candidate = gates is null ? null : Path.Combine(Path.GetDirectoryName(gates)!, FileName);
        if (candidate is not null && File.Exists(candidate))
        {
            return candidate;
        }

        string local = Path.Combine(AppContext.BaseDirectory, FileName);
        return File.Exists(local) ? local : null;
    }

    /// <summary>The first rule whose pattern matches; metrics no rule matches are recorded but never gated.</summary>
    public MetricRule RuleFor(string metric) =>
        Metrics.FirstOrDefault(r => r.Matches(metric)) ?? new MetricRule { Pattern = "**", Gated = false };

    private void Check(string path)
    {
        var errors = new List<string>();
        if (SchemaVersion != 1)
        {
            errors.Add($"schemaVersion {SchemaVersion} is not supported (1).");
        }

        if (Tolerance is <= 0 or >= 1)
        {
            errors.Add("tolerance must be between 0 and 1.");
        }

        if (Baseline.Window < 1 || Baseline.MinimumRuns < 1 || Baseline.MinimumRuns > Baseline.Window)
        {
            errors.Add("baseline: need 1 <= minimumRuns <= window.");
        }

        if (SustainedRuns < 1)
        {
            errors.Add("sustainedRuns must be >= 1.");
        }

        if (RetentionDays < 1)
        {
            errors.Add("retentionDays must be >= 1.");
        }

        foreach (MetricRule rule in Metrics)
        {
            if (rule.Direction is not (MetricRule.LowerIsBetter or MetricRule.HigherIsBetter))
            {
                errors.Add($"metric '{rule.Pattern}': direction must be {MetricRule.LowerIsBetter} or {MetricRule.HigherIsBetter}.");
            }

            if (rule.Tolerance is <= 0 or >= 1)
            {
                errors.Add($"metric '{rule.Pattern}': tolerance must be between 0 and 1.");
            }
        }

        if (errors.Count > 0)
        {
            throw new FormatException($"{path}: " + string.Join(" ", errors));
        }
    }
}

public sealed class BaselinePolicy
{
    public int Window { get; set; } = 7;

    public int MinimumRuns { get; set; } = 3;
}

public sealed class MetricRule
{
    public const string LowerIsBetter = "lower-is-better";
    public const string HigherIsBetter = "higher-is-better";

    private Regex? _regex;

    public string Pattern { get; set; } = "**";

    public string Direction { get; set; } = LowerIsBetter;

    public bool Gated { get; set; }

    /// <summary>Overrides the policy tolerance for this rule.</summary>
    public double? Tolerance { get; set; }

    /// <summary>A change smaller than this (in the metric's unit) never counts as a regression.</summary>
    public double MinAbsoluteChange { get; set; }

    public bool IsHigherBetter => Direction == HigherIsBetter;

    public bool Matches(string metric)
    {
        _regex ??= new Regex(
            "^" + string.Join(@"\.", Pattern.Split('.').Select(part => part switch
            {
                "**" => ".+",
                _ => Regex.Escape(part).Replace(@"\*", "[^.]*", StringComparison.Ordinal),
            })) + "$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return _regex.IsMatch(metric);
    }
}

#pragma warning restore CA2227
