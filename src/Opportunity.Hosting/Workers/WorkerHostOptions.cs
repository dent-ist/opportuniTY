using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Opportunity.Hosting.Workers;

/// <summary>Worker types a worker process can host. Names are the values accepted in <c>Workers:Enabled</c>.</summary>
public static class WorkerTypes
{
    public const string Import = "import";
    public const string Indexing = "indexing";
    public const string Rendering = "rendering";
    public const string Export = "export";
    public const string Production = "production";
    public const string Dispatcher = "dispatcher";
    public const string BulkCoding = "bulk-coding";

    /// <summary>Shorthand for every worker type (Lite profile).</summary>
    public const string AllKeyword = "all";

    public static IReadOnlyList<string> All { get; } =
        [Import, Indexing, Rendering, Export, Production, Dispatcher, BulkCoding];
}

public sealed class WorkerHostOptions
{
    public const string SectionName = "Workers";

    /// <summary>
    /// Comma-separated worker types to run in this process, or <c>all</c>. A single string rather than an array so an
    /// environment variable (<c>Workers__Enabled=import,indexing</c>) replaces the host default instead of merging
    /// with it index by index.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Enabled { get; set; } = string.Empty;

    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Parses <see cref="Enabled"/>; unknown names are returned in <paramref name="unknown"/>.</summary>
    public static IReadOnlyList<string> Parse(string? enabled, out IReadOnlyList<string> unknown)
    {
        var names = (enabled ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (names.Contains(WorkerTypes.AllKeyword))
        {
            unknown = names.Count == 1 ? [] : ["'all' cannot be combined with other worker types"];
            return WorkerTypes.All;
        }

        unknown = [.. names.Where(n => !WorkerTypes.All.Contains(n))];
        return [.. WorkerTypes.All.Where(names.Contains)];
    }
}

internal sealed class WorkerHostOptionsValidator : IValidateOptions<WorkerHostOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkerHostOptions options)
    {
        var enabled = WorkerHostOptions.Parse(options.Enabled, out var unknown);

        if (unknown.Count > 0)
        {
            return ValidateOptionsResult.Fail(
                $"Workers:Enabled contains unknown worker types [{string.Join(", ", unknown)}]; " +
                $"valid values are '{WorkerTypes.AllKeyword}' or a comma-separated subset of [{string.Join(", ", WorkerTypes.All)}].");
        }

        return enabled.Count == 0
            ? ValidateOptionsResult.Fail("Workers:Enabled must name at least one worker type.")
            : ValidateOptionsResult.Success;
    }
}
