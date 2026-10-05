namespace Opportunity.Contracts.Search;

/// <summary>
/// One term of a Highlight Set (E16-T12): a word, <c>"phrase"</c>, wildcard (<c>terminat*</c>), <c>a W/n b</c> or an
/// <c>OR</c> of these, in the query language (ADR-008). Several unquoted words without operators are a phrase.
/// </summary>
/// <param name="Color">The term's own colour (a name from <see cref="HighlightSetList.Colors"/>); null: the set's colour.</param>
public sealed record HighlightTermResource(Guid TermId, string Expression, string? Color);

/// <summary>Who created or last changed a Highlight Set.</summary>
public sealed record HighlightSetActor(Guid UserId, string DisplayName);

/// <summary>
/// A workspace Highlight Set (persistent highlighting): a named term list with a colour that reviewers switch on and
/// off in the viewer. Every workspace member reads them; <c>HighlightSet.Manage</c> changes them.
/// </summary>
/// <param name="Color">A palette name from <see cref="HighlightSetList.Colors"/>; the web app maps it to design tokens.</param>
/// <param name="Version">Optimistic-concurrency version; also the ETag (<c>If-Match</c> on change and delete).</param>
public sealed record HighlightSetResource(
    Guid HighlightSetId,
    string Name,
    string? Description,
    string Color,
    IReadOnlyList<HighlightTermResource> Terms,
    HighlightSetActor ModifiedBy,
    DateTimeOffset ModifiedAt,
    long Version);

/// <summary><c>GET …/highlight-sets</c>: every set of the workspace by name, and the colour palette.</summary>
/// <param name="Colors">The palette names a set or term may use, in display order.</param>
public sealed record HighlightSetList(IReadOnlyList<HighlightSetResource> Items, IReadOnlyList<string> Colors);

/// <summary>A term of a create or replace request. Keep <see cref="TermId"/> to keep a term's identity across edits.</summary>
public sealed record HighlightTermRequest(string? Expression, string? Color = null, Guid? TermId = null);

/// <summary>Body of <c>POST</c> (create) and <c>PUT</c> (replace) <c>…/highlight-sets</c>.</summary>
/// <param name="Name">1–200 characters, unique in the workspace (case-insensitive).</param>
/// <param name="Color">A palette name (see <see cref="HighlightSetList.Colors"/>).</param>
/// <param name="Terms">1–500 terms, each at most 1,000 characters.</param>
public sealed record HighlightSetRequest(string? Name, string? Color, IReadOnlyList<HighlightTermRequest>? Terms, string? Description = null);

/// <summary>
/// The caller's highlighting toggles in the workspace (<c>GET</c>/<c>PUT …/highlight-set-selection</c>): the sets they
/// switched off (every other set is on, new sets included) and whether the hits of the current search are shown.
/// </summary>
public sealed record HighlightSetSelection(IReadOnlyList<Guid> DisabledSetIds, bool SearchHits);
