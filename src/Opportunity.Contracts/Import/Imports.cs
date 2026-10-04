using Opportunity.Contracts.Api;

namespace Opportunity.Contracts.Import;

/// <summary>
/// JSON part (<c>request</c>) of <c>POST …/imports</c>; the DAT is the <c>file</c> part and an optional OPT the <c>opt</c>
/// part (an <c>opt</c> without a <c>file</c> is an OPT-only image load against existing documents). Give a saved
/// <see cref="ProfileId"/> or an ad-hoc <see cref="Profile"/> (neither: auto-map with default settings).
/// </summary>
public sealed record ImportStartRequest
{
    /// <summary>Import name shown in the import list and the "Import" field; default <c>&lt;DAT file name&gt; &lt;yyyy-mm-dd&gt;</c>.</summary>
    public string? Name { get; init; }

    public Guid? ProfileId { get; init; }

    public ImportProfileDefinition? Profile { get; init; }

    /// <summary>Overrides the profile's mode (chosen in step 1 of the wizard).</summary>
    public ImportMode? Mode { get; init; }

    /// <summary>Auto-map columns the profile does not cover; the result is frozen with the import.</summary>
    public bool AutoMap { get; init; } = true;

    /// <summary>
    /// Q-31: coding or privilege fields this import may load, each enabled explicitly (Workspace Admin only, audited).
    /// Every mapped coding field must be listed.
    /// </summary>
    public IReadOnlyList<int> CodingOverlayFieldIds { get; init; } = [];
}

/// <summary>
/// An import (one load of a DAT, with or without an OPT, or of an OPT alone) with its report in the import report vocabulary (read / imported / overlaid / skipped /
/// errored) and the job that runs it. <see cref="ImportId"/> is the import batch every loaded document belongs to.
/// </summary>
public sealed record ImportResource(
    Guid ImportId,
    Guid WorkspaceId,
    string Name,
    ImportMode Mode,
    string SourceFileName,
    long SourceSize,
    string SourceSha256,
    Guid? ProfileId,
    IReadOnlyList<int> CodingOverlayFieldIds,
    ImportReport Report,
    JobResource Job,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? OptFileName = null,
    bool ImagesOnly = false);

/// <summary>The load file an issue refers to.</summary>
public enum ImportIssueFile
{
    /// <summary>The DAT: the row is its 1-based data row.</summary>
    Dat,

    /// <summary>The OPT: the row is its 1-based OPT row (one per page image).</summary>
    Opt,
}

public enum ImportRowIssueSeverity
{
    /// <summary>The row was not loaded.</summary>
    Error,

    /// <summary>The row was loaded; a value was adjusted or ignored.</summary>
    Warning,
}

/// <summary>One row-level error or warning of an import (the error detail of the import report).</summary>
/// <param name="Row">1-based data row of the load file named by <paramref name="File"/>.</param>
/// <param name="Line">1-based physical line where the row starts.</param>
/// <param name="ControlNumber">For an OPT row: the image key of the row.</param>
public sealed record ImportRowIssueResource(
    long Row,
    long? Line,
    ImportRowIssueSeverity Severity,
    string? ControlNumber,
    string? Column,
    string Code,
    string Message,
    ImportIssueFile File = ImportIssueFile.Dat);

/// <param name="RowsRead">Data rows in the file; null until the file has been read (preparation).</param>
/// <param name="RowsImported">New documents created.</param>
/// <param name="RowsOverlaid">Existing documents changed.</param>
/// <param name="RowsSkipped">Existing documents the load left unchanged (identical values).</param>
/// <param name="RowsErrored">Rows not loaded; see the error detail download.</param>
public sealed record ImportReport(
    long? RowsRead,
    long RowsImported,
    long RowsOverlaid,
    long RowsSkipped,
    long RowsErrored,
    int FieldsCreated,
    int ChoicesCreated);
