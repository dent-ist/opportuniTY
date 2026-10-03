namespace Opportunity.Application.Storage;

/// <summary>
/// Provider-neutral object storage port (ADR-011 §3, ADR-019). Implementations live in <c>Opportunity.Storage</c> and
/// must pass the shared contract suite. Every method takes a logical key; none exposes a bucket, container or path.
/// </summary>
/// <remarks>
/// URL signing is deliberately a separate port, <see cref="IObjectUrlSigner"/>, so only the protected-content gateway
/// can obtain signed URLs (ADR-015 D12.1).
/// </remarks>
public interface IObjectStore
{
    /// <summary>
    /// Streams <paramref name="content"/> to <paramref name="key"/>, computing SHA-256 on the way, without buffering
    /// more than one part in memory. Objects are write-once: re-putting identical bytes is a no-op
    /// (<see cref="PutOutcome.AlreadyExisted"/>); different bytes throw <see cref="ObjectAlreadyExistsException"/>.
    /// A content-addressed key, or <see cref="PutObjectOptions.ExpectedSha256"/>, that does not match the streamed
    /// bytes throws <see cref="ObjectIntegrityException"/> and leaves no object behind.
    /// </summary>
    Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Opens a forward-only stream over the object or a byte range of it.</summary>
    /// <exception cref="ObjectNotFoundException">The key does not exist.</exception>
    Task<Stream> OpenReadAsync(ObjectKey key, ByteRange? range = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the object's metadata, or null when it does not exist.</summary>
    Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default);

    /// <summary>Lists every object under the prefix in ordinal key order, paging internally.</summary>
    IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every object under a deletable prefix (ADR-011 §7). Idempotent and resumable. Only the lifecycle worker
    /// may call this.
    /// </summary>
    /// <exception cref="ArgumentException">The prefix is not one of the deletable shapes.</exception>
    Task<DeletePrefixResult> DeletePrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default);
}

/// <summary>An inclusive-start byte range. A null <see cref="Length"/> reads to the end.</summary>
public readonly record struct ByteRange
{
    public ByteRange(long offset, long? length = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length is { } l)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(l, nameof(length));
        }

        Offset = offset;
        Length = length;
    }

    public long Offset { get; }

    public long? Length { get; }

    /// <summary>Inclusive last byte, or null for open-ended.</summary>
    public long? LastByte => Length is { } l ? Offset + l - 1 : null;
}

public sealed record PutObjectOptions
{
    /// <summary>Stored content type; sniffed by the caller from magic bytes, never taken from a load file.</summary>
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>When set, the streamed bytes must hash to this value. Content-addressed keys imply it.</summary>
    public Sha256Digest? ExpectedSha256 { get; init; }

    /// <summary>When set, the streamed byte count must equal this value.</summary>
    public long? ExpectedLength { get; init; }
}

public enum PutOutcome
{
    Created,

    /// <summary>The key already held identical bytes; nothing was written.</summary>
    AlreadyExisted,
}

public enum EncryptionScheme
{
    ProviderSse,
    Envelope,
}

/// <summary>What the caller records in the <c>StoredObject</c> registry row (ADR-011 §2.3, §6).</summary>
public sealed record PutObjectResult(
    ObjectKey Key,
    PutOutcome Outcome,
    Sha256Digest Sha256,
    long Length,
    string KeyId,
    EncryptionScheme EncryptionScheme);

/// <param name="Sha256">Recorded by the provider when it was known before the write committed; otherwise null and the
/// PostgreSQL registry is the authority.</param>
/// <param name="KeyId">The encryption key identifier recorded with the object (ADR-011 §6.1).</param>
public sealed record ObjectInfo(
    ObjectKey Key,
    long Length,
    Sha256Digest? Sha256,
    string? KeyId,
    string ContentType,
    DateTimeOffset? LastModified);

public sealed record ObjectListing(ObjectKey Key, long Length);

/// <param name="Residuals">Copies the provider could not remove (non-current versions, soft-deleted blobs), reported
/// for the destruction certificate (ADR-014).</param>
public sealed record DeletePrefixResult(long ObjectCount, long ByteCount, IReadOnlyList<string> Residuals);
