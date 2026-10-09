using Opportunity.Application.Audit;

namespace Opportunity.Application.Keys;

/// <summary>Stored as smallint in <c>opportunity.workspace_data_key</c>.</summary>
public enum WorkspaceDataKeyState : short
{
    /// <summary>Encrypts new objects; exactly one per workspace.</summary>
    Active = 1,

    /// <summary>Superseded by a rotation; still decrypts the objects written with it.</summary>
    Retired = 2,

    /// <summary>Crypto-shredded: the wrapped key is gone and its objects are unreadable.</summary>
    Destroyed = 3,
}

/// <summary>One version of a workspace data key, wrapped by a KEK version. Never holds the plaintext key.</summary>
public sealed record WorkspaceDataKeyRecord(
    Guid WorkspaceId,
    int Version,
    WorkspaceDataKeyState State,
    string KekId,
    int KekVersion,
    byte[]? WrappedKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RewrappedAt,
    DateTimeOffset? DestroyedAt)
{
    public WrappedKey? Wrapped => WrappedKey is null ? null : new WrappedKey(KekId, KekVersion, WrappedKey);

    public override string ToString() => $"WorkspaceDataKey({WorkspaceId:N} v{Version} {State}, {KekId} v{KekVersion})";
}

/// <summary>
/// PostgreSQL store of workspace data keys (V0053, RLS-scoped). Every change commits together with its audit event; key
/// destruction is refused while the workspace is under a preservation lock.
/// </summary>
public interface IWorkspaceDataKeyStore
{
    Task<WorkspaceDataKeyRecord?> GetActiveAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<WorkspaceDataKeyRecord?> GetAsync(Guid workspaceId, int version, CancellationToken cancellationToken = default);

    /// <summary>Every version, oldest first.</summary>
    Task<IReadOnlyList<WorkspaceDataKeyRecord>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts version 1 as the active key with <paramref name="audit"/> unless the workspace already has a key (a
    /// concurrent writer won); returns the active key either way.
    /// </summary>
    Task<WorkspaceDataKeyRecord> CreateInitialAsync(Guid workspaceId, WrappedKey wrapped, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires active version <paramref name="expectedActiveVersion"/> and inserts the next version as active. False when
    /// the active version changed meanwhile (nothing written).
    /// </summary>
    Task<bool> RotateAsync(
        Guid workspaceId, int expectedActiveVersion, WrappedKey wrapped, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the wrapping of one version, provided it is still wrapped by <paramref name="expectedKekId"/> at
    /// <paramref name="expectedKekVersion"/> and not destroyed. False otherwise (nothing written).
    /// </summary>
    Task<bool> RewrapAsync(
        Guid workspaceId,
        int version,
        string expectedKekId,
        int expectedKekVersion,
        WrappedKey replacement,
        AuditEvent audit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Destroys every version (the wrapped keys are erased, the rows remain as the record) with <paramref name="audit"/>.
    /// Returns how many versions were destroyed by this call.
    /// </summary>
    /// <exception cref="Workspaces.PreservationLockedException">The workspace is under a preservation lock.</exception>
    Task<int> DestroyAllAsync(Guid workspaceId, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>Every registered workspace (the read-open registry), for installation-wide rewraps.</summary>
    Task<IReadOnlyList<Guid>> ListWorkspaceIdsAsync(CancellationToken cancellationToken = default);
}
