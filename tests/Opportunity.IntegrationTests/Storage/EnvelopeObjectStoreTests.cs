using System.Security.Cryptography;

using AwesomeAssertions;

using Opportunity.Application.Jobs;
using Opportunity.Application.Keys;
using Opportunity.Application.Storage;
using Opportunity.Security.Keys;
using Opportunity.Storage;
using Opportunity.Storage.Encryption;
using Opportunity.Storage.FileSystem;
using Opportunity.Storage.S3;

namespace Opportunity.IntegrationTests.Storage;

/// <summary>
/// Envelope encryption in front of a provider (E05-T09, ADR-011 §6): callers keep plaintext semantics (hash, length,
/// ranges, write-once) while the provider only ever stores ciphertext bound to its key, workspace and data key version.
/// Small 4 KiB chunks so modest payloads span several chunks.
/// </summary>
public abstract class EnvelopeObjectStoreTests : IDisposable
{
    protected const int ChunkLog2 = 12;
    protected const int Chunk = 1 << ChunkLog2;
    protected const int HeaderSize = 64;
    protected const int TagSize = 16;

    private readonly string _keys = Directory.CreateTempSubdirectory("opportunity-envelope-keys-").FullName;
    private ObjectStoreBase? _inner;
    private EnvelopeObjectStore? _store;

    protected EnvelopeObjectStoreTests()
    {
        Keks = new LocalKeyEncryptionKeyProvider(new LocalKeyStoreOptions { KeyDirectory = _keys }, new EnvironmentSecretProvider(_ => null));
        KeyRing = NewKeyRing();
    }

    protected InMemoryWorkspaceDataKeyStore KeyStore { get; } = new();

    protected LocalKeyEncryptionKeyProvider Keks { get; }

    protected WorkspaceKeyService KeyRing { get; }

    protected ObjectStoreBase Inner => _inner ??= CreateInner();

    protected EnvelopeObjectStore Store => _store ??= new EnvelopeObjectStore(Inner, KeyRing, ChunkLog2);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract ObjectStoreBase CreateInner();

    /// <summary>The bytes the provider holds for <paramref name="key"/>, read past the envelope layer.</summary>
    protected async Task<byte[]> StoredBytesAsync(ObjectKey key)
    {
        await using var stream = await Inner.OpenReadAsync(key, cancellationToken: Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    protected WorkspaceKeyService NewKeyRing() =>
        new(KeyStore, Keks, new WorkspaceKeyOptions(), TimeProvider.System);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData((3 * Chunk) + 5)]
    public async Task Objects_round_trip_with_plaintext_hash_and_length_and_the_provider_holds_only_ciphertext(int size)
    {
        var ws = Guid.NewGuid();
        var bytes = Payload(size);
        var key = ObjectKeys.Native(ws, Guid.NewGuid(), Sha256Digest.Compute(bytes));

        var put = await Store.PutAsync(key, new MemoryStream(bytes), new PutObjectOptions { ContentType = "application/pdf", ExpectedLength = size }, Ct);

        put.Outcome.Should().Be(PutOutcome.Created);
        put.Sha256.Should().Be(Sha256Digest.Compute(bytes));
        put.Length.Should().Be(size);
        put.KeyId.Should().Be("wdk-v1");
        put.EncryptionScheme.Should().Be(EncryptionScheme.Envelope);
        (await ReadAllAsync(key)).Should().Equal(bytes);
        var head = await Store.HeadAsync(key, Ct);
        head!.Length.Should().Be(size);
        head.KeyId.Should().Be("wdk-v1");
        head.EncryptionScheme.Should().Be(EncryptionScheme.Envelope);
        head.ContentType.Should().Be("application/pdf");

        var stored = await StoredBytesAsync(key);
        stored.Length.Should().BeGreaterThan(size);
        if (size >= 16)
        {
            stored.AsSpan().IndexOf(bytes.AsSpan(0, 16)).Should().Be(-1, "no plaintext reaches the provider");
        }
    }

    [Fact]
    public async Task Byte_ranges_across_chunk_boundaries_return_the_plaintext_slice()
    {
        var bytes = Payload((3 * Chunk) + 100);
        var key = ObjectKeys.Rendition(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "doc.pdf");
        await Store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);

        foreach (var (offset, length) in new (long, long?)[]
        {
            (0, 10), (Chunk - 3, 7), (Chunk, Chunk), (Chunk + 1, (2 * Chunk) + 10), (3 * Chunk, null), (bytes.Length - 1, 1), (5, null),
        })
        {
            var end = length is { } l ? (int)Math.Min(bytes.Length, offset + l) : bytes.Length;
            (await ReadAllAsync(key, new ByteRange(offset, length))).Should().Equal(bytes[(int)offset..end], $"range {offset}+{length}");
        }

        (await ReadAllAsync(key, new ByteRange(bytes.Length - 5, 100))).Should().Equal(bytes[^5..], "a range past the end is truncated");
        await FluentActions.Awaiting(() => Store.OpenReadAsync(key, new ByteRange(bytes.Length, 1), Ct))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => Store.OpenReadAsync(ObjectKeys.Rendition(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "x.png"), new ByteRange(0, 1), Ct))
            .Should().ThrowAsync<ObjectNotFoundException>();
    }

    [Fact]
    public async Task Write_once_holds_for_the_plaintext()
    {
        var ws = Guid.NewGuid();
        var bytes = Payload(Chunk + 7);
        var key = ObjectKeys.Rendition(ws, Guid.NewGuid(), Guid.NewGuid(), "p000001.png");
        await Store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);

        var again = await Store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);
        var different = () => Store.PutAsync(key, new MemoryStream(Payload(Chunk + 7, seed: 9)), cancellationToken: Ct);

        again.Outcome.Should().Be(PutOutcome.AlreadyExisted);
        again.KeyId.Should().Be("wdk-v1");
        again.EncryptionScheme.Should().Be(EncryptionScheme.Envelope);
        await different.Should().ThrowAsync<ObjectAlreadyExistsException>();
        (await ReadAllAsync(key)).Should().Equal(bytes);
    }

    [Fact]
    public async Task Wrong_plaintext_for_a_content_key_or_declared_length_is_rejected_and_leaves_nothing_behind()
    {
        var ws = Guid.NewGuid();
        var key = ObjectKeys.Native(ws, Guid.NewGuid(), Sha256Digest.Compute(Payload(100)));
        var lengthKey = ObjectKeys.Rendition(ws, Guid.NewGuid(), Guid.NewGuid(), "doc.pdf");

        await FluentActions.Awaiting(() => Store.PutAsync(key, new MemoryStream(Payload(100, seed: 3)), cancellationToken: Ct))
            .Should().ThrowAsync<ObjectIntegrityException>();
        await FluentActions.Awaiting(() => Store.PutAsync(lengthKey, new MemoryStream(Payload(3 * Chunk)), new PutObjectOptions { ExpectedLength = 10 }, Ct))
            .Should().ThrowAsync<ObjectIntegrityException>();

        (await Store.HeadAsync(key, Ct)).Should().BeNull();
        (await Store.HeadAsync(lengthKey, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Tampering_truncation_and_copying_ciphertext_to_another_key_are_detected()
    {
        var ws = Guid.NewGuid();
        var bytes = Payload((2 * Chunk) + 10);
        var key = ObjectKeys.Rendition(ws, Guid.NewGuid(), Guid.NewGuid(), "doc.pdf");
        await Store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);
        var stored = await StoredBytesAsync(key);

        var flipped = stored.ToArray();
        flipped[HeaderSize + Chunk + 40] ^= 0x01;
        var flippedKey = await PutRawAsync(ws, flipped);
        var truncated = await PutRawAsync(ws, stored[..(HeaderSize + (2 * (Chunk + TagSize)))]);
        var copied = await PutRawAsync(ws, stored);

        await FluentActions.Awaiting(() => ReadAllAsync(flippedKey)).Should().ThrowAsync<ObjectIntegrityException>();
        await FluentActions.Awaiting(() => ReadAllAsync(flippedKey, new ByteRange(Chunk + 1, 10))).Should().ThrowAsync<ObjectIntegrityException>();
        await FluentActions.Awaiting(() => ReadAllAsync(truncated)).Should().ThrowAsync<ObjectIntegrityException>("the chunk read as last was not sealed as last");
        await FluentActions.Awaiting(() => ReadAllAsync(copied)).Should().ThrowAsync<ObjectIntegrityException>("the object key is bound to its logical key");
        (await ReadAllAsync(key, new ByteRange(0, 10))).Should().Equal(bytes[..10]);
    }

    [Fact]
    public async Task Objects_from_before_encryption_and_installation_objects_are_read_as_stored()
    {
        var ws = Guid.NewGuid();
        var legacy = Payload(Chunk * 2);
        var legacyKey = ObjectKeys.Native(ws, Guid.NewGuid(), Sha256Digest.Compute(legacy));
        var small = Payload(10);
        var smallKey = ObjectKeys.Native(ws, Guid.NewGuid(), Sha256Digest.Compute(small));
        await Inner.PutAsync(legacyKey, new MemoryStream(legacy), cancellationToken: Ct);
        await Inner.PutAsync(smallKey, new MemoryStream(small), cancellationToken: Ct);
        var certificate = Payload(500);
        var certificateKey = ObjectKeys.DestructionCertificate(Guid.NewGuid(), Sha256Digest.Compute(certificate));

        var put = await Store.PutAsync(certificateKey, new MemoryStream(certificate), cancellationToken: Ct);

        put.EncryptionScheme.Should().Be(EncryptionScheme.ProviderSse);
        (await StoredBytesAsync(certificateKey)).Should().Equal(certificate);
        (await ReadAllAsync(legacyKey)).Should().Equal(legacy);
        (await ReadAllAsync(legacyKey, new ByteRange(Chunk - 2, 4))).Should().Equal(legacy[(Chunk - 2)..(Chunk + 2)]);
        (await ReadAllAsync(smallKey)).Should().Equal(small);
        (await Store.HeadAsync(legacyKey, Ct))!.KeyId.Should().Be(ObjectStorageOptions.InstallationDefaultKeyId);
        (await Store.HeadAsync(legacyKey, Ct))!.EncryptionScheme.Should().Be(EncryptionScheme.ProviderSse);
        (await Store.PutAsync(legacyKey, new MemoryStream(legacy), cancellationToken: Ct)).Outcome.Should().Be(PutOutcome.AlreadyExisted);
    }

    [Fact]
    public async Task Rotating_or_dedicating_keys_affects_new_objects_only_and_shredding_makes_every_object_unreadable()
    {
        var ws = Guid.NewGuid();
        var before = Payload(Chunk + 1);
        var beforeKey = ObjectKeys.Rendition(ws, Guid.NewGuid(), Guid.NewGuid(), "a.png");
        await Store.PutAsync(beforeKey, new MemoryStream(before), cancellationToken: Ct);
        var operatorActor = OperationsActor.Cli("test-operator");

        await KeyRing.UseDedicatedKeyAsync(ws, operatorActor, Ct);
        var after = Payload(Chunk + 2);
        var afterKey = ObjectKeys.Rendition(ws, Guid.NewGuid(), Guid.NewGuid(), "b.png");
        var put = await Store.PutAsync(afterKey, new MemoryStream(after), cancellationToken: Ct);

        put.KeyId.Should().Be("wdk-v2");
        (await Store.HeadAsync(beforeKey, Ct))!.KeyId.Should().Be("wdk-v1", "objects are never re-encrypted");
        (await ReadAllAsync(beforeKey)).Should().Equal(before);
        (await ReadAllAsync(afterKey)).Should().Equal(after);

        await KeyRing.DestroyWorkspaceKeysAsync(ws, operatorActor, Ct);
        var fresh = new EnvelopeObjectStore(Inner, NewKeyRing(), ChunkLog2);

        await FluentActions.Awaiting(() => ReadAllAsync(beforeKey, store: fresh)).Should().ThrowAsync<KeyUnavailableException>();
        await FluentActions.Awaiting(() => ReadAllAsync(afterKey, store: fresh)).Should().ThrowAsync<KeyUnavailableException>();
    }

    [Fact]
    public async Task Envelope_objects_are_never_presigned()
    {
        var key = ObjectKeys.Native(Guid.NewGuid(), Guid.NewGuid(), Sha256Digest.Compute([1]));

        Store.DeliveryMode.Should().Be(ObjectDeliveryMode.Stream);
        await FluentActions.Awaiting(() => Store.PresignGetAsync(key, new PresignGetOptions("x.pdf"), Ct)).Should().ThrowAsync<NotSupportedException>();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_keys))
        {
            Directory.Delete(_keys, recursive: true);
        }
    }

    protected async Task<byte[]> ReadAllAsync(ObjectKey key, ByteRange? range = null, IObjectStore? store = null)
    {
        await using var stream = await (store ?? Store).OpenReadAsync(key, range, Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    protected static byte[] Payload(int size, int seed = 1)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private async Task<ObjectKey> PutRawAsync(Guid ws, byte[] bytes)
    {
        var key = ObjectKeys.Rendition(ws, Guid.NewGuid(), Guid.NewGuid(), "doc.pdf");
        await Inner.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);
        return key;
    }
}

[Collection(FileSystemStoreGroup.Name)]
public sealed class FileSystemEnvelopeObjectStoreTests : EnvelopeObjectStoreTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "opportunity-envelope-" + Guid.NewGuid().ToString("N"));

    protected override ObjectStoreBase CreateInner() => new FileSystemObjectStore(new FileSystemObjectStoreOptions { RootPath = _root });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>The S3 provider adds conditional writes and provider checksums over the ciphertext.</summary>
[Collection(S3StoreGroup.Name)]
public sealed class S3EnvelopeObjectStoreTests(S3StoreFixture fixture) : EnvelopeObjectStoreTests
{
    private readonly string _prefix = "c" + Guid.NewGuid().ToString("N")[..12];

    protected override ObjectStoreBase CreateInner() => new S3ObjectStore(fixture.Options(_prefix, S3ObjectStoreOptions.MinPartSizeBytes));

    [Fact]
    public async Task Multipart_uploads_of_ciphertext_round_trip()
    {
        var bytes = Payload((int)S3ObjectStoreOptions.MinPartSizeBytes + (3 * Chunk) + 11);
        var key = ObjectKeys.Native(Guid.NewGuid(), Guid.NewGuid(), Sha256Digest.Compute(bytes));

        var put = await Store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);

        put.Sha256.Should().Be(Sha256Digest.Compute(bytes));
        (await ReadAllAsync(key, new ByteRange(bytes.Length - 20))).Should().Equal(bytes[^20..]);
        SHA256.HashData(await ReadAllAsync(key)).Should().Equal(SHA256.HashData(bytes));
    }
}
