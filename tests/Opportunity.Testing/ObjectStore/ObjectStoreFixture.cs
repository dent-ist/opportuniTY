using System.Diagnostics;
using System.Text;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Opportunity.Testing.Images;
using Opportunity.Testing.Toxiproxy;

namespace Opportunity.Testing.ObjectStore;

/// <summary>
/// S3-compatible object store behind Toxiproxy: SeaweedFS (Apache-2.0, chosen for decision Q-38's
/// permissive-license default) with SigV4 credentials enforced. Each test gets its own bucket.
/// </summary>
public class ObjectStoreFixture : DependencyFixture
{
    public const string AccessKey = "opportunity";
    public const string SecretKey = "opportunity-secret";
    public const string Region = "us-east-1";

    private const string HostAlias = "objectstore";
    private const int Port = 8333;

    private const string IdentitiesJson = $$"""
        {"identities":[{"name":"opportunity","credentials":[{"accessKey":"{{AccessKey}}","secretKey":"{{SecretKey}}"}],
          "actions":["Admin","Read","List","Tagging","Write"]}]}
        """;

    private readonly IContainer _container;
    private AmazonS3Client? _admin;

    public ObjectStoreFixture()
    {
        _container = new ContainerBuilder(ContainerImages.ObjectStore)
            .WithNetwork(Network)
            .WithNetworkAliases(HostAlias)
            .WithPortBinding(Port, assignRandomHostPort: true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(IdentitiesJson), "/etc/seaweedfs/s3.json")
            .WithCommand("server", "-dir=/data", "-ip.bind=0.0.0.0", "-volume.max=0", "-s3", "-s3.config=/etc/seaweedfs/s3.json")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(Port))
            .Build();
    }

    /// <summary>Direct S3 endpoint.</summary>
    public Uri ServiceUrl => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}/");

    protected override string NetworkAlias => HostAlias;

    protected override int ServicePort => Port;

    /// <summary>
    /// An S3 client for the store (path-style addressing), optionally routed through a fault proxy.
    /// The caller owns and disposes it.
    /// </summary>
    public AmazonS3Client CreateClient(FaultProxy? via = null, TimeSpan? timeout = null, int maxErrorRetry = 2)
    {
        var url = via is null ? ServiceUrl : new Uri($"http://{via.Host}:{via.Port}/");
        var config = new AmazonS3Config
        {
            ServiceURL = url.ToString(),
            ForcePathStyle = true,
            AuthenticationRegion = Region,
            UseHttp = true,
            MaxErrorRetry = maxErrorRetry,
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
            // Only send/validate checksums when an operation requires them; keeps uploads plain for any S3 server.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        return new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), config);
    }

    /// <summary>Creates an isolated bucket for one test; dispose to empty and delete it.</summary>
    public async Task<ObjectStoreBucket> CreateBucketAsync(CancellationToken cancellationToken = default)
    {
        var name = TestIsolation.NewName("t", '-');
        await Admin.PutBucketAsync(name, cancellationToken).ConfigureAwait(false);
        return new ObjectStoreBucket(this, name);
    }

    internal async Task DeleteBucketAsync(string name)
    {
        var request = new ListObjectsV2Request { BucketName = name };
        ListObjectsV2Response page;
        do
        {
            page = await Admin.ListObjectsV2Async(request).ConfigureAwait(false);
            foreach (var obj in page.S3Objects ?? [])
            {
                await Admin.DeleteObjectAsync(name, obj.Key).ConfigureAwait(false);
            }

            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated == true);

        await Admin.DeleteBucketAsync(name).ConfigureAwait(false);
    }

    protected override async Task StartDependencyAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        _admin = CreateClient(maxErrorRetry: 0, timeout: TimeSpan.FromSeconds(5));
        await WaitUntilWritableAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
    }

    protected override async ValueTask StopDependencyAsync()
    {
        _admin?.Dispose();
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    private AmazonS3Client Admin => _admin ?? throw new InvalidOperationException("The object store has not been started.");

    // The S3 port opens before the filer and volume server accept writes; probe with a real round trip.
    private async Task WaitUntilWritableAsync(TimeSpan budget)
    {
        var probe = "readiness-" + TestIsolation.NewId();
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await Admin.PutBucketAsync(probe).ConfigureAwait(false);
                await Admin.PutObjectAsync(new PutObjectRequest { BucketName = probe, Key = "probe", ContentBody = "ok" }).ConfigureAwait(false);
                await DeleteBucketAsync(probe).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException or TaskCanceledException or IOException)
            {
                if (stopwatch.Elapsed > budget)
                {
                    throw new TimeoutException($"Object store at {ServiceUrl} did not accept writes within {budget}.", ex);
                }

                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>A per-test bucket. Disposing deletes its objects and the bucket.</summary>
public sealed class ObjectStoreBucket : IAsyncDisposable
{
    private readonly ObjectStoreFixture _fixture;

    internal ObjectStoreBucket(ObjectStoreFixture fixture, string name)
    {
        _fixture = fixture;
        Name = name;
    }

    public string Name { get; }

    public async ValueTask DisposeAsync() => await _fixture.DeleteBucketAsync(Name).ConfigureAwait(false);
}
