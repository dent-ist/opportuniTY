using Amazon.S3;
using Amazon.S3.Model;

using AwesomeAssertions;

using Opportunity.Testing.ObjectStore;

namespace Opportunity.IntegrationTests.Containers;

[Collection(ObjectStoreCollectionDefinition.Name)]
public sealed class ObjectStoreFixtureTests(ObjectStoreFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Round_trips_an_object_in_an_isolated_bucket()
    {
        await using var bucket = await fixture.CreateBucketAsync(Ct);
        using var s3 = fixture.CreateClient();

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = "natives/doc-1.txt", ContentBody = "original" }, Ct);

        (await ReadAsync(s3, bucket.Name, "natives/doc-1.txt")).Should().Be("original");
    }

    [Fact]
    public async Task Buckets_are_isolated_and_deleted_on_dispose()
    {
        using var s3 = fixture.CreateClient();
        string firstName;
        await using (var first = await fixture.CreateBucketAsync(Ct))
        {
            await using var second = await fixture.CreateBucketAsync(Ct);
            firstName = first.Name;
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = first.Name, Key = "k", ContentBody = "v" }, Ct);

            var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = second.Name }, Ct);
            (listed.S3Objects ?? []).Should().BeEmpty();
        }

        var buckets = await s3.ListBucketsAsync(Ct);
        (buckets.Buckets ?? []).Select(b => b.BucketName).Should().NotContain(firstName);
    }

    [Fact]
    public async Task Rejects_requests_signed_with_wrong_credentials()
    {
        await using var bucket = await fixture.CreateBucketAsync(Ct);
        using var intruder = new AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials("intruder", "wrong-secret"),
            new AmazonS3Config { ServiceURL = fixture.ServiceUrl.ToString(), ForcePathStyle = true, AuthenticationRegion = ObjectStoreFixture.Region });

        var read = () => intruder.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket.Name }, Ct);
        await read.Should().ThrowAsync<AmazonS3Exception>();
    }

    [Fact]
    public async Task Latency_toxic_slows_requests_and_lifting_it_restores_speed()
    {
        await using var bucket = await fixture.CreateBucketAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        using var s3 = fixture.CreateClient(proxy);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = "k", ContentBody = "v" }, Ct);

        await using (await proxy.AddLatencyAsync(Timing.InjectedLatency, cancellationToken: Ct))
        {
            (await Timing.MeasureAsync(() => ReadAsync(s3, bucket.Name, "k"))).Should().BeGreaterThanOrEqualTo(Timing.MinimumObservedLatency);
        }

        (await Timing.MeasureAsync(() => ReadAsync(s3, bucket.Name, "k"))).Should().BeLessThan(Timing.MinimumObservedLatency);
    }

    [Fact]
    public async Task Cut_link_fails_requests_and_restoring_it_recovers()
    {
        await using var bucket = await fixture.CreateBucketAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        using var s3 = fixture.CreateClient(proxy, timeout: TimeSpan.FromSeconds(3), maxErrorRetry: 0);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = "k", ContentBody = "v" }, Ct);

        await proxy.CutAsync(Ct);
        var read = () => ReadAsync(s3, bucket.Name, "k");
        await read.Should().ThrowAsync<Exception>();

        await proxy.RestoreAsync(Ct);
        (await ReadAsync(s3, bucket.Name, "k")).Should().Be("v");
    }

    [Fact]
    public async Task Timeout_toxic_makes_requests_time_out()
    {
        await using var bucket = await fixture.CreateBucketAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        using var s3 = fixture.CreateClient(proxy, timeout: TimeSpan.FromSeconds(1), maxErrorRetry: 0);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = "k", ContentBody = "v" }, Ct);

        await using (await proxy.AddTimeoutAsync(TimeSpan.Zero, cancellationToken: Ct))
        {
            var read = () => ReadAsync(s3, bucket.Name, "k");
            await read.Should().ThrowAsync<Exception>();
        }

        (await ReadAsync(s3, bucket.Name, "k")).Should().Be("v");
    }

    private static async Task<string> ReadAsync(AmazonS3Client s3, string bucket, string key)
    {
        using var response = await s3.GetObjectAsync(bucket, key, Ct);
        using var reader = new StreamReader(response.ResponseStream);
        return await reader.ReadToEndAsync(Ct);
    }
}
