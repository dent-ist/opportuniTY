using System.Diagnostics;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using Opportunity.Storage;
using Opportunity.Storage.S3;

namespace Opportunity.IntegrationTests.Storage;

/// <summary>
/// The S3-compatible store the S3 contract suite runs against: the bundled SeaweedFS by default, or the candidate
/// selected with <see cref="S3Provider.ProviderVariable"/> (ADR-020). One bucket per run; tests isolate themselves with
/// a random installation prefix. Credentials are always enforced, so presigned URLs are really verified.
/// </summary>
public sealed class S3StoreFixture : IAsyncLifetime
{
    private readonly S3Provider _provider = S3Provider.FromEnvironment();
    private S3Endpoint? _endpoint;

    public string Bucket => _provider.Bucket;

    private S3Endpoint Endpoint => _endpoint ?? throw new InvalidOperationException("The store has not started.");

    public S3ObjectStoreOptions Options(string installationPrefix, long partSizeBytes) => new()
    {
        ServiceUrl = Endpoint.ServiceUrl,
        Region = Endpoint.Region,
        Bucket = Bucket,
        InstallationPrefix = installationPrefix,
        AccessKey = Endpoint.AccessKey,
        SecretKey = Endpoint.SecretKey,
        PartSizeBytes = partSizeBytes,
    };

    /// <summary>Raw client for tampering with objects behind the store's back.</summary>
    public AmazonS3Client CreateRawClient() => new(
        new BasicAWSCredentials(Endpoint.AccessKey, Endpoint.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = Endpoint.ServiceUrl.ToString(),
            ForcePathStyle = true,
            AuthenticationRegion = Endpoint.Region,
            UseHttp = Endpoint.ServiceUrl.Scheme == Uri.UriSchemeHttp,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public async ValueTask InitializeAsync()
    {
        _endpoint = await _provider.StartAsync(TestContext.Current.CancellationToken);

        // Most stores open their port before they accept writes; bootstrap, then probe a real write.
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

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class S3StoreGroup : ICollectionFixture<S3StoreFixture>
{
    public const string Name = "Object storage: S3-compatible";
}
