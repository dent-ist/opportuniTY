namespace Opportunity.Search.Querying;

/// <summary>
/// The projection fields the search service itself depends on (projection v1, ADR-007). This is the contract with the
/// projection builder (E07-T02): every document it writes must carry these fields with these meanings. Everything
/// else (custom fields, coding slots, field capabilities) is resolved by the query translator (E07-T07).
/// </summary>
public static class ProjectionFields
{
    /// <summary>keyword, <c>Guid.ToString("D")</c>. The isolation term injected into every query's outer filter (ADR-006 R7).</summary>
    public const string WorkspaceId = "workspaceId";

    /// <summary>keyword, <c>Guid.ToString("D")</c>, unique per workspace: the post-filter key and the sort tie-breaker.</summary>
    public const string DocumentId = "documentId";

    /// <summary>keyword with normalizer; <see cref="ControlNumberSort"/> is the natural-sort key.</summary>
    public const string ControlNumber = "controlNumber";

    /// <summary>Top-level natural-sort key (ADR-009 R5), written by the projection builder (projection v2).</summary>
    public const string ControlNumberSort = "controlNumberSort";

    /// <summary>keyword, multi-valued, see <see cref="SecurityTags"/>. Injected only, never user-addressable (ADR-007).</summary>
    public const string SecurityTags = "securityTags";

    /// <summary>Stored full text (excluded from <c>_source</c>): searched and highlighted, never returned whole.</summary>
    public const string Text = "text";

    public const string FileName = "fileName";
    public const string FileNameKeyword = "fileName.kw";
    public const string FileType = "fileType";
    public const string FileExtension = "fileExtension";
    public const string MimeType = "mimeType";
    public const string DocumentDate = "documentDate";
    public const string FamilyId = "familyId";
    public const string ParentDocumentId = "parentDocumentId";
    public const string FamilySequence = "familySequence";
    public const string FileSize = "fileSize";
    public const string PageCount = "pageCount";

    /// <summary>Fields fetched from <c>_source</c> for a result row: grid fields only, never <see cref="Text"/> (ADR-015 D8.5).</summary>
    public static IReadOnlyList<string> GridSource { get; } =
    [
        WorkspaceId, DocumentId, ControlNumber, FileName, FileType, FileExtension, MimeType, DocumentDate, FamilyId,
        ParentDocumentId, FamilySequence, FileSize, PageCount,
    ];

    /// <summary>Fields the query language may never address (ADR-008 R11): they answer as unknown fields.</summary>
    public static IReadOnlySet<string> NotAddressable { get; } =
        new HashSet<string>(["workspaceId", "securityTags", "documentId", "projectionVersion"], StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Encoding of the document-side security attributes in <see cref="ProjectionFields.SecurityTags"/> (ADR-015 D8.2):
/// one value per restriction class (<c>class:{classKey}</c>) and per covering ethical wall (<c>wall:{wallId:D}</c>).
/// The projection builder (E07-T02) writes them; the search service excludes the caller's denied classes and walls in
/// the outer filter as defence in depth. The page post-filter against PostgreSQL (Q-12) remains authoritative.
/// </summary>
public static class SecurityTags
{
    public const string ClassPrefix = "class:";
    public const string WallPrefix = "wall:";

    public static string Class(string classKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classKey);
        return ClassPrefix + classKey;
    }

    public static string Wall(Guid wallId) => WallPrefix + wallId.ToString("D");
}
