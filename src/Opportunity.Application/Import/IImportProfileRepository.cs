namespace Opportunity.Application.Import;

/// <summary>A saved import profile; <see cref="DefinitionJson"/> is the profile as the API exchanges it.</summary>
public sealed record ImportProfileRecord(
    Guid WorkspaceId,
    Guid ProfileId,
    string Name,
    string? Description,
    string DefinitionJson,
    long Version,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt);

public enum ImportProfileWriteOutcome
{
    Ok,
    NotFound,

    /// <summary>Another profile of the workspace has the same name (case-insensitive).</summary>
    NameConflict,

    /// <summary>The expected version is not the current one.</summary>
    VersionConflict,
}

public sealed record ImportProfileWriteResult(ImportProfileWriteOutcome Outcome, ImportProfileRecord? Profile)
{
    public static ImportProfileWriteResult Ok(ImportProfileRecord profile) => new(ImportProfileWriteOutcome.Ok, profile);
}

/// <summary>Import profiles saved per workspace (E08-T02). Profiles are copied between workspaces by export and import.</summary>
public interface IImportProfileRepository
{
    Task<IReadOnlyList<ImportProfileRecord>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<ImportProfileRecord?> GetAsync(Guid workspaceId, Guid profileId, CancellationToken cancellationToken = default);

    Task<ImportProfileWriteResult> CreateAsync(
        Guid workspaceId, string name, string? description, string definitionJson, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>Replaces name, description and definition when <paramref name="expectedVersion"/> is current; bumps the version.</summary>
    Task<ImportProfileWriteResult> UpdateAsync(
        Guid workspaceId, Guid profileId, long expectedVersion, string name, string? description, string definitionJson, Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes when <paramref name="expectedVersion"/> is current (or null).</summary>
    Task<ImportProfileWriteOutcome> DeleteAsync(Guid workspaceId, Guid profileId, long? expectedVersion, CancellationToken cancellationToken = default);
}
