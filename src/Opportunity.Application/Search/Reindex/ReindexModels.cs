using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Opportunity.Application.Search.Indexing;

namespace Opportunity.Application.Search.Reindex;

/// <summary>
/// Where a reindex is (E07-T11, ADR-006 R13). Stored as smallint; values are fixed forever. <see cref="Pending"/> to
/// <see cref="Switching"/> are in flight (at most one per workspace); the rest only finish the old or aborted copy.
/// </summary>
public enum ReindexPhase : short
{
    /// <summary>Job submitted; the coordinator creates the target and starts dual-target writes next (R13 steps 1–2).</summary>
    Pending = 1,

    /// <summary>Writers dual-target; the backfill waits until no process still writes from a placement cached before that.</summary>
    Building = 2,

    /// <summary>Reindex tasks fill the target from PostgreSQL by DocumentId key range (R13 step 3).</summary>
    Backfilling = 3,

    /// <summary>The target is compared with PostgreSQL: documents, versions and projection hashes (R13 step 5).</summary>
    Validating = 4,

    /// <summary>Validation passed; reads move to the target in one alias switch (R13 steps 4 and 6).</summary>
    Switching = 5,

    /// <summary>Reads and writes use the new location; the old one is write-blocked once every cached placement expired.</summary>
    Switched = 6,

    /// <summary>The old location is read-only and kept for rollback and open readers until it is deleted (R13 step 7).</summary>
    Retaining = 7,

    /// <summary>The old location is deleted.</summary>
    Completed = 8,

    /// <summary>The rebuild was abandoned and the current placement keeps serving; the target is dropped once more after the cache TTL.</summary>
    Aborting = 9,

    /// <summary>The abandoned target is gone.</summary>
    Aborted = 10,
}

public static class ReindexPhases
{
    /// <summary>The phases that hold the workspace's one in-flight rebuild.</summary>
    public static bool IsInFlight(ReindexPhase phase) => phase is >= ReindexPhase.Pending and <= ReindexPhase.Switching;

    /// <summary>Nothing is left to do.</summary>
    public static bool IsFinished(ReindexPhase phase) => phase is ReindexPhase.Completed or ReindexPhase.Aborted;
}

/// <summary>What a reindex builds (frozen into the job's parameters at submission).</summary>
/// <param name="Kind">Target tier; null keeps the current one (Dedicated moves a shared workspace to its own index).</param>
/// <param name="Generation">Target ProjectionGeneration; null means the generation this build projects.</param>
/// <param name="PrimaryShards">Dedicated target primaries; null derives them from the workspace's size estimate.</param>
public sealed record ReindexRequest(IndexPlacementKind? Kind = null, int? Generation = null, int? PrimaryShards = null)
{
    public const int MaxPrimaryShards = 1024;

    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind?.ToString(),
        ["generation"] = Generation,
        ["primaryShards"] = PrimaryShards,
    };

    public static ReindexRequest Parse(JsonObject? parameters)
    {
        if (parameters is null)
        {
            return new ReindexRequest();
        }

        var kind = parameters["kind"]?.GetValue<string>() is { } k ? Enum.Parse<IndexPlacementKind>(k) : (IndexPlacementKind?)null;
        return new ReindexRequest(kind, parameters["generation"]?.GetValue<int>(), parameters["primaryShards"]?.GetValue<int>());
    }
}

/// <summary>
/// What validation found (R13 step 5): the target compared with PostgreSQL document by document (all documents up to
/// <c>FullCheckMaxDocuments</c>, sampled key ranges above), and the projection rebuilt from PostgreSQL compared with the
/// target's stored source for documents whose versions agree.
/// </summary>
public sealed record ReindexValidation
{
    public long PostgresDocuments { get; init; }

    public long IndexDocuments { get; init; }

    public bool FullCheck { get; init; }

    public long DocumentsCompared { get; init; }

    /// <summary>Documents that disagreed at first sight and were checked again once the watermark covered them.</summary>
    public long Rechecked { get; init; }

    /// <summary>Indexed at an older version than PostgreSQL holds (a lost update).</summary>
    public long StaleDocuments { get; init; }

    /// <summary>Live in PostgreSQL, absent from the target.</summary>
    public long MissingDocuments { get; init; }

    /// <summary>In the target but deleted or unknown in PostgreSQL.</summary>
    public long UnexpectedDocuments { get; init; }

    public long HashesCompared { get; init; }

    public long HashMismatches { get; init; }

    /// <summary>Up to 20 documents that failed, for diagnosis.</summary>
    public IReadOnlyList<Guid> Examples { get; init; } = [];

    public string? Failure { get; init; }

    public bool Passed => Failure is null;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonObject ToJson() => (JsonObject)JsonSerializer.SerializeToNode(this, Json)!;

    public static ReindexValidation? FromJson(JsonObject? json) => json?.Deserialize<ReindexValidation>(Json);

    public override string ToString() => Failure ?? string.Create(CultureInfo.InvariantCulture,
        $"{DocumentsCompared} documents compared ({(FullCheck ? "all" : "sampled")}), {HashesCompared} projection hashes equal");
}

/// <summary>The resumable state of one reindex job (<c>search_reindex</c>). Locations are placement facts, never names.</summary>
public sealed record ReindexRun
{
    public required Guid WorkspaceId { get; init; }

    public required Guid JobId { get; init; }

    public required ReindexPhase Phase { get; init; }

    public required ReindexRequest Request { get; init; }

    /// <summary>Where reads came from when the rebuild began (the location retired by the switch).</summary>
    public IndexLocation? Source { get; init; }

    /// <summary>The location being built.</summary>
    public IndexLocation? Target { get; init; }

    /// <summary>Earliest time of the phase's next timed step (backfill start, write block, deletion, second drop).</summary>
    public DateTimeOffset? NextStepAt { get; init; }

    /// <summary>When the retired location is deleted.</summary>
    public DateTimeOffset? RetainUntil { get; init; }

    public long? DocumentsPlanned { get; init; }

    public ReindexValidation? Validation { get; init; }

    public string? Error { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? SwitchedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>The job's status reason while the run is in this phase (the job monitor shows it).</summary>
    public string StatusReason => Phase switch
    {
        ReindexPhase.Pending => "Starting: creating the new index",
        ReindexPhase.Building => "Building: new and current index both receive changes",
        ReindexPhase.Backfilling => "Backfilling the new index from the database",
        ReindexPhase.Validating => "Validating the new index",
        ReindexPhase.Switching => "Switching searches to the new index",
        ReindexPhase.Switched or ReindexPhase.Retaining => "Switched; the previous index is kept read-only until " +
            (RetainUntil?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "its retention ends"),
        ReindexPhase.Completed => "Switched; the previous index is deleted",
        _ => Error is null ? "Aborted; the current index keeps serving" : "Aborted; the current index keeps serving: " + Error,
    };
}

/// <summary>A document's projection state as PostgreSQL holds it.</summary>
public readonly record struct DocumentVersionState(Guid DocumentId, long Version, bool Deleted);

/// <summary>Projection states read in one snapshot with the workspace's SearchGeneration counter of that snapshot.</summary>
public sealed record DocumentVersionPage(IReadOnlyList<DocumentVersionState> Documents, long Generation);

/// <summary>The reindex index tasks of a job.</summary>
public sealed record ReindexTaskProgress(long Total, long Applied, long Failed)
{
    public long Unapplied => Total - Applied;
}

public enum ReindexCreateOutcome
{
    Created,

    /// <summary>The run of this job already exists (a retried submission).</summary>
    Existing,

    /// <summary>Another reindex of the workspace is in flight.</summary>
    Conflict,
}
