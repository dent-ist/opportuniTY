using Opportunity.Application.Authorization;
using Opportunity.Contracts.Search;
using Opportunity.Core.QueryLanguage;

namespace Opportunity.Application.Search;

/// <summary>
/// What the search path needs from saved searches (E07-T09): expanding <c>savedsearch:&lt;id&gt;</c> references into
/// the referenced criteria (re-parsed now, cycles and depth checked), finding a saved search the caller may run, and
/// recording a run. A saved search is a candidate set, never a grant (ADR-015 D5.8): whatever it expands to lands in
/// the user clause below the workspace and security filters and is post-filtered for the runner.
/// </summary>
public interface ISavedSearchQueries
{
    /// <summary>
    /// Replaces every saved-search reference in <paramref name="request"/>'s AST with the referenced criteria. Direct
    /// references must be visible to <see cref="SavedSearchExpansionRequest.Caller"/> when it is set (text the caller
    /// wrote); references inside a saved search are part of that search's criteria and resolve within the workspace.
    /// </summary>
    Task<SavedSearchExpansion> ExpandAsync(SavedSearchExpansionRequest request, CancellationToken cancellationToken = default);

    /// <summary>A saved search the caller may see (own, shared with them or a group of theirs, or any as Workspace Admin); null otherwise.</summary>
    Task<SavedSearchRunSource?> FindForRunAsync(SecurityPrincipal caller, Guid workspaceId, Guid savedSearchId, CancellationToken cancellationToken = default);

    /// <summary>Records the run's time, hit count and freshness as the saved search's last run.</summary>
    Task RecordRunAsync(Guid workspaceId, Guid savedSearchId, SavedSearchRunResult run, CancellationToken cancellationToken = default);
}

/// <param name="Caller">Check direct references against this caller's visibility; null when the text comes from a saved search.</param>
/// <param name="Self">The saved search the query belongs to (on save): reaching it again is a cycle.</param>
public sealed record SavedSearchExpansionRequest(Guid WorkspaceId, QueryNode Ast, SecurityPrincipal? Caller, Guid? Self = null);

/// <summary>
/// The expanded AST (spans of inlined criteria are the span of the reference in the caller's text, so every later error
/// points at text the caller can see) or the positioned reference errors.
/// </summary>
public sealed record SavedSearchExpansion(QueryNode Ast, IReadOnlyList<QueryDiagnostic> Errors, IReadOnlyList<Guid> DirectReferences)
{
    /// <summary>The span of each direct reference in the caller's text and the name of the saved search it names.</summary>
    public IReadOnlyDictionary<SourceSpan, string> ReferenceNames { get; init; } = new Dictionary<SourceSpan, string>();

    public bool Success => Errors.Count == 0;

    public static SavedSearchExpansion Unchanged(QueryNode ast) => new(ast, [], []);

    /// <summary>
    /// Binding errors inside an inlined reference (the planner may point at part of it, e.g. a field name) get the whole
    /// reference as their span and the referenced search's name in the message.
    /// </summary>
    public IReadOnlyList<QueryDiagnostic> Annotate(IReadOnlyList<QueryDiagnostic> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (ReferenceNames.Count == 0)
        {
            return errors;
        }

        return [.. errors.Select(e =>
        {
            foreach (var (span, name) in ReferenceNames)
            {
                if (e.Span.Start >= span.Start && e.Span.End <= span.End)
                {
                    return e with { Message = $"In saved search '{name}': {e.Message}", Span = span };
                }
            }

            return e;
        })];
    }
}

/// <summary>A saved search to run: its criteria and the sort it opens with.</summary>
public sealed record SavedSearchRunSource(Guid SavedSearchId, string Name, string QueryText, IReadOnlyList<SearchSortKey> Sort)
{
    /// <summary>The stored "Include family / duplicates / email thread" choice (E09-T03), applied unless the run gives its own.</summary>
    public Core.Documents.RelationshipExpansion Expansion { get; init; }
}

public sealed record SavedSearchRunResult(DateTimeOffset At, long HitCount, bool Exact, bool? Current, long? ServedGeneration);
