using System.Globalization;

namespace Opportunity.Application.Storage;

/// <summary>
/// Builds the logical keys of ADR-011 §1.5. Inputs are identifiers, digests and system-generated names only.
/// </summary>
public static class ObjectKeys
{
    public static ObjectKey Native(Guid workspaceId, Guid documentId, Sha256Digest sha256) =>
        Document(workspaceId, documentId, $"native/{sha256.Hex}");

    public static ObjectKey Text(Guid workspaceId, Guid documentId, Sha256Digest sha256) =>
        Document(workspaceId, documentId, $"text/{sha256.Hex}");

    public static ObjectKey Image(Guid workspaceId, Guid documentId, Sha256Digest sha256) =>
        Document(workspaceId, documentId, $"image/{sha256.Hex}");

    /// <param name="name">System-generated file name such as <c>p000001.png</c> or <c>doc.pdf</c>.</param>
    public static ObjectKey Rendition(Guid workspaceId, Guid documentId, Guid renditionId, string name) =>
        Document(workspaceId, documentId, $"rend/{Id(renditionId)}/{name}");

    public static ObjectKey ImportSource(Guid workspaceId, Guid importId, Sha256Digest sha256) =>
        Workspace(workspaceId, $"imports/{Id(importId)}/source/{sha256.Hex}");

    public static ObjectKey ImportUpload(Guid workspaceId, Guid importId, Guid uploadId) =>
        Workspace(workspaceId, $"imports/{Id(importId)}/upload/{Id(uploadId)}");

    public static ObjectKey SnapshotManifest(Guid workspaceId, Guid snapshotId, int chunk)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunk);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunk, 999_999);
        return Workspace(workspaceId, $"snapshots/{Id(snapshotId)}/{chunk.ToString("D6", CultureInfo.InvariantCulture)}.bin");
    }

    /// <param name="relativePath">Generated volume path, e.g. <c>VOL001/NATIVES/ABC0000001.pdf</c>; lowercased in the key only.</param>
    public static ObjectKey ExportFile(Guid workspaceId, Guid exportId, Guid runId, string relativePath) =>
        Workspace(workspaceId, $"exports/{Id(exportId)}/{Id(runId)}/{Relative(relativePath)}");

    /// <param name="relativePath">Generated volume path, e.g. <c>VOL001/IMAGES/IMG001/ABC0000001.tif</c>.</param>
    public static ObjectKey ProductionFile(Guid workspaceId, Guid productionId, int version, Guid runId, string relativePath)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return Workspace(
            workspaceId,
            $"productions/{Id(productionId)}/v{version.ToString(CultureInfo.InvariantCulture)}/{Id(runId)}/{Relative(relativePath)}");
    }

    public static ObjectKey Report(Guid workspaceId, Guid reportId, Sha256Digest sha256) =>
        Workspace(workspaceId, $"reports/{Id(reportId)}/{sha256.Hex}");

    /// <param name="name">System-generated scratch name; may contain <c>/</c>.</param>
    public static ObjectKey JobScratch(Guid workspaceId, Guid jobId, string name) =>
        Workspace(workspaceId, $"tmp/{Id(jobId)}/{name}");

    public static ObjectKey DestructionCertificate(Guid deletionId, Sha256Digest sha256) =>
        ObjectKey.Parse($"sys/certificates/{Id(deletionId)}/{sha256.Hex}");

    private static ObjectKey Document(Guid workspaceId, Guid documentId, string rest) =>
        Workspace(workspaceId, $"docs/{Id(documentId)}/{rest}");

    private static ObjectKey Workspace(Guid workspaceId, string rest) => ObjectKey.Parse($"ws/{Id(workspaceId)}/{rest}");

    private static string Id(Guid id) => ObjectKeyGrammar.Id(id);

    private static string Relative(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        return relativePath.ToLowerInvariant();
    }
}
