namespace Opportunity.Application.Storage;

/// <summary>Base type for provider-neutral object-store failures. Messages carry logical keys only, never URLs.</summary>
public abstract class ObjectStoreException : Exception
{
    protected ObjectStoreException(ObjectKey key, string message, Exception? innerException = null)
        : base(message, innerException) => Key = key;

    public ObjectKey Key { get; }
}

public sealed class ObjectNotFoundException(ObjectKey key, Exception? innerException = null)
    : ObjectStoreException(key, $"Object '{key}' does not exist.", innerException);

/// <summary>A put would overwrite a write-once object with different bytes (ADR-011 §4.1).</summary>
public sealed class ObjectAlreadyExistsException(ObjectKey key, Exception? innerException = null)
    : ObjectStoreException(key, $"Object '{key}' already exists with different content; objects are write-once.", innerException);

/// <summary>Bytes did not match the expected SHA-256 or length, on write or on a verified read.</summary>
public sealed class ObjectIntegrityException(ObjectKey key, string detail)
    : ObjectStoreException(key, $"Integrity check failed for '{key}': {detail}");
