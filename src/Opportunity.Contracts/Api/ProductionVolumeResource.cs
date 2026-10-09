namespace Opportunity.Contracts.Api;

/// <summary>Lifecycle of a production volume run.</summary>
public enum ProductionVolumeStatusResource
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// One run that writes a finalized production's volume (E12-T05): page images with burned redactions and endorsements,
/// natives with slip sheets, placeholders, text, the DAT and OPT load files and a manifest with the SHA-256 of every
/// file. Every run of the same production writes byte-identical files; the manifest SHA-256 shows it.
/// </summary>
/// <param name="Volume">The volume folder name (e.g. <c>ABC_VOL001</c>).</param>
/// <param name="Report">Counts and the manifest checksum; null until the run completed.</param>
/// <param name="Verification">
/// The burn-in verification (E12-T06) once the run's chunks are done; a run whose verification failed ends as Failed and is
/// never delivered. Null for runs still running and runs written before the verification existed.
/// </param>
public sealed record ProductionVolumeResource(
    Guid VolumeId,
    Guid ProductionId,
    Guid WorkspaceId,
    string Volume,
    ProductionVolumeStatusResource Status,
    string? StatusReason,
    ProductionVolumeReportResource? Report,
    JobResource Job,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    ProductionVolumeVerificationResource? Verification = null);

/// <summary>Outcome of a volume run's burn-in verification.</summary>
public enum ProductionVolumeVerificationStatusResource
{
    Passed,
    Failed,
}

/// <summary>
/// The burn-in verification of a volume run (E12-T06): every redacted and withheld member is checked before the run may
/// complete. Pages that carry redactions are read back and inspected pixel by pixel in the render sandbox (each box opaque
/// in its burned colour, nothing of the source visible, no hidden data in the file); text must be the replacement text
/// and no native may be shipped. The per-check rows are in the run's burn-in report.
/// </summary>
/// <param name="Documents">Redacted and withheld members checked.</param>
/// <param name="Pages">Produced pages with redactions inspected.</param>
/// <param name="Boxes">Redaction boxes inspected.</param>
/// <param name="Failures">Failed checks; any failure blocks the volume.</param>
/// <param name="ReportSha256">SHA-256 of the burn-in report (also in the manifest of a completed run).</param>
public sealed record ProductionVolumeVerificationResource(
    ProductionVolumeVerificationStatusResource Status,
    long Documents,
    long Pages,
    long Boxes,
    long Failures,
    string ReportSha256);

/// <param name="Documents">Members written (every member of the production).</param>
/// <param name="Images">Image files, one per produced page (slip sheets and placeholders included).</param>
/// <param name="Natives">Native files.</param>
/// <param name="Texts">Text files.</param>
/// <param name="Files">Delivered files, manifest included.</param>
public sealed record ProductionVolumeReportResource(
    long Documents,
    long Images,
    long Natives,
    long Texts,
    long Files,
    long TotalBytes,
    string ManifestSha256);
