using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Storage;

namespace Opportunity.Import.Jobs;

/// <summary>An uploaded load file stored for an import (ADR-011 import source area, content-addressed).</summary>
public sealed record StoredImportSource(string ObjectKey, byte[] Sha256, long Size);

/// <summary>
/// Stores an import's uploaded DAT under <c>ws/{id}/imports/{importId}/source/{sha256}</c>. The API uses this instead of
/// the object store itself: only the protected-content gateway of the API may reach storage (ADR-015 D12.1), and an
/// upload is not content delivery.
/// </summary>
public interface IImportSourceStore
{
    /// <summary>False when the installation has no object storage configured.</summary>
    bool IsAvailable { get; }

    /// <param name="open">Opens the upload from its start; called twice (hash, then store).</param>
    Task<StoredImportSource> StoreAsync(Guid workspaceId, Guid importId, Func<Stream> open, long length, CancellationToken cancellationToken = default);
}

public sealed class ObjectStoreImportSourceStore(IObjectStore? store) : IImportSourceStore
{
    public bool IsAvailable => store is not null;

    public async Task<StoredImportSource> StoreAsync(
        Guid workspaceId, Guid importId, Func<Stream> open, long length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(open);
        var objects = store ?? throw new InvalidOperationException("Object storage is not configured.");
        byte[] sha256;
        var hashing = open();
        await using (hashing.ConfigureAwait(false))
        {
            sha256 = await SHA256.HashDataAsync(hashing, cancellationToken).ConfigureAwait(false);
        }

        var key = ObjectKeys.ImportSource(workspaceId, importId, Sha256Digest.FromBytes(sha256));
        var upload = open();
        await using (upload.ConfigureAwait(false))
        {
            await objects.PutAsync(key, upload, new PutObjectOptions { ContentType = "application/octet-stream", ExpectedLength = length }, cancellationToken)
                .ConfigureAwait(false);
        }

        return new StoredImportSource(key.Value, sha256, length);
    }
}

public static class ImportSourceStoreRegistration
{
    /// <summary>The import source store over the host's object store, when one is registered.</summary>
    public static IServiceCollection AddImportSourceStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IImportSourceStore>(sp => new ObjectStoreImportSourceStore(sp.GetService<IObjectStore>()));
        return services;
    }
}
