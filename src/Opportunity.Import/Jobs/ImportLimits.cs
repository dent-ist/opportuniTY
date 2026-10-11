using System.Globalization;

using Microsoft.Extensions.Configuration;

using Opportunity.Import.LoadFiles;

namespace Opportunity.Import.Jobs;

/// <summary>
/// Configurable ingest limits (ADR-015 D15.2, SEC-15, E08-T09), section <c>Import:Limits</c>. The DAT record and field
/// limits are frozen with an import's preparation (every chunk and the error file re-read the DAT with the limits that
/// planned it). A native over <see cref="MaxNativeBytes"/> fails its row (<c>native-too-large</c>). An archive native
/// (ZIP, including Office Open XML) whose declared expansion ratio, entry count or nesting depth exceeds the limits is
/// quarantined as Unscannable. Violations go to the row errors and the error file; they never fail the chunk.
/// </summary>
public sealed class ImportLimits
{
    public const string SectionName = "Import:Limits";

    /// <summary>Largest DAT record in bytes; longer records are rejected without buffering.</summary>
    public int MaxRecordBytes { get; init; } = new DatReaderOptions().MaxRecordBytes;

    /// <summary>Most fields per DAT record.</summary>
    public int MaxFieldCount { get; init; } = new DatReaderOptions().MaxFieldCount;

    /// <summary>Largest native file stored for one row (4 GiB).</summary>
    public long MaxNativeBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Largest total uncompressed size of an archive relative to its file size (100:1).</summary>
    public double MaxArchiveRatio { get; init; } = 100;

    /// <summary>Most entries in an archive, nested archives included.</summary>
    public int MaxArchiveEntries { get; init; } = 100_000;

    /// <summary>Deepest nesting of archives in archives (the native itself is depth 1).</summary>
    public int MaxArchiveDepth { get; init; } = 5;

    /// <summary>Largest nested archive that is expanded in memory to inspect its own entries.</summary>
    public long MaxNestedArchiveBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Binds <c>Import:Limits:*</c>; missing values keep the defaults.</summary>
    public static ImportLimits FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var defaults = new ImportLimits();
        var limits = new ImportLimits
        {
            MaxRecordBytes = Read(section, nameof(MaxRecordBytes), defaults.MaxRecordBytes, int.Parse),
            MaxFieldCount = Read(section, nameof(MaxFieldCount), defaults.MaxFieldCount, int.Parse),
            MaxNativeBytes = Read(section, nameof(MaxNativeBytes), defaults.MaxNativeBytes, long.Parse),
            MaxArchiveRatio = Read(section, nameof(MaxArchiveRatio), defaults.MaxArchiveRatio, double.Parse),
            MaxArchiveEntries = Read(section, nameof(MaxArchiveEntries), defaults.MaxArchiveEntries, int.Parse),
            MaxArchiveDepth = Read(section, nameof(MaxArchiveDepth), defaults.MaxArchiveDepth, int.Parse),
            MaxNestedArchiveBytes = Read(section, nameof(MaxNestedArchiveBytes), defaults.MaxNestedArchiveBytes, long.Parse),
        };
        limits.Validate();
        return limits;
    }

    public void Validate()
    {
        var errors = new List<string>();
        if (MaxRecordBytes < 16)
        {
            errors.Add("MaxRecordBytes must be at least 16");
        }

        if (MaxFieldCount < 1)
        {
            errors.Add("MaxFieldCount must be at least 1");
        }

        if (MaxNativeBytes < 1)
        {
            errors.Add("MaxNativeBytes must be at least 1");
        }

        if (!(MaxArchiveRatio >= 1))
        {
            errors.Add("MaxArchiveRatio must be at least 1");
        }

        if (MaxArchiveEntries < 1 || MaxArchiveDepth < 1 || MaxNestedArchiveBytes is < 0 or > 1024L * 1024 * 1024)
        {
            errors.Add("MaxArchiveEntries and MaxArchiveDepth must be at least 1, MaxNestedArchiveBytes between 0 and 1 GiB");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid {SectionName} settings: {string.Join("; ", errors)}.");
        }
    }

    private static T Read<T>(IConfigurationSection section, string key, T fallback, Func<string, IFormatProvider, T> parse)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return parse(value.Trim(), CultureInfo.InvariantCulture);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{SectionName}:{key} '{value}' is not a valid number.", ex);
        }
    }
}
