namespace Opportunity.Contracts.Api;

/// <summary>
/// Body of <c>POST /api/v1/workspaces/{workspaceId}/search-term-reports</c> (E07-T10). Give the terms either as
/// <see cref="Terms"/> or as <see cref="TermsCsv"/>: pasted <c>Name,Expression</c> lines (RFC 4180 quoting, an optional
/// <c>Name,Expression</c> header row, a line with one value uses it as name and expression).
/// </summary>
/// <param name="Name">Display name, 1–200 characters.</param>
/// <param name="Terms">1–500 named query-language expressions, in report order.</param>
/// <param name="Scope">What to count in: the workspace, a saved search's results or a snapshot.</param>
/// <param name="TermsCsv">Alternative to <see cref="Terms"/>: the term list as CSV text.</param>
public sealed record CreateSearchTermReportRequest(
    string? Name,
    IReadOnlyList<SearchTermRequest>? Terms,
    SearchTermReportScopeRequest? Scope,
    string? TermsCsv = null);

/// <param name="Name">The term's label in the report, 1–200 characters.</param>
/// <param name="Expression">Query-language text (ADR-008); a term that does not parse or bind gets a per-term error.</param>
public sealed record SearchTermRequest(string? Name, string? Expression);

/// <param name="Id">The saved search or snapshot; omitted for the workspace.</param>
public sealed record SearchTermReportScopeRequest(SearchTermReportScopeKindResource Kind, Guid? Id = null);

public enum SearchTermReportScopeKindResource
{
    Workspace,
    SavedSearch,
    Snapshot,
}

public enum SearchTermReportStatusResource
{
    Queued,
    Running,
    Completed,
    Failed,
}

/// <param name="Name">The saved search's or snapshot's name when the report was created; null for the workspace.</param>
public sealed record SearchTermReportScopeResource(SearchTermReportScopeKindResource Kind, Guid? Id, string? Name);

public sealed record SearchTermReportUser(Guid UserId, string DisplayName);

/// <summary>One row of <c>GET …/search-term-reports</c>.</summary>
public sealed record SearchTermReportSummary
{
    public required Guid ReportId { get; init; }

    public required string Name { get; init; }

    public required SearchTermReportStatusResource Status { get; init; }

    public required SearchTermReportScopeResource Scope { get; init; }

    public required int TermCount { get; init; }

    public required SearchTermReportUser CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the current run finished (completed or failed); null while queued or running.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The current run's job (Jobs page).</summary>
    public required Guid JobId { get; init; }
}

/// <summary>
/// A search term report (<c>GET …/search-term-reports/{reportId}</c> and the body of every 202): the summary plus how it
/// was run and its counts. Counts are null until the run completes. They are the executor's: documents the executor
/// could not see when the run read them (restricted classes, ethical walls) are in no count.
/// </summary>
public sealed record SearchTermReportResource
{
    public required Guid ReportId { get; init; }

    public required string Name { get; init; }

    public required SearchTermReportStatusResource Status { get; init; }

    /// <summary>Why the run failed; null otherwise.</summary>
    public string? StatusReason { get; init; }

    public required SearchTermReportScopeResource Scope { get; init; }

    public required int TermCount { get; init; }

    public required SearchTermReportUser CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required Guid JobId { get; init; }

    /// <summary>The materialized Report snapshot the counts were computed over (Q-30); reruns reuse it.</summary>
    public Guid? SnapshotId { get; init; }

    /// <summary>The refresh-aware search watermark when the run started (ADR-001 §7.3).</summary>
    public long? SearchGeneration { get; init; }

    /// <summary>
    /// False when the index was not current while the run executed (changes were still being made searchable), so hits
    /// may not reflect the latest changes; the report must say so. Null until the run has started.
    /// </summary>
    public bool? IndexCurrent { get; init; }

    /// <summary>Who ran the current run (the creator, or whoever reran it) and when it started.</summary>
    public required SearchTermReportUser ExecutedBy { get; init; }

    public DateTimeOffset? ExecutedAt { get; init; }

    public SearchTermReportTotals? Totals { get; init; }

    public required IReadOnlyList<SearchTermReportTerm> Terms { get; init; }

    public required JobResource Job { get; init; }
}

/// <param name="DocumentsInScope">Scope documents the executor could see.</param>
/// <param name="DocumentsWithHits">Documents hit by at least one term.</param>
/// <param name="DocumentsWithHitsIncludingFamily">Those plus the scope documents of their families.</param>
/// <param name="DocumentsWithoutHits">Scope documents no term hits.</param>
public sealed record SearchTermReportTotals(
    long DocumentsInScope, long DocumentsWithHits, long DocumentsWithHitsIncludingFamily, long DocumentsWithoutHits);

/// <param name="Error">Why the term could not be counted (syntax, unknown field, too broad); its counts are then null.</param>
/// <param name="DocumentsWithHits">Scope documents the term hits.</param>
/// <param name="DocumentsWithHitsIncludingFamily">Those plus the scope documents of their families.</param>
/// <param name="UniqueHits">Hit documents no other term hits.</param>
/// <param name="UniqueHitsIncludingFamily">Documents whose family is hit by this term and no other.</param>
public sealed record SearchTermReportTerm(
    Guid TermId,
    string Name,
    string Expression,
    SearchTermReportTermError? Error,
    long? DocumentsWithHits,
    long? DocumentsWithHitsIncludingFamily,
    long? UniqueHits,
    long? UniqueHitsIncludingFamily);

/// <param name="Position">Start of the offending text in the expression (UTF-16 index), when known.</param>
public sealed record SearchTermReportTermError(string Code, string Message, int? Position);
