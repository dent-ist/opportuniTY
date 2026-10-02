using Amazon.S3.Model;

using Opportunity.Application.Storage;
using Opportunity.Storage;
using Opportunity.Storage.AzureBlob;
using Opportunity.Storage.FileSystem;
using Opportunity.Storage.S3;

namespace Opportunity.IntegrationTests.Storage;

/// <summary>Lite filesystem provider against the contract.</summary>
[Collection(FileSystemStoreGroup.Name)]
public sealed class FileSystemObjectStoreContractTests : ObjectStoreContractTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opportunity-store-" + Guid.NewGuid().ToString("N"));

    protected override long PartSizeBytes => 4 * 1024 * 1024;

    protected override ObjectStoreBase CreateStore() => new FileSystemObjectStore(new FileSystemObjectStoreOptions { RootPath = _root });

    protected override async Task CorruptAsync(ObjectKey key, byte[] replacement) =>
        await File.WriteAllBytesAsync(Path.Combine(_root, "objects", key.Value), replacement, Ct);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>S3-compatible provider against SeaweedFS (Apache-2.0).</summary>
[Collection(SeaweedFsGroup.Name)]
public sealed class S3ObjectStoreContractTests(SeaweedFsFixture fixture) : ObjectStoreContractTests
{
    private readonly string _prefix = NewInstallationPrefix();

    protected override long PartSizeBytes => S3ObjectStoreOptions.MinPartSizeBytes;

    protected override ObjectStoreBase CreateStore() => new S3ObjectStore(fixture.Options(_prefix, PartSizeBytes));

    protected override async Task CorruptAsync(ObjectKey key, byte[] replacement)
    {
        using var raw = fixture.CreateRawClient();
        await raw.PutObjectAsync(
            new PutObjectRequest { BucketName = SeaweedFsFixture.Bucket, Key = $"{_prefix}/{key.Value}", InputStream = new MemoryStream(replacement) },
            Ct);
    }
}

/// <summary>Azure Blob provider against Azurite (MIT).</summary>
[Collection(AzuriteGroup.Name)]
public sealed class AzureBlobObjectStoreContractTests(AzuriteFixture fixture) : ObjectStoreContractTests
{
    private readonly string _prefix = NewInstallationPrefix();

    protected override long PartSizeBytes => 4 * 1024 * 1024;

    protected override ObjectStoreBase CreateStore() => new AzureBlobObjectStore(fixture.Options(_prefix, PartSizeBytes));

    protected override async Task CorruptAsync(ObjectKey key, byte[] replacement) =>
        await fixture.CreateRawClient().GetBlobClient($"{_prefix}/{key.Value}").UploadAsync(new BinaryData(replacement), overwrite: true, Ct);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FileSystemStoreGroup
{
    public const string Name = "Object storage: filesystem";
}
