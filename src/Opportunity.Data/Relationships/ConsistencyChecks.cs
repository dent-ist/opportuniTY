using Opportunity.Application.Documents;

namespace Opportunity.Data.Relationships;

/// <summary>
/// The duplicate-group and thread consistency checks (E09-T02): each query yields one text item per finding (an id, or
/// <c>id: detail</c>) for the workspace <c>@ws</c>. Live members exclude deleted documents.
/// </summary>
internal static class ConsistencyChecks
{
    private const string LiveDocuments =
        """
        opportunity.document d
        JOIN opportunity.document_projection_state s ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
        """;

    public static readonly IReadOnlyList<(RelationshipFindingKind Kind, string Sql)> All =
    [
        (RelationshipFindingKind.DuplicateGroupCountMismatch,
            $"""
            SELECT g.duplicate_group_id::text || ': stored ' || g.member_count || ', live ' || coalesce(l.n, 0)
            FROM opportunity.duplicate_group g
            LEFT JOIN (SELECT d.duplicate_group_id AS id, count(*) AS n FROM {LiveDocuments}
                       WHERE d.workspace_id = @ws AND d.duplicate_group_id IS NOT NULL AND NOT s.is_deleted
                       GROUP BY d.duplicate_group_id) l ON l.id = g.duplicate_group_id
            WHERE g.workspace_id = @ws AND g.member_count <> coalesce(l.n, 0)
            """),
        (RelationshipFindingKind.DuplicateGroupPrimaryMismatch,
            $"""
            SELECT g.duplicate_group_id::text
            FROM opportunity.duplicate_group g
            LEFT JOIN (SELECT DISTINCT ON (d.duplicate_group_id) d.duplicate_group_id AS id, d.document_id
                       FROM {LiveDocuments}
                       WHERE d.workspace_id = @ws AND d.duplicate_group_id IS NOT NULL AND NOT s.is_deleted
                       ORDER BY d.duplicate_group_id, d.family_date NULLS LAST, d.control_number_sort_key, d.document_id) p
                   ON p.id = g.duplicate_group_id
            WHERE g.workspace_id = @ws
              AND (g.primary_document_id IS DISTINCT FROM p.document_id
                   OR EXISTS (SELECT FROM opportunity.document m
                              WHERE m.workspace_id = @ws AND m.duplicate_group_id = g.duplicate_group_id
                                AND m.is_duplicate_primary <> coalesce(m.family_id = g.primary_family_id, false)))
            """),
        (RelationshipFindingKind.EmptyDuplicateGroup,
            """
            SELECT g.duplicate_group_id::text FROM opportunity.duplicate_group g
            WHERE g.workspace_id = @ws
              AND NOT EXISTS (SELECT FROM opportunity.document d WHERE d.workspace_id = @ws AND d.duplicate_group_id = g.duplicate_group_id)
            """),
        (RelationshipFindingKind.DuplicateGroupHashConflict,
            """
            SELECT g.duplicate_group_id::text || ' (' || g.hash_value || '): '
                   || count(DISTINCT (d.upstream_dedupe_hash_kind, d.upstream_dedupe_hash)) || ' hashes'
            FROM opportunity.duplicate_group g
            JOIN opportunity.document d ON d.workspace_id = g.workspace_id AND d.duplicate_group_id = g.duplicate_group_id
            WHERE g.workspace_id = @ws AND g.source = 1 AND d.upstream_dedupe_hash IS NOT NULL
            GROUP BY g.duplicate_group_id, g.hash_value
            HAVING count(DISTINCT (d.upstream_dedupe_hash_kind, d.upstream_dedupe_hash)) > 1
            """),
        (RelationshipFindingKind.DedupeHashSplitAcrossGroups,
            """
            SELECT d.upstream_dedupe_hash || ': ' || count(DISTINCT d.duplicate_group_id) || ' groups'
            FROM opportunity.document d
            JOIN opportunity.duplicate_group g ON g.workspace_id = d.workspace_id AND g.duplicate_group_id = d.duplicate_group_id
            WHERE d.workspace_id = @ws AND g.source = 1 AND d.upstream_dedupe_hash IS NOT NULL
            GROUP BY d.upstream_dedupe_hash_kind, d.upstream_dedupe_hash
            HAVING count(DISTINCT d.duplicate_group_id) > 1
            """),
        (RelationshipFindingKind.PrimaryWithoutGroup,
            """
            SELECT d.control_number FROM opportunity.document d
            WHERE d.workspace_id = @ws AND d.is_duplicate_primary AND d.duplicate_group_id IS NULL
            """),
        (RelationshipFindingKind.EmailThreadCountMismatch,
            $"""
            SELECT t.email_thread_id::text || ': stored ' || t.member_count || ', live ' || coalesce(l.n, 0)
            FROM opportunity.email_thread t
            LEFT JOIN (SELECT d.email_thread_id AS id, count(*) AS n FROM {LiveDocuments}
                       WHERE d.workspace_id = @ws AND d.email_thread_id IS NOT NULL AND NOT s.is_deleted
                       GROUP BY d.email_thread_id) l ON l.id = t.email_thread_id
            WHERE t.workspace_id = @ws AND t.member_count <> coalesce(l.n, 0)
            """),
        (RelationshipFindingKind.EmptyEmailThread,
            """
            SELECT t.email_thread_id::text FROM opportunity.email_thread t
            WHERE t.workspace_id = @ws
              AND NOT EXISTS (SELECT FROM opportunity.document d WHERE d.workspace_id = @ws AND d.email_thread_id = t.email_thread_id)
            """),
        (RelationshipFindingKind.AttachmentInEmailThread,
            """
            SELECT d.control_number FROM opportunity.document d
            WHERE d.workspace_id = @ws AND d.email_thread_id IS NOT NULL AND d.parent_document_id IS NOT NULL
            """),
    ];
}
