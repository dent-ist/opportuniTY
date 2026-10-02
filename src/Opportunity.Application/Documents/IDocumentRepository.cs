using Opportunity.Core.Documents;

namespace Opportunity.Application.Documents;

/// <summary>
/// Authoritative document writes. Every write that changes a projection input bumps DocumentVersion by exactly 1 in
/// the same transaction (ADR-001 §2); a write that changes nothing projected leaves it unchanged.
/// </summary>
public interface IDocumentRepository
{
    /// <summary>Inserts one document at DocumentVersion 1.</summary>
    Task InsertAsync(Document document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk insert (import chunk) of documents of one workspace at DocumentVersion 1, in one transaction, via staged
    /// binary COPY (ADR-015 D7.4.2).
    /// </summary>
    Task InsertManyAsync(Guid workspaceId, IReadOnlyCollection<Document> documents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the mutable columns of a document. Identity (control number, ids) is immutable. When
    /// <paramref name="expectedVersion"/> is given (If-Match) and differs from the current version, nothing is written.
    /// </summary>
    Task<DocumentWriteResult> UpdateAsync(Document document, long? expectedVersion = null, CancellationToken cancellationToken = default);

    /// <summary>Current DocumentVersion, or null when the document does not exist in the workspace.</summary>
    Task<long?> GetVersionAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken = default);
}

public enum DocumentWriteOutcome
{
    /// <summary>A projection input changed; the version was bumped.</summary>
    Updated,

    /// <summary>Only non-projected data changed (or nothing); the version is unchanged.</summary>
    Unchanged,

    /// <summary>The expected version did not match; nothing was written.</summary>
    VersionConflict,

    NotFound,
}

/// <param name="Outcome">What happened.</param>
/// <param name="DocumentVersion">The document's version after the call (current version on conflict), or null if not found.</param>
public readonly record struct DocumentWriteResult(DocumentWriteOutcome Outcome, long? DocumentVersion);
