using System.Security.Cryptography;

using Opportunity.Application.Keys;

namespace Opportunity.Application.Audit.Chain;

/// <summary>Why a checkpoint was taken (ADR-013 §3.4).</summary>
public enum AuditCheckpointReason
{
    /// <summary>Every 10 minutes when the chain advanced.</summary>
    Scheduled,

    /// <summary>An operator ran <c>audit seal --checkpoint</c>.</summary>
    Manual,

    /// <summary>Before the retention purge drops an audit partition (the purge refuses events not yet checkpointed).</summary>
    BeforePurge,

    /// <summary>Before a workspace deletion run (E20-T02).</summary>
    BeforeDeletion,

    /// <summary>When a matter is closed.</summary>
    MatterClosed,
}

/// <summary>A signed checkpoint as stored in <c>audit.checkpoint</c>.</summary>
public sealed record AuditCheckpoint(
    AuditCheckpointData Data,
    string KeyId,
    string Algorithm,
    byte[] Signature)
{
    public Guid ChainId => Data.ChainId;

    public long Sequence => Data.Sequence;
}

/// <param name="Chains">Chains that had unsealed events.</param>
/// <param name="Sealed">Events given a sequence and hash by this run.</param>
/// <param name="Busy">Chains skipped because another sealer holds them.</param>
/// <param name="Failed">Chains that could not be sealed this round (logged; retried next round).</param>
public sealed record AuditSealResult(int Chains, long Sealed, int Busy, int Failed = 0);

/// <summary>
/// The audit hash-chain sealer (ADR-013 §3.2–§3.4, E14-T03): chains committed events per workspace (and the system
/// chain) and takes signed checkpoints. The dispatcher runs it every second; deletion (E20-T02) and the retention purge
/// call <see cref="CheckpointAsync"/> first, because the purge refuses events that no checkpoint covers.
/// </summary>
public interface IAuditChainSealer
{
    /// <summary>Seals every committed, unsealed event of every chain (or of one chain).</summary>
    /// <param name="chainId">A workspace id, <see cref="AuditChainFormat.SystemChainId"/>, or null for all chains.</param>
    Task<AuditSealResult> SealAsync(Guid? chainId = null, CancellationToken cancellationToken = default);

    /// <summary>Seals, then checkpoints every chain that advanced since its last checkpoint (or one chain).</summary>
    /// <param name="chainId">A workspace id, <see cref="AuditChainFormat.SystemChainId"/>, or null for all chains.</param>
    Task<IReadOnlyList<AuditCheckpoint>> CheckpointAsync(
        AuditCheckpointReason reason, Guid? chainId = null, CancellationToken cancellationToken = default);
}

/// <summary>What <c>audit verify</c> found wrong with one chain.</summary>
public enum AuditChainIssueKind
{
    /// <summary>The stored hash does not match the event's content: a column was changed.</summary>
    EventModified,

    /// <summary>An event's PrevHash is not its predecessor's hash: events were reordered, replaced or rewritten.</summary>
    ChainLinkBroken,

    /// <summary>Chain positions are absent and no purge record covers them: events were deleted.</summary>
    EventsMissing,

    /// <summary>Two events claim the same chain position.</summary>
    DuplicateSequence,

    /// <summary>The chain ends before a checkpoint or the sealer's head: the newest events were deleted.</summary>
    TailMissing,

    /// <summary>A checkpoint's hash or Merkle root disagrees with the events: the chain was rewritten.</summary>
    CheckpointMismatch,

    /// <summary>A checkpoint signature does not verify with the public key.</summary>
    CheckpointSignatureInvalid,

    /// <summary>No public key is available for the checkpoint's key id.</summary>
    CheckpointKeyUnavailable,

    /// <summary>Checkpoints do not follow each other: a checkpoint was deleted.</summary>
    CheckpointMissing,

    /// <summary>A purge record (gap) is not backed by the signed <c>Audit.Purged</c> event of the system chain.</summary>
    PurgeRecordUnverified,
}

public sealed record AuditChainIssue(AuditChainIssueKind Kind, long? Sequence, string Message);

/// <summary>The verification report of one chain.</summary>
public sealed record AuditChainVerification
{
    public const int MaxIssues = 100;

    public required Guid ChainId { get; init; }

    /// <summary>Sealed events present in the database.</summary>
    public long Events { get; init; }

    public long LastSequence { get; init; }

    public byte[]? LastEventHash { get; init; }

    /// <summary>Chain positions removed by the retention purge, accounted for by purge records.</summary>
    public long PurgedEvents { get; init; }

    /// <summary>Committed events the sealer has not reached yet (not evidence of tampering).</summary>
    public long UnsealedEvents { get; init; }

    /// <summary>Events recorded before sealing began (ADR-013 §3.3: only as trustworthy as the storage controls then).</summary>
    public long EventsRecordedBeforeSealing { get; init; }

    public DateTime? FirstSealedAt { get; init; }

    public int Checkpoints { get; init; }

    public long LastCheckpointSequence { get; init; }

    /// <summary>Events newer than the last checkpoint: a deletion of these alone is not yet provable.</summary>
    public long EventsAfterLastCheckpoint => Math.Max(0, LastSequence - LastCheckpointSequence);

    public IReadOnlyList<AuditChainIssue> Issues { get; init; } = [];

    /// <summary>Issues beyond <see cref="MaxIssues"/> are counted, not listed.</summary>
    public long IssueCount { get; init; }

    public bool Intact => IssueCount == 0;
}

/// <summary>Public keys for checkpoint signatures, by key id (<c>audit-checkpoint-v&lt;n&gt;</c>).</summary>
public interface IAuditCheckpointKeys
{
    /// <returns>The DER SubjectPublicKeyInfo, or null when unknown.</returns>
    Task<byte[]?> GetPublicKeyAsync(string keyId, CancellationToken cancellationToken = default);
}

/// <summary>Keys from exported PEM files (<c>keys public-key</c> output: a <c># &lt;key id&gt; ES256</c> line, then the PEM).</summary>
public sealed class PemCheckpointKeys : IAuditCheckpointKeys
{
    private readonly Dictionary<string, byte[]> _byKeyId = new(StringComparer.Ordinal);
    private readonly List<byte[]> _unnamed = [];

    /// <summary>Parses PEM texts; a key without a <c># key-id</c> comment is tried for any key id.</summary>
    public PemCheckpointKeys(IEnumerable<string> pemTexts)
    {
        ArgumentNullException.ThrowIfNull(pemTexts);
        foreach (var text in pemTexts)
        {
            string? keyId = null;
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#'))
                {
                    keyId = trimmed.TrimStart('#').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                }
            }

            var spki = PemEncoding.TryFind(text, out var fields) && text[fields.Label] == "PUBLIC KEY"
                ? Convert.FromBase64String(text[fields.Base64Data].ToString())
                : throw new FormatException("No PUBLIC KEY PEM block found.");
            using (var check = ECDsa.Create())
            {
                check.ImportSubjectPublicKeyInfo(spki, out _);
            }

            if (keyId is null)
            {
                _unnamed.Add(spki);
            }
            else
            {
                _byKeyId[keyId] = spki;
            }
        }
    }

    public IReadOnlyCollection<string> KeyIds => _byKeyId.Keys;

    public Task<byte[]?> GetPublicKeyAsync(string keyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byKeyId.TryGetValue(keyId, out var spki) ? spki : _unnamed.Count == 1 ? _unnamed[0] : null);
}

/// <summary>Keys straight from the installation's <see cref="ISigningKeyProvider"/>.</summary>
public sealed class SigningProviderCheckpointKeys(ISigningKeyProvider provider) : IAuditCheckpointKeys
{
    public async Task<byte[]?> GetPublicKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        var dash = keyId.LastIndexOf("-v", StringComparison.Ordinal);
        if (dash <= 0 || !int.TryParse(keyId.AsSpan(dash + 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version))
        {
            return null;
        }

        try
        {
            return (await provider.GetPublicKeyAsync(keyId[..dash], version, cancellationToken).ConfigureAwait(false)).SubjectPublicKeyInfo;
        }
        catch (KeyUnavailableException)
        {
            return null;
        }
    }
}

public static class AuditCheckpointSignatures
{
    /// <summary>Verifies an ES256 (IEEE P1363) checkpoint signature with a DER SubjectPublicKeyInfo.</summary>
    public static bool Verify(AuditCheckpoint checkpoint, byte[] subjectPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(subjectPublicKeyInfo);
        if (checkpoint.Algorithm != KeySignature.Es256)
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        return key.VerifyData(AuditChainFormat.CheckpointPayload(checkpoint.Data), checkpoint.Signature, HashAlgorithmName.SHA256);
    }
}
