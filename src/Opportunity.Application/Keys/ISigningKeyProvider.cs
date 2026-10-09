namespace Opportunity.Application.Keys;

/// <summary>
/// Asymmetric signing keys held by the key store (ADR-015 D10.2), for the audit checkpoint signatures of
/// <c>E14-T03</c> and later signed artifacts. Signatures are ECDSA P-256 with SHA-256 (<c>ES256</c>, IEEE P1363
/// format). Rotation adds a version; older versions keep verifying, and their public keys can be exported for a
/// declaration exhibit.
/// </summary>
public interface ISigningKeyProvider
{
    /// <summary>Signs with the newest version of the purpose's key, creating version 1 on first use.</summary>
    Task<KeySignature> SignAsync(string purpose, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="signature"/> is a valid signature of <paramref name="data"/> by the version it names.</summary>
    Task<bool> VerifyAsync(KeySignature signature, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>The DER SubjectPublicKeyInfo of one version (the newest when null).</summary>
    /// <exception cref="KeyUnavailableException">No such key version.</exception>
    Task<SigningPublicKey> GetPublicKeyAsync(string purpose, int? version = null, CancellationToken cancellationToken = default);

    /// <summary>Adds a new newest version and returns its number.</summary>
    Task<int> RotateAsync(string purpose, CancellationToken cancellationToken = default);
}

public static class SigningKeyPurposes
{
    /// <summary>Signed Merkle checkpoints of the audit hash chain (<c>E14-T03</c>).</summary>
    public const string AuditCheckpoint = "audit-checkpoint";
}

/// <param name="Value">IEEE P1363 (r || s) signature bytes.</param>
public sealed record KeySignature(string Purpose, int Version, string Algorithm, byte[] Value)
{
    public const string Es256 = "ES256";

    /// <summary>Stable key id recorded next to a signature, e.g. <c>audit-checkpoint-v2</c>.</summary>
    public string KeyId => $"{Purpose}-v{Version}";
}

public sealed record SigningPublicKey(string Purpose, int Version, string Algorithm, byte[] SubjectPublicKeyInfo)
{
    public string KeyId => $"{Purpose}-v{Version}";
}
