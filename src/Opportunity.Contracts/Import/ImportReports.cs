using Opportunity.Contracts.Api;

namespace Opportunity.Contracts.Import;

/// <summary>
/// Result of <c>POST …/imports/preflight</c> (E08-T06): the load file checked against the profile and the workspace
/// without writing a document, object or index entry. <see cref="Blocking"/> is true when any error was found (the
/// wizard blocks the start; the API still accepts it). <see cref="Issues"/> holds the first 200 issues in row order;
/// every issue downloads as CSV from <c>GET …/imports/preflight/{preflightId}/issues</c> (kept at least 24 hours,
/// for the user who ran the pre-flight only).
/// </summary>
/// <param name="RowsRead">Data rows of the DAT (header excluded); OPT rows for an OPT-only load.</param>
/// <param name="ErrorCount">Issues of severity error (each would fail its row, or the whole load for row 0).</param>
/// <param name="WarningCount">Issues of severity warning (the row loads; something is flagged or adjusted).</param>
public sealed record ImportPreflightResource(
    Guid PreflightId,
    long RowsRead,
    long ErrorCount,
    long WarningCount,
    bool Blocking,
    IReadOnlyList<ImportPreflightIssueCount> IssueCounts,
    IReadOnlyList<ImportPreflightIssue> Issues,
    ImportMode Mode);

/// <summary>How many issues of one code and severity the pre-flight found.</summary>
public sealed record ImportPreflightIssueCount(string Code, ImportRowIssueSeverity Severity, long Count);

/// <summary>
/// One pre-flight issue. <see cref="Row"/> is the 1-based data row of the DAT (OPT row for codes starting with
/// <c>OPT_</c>), 0 for the file, header or mapping. Codes are UPPER_SNAKE_CASE, e.g. <c>FIELD_COUNT_MISMATCH</c>,
/// <c>REQUIRED_FIELD_MISSING</c>, <c>DATE_PARSE_FAILED</c>, <c>DUPLICATE_CONTROL_NUMBER</c>, <c>KEY_EXISTS</c>,
/// <c>KEY_MISSING</c>, <c>NATIVE_MISSING</c>, <c>TEXT_MISSING</c>, <c>PATHS_SAMPLED</c>.
/// </summary>
public sealed record ImportPreflightIssue(
    long Row,
    string? ControlNumber,
    string? Column,
    string Code,
    ImportRowIssueSeverity Severity,
    string Message);

/// <summary>
/// The import summary report (E08-T06): row outcomes, linked and missing files, images and pages, families and
/// elapsed time, plus issue counts by code. Live while the import runs; frozen with the job when it finishes
/// (<see cref="Final"/>) and audited with <c>Import.Completed</c>. Also served as CSV (<c>…/report.csv</c>).
/// </summary>
/// <param name="ElapsedSeconds">From the start of the import to its completion (or to now while it runs).</param>
/// <param name="ErrorFileRows">DAT rows the error file holds (errored rows; rows too long to buffer are not reproducible).</param>
public sealed record ImportReportResource(
    Guid ImportId,
    string Name,
    ImportMode Mode,
    string SourceFileName,
    JobResourceStatus Status,
    bool Final,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    double ElapsedSeconds,
    ImportReportRows Rows,
    ImportReportFiles Natives,
    ImportReportFiles Text,
    ImportReportImages Images,
    ImportReportFamilies Families,
    int FieldsCreated,
    int ChoicesCreated,
    long ErrorFileRows,
    IReadOnlyList<ImportPreflightIssueCount> IssueCounts);

/// <param name="Read">Data rows of the load file (OPT documents for an OPT-only load); null until it was read.</param>
/// <param name="WithWarnings">Loaded rows that carry at least one warning.</param>
public sealed record ImportReportRows(long? Read, long Imported, long Overlaid, long Skipped, long Errored, long WithWarnings);

/// <summary>Natives or extracted text of the documents this import created or overlaid.</summary>
/// <param name="Linked">Documents with the file stored.</param>
/// <param name="Missing">Documents flagged Native Missing / Text Missing (blank link or file not in the volume).</param>
/// <param name="Truncated">Text only: documents whose text exceeds the indexed-text cap.</param>
public sealed record ImportReportFiles(long Linked, long Missing, long Truncated = 0);

/// <param name="DocumentsLinked">Documents that received an imported page set.</param>
/// <param name="DocumentsWithoutImages">Loaded DAT rows the OPT gave no images.</param>
public sealed record ImportReportImages(long DocumentsLinked, long DocumentsWithoutImages, long PagesLinked, long PagesMissing);

/// <param name="Built">Families of this import's documents with a parent and at least one attachment.</param>
/// <param name="Orphans">Attachments whose parent is not in the workspace.</param>
public sealed record ImportReportFamilies(long Built, long Orphans);
