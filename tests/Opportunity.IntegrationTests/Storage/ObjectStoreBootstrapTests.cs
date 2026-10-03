using System.Text;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Bootstrap;
using Opportunity.Application.Storage;
using Opportunity.IntegrationTests.Containers;
using Opportunity.Storage;
using Opportunity.Testing;
using Opportunity.Testing.ObjectStore;

namespace Opportunity.IntegrationTests.Storage;

/// <summary>The migrator's object-storage step creates the bucket and is a no-op when it exists (E04-T01 / #39).</summary>
[Collection(ObjectStoreCollectionDefinition.Name)]
public sealed class ObjectStoreBootstrapTests(ObjectStoreFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bucket_bootstrap_creates_the_bucket_and_reruns_without_error()
    {
        var bucket = TestIsolation.NewName("boot", '-');
        var options = new ObjectStorageOptions
        {
            Provider = ObjectStorageProvider.S3,
            S3 =
            {
                ServiceUrl = fixture.ServiceUrl,
                Region = ObjectStoreFixture.Region,
                Bucket = bucket,
                AccessKey = ObjectStoreFixture.AccessKey,
                SecretKey = ObjectStoreFixture.SecretKey,
                PartSizeBytes = S3ObjectStoreOptions.MinPartSizeBytes,
            },
        };
        options.Validate();
        await using var services = new ServiceCollection().AddObjectStorage(options).BuildServiceProvider();
        var step = services.GetServices<IInfrastructureBootstrapStep>().Should().ContainSingle().Subject;

        try
        {
            await step.RunAsync(Ct);
            await step.RunAsync(Ct);

            var store = services.GetRequiredService<IObjectStore>();
            var key = ObjectKeys.ImportUpload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await store.PutAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("bootstrap")), cancellationToken: Ct);
            (await store.HeadAsync(key, Ct)).Should().NotBeNull();
        }
        finally
        {
            using var admin = fixture.CreateClient();
            var listed = await admin.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = bucket }, CancellationToken.None);
            foreach (var obj in listed.S3Objects ?? [])
            {
                await admin.DeleteObjectAsync(bucket, obj.Key, CancellationToken.None);
            }

            await admin.DeleteBucketAsync(bucket, CancellationToken.None);
        }
    }
}
