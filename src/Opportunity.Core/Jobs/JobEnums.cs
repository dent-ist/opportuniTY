namespace Opportunity.Core.Jobs;

/// <summary>Job lifecycle (ADR-010 §2). Stored as text; see <see cref="JobStateMachine"/> for the transitions.</summary>
public enum JobStatus
{
    Created,
    Preparing,
    Running,
    Paused,
    Cancelling,
    Cancelled,
    Completed,
    CompletedWithErrors,
    Failed,
}

/// <summary>Chunk lifecycle (ADR-010 §2). Stored as smallint; values are fixed forever.</summary>
public enum JobChunkStatus : short
{
    Pending = 1,
    Dispatched = 2,
    Running = 3,
    RetryWait = 4,
    Committed = 5,
    Failed = 6,
    Cancelled = 7,
}

/// <summary>Server-side operations that run as chunked jobs (ADR-002 §4, ADR-010 §6). Stored as text.</summary>
public enum JobType
{
    Import,
    BulkCoding,
    RelationshipFixup,
    Reindex,
    Export,
    Production,
    Render,

    /// <summary>A search term report run (E07-T10): counts term hits over a Report snapshot.</summary>
    SearchTermReport,
}

/// <summary>
/// The <c>OperationKind</c> of the chunk idempotency key (ADR-010 §5.1). The names are part of the key formula: never
/// rename a member.
/// </summary>
public enum ChunkOperationKind
{
    ImportChunk,
    BulkCodingChunk,
    RelationshipChunk,
    IndexChunk,
    ReindexChunk,
    ExportChunk,
    ProductionChunk,
    RenderChunk,

    /// <summary>Search term report chunks run in the API host (they need the search service), never through a queue.</summary>
    SearchTermReportChunk,

    /// <summary>
    /// A range of a finalized production's members written to its volume (E12-T05). Runs in the rendering worker, which
    /// owns the render sandbox where produced pages are decoded, redacted and endorsed.
    /// </summary>
    ProductionVolumeChunk,
}

/// <summary>How a chunk names its members (ADR-010 §4). Stored as smallint; values are fixed forever.</summary>
public enum ChunkMembershipKind : short
{
    /// <summary>Ordinals <c>From…To</c> of a materialized snapshot (ADR-002 §6).</summary>
    SnapshotRange = 1,

    /// <summary>Rows <c>From…To</c> of an import batch; the chunk writes its own <c>ImportBatchMember</c> rows.</summary>
    ImportRows = 2,

    /// <summary>Current documents in a DocumentId key range, for one projection generation (reindex).</summary>
    DocumentKeyRange = 3,

    /// <summary>An explicit list of at most <see cref="ChunkMembership.MaxExplicitIds"/> document ids.</summary>
    ExplicitIds = 4,
}

/// <summary>Why a chunk attempt failed (ADR-010 §7). Stored as smallint; values are fixed forever.</summary>
public enum ChunkErrorClass : short
{
    /// <summary>Timeouts, serialization failures, unavailable dependencies: retried with backoff.</summary>
    Transient = 1,

    /// <summary>Validation, mapping or envelope/PG mismatch: failed immediately.</summary>
    Permanent = 2,

    /// <summary>The attempts ran out without an error being recorded (crash loop, expired leases).</summary>
    AttemptsExhausted = 3,
}

/// <summary>Per-item outcomes that are not plain success (ADR-010 §1, Q-07, Q-15). Stored as smallint.</summary>
public enum JobItemResultKind : short
{
    /// <summary>Q-07: someone else changed the field after the job's start; the document was left unchanged.</summary>
    SkippedConcurrentEdit = 1,

    /// <summary>Q-15: the initiator can no longer access the document; it was excluded.</summary>
    ExcludedNoAccess = 2,

    /// <summary>The item was invalid; the rest of the chunk still committed.</summary>
    Failed = 3,
}
