using Opportunity.Application.Audit;
using Opportunity.Contracts.Search;

namespace Opportunity.Application.Search.SavedSearches;

/// <summary>
/// Who looks at saved searches: their own, those shared with them or one of their IdP groups, or every search of the
/// workspace (<see cref="SeesAll"/>, Workspace Admins). Built from the PDP's effective permissions, never from the request.
/// </summary>
public sealed record SavedSearchViewer(Guid UserId, IReadOnlyList<string> Groups, bool SeesAll);

/// <summary>The most recent run of a saved search (by anyone; the count is that runner's).</summary>
/// <param name="Current">Q-10: every change committed before the run was searchable; null when unknown.</param>
public sealed record SavedSearchLastRun(DateTimeOffset At, long HitCount, bool Exact, bool? Current, long? ServedGeneration);

public sealed record SavedSearchRecord
{
    public required Guid WorkspaceId { get; init; }

    public required Guid SavedSearchId { get; init; }

    public required string Name { get; init; }

    public Guid? FolderId { get; init; }

    public required Guid OwnerId { get; init; }

    public required string OwnerDisplayName { get; init; }

    /// <summary>ADR-008 query text, re-parsed at every run (Q-16 search text).</summary>
    public required string QueryText { get; init; }

    public required int AstVersion { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];

    public IReadOnlyList<SearchSortKey> Sort { get; init; } = [];

    public bool IncludeFamily { get; init; }

    /// <summary>Run with the hits' duplicates (E09-T03).</summary>
    public bool IncludeDuplicates { get; init; }

    /// <summary>Run with the hits' email threads (E09-T03).</summary>
    public bool IncludeThread { get; init; }

    /// <summary>The expansion a run applies unless the request gives its own.</summary>
    public Core.Documents.RelationshipExpansion Expansion => new(IncludeFamily, IncludeDuplicates, IncludeThread);

    /// <summary>The saved searches the query references directly (<c>savedsearch:&lt;id&gt;</c>), for cycle checks.</summary>
    public IReadOnlyList<Guid> References { get; init; } = [];

    public IReadOnlyList<SavedSearchShareResource> Shares { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    public required long Version { get; init; }

    public SavedSearchLastRun? LastRun { get; init; }
}

/// <summary>What a new saved search or a replacement holds (owner and sharing are set elsewhere).</summary>
public sealed record SavedSearchDefinition(
    string Name,
    Guid? FolderId,
    string QueryText,
    int AstVersion,
    IReadOnlyList<string> Columns,
    IReadOnlyList<SearchSortKey> Sort,
    bool IncludeFamily,
    IReadOnlyList<Guid> References)
{
    /// <summary>Run with the hits' duplicates (E09-T03).</summary>
    public bool IncludeDuplicates { get; init; }

    /// <summary>Run with the hits' email threads (E09-T03).</summary>
    public bool IncludeThread { get; init; }
}

/// <summary>The criteria of one saved search, for nesting (<c>savedsearch:&lt;id&gt;</c>).</summary>
public sealed record SavedSearchCriteria(Guid SavedSearchId, string Name, string QueryText, IReadOnlyList<Guid> References);

/// <summary>Keyset position of the list (ordered by name, then ID).</summary>
public sealed record SavedSearchListPosition(string Name, Guid SavedSearchId);

/// <param name="FolderId">Only searches in this folder (null: every folder).</param>
/// <param name="NameContains">Case-insensitive substring of the name.</param>
public sealed record SavedSearchListFilter(Guid? FolderId, string? NameContains, SavedSearchListPosition? After, int Limit);

public sealed record SavedSearchPage(IReadOnlyList<SavedSearchRecord> Items, bool HasMore);

public sealed record SavedSearchFolderRecord(Guid WorkspaceId, Guid FolderId, string Name, Guid? ParentFolderId, Guid CreatedBy, long Version);

public enum SavedSearchWriteStatus
{
    Ok,
    NotFound,
    VersionConflict,

    /// <summary>The folder (or parent folder) does not exist in the workspace.</summary>
    FolderNotFound,

    /// <summary>A sibling folder already has the name.</summary>
    DuplicateName,

    /// <summary>A folder cannot move below itself.</summary>
    FolderCycle,

    /// <summary>A folder still holds folders or saved searches.</summary>
    FolderNotEmpty,

    /// <summary>A share target names a user that does not exist.</summary>
    UnknownUser,
}

public sealed record SavedSearchWriteResult(SavedSearchWriteStatus Status, SavedSearchRecord? Search = null, SavedSearchFolderRecord? Folder = null);

/// <summary>
/// PostgreSQL store of saved searches, their sharing and folders (V0028, tenant tables under forced RLS). Every change
/// inserts its audit event in the transaction of the change (ADR-013 §2.1), so neither commits without the other.
/// Implemented by <c>Opportunity.Data</c>.
/// </summary>
public interface ISavedSearchStore
{
    /// <summary>The search, or null when it does not exist or <paramref name="viewer"/> (when given) may not see it.</summary>
    Task<SavedSearchRecord?> GetAsync(Guid workspaceId, Guid savedSearchId, SavedSearchViewer? viewer, CancellationToken cancellationToken = default);

    Task<SavedSearchPage> ListAsync(Guid workspaceId, SavedSearchViewer viewer, SavedSearchListFilter filter, CancellationToken cancellationToken = default);

    /// <summary>The criteria of the given searches that exist (and that <paramref name="viewer"/> may see, when given). One round trip.</summary>
    Task<IReadOnlyDictionary<Guid, SavedSearchCriteria>> GetCriteriaAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> savedSearchIds, SavedSearchViewer? viewer, CancellationToken cancellationToken = default);

    Task<SavedSearchWriteResult> CreateAsync(
        Guid workspaceId, Guid savedSearchId, Guid ownerId, SavedSearchDefinition definition, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>Replaces the definition when the version matches (<paramref name="expectedVersion"/> null: any).</summary>
    Task<SavedSearchWriteResult> UpdateAsync(
        Guid workspaceId, Guid savedSearchId, long? expectedVersion, SavedSearchDefinition definition, AuditEvent audit,
        CancellationToken cancellationToken = default);

    Task<SavedSearchWriteResult> DeleteAsync(
        Guid workspaceId, Guid savedSearchId, long? expectedVersion, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>Replaces the share list (and bumps the version).</summary>
    Task<SavedSearchWriteResult> SetSharingAsync(
        Guid workspaceId, Guid savedSearchId, long? expectedVersion, IReadOnlyList<SavedSearchShareTarget> shares, AuditEvent audit,
        CancellationToken cancellationToken = default);

    /// <summary>Records a run as the search's last run; does not change its version.</summary>
    Task RecordRunAsync(Guid workspaceId, Guid savedSearchId, SavedSearchLastRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Users and IdP groups with a role in the workspace (users also through a group of theirs), except
    /// <paramref name="excludeUserId"/>, whose name (or e-mail) contains <paramref name="nameContains"/>; by display name.
    /// </summary>
    Task<IReadOnlyList<SavedSearchShareResource>> ListShareCandidatesAsync(
        Guid workspaceId, string? nameContains, Guid excludeUserId, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SavedSearchFolderRecord>> ListFoldersAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<SavedSearchFolderRecord?> GetFolderAsync(Guid workspaceId, Guid folderId, CancellationToken cancellationToken = default);

    Task<SavedSearchWriteResult> CreateFolderAsync(
        Guid workspaceId, Guid folderId, string name, Guid? parentFolderId, Guid createdBy, CancellationToken cancellationToken = default);

    Task<SavedSearchWriteResult> UpdateFolderAsync(
        Guid workspaceId, Guid folderId, long? expectedVersion, string name, Guid? parentFolderId, CancellationToken cancellationToken = default);

    Task<SavedSearchWriteResult> DeleteFolderAsync(Guid workspaceId, Guid folderId, long? expectedVersion, CancellationToken cancellationToken = default);
}
