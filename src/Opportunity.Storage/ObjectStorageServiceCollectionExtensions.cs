using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Bootstrap;
using Opportunity.Application.Storage;
using Opportunity.Storage.AzureBlob;
using Opportunity.Storage.FileSystem;
using Opportunity.Storage.S3;

namespace Opportunity.Storage;

public static class ObjectStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the configured provider as <see cref="IObjectStore"/> and <see cref="IObjectUrlSigner"/>, plus a
    /// migrator bootstrap step that creates the bucket/container. Hosts bind <see cref="ObjectStorageOptions"/> from the
    /// <c>ObjectStorage</c> section.
    /// </summary>
    public static IServiceCollection AddObjectStorage(this IServiceCollection services, ObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<ObjectStoreBase>(_ => CreateStore(options));
        services.AddSingleton<IObjectStore>(sp => sp.GetRequiredService<ObjectStoreBase>());
        services.AddSingleton(sp => (IObjectUrlSigner)sp.GetRequiredService<ObjectStoreBase>());
        services.AddSingleton<IInfrastructureBootstrapStep, ObjectStoreBootstrapStep>();
        return services;
    }

    public static ObjectStoreBase CreateStore(ObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var policy = options.CreatePresignPolicy();
        return options.Provider switch
        {
            ObjectStorageProvider.FileSystem => new FileSystemObjectStore(options.FileSystem, options.KeyId),
            ObjectStorageProvider.S3 => new S3ObjectStore(options.S3, options.KeyId, policy),
            ObjectStorageProvider.AzureBlob => new AzureBlobObjectStore(options.AzureBlob, options.KeyId, policy),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "Unknown object storage provider."),
        };
    }
}

/// <summary>Creates the installation bucket or container; idempotent.</summary>
public sealed class ObjectStoreBootstrapStep(ObjectStoreBase store) : IInfrastructureBootstrapStep
{
    public string Name => "object-storage";

    public int Order => 300;

    public Task RunAsync(CancellationToken cancellationToken) => store switch
    {
        S3ObjectStore s3 => s3.EnsureBucketAsync(cancellationToken),
        AzureBlobObjectStore azure => azure.EnsureContainerAsync(cancellationToken),
        _ => Task.CompletedTask,
    };
}
