namespace Opportunity.Contracts.Api;

/// <summary>
/// One event of <c>GET /api/v1/workspaces/{workspaceId}/audit-events</c> (E14-T04, ADR-013 §4 envelope). Audit holds
/// identifiers, never document content (ADR-013 §7): a document event names the document by ID and, when you may see
/// it, its Control Number resolved now.
/// </summary>
/// <param name="Details">Action-specific IDs and enums.</param>
/// <param name="SearchText">The executed query text of a search event (Q-16); only with <c>includeSearchText</c> and <c>Audit.ReadSearchText</c>.</param>
/// <param name="Sequence">The event's position in the workspace's hash chain; null until sealed.</param>
public sealed record AuditEventResource(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string Category,
    string Action,
    AuditOutcomeResource Outcome,
    string? ReasonCode,
    AuditActorResource Actor,
    Guid? OnBehalfOf,
    AuditAccessPathResource AccessPath,
    AuditEventResourceRef? Resource,
    Guid? JobId,
    int? ChunkSequence,
    Guid? SnapshotId,
    long? SearchGeneration,
    string CorrelationId,
    string? ClientIp,
    IReadOnlyDictionary<string, string?> Details,
    IReadOnlyDictionary<string, string?>? SearchText,
    DateTimeOffset RecordedAt,
    long? Sequence);

/// <param name="Id">A user ID for people; a service name for services and system actors.</param>
public sealed record AuditActorResource(AuditActorTypeResource Type, string Id, string DisplayName);

/// <param name="ControlNumber">For a document you may see: its Control Number now.</param>
public sealed record AuditEventResourceRef(string Type, string? Id, string? ControlNumber);

public enum AuditOutcomeResource
{
    Success,
    Denied,
    Failure,
}

public enum AuditActorTypeResource
{
    User,
    Service,
    System,
}

public enum AuditAccessPathResource
{
    Normal,
    BreakGlass,
    Impersonation,
}

/// <summary>One (category, action) pair of the closed audit taxonomy (ADR-013 §5).</summary>
public sealed record AuditActionResource(string Category, string Action);

/// <summary>
/// <c>POST …/audit-exports</c>: the viewer's filters (they combine), and whether to include search text (needs
/// <c>Audit.ReadSearchText</c>). A document is named by <see cref="ControlNumber"/> or <see cref="DocumentId"/>.
/// </summary>
public sealed record CreateAuditExportRequest
{
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive end of the date range.</summary>
    public DateTimeOffset? To { get; init; }

    public string? Category { get; init; }

    /// <summary>An action of <see cref="Category"/>.</summary>
    public string? Action { get; init; }

    /// <summary>The acting user's ID.</summary>
    public string? ActorId { get; init; }

    public string? ControlNumber { get; init; }

    public Guid? DocumentId { get; init; }

    public Guid? JobId { get; init; }

    public AuditOutcomeResource? Outcome { get; init; }

    public bool IncludeSearchText { get; init; }
}

/// <summary>The filters an audit export was requested with (a document by ID).</summary>
public sealed record AuditExportFiltersResource(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Category,
    string? Action,
    string? ActorId,
    Guid? DocumentId,
    Guid? JobId,
    AuditOutcomeResource? Outcome);

public enum AuditExportStatusResource
{
    Queued,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// An audit export (E14-T04): a job that writes the filtered audit trail as CSV. Only its requester sees it and may
/// download the file (<c>GET …/audit-exports/{exportId}/content</c>).
/// </summary>
/// <param name="OccurredBefore">The file holds events that occurred before this instant (the request time, or the filter's end).</param>
/// <param name="ContentSha256">SHA-256 (hex) of the file, once completed.</param>
public sealed record AuditExportResource(
    Guid ExportId,
    Guid JobId,
    AuditExportStatusResource Status,
    string? StatusReason,
    AuditExportFiltersResource Filters,
    bool IncludeSearchText,
    DateTimeOffset OccurredBefore,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    long? RowCount,
    long? ByteCount,
    string? ContentSha256);
