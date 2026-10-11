using System.Text;

using Amazon.S3;
using Amazon.S3.Model;

using AwesomeAssertions;

using Opportunity.Application.Storage;
using Opportunity.Storage;
using Opportunity.Storage.S3;

namespace Opportunity.IntegrationTests.Storage;

/// <summary>
/// ADR-016 §4.2: the IObjectStore contract runs unchanged against a bucket with versioning enabled (the write-once
/// <c>If-None-Match</c> race in particular) before Full turns versioning on.
/// </summary>
[Collection(S3StoreGroup.Name)]
public sealed class VersionedS3ObjectStoreContractTests(S3StoreFixture fixture) : ObjectStoreContractTests, IAsyncLifetime
{
    private readonly string _prefix = NewInstallationPrefix();

    protected override long PartSizeBytes => S3ObjectStoreOptions.MinPartSizeBytes;

    public async ValueTask InitializeAsync() => await VersionedBucket.EnsureAsync(fixture);

    protected override ObjectStoreBase CreateStore() => new S3ObjectStore(VersionedBucket.Options(fixture, _prefix, PartSizeBytes));

    protected override async Task CorruptAsync(ObjectKey key, byte[] replacement)
    {
        using var raw = fixture.CreateRawClient();
        await raw.PutObjectAsync(
            new PutObjectRequest { BucketName = VersionedBucket.Name, Key = $"{_prefix}/{key.Value}", InputStream = new MemoryStream(replacement) },
            Ct);
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// E19-T08 acceptance: a deleted native object is recoverable from its bucket version (ADR-016 §4.1), and the bootstrap
/// step turns versioning on when <see cref="S3ObjectStoreOptions.Versioning"/> is set.
/// </summary>
[Collection(S3StoreGroup.Name)]
public sealed class ObjectVersionRecoveryTests(S3StoreFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await VersionedBucket.EnsureAsync(fixture);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_deleted_native_comes_back_from_its_version_with_the_same_bytes_and_hash()
    {
        var prefix = "v" + Guid.NewGuid().ToString("N")[..12];
        using var store = new S3ObjectStore(VersionedBucket.Options(fixture, prefix, S3ObjectStoreOptions.MinPartSizeBytes));
        var recovery = store;
        (await recovery.IsVersioningEnabledAsync(Ct)).Should().BeTrue();

        var bytes = Encoding.UTF8.GetBytes("synthetic native for the version recovery drill");
        var sha = Sha256Digest.Compute(bytes);
        var key = ObjectKeys.Native(Guid.CreateVersion7(), Guid.CreateVersion7(), sha);
        await store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);
        (await recovery.RecoverAsync(key, cancellationToken: Ct)).Should().Be(ObjectRecoveryOutcome.AlreadyPresent);

        // An accidental deletion behind the application's back (operator error, a compromised identity).
        using (var raw = fixture.CreateRawClient())
        {
            await raw.DeleteObjectAsync(VersionedBucket.Name, $"{prefix}/{key.Value}", Ct);
        }

        (await store.HeadAsync(key, Ct)).Should().BeNull();
        var versions = await recovery.ListVersionsAsync(key, Ct);
        versions.Should().Contain(v => v.IsDeleteMarker && v.IsLatest).And.Contain(v => !v.IsDeleteMarker && v.Size == bytes.Length);

        (await recovery.RecoverAsync(key, cancellationToken: Ct)).Should().Be(ObjectRecoveryOutcome.Recovered);
        var info = await store.HeadAsync(key, Ct);
        info.Should().NotBeNull();
        info!.Sha256.Should().Be(sha, "the recovered object keeps its registered hash");
        await using var read = await store.OpenReadAsync(key, cancellationToken: Ct);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, Ct);
        copy.ToArray().Should().Equal(bytes);

        var missing = ObjectKeys.Native(Guid.CreateVersion7(), Guid.CreateVersion7(), Sha256Digest.Compute([1, 2, 3]));
        (await recovery.RecoverAsync(missing, cancellationToken: Ct)).Should().Be(ObjectRecoveryOutcome.NoVersion);
    }

    [Fact]
    public async Task The_bootstrap_step_turns_versioning_on_when_configured()
    {
        var bucket = "versioning-" + Guid.NewGuid().ToString("N")[..12];
        var options = fixture.Options("bootstrap", S3ObjectStoreOptions.MinPartSizeBytes);
        options.Bucket = bucket;
        options.Versioning = true;
        using var store = new S3ObjectStore(options);
        await new ObjectStoreBootstrapStep(store).RunAsync(Ct);
        await new ObjectStoreBootstrapStep(store).RunAsync(Ct);
        (await ((IObjectVersionRecovery)store).IsVersioningEnabledAsync(Ct)).Should().BeTrue();
        using var raw = fixture.CreateRawClient();
        await raw.DeleteBucketAsync(bucket, Ct);
    }
}

/// <summary>A second bucket in the S3 contract store with versioning enabled (idempotent).</summary>
internal static class VersionedBucket
{
    public const string Name = "contract-versioned";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _ready;

    public static S3ObjectStoreOptions Options(S3StoreFixture fixture, string prefix, long partSize)
    {
        var options = fixture.Options(prefix, partSize);
        options.Bucket = Name;
        return options;
    }

    public static async Task EnsureAsync(S3StoreFixture fixture)
    {
        await Gate.WaitAsync();
        try
        {
            if (_ready)
            {
                return;
            }

            var options = Options(fixture, "bootstrap", S3ObjectStoreOptions.MinPartSizeBytes);
            options.Versioning = true;
            using var store = new S3ObjectStore(options);
            await store.EnsureBucketAsync();
            await store.EnableVersioningAsync();
            _ready = true;
        }
        catch (AmazonS3Exception ex)
        {
            throw new InvalidOperationException($"The S3 store refused bucket versioning: {ex.ErrorCode} {ex.Message}", ex);
        }
        finally
        {
            Gate.Release();
        }
    }
}
