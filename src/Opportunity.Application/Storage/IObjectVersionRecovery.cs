namespace Opportunity.Application.Storage;

/// <summary>One version of an object in a versioned bucket (newest first in listings).</summary>
public sealed record ObjectVersion(string VersionId, bool IsLatest, bool IsDeleteMarker, long Size, DateTimeOffset? LastModified);

public enum ObjectRecoveryOutcome
{
    /// <summary>The object is present; nothing was changed.</summary>
    AlreadyPresent,

    /// <summary>The object is current again (its delete marker was removed, or the named version was copied over it).</summary>
    Recovered,

    /// <summary>No earlier version exists: the object is lost in this store (try the replica).</summary>
    NoVersion,
}

/// <summary>
/// Recovery of single objects from bucket versions (ADR-016 §4.1–§4.2): object storage is never rolled back to a point
/// in time; a deleted or damaged object is brought back from its noncurrent version. Versions are a backup mechanism
/// only (ADR-011 §3.4). Offered by providers whose store supports versioning (S3-compatible); used by the restore tool
/// and the <c>restore object</c> command, never by request paths.
/// </summary>
public interface IObjectVersionRecovery
{
    /// <summary>Whether the bucket has versioning enabled.</summary>
    Task<bool> IsVersioningEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>The object's versions and delete markers, newest first.</summary>
    Task<IReadOnlyList<ObjectVersion>> ListVersionsAsync(ObjectKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the object current again: removes a delete marker that hides it, or with <paramref name="versionId"/> copies
    /// that version over the current one. A present object is left alone unless a version is named.
    /// </summary>
    Task<ObjectRecoveryOutcome> RecoverAsync(ObjectKey key, string? versionId = null, CancellationToken cancellationToken = default);
}
