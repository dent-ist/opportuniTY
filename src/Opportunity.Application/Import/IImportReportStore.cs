using Opportunity.Contracts.Import;

namespace Opportunity.Application.Import;

/// <summary>
/// The import summary report (E08-T06): computed from the batch, its members, documents, page sets and row issues; frozen
/// on the batch (<c>import_batch.report</c>) in the transaction that completes the import, so it is retained with the job.
/// </summary>
public interface IImportReportStore
{
    /// <summary>The frozen report of a finished import, else the live one; null when the import does not exist.</summary>
    Task<ImportReportData?> GetAsync(Guid workspaceId, Guid importBatchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// DAT row errors in row order (rows after <paramref name="afterRow"/>), one entry per row with its messages joined —
    /// the rows of the re-loadable error file.
    /// </summary>
    Task<IReadOnlyList<ImportErroredRow>> GetErroredRowsAsync(
        Guid workspaceId, Guid importBatchId, long afterRow, int limit, CancellationToken cancellationToken = default);
}

/// <summary>A DAT row that was not loaded, with the codes and messages of its errors.</summary>
public sealed record ImportErroredRow(long RowNo, string ErrorText);

/// <summary>Report figures; the API adds the job status and the batch's identity.</summary>
public sealed record ImportReportData
{
    public bool Final { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public double ElapsedSeconds { get; init; }

    public long? RowsRead { get; init; }

    public long RowsImported { get; init; }

    public long RowsOverlaid { get; init; }

    public long RowsSkipped { get; init; }

    public long RowsErrored { get; init; }

    public long RowsWithWarnings { get; init; }

    public long NativesLinked { get; init; }

    public long NativesMissing { get; init; }

    public long TextLinked { get; init; }

    public long TextMissing { get; init; }

    public long TextTruncated { get; init; }

    public long ImageDocumentsLinked { get; init; }

    public long DocumentsWithoutImages { get; init; }

    public long PagesLinked { get; init; }

    public long PagesMissing { get; init; }

    public long FamiliesBuilt { get; init; }

    public long FamilyOrphans { get; init; }

    public int FieldsCreated { get; init; }

    public int ChoicesCreated { get; init; }

    public long ErrorFileRows { get; init; }

    public IReadOnlyList<ImportReportIssueCount> IssueCounts { get; init; } = [];
}

/// <param name="Code">The importer's issue code (kebab-case, e.g. <c>invalid-date</c>).</param>
public sealed record ImportReportIssueCount(string Code, ImportIssueSeverity Severity, long Count);

/// <summary>
/// Pre-flight results (E08-T06): the summary and its issues, kept for <see cref="RetentionPeriod"/> for their creator.
/// Nothing here touches documents, objects or the index.
/// </summary>
public interface IImportPreflightStore
{
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(48);

    /// <summary>The state of these normalized control numbers in the workspace; numbers not listed are free.</summary>
    Task<IReadOnlyDictionary<string, ImportKeyState>> FindKeysAsync(
        Guid workspaceId, IReadOnlyCollection<string> controlNumberNorms, CancellationToken cancellationToken = default);

    /// <summary>Stores a pre-flight with its issues (and purges the workspace's expired ones).</summary>
    Task SaveAsync(ImportPreflightRecord preflight, IReadOnlyList<ImportPreflightIssue> issues, CancellationToken cancellationToken = default);

    /// <summary>An unexpired pre-flight; null when unknown or expired.</summary>
    Task<ImportPreflightRecord?> GetAsync(Guid workspaceId, Guid preflightId, CancellationToken cancellationToken = default);

    /// <summary>Issues in stored order (row order) after issue <paramref name="afterIssueNo"/>.</summary>
    Task<IReadOnlyList<ImportPreflightIssue>> GetIssuesAsync(
        Guid workspaceId, Guid preflightId, int afterIssueNo, int limit, CancellationToken cancellationToken = default);
}

public enum ImportKeyState
{
    /// <summary>A live document has the number.</summary>
    Exists,

    /// <summary>A deleted document has the number.</summary>
    Deleted,

    /// <summary>The number belonged to a removed document and cannot be reused.</summary>
    Retired,
}

/// <param name="IssuesDropped">Issues beyond the stored maximum (counted, not kept).</param>
public sealed record ImportPreflightRecord(
    Guid WorkspaceId,
    Guid PreflightId,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ImportMode Mode,
    string SourceFileName,
    long RowsRead,
    long ErrorCount,
    long WarningCount,
    long IssuesDropped);
