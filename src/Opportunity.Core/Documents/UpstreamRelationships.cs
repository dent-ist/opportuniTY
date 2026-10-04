namespace Opportunity.Core.Documents;

/// <summary>
/// Upstream relationship identifiers of one load-file row, already validated by the mapping (hashes lower-case hex,
/// <see cref="ConversationIndex"/> upper-case hex). Null means the load does not supply the value.
/// </summary>
public sealed record UpstreamRelationshipValues
{
    public string? DuplicateGroup { get; init; }

    public string? DedupeHash { get; init; }

    public string? EmailHash { get; init; }

    public string? EmailThreadGroup { get; init; }

    public string? ConversationIndex { get; init; }

    /// <summary>The row names a parent (ParentID or an attachment range starting at another document).</summary>
    public bool IsAttachment { get; init; }
}

/// <param name="DeriveEmailThreadFromConversationIndex">
/// ADR-009 R19 import option (off by default): without an upstream thread group, group by the ConversationIndex header.
/// </param>
public sealed record UpstreamRelationshipOptions(bool DeriveEmailThreadFromConversationIndex = false);

/// <summary>What <see cref="UpstreamRelationships.Apply"/> set: the group and thread rows the writer must record.</summary>
public sealed record UpstreamRelationshipResult(DuplicateGroupKey? DuplicateGroup, EmailThreadKey? EmailThread, IReadOnlyList<string> Warnings);

/// <summary>
/// Honours upstream dedupe and threading (Q-09, ADR-009 R13, R17-R20): sets a document's duplicate group, upstream
/// hash and email thread from the load file. Duplicates are labelled, never suppressed: nothing here hides or codes a
/// document. Values the load does not supply leave the document unchanged.
/// </summary>
public static class UpstreamRelationships
{
    public static UpstreamRelationshipResult Apply(Document document, UpstreamRelationshipValues values, UpstreamRelationshipOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(values);
        options ??= new UpstreamRelationshipOptions();
        var warnings = new List<string>();

        DuplicateGroupKey? group = null;
        if (values.DuplicateGroup is { Length: > 0 } groupValue)
        {
            var id = RelationshipIds.DuplicateGroup(document.WorkspaceId, DuplicateHashKind.UpstreamGroup, groupValue);
            group = new DuplicateGroupKey(id, DuplicateGroupSource.Upstream, DuplicateHashKind.UpstreamGroup, groupValue);
            document.DuplicateGroupId = id;
        }

        // R14 keys computed grouping on the dedupe hash first, then the email hash; keep the one it would use.
        if (values.DedupeHash is { Length: > 0 } dedupe)
        {
            document.UpstreamDedupeHash = dedupe;
            document.UpstreamDedupeHashKind = DuplicateHashKind.UpstreamDedupeHash;
        }
        else if (values.EmailHash is { Length: > 0 } email)
        {
            document.UpstreamDedupeHash = email;
            document.UpstreamDedupeHashKind = DuplicateHashKind.UpstreamEmailHash;
        }

        EmailThreadKey? thread = null;
        if (values.EmailThreadGroup is { Length: > 0 } threadValue)
        {
            thread = new EmailThreadKey(
                RelationshipIds.EmailThread(document.WorkspaceId, EmailThreadSource.Upstream, threadValue), EmailThreadSource.Upstream, threadValue);
        }
        else if (options.DeriveEmailThreadFromConversationIndex && values.ConversationIndex is { Length: > 0 } index)
        {
            var root = Documents.ConversationIndex.ThreadRoot(index);
            thread = new EmailThreadKey(
                RelationshipIds.EmailThread(document.WorkspaceId, EmailThreadSource.ConversationIndex, root), EmailThreadSource.ConversationIndex, root);
        }

        if (thread is not null && values.IsAttachment)
        {
            // R20: thread expansion composes with family expansion, so attachments are reached through their parent.
            warnings.Add("Attachments carry no email thread; the row's thread value was not applied.");
            thread = null;
        }

        if (thread is not null)
        {
            document.EmailThreadId = thread.EmailThreadId;
            document.EmailThreadSource = thread.Source;
        }

        return new UpstreamRelationshipResult(group, thread, warnings);
    }
}
