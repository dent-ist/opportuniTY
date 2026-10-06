using Opportunity.Application.Authorization;
using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;

namespace Opportunity.Application.Documents;

/// <summary>One live document of a relationship group, as PostgreSQL holds it.</summary>
public sealed record RelationshipRow(
    Guid DocumentId,
    string ControlNumber,
    string? FileName,
    DateTimeOffset? DocumentDate,
    Guid FamilyId,
    int FamilySequence,
    Guid? ParentDocumentId,
    Guid? DuplicateGroupId,
    bool IsDuplicatePrimary,
    Guid? EmailThreadId)
{
    /// <summary>Top-level document of its family (it may still be a family of one).</summary>
    public bool IsTopLevel => ParentDocumentId is null && FamilySequence == 0;
}

/// <summary>
/// The anchor document's family, duplicate group and email thread (each including the anchor, live documents only,
/// unauthorized), plus the attachments of other families listed, so a parent flag never relies on a hidden child.
/// </summary>
/// <param name="ThreadTruncated">The thread has more members than were read.</param>
public sealed record RelationshipNeighbourhood(
    RelationshipRow Anchor,
    IReadOnlyList<RelationshipRow> Family,
    IReadOnlyList<RelationshipRow> Duplicates,
    IReadOnlyList<RelationshipRow> Thread,
    bool ThreadTruncated,
    IReadOnlyList<(Guid FamilyId, Guid DocumentId)> OtherFamilyChildren);

/// <summary>PostgreSQL read of a document's relationship groups (E09-T05).</summary>
public interface IDocumentRelationshipViewReader
{
    /// <summary>Null when the document does not exist or is deleted. Each group holds at most <paramref name="memberLimit"/> rows.</summary>
    Task<RelationshipNeighbourhood?> GetAsync(Guid workspaceId, Guid documentId, int memberLimit, CancellationToken cancellationToken = default);
}

/// <summary>A related document as the caller may see it; <see cref="Coding"/> by query name, values as text.</summary>
public sealed record RelatedDocument(
    RelationshipRow Row, bool IsParent, bool IsSelf, IReadOnlyDictionary<string, IReadOnlyList<string>> Coding);

public sealed record RelationshipsView(
    Guid DocumentId,
    Guid? FamilyId,
    RelatedDocument? FamilyParent,
    IReadOnlyList<RelatedDocument> FamilyMembers,
    Guid? DuplicateGroupId,
    Guid? PrimaryDocumentId,
    IReadOnlyList<RelatedDocument> DuplicateMembers,
    Guid? EmailThreadId,
    IReadOnlyList<RelatedDocument> ThreadMembers,
    int ThreadTotal);

public enum RelationshipsStatus
{
    Ok,
    NotFound,
    Forbidden,
    Invalid,
}

public sealed record RelationshipsOutcome(RelationshipsStatus Status, RelationshipsView? View = null, IReadOnlyList<FieldError>? Errors = null);

/// <summary>
/// "Related items" of a document (E09-T05, wave-12 contract): its family, duplicates and email thread for the caller.
/// The anchor needs <c>Document.View</c> (hidden or unknown → the document 404). Every other member is authorized for
/// <c>Document.View</c> in one batched PDP call: members the caller may not see are omitted entirely (Q-52, Q-13): never
/// listed or counted, never making a document a parent, and a group in which the caller sees no other member is
/// answered exactly like no group at all.
/// </summary>
public sealed class DocumentRelationshipService(
    IDocumentRelationshipViewReader reader,
    IAuthorizationService authorization,
    ICodingRepository coding,
    IFieldCatalogRepository fields,
    IFieldAccessFilter fieldAccess)
{
    public const int MaxFields = 20;
    public const int MaxThreadMembers = 200;

    /// <summary>Rows read per group; a larger thread lists and counts its first ones only.</summary>
    public const int MemberReadLimit = 10_000;

    private const int AuthorizationBatch = 5_000;

    public async Task<RelationshipsOutcome> GetAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid documentId, IReadOnlyList<string> fieldNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(fieldNames);
        var decision = await authorization.AuthorizeAsync(principal, workspaceId, Permission.DocumentView, documentId, cancellationToken)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return new RelationshipsOutcome(decision.Outcome == AuthorizationOutcome.NotFound ? RelationshipsStatus.NotFound : RelationshipsStatus.Forbidden);
        }

        Dictionary<int, (string Name, FieldDefinition Field)> requested = [];
        FieldCatalog? catalog = null;
        if (fieldNames.Count > 0)
        {
            if (fieldNames.Count > MaxFields)
            {
                return Invalid($"At most {MaxFields} fields can be requested.");
            }

            catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
            var restricted = await fieldAccess.RestrictedFieldIdsAsync(workspaceId, principal, catalog, cancellationToken).ConfigureAwait(false);
            var byName = FieldQueryNames.Assign(catalog.Fields.Where(f => !f.IsDeleted))
                .ToDictionary(n => n.Value, n => n.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var name in fieldNames)
            {
                if (!byName.TryGetValue(name, out var fieldId) || catalog.Find(fieldId) is not { Storage: FieldStorage.Coding } field
                    || restricted.Contains(fieldId))
                {
                    return Invalid($"Unknown coding field '{name}'.");
                }

                requested[fieldId] = (name, field);
            }
        }

        var group = await reader.GetAsync(workspaceId, documentId, MemberReadLimit, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return new RelationshipsOutcome(RelationshipsStatus.NotFound);
        }

        var anchor = group.Anchor;
        var others = group.Family.Concat(group.Duplicates).Concat(group.Thread).Select(r => r.DocumentId)
            .Concat(group.OtherFamilyChildren.Select(c => c.DocumentId))
            .Where(id => id != anchor.DocumentId)
            .Distinct()
            .ToList();
        var decisions = new Dictionary<Guid, AuthorizationDecision>(others.Count);
        foreach (var batch in others.Chunk(AuthorizationBatch))
        {
            var batchDecisions = await authorization.AuthorizeManyAsync(
                principal, workspaceId, Permission.DocumentView, batch, DenialAudit.Summary, cancellationToken).ConfigureAwait(false);
            foreach (var (id, d) in batchDecisions)
            {
                decisions[id] = d;
            }
        }

        bool Visible(Guid id) => id == anchor.DocumentId || (decisions.TryGetValue(id, out var d) && d.IsAllowed);

        // Family order as read (family sequence, then the natural control-number order).
        var familyVisible = group.Family.Where(r => Visible(r.DocumentId)).ToList();
        var duplicatesVisible = group.Duplicates.Where(r => Visible(r.DocumentId)).ToList();
        var threadVisible = group.Thread.Where(r => Visible(r.DocumentId)).ToList();
        if (duplicatesVisible.Count < 2)
        {
            duplicatesVisible = [];
        }

        if (threadVisible.Count < 2)
        {
            threadVisible = [];
        }

        var threadListed = threadVisible.Take(MaxThreadMembers).ToList();

        // A parent is a top-level document with another member of its family the caller can see.
        var familiesWithVisibleChildren = group.OtherFamilyChildren.Where(c => Visible(c.DocumentId)).Select(c => c.FamilyId).ToHashSet();
        if (familyVisible.Count > 1)
        {
            familiesWithVisibleChildren.Add(anchor.FamilyId);
        }

        bool IsParent(RelationshipRow row) => row.IsTopLevel && familiesWithVisibleChildren.Contains(row.FamilyId);

        var listed = familyVisible.Concat(duplicatesVisible).Concat(threadListed).Select(r => r.DocumentId).Distinct().ToList();
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FieldCodingState>> states =
            requested.Count == 0 || listed.Count == 0
                ? new Dictionary<Guid, IReadOnlyDictionary<int, FieldCodingState>>()
                : await coding.GetFieldStatesAsync(workspaceId, listed, requested.Keys.ToList(), cancellationToken).ConfigureAwait(false);

        RelatedDocument Related(RelationshipRow row)
        {
            var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (states.TryGetValue(row.DocumentId, out var state))
            {
                foreach (var (fieldId, (name, field)) in requested)
                {
                    if (state.TryGetValue(fieldId, out var s) && CodingValueText.Format(field, s.Value, catalog!.ChoicesOf(fieldId)) is { Count: > 0 } text)
                    {
                        values[name] = text;
                    }
                }
            }

            return new RelatedDocument(row, IsParent(row), row.DocumentId == anchor.DocumentId, values);
        }

        var familyMembers = familyVisible.Select(Related).ToList();
        var parent = familyMembers.Count > 1 ? familyMembers.FirstOrDefault(m => m.Row.IsTopLevel) : null;
        var primary = duplicatesVisible.FirstOrDefault(r => r.IsDuplicatePrimary);
        return new RelationshipsOutcome(RelationshipsStatus.Ok, new RelationshipsView(
            anchor.DocumentId,
            familyMembers.Count > 1 ? anchor.FamilyId : null,
            parent,
            familyMembers,
            duplicatesVisible.Count > 0 ? anchor.DuplicateGroupId : null,
            primary?.DocumentId,
            [.. duplicatesVisible.Select(Related)],
            threadVisible.Count > 0 ? anchor.EmailThreadId : null,
            [.. threadListed.Select(Related)],
            threadVisible.Count));
    }

    private static RelationshipsOutcome Invalid(string message) =>
        new(RelationshipsStatus.Invalid, Errors: [new FieldError("fields", "invalid-fields", message)]);
}
