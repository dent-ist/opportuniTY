using System.Globalization;
using System.Text.Json.Nodes;

using Opportunity.Application.Jobs;

namespace Opportunity.Application.Audit;

/// <summary>
/// The filters of the audit log viewer and of an audit export (E14-T04). They combine (AND); each one that is set narrows
/// the workspace's audit trail. The document filter is the event's resource; the job filter matches events of the job
/// (<see cref="AuditEvent.JobId"/>) and events about the job.
/// </summary>
public sealed record AuditLogFilters
{
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive upper bound.</summary>
    public DateTimeOffset? To { get; init; }

    public string? Category { get; init; }

    public string? Action { get; init; }

    public string? ActorId { get; init; }

    public Guid? DocumentId { get; init; }

    public Guid? JobId { get; init; }

    public AuditOutcome? Outcome { get; init; }

    public const string DocumentResourceType = "Document";

    /// <summary>The store query of the workspace's chain with these filters (no cursor, visibility or limit yet).</summary>
    public AuditQuery ToQuery(Guid workspaceId) => new(workspaceId)
    {
        From = From,
        To = To,
        Category = Category,
        Action = Action,
        ActorId = ActorId,
        ResourceType = DocumentId is null ? null : DocumentResourceType,
        ResourceId = DocumentId?.ToString(),
        JobId = JobId,
        Outcome = Outcome,
    };

    /// <summary>The filters as audit details (<c>Audit.Queried</c>, <c>Audit.Exported</c>): IDs and enums only.</summary>
    public Dictionary<string, string?> ToDetails()
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["From"] = From?.ToString("O", CultureInfo.InvariantCulture),
            ["To"] = To?.ToString("O", CultureInfo.InvariantCulture),
            ["Category"] = Category,
            ["Action"] = Action,
            ["ActorId"] = ActorId,
            ["DocumentId"] = DocumentId?.ToString(),
            ["JobId"] = JobId?.ToString(),
            ["Outcome"] = Outcome?.ToString(),
        };
        return details.Where(d => d.Value is not null).ToDictionary(StringComparer.Ordinal);
    }

    public JsonObject ToJson()
    {
        var json = new JsonObject();
        foreach (var (key, value) in ToDetails())
        {
            json[char.ToLowerInvariant(key[0]) + key[1..]] = value;
        }

        return json;
    }

    public static AuditLogFilters FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        string? Text(string name) => json[name]?.GetValue<string>();
        return new AuditLogFilters
        {
            From = Text("from") is { } from ? DateTimeOffset.Parse(from, CultureInfo.InvariantCulture) : null,
            To = Text("to") is { } to ? DateTimeOffset.Parse(to, CultureInfo.InvariantCulture) : null,
            Category = Text("category"),
            Action = Text("action"),
            ActorId = Text("actorId"),
            DocumentId = Text("documentId") is { } document ? Guid.Parse(document) : null,
            JobId = Text("jobId") is { } job ? Guid.Parse(job) : null,
            Outcome = Text("outcome") is { } outcome ? Enum.Parse<AuditOutcome>(outcome) : null,
        };
    }
}

public enum AuditExportStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

/// <summary>An audit export (E14-T04): its request, its job and, once completed, the file's rows, bytes and SHA-256.</summary>
public sealed record AuditExportRecord
{
    public required Guid WorkspaceId { get; init; }

    public required Guid ExportId { get; init; }

    public required Guid JobId { get; init; }

    public required AuditLogFilters Filters { get; init; }

    public bool IncludeSearchText { get; init; }

    /// <summary>The export holds events that occurred before this instant.</summary>
    public required DateTimeOffset OccurredBefore { get; init; }

    public required AuditExportStatus Status { get; init; }

    public string? StatusReason { get; init; }

    public required Guid CreatedBy { get; init; }

    public required string CreatedByDisplay { get; init; }

    public IReadOnlyList<string> CreatedByGroups { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public long? RowCount { get; init; }

    public long? ByteCount { get; init; }

    public string? ContentSha256 { get; init; }
}

public sealed record NewAuditExport
{
    public required Guid WorkspaceId { get; init; }

    public Guid ExportId { get; init; } = Guid.CreateVersion7();

    public Guid JobId { get; init; } = Guid.CreateVersion7();

    public required AuditLogFilters Filters { get; init; }

    public bool IncludeSearchText { get; init; }

    public required DateTimeOffset OccurredBefore { get; init; }

    public required Guid CreatedBy { get; init; }

    public required string CreatedByDisplay { get; init; }

    public IReadOnlyList<string> CreatedByGroups { get; init; } = [];

    public string? ClientIdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary><c>Audit.Exported</c>, written with the export and its job in one transaction (workspace, job and resource filled in).</summary>
    public required AuditEvent Requested { get; init; }
}

public sealed record AuditExportCreation(AuditExportRecord Export, JobInfo Job, bool Created);

/// <summary>A planned window of an export: events from <see cref="FromMicros"/> to <see cref="ToMicros"/> (Unix µs, inclusive).</summary>
public sealed record AuditExportWindow(long FromMicros, long ToMicros, int Count)
{
    public static DateTimeOffset Time(long micros) => DateTimeOffset.UnixEpoch.AddTicks(micros * 10);

    public static long Micros(DateTimeOffset time) => (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
}

/// <summary>
/// The audit log viewer's and audit exports' storage (E14-T04): resolving a document filter, the export records with
/// their jobs, planning an export's windows, reading a window's events and the file's parts. Reading events here writes
/// no <c>Audit.Queried</c>: the viewer reads through <see cref="IAuditEventReader"/>, and an export is audited as
/// <c>Audit.Exported</c> when requested and <c>Audit.ExportDownloaded</c> when its file is read.
/// </summary>
public interface IAuditLogStore
{
    /// <summary>
    /// The document with this Control Number (normalized as the workspace does) and/or ID, if the visibility lets the
    /// reader see it.
    /// </summary>
    Task<Guid?> FindDocumentAsync(
        Guid workspaceId, string? controlNumber, Guid? documentId, AuditDocumentVisibility visibility, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the export with its AuditExport job and <see cref="NewAuditExport.Requested"/>; a retry with the same
    /// initiator and client key returns the existing export.
    /// </summary>
    Task<AuditExportCreation> CreateAsync(NewAuditExport request, CancellationToken cancellationToken = default);

    Task<AuditExportRecord?> GetAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default);

    /// <summary>The initiator's exports, newest first, keyset-paged on (CreatedAt, ExportId).</summary>
    Task<IReadOnlyList<AuditExportRecord>> ListAsync(
        Guid workspaceId, Guid createdBy, (DateTimeOffset CreatedAt, Guid ExportId)? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>Exports still Queued or Running, oldest first.</summary>
    Task<IReadOnlyList<Guid>> GetActiveAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default);

    Task<bool> TryClaimAsync(Guid workspaceId, Guid exportId, string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    Task ReleaseClaimAsync(Guid workspaceId, Guid exportId, string owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plans the windows of the events matching <paramref name="query"/> (filters, time bound and visibility), oldest
    /// first, each at most <paramref name="maxPerWindow"/> events unless one microsecond holds more; null when more than
    /// <paramref name="maxEvents"/> events match.
    /// </summary>
    Task<IReadOnlyList<AuditExportWindow>?> PlanAsync(AuditQuery query, int maxPerWindow, long maxEvents, CancellationToken cancellationToken = default);

    /// <summary>The events of <paramref name="query"/> in one window, oldest first.</summary>
    Task<long> ReadWindowAsync(
        AuditQuery query, AuditExportWindow window, Func<StoredAuditEvent, ValueTask> onEvent, CancellationToken cancellationToken = default);

    Task MarkRunningAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default);

    /// <summary>Stores (or, for a retried chunk, replaces) one part of the file.</summary>
    Task WritePartAsync(Guid workspaceId, Guid exportId, int chunkSequence, int rows, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>The parts in chunk order, one at a time; returns the total rows.</summary>
    Task<long> ReadPartsAsync(Guid workspaceId, Guid exportId, Func<ReadOnlyMemory<byte>, ValueTask> onPart, CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        Guid workspaceId, Guid exportId, long rows, long bytes, string sha256, CancellationToken cancellationToken = default);

    Task<bool> FailAsync(Guid workspaceId, Guid exportId, string reason, CancellationToken cancellationToken = default);
}
