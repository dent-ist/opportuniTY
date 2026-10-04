using System.Diagnostics;
using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Contracts.Search;
using Opportunity.Core.QueryLanguage;

namespace Opportunity.Application.Search.SavedSearches;

public enum SavedSearchOutcomeStatus
{
    Ok,
    Created,

    /// <summary>Does not exist or is not visible to the caller: 404 (never 403, Q-65).</summary>
    NotFound,

    /// <summary>Visible, but only its owner or a Workspace Admin may change it: 403.</summary>
    Forbidden,

    /// <summary>The <c>If-Match</c> version is stale: 412.</summary>
    VersionConflict,

    InvalidRequest,

    /// <summary>The query does not parse, bind or nest (unknown or deleted field, cycle, missing reference): 400 with positioned errors.</summary>
    InvalidQuery,

    /// <summary>A sibling folder has the name: 409.</summary>
    Conflict,

    /// <summary>The folder still holds folders or saved searches: 409 <c>FOLDER_NOT_EMPTY</c>.</summary>
    FolderNotEmpty,
}

public sealed record SavedSearchOutcome
{
    public required SavedSearchOutcomeStatus Status { get; init; }

    public SavedSearchRecord? Search { get; init; }

    public SavedSearchFolderRecord? Folder { get; init; }

    public IReadOnlyDictionary<string, string[]> RequestErrors { get; init; } = new Dictionary<string, string[]>();

    public IReadOnlyList<QueryValidationDiagnostic> QueryErrors { get; init; } = [];

    internal static SavedSearchOutcome Of(SavedSearchOutcomeStatus status) => new() { Status = status };

    internal static SavedSearchOutcome Invalid(string field, string message) =>
        new() { Status = SavedSearchOutcomeStatus.InvalidRequest, RequestErrors = new Dictionary<string, string[]> { [field] = [message] } };

    internal static SavedSearchOutcome InvalidQuery(IEnumerable<QueryDiagnostic> errors) => new()
    {
        Status = SavedSearchOutcomeStatus.InvalidQuery,
        QueryErrors = [.. errors.Select(d => new QueryValidationDiagnostic(d.Code, d.Message, new TextSpan(d.Span.Start, d.Span.End), d.Expected))],
    };
}

/// <summary>
/// The Searches section's use cases (E07-T09, Q-65): saved searches and their folders. Callers see their own searches,
/// those shared with them or one of their IdP groups, and every search as Workspace Admin; anything else answers
/// <see cref="SavedSearchOutcomeStatus.NotFound"/>. Only the owner or a Workspace Admin changes, shares or deletes a
/// search (sharing also needs <c>SavedSearch.Share</c>, declared by the endpoint). Criteria are validated like a search
/// (parse, nested references with cycle detection, binding against the workspace's fields) when saved, and re-parsed at
/// every run. Create, modify, share and delete are audited in the transaction of the change; runs are audited by the
/// search path (<c>Search.Executed</c> with the saved search's ID).
/// </summary>
public sealed class SavedSearchService(
    ISavedSearchStore store,
    ISavedSearchQueries queries,
    IAuthorizationService authorization,
    QueryLimits limits,
    TimeProvider time,
    IQueryBinder? binder = null)
{
    public const int MaxNameLength = 200;
    public const int MaxColumns = 100;
    public const int MaxShares = 200;
    private const string ResourceType = "SavedSearch";
    private const string CorrelationTag = "opportunity.correlation_id";

    public async Task<SavedSearchPage> ListAsync(
        SecurityPrincipal principal, Guid workspaceId, SavedSearchListFilter filter, CancellationToken cancellationToken = default)
    {
        var viewer = await SavedSearchQueries.ViewerAsync(authorization, principal, workspaceId, cancellationToken).ConfigureAwait(false);
        return await store.ListAsync(workspaceId, viewer, filter, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedSearchOutcome> GetAsync(SecurityPrincipal principal, Guid workspaceId, Guid savedSearchId, CancellationToken cancellationToken = default)
    {
        var viewer = await SavedSearchQueries.ViewerAsync(authorization, principal, workspaceId, cancellationToken).ConfigureAwait(false);
        return await store.GetAsync(workspaceId, savedSearchId, viewer, cancellationToken).ConfigureAwait(false) is { } search
            ? new SavedSearchOutcome { Status = SavedSearchOutcomeStatus.Ok, Search = search }
            : SavedSearchOutcome.Of(SavedSearchOutcomeStatus.NotFound);
    }

    public async Task<SavedSearchOutcome> CreateAsync(
        SecurityPrincipal principal, Guid workspaceId, SavedSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var id = Guid.CreateVersion7();
        var validated = await ValidateAsync(principal, workspaceId, request, null, cancellationToken).ConfigureAwait(false);
        if (validated.Problem is { } problem)
        {
            return problem;
        }

        var definition = validated.Definition!;
        var audit = Event(principal, workspaceId, id, AuditTaxonomy.SavedSearch.Created, Details(id, definition, 1), Restricted(definition));
        var result = await store.CreateAsync(workspaceId, id, principal.UserId, definition, audit, cancellationToken).ConfigureAwait(false);
        return Write(result, SavedSearchOutcomeStatus.Created);
    }

    public async Task<SavedSearchOutcome> UpdateAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid savedSearchId, long? expectedVersion, SavedSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var (current, denied) = await ForChangeAsync(principal, workspaceId, savedSearchId, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        var validated = await ValidateAsync(principal, workspaceId, request, savedSearchId, cancellationToken).ConfigureAwait(false);
        if (validated.Problem is { } problem)
        {
            return problem;
        }

        var definition = validated.Definition!;
        var details = Details(savedSearchId, definition, current!.Version + 1);
        details["changed"] = string.Join(',', Changed(current, definition));
        details["ownerId"] = current.OwnerId.ToString();
        var audit = Event(principal, workspaceId, savedSearchId, AuditTaxonomy.SavedSearch.Modified, details, Restricted(definition));
        var result = await store.UpdateAsync(workspaceId, savedSearchId, expectedVersion, definition, audit, cancellationToken).ConfigureAwait(false);
        return Write(result, SavedSearchOutcomeStatus.Ok);
    }

    public async Task<SavedSearchOutcome> DeleteAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid savedSearchId, long? expectedVersion, CancellationToken cancellationToken = default)
    {
        var (current, denied) = await ForChangeAsync(principal, workspaceId, savedSearchId, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        var audit = Event(principal, workspaceId, savedSearchId, AuditTaxonomy.SavedSearch.Deleted,
            new Dictionary<string, string?> { ["savedSearchId"] = savedSearchId.ToString(), ["ownerId"] = current!.OwnerId.ToString() },
            new Dictionary<string, string?> { ["query"] = current.QueryText });
        var result = await store.DeleteAsync(workspaceId, savedSearchId, expectedVersion, audit, cancellationToken).ConfigureAwait(false);
        return Write(result, SavedSearchOutcomeStatus.Ok);
    }

    /// <summary>Copies a visible search as a new private search of the caller (criteria copied as they are).</summary>
    public async Task<SavedSearchOutcome> CloneAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid savedSearchId, SavedSearchCloneRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await SavedSearchQueries.ViewerAsync(authorization, principal, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await store.GetAsync(workspaceId, savedSearchId, viewer, cancellationToken).ConfigureAwait(false) is not { } source)
        {
            return SavedSearchOutcome.Of(SavedSearchOutcomeStatus.NotFound);
        }

        var name = request.Name?.Trim() ?? Truncate("Copy of " + source.Name);
        if (name.Length is 0 or > MaxNameLength)
        {
            return SavedSearchOutcome.Invalid("name", $"The name must be 1–{MaxNameLength} characters.");
        }

        var id = Guid.CreateVersion7();
        var definition = new SavedSearchDefinition(name, request.FolderId ?? source.FolderId, source.QueryText, source.AstVersion, source.Columns,
            source.Sort, source.IncludeFamily, source.References);
        var details = Details(id, definition, 1);
        details["clonedFrom"] = savedSearchId.ToString();
        var audit = Event(principal, workspaceId, id, AuditTaxonomy.SavedSearch.Created, details, Restricted(definition));
        var result = await store.CreateAsync(workspaceId, id, principal.UserId, definition, audit, cancellationToken).ConfigureAwait(false);
        return Write(result, SavedSearchOutcomeStatus.Created);
    }

    /// <summary>Replaces who the search is shared with (owner or Workspace Admin; the endpoint requires <c>SavedSearch.Share</c>).</summary>
    public async Task<SavedSearchOutcome> ShareAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid savedSearchId, long? expectedVersion, SavedSearchSharingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (current, denied) = await ForChangeAsync(principal, workspaceId, savedSearchId, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        if (request.SharedWith is null)
        {
            return SavedSearchOutcome.Invalid("sharedWith", "Give the complete list of users and groups (empty to make the search private).");
        }

        var targets = new List<SavedSearchShareTarget>();
        foreach (var target in request.SharedWith)
        {
            if (target is null || !Enum.IsDefined(target.Kind) || string.IsNullOrWhiteSpace(target.Id))
            {
                return SavedSearchOutcome.Invalid("sharedWith", "Each entry needs kind (user or group) and id.");
            }

            if (target.Kind == SavedSearchShareKind.User)
            {
                if (!Guid.TryParse(target.Id, out var userId))
                {
                    return SavedSearchOutcome.Invalid("sharedWith", "A user is identified by their user ID.");
                }

                targets.Add(new SavedSearchShareTarget(SavedSearchShareKind.User, userId.ToString()));
            }
            else
            {
                var group = target.Id.Trim();
                if (group.Length > 256)
                {
                    return SavedSearchOutcome.Invalid("sharedWith", "Group names have at most 256 characters.");
                }

                targets.Add(new SavedSearchShareTarget(SavedSearchShareKind.Group, group));
            }
        }

        targets = [.. targets.Distinct()];
        if (targets.Count > MaxShares)
        {
            return SavedSearchOutcome.Invalid("sharedWith", $"Share with at most {MaxShares} users and groups; use IdP groups for larger audiences.");
        }

        var details = new Dictionary<string, string?>
        {
            ["savedSearchId"] = savedSearchId.ToString(),
            ["ownerId"] = current!.OwnerId.ToString(),
            ["scope"] = targets.Count == 0 ? "private" : "shared",
            ["users"] = Invariant(targets.Count(t => t.Kind == SavedSearchShareKind.User)),
            ["userIds"] = targets.Count(t => t.Kind == SavedSearchShareKind.User) <= 50
                ? string.Join(',', targets.Where(t => t.Kind == SavedSearchShareKind.User).Select(t => t.Id))
                : null,
            ["groups"] = Invariant(targets.Count(t => t.Kind == SavedSearchShareKind.Group)),
            ["previousShares"] = Invariant(current.Shares.Count),
            ["version"] = Invariant(current.Version + 1),
        };
        var audit = Event(principal, workspaceId, savedSearchId, AuditTaxonomy.SavedSearch.Shared, details, null);
        var result = await store.SetSharingAsync(workspaceId, savedSearchId, expectedVersion, targets, audit, cancellationToken).ConfigureAwait(false);
        return result.Status == SavedSearchWriteStatus.UnknownUser
            ? SavedSearchOutcome.Invalid("sharedWith", "Every user must be a known user of the installation.")
            : Write(result, SavedSearchOutcomeStatus.Ok);
    }

    /// <summary>Who the caller may share with (the endpoint requires <c>SavedSearch.Share</c>): workspace members and role-holding groups.</summary>
    public Task<IReadOnlyList<SavedSearchShareResource>> ListShareCandidatesAsync(
        SecurityPrincipal principal, Guid workspaceId, string? nameContains, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return store.ListShareCandidatesAsync(workspaceId, nameContains, principal.UserId, limit, cancellationToken);
    }

    public Task<IReadOnlyList<SavedSearchFolderRecord>> ListFoldersAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ListFoldersAsync(workspaceId, cancellationToken);

    public Task<SavedSearchFolderRecord?> GetFolderAsync(Guid workspaceId, Guid folderId, CancellationToken cancellationToken = default) =>
        store.GetFolderAsync(workspaceId, folderId, cancellationToken);

    public async Task<SavedSearchOutcome> CreateFolderAsync(
        SecurityPrincipal principal, Guid workspaceId, SavedSearchFolderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        if (FolderName(request) is not { } name)
        {
            return SavedSearchOutcome.Invalid("name", $"The name must be 1–{MaxNameLength} characters.");
        }

        var result = await store.CreateFolderAsync(workspaceId, Guid.CreateVersion7(), name, request.ParentFolderId, principal.UserId, cancellationToken)
            .ConfigureAwait(false);
        return FolderWrite(result, SavedSearchOutcomeStatus.Created);
    }

    public async Task<SavedSearchOutcome> UpdateFolderAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid folderId, long? expectedVersion, SavedSearchFolderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await FolderForChangeAsync(principal, workspaceId, folderId, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (FolderName(request) is not { } name)
        {
            return SavedSearchOutcome.Invalid("name", $"The name must be 1–{MaxNameLength} characters.");
        }

        var result = await store.UpdateFolderAsync(workspaceId, folderId, expectedVersion, name, request.ParentFolderId, cancellationToken)
            .ConfigureAwait(false);
        return FolderWrite(result, SavedSearchOutcomeStatus.Ok);
    }

    public async Task<SavedSearchOutcome> DeleteFolderAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid folderId, long? expectedVersion, CancellationToken cancellationToken = default)
    {
        if (await FolderForChangeAsync(principal, workspaceId, folderId, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        return Write(await store.DeleteFolderAsync(workspaceId, folderId, expectedVersion, cancellationToken).ConfigureAwait(false),
            SavedSearchOutcomeStatus.Ok);
    }

    private static string? FolderName(SavedSearchFolderRequest request) =>
        request.Name?.Trim() is { Length: > 0 and <= MaxNameLength } name ? name : null;

    /// <summary>The folder's creator or a Workspace Admin may rename, move or delete it.</summary>
    private async Task<SavedSearchOutcome?> FolderForChangeAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid folderId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (await store.GetFolderAsync(workspaceId, folderId, cancellationToken).ConfigureAwait(false) is not { } folder)
        {
            return SavedSearchOutcome.Of(SavedSearchOutcomeStatus.NotFound);
        }

        if (folder.CreatedBy == principal.UserId)
        {
            return null;
        }

        var effective = await authorization.GetEffectivePermissionsAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        return SavedSearchQueries.IsAdministrator(effective) ? null : SavedSearchOutcome.Of(SavedSearchOutcomeStatus.Forbidden);
    }

    /// <summary>The visible search, or 404 when invisible and 403 when visible but neither owned nor administered.</summary>
    private async Task<(SavedSearchRecord? Current, SavedSearchOutcome? Denied)> ForChangeAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid savedSearchId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var viewer = await SavedSearchQueries.ViewerAsync(authorization, principal, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await store.GetAsync(workspaceId, savedSearchId, viewer, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return (null, SavedSearchOutcome.Of(SavedSearchOutcomeStatus.NotFound));
        }

        return current.OwnerId == principal.UserId || viewer.SeesAll
            ? (current, null)
            : (current, SavedSearchOutcome.Of(SavedSearchOutcomeStatus.Forbidden));
    }

    /// <summary>Request checks, then the criteria exactly as a run would see them: parse, expand nested searches, bind.</summary>
    private async Task<(SavedSearchDefinition? Definition, SavedSearchOutcome? Problem)> ValidateAsync(
        SecurityPrincipal principal, Guid workspaceId, SavedSearchRequest request, Guid? self, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return (null, SavedSearchOutcome.Invalid("name", $"The name must be 1–{MaxNameLength} characters."));
        }

        if (request.Query is null)
        {
            return (null, SavedSearchOutcome.Invalid("query", "The query text is required (it may be empty: every document the runner may see)."));
        }

        var columns = new List<string>();
        foreach (var column in request.Columns ?? [])
        {
            var trimmed = column?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 200)
            {
                return (null, SavedSearchOutcome.Invalid("columns", "Columns are field query names of 1–200 characters."));
            }

            columns.Add(trimmed);
        }

        if (columns.Count > MaxColumns || columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count)
        {
            return (null, SavedSearchOutcome.Invalid("columns", $"At most {MaxColumns} distinct columns."));
        }

        var sort = new List<SearchSortKey>();
        foreach (var key in request.Sort ?? [])
        {
            if (key is null || !Enum.IsDefined(key.Direction)
                || SearchSortFields.All.FirstOrDefault(f => string.Equals(f, key.Field, StringComparison.OrdinalIgnoreCase)) is not { } field)
            {
                return (null, SavedSearchOutcome.Invalid("sort", $"Sortable fields: {string.Join(", ", SearchSortFields.All)}."));
            }

            sort.Add(new SearchSortKey(field, key.Direction));
        }

        if (sort.Count > 5 || sort.Select(s => s.Field).Distinct(StringComparer.Ordinal).Count() != sort.Count)
        {
            return (null, SavedSearchOutcome.Invalid("sort", "At most 5 distinct sort fields."));
        }

        var parsed = QueryParser.Parse(request.Query, limits);
        if (parsed.Ast is not { } ast)
        {
            return (null, SavedSearchOutcome.InvalidQuery(parsed.Errors));
        }

        var expansion = await queries.ExpandAsync(new SavedSearchExpansionRequest(workspaceId, ast, principal, self), cancellationToken)
            .ConfigureAwait(false);
        if (!expansion.Success)
        {
            return (null, SavedSearchOutcome.InvalidQuery(expansion.Errors));
        }

        if (binder is not null)
        {
            var errors = await binder.BindAsync(workspaceId, expansion.Ast, cancellationToken).ConfigureAwait(false);
            if (errors.Count > 0)
            {
                return (null, SavedSearchOutcome.InvalidQuery(expansion.Annotate(errors)));
            }
        }

        return (new SavedSearchDefinition(name, request.FolderId, request.Query, QueryNode.AstVersion, columns, sort, request.IncludeFamily ?? false,
            expansion.DirectReferences), null);
    }

    private static SavedSearchOutcome Write(SavedSearchWriteResult result, SavedSearchOutcomeStatus success) => result.Status switch
    {
        SavedSearchWriteStatus.Ok => new SavedSearchOutcome { Status = success, Search = result.Search, Folder = result.Folder },
        SavedSearchWriteStatus.VersionConflict => SavedSearchOutcome.Of(SavedSearchOutcomeStatus.VersionConflict),
        SavedSearchWriteStatus.FolderNotFound => SavedSearchOutcome.Invalid("folderId", "No such folder in this workspace."),
        SavedSearchWriteStatus.FolderCycle => SavedSearchOutcome.Invalid("parentFolderId", "A folder cannot move into itself or one of its subfolders."),
        SavedSearchWriteStatus.DuplicateName => SavedSearchOutcome.Of(SavedSearchOutcomeStatus.Conflict),
        SavedSearchWriteStatus.FolderNotEmpty => SavedSearchOutcome.Of(SavedSearchOutcomeStatus.FolderNotEmpty),
        SavedSearchWriteStatus.UnknownUser => SavedSearchOutcome.Invalid("sharedWith", "Every user must be a known user of the installation."),
        _ => SavedSearchOutcome.Of(SavedSearchOutcomeStatus.NotFound),
    };

    private static SavedSearchOutcome FolderWrite(SavedSearchWriteResult result, SavedSearchOutcomeStatus success) =>
        result.Status == SavedSearchWriteStatus.FolderNotFound
            ? SavedSearchOutcome.Invalid("parentFolderId", "No such parent folder in this workspace.")
            : Write(result, success);

    private static IEnumerable<string> Changed(SavedSearchRecord current, SavedSearchDefinition next)
    {
        if (!string.Equals(current.Name, next.Name, StringComparison.Ordinal))
        {
            yield return "name";
        }

        if (current.FolderId != next.FolderId)
        {
            yield return "folderId";
        }

        if (!string.Equals(current.QueryText, next.QueryText, StringComparison.Ordinal))
        {
            yield return "query";
        }

        if (!current.Columns.SequenceEqual(next.Columns, StringComparer.Ordinal))
        {
            yield return "columns";
        }

        if (!current.Sort.SequenceEqual(next.Sort))
        {
            yield return "sort";
        }

        if (current.IncludeFamily != next.IncludeFamily)
        {
            yield return "includeFamily";
        }
    }

    /// <summary>IDs and settings only: names are user content and stay out of audit details (ADR-013 §7).</summary>
    private static Dictionary<string, string?> Details(Guid id, SavedSearchDefinition definition, long version) => new()
    {
        ["savedSearchId"] = id.ToString(),
        ["folderId"] = definition.FolderId?.ToString(),
        ["astVersion"] = Invariant(definition.AstVersion),
        ["columns"] = Invariant(definition.Columns.Count),
        ["sort"] = string.Join(',', definition.Sort.Select(s => s.Field + ":" + (s.Direction == SearchSortDirection.Desc ? "desc" : "asc"))),
        ["includeFamily"] = definition.IncludeFamily ? "true" : "false",
        ["references"] = definition.References.Count <= 50 ? string.Join(',', definition.References) : Invariant(definition.References.Count),
        ["version"] = Invariant(version),
    };

    /// <summary>Q-16: the criteria are search text, readable only with <c>Audit.ReadSearchText</c>.</summary>
    private static Dictionary<string, string?> Restricted(SavedSearchDefinition definition) => new() { ["query"] = definition.QueryText };

    private AuditEvent Event(
        SecurityPrincipal actor, Guid workspaceId, Guid savedSearchId, string action, IReadOnlyDictionary<string, string?> details,
        IReadOnlyDictionary<string, string?>? restricted) => new()
        {
            WorkspaceId = workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.SavedSearch.Category,
            Action = action,
            ActorType = AuditActorType.User,
            ActorId = actor.UserId.ToString(),
            ActorDisplay = actor.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? actor.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : actor.DisplayName,
            ClientIp = actor.ClientIp,
            UserAgent = actor.UserAgent,
            ResourceType = ResourceType,
            ResourceId = savedSearchId.ToString(),
            Outcome = AuditOutcome.Success,
            CorrelationId = actor.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = details,
            RestrictedDetails = restricted is not null && Fits(restricted) ? restricted : null,
        };

    private static bool Fits(IReadOnlyDictionary<string, string?> restricted) =>
        restricted.Values.Sum(v => v?.Length ?? 0) <= AuditEventRules.MaxRestrictedDetailsBytes / 2;

    private static string Truncate(string name) => name.Length <= MaxNameLength ? name : name[..MaxNameLength];

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
