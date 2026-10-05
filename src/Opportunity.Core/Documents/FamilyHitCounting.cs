namespace Opportunity.Core.Documents;

/// <summary>One document of a scope: its family (ADR-009 R7: a standalone document is its own family) and the terms it hits.</summary>
public sealed record FamilyScopedDocument(Guid DocumentId, Guid FamilyId, IReadOnlySet<int> Terms);

/// <summary>Per-term counts of <see cref="FamilyHitCounting.Count"/>.</summary>
public sealed record TermHitCounts(int Term, long WithHits, long WithHitsIncludingFamily, long Unique, long UniqueIncludingFamily);

/// <summary>Scope totals of <see cref="FamilyHitCounting.Count"/>.</summary>
public sealed record ScopeHitTotals(long InScope, long WithHits, long WithHitsIncludingFamily, long WithoutHits);

/// <summary>
/// The family rules of hit counting (E07-T10 search term reports; the same expansion E09-T03 applies to searches and
/// bulk actions). Families come from <c>document.family_id</c> (ADR-009 R7). Every rule works inside a <em>scope</em>:
/// the documents of a set that the person running the operation may see, so expansion never adds a document outside
/// the set or hidden from that person.
/// <list type="bullet">
/// <item><b>With family</b> of a set of hit documents: every scope document whose family has at least one hit document.</item>
/// <item><b>Unique hits</b> of a term: hit documents that no other term hits.</item>
/// <item><b>Unique hits with family</b> of a term: documents of its with-family set that are in no other term's
/// with-family set, i.e. whose family is hit by this term only.</item>
/// </list>
/// This class is the in-memory reference; <c>Opportunity.Data.Relationships.FamilyHitSql</c> computes the same numbers
/// in PostgreSQL for sets of any size, and the known-answer tests check that both agree.
/// </summary>
public static class FamilyHitCounting
{
    public static (IReadOnlyList<TermHitCounts> Terms, ScopeHitTotals Totals) Count(IReadOnlyCollection<FamilyScopedDocument> scope, IEnumerable<int> terms)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(terms);
        var familyTerms = new Dictionary<Guid, HashSet<int>>();
        foreach (var document in scope)
        {
            if (!familyTerms.TryGetValue(document.FamilyId, out var set))
            {
                familyTerms[document.FamilyId] = set = [];
            }

            set.UnionWith(document.Terms);
        }

        var counts = new List<TermHitCounts>();
        foreach (var term in terms)
        {
            long hits = 0, family = 0, unique = 0, uniqueFamily = 0;
            foreach (var document in scope)
            {
                var own = document.Terms;
                var expanded = familyTerms[document.FamilyId];
                hits += own.Contains(term) ? 1 : 0;
                unique += own.Count == 1 && own.Contains(term) ? 1 : 0;
                family += expanded.Contains(term) ? 1 : 0;
                uniqueFamily += expanded.Count == 1 && expanded.Contains(term) ? 1 : 0;
            }

            counts.Add(new TermHitCounts(term, hits, family, unique, uniqueFamily));
        }

        var withHits = scope.LongCount(d => d.Terms.Count > 0);
        var withFamily = scope.LongCount(d => familyTerms[d.FamilyId].Count > 0);
        return (counts, new ScopeHitTotals(scope.Count, withHits, withFamily, scope.Count - withHits));
    }
}
