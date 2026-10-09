using System.Text.RegularExpressions;

namespace Opportunity.Application.Keys;

/// <summary>
/// Key-encryption keys (KEKs) held by a key store (ADR-015 D10.3): the built-in local key ring for Lite/Compose, or an
/// adapter for Vault Transit, AWS KMS or Azure Key Vault registered in its place. KEK material never leaves the
/// provider through this port: callers hand it data keys to wrap and get wrapped bytes back. A KEK has numbered
/// versions; new wraps use the newest, unwraps use the version recorded with the wrapped key, so rotating a KEK never
/// re-encrypts data (the rewrap job moves wrapped data keys to the newest version).
/// </summary>
public interface IKeyEncryptionKeyProvider
{
    /// <summary>Short provider name recorded in operations output, e.g. <c>local</c>.</summary>
    string Name { get; }

    /// <summary>Creates version 1 of the KEK when it does not exist yet; returns its state either way.</summary>
    Task<KeyEncryptionKeyInfo> EnsureAsync(string kekId, CancellationToken cancellationToken = default);

    /// <summary>The KEK's versions, or null when it does not exist (never created, or destroyed).</summary>
    Task<KeyEncryptionKeyInfo?> DescribeAsync(string kekId, CancellationToken cancellationToken = default);

    /// <summary>Wraps <paramref name="plaintext"/> with the newest version of the KEK.</summary>
    /// <exception cref="KeyUnavailableException">The KEK does not exist.</exception>
    Task<WrappedKey> WrapAsync(string kekId, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken = default);

    /// <summary>Unwraps with the KEK version recorded in <paramref name="wrapped"/>.</summary>
    /// <exception cref="KeyUnavailableException">That KEK version was destroyed, or the wrapped bytes do not authenticate.</exception>
    Task<byte[]> UnwrapAsync(WrappedKey wrapped, CancellationToken cancellationToken = default);

    /// <summary>Adds a new newest version. Older versions stay available for unwrapping until destroyed.</summary>
    Task<KeyEncryptionKeyInfo> RotateAsync(string kekId, CancellationToken cancellationToken = default);

    /// <summary>Destroys one version (after a rewrap left nothing wrapped by it). The newest version cannot be destroyed alone.</summary>
    Task DestroyVersionAsync(string kekId, int version, CancellationToken cancellationToken = default);

    /// <summary>Destroys every version of the KEK (crypto-shredding). Idempotent.</summary>
    Task DestroyAsync(string kekId, CancellationToken cancellationToken = default);
}

/// <summary>A data key wrapped by one KEK version. <see cref="Ciphertext"/> is opaque to everything but the provider.</summary>
public sealed record WrappedKey(string KekId, int KekVersion, byte[] Ciphertext)
{
    public override string ToString() => $"WrappedKey({KekId} v{KekVersion}, {Ciphertext.Length} bytes)";
}

public sealed record KeyEncryptionKeyInfo(string KekId, IReadOnlyList<int> Versions)
{
    public int CurrentVersion => Versions[^1];
}

/// <summary>Key material is gone (destroyed, crypto-shredded) or does not authenticate. Never carries key bytes.</summary>
public sealed class KeyUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>KEK identifiers: the installation KEK and one dedicated KEK per workspace that asks for it.</summary>
public static partial class KeyEncryptionKeyIds
{
    public const string Installation = "installation";

    private const string WorkspacePrefix = "ws-";

    public static string ForWorkspace(Guid workspaceId) => WorkspacePrefix + workspaceId.ToString("N");

    public static bool IsDedicatedTo(string kekId, Guid workspaceId) => string.Equals(kekId, ForWorkspace(workspaceId), StringComparison.Ordinal);

    /// <summary><c>[a-z0-9][a-z0-9-]{0,63}</c>: safe as a file or path segment in every provider.</summary>
    public static bool IsValid(string? kekId) => kekId is not null && Pattern().IsMatch(kekId);

    public static string Validate(string? kekId) =>
        IsValid(kekId) ? kekId! : throw new ArgumentException("A KEK id is [a-z0-9][a-z0-9-]{0,63}.", nameof(kekId));

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
