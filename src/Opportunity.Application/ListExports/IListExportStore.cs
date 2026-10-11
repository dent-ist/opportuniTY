using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;

namespace Opportunity.Application.ListExports;

public enum ListExportStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

/// <summary>One column of a list export: the field and the header written for it (its name when the export was requested).</summary>
public sealed record ListExportColumn(int FieldId, string Header);

/// <summary>A document list export (#181): its columns, frozen selection, job and, once completed, the file's totals.</summary>
public sealed record ListExportRecord
{
    public required Guid WorkspaceId { get; init; }

    public required Guid ExportId { get; init; }

    public required Guid JobId { get; init; }

    public required Guid SnapshotId { get; init; }

    public required string Name { get; init; }

    /// <summary>Control Number first, then the view's columns in order.</summary>
    public required IReadOnlyList<ListExportColumn> Columns { get; init; }

    public required ListExportStatus Status { get; init; }

    public string? StatusReason { get; init; }

    public required Guid CreatedBy { get; init; }

    public required string CreatedByDisplay { get; init; }

    public IReadOnlyList<string> CreatedByGroups { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public long? RowCount { get; init; }

    public long? ExcludedCount { get; init; }

    public long? ByteCount { get; init; }

    public string? ContentSha256 { get; init; }

    /// <summary>Documents in the frozen selection; null while it is being frozen.</summary>
    public long? DocumentCount { get; init; }
}

public sealed record NewListExport
{
    public required Guid WorkspaceId { get; init; }

    public Guid ExportId { get; init; } = Guid.CreateVersion7();

    public Guid JobId { get; init; } = Guid.CreateVersion7();

    public required Guid SnapshotId { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<ListExportColumn> Columns { get; init; }

    public required Guid CreatedBy { get; init; }

    public required string CreatedByDisplay { get; init; }

    public IReadOnlyList<string> CreatedByGroups { get; init; } = [];

    public string? ClientIdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary><c>Export.ListRequested</c>, written with the export and its job in one transaction (workspace, job and resource filled in).</summary>
    public required AuditEvent Requested { get; init; }
}

/// <param name="Created">False when an earlier request with the same Idempotency-Key created <paramref name="Export"/>.</param>
public sealed record ListExportCreation(ListExportRecord Export, JobInfo Job, bool Created);

/// <summary>
/// PostgreSQL storage of list exports (V0065): the export with its job, a claim for preparing and finishing, and the CSV
/// parts its chunks write (one per chunk sequence, replaced as a whole when a chunk is retried). Every call runs in the
/// workspace's RLS context.
/// </summary>
public interface IListExportStore
{
    /// <summary>Creates the export, its ListExport job (Created) and the request's audit event in one transaction; idempotent per key.</summary>
    Task<ListExportCreation> CreateAsync(NewListExport request, CancellationToken cancellationToken = default);

    Task<ListExportRecord?> GetAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default);

    /// <summary>The creator's exports, newest first, after the keyset position.</summary>
    Task<IReadOnlyList<ListExportRecord>> ListAsync(
        Guid workspaceId, Guid createdBy, (DateTimeOffset CreatedAt, Guid ExportId)? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>Queued or Running exports, oldest first.</summary>
    Task<IReadOnlyList<Guid>> GetActiveAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default);

    Task<bool> TryClaimAsync(Guid workspaceId, Guid exportId, string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    Task ReleaseClaimAsync(Guid workspaceId, Guid exportId, string owner, CancellationToken cancellationToken = default);

    /// <summary>Marks the export Running and points its job at the (Ready) frozen selection, so the selection is kept while the job is.</summary>
    Task MarkRunningAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default);

    /// <summary>Stores (or replaces) the CSV rows of one chunk while the export is Running.</summary>
    Task WritePartAsync(
        Guid workspaceId, Guid exportId, int chunkSequence, int rows, int excluded, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Reads the parts in chunk order, one round trip each; returns rows and excluded documents in total.</summary>
    Task<(long Rows, long Excluded)> ReadPartsAsync(
        Guid workspaceId, Guid exportId, Func<ReadOnlyMemory<byte>, ValueTask> onPart, CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        Guid workspaceId, Guid exportId, long rows, long excluded, long bytes, string sha256, CancellationToken cancellationToken = default);

    Task<bool> FailAsync(Guid workspaceId, Guid exportId, string reason, CancellationToken cancellationToken = default);
}
