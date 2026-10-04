using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;

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

    /// <summary>
    /// Writes <c>Import.Completed</c> once, for a job that finished without a chunk commit (an empty file, or a
    /// preparation that failed; <paramref name="reasonCode"/> then names why).
    /// </summary>
    Task RecordCompletedAsync(Guid workspaceId, Guid importBatchId, string? reasonCode = null, CancellationToken cancellationToken = default);

    /// <summary>First rows of normalized Beg Bates values (OPT matched by Beg Bates); idempotent like <see cref="AddKeysAsync"/>.</summary>
    Task AddBatesKeysAsync(Guid workspaceId, Guid importBatchId, IReadOnlyList<ImportKey> keys, CancellationToken cancellationToken = default);

    /// <summary>Stages OPT rows read by the preparation pass; rows already staged (a re-run) are kept.</summary>
    Task AddImageRowsAsync(Guid workspaceId, Guid importBatchId, IReadOnlyList<ImportImageRow> rows, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns every staged OPT document to the row it belongs to (the first OPT document wins a key) and records a
    /// warning on the first OPT row of each document that belongs to none (orphan, duplicate key, rows before the first
    /// document break). Idempotent.
    /// </summary>
    Task<ImportImageMatch> MatchImageRowsAsync(Guid workspaceId, Guid importBatchId, ImportImageMatchMode mode, CancellationToken cancellationToken = default);

    /// <summary>Staged OPT rows of data rows <paramref name="rowFrom"/>…<paramref name="rowTo"/>, in OPT order.</summary>
    Task<IReadOnlyList<ImportImageRow>> GetImageRowsAsync(
        Guid workspaceId, Guid importBatchId, long rowFrom, long rowTo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Live documents of the workspace by normalized control number, or by Beg Bates (compared trimmed, and upper-cased
    /// unless <paramref name="caseSensitive"/>); a key that several documents share is left out.
    /// </summary>
    Task<IReadOnlyDictionary<string, ImportExistingDocument>> FindDocumentsAsync(
        Guid workspaceId, IReadOnlyCollection<string> keys, ImportImageMatchMode by, bool caseSensitive, CancellationToken cancellationToken = default);

    /// <summary>Row errors and warnings in load-file and row order (DAT first, then OPT), keyset-paged.</summary>
    Task<IReadOnlyList<ImportRowIssueRecord>> GetRowIssuesAsync(
        Guid workspaceId, Guid importBatchId, ImportIssueSeverity? severity, ImportRowIssueCursor? after, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The current family report lines (ADR-009 R10) of the documents this import created or overlaid, in row order,
    /// keyset-paged. Lines change as later chunks or imports complete a family.
    /// </summary>
    Task<IReadOnlyList<ImportFamilyIssueRecord>> GetFamilyIssuesAsync(
        Guid workspaceId, Guid importBatchId, ImportRowIssueCursor? after, int limit, CancellationToken cancellationToken = default);
}

/// <summary>One family report line of an import's document; <see cref="IssueNo"/> numbers the lines of the document.</summary>
public sealed record ImportFamilyIssueRecord(
    long RowNo,
    int IssueNo,
    Guid DocumentId,
    string ControlNumber,
    Opportunity.Core.Documents.FamilyIssueKind Kind,
    Opportunity.Core.Documents.FamilyStatus Status,
    string Message,
    IReadOnlyList<string> Related,
    long? MissingCount);

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

    /// <summary>The OPT loaded with the DAT (or alone, <see cref="ImagesOnly"/>), stored like the DAT.</summary>
    public ImportOptSource? Opt { get; init; }

    /// <summary>An OPT-only load: the source is the OPT; its documents replace pages of existing documents.</summary>
    public bool ImagesOnly { get; init; }

    public Guid? ProfileId { get; init; }

    public long? ProfileVersion { get; init; }

    /// <summary>The effective profile (Contracts <see cref="ImportProfileDefinition"/> JSON) the import runs with.</summary>
    public required string ProfileJson { get; init; }

    /// <summary>Q-31: coding/privilege fields enabled for overlay by this import.</summary>
    public IReadOnlyList<int> CodingOverlayFieldIds { get; init; } = [];

    /// <summary>
    /// <c>Workspace.ManageFields</c> was granted at start for a mapping that creates fields or choices. Without it the
    /// preparation pass refuses to create any, whatever the recompiled mapping asks for.
    /// </summary>
    public bool MayCreateFields { get; init; }

    public required Guid InitiatedBy { get; init; }

    public string? ClientIdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>Who started the import from where (actor, client, session, access path); the store fills the rest.</summary>
    public required AuditEvent AuditTemplate { get; init; }
}

public sealed record ImportBatchCreation(ImportBatchRecord Batch, JobInfo Job, bool Created);

public readonly record struct ImportBatchCursor(DateTimeOffset CreatedAt, Guid ImportBatchId);

public readonly record struct ImportRowIssueCursor(long RowNo, int IssueNo, ImportIssueSource Source = ImportIssueSource.Dat);

/// <summary>An OPT stored for an import (ADR-011 import source area).</summary>
public sealed record ImportOptSource(string FileName, string ObjectKey, byte[] Sha256, long Size);

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

    public ImportOptSource? Opt { get; init; }

    /// <summary>An OPT-only load (<see cref="NewImportBatch.ImagesOnly"/>).</summary>
    public bool ImagesOnly { get; init; }

    public Guid? ProfileId { get; init; }

    public long? ProfileVersion { get; init; }

    public required string ProfileJson { get; init; }

    public IReadOnlyList<int> CodingOverlayFieldIds { get; init; } = [];

    /// <summary>Field and choice creation was authorized at start (<see cref="NewImportBatch.MayCreateFields"/>).</summary>
    public bool MayCreateFields { get; init; }

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
/// <param name="OptRowsTotal">OPT rows staged for the import; null without an OPT.</param>
public sealed record ImportPreparation(
    IReadOnlyList<string> Header,
    string DatEncoding,
    bool EncodingFallback,
    long DataOffset,
    long RowsTotal,
    int FieldsCreated,
    int ChoicesCreated,
    long? OptRowsTotal = null);

/// <summary>Data rows <c>RowFrom…RowTo</c> occupy bytes <c>ByteFrom…ByteTo</c> (exclusive) of the DAT.</summary>
public sealed record ImportChunkRange(long RowFrom, long RowTo, long ByteFrom, long ByteTo, long LineFrom);

public sealed record ImportKey(string ControlNumberNorm, long RowNo);

/// <summary>The load file an issue's row refers to. Stored as smallint.</summary>
public enum ImportIssueSource : short
{
    Dat = 1,
    Opt = 2,
}

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

    /// <summary>The page images (OPT) of the row's document, stored for <see cref="ImportImages.DocumentId"/>.</summary>
    public ImportImages? Images { get; init; }

    /// <summary>Warnings about the row's OPT rows (missing or rejected images, page count mismatch).</summary>
    public IReadOnlyList<ImportOptIssue> OptIssues { get; init; } = [];

    /// <summary>
    /// OPT-only loads: the row is an OPT document and <see cref="Issues"/> are reported against this OPT row (its
    /// document break) instead of a DAT row.
    /// </summary>
    public long? OptRowNo { get; init; }

    public bool HasErrors => Document is null || Issues.Any(i => i.Severity == ImportIssueSeverity.Error);
}

/// <summary>A warning about one OPT row.</summary>
public sealed record ImportOptIssue(long OptRow, long LineNo, string? ImageKey, ImportRowIssue Issue);

/// <summary>
/// The Imported page set of one document (ADR-012 §1.6): pages in order and the original rasters, already stored under
/// <see cref="DocumentId"/>'s image area. The chunk registers the objects and writes the set in its transaction.
/// </summary>
public sealed record ImportImages
{
    /// <summary>The document the objects were stored for; the chunk links them only to that document.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>The OPT row that starts the document (its document break).</summary>
    public required long BreakOptRow { get; init; }

    public required long BreakLineNo { get; init; }

    public string? BreakImageKey { get; init; }

    public IReadOnlyList<ImportImageObject> Objects { get; init; } = [];

    public IReadOnlyList<ImportPage> Pages { get; init; } = [];

    /// <summary>Some page has no image: the set is Incomplete and the document <c>ImagesIncomplete</c>.</summary>
    public bool Incomplete => Pages.Count == 0 || Pages.Any(p => p.ImageMissing);
}

/// <summary>A stored image file (ADR-011 §2.3 registry values).</summary>
public sealed record ImportImageObject(
    string LogicalKey, byte[] Sha256, long SizeBytes, string ContentType, string KeyId, EncryptionScheme EncryptionScheme);

/// <param name="Raster">Null when the page's image is missing.</param>
public sealed record ImportPage(int Ordinal, string? ImageKey, int SourceFrame, ImportPageRaster? Raster)
{
    public bool ImageMissing => Raster is null;
}

/// <summary>The original raster of a page: frame <see cref="ImportPage.SourceFrame"/> of the object <see cref="LogicalKey"/>.</summary>
public sealed record ImportPageRaster(
    string LogicalKey, int WidthPx, int HeightPx, int DpiX, int DpiY, PageImageFormat Format, PageColorMode ColorMode);

/// <summary>How the preparation pass assigns OPT documents to rows.</summary>
public enum ImportImageMatchMode
{
    /// <summary>Break-row image key = the DAT row's normalized control number.</summary>
    ControlNumber,

    /// <summary>Break-row image key = the DAT row's normalized Beg Bates.</summary>
    BegBates,

    /// <summary>OPT-only load: every OPT document is its own row (the first of a repeated key wins).</summary>
    OptDocuments,
}

/// <summary>One staged OPT row.</summary>
/// <param name="DocNo">1-based OPT document; 0 for rows before the first document break.</param>
/// <param name="RowNo">The row the document belongs to; null for orphans.</param>
public sealed record ImportImageRow(
    long OptRow,
    long LineNo,
    long DocNo,
    bool IsBreak,
    string ImageKey,
    string Volume,
    string Path,
    int? PageCount,
    string? Problem,
    string? MatchKey,
    long? RowNo = null);

/// <param name="Documents">OPT documents (document breaks) staged.</param>
/// <param name="Matched">Documents assigned to a row.</param>
/// <param name="Unmatched">Documents (and leading rows without a break) reported as belonging to no row.</param>
public sealed record ImportImageMatch(long Documents, long Matched, long Unmatched);

/// <summary>A live document found for an OPT key.</summary>
public sealed record ImportExistingDocument(Guid DocumentId, string ControlNumber, string ControlNumberNorm);

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
    string Message,
    ImportIssueSource Source = ImportIssueSource.Dat);
