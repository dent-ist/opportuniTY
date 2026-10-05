using Opportunity.Application.Audit;
using Opportunity.Contracts.Search;

namespace Opportunity.Application.Search.HighlightSets;

/// <summary>A term of a Highlight Set as stored: its expression (ADR-008 syntax) and an optional own colour.</summary>
public sealed record HighlightTerm(Guid TermId, string Expression, string? Color);

public sealed record HighlightSetRecord
{
    public required Guid WorkspaceId { get; init; }

    public required Guid HighlightSetId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Color { get; init; }

    public required IReadOnlyList<HighlightTerm> Terms { get; init; }

    public required Guid ModifiedBy { get; init; }

    public required string ModifiedByDisplayName { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    public required long Version { get; init; }
}

/// <summary>What a new set or a replacement holds.</summary>
public sealed record HighlightSetDefinition(string Name, string? Description, string Color, IReadOnlyList<HighlightTerm> Terms);

public enum HighlightSetWriteStatus
{
    Ok,
    NotFound,
    VersionConflict,

    /// <summary>Another set of the workspace has the name.</summary>
    NameTaken,
}

public sealed record HighlightSetWriteResult(HighlightSetWriteStatus Status, HighlightSetRecord? Set = null);

/// <summary>
/// Highlight Sets and each reviewer's toggles, per workspace (implemented by <c>Opportunity.Data</c> under RLS). Writes
/// insert their audit event in the transaction of the change.
/// </summary>
public interface IHighlightSetStore
{
    Task<IReadOnlyList<HighlightSetRecord>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>The sets among <paramref name="highlightSetIds"/> that exist (any order).</summary>
    Task<IReadOnlyList<HighlightSetRecord>> GetManyAsync(Guid workspaceId, IReadOnlyCollection<Guid> highlightSetIds, CancellationToken cancellationToken = default);

    Task<HighlightSetWriteResult> CreateAsync(
        Guid workspaceId, Guid highlightSetId, Guid actorId, HighlightSetDefinition definition, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <param name="expectedVersion">The version the change was based on (If-Match).</param>
    Task<HighlightSetWriteResult> UpdateAsync(
        Guid workspaceId, Guid highlightSetId, long expectedVersion, Guid actorId, HighlightSetDefinition definition, AuditEvent audit,
        CancellationToken cancellationToken = default);

    Task<HighlightSetWriteResult> DeleteAsync(
        Guid workspaceId, Guid highlightSetId, long expectedVersion, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>The user's toggles; everything on when they never changed them.</summary>
    Task<HighlightSetSelection> GetSelectionAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default);

    Task SetSelectionAsync(Guid workspaceId, Guid userId, HighlightSetSelection selection, CancellationToken cancellationToken = default);
}
