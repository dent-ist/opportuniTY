using System.Text.Json.Nodes;

using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Core.Pages;

namespace Opportunity.Application.Content;

/// <summary>
/// Reads what the document viewer shows besides stored content (E11-T01): the document's field values and artifact
/// facts, and the pages of its active page set (ADR-012). Implemented by <c>Opportunity.Data</c> inside the workspace's
/// RLS context; called only by <see cref="IDocumentAccessService.ReadAsync{T}"/> after the PDP allowed the document.
/// Both return null for a document that does not exist or is deleted.
/// </summary>
public interface IDocumentViewerCatalog
{
    Task<DocumentViewerRecord?> GetDocumentAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken = default);

    /// <param name="afterPage">Keyset position: return pages with a higher number.</param>
    /// <param name="limit">Maximum number of pages to return.</param>
    Task<DocumentPageList?> GetPagesAsync(
        Guid workspaceId, Guid documentId, int afterPage, int limit, CancellationToken cancellationToken = default);
}

/// <summary>One document as the metadata view needs it, read in one snapshot.</summary>
/// <param name="Coding">Current coding values by field id (canonical ADR-003 JSON).</param>
/// <param name="RawValues">Original imported strings by field id, for values the import coerced (ADR-003 R9).</param>
/// <param name="DisplayTimeZone">The workspace's display time zone (IANA id, Q-28).</param>
/// <param name="Native">The registered native object, when there is one.</param>
/// <param name="Text">The registered extracted-text object, when there is one.</param>
/// <param name="ActivePageSet">The document's active page set, when it has one.</param>
public sealed record DocumentViewerRecord(
    Document Document,
    long DocumentVersion,
    FieldCatalog Catalog,
    IReadOnlyDictionary<int, JsonNode> Coding,
    IReadOnlyDictionary<int, string> RawValues,
    string DisplayTimeZone,
    StoredArtifact? Native,
    StoredArtifact? Text,
    PageSetSummary? ActivePageSet);

/// <summary>Size and state of a registered object; the key never leaves the data layer.</summary>
public sealed record StoredArtifact(long SizeBytes, bool Quarantined);

/// <param name="PageCount">Number of page rows of the set.</param>
public sealed record PageSetSummary(Guid PageSetId, PageSetSource Source, PageSetStatus Status, int PageCount);

/// <summary>A page of pages of the active page set; <see cref="ActivePageSet"/> is null when the document has none.</summary>
public sealed record DocumentPageList(PageSetSummary? ActivePageSet, IReadOnlyList<DocumentPageInfo> Pages);

/// <summary>One page (ADR-012 §1) with the viewer images the gateway can serve for it.</summary>
/// <param name="ImageFormat">Format of the review image the gateway serves (PNG, JPEG or WebP), or null when none is servable yet.</param>
public sealed record DocumentPageInfo(
    int PageNumber,
    decimal WidthPt,
    decimal HeightPt,
    int Rotation,
    PageColorMode ColorMode,
    bool ImageMissing,
    PageImageFormat? ImageFormat,
    int? ImageWidthPx,
    int? ImageHeightPx,
    bool HasThumbnail);
