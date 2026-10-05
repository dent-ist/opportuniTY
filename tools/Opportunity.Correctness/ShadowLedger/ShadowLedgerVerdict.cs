using System.Text.Json;
using System.Text.Json.Serialization;

namespace Opportunity.Correctness.ShadowLedger;

/// <summary>What one finding is about.</summary>
public enum LedgerFindingKind
{
    /// <summary>After quiescence the index holds an older DocumentVersion than PostgreSQL.</summary>
    StaleVersion,

    /// <summary><c>_version</c> ≠ <c>projectionVersion</c>: a write bypassed external versioning (ADR-001 §2 invariant).</summary>
    ExternalVersionMismatch,

    /// <summary>The index holds a version PostgreSQL never committed (higher than the authoritative one).</summary>
    VersionAhead,

    /// <summary>A sampled document's indexed version went down, or the document vanished, between two observations.</summary>
    VersionRegression,

    /// <summary>A deleted (or purged) document is still in the index after quiescence.</summary>
    Resurrected,

    /// <summary>A live, touched document is not in the index after quiescence.</summary>
    Missing,

    /// <summary>Same version, different coding values (or a foreign <c>workspaceId</c>).</summary>
    ValueMismatch,
}

public sealed record LedgerFinding(
    Guid DocumentId, LedgerFindingKind Kind, long? PostgresVersion, long? IndexedVersion, long? ExternalVersion, string? Detail = null);

/// <summary>The §26 counters, shaped like the result bundle's <c>oracles.shadowLedger</c> (result-bundle.v1).</summary>
public sealed record ShadowLedgerCounters
{
    /// <summary><c>passed</c> or <c>failed</c>.</summary>
    public required string Status { get; init; }

    public required long TouchedDocs { get; init; }

    public required long StaleOverwrites { get; init; }

    public required long VersionRegressions { get; init; }

    public required long MissingDocs { get; init; }

    public required long ValueMismatches { get; init; }

    /// <summary>OpenSearch 409s counted as applied no-ops during the run; null when the run did not measure them.</summary>
    public long? VersionConflictRejections { get; init; }
}

public sealed record LedgerSampling
{
    public required long Passes { get; init; }

    public required long Observations { get; init; }

    public required long DocumentsTracked { get; init; }

    public required long CommittedEventsCaptured { get; init; }
}

public sealed record LedgerReconciliation
{
    public required long Documents { get; init; }

    public required long Batches { get; init; }

    public required int BatchSize { get; init; }

    public required double DurationMs { get; init; }

    public required double DocumentsPerSecond { get; init; }
}

/// <summary>
/// <c>verdict.json</c> of the shadow-ledger oracle (E17-T07; schema <c>shadow-ledger-verdict.v1.schema.json</c>):
/// <see cref="ShadowLedger"/> drops into a result bundle's <c>oracles.shadowLedger</c> unchanged; the rest explains it.
/// </summary>
public sealed record ShadowLedgerVerdict
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Kind { get; init; } = "shadow-ledger";

    public required Guid WorkspaceId { get; init; }

    public required string Candidate { get; init; }

    /// <summary>The run's seed (fault matrix, generator), when there is one.</summary>
    public long? Seed { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required DateTimeOffset EndedUtc { get; init; }

    public required ShadowLedgerCounters ShadowLedger { get; init; }

    public required LedgerSampling Sampling { get; init; }

    public LedgerReconciliation? Reconciliation { get; init; }

    /// <summary>The first findings (bounded), for diagnosis.</summary>
    public required IReadOnlyList<LedgerFinding> Findings { get; init; }

    [JsonIgnore]
    public bool Passed => ShadowLedger.Status == "passed";

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower), new UtcConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public async Task WriteAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, ToJson() + "\n", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One line for test output and logs.</summary>
    public override string ToString() =>
        $"shadow ledger {ShadowLedger.Status}: touched {ShadowLedger.TouchedDocs}, staleOverwrites {ShadowLedger.StaleOverwrites}, " +
        $"versionRegressions {ShadowLedger.VersionRegressions}, missingDocs {ShadowLedger.MissingDocs}, valueMismatches {ShadowLedger.ValueMismatches}, " +
        $"versionConflictRejections {ShadowLedger.VersionConflictRejections?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}" +
        (Findings.Count == 0 ? string.Empty : "; first: " + string.Join(", ", Findings.Take(3).Select(f => $"{f.Kind} {f.DocumentId} pg={f.PostgresVersion} os={f.IndexedVersion}/{f.ExternalVersion}")));

    /// <summary>Timestamps as RFC 3339 UTC with a trailing Z (the bundle schema's <c>utc</c> format).</summary>
    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }
}
