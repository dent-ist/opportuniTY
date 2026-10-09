using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;

namespace Opportunity.Application.Exports;

/// <summary>Lifecycle of an export. Stored as smallint.</summary>
public enum ExportStatus : short
{
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>What an export file is. Stored as smallint; values are fixed forever.</summary>
public enum ExportFileKind : short
{
    Native = 1,
    Text = 2,
    Image = 3,
    Dat = 4,
    Opt = 5,
    Manifest = 6,
    Report = 7,

    /// <summary>The DAT rows one chunk wrote; assembled into the DAT by the finalization, never delivered.</summary>
    DatPart = 8,

    /// <summary>The OPT rows one chunk wrote; assembled into the OPT by the finalization, never delivered.</summary>
    OptPart = 9,

    /// <summary>
    /// Production volumes (E12-T05): the produced-page records one chunk wrote (page Bates, file, source page, burned
    /// redaction boxes in image pixels); assembled into the volume's verification file, never delivered.
    /// </summary>
    PagePart = 10,

    /// <summary>
    /// Production volumes: what the burn-in verification (E12-T06) reads back (every produced page with its file, its
    /// SHA-256 and the pixel boxes burned into it). Kept with the volume, never delivered to the recipient.
    /// </summary>
    Verification = 11,
}

/// <summary>An export to create with its Export job (Created) and its <c>Export.Created</c> audit event.</summary>
public sealed record NewExport
{
    public required Guid WorkspaceId { get; init; }

    public Guid ExportId { get; init; } = Guid.CreateVersion7();

    public Guid JobId { get; init; } = Guid.CreateVersion7();

    public required Guid SnapshotId { get; init; }

    public required string Name { get; init; }

    /// <summary>The validated settings, serialized; frozen for the life of the export.</summary>
    public required string SettingsJson { get; init; }

    public required Guid InitiatedBy { get; init; }

    public required string InitiatedByDisplay { get; init; }

    public IReadOnlyList<string> InitiatedByGroups { get; init; } = [];

    public string? ClientIdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>Actor and request context of the <c>Export.Created</c> event; the store fills in the resource.</summary>
    public required AuditEvent AuditTemplate { get; init; }

    /// <summary>Job parameters (identifiers and settings only).</summary>
    public JsonObject? Parameters { get; init; }

    /// <summary>
    /// Set for a production volume run (E12-T05): the finalized production whose volume this writes. Its job is a
    /// Production job, its audit events are <c>Production.*</c>, and export listings never show it.
    /// </summary>
    public Guid? ProductionId { get; init; }
}

/// <param name="Created">False for a retried request with the same Idempotency-Key.</param>
public sealed record ExportCreation(ExportRecord Export, JobInfo Job, bool Created);

/// <summary>The stored export.</summary>
public sealed record ExportRecord
{
    public required Guid WorkspaceId { get; init; }

    public required Guid ExportId { get; init; }

    public required Guid JobId { get; init; }

    public required Guid SnapshotId { get; init; }

    public required string Name { get; init; }

    public required string SettingsJson { get; init; }

    public required ExportStatus Status { get; init; }

    public string? StatusReason { get; init; }

    public required Guid CreatedBy { get; init; }

    public required string CreatedByDisplay { get; init; }

    public IReadOnlyList<string> CreatedByGroups { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Set once the export completed.</summary>
    public ExportReport? Report { get; init; }

    /// <summary>The production whose volume this run writes (E12-T05); null for an export.</summary>
    public Guid? ProductionId { get; init; }
}

/// <summary>Counts of a completed export and the SHA-256 of its manifest.</summary>
public sealed record ExportReport(
    long DocumentsExported,
    long DocumentsExcluded,
    long Natives,
    long Texts,
    long Images,
    long Pages,
    long Files,
    long TotalBytes,
    byte[] ManifestSha256);

/// <summary>Keyset position of the export list (newest first).</summary>
public sealed record ExportListCursor(DateTimeOffset CreatedAt, Guid ExportId);

/// <summary>A Running export with the state of its job, for the worker that plans and finalizes exports.</summary>
public sealed record ActiveExport(ExportRecord Export, JobStatus JobStatus, long ChunksFailed);

/// <summary>An object written for an export, to register (ADR-011 §2.4) and list with its delivered path.</summary>
/// <param name="Path">Relative package path with <c>/</c> separators; a generated name for parts.</param>
/// <param name="ObjectKey">The generated logical key under <c>ws/{ws}/exports/{exportId}/{runId}/</c>.</param>
public sealed record NewExportFile(
    string Path,
    ExportFileKind Kind,
    string ObjectKey,
    byte[] Sha256,
    long SizeBytes,
    string ContentType,
    string KeyId,
    EncryptionScheme EncryptionScheme,
    long? Ordinal = null);

/// <summary>A registered export file.</summary>
public sealed record ExportFileRecord(
    Guid FileId,
    string Path,
    ExportFileKind Kind,
    string ObjectKey,
    byte[] Sha256,
    long SizeBytes,
    string ContentType,
    int? ChunkSequence,
    long? Ordinal);

/// <summary>What one chunk did with one snapshot member.</summary>
/// <param name="Reason">Excluded members only: the generic, requester-facing reason.</param>
public sealed record ExportDocumentOutcome(
    long Ordinal,
    Guid DocumentId,
    bool Excluded,
    string? ControlNumber,
    string? Reason = null,
    int Natives = 0,
    int Texts = 0,
    int Images = 0,
    int Pages = 0);

/// <summary>Everything one export chunk records, committed together with the chunk (fence F3).</summary>
public sealed record ExportChunkWrite(
    Guid ExportId,
    IReadOnlyList<ExportDocumentOutcome> Documents,
    IReadOnlyList<NewExportFile> Files,
    IReadOnlyList<AuditEvent> Audit);

/// <summary>A stored object an export reads: its registered key, hash and size (chain of custody, ADR-011 §2.5).</summary>
public sealed record ExportSourceObject(string ObjectKey, byte[] Sha256, long SizeBytes, string? ContentType);

/// <summary>One page of a document's active page set with the image an export copies (the original, else the review image).</summary>
/// <param name="Image">Null when the page has no stored image.</param>
public sealed record ExportSourcePage(int Ordinal, ExportSourceObject? Image, PageImageFormat? Format);

/// <summary>One live document with everything its DAT row, files and OPT rows need, read in one snapshot.</summary>
/// <param name="Coding">Current coding values by field id (canonical ADR-003 JSON).</param>
/// <param name="ParentControlNumber">Control number of the immediate parent, when the document has one.</param>
/// <param name="FamilyControlNumber">Control number of the family's top-level parent (the document itself when standalone).</param>
/// <param name="FamilyBegAttach">First control number of the family, by control-number order; null for a standalone document.</param>
/// <param name="FamilyEndAttach">Last control number of the family; null for a standalone document.</param>
public sealed record ExportSourceDocument(
    Document Document,
    IReadOnlyDictionary<int, JsonNode> Coding,
    ExportSourceObject? Native,
    ExportSourceObject? Text,
    IReadOnlyList<ExportSourcePage> Pages,
    string? ParentControlNumber,
    string FamilyControlNumber,
    string? FamilyBegAttach,
    string? FamilyEndAttach);

/// <summary>The field catalogue and the live documents of one read; deleted and unknown documents are absent.</summary>
public sealed record ExportSourceBatch(FieldCatalog Catalog, IReadOnlyDictionary<Guid, ExportSourceDocument> Documents);

/// <summary>The initiator as PostgreSQL knows them now (display name and IdP group snapshot, ADR-015 D9.4).</summary>
public sealed record ExportInitiator(string DisplayName, IReadOnlyList<string> Groups);

/// <summary>
/// PostgreSQL storage of exports (V0024): the export and its job, the per-chunk outcome rows and file registrations, the
/// finalization and the reads the worker needs. Every workspace call runs in that workspace's RLS context.
/// </summary>
public interface IExportStore
{
    /// <summary>
    /// Creates the export, its Export job (Created, targeting the snapshot) and <c>Export.Created</c> in one transaction.
    /// A retry with the same initiator and Idempotency-Key returns the existing export.
    /// </summary>
    Task<ExportCreation> CreateAsync(NewExport request, CancellationToken cancellationToken = default);

    Task<ExportRecord?> GetAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default);

    Task<ExportRecord?> GetByJobAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Exports newest first; only <paramref name="createdBy"/>'s when given.</summary>
    Task<IReadOnlyList<ExportRecord>> ListAsync(
        Guid workspaceId, Guid? createdBy, ExportListCursor? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>Running exports, oldest first, with their job's status: the ones to plan or finalize.</summary>
    Task<IReadOnlyList<ActiveExport>> GetActiveAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Running production volume runs (E12-T05), oldest first, with their job's status.</summary>
    Task<IReadOnlyList<ActiveExport>> GetActiveVolumesAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default);

    /// <summary>The volume runs of a production, newest first.</summary>
    Task<IReadOnlyList<ExportRecord>> ListVolumesAsync(
        Guid workspaceId, Guid productionId, ExportListCursor? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>Takes (or renews) the planning/finalization claim of a Running export unless another live claim holds it.</summary>
    Task<bool> TryClaimAsync(Guid workspaceId, Guid exportId, string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Gives up <paramref name="owner"/>'s claim (after planning, so any worker may finalize).</summary>
    Task ReleaseClaimAsync(Guid workspaceId, Guid exportId, string owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// One chunk in one transaction: its outcome rows, the registration of its files (StoredObject + export_file), its
    /// audit events and fence F3 with <paramref name="completion"/>. When F3 refuses, nothing is written and the chunk is
    /// released (returned to Pending or cancelled).
    /// </summary>
    Task<ChunkCommitResult> ApplyChunkAsync(
        ClaimedChunk chunk, ExportChunkWrite write, ChunkCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>Files of the export of the given kinds in (chunk sequence, path) order, keyset-paged by path.</summary>
    Task<IReadOnlyList<ExportFileRecord>> GetFilesAsync(
        Guid workspaceId, Guid exportId, IReadOnlyCollection<ExportFileKind> kinds, string? afterPath, int limit,
        CancellationToken cancellationToken = default);

    Task<ExportFileRecord?> GetFileAsync(Guid workspaceId, Guid exportId, Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>Excluded members in ordinal order after <paramref name="afterOrdinal"/>.</summary>
    Task<IReadOnlyList<ExportDocumentOutcome>> GetExclusionsAsync(
        Guid workspaceId, Guid exportId, long afterOrdinal, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Running → Completed: registers the finalization's files, writes the report and <paramref name="audit"/> in one
    /// transaction. False when the export is no longer Running (another worker finished it).
    /// </summary>
    Task<bool> CompleteAsync(
        Guid workspaceId, Guid exportId, IReadOnlyList<NewExportFile> files, ExportReport report, AuditEvent audit,
        CancellationToken cancellationToken = default);

    /// <summary>Running → Failed or Cancelled with a reason. False when the export is no longer Running.</summary>
    Task<bool> EndAsync(Guid workspaceId, Guid exportId, ExportStatus status, string reason, CancellationToken cancellationToken = default);

    /// <summary>Per-outcome totals of the members the chunks recorded (exported/excluded and file counts).</summary>
    Task<ExportDocumentTotals> GetDocumentTotalsAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default);

    /// <summary>The documents with their coding, artifacts, pages and family columns, in one <c>REPEATABLE READ</c> snapshot.</summary>
    Task<ExportSourceBatch> ReadDocumentsAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);

    /// <summary>The initiator's current display name and groups (installation-level user record), or null when unknown.</summary>
    Task<ExportInitiator?> ReadInitiatorAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record ExportDocumentTotals(long Exported, long Excluded, long Natives, long Texts, long Images, long Pages);
