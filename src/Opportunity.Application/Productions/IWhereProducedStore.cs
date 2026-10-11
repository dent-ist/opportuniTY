namespace Opportunity.Application.Productions;

/// <summary>How a document of a where-produced lookup relates to the document looked up (E13-T04). Flags: a family member can also be a duplicate.</summary>
[Flags]
public enum WhereProducedRelation
{
    None = 0,

    /// <summary>The document looked up.</summary>
    Self = 1,

    /// <summary>A member of the document's family.</summary>
    Family = 2,

    /// <summary>A member of the document's duplicate group.</summary>
    Duplicate = 4,
}

/// <summary>A document of a where-produced lookup: the document itself, a family member or a duplicate.</summary>
public sealed record WhereProducedDocument(Guid DocumentId, string? ControlNumber, WhereProducedRelation Relation);

/// <summary>
/// One document's numbers in one produced (finalized or voided) production: the Bates → DocumentId cross-reference row
/// (production_document) with its production.
/// </summary>
public sealed record ProducedDocumentRow(
    Guid ProductionId,
    string ProductionName,
    int ProductionVersion,
    ProductionStatus ProductionStatus,
    DateTimeOffset FinalizedAt,
    DateTimeOffset? VoidedAt,
    long Sequence,
    Guid DocumentId,
    string? ControlNumber,
    Core.Productions.ProductionOutputKind Output,
    string ProdBegBates,
    string ProdEndBates,
    string ProdBegAttach,
    string ProdEndAttach);

/// <summary>A completed volume run of a production: its frozen export settings (the volume name is derived from them).</summary>
public sealed record ProducedVolumeRow(Guid ProductionId, Guid VolumeId, string SettingsJson, DateTimeOffset CompletedAt);

/// <summary>Keyset position of the matter-wide cross-reference (productions by finalization, members in production order).</summary>
public sealed record ProducedDocumentCursor(DateTimeOffset FinalizedAt, Guid ProductionId, long Sequence);

/// <summary>
/// Reads for the where-produced lookup and the Bates cross-reference (E13-T04) over produced productions only
/// (finalized, and voided ones that were produced before they were withdrawn); drafts never issued numbers. It never
/// authorizes; the service does.
/// </summary>
public interface IWhereProducedStore
{
    /// <summary>
    /// The document, its family members and its duplicates (at most <paramref name="limit"/> rows, the document first);
    /// null when the document does not exist in the workspace.
    /// </summary>
    Task<IReadOnlyList<WhereProducedDocument>?> ReadRelatedAsync(Guid workspaceId, Guid documentId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Every produced production's numbers of the documents, by finalization then production order.</summary>
    Task<IReadOnlyList<ProducedDocumentRow>> ReadProducedAsync(Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);

    /// <summary>A page of the produced productions' members (of one production when <paramref name="productionId"/> is set).</summary>
    Task<IReadOnlyList<ProducedDocumentRow>> ReadProducedPageAsync(
        Guid workspaceId, Guid? productionId, ProducedDocumentCursor? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>Completed volume runs of the productions, oldest first.</summary>
    Task<IReadOnlyList<ProducedVolumeRow>> ReadVolumesAsync(Guid workspaceId, IReadOnlyCollection<Guid> productionIds, CancellationToken cancellationToken = default);

    /// <summary>Whether the production exists and was produced (finalized or voided).</summary>
    Task<bool> IsProducedAsync(Guid workspaceId, Guid productionId, CancellationToken cancellationToken = default);
}
