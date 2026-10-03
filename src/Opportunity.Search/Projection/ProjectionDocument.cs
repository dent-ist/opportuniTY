using System.Text.Json.Nodes;

using Opportunity.Application.Search.Projection;
using Opportunity.Core.Fields;

namespace Opportunity.Search.Projection;

/// <summary>
/// Builds the search projection of one document from its authoritative inputs (§23, ADR-007). The coding
/// representation is not decided (ADR-004b): the interim Candidate A builder writes one unified document, and a later
/// candidate (separate coding index, parent/child) replaces this registration without changing callers, because the
/// result is a list of version-guarded writes rather than a single body.
/// </summary>
public interface IProjectionBuilder
{
    /// <summary>The ProjectionGeneration whose mapping the produced bodies conform to (ADR-007 R2).</summary>
    int Generation { get; }

    /// <param name="source">Inputs read in one snapshot (<see cref="IProjectionSourceReader"/>).</param>
    /// <param name="catalog">The workspace's live field definitions from the same snapshot.</param>
    /// <param name="text">Extracted text, already capped (<see cref="IndexedText"/>); null when the document has none.</param>
    ProjectionDocument Build(ProjectionSource source, FieldCatalog catalog, ProjectionText? text);
}

public enum ProjectionWriteKind
{
    /// <summary>Full-document <c>index</c> with <c>version_type=external</c> (ADR-001 §3).</summary>
    Index,

    /// <summary><c>delete</c> with <c>version_type=external</c> at the deletion's DocumentVersion.</summary>
    Delete,

    /// <summary>Unconditional <c>delete</c>: the PostgreSQL row is gone and ids are never reused (ADR-001 §4 R1).</summary>
    DeleteUnconditional,
}

/// <summary>
/// One OpenSearch write of a projection. Index workers resolve the physical targets and routing through index
/// management; the write itself never names an index.
/// </summary>
/// <param name="Id">The OpenSearch <c>_id</c>.</param>
/// <param name="Kind">Operation.</param>
/// <param name="Version">External version (= DocumentVersion); null for <see cref="ProjectionWriteKind.DeleteUnconditional"/>.</param>
/// <param name="Body">Document body for <see cref="ProjectionWriteKind.Index"/>; null for deletes.</param>
public sealed record ProjectionWrite(string Id, ProjectionWriteKind Kind, long? Version, JsonObject? Body);

/// <summary>The projection of one document: the writes that make the index reflect DocumentVersion <see cref="Version"/>.</summary>
/// <param name="DroppedFieldIds">
/// Fields whose stored value did not fit the field's slot kind and were left out (never expected for canonical values;
/// a non-empty list points at drift between PostgreSQL and the field catalogue).
/// </param>
public sealed record ProjectionDocument(
    Guid WorkspaceId,
    Guid DocumentId,
    long? Version,
    int Generation,
    IReadOnlyList<ProjectionWrite> Writes,
    IReadOnlyList<int> DroppedFieldIds);
