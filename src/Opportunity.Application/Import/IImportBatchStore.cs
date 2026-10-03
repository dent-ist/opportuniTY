using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;

namespace Opportunity.Application.Import;

/// <summary>
/// Import batches and their chunk writes (E08-T03, ADR-010 §4 <c>ImportRows</c>). A batch is created with its Import
/// job, prepared once by the import worker (whole-file duplicate keys, chunk byte ranges) and then written chunk by
/// chunk: each <see cref="ApplyChunkAsync"/> is one transaction holding the documents, the batch members, the row
/// outcomes, the report counters, exactly one IndexChunkTask (never SearchOutbox rows) and fence F3.
/// </summary>
public interface IImportBatchStore
{
    /// <summary>
    /// Creates the Import job (Created) and its batch in one transaction with <c>Job.Created</c>, <c>Import.Started</c>
    /// and, when coding fields are enabled for overlay, <c>Coding.OverlayEnabled</c> (Q-31). A retry with the same
    /// initiator and client key returns the existing batch.
    /// </summary>
    Task<ImportBatchCreation> CreateAsync(NewImportBatch batch, CancellationToken cancellationToken = default);

    Task<ImportBatchRecord?> GetAsync(Guid workspaceId, Guid importBatchId, CancellationToken cancellationToken = default);

    /// <summary>Batches newest first, keyset-paged.</summary>
    Task<IReadOnlyList<ImportBatchRecord>> ListAsync(
        Guid workspaceId, ImportBatchCursor? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>Batches whose job waits to be prepared or started and whose preparation claim is free or expired, oldest first.</summary>
    Task<IReadOnlyList<Guid>> GetPreparableAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Takes or renews the preparation claim; false while another worker holds it.</summary>
    Task<bool> TryClaimPreparationAsync(
        Guid workspaceId, Guid importBatchId, string owner, TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records first occurrences of control numbers, in row order: a key already recorded keeps its earlier row, so a
    /// re-run of the preparation is idempotent.
    /// </summary>
    Task AddKeysAsync(Guid workspaceId, Guid importBatchId, IReadOnlyList<ImportKey> keys, CancellationToken cancellationToken = default);

    /// <summary>Stores the preparation facts and chunk ranges; false when the batch was already prepared.</summary>
    Task<bool> CompletePreparationAsync(
        Guid workspaceId, Guid importBatchId, ImportPreparation preparation, IReadOnlyList<ImportChunkRange> chunks,
        CancellationToken cancellationToken = default);

    Task<ImportChunkRange?> GetChunkRangeAsync(Guid workspaceId, Guid importBatchId, long rowFrom, CancellationToken cancellationToken = default);

    /// <summary>
    /// One import chunk in one transaction (§12, §21): append/overlay by control number, members, row issues, counters,
    /// one IndexChunkTask and fence F3. <paramref name="chunk"/> must be a leased <c>ImportRows</c> chunk of the
    /// batch's job. When F3 refuses, nothing is written and the chunk is released (or cancelled).
    /// </summary>
    Task<ImportChunkResult> ApplyChunkAsync(ClaimedChunk chunk, ImportChunkWrite write, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>Import.Completed</c> once, for a job that finished without a chunk commit (an empty file).</summary>
    Task RecordCompletedAsync(Guid workspaceId, Guid importBatchId, CancellationToken cancellationToken = default);

    /// <summary>Row errors and warnings in row order, keyset-paged.</summary>
    Task<IReadOnlyList<ImportRowIssueRecord>> GetRowIssuesAsync(
        Guid workspaceId, Guid importBatchId, ImportIssueSeverity? severity, ImportRowIssueCursor? after, int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>A batch to create together with its Import job.</summary>
public sealed record NewImportBatch
{
    public required Guid WorkspaceId { get; init; }

    public Guid ImportBatchId { get; init; } = Guid.CreateVersion7();

    public Guid JobId { get; init; } = Guid.CreateVersion7();

    public required string Name { get; init; }

    public required ImportMode Mode { get; init; }

    public required string SourceFileName { get; init; }

    public required string SourceObjectKey { get; init; }

    public required byte[] SourceSha256 { get; init; }

    public required long SourceSize { get; init; }

    public Guid? ProfileId { get; init; }

    public long? ProfileVersion { get; init; }

    /// <summary>The effective profile (Contracts <see cref="ImportProfileDefinition"/> JSON) the import runs with.</summary>
    public required string ProfileJson { get; init; }

    /// <summary>Q-31: coding/privilege fields enabled for overlay by this import.</summary>
    public IReadOnlyList<int> CodingOverlayFieldIds { get; init; } = [];

    public required Guid InitiatedBy { get; init; }

    public string? ClientIdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>Who started the import from where (actor, client, session, access path); the store fills the rest.</summary>
    public required AuditEvent AuditTemplate { get; init; }
}

public sealed record ImportBatchCreation(ImportBatchRecord Batch, JobInfo Job, bool Created);

public readonly record struct ImportBatchCursor(DateTimeOffset CreatedAt, Guid ImportBatchId);

public readonly record struct ImportRowIssueCursor(long RowNo, int IssueNo);

public sealed record ImportBatchRecord
{
    public required Guid WorkspaceId { get; init; }

    public required Guid ImportBatchId { get; init; }

    public required Guid JobId { get; init; }

    public required string Name { get; init; }

    public required ImportMode Mode { get; init; }

    public required string SourceFileName { get; init; }

    public required string SourceObjectKey { get; init; }

    public required byte[] SourceSha256 { get; init; }

    public long SourceSize { get; init; }

    public Guid? ProfileId { get; init; }

    public long? ProfileVersion { get; init; }

    public required string ProfileJson { get; init; }

    public IReadOnlyList<int> CodingOverlayFieldIds { get; init; } = [];

    /// <summary>Null until the preparation pass completed.</summary>
    public ImportPreparation? Preparation { get; init; }

    public long RowsImported { get; init; }

    public long RowsOverlaid { get; init; }

    public long RowsSkipped { get; init; }

    public long RowsErrored { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? PreparedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>What the preparation pass found; every chunk reads the DAT with exactly these settings.</summary>
/// <param name="DataOffset">Bytes before the first data row (byte-order mark, header, blank lines).</param>
/// <param name="EncodingFallback">The reader's row-level encoding fallback (on when the encoding was detected).</param>
public sealed record ImportPreparation(
    IReadOnlyList<string> Header,
    string DatEncoding,
    bool EncodingFallback,
    long DataOffset,
    long RowsTotal,
    int FieldsCreated,
    int ChoicesCreated);

/// <summary>Data rows <c>RowFrom…RowTo</c> occupy bytes <c>ByteFrom…ByteTo</c> (exclusive) of the DAT.</summary>
public sealed record ImportChunkRange(long RowFrom, long RowTo, long ByteFrom, long ByteTo, long LineFrom);

public sealed record ImportKey(string ControlNumberNorm, long RowNo);

public enum ImportIssueSeverity : short
{
    /// <summary>The row was not loaded.</summary>
    Error = 1,

    /// <summary>The row was loaded; something was adjusted or ignored.</summary>
    Warning = 2,
}

public sealed record ImportRowIssue(ImportIssueSeverity Severity, string Code, string Message, string? Column = null)
{
    public const int MaxCodeLength = 100;
    public const int MaxMessageLength = 2_000;
}

/// <summary>A coding/privilege value an administrator enabled for overlay (Q-31); canonical ADR-003 JSON.</summary>
public sealed record ImportCodingValue(int FieldId, JsonNode Value);

/// <summary>One mapped data row of a chunk.</summary>
public sealed record ImportRow
{
    public required long RowNo { get; init; }

    public long? LineNo { get; init; }

    public string? ControlNumber { get; init; }

    public string? ControlNumberNorm { get; init; }

    /// <summary>
    /// The document as this row describes it: identity, structural columns and <see cref="Document.Metadata"/> /
    /// <see cref="Document.MetadataRaw"/> holding only the values the row supplies. Null for a row with errors.
    /// </summary>
    public Document? Document { get; init; }

    /// <summary>Document columns (<c>opportunity.document</c> names) the row supplies; overlay updates only these.</summary>
    public IReadOnlySet<string> SuppliedColumns { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyList<ImportCodingValue> Coding { get; init; } = [];

    /// <summary>The upstream duplicate group the row names (E09-T02); the chunk records it with its documents.</summary>
    public DuplicateGroupKey? DuplicateGroup { get; init; }

    /// <summary>The upstream (or ConversationIndex) email thread the row names (E09-T02).</summary>
    public EmailThreadKey? EmailThread { get; init; }

    public IReadOnlyList<ImportRowIssue> Issues { get; init; } = [];

    public bool HasErrors => Document is null || Issues.Any(i => i.Severity == ImportIssueSeverity.Error);
}

public sealed record ImportChunkWrite(Guid ImportBatchId, ImportMode Mode, IReadOnlyList<ImportRow> Rows);

/// <param name="Commit">Fence F3: the chunk's writes happened only when committed.</param>
public sealed record ImportChunkResult(
    ChunkCommitResult Commit, int Imported, int Overlaid, int Skipped, int Errored, Guid? IndexTaskId)
{
    public bool Committed => Commit.Committed;
}

public sealed record ImportRowIssueRecord(
    long RowNo,
    int IssueNo,
    ImportIssueSeverity Severity,
    long? LineNo,
    string? ControlNumber,
    string? Column,
    string Code,
    string Message);
