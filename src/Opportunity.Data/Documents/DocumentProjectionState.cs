namespace Opportunity.Data.Documents;

/// <summary>
/// Row of <c>document_projection_state</c> (ADR-001 §2): the authoritative DocumentVersion, also the OpenSearch
/// external version and the API ETag. Maintained only by <see cref="DocumentRepository"/>.
/// </summary>
public sealed class DocumentProjectionState
{
    public Guid WorkspaceId { get; set; }

    public Guid DocumentId { get; set; }

    public long DocumentVersion { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
