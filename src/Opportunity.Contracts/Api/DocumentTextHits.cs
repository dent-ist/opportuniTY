namespace Opportunity.Contracts.Api;

/// <summary>Where a highlighted unit comes from.</summary>
public enum TermHitSource
{
    /// <summary>The current search (built-in "Search hits").</summary>
    Search,

    /// <summary>A workspace Highlight Set.</summary>
    HighlightSet,
}

/// <summary>What a unit matches (ADR-008 R17): a phrase and a proximity are highlighted as one span each.</summary>
public enum TermHitKind
{
    Term,
    Phrase,
    Wildcard,
    Proximity,
}

/// <summary>
/// A highlighted unit: a term, phrase, wildcard or proximity of the search or of a Highlight Set term. Unit numbers are
/// stable for the same request parameters, so the pages of one document can be added up.
/// </summary>
/// <param name="Label">What the reviewer reads ("termination", "price increase", "agreement W/5 termination").</param>
/// <param name="Color">The palette colour of a Highlight Set unit (term colour, else set colour); null for search hits.</param>
/// <param name="Count">Hits of this unit that start in the page's chunks.</param>
public sealed record TermHitUnitResource(
    int Unit,
    TermHitSource Source,
    Guid? HighlightSetId,
    Guid? TermId,
    string Label,
    TermHitKind Kind,
    string? Color,
    long Count);

/// <summary>
/// One highlighted span: UTF-16 offsets <c>[Start, End)</c> in the text of chunk <see cref="Chunk"/> (as served by
/// <c>…/text/chunks/{chunk}</c>). A span that runs into the next chunk has an <see cref="End"/> beyond the chunk's text.
/// </summary>
public sealed record TermHitResource(int Unit, int Chunk, int Start, int End);

/// <summary>
/// <c>GET …/documents/{documentId}/text/hits</c> (E16-T12): server-computed hits over the extracted text, a page of
/// whole chunks at a time (<see cref="FromChunk"/> to <see cref="ToChunk"/>, exclusive), ordered by position. Ask again
/// with <c>fromChunk</c> = <see cref="NextChunk"/> until it is null; add the per-page unit counts up for the totals.
/// </summary>
/// <param name="Truncated">Only the first part of the text is searchable (Q-29): the search hits beyond it are still marked.</param>
/// <param name="Missing">The document has no extracted text.</param>
public sealed record DocumentTextHitsResource(
    Guid DocumentId,
    int ChunkCount,
    int FromChunk,
    int ToChunk,
    int? NextChunk,
    bool Truncated,
    bool Missing,
    IReadOnlyList<TermHitUnitResource> Units,
    IReadOnlyList<TermHitResource> Hits);
