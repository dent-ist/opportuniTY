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
    DateTimeOffset? CompletedAt);

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
