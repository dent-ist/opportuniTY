using System.Diagnostics;
using System.Text;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Opportunity.Storage;
using Opportunity.Storage.S3;

namespace Opportunity.IntegrationTests.Storage;

/// <summary>
/// SeaweedFS S3 gateway (Apache-2.0) with SigV4 credentials enforced, so presigned URLs are really verified. One bucket
/// per run; tests isolate themselves with a random installation prefix. To be folded into the shared fixture library.
/// </summary>
public sealed class SeaweedFsFixture : IAsyncLifetime
{
    public const string AccessKey = "opportunity";
    public const string SecretKey = "opportunity-secret";
    public const string Bucket = "contract";

    private const int Port = 8333;

    private const string IdentitiesJson = $$"""
        {"identities":[{"name":"opportunity","credentials":[{"accessKey":"{{AccessKey}}","secretKey":"{{SecretKey}}"}],
          "actions":["Admin","Read","List","Tagging","Write"]}]}
        """;

    private readonly IContainer _container = new ContainerBuilder(StorageImages.ObjectStore)
        .WithPortBinding(Port, assignRandomHostPort: true)
        .WithResourceMapping(Encoding.UTF8.GetBytes(IdentitiesJson), "/etc/seaweedfs/s3.json")
        .WithCommand("server", "-dir=/data", "-ip.bind=0.0.0.0", "-volume.max=0", "-s3", "-s3.config=/etc/seaweedfs/s3.json")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(Port))
        .Build();

    public Uri ServiceUrl => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}/");

    public S3ObjectStoreOptions Options(string installationPrefix, long partSizeBytes) => new()
    {
        ServiceUrl = ServiceUrl,
        Bucket = Bucket,
        InstallationPrefix = installationPrefix,
        AccessKey = AccessKey,
        SecretKey = SecretKey,
        PartSizeBytes = partSizeBytes,
    };

    /// <summary>Raw client for tampering with objects behind the store's back.</summary>
    public AmazonS3Client CreateRawClient() => new(
        new BasicAWSCredentials(AccessKey, SecretKey),
        new AmazonS3Config
        {
            ServiceURL = ServiceUrl.ToString(),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            UseHttp = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // The S3 port opens before the filer and volume server accept writes; bootstrap, then probe a real write.
        using var store = new S3ObjectStore(Options("probe", S3ObjectStoreOptions.MinPartSizeBytes));
        using var raw = CreateRawClient();
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await store.EnsureBucketAsync();
                await raw.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = "probe/ready", ContentBody = "ok" });
                await raw.DeleteObjectAsync(Bucket, "probe/ready");
                return;
            }
            catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException or IOException && stopwatch.Elapsed < TimeSpan.FromSeconds(90))
            {
                await Task.Delay(250);
            }
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SeaweedFsGroup : ICollectionFixture<SeaweedFsFixture>
{
    public const string Name = "Object storage: SeaweedFS (S3)";
}
