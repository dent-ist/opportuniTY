namespace Opportunity.Data.Relationships;

/// <summary>
/// Family expansion of hit sets in PostgreSQL: the SQL form of <c>Opportunity.Core.Documents.FamilyHitCounting</c>
/// (with family, unique, unique with family), for sets of any size. The caller supplies two CTEs (or tables):
/// <c>scope(document_id, family_id)</c>, the documents the person may see within the set, with
/// <c>document.family_id</c> (ADR-009 R7: a standalone document is its own family); and
/// <c>hits(term_no, document_id)</c>, a subset of the scope. Expansion therefore never reaches outside the scope.
/// Used by search term reports (E07-T10); family expansion for searches and bulk actions (E09-T03) applies the same rule.
/// </summary>
public static class FamilyHitSql
{
    /// <summary>
    /// CTE definitions (without the leading <c>WITH</c>) ending in <c>expanded(document_id, own_terms, family_terms)</c>:
    /// for every scope document, the sorted terms it hits and the sorted terms any member of its family hits (both null
    /// when none).
    /// </summary>
    public static string Expanded(string scope, string hits) =>
        $"""
        hit_terms AS (
            SELECT h.document_id, array_agg(DISTINCT h.term_no ORDER BY h.term_no) AS terms
            FROM {hits} h GROUP BY h.document_id),
        family_terms AS (
            SELECT s.family_id, array_agg(DISTINCT t.term_no ORDER BY t.term_no) AS terms
            FROM {scope} s
            JOIN hit_terms ht ON ht.document_id = s.document_id
            CROSS JOIN LATERAL unnest(ht.terms) AS t(term_no)
            GROUP BY s.family_id),
        expanded AS (
            SELECT s.document_id, ht.terms AS own_terms, ft.terms AS family_terms
            FROM {scope} s
            LEFT JOIN hit_terms ht ON ht.document_id = s.document_id
            LEFT JOIN family_terms ft ON ft.family_id = s.family_id)
        """;

    /// <summary>
    /// Per term over <c>expanded</c>: <c>term_no, with_hits, unique_hits, with_family, unique_with_family</c>. Terms
    /// without hits have no row (count zero).
    /// </summary>
    public const string TermCounts =
        """
        SELECT t.term_no,
               count(*) FILTER (WHERE t.term_no = ANY (e.own_terms)) AS with_hits,
               count(*) FILTER (WHERE e.own_terms = ARRAY[t.term_no]) AS unique_hits,
               count(*) AS with_family,
               count(*) FILTER (WHERE e.family_terms = ARRAY[t.term_no]) AS unique_with_family
        FROM expanded e CROSS JOIN LATERAL unnest(e.family_terms) AS t(term_no)
        GROUP BY t.term_no
        """;

    /// <summary>Totals over <c>expanded</c>: <c>in_scope, with_hits, with_family</c>.</summary>
    public const string Totals =
        """
        SELECT count(*) AS in_scope,
               count(*) FILTER (WHERE e.own_terms IS NOT NULL) AS with_hits,
               count(*) FILTER (WHERE e.family_terms IS NOT NULL) AS with_family
        FROM expanded e
        """;
}
