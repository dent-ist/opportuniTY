using System.Buffers;
using System.Security.Cryptography;

using Opportunity.Application.Storage;

namespace Opportunity.Storage;

/// <summary>Write-once, checksum and prefix rules shared by every provider.</summary>
public abstract class ObjectStoreBase : IObjectStore
{
    protected const int CopyBufferSize = 81920;

    protected ObjectStoreBase(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        KeyId = keyId;
    }

    /// <summary>Key identifier recorded with every new object (ADR-011 §6).</summary>
    public string KeyId { get; }

    public abstract Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default);

    public abstract Task<Stream> OpenReadAsync(ObjectKey key, ByteRange? range = null, CancellationToken cancellationToken = default);

    public abstract Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default);

    public abstract IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default);

    public async Task<DeletePrefixResult> DeletePrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (!prefix.IsDeletable)
        {
            throw new ArgumentException($"Prefix '{prefix}' is not a deletable shape (ADR-011 §7.1).", nameof(prefix));
        }

        return await DeletePrefixCoreAsync(prefix, cancellationToken).ConfigureAwait(false);
    }

    protected abstract Task<DeletePrefixResult> DeletePrefixCoreAsync(ObjectPrefix prefix, CancellationToken cancellationToken);

    /// <summary>
    /// Set by <see cref="Encryption.EnvelopeObjectStore"/> for the duration of one put: the bytes are ciphertext, so a
    /// content-addressed key names the hash of the plaintext, which the envelope layer verifies itself.
    /// </summary>
    private static readonly AsyncLocal<bool> CiphertextWrite = new();

    /// <summary>Puts ciphertext: the provider skips the content-address check (the caller verified the plaintext hash).</summary>
    internal async Task<PutObjectResult> PutCiphertextAsync(ObjectKey key, Stream content, PutObjectOptions options, CancellationToken cancellationToken)
    {
        CiphertextWrite.Value = true; // Flows into the provider's put only; restored when this method returns.
        return await PutAsync(key, content, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The SHA-256 the bytes must have: from the options, the content-addressed key, or both (which must agree).</summary>
    protected static Sha256Digest? ResolveExpectedSha256(ObjectKey key, PutObjectOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        if (CiphertextWrite.Value)
        {
            return options.ExpectedSha256;
        }

        if (key.ContentSha256 is { } fromKey && options.ExpectedSha256 is { } fromOptions && fromKey != fromOptions)
        {
            throw new ArgumentException("ExpectedSha256 contradicts the content-addressed key.", nameof(options));
        }

        return options.ExpectedSha256 ?? key.ContentSha256;
    }

    /// <summary>Throws <see cref="ObjectIntegrityException"/> when the streamed bytes are not what the caller declared.</summary>
    protected static void VerifyWritten(ObjectKey key, PutObjectOptions options, Sha256Digest? expected, Sha256Digest actual, long length)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ExpectedLength is { } expectedLength && expectedLength != length)
        {
            throw new ObjectIntegrityException(key, $"expected {expectedLength} bytes, received {length}.");
        }

        if (expected is { } e && e != actual)
        {
            throw new ObjectIntegrityException(key, "SHA-256 of the received bytes does not match the expected value.");
        }
    }

    /// <summary>
    /// Resolves a put against an object that already exists: identical bytes are a no-op, anything else violates
    /// write-once. When the provider did not record a hash the existing bytes are streamed and hashed.
    /// </summary>
    protected async Task<PutObjectResult> ResolveExistingAsync(ObjectKey key, Sha256Digest incoming, long incomingLength, CancellationToken cancellationToken)
    {
        var existing = await HeadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new ObjectAlreadyExistsException(key); // Raced with a delete; treat as a conflict and let the caller retry.

        var existingSha = existing.Sha256;
        if (existingSha is null)
        {
            var stream = await OpenReadAsync(key, null, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                existingSha = await Sha256Digest.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }

        if (existing.Length != incomingLength || existingSha != incoming)
        {
            throw new ObjectAlreadyExistsException(key);
        }

        return new PutObjectResult(key, PutOutcome.AlreadyExisted, incoming, incomingLength, existing.KeyId ?? KeyId, EncryptionScheme.ProviderSse);
    }

    /// <summary>Consumes and hashes the whole stream without storing it (re-put of an existing key).</summary>
    protected static async Task<(Sha256Digest Sha256, long Length)> HashStreamAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long length = 0;
            int n;
            while ((n = await content.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                length += n;
            }

            return (Sha256Digest.FromBytes(hash.GetHashAndReset()), length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the stream ends; returns the byte count.</summary>
    protected static async Task<int> FillAsync(Stream content, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var filled = 0;
        while (filled < count)
        {
            var n = await content.ReadAsync(buffer.AsMemory(filled, count - filled), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            filled += n;
        }

        return filled;
    }

    /// <summary>Maps a logical key or prefix to the provider's physical name: <c>{installationPrefix}/{logical}</c>.</summary>
    protected static string Physical(string installationPrefix, string logical) =>
        installationPrefix.Length == 0 ? logical : installationPrefix + "/" + logical;

    protected static string ValidateInstallationPrefix(string? installationPrefix)
    {
        var value = (installationPrefix ?? string.Empty).Trim('/');
        if (value.Length > 0 && value.Split('/').Any(s => !IsPrefixSegment(s)))
        {
            throw new ArgumentException("InstallationPrefix must use the key grammar [a-z0-9._-] with '/' separators.", nameof(installationPrefix));
        }

        return value;

        static bool IsPrefixSegment(string s) =>
            s.Length > 0 && s is not "." and not ".." && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-');
    }

    /// <summary>Parses a physical name back into a logical key; null for names outside the logical namespace.</summary>
    protected static ObjectKey? ToLogical(string installationPrefix, string physical)
    {
        ArgumentNullException.ThrowIfNull(physical);
        var logical = installationPrefix.Length == 0
            ? physical
            : physical.StartsWith(installationPrefix + "/", StringComparison.Ordinal) ? physical[(installationPrefix.Length + 1)..] : null;
        return ObjectKey.TryParse(logical, out var key) ? key : null;
    }
}
