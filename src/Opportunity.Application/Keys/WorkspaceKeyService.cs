using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;

namespace Opportunity.Application.Keys;

/// <summary>
/// The plaintext data keys object storage encrypts with (ADR-011 §6 as amended by <c>E05-T09</c>). Each workspace has
/// numbered data keys; exactly one is active for new objects, older ones keep decrypting what they wrote.
/// </summary>
public interface IWorkspaceDataKeyRing
{
    /// <summary>The active data key, creating version 1 (wrapped by the installation KEK) on the workspace's first object.</summary>
    ValueTask<WorkspaceDataKey> GetCurrentAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>One version of the workspace's data key.</summary>
    /// <exception cref="KeyUnavailableException">The version does not exist, was destroyed, or its KEK is gone.</exception>
    ValueTask<WorkspaceDataKey> GetAsync(Guid workspaceId, int version, CancellationToken cancellationToken = default);
}

/// <summary>
/// The crypto-shredding hook of workspace deletion (<c>E20-T02</c>, ADR-014): destroys every data key of the workspace
/// and, when it has one, its dedicated KEK, audited, so every remaining copy of its objects (including backups) is
/// unreadable. Refused while the workspace is under a preservation lock.
/// </summary>
public interface IWorkspaceCryptoShredder
{
    Task<WorkspaceKeyDestruction> DestroyWorkspaceKeysAsync(Guid workspaceId, OperationsActor actor, CancellationToken cancellationToken = default);
}

/// <summary>A plaintext data key. Owned by the key ring's cache: never log it, copy it or keep it beyond one operation.</summary>
public sealed class WorkspaceDataKey
{
    public const int KeySizeBytes = 32;

    private readonly byte[] _key;

    internal WorkspaceDataKey(Guid workspaceId, int version, byte[] key)
    {
        WorkspaceId = workspaceId;
        Version = version;
        _key = key;
    }

    public Guid WorkspaceId { get; }

    public int Version { get; }

    /// <summary>The <c>KeyId</c> recorded per object (ADR-011 §6.1), e.g. <c>wdk-v2</c>; scoped to the workspace.</summary>
    public string KeyId => FormatKeyId(Version);

    public ReadOnlySpan<byte> Material => _key;

    public static string FormatKeyId(int version) => "wdk-v" + version.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => $"WorkspaceDataKey({WorkspaceId:N}, {KeyId}, redacted)";

    internal void Erase() => CryptographicOperations.ZeroMemory(_key);
}

public sealed class WorkspaceKeyOptions
{
    public const string SectionName = "KeyManagement";

    /// <summary>How long a process reuses an unwrapped data key (and the active version) before asking the store again.</summary>
    public TimeSpan DataKeyCacheTtl { get; set; } = TimeSpan.FromMinutes(1);
}

/// <param name="Examined">Data key versions looked at.</param>
/// <param name="Rewrapped">Versions now wrapped by their workspace's KEK at its newest version.</param>
/// <param name="Failed">Workspaces whose rewrap failed (left unchanged; rerun the job).</param>
public sealed record KeyRewrapResult(int Workspaces, int Examined, int Rewrapped, IReadOnlyList<Guid> Failed);

public sealed record WorkspaceKeyDestruction(Guid WorkspaceId, int DataKeysDestroyed, bool DedicatedKekDestroyed);

/// <summary>
/// Workspace data keys and their KEKs: lazy creation, rotation, switching a workspace to a dedicated KEK, the rewrap
/// job, KEK rotation and pruning, and crypto-shredding. Every state change is audited (<c>Admin.Key*</c>) with key ids
/// and versions only, never key material.
/// </summary>
public sealed class WorkspaceKeyService(
    IWorkspaceDataKeyStore store,
    IKeyEncryptionKeyProvider keks,
    WorkspaceKeyOptions options,
    TimeProvider time,
    IAuditEventWriter? installationAudit = null) : IWorkspaceDataKeyRing, IWorkspaceCryptoShredder
{
    public const string ResourceType = "WorkspaceDataKey";
    public const string KekResourceType = "KeyEncryptionKey";
    private const string SystemActorId = "service:key-management";

    // Wrapped payload: magic, workspace id, version, key. Binds a wrapped key to its row, so a row copied to another
    // workspace or version does not unwrap.
    private static readonly byte[] BindingMagic = "OPPWDK1\0"u8.ToArray();
    private const int BindingLength = 8 + 16 + 4 + WorkspaceDataKey.KeySizeBytes;

    private readonly ConcurrentDictionary<(Guid, int), (WorkspaceDataKey Key, DateTimeOffset Expires)> _keys = new();
    private readonly ConcurrentDictionary<Guid, (int Version, DateTimeOffset Expires)> _active = new();

    public async ValueTask<WorkspaceDataKey> GetCurrentAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        RequireWorkspace(workspaceId);
        if (_active.TryGetValue(workspaceId, out var active) && active.Expires > time.GetUtcNow())
        {
            return await GetAsync(workspaceId, active.Version, cancellationToken).ConfigureAwait(false);
        }

        var row = await store.GetActiveAsync(workspaceId, cancellationToken).ConfigureAwait(false)
            ?? await CreateInitialAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var key = Cache(row, await UnwrapAsync(row, cancellationToken).ConfigureAwait(false));
        _active[workspaceId] = (row.Version, time.GetUtcNow() + options.DataKeyCacheTtl);
        return key;
    }

    public async ValueTask<WorkspaceDataKey> GetAsync(Guid workspaceId, int version, CancellationToken cancellationToken = default)
    {
        RequireWorkspace(workspaceId);
        if (_keys.TryGetValue((workspaceId, version), out var cached) && cached.Expires > time.GetUtcNow())
        {
            return cached.Key;
        }

        var row = await store.GetAsync(workspaceId, version, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyUnavailableException($"Workspace data key {WorkspaceDataKey.FormatKeyId(version)} does not exist.");
        return Cache(row, await UnwrapAsync(row, cancellationToken).ConfigureAwait(false));
    }

    public Task<IReadOnlyList<WorkspaceDataKeyRecord>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ListAsync(workspaceId, cancellationToken);

    /// <summary>Adds a new active data key under the workspace's current KEK; objects written before keep their key.</summary>
    public Task<WorkspaceDataKeyRecord> RotateAsync(Guid workspaceId, OperationsActor actor, CancellationToken cancellationToken = default) =>
        AddVersionAsync(workspaceId, actor, dedicated: false, cancellationToken);

    /// <summary>
    /// Switches the workspace to its own KEK: creates the KEK and a new active data key wrapped by it, so new objects are
    /// protected by the dedicated KEK. Older data keys stay wrapped by the installation KEK until <see cref="RewrapAsync"/>
    /// moves them. A no-op (returns the active key) when the workspace already uses its dedicated KEK.
    /// </summary>
    public Task<WorkspaceDataKeyRecord> UseDedicatedKeyAsync(Guid workspaceId, OperationsActor actor, CancellationToken cancellationToken = default) =>
        AddVersionAsync(workspaceId, actor, dedicated: true, cancellationToken);

    /// <summary>
    /// The rewrap job: re-wraps every surviving data key version whose wrapping is not the newest version of its
    /// workspace's KEK (the active key's KEK). Data is never re-encrypted. Idempotent and resumable: each version is
    /// rewritten in its own audited transaction, guarded by its previous wrapping; a failing workspace is reported and
    /// skipped.
    /// </summary>
    /// <param name="workspaceId">One workspace, or null for every workspace of the installation.</param>
    public async Task<KeyRewrapResult> RewrapAsync(Guid? workspaceId, OperationsActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        IReadOnlyList<Guid> workspaces = workspaceId is { } one
            ? [RequireWorkspace(one)]
            : await store.ListWorkspaceIdsAsync(cancellationToken).ConfigureAwait(false);
        int examined = 0, rewrapped = 0, visited = 0;
        var failed = new List<Guid>();
        var kekVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ws in workspaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var rows = await store.ListAsync(ws, cancellationToken).ConfigureAwait(false);
                var active = rows.FirstOrDefault(r => r.State == WorkspaceDataKeyState.Active);
                if (active is null)
                {
                    continue;
                }

                visited++;
                if (!kekVersions.TryGetValue(active.KekId, out var target))
                {
                    target = (await keks.DescribeAsync(active.KekId, cancellationToken).ConfigureAwait(false)
                        ?? throw new KeyUnavailableException($"KEK {active.KekId} of the active data key does not exist.")).CurrentVersion;
                    kekVersions[active.KekId] = target;
                }

                foreach (var row in rows.Where(r => r.State != WorkspaceDataKeyState.Destroyed))
                {
                    examined++;
                    if (row.KekId == active.KekId && row.KekVersion == target)
                    {
                        continue;
                    }

                    var plaintext = await keks.UnwrapAsync(row.Wrapped!, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var replacement = await keks.WrapAsync(active.KekId, plaintext, cancellationToken).ConfigureAwait(false);
                        var audit = Audit(ws, actor, AuditTaxonomy.Admin.KeyRotated, row.Version, new Dictionary<string, string?>
                        {
                            ["operation"] = "Rewrap",
                            ["keyVersion"] = Invariant(row.Version),
                            ["fromKekId"] = row.KekId,
                            ["fromKekVersion"] = Invariant(row.KekVersion),
                            ["toKekId"] = replacement.KekId,
                            ["toKekVersion"] = Invariant(replacement.KekVersion),
                        });
                        if (await store.RewrapAsync(ws, row.Version, row.KekId, row.KekVersion, replacement, audit, cancellationToken).ConfigureAwait(false))
                        {
                            rewrapped++;
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }
                }
            }
            catch (Exception ex) when (workspaceId is null && ex is not OperationCanceledException)
            {
                failed.Add(ws);
            }
        }

        return new KeyRewrapResult(visited, examined, rewrapped, failed);
    }

    /// <summary>
    /// Rotates a KEK (installation or dedicated): new wraps use the new version at once; run <see cref="RewrapAsync"/>,
    /// then <see cref="PruneKekAsync"/> to destroy versions nothing is wrapped by any more.
    /// </summary>
    public async Task<KeyEncryptionKeyInfo> RotateKekAsync(string kekId, OperationsActor actor, CancellationToken cancellationToken = default)
    {
        KeyEncryptionKeyIds.Validate(kekId);
        ArgumentNullException.ThrowIfNull(actor);
        var info = await keks.RotateAsync(kekId, cancellationToken).ConfigureAwait(false);
        await AuditKekAsync(kekId, actor, AuditTaxonomy.Admin.KeyRotated, new Dictionary<string, string?>
        {
            ["kekId"] = kekId,
            ["kekVersion"] = Invariant(info.CurrentVersion),
            ["provider"] = keks.Name,
        }, cancellationToken).ConfigureAwait(false);
        return info;
    }

    /// <summary>Destroys the KEK versions older than the newest that no surviving data key is wrapped by. Returns them.</summary>
    public async Task<IReadOnlyList<int>> PruneKekAsync(string kekId, OperationsActor actor, CancellationToken cancellationToken = default)
    {
        KeyEncryptionKeyIds.Validate(kekId);
        ArgumentNullException.ThrowIfNull(actor);
        var info = await keks.DescribeAsync(kekId, cancellationToken).ConfigureAwait(false);
        if (info is null || info.Versions.Count < 2)
        {
            return [];
        }

        var inUse = new HashSet<int>();
        IReadOnlyList<Guid> workspaces = kekId.StartsWith("ws-", StringComparison.Ordinal) && Guid.TryParseExact(kekId[3..], "N", out var owner)
            ? [owner]
            : await store.ListWorkspaceIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var ws in workspaces)
        {
            foreach (var row in await store.ListAsync(ws, cancellationToken).ConfigureAwait(false))
            {
                if (row.State != WorkspaceDataKeyState.Destroyed && row.KekId == kekId)
                {
                    inUse.Add(row.KekVersion);
                }
            }
        }

        var destroyed = new List<int>();
        foreach (var version in info.Versions.Where(v => v != info.CurrentVersion && !inUse.Contains(v)))
        {
            await keks.DestroyVersionAsync(kekId, version, cancellationToken).ConfigureAwait(false);
            destroyed.Add(version);
            await AuditKekAsync(kekId, actor, AuditTaxonomy.Admin.KeyDestroyed, new Dictionary<string, string?>
            {
                ["kekId"] = kekId,
                ["kekVersion"] = Invariant(version),
                ["provider"] = keks.Name,
            }, cancellationToken).ConfigureAwait(false);
        }

        return destroyed;
    }

    public async Task<WorkspaceKeyDestruction> DestroyWorkspaceKeysAsync(
        Guid workspaceId, OperationsActor actor, CancellationToken cancellationToken = default)
    {
        RequireWorkspace(workspaceId);
        ArgumentNullException.ThrowIfNull(actor);
        var dedicatedKek = KeyEncryptionKeyIds.ForWorkspace(workspaceId);
        var dedicated = await keks.DescribeAsync(dedicatedKek, cancellationToken).ConfigureAwait(false) is not null;
        var audit = Audit(workspaceId, actor, AuditTaxonomy.Admin.KeyDestroyed, null, new Dictionary<string, string?>
        {
            ["operation"] = "CryptoShred",
            ["dedicatedKekId"] = dedicated ? dedicatedKek : null,
        });

        // The database first: it refuses under a hold and records the destruction; destroying the KEK afterwards is
        // idempotent, so a failure there is repaired by running the destruction again.
        var count = await store.DestroyAllAsync(workspaceId, audit, cancellationToken).ConfigureAwait(false);
        Evict(workspaceId);
        if (dedicated)
        {
            await keks.DestroyAsync(dedicatedKek, cancellationToken).ConfigureAwait(false);
        }

        return new WorkspaceKeyDestruction(workspaceId, count, dedicated);
    }

    private async Task<WorkspaceDataKeyRecord> AddVersionAsync(Guid workspaceId, OperationsActor actor, bool dedicated, CancellationToken cancellationToken)
    {
        RequireWorkspace(workspaceId);
        ArgumentNullException.ThrowIfNull(actor);
        for (var attempt = 0; ; attempt++)
        {
            var active = await store.GetActiveAsync(workspaceId, cancellationToken).ConfigureAwait(false)
                ?? await CreateInitialAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            var kekId = dedicated ? KeyEncryptionKeyIds.ForWorkspace(workspaceId) : active.KekId;
            if (dedicated && active.KekId == kekId)
            {
                return active;
            }

            await keks.EnsureAsync(kekId, cancellationToken).ConfigureAwait(false);
            var version = active.Version + 1;
            var wrapped = await WrapNewKeyAsync(workspaceId, version, kekId, cancellationToken).ConfigureAwait(false);
            var audit = Audit(workspaceId, actor, dedicated ? AuditTaxonomy.Admin.KeyCreated : AuditTaxonomy.Admin.KeyRotated, version,
                new Dictionary<string, string?>
                {
                    ["operation"] = dedicated ? "UseDedicatedKek" : "Rotate",
                    ["keyVersion"] = Invariant(version),
                    ["previousKeyVersion"] = Invariant(active.Version),
                    ["kekId"] = wrapped.KekId,
                    ["kekVersion"] = Invariant(wrapped.KekVersion),
                });
            if (await store.RotateAsync(workspaceId, active.Version, wrapped, audit, cancellationToken).ConfigureAwait(false))
            {
                _active.TryRemove(workspaceId, out _);
                return (await store.GetAsync(workspaceId, version, cancellationToken).ConfigureAwait(false))!;
            }

            if (attempt >= 2)
            {
                throw new InvalidOperationException("The workspace's active data key kept changing; retry the rotation.");
            }
        }
    }

    private async Task<WorkspaceDataKeyRecord> CreateInitialAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        await keks.EnsureAsync(KeyEncryptionKeyIds.Installation, cancellationToken).ConfigureAwait(false);
        var wrapped = await WrapNewKeyAsync(workspaceId, 1, KeyEncryptionKeyIds.Installation, cancellationToken).ConfigureAwait(false);
        var audit = Audit(workspaceId, null, AuditTaxonomy.Admin.KeyCreated, 1, new Dictionary<string, string?>
        {
            ["operation"] = "Create",
            ["keyVersion"] = "1",
            ["kekId"] = wrapped.KekId,
            ["kekVersion"] = Invariant(wrapped.KekVersion),
        });
        return await store.CreateInitialAsync(workspaceId, wrapped, audit, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WrappedKey> WrapNewKeyAsync(Guid workspaceId, int version, string kekId, CancellationToken cancellationToken)
    {
        var payload = new byte[BindingLength];
        try
        {
            WriteBinding(payload, workspaceId, version);
            RandomNumberGenerator.Fill(payload.AsSpan(BindingLength - WorkspaceDataKey.KeySizeBytes));
            return await keks.WrapAsync(kekId, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private async Task<byte[]> UnwrapAsync(WorkspaceDataKeyRecord row, CancellationToken cancellationToken)
    {
        if (row.State == WorkspaceDataKeyState.Destroyed || row.Wrapped is not { } wrapped)
        {
            throw new KeyUnavailableException($"Workspace data key {WorkspaceDataKey.FormatKeyId(row.Version)} was destroyed.");
        }

        var payload = await keks.UnwrapAsync(wrapped, cancellationToken).ConfigureAwait(false);
        try
        {
            var expected = new byte[BindingLength - WorkspaceDataKey.KeySizeBytes];
            WriteBinding(expected, row.WorkspaceId, row.Version);
            if (payload.Length != BindingLength || !CryptographicOperations.FixedTimeEquals(payload.AsSpan(0, expected.Length), expected))
            {
                throw new KeyUnavailableException($"Workspace data key {WorkspaceDataKey.FormatKeyId(row.Version)} is not bound to this workspace and version.");
            }

            return payload[expected.Length..];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void WriteBinding(Span<byte> destination, Guid workspaceId, int version)
    {
        BindingMagic.CopyTo(destination);
        workspaceId.TryWriteBytes(destination[8..24], bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(destination[24..28], version);
    }

    private WorkspaceDataKey Cache(WorkspaceDataKeyRecord row, byte[] key)
    {
        var entry = new WorkspaceDataKey(row.WorkspaceId, row.Version, key);
        _keys[(row.WorkspaceId, row.Version)] = (entry, time.GetUtcNow() + options.DataKeyCacheTtl);
        return entry;
    }

    private void Evict(Guid workspaceId)
    {
        _active.TryRemove(workspaceId, out _);
        foreach (var cached in _keys.Where(k => k.Key.Item1 == workspaceId).ToList())
        {
            // Not erased: a concurrent reader may still hold the instance for the operation in flight.
            _keys.TryRemove(cached.Key, out _);
        }
    }

    private AuditEvent Audit(Guid workspaceId, OperationsActor? actor, string action, int? version, Dictionary<string, string?> details) => new()
    {
        WorkspaceId = workspaceId,
        OccurredAt = time.GetUtcNow(),
        Category = AuditTaxonomy.Admin.Category,
        Action = action,
        ActorType = ActorType(actor),
        ActorId = ActorId(actor),
        ActorDisplay = ActorDisplay(actor),
        ResourceType = ResourceType,
        ResourceId = version is { } v ? WorkspaceDataKey.FormatKeyId(v) : null,
        Outcome = AuditOutcome.Success,
        Details = details,
    };

    private async Task AuditKekAsync(string kekId, OperationsActor actor, string action, Dictionary<string, string?> details, CancellationToken cancellationToken)
    {
        if (installationAudit is null)
        {
            return;
        }

        await installationAudit.WriteAsync(new AuditEvent
        {
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Admin.Category,
            Action = action,
            ActorType = ActorType(actor),
            ActorId = ActorId(actor),
            ActorDisplay = ActorDisplay(actor),
            ResourceType = KekResourceType,
            ResourceId = kekId,
            Outcome = AuditOutcome.Success,
            Details = details,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static AuditActorType ActorType(OperationsActor? actor) =>
        actor?.UserId is not null ? AuditActorType.User : actor is null ? AuditActorType.System : AuditActorType.Service;

    private static string ActorId(OperationsActor? actor) =>
        actor?.UserId?.ToString() ?? (actor is null ? SystemActorId : OperationsActor.CliServiceId);

    private static string ActorDisplay(OperationsActor? actor)
    {
        var display = actor?.UserId is not null ? actor.UserId.Value.ToString()
            : actor?.OperatorName is { } name ? $"Operations CLI ({name})"
            : "Key management";
        return display.Length <= AuditEventRules.MaxActorDisplayLength ? display : display[..AuditEventRules.MaxActorDisplayLength];
    }

    private static Guid RequireWorkspace(Guid workspaceId) =>
        workspaceId == Guid.Empty ? throw new ArgumentException("A workspace id is required.", nameof(workspaceId)) : workspaceId;

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
