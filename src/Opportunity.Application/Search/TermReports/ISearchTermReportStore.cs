using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Core.SearchTermReports;

namespace Opportunity.Application.Search.TermReports;

/// <summary>The stored header of a search term report and its current run.</summary>
public sealed record SearchTermReportRecord
{
    public required Guid WorkspaceId { get; init; }

    public required Guid ReportId { get; init; }

    public required string Name { get; init; }

    public required SearchTermReportScopeKind ScopeKind { get; init; }

    public Guid? ScopeId { get; init; }

    public string? ScopeName { get; init; }

    public required SearchTermReportStatus Status { get; init; }

    public string? StatusReason { get; init; }

    public required Guid JobId { get; init; }

    public int RunCount { get; init; } = 1;

    public Guid? SnapshotId { get; init; }

    public long? SearchGeneration { get; init; }

    public bool? IndexCurrent { get; init; }

    public long? DocumentsInScope { get; init; }

    public long? DocumentsWithHits { get; init; }

    public long? DocumentsWithHitsIncludingFamily { get; init; }

    public long? DocumentsWithoutHits { get; init; }

    public long? ExcludedNoAccess { get; init; }

    public required int TermCount { get; init; }

    public required Guid CreatedBy { get; init; }

    public required string CreatedByDisplay { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required Guid ExecutedBy { get; init; }

    public required string ExecutedByDisplay { get; init; }

    public IReadOnlyList<string> ExecutedByGroups { get; init; } = [];

    public DateTimeOffset? ExecutedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>One term of a report; counts are null until the run completes, and stay null for a term with an error.</summary>
public sealed record SearchTermRecord(
    int TermNo,
    Guid TermId,
    string Name,
    string Expression,
    SearchTermError? Error,
    long? DocumentsWithHits,
    long? DocumentsWithHitsIncludingFamily,
    long? UniqueHits,
    long? UniqueHitsIncludingFamily);

public sealed record SearchTermError(string Code, string Message, int? Position);

/// <summary>A term to store: its name, expression and the error found when it was validated (if any).</summary>
public sealed record NewSearchTerm(string Name, string Expression, SearchTermError? Error);

/// <summary>Who runs a report: the executor every chunk is authorized as (ADR-010 §9).</summary>
public sealed record SearchTermReportExecutor(Guid UserId, string DisplayName, IReadOnlyList<string> Groups, string? CorrelationId);

public sealed record NewSearchTermReport
{
    public required Guid WorkspaceId { get; init; }

    public Guid ReportId { get; init; } = Guid.CreateVersion7();

    public Guid JobId { get; init; } = Guid.CreateVersion7();

    public required string Name { get; init; }

    public required SearchTermReportScopeKind ScopeKind { get; init; }

    public Guid? ScopeId { get; init; }

    public string? ScopeName { get; init; }

    /// <summary>A Ready Report snapshot to run over as it is (a snapshot scope frozen for reports); null to freeze one.</summary>
    public Guid? SnapshotId { get; init; }

    public required IReadOnlyList<NewSearchTerm> Terms { get; init; }

    public required SearchTermReportExecutor Executor { get; init; }

    public string? ClientIdempotencyKey { get; init; }

    public required JsonObject JobParameters { get; init; }
}

/// <param name="Created">False when an earlier request with the same creator and Idempotency-Key created the report.</param>
public sealed record SearchTermReportCreation(SearchTermReportRecord Report, bool Created);

/// <summary>Keyset position of the list (newest first).</summary>
public sealed record SearchTermReportListCursor(DateTimeOffset CreatedAt, Guid ReportId);

/// <summary>The counted hits of one chunk: the visible members and, per term number, the members it hits.</summary>
public sealed record SearchTermChunkResult(IReadOnlyList<Guid> VisibleDocuments, IReadOnlyDictionary<int, IReadOnlyList<Guid>> Hits);

/// <summary>
/// PostgreSQL storage of search term reports (V0034). Every call runs in the workspace's RLS context. The report
/// service owns authorization; the runner drives the runs.
/// </summary>
public interface ISearchTermReportStore
{
    /// <summary>Creates the report, its terms and its first run's job (Created) in one transaction.</summary>
    Task<SearchTermReportCreation> CreateAsync(NewSearchTermReport report, CancellationToken cancellationToken = default);

    Task<SearchTermReportRecord?> GetAsync(Guid workspaceId, Guid reportId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchTermRecord>> GetTermsAsync(Guid workspaceId, Guid reportId, CancellationToken cancellationToken = default);

    /// <summary>Reports newest first; only <paramref name="createdBy"/>'s when given.</summary>
    Task<IReadOnlyList<SearchTermReportRecord>> ListAsync(
        Guid workspaceId, Guid? createdBy, SearchTermReportListCursor? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts another run of a Completed or Failed report: a new job (Created) for <paramref name="executor"/>, the report
    /// back to Queued with its counts and term errors replaced by <paramref name="termErrors"/> (by term number). The
    /// snapshot is kept. Null when the report is not Completed or Failed any more.
    /// </summary>
    Task<SearchTermReportRecord?> RerunAsync(
        Guid workspaceId, Guid reportId, Guid jobId, SearchTermReportExecutor executor, IReadOnlyDictionary<int, SearchTermError> termErrors,
        JsonObject jobParameters, CancellationToken cancellationToken = default);

    /// <summary>Deletes the report with its terms and hit rows, writing <paramref name="audit"/> in the same transaction.</summary>
    Task<bool> DeleteAsync(Guid workspaceId, Guid reportId, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>Reports whose current run is Queued or Running, oldest first.</summary>
    Task<IReadOnlyList<Guid>> GetActiveAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Takes (or renews) the claim to prepare or finish a run; false while another runner holds it.</summary>
    Task<bool> TryClaimAsync(Guid workspaceId, Guid reportId, string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    Task ReleaseClaimAsync(Guid workspaceId, Guid reportId, string owner, CancellationToken cancellationToken = default);

    /// <summary>Records the run's snapshot (on the report and as the job's target, so the snapshot is kept).</summary>
    Task SetSnapshotAsync(Guid workspaceId, Guid reportId, Guid jobId, Guid snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Queued → Running for <paramref name="jobId"/>, with the watermark and index state at the start.</summary>
    Task MarkRunningAsync(
        Guid workspaceId, Guid reportId, Guid jobId, long searchGeneration, bool indexCurrent, DateTimeOffset executedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Records a term's error unless it already has one (the first error found wins).</summary>
    Task SetTermErrorAsync(Guid workspaceId, Guid reportId, int termNo, SearchTermError termError, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a chunk's rows: each visible member with its <c>family_id</c> and each (term, member) hit. Idempotent: a
    /// chunk that runs again writes the same rows.
    /// </summary>
    Task WriteChunkAsync(Guid workspaceId, Guid reportId, Guid jobId, SearchTermChunkResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the run of <paramref name="jobId"/>: computes every count from the run's rows (family expansion in
    /// PostgreSQL), stores them, removes earlier runs' rows and writes <paramref name="audit"/> (built from the completed
    /// report) in the same transaction. False when the report is not Running this job any more.
    /// </summary>
    Task<bool> CompleteAsync(
        Guid workspaceId, Guid reportId, Guid jobId, bool indexCurrentAtEnd, Func<SearchTermReportRecord, AuditEvent> audit,
        CancellationToken cancellationToken = default);

    /// <summary>The run of <paramref name="jobId"/> failed; false when the report is not running this job any more.</summary>
    Task<bool> FailAsync(Guid workspaceId, Guid reportId, Guid jobId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// The hit documents of a term in the current run, at most <paramref name="limit"/> (in document ID order).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetHitsAsync(Guid workspaceId, Guid reportId, int termNo, int limit, CancellationToken cancellationToken = default);
}
