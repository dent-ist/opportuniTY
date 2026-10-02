using Azure.Storage.Blobs;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Opportunity.Storage;
using Opportunity.Storage.AzureBlob;

namespace Opportunity.IntegrationTests.Storage;

/// <summary>Azurite blob service (MIT). One container per run; tests isolate themselves with an installation prefix.</summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    public const string Container = "contract";

    private const int Port = 10000;

    // Azurite's documented, publicly known development account key.
    private const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly IContainer _container = new ContainerBuilder(StorageImages.Azurite)
        .WithPortBinding(Port, assignRandomHostPort: true)
        .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--blobPort", "10000", "--skipApiVersionCheck", "--inMemoryPersistence")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Azurite Blob service successfully listens"))
        .Build();

    public string ConnectionString =>
        $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={AccountKey};" +
        $"BlobEndpoint=http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}/devstoreaccount1;";

    public AzureBlobObjectStoreOptions Options(string installationPrefix, long blockSizeBytes) => new()
    {
        ConnectionString = ConnectionString,
        Container = Container,
        InstallationPrefix = installationPrefix,
        BlockSizeBytes = blockSizeBytes,
    };

    public BlobContainerClient CreateRawClient() => new(ConnectionString, Container);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await new AzureBlobObjectStore(Options(string.Empty, 4 * 1024 * 1024)).EnsureContainerAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AzuriteGroup : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Object storage: Azurite (Azure Blob)";
}
