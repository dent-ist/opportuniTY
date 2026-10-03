using System.Text.Json.Nodes;

using Opportunity.Core.Documents;
using Opportunity.Core.Fields;

namespace Opportunity.Application.Search.Projection;

/// <summary>
/// Reads the authoritative PostgreSQL inputs of the search projection (ADR-001 §3): document columns, canonical
/// metadata, current coding, relationship ids and security attributes, each with its DocumentVersion. Everything of one
/// call comes from <b>one snapshot</b>, so a projection built from it is exactly the state at the version it carries.
/// Index workers (E07-T03/T04) call this through the projection service in <c>Opportunity.Search</c>.
/// </summary>
public interface IProjectionSourceReader
{
    /// <summary>
    /// One entry per requested id, in request order (duplicates collapsed): live documents with their inputs, soft-deleted
    /// ones as <see cref="ProjectionSourceState.Deleted"/> with the deletion version, and ids without a row as
    /// <see cref="ProjectionSourceState.Missing"/>.
    /// </summary>
    Task<ProjectionSourceBatch> ReadAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);
}

public enum ProjectionSourceState
{
    Live,

    /// <summary><c>IsDeleted</c> in DocumentProjectionState: an external delete at <see cref="ProjectionSource.DocumentVersion"/>.</summary>
    Deleted,

    /// <summary>No row at all (hard-purged or never existed): an unconditional delete (ADR-001 §4 R1).</summary>
    Missing,
}

/// <param name="WorkspaceId">Workspace of every document in the batch.</param>
/// <param name="Catalog">Live field definitions and choices, read in the same snapshot as the documents.</param>
/// <param name="Documents">One entry per requested id.</param>
public sealed record ProjectionSourceBatch(Guid WorkspaceId, FieldCatalog Catalog, IReadOnlyList<ProjectionSource> Documents);

/// <summary>Projection inputs of one document.</summary>
public sealed record ProjectionSource
{
    public required Guid WorkspaceId { get; init; }

    public required Guid DocumentId { get; init; }

    public required ProjectionSourceState State { get; init; }

    /// <summary>The OpenSearch external version (ADR-001 §2); null only when <see cref="ProjectionSourceState.Missing"/>.</summary>
    public long? DocumentVersion { get; init; }

    /// <summary>Structural columns and canonical metadata; set exactly when <see cref="ProjectionSourceState.Live"/>.</summary>
    public Document? Document { get; init; }

    /// <summary>Current coding values by field id (canonical ADR-003 JSON; cleared fields are absent).</summary>
    public IReadOnlyDictionary<int, JsonNode> Coding { get; init; } = new Dictionary<int, JsonNode>();

    /// <summary>
    /// Document-side security attributes projected into <c>securityTags</c> (ADR-015 D8.2), e.g.
    /// <c>class:&lt;restrictionClass&gt;</c> and <c>wall:&lt;wallId&gt;</c>. Never user-addressable.
    /// </summary>
    public IReadOnlyList<string> SecurityTags { get; init; } = [];

    /// <summary>Object-storage logical key of the extracted text, when the document has text.</summary>
    public string? TextObjectKey { get; init; }

    public static ProjectionSource Missing(Guid workspaceId, Guid documentId) =>
        new() { WorkspaceId = workspaceId, DocumentId = documentId, State = ProjectionSourceState.Missing };
}
