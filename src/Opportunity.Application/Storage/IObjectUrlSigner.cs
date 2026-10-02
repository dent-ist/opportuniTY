namespace Opportunity.Application.Storage;

/// <summary>How the protected-content gateway delivers bytes from the configured provider (ADR-011 §5.4).</summary>
public enum ObjectDeliveryMode
{
    /// <summary>The API proxies the bytes (always for the filesystem provider).</summary>
    Stream,

    /// <summary>The browser fetches a short-lived signed URL.</summary>
    Presign,
}

/// <summary>
/// Issues presigned URLs. Only the protected-content gateway (<c>E05-T04</c>) may depend on this port, after the PDP
/// check and the audit write (ADR-015 D12.1); an architecture test enforces it.
/// </summary>
/// <remarks>
/// Presigned URLs are bearer secrets: never log, trace, audit or persist them (ADR-011 §5.6).
/// <see cref="PresignedObjectUrl.ToString"/> is redacted for that reason.
/// </remarks>
public interface IObjectUrlSigner
{
    ObjectDeliveryMode DeliveryMode { get; }

    /// <summary>
    /// Signs one GET for a native or an export/production package (ADR-015 D12.3). The response is forced to
    /// <c>Content-Disposition: attachment</c>, <c>Content-Type: application/octet-stream</c> and
    /// <c>Cache-Control: private, no-store</c>.
    /// </summary>
    /// <exception cref="NotSupportedException"><see cref="DeliveryMode"/> is <see cref="ObjectDeliveryMode.Stream"/>.</exception>
    /// <exception cref="ArgumentException">The key's area may not be presigned or the TTL is out of bounds.</exception>
    Task<PresignedObjectUrl> PresignGetAsync(ObjectKey key, PresignGetOptions options, CancellationToken cancellationToken = default);

    /// <summary>Signs one PUT to an import upload-staging key only (ADR-011 §5.7, ADR-015 D12.4).</summary>
    /// <exception cref="NotSupportedException"><see cref="DeliveryMode"/> is <see cref="ObjectDeliveryMode.Stream"/>.</exception>
    Task<PresignedObjectUrl> PresignPutAsync(ObjectKey key, PresignPutOptions options, CancellationToken cancellationToken = default);
}

/// <param name="DownloadFileName">Delivered file name, e.g. the sanitized control number plus extension. It is
/// sanitized again by <see cref="ContentDispositionHeader"/>.</param>
/// <param name="Ttl">Null for the installation default (60 s).</param>
public sealed record PresignGetOptions(string DownloadFileName, TimeSpan? Ttl = null);

public sealed record PresignPutOptions(long ContentLength, string ContentType, TimeSpan? Ttl = null);

/// <summary>A signed URL. Treat as a secret; <see cref="ToString"/> never reveals it.</summary>
public sealed class PresignedObjectUrl(Uri url, string method, DateTimeOffset expiresAt)
{
    public Uri Url { get; } = url;

    public string Method { get; } = method;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    public override string ToString() => $"PresignedObjectUrl({Method}, expires {ExpiresAt:O}, url redacted)";
}
