using Opportunity.Application.Search;
using Opportunity.Contracts.Search;

namespace Opportunity.Application.Documents;

/// <summary>One imported folder as PostgreSQL holds it (#179, V0068); unauthorized.</summary>
/// <param name="AncestorIds">The folders above it and the folder itself, outermost first.</param>
/// <param name="SortKey">The folder's place among its siblings (the continuation key of a children page).</param>
public sealed record DocumentFolderRow(
    Guid FolderId, Guid? ParentFolderId, string Name, string Path, int Depth, IReadOnlyList<Guid> AncestorIds, string SortKey);

/// <summary>PostgreSQL reads of the read-only folder tree (#179).</summary>
public interface IDocumentFolderStore
{
    /// <summary>
    /// The children of <paramref name="parentFolderId"/> (top-level folders when null), in name order (case-insensitive),
    /// after <paramref name="after"/> (a <see cref="DocumentFolderRow.SortKey"/>), at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<DocumentFolderRow>> ListChildrenAsync(
        Guid workspaceId, Guid? parentFolderId, string? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>The folders with these ids that exist in the workspace.</summary>
    Task<IReadOnlyList<DocumentFolderRow>> GetAsync(Guid workspaceId, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken = default);
}

/// <summary>Documents the caller may see per folder, as the search index holds them.</summary>
/// <param name="Documents">Filed directly in the folder.</param>
/// <param name="Total">In the folder or any folder below it.</param>
public sealed record DocumentFolderCount(long Documents, long Total);

public enum DocumentFolderCountStatus
{
    Ok,
    NotFound,
    Forbidden,
}

public sealed record DocumentFolderCountOutcome(
    DocumentFolderCountStatus Status, IReadOnlyDictionary<Guid, DocumentFolderCount> Counts, bool Approximate = false)
{
    public static DocumentFolderCountOutcome Of(DocumentFolderCountStatus status) => new(status, new Dictionary<Guid, DocumentFolderCount>());
}

/// <summary>
/// Folder counts for the caller (implemented by the search module): one aggregation under the same outer filter as every
/// search, so restricted and walled documents are never counted (ADR-015 D8, Q-52). Like search totals, the counts follow
/// the index (approximate while it catches up, Q-10).
/// </summary>
public interface IDocumentFolderCounts
{
    /// <summary>At most <see cref="DocumentFolderService.MaxPageSize"/> folders per call.</summary>
    Task<DocumentFolderCountOutcome> CountAsync(SearchCaller caller, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Folders tree of the Documents browser (#179, Q-55): the imported folder paths as a read-only tree, loaded one level
/// at a time. A folder is listed only when it (with its subfolders) holds a document the caller may see; restricted and
/// walled documents are neither counted nor do they reveal a folder (Q-52), and an unknown or hidden folder answers like
/// one that does not exist. Requires <c>Search.Execute</c> (the counts are searches).
/// </summary>
public sealed class DocumentFolderService(IDocumentFolderStore store, IDocumentFolderCounts counts)
{
    public const int DefaultPageSize = 100;

    public const int MaxPageSize = 500;

    /// <summary>PostgreSQL pages read per request while skipping folders that hold nothing the caller may see.</summary>
    internal const int MaxScans = 10;

    public async Task<DocumentFolderListOutcome> ListAsync(
        SearchCaller caller, Guid? parentFolderId, string? cursor, int? limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var size = limit ?? DefaultPageSize;
        if (size is < 1 or > MaxPageSize)
        {
            return DocumentFolderListOutcome.Invalid("limit", $"limit must be between 1 and {MaxPageSize}.");
        }

        string? after = null;
        if (cursor is not null && !DocumentFolderCursor.TryDecode(cursor, out after))
        {
            return DocumentFolderListOutcome.Invalid("cursor", "The cursor is not valid.");
        }

        DocumentFolderResource? parent = null;
        if (parentFolderId is { } parentId)
        {
            var found = await GetAsync(caller, parentId, cancellationToken).ConfigureAwait(false);
            if (found.Status != DocumentFolderCountStatus.Ok)
            {
                return new DocumentFolderListOutcome(found.Status);
            }

            parent = found.Folder;
        }

        var items = new List<DocumentFolderResource>();
        var approximate = false;
        var more = true;
        for (var scan = 0; scan < MaxScans && more && items.Count < size; scan++)
        {
            var rows = await store.ListChildrenAsync(caller.WorkspaceId, parentFolderId, after, size, cancellationToken).ConfigureAwait(false);
            more = rows.Count == size;
            if (rows.Count == 0)
            {
                break;
            }

            var counted = await counts.CountAsync(caller, [.. rows.Select(r => r.FolderId)], cancellationToken).ConfigureAwait(false);
            if (counted.Status != DocumentFolderCountStatus.Ok)
            {
                return new DocumentFolderListOutcome(counted.Status);
            }

            approximate |= counted.Approximate;
            foreach (var row in rows)
            {
                after = row.SortKey;
                if (counted.Counts.GetValueOrDefault(row.FolderId) is { Total: > 0 } count)
                {
                    items.Add(ToResource(row, count));
                    if (items.Count == size)
                    {
                        // Folders after this one may remain on the page just read.
                        more = more || !ReferenceEquals(row, rows[^1]);
                        break;
                    }
                }
            }
        }

        return new DocumentFolderListOutcome(DocumentFolderCountStatus.Ok, new DocumentFolderPage(
            parent, items, more && after is not null ? DocumentFolderCursor.Encode(after) : null, approximate));
    }

    /// <summary>One folder with the path to it, for a link or a restored selection; hidden folders are not found.</summary>
    public async Task<DocumentFolderOutcome> GetAsync(SearchCaller caller, Guid folderId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var rows = await store.GetAsync(caller.WorkspaceId, [folderId], cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            // Still ask the PDP, so an unknown folder answers exactly like a forbidden workspace would.
            var check = await counts.CountAsync(caller, [], cancellationToken).ConfigureAwait(false);
            return new DocumentFolderOutcome(check.Status == DocumentFolderCountStatus.Forbidden ? DocumentFolderCountStatus.Forbidden : DocumentFolderCountStatus.NotFound);
        }

        var row = rows[0];
        var ancestors = await store.GetAsync(caller.WorkspaceId, [.. row.AncestorIds], cancellationToken).ConfigureAwait(false);
        var counted = await counts.CountAsync(caller, [.. row.AncestorIds], cancellationToken).ConfigureAwait(false);
        if (counted.Status != DocumentFolderCountStatus.Ok)
        {
            return new DocumentFolderOutcome(counted.Status);
        }

        var count = counted.Counts.GetValueOrDefault(folderId) ?? new DocumentFolderCount(0, 0);
        if (count.Total == 0)
        {
            return new DocumentFolderOutcome(DocumentFolderCountStatus.NotFound);
        }

        var byId = ancestors.ToDictionary(a => a.FolderId);
        var path = row.AncestorIds.Take(row.AncestorIds.Count - 1)
            .Where(byId.ContainsKey)
            .Select(id => ToResource(byId[id], counted.Counts.GetValueOrDefault(id) ?? new DocumentFolderCount(0, 0)))
            .ToList();
        return new DocumentFolderOutcome(DocumentFolderCountStatus.Ok, ToResource(row, count) with { Ancestors = path }, counted.Approximate);
    }

    private static DocumentFolderResource ToResource(DocumentFolderRow row, DocumentFolderCount count) => new(
        row.FolderId, row.ParentFolderId, row.Name, row.Path, row.Depth, count.Documents, count.Total, count.Total > count.Documents);
}

public sealed record DocumentFolderListOutcome(
    DocumentFolderCountStatus Status, DocumentFolderPage? Page = null, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static DocumentFolderListOutcome Invalid(string field, string message) =>
        new(DocumentFolderCountStatus.Ok, null, new Dictionary<string, string[]> { [field] = [message] });
}

public sealed record DocumentFolderOutcome(DocumentFolderCountStatus Status, DocumentFolderResource? Folder = null, bool Approximate = false);

/// <summary>Opaque continuation of a children page: the last folder's sort key.</summary>
internal static class DocumentFolderCursor
{
    public static string Encode(string sortKey) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sortKey)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string cursor, out string? sortKey)
    {
        sortKey = null;
        if (cursor.Length is 0 or > 12_000)
        {
            return false;
        }

        var base64 = cursor.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        try
        {
            sortKey = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return sortKey.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
