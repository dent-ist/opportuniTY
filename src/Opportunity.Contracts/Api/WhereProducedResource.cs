namespace Opportunity.Contracts.Api;

/// <summary>How a document relates to the document looked up.</summary>
public enum WhereProducedRelationResource
{
    /// <summary>The document looked up.</summary>
    Self,

    /// <summary>A member of its family.</summary>
    Family,

    /// <summary>A member of its duplicate group.</summary>
    Duplicate,
}

/// <summary>
/// Where a document, its family and its duplicates were produced (E13-T04): every finalized or voided production that
/// gave them Bates numbers, with the production's volumes. Documents you may not view are omitted entirely (Q-52).
/// </summary>
/// <param name="Documents">The document, its family members and its duplicates, produced or not.</param>
/// <param name="Items">One row per document and production, by production (oldest finalization first) then production order.</param>
/// <param name="Truncated">More than 5,000 related documents exist; only the first 5,000 were looked up.</param>
public sealed record WhereProducedResource(
    Guid DocumentId,
    IReadOnlyList<WhereProducedDocumentResource> Documents,
    IReadOnlyList<WhereProducedItemResource> Items,
    bool Truncated);

/// <param name="Productions">Productions that gave the document Bates numbers.</param>
public sealed record WhereProducedDocumentResource(
    Guid DocumentId,
    string? ControlNumber,
    IReadOnlyList<WhereProducedRelationResource> Relations,
    int Productions);

/// <param name="ProducedAt">When the production was finalized (produced).</param>
/// <param name="VoidedAt">When a voided production was withdrawn; its numbers stay retired.</param>
/// <param name="Volumes">The names of the production's completed volume runs (empty until a volume is written).</param>
/// <param name="Output">How the document was produced: page images, native with a slip sheet, or a placeholder (always set).</param>
public sealed record WhereProducedItemResource(
    Guid ProductionId,
    string ProductionName,
    int ProductionVersion,
    ProductionStatusResource ProductionStatus,
    DateTimeOffset ProducedAt,
    DateTimeOffset? VoidedAt,
    IReadOnlyList<string> Volumes,
    Guid DocumentId,
    string? ControlNumber,
    IReadOnlyList<WhereProducedRelationResource> Relations,
    ProductionOutputResource? Output,
    string ProdBegBates,
    string ProdEndBates,
    string ProdBegAttach,
    string ProdEndAttach);
