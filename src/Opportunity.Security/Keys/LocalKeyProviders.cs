using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Opportunity.Application.Keys;

namespace Opportunity.Security.Keys;

/// <summary>
/// The built-in KEK provider over <see cref="LocalKeyStore"/>: 256-bit KEKs, AES-256-GCM wrapping with the KEK id and
/// version as associated data. Wrapped form: nonce (12) || ciphertext || tag (16).
/// </summary>
public sealed class LocalKeyEncryptionKeyProvider : IKeyEncryptionKeyProvider
{
    private const string Kind = "kek";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly LocalKeyStore _store;

    public LocalKeyEncryptionKeyProvider(LocalKeyStoreOptions options, ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        _store = new LocalKeyStore(options, secrets);
    }

    public string Name => "local";

    public async Task<KeyEncryptionKeyInfo> EnsureAsync(string kekId, CancellationToken cancellationToken = default)
    {
        if (await DescribeAsync(kekId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        await CreateVersionAsync(kekId, 1, cancellationToken).ConfigureAwait(false);
        return (await DescribeAsync(kekId, cancellationToken).ConfigureAwait(false))!;
    }

    public Task<KeyEncryptionKeyInfo?> DescribeAsync(string kekId, CancellationToken cancellationToken = default)
    {
        var versions = _store.Versions(Kind, KeyEncryptionKeyIds.Validate(kekId));
        return Task.FromResult(versions.Count == 0 ? null : new KeyEncryptionKeyInfo(kekId, versions));
    }

    public async Task<WrappedKey> WrapAsync(string kekId, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken = default)
    {
        var info = await DescribeAsync(kekId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyUnavailableException($"KEK {kekId} does not exist.");
        var version = info.CurrentVersion;
        var kek = await ReadAsync(kekId, version, cancellationToken).ConfigureAwait(false);
        try
        {
            var output = new byte[NonceSize + plaintext.Length + TagSize];
            RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));
            using var aes = new AesGcm(kek, TagSize);
            aes.Encrypt(output.AsSpan(0, NonceSize), plaintext.Span, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(output.Length - TagSize), Aad(kekId, version));
            return new WrappedKey(kekId, version, output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public async Task<byte[]> UnwrapAsync(WrappedKey wrapped, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        if (wrapped.Ciphertext.Length < NonceSize + TagSize)
        {
            throw new KeyUnavailableException("The wrapped key is malformed.");
        }

        var kek = await ReadAsync(wrapped.KekId, wrapped.KekVersion, cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = wrapped.Ciphertext;
            var plaintext = new byte[bytes.Length - NonceSize - TagSize];
            using var aes = new AesGcm(kek, TagSize);
            aes.Decrypt(bytes.AsSpan(0, NonceSize), bytes.AsSpan(NonceSize, plaintext.Length), bytes.AsSpan(bytes.Length - TagSize), plaintext, Aad(wrapped.KekId, wrapped.KekVersion));
            return plaintext;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new KeyUnavailableException($"The wrapped key does not open with KEK {wrapped.KekId} v{wrapped.KekVersion}.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public async Task<KeyEncryptionKeyInfo> RotateAsync(string kekId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var next = ((await DescribeAsync(kekId, cancellationToken).ConfigureAwait(false))?.CurrentVersion ?? 0) + 1;
            if (await CreateVersionAsync(kekId, next, cancellationToken).ConfigureAwait(false))
            {
                return (await DescribeAsync(kekId, cancellationToken).ConfigureAwait(false))!;
            }
        }

        throw new InvalidOperationException($"KEK {kekId} kept changing while rotating; retry.");
    }

    public async Task DestroyVersionAsync(string kekId, int version, CancellationToken cancellationToken = default)
    {
        var info = await DescribeAsync(kekId, cancellationToken).ConfigureAwait(false);
        if (info is null || !info.Versions.Contains(version))
        {
            return;
        }

        if (version == info.CurrentVersion)
        {
            throw new InvalidOperationException($"The newest version of KEK {kekId} cannot be destroyed alone; destroy the KEK instead.");
        }

        _store.Destroy(Kind, kekId, version);
    }

    public Task DestroyAsync(string kekId, CancellationToken cancellationToken = default)
    {
        _store.DestroyAll(Kind, KeyEncryptionKeyIds.Validate(kekId));
        return Task.CompletedTask;
    }

    private async Task<bool> CreateVersionAsync(string kekId, int version, CancellationToken cancellationToken)
    {
        if (version == 1 && kekId == KeyEncryptionKeyIds.Installation && !_store.CreateMissingKeys)
        {
            throw new KeyUnavailableException(
                $"The installation KEK does not exist and {KeyManagementOptions.SectionName}:Local:CreateMissingKeys is false; restore the key directory.");
        }

        var material = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            return await _store.TryCreateAsync(Kind, kekId, version, material, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private async Task<byte[]> ReadAsync(string kekId, int version, CancellationToken cancellationToken)
    {
        var kek = await _store.ReadAsync(Kind, KeyEncryptionKeyIds.Validate(kekId), version, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyUnavailableException($"KEK {kekId} v{version} does not exist (destroyed or never created).");
        if (kek.Length != KeySize)
        {
            CryptographicOperations.ZeroMemory(kek);
            throw new KeyUnavailableException($"KEK {kekId} v{version} has the wrong size.");
        }

        return kek;
    }

    private static byte[] Aad(string kekId, int version) =>
        Encoding.UTF8.GetBytes("opportunity/kek/" + kekId + "/v" + version.ToString(CultureInfo.InvariantCulture));
}

/// <summary>The built-in signing key provider over <see cref="LocalKeyStore"/>: ECDSA P-256 keys stored as PKCS#8.</summary>
public sealed class LocalSigningKeyProvider : ISigningKeyProvider
{
    private const string Kind = "signing";

    private readonly LocalKeyStore _store;

    public LocalSigningKeyProvider(LocalKeyStoreOptions options, ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        _store = new LocalKeyStore(options, secrets);
    }

    public async Task<KeySignature> SignAsync(string purpose, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var versions = _store.Versions(Kind, KeyEncryptionKeyIds.Validate(purpose));
        int version;
        if (versions.Count == 0)
        {
            if (!_store.CreateMissingKeys)
            {
                throw new KeyUnavailableException($"Signing key {purpose} does not exist and CreateMissingKeys is false.");
            }

            await CreateVersionAsync(purpose, 1, cancellationToken).ConfigureAwait(false);
            version = _store.Versions(Kind, purpose)[^1];
        }
        else
        {
            version = versions[^1];
        }

        using var key = await LoadAsync(purpose, version, cancellationToken).ConfigureAwait(false);
        return new KeySignature(purpose, version, KeySignature.Es256, key.SignData(data.Span, HashAlgorithmName.SHA256));
    }

    public async Task<bool> VerifyAsync(KeySignature signature, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Algorithm != KeySignature.Es256 || !KeyEncryptionKeyIds.IsValid(signature.Purpose))
        {
            return false;
        }

        var pkcs8 = await _store.ReadAsync(Kind, signature.Purpose, signature.Version, cancellationToken).ConfigureAwait(false);
        if (pkcs8 is null)
        {
            return false;
        }

        using var key = Import(pkcs8);
        return key.VerifyData(data.Span, signature.Value, HashAlgorithmName.SHA256);
    }

    public async Task<SigningPublicKey> GetPublicKeyAsync(string purpose, int? version = null, CancellationToken cancellationToken = default)
    {
        var versions = _store.Versions(Kind, KeyEncryptionKeyIds.Validate(purpose));
        var chosen = version ?? (versions.Count > 0 ? versions[^1] : throw new KeyUnavailableException($"Signing key {purpose} does not exist."));
        using var key = await LoadAsync(purpose, chosen, cancellationToken).ConfigureAwait(false);
        return new SigningPublicKey(purpose, chosen, KeySignature.Es256, key.ExportSubjectPublicKeyInfo());
    }

    public async Task<int> RotateAsync(string purpose, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var versions = _store.Versions(Kind, KeyEncryptionKeyIds.Validate(purpose));
            var next = (versions.Count > 0 ? versions[^1] : 0) + 1;
            if (await CreateVersionAsync(purpose, next, cancellationToken).ConfigureAwait(false))
            {
                return next;
            }
        }

        throw new InvalidOperationException($"Signing key {purpose} kept changing while rotating; retry.");
    }

    private async Task<bool> CreateVersionAsync(string purpose, int version, CancellationToken cancellationToken)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            return await _store.TryCreateAsync(Kind, purpose, version, pkcs8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private async Task<ECDsa> LoadAsync(string purpose, int version, CancellationToken cancellationToken)
    {
        var pkcs8 = await _store.ReadAsync(Kind, purpose, version, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyUnavailableException($"Signing key {purpose} v{version} does not exist.");
        return Import(pkcs8);
    }

    private static ECDsa Import(byte[] pkcs8)
    {
        try
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }
}
