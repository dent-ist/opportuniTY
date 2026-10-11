namespace Opportunity.Contracts.Search;

/// <summary>
/// An imported folder of the read-only Folders tree (#179, Q-55), with the documents the caller may see in it. Folders
/// come from the Folder Path an import maps; users do not create, rename or move them.
/// </summary>
/// <param name="FolderId">Pass it to a search as <c>folder.folderId</c> to list the folder's documents.</param>
/// <param name="ParentFolderId">The folder above; null for a top-level folder.</param>
/// <param name="Name">The folder's own name, as first loaded.</param>
/// <param name="Path">The full path, folder names joined by a backslash (<c>Mail\Inbox</c>).</param>
/// <param name="Depth">1 for a top-level folder.</param>
/// <param name="DocumentCount">Documents filed directly in the folder that the caller may see.</param>
/// <param name="TotalCount">Documents in the folder and all folders below it that the caller may see.</param>
/// <param name="HasSubfolders">Whether a folder below it holds documents the caller may see (it can be expanded).</param>
public sealed record DocumentFolderResource(
    Guid FolderId,
    Guid? ParentFolderId,
    string Name,
    string Path,
    int Depth,
    long DocumentCount,
    long TotalCount,
    bool HasSubfolders)
{
    /// <summary>The folders above it, outermost first (single-folder reads only; null in lists).</summary>
    public IReadOnlyList<DocumentFolderResource>? Ancestors { get; init; }
}

/// <summary>One level of the Folders tree: the children of <see cref="Parent"/> (the top level when null), in name order.</summary>
/// <param name="Parent">The folder whose children these are; null for the top level.</param>
/// <param name="Items">Folders holding at least one document the caller may see.</param>
/// <param name="NextCursor">Continues this level; null at its end.</param>
/// <param name="CountsApproximate">The index is still catching up with recent changes, so counts may trail (≈, Q-10).</param>
public sealed record DocumentFolderPage(
    DocumentFolderResource? Parent,
    IReadOnlyList<DocumentFolderResource> Items,
    string? NextCursor,
    bool CountsApproximate);
