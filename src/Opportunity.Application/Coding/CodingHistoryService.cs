using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Identity;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;

namespace Opportunity.Application.Coding;

/// <summary>One change in a document's coding history, with the field and the author resolved.</summary>
/// <param name="ActorDisplayName">The author's display name, or null when the directory has none.</param>
public sealed record CodingHistoryEntry(CodingEvent Event, FieldDefinition Field, string? ActorDisplayName);

public enum CodingHistoryStatus
{
    Ok,

    /// <summary>The document does not exist or is hidden from the caller: the document 404.</summary>
    NotFound,

    /// <summary>The field filter names no field the caller may see.</summary>
    UnknownField,
}

public sealed record CodingHistoryPage(CodingHistoryStatus Status, IReadOnlyList<CodingHistoryEntry> Entries, CodingEventCursor? Next)
{
    internal static CodingHistoryPage Of(CodingHistoryStatus status) => new(status, [], null);
}

/// <summary>
/// A document's coding history (E10-T05; baseline §14, §27): who changed which field when, from which value to which,
/// interactively or by which job, and with which actor type (legal finding 15), newest first, read from CodingEvent
/// provenance. The PDP decides the document (hidden or unknown → the document 404, like the coding and content routes)
/// and field-level restrictions (E05-T06) apply: the history of a field hidden from the caller, or of a deleted field,
/// is never returned, and naming one as the filter answers like an unknown field.
/// </summary>
public sealed class CodingHistoryService(
    ICodingRepository coding,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess,
    IAuthorizationService authorization,
    IUserDirectory users)
{
    public const int MaxLimit = 500;

    public async Task<CodingHistoryPage> GetAsync(
        CodingCaller caller, Guid documentId, int? fieldId, CodingEventCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);
        var decision = await authorization.AuthorizeAsync(caller.Principal, caller.WorkspaceId, Permission.DocumentView, documentId, cancellationToken)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return CodingHistoryPage.Of(CodingHistoryStatus.NotFound);
        }

        var catalog = await fields.GetCatalogAsync(caller.WorkspaceId, includeDeleted: true, cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(caller.WorkspaceId, caller.Principal, catalog, cancellationToken).ConfigureAwait(false);
        var excluded = catalog.Fields.Where(f => f.IsDeleted).Select(f => f.FieldId).Concat(restricted).ToHashSet();
        if (fieldId is { } only && (catalog.Find(only) is null || excluded.Contains(only)))
        {
            return CodingHistoryPage.Of(CodingHistoryStatus.UnknownField);
        }

        var page = await coding.GetEventsAsync(new CodingEventQuery(caller.WorkspaceId)
        {
            DocumentId = documentId,
            FieldId = fieldId,
            ExcludedFieldIds = excluded,
            After = after,
            Limit = limit,
            Descending = true,
        }, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<Guid, string> names = page.Events.Count == 0
            ? new Dictionary<Guid, string>()
            : await users.GetDisplayNamesAsync([.. page.Events.Select(e => e.ActorId).Distinct()], cancellationToken).ConfigureAwait(false);
        var entries = new List<CodingHistoryEntry>(page.Events.Count);
        foreach (var e in page.Events)
        {
            // Events of fields unknown to the catalogue cannot exist (foreign key); skip defensively rather than leak an ID.
            if (catalog.Find(e.FieldId) is { } field)
            {
                entries.Add(new CodingHistoryEntry(e, field, names.GetValueOrDefault(e.ActorId)));
            }
        }

        return new CodingHistoryPage(CodingHistoryStatus.Ok, entries, page.Next);
    }
}
