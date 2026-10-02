using System.Net;
using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Storage;
using Opportunity.Storage;

namespace Opportunity.IntegrationTests.Storage;

/// <summary>
/// The provider contract (ADR-011 §3.5, E19-T01). Every <see cref="IObjectStore"/> implementation derives a test class
/// from this one and must pass all of it unchanged. Each test gets an isolated store (own root or installation prefix)
/// and random workspace IDs.
/// </summary>
public abstract class ObjectStoreContractTests : IDisposable
{
    private static readonly HttpClient Http = new();

    private ObjectStoreBase? _store;

    protected ObjectStoreBase Store => _store ??= CreateStore();

    protected IObjectUrlSigner Signer => (IObjectUrlSigner)Store;

    /// <summary>Part/block size the provider is configured with; the large-object test spans several parts.</summary>
    protected abstract long PartSizeBytes { get; }

    protected abstract ObjectStoreBase CreateStore();

    /// <summary>Overwrites an object's bytes behind the store's back (bit rot, tampering).</summary>
    protected abstract Task CorruptAsync(ObjectKey key, byte[] replacement);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- put / get / head ----

    [Fact]
    public async Task Put_then_read_round_trips_bytes_and_records_sha256_and_key_id()
    {
        var bytes = Payload("hello, chain of custody");
        var key = ObjectKeys.Native(NewId(), NewId(), Sha256Digest.Compute(bytes));

        var result = await Store.PutAsync(key, new MemoryStream(bytes), new PutObjectOptions { ContentType = "application/pdf" }, Ct);

        result.Outcome.Should().Be(PutOutcome.Created);
        result.Sha256.Should().Be(Sha256Digest.Compute(bytes));
        result.Length.Should().Be(bytes.Length);
        result.KeyId.Should().Be(ObjectStorageOptions.InstallationDefaultKeyId);
        result.EncryptionScheme.Should().Be(EncryptionScheme.ProviderSse);

        var head = await Store.HeadAsync(key, Ct);
        head.Should().NotBeNull();
        head!.Length.Should().Be(bytes.Length);
        head.Sha256.Should().Be(result.Sha256);
        head.KeyId.Should().Be(ObjectStorageOptions.InstallationDefaultKeyId);
        head.ContentType.Should().Be("application/pdf");

        (await ReadAllAsync(key)).Should().Equal(bytes);
    }

    [Fact]
    public async Task Empty_object_round_trips()
    {
        var key = ObjectKeys.Text(NewId(), NewId(), Sha256Digest.Compute([]));

        var result = await Store.PutAsync(key, new MemoryStream(), cancellationToken: Ct);

        result.Length.Should().Be(0);
        (await Store.HeadAsync(key, Ct))!.Length.Should().Be(0);
        (await ReadAllAsync(key)).Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_object_heads_as_null_and_read_throws_not_found()
    {
        var key = ObjectKeys.Rendition(NewId(), NewId(), NewId(), "p000001.png");

        (await Store.HeadAsync(key, Ct)).Should().BeNull();
        var read = () => Store.OpenReadAsync(key, cancellationToken: Ct);
        (await read.Should().ThrowAsync<ObjectNotFoundException>()).Which.Key.Should().Be(key);
    }

    [Fact]
    public async Task Byte_ranges_return_the_requested_slice()
    {
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var key = ObjectKeys.Rendition(NewId(), NewId(), NewId(), "doc.pdf");
        await Store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);

        (await ReadAllAsync(key, new ByteRange(100, 50))).Should().Equal(bytes[100..150]);
        (await ReadAllAsync(key, new ByteRange(990))).Should().Equal(bytes[990..]);
        (await ReadAllAsync(key, new ByteRange(995, 100))).Should().Equal(bytes[995..], "a range past the end is truncated");
        var beyond = () => Store.OpenReadAsync(key, new ByteRange(1000, 10), Ct);
        await beyond.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ---- write-once and checksums ----

    [Fact]
    public async Task Reputting_identical_content_is_a_no_op_verified_by_checksum()
    {
        var bytes = Payload("same bytes");
        var contentKey = ObjectKeys.Native(NewId(), NewId(), Sha256Digest.Compute(bytes));
        var plainKey = ObjectKeys.Rendition(NewId(), NewId(), NewId(), "p000001.words.json");

        foreach (var key in new[] { contentKey, plainKey })
        {
            var first = await Store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);
            var second = await Store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);

            first.Outcome.Should().Be(PutOutcome.Created);
            second.Outcome.Should().Be(PutOutcome.AlreadyExisted);
            second.Sha256.Should().Be(first.Sha256);
            second.KeyId.Should().Be(first.KeyId);
            (await ReadAllAsync(key)).Should().Equal(bytes);
        }
    }

    [Fact]
    public async Task Putting_different_content_to_an_existing_key_is_rejected_and_the_original_survives()
    {
        var original = Payload("original rendition");
        var key = ObjectKeys.Rendition(NewId(), NewId(), NewId(), "p000001.png");
        await Store.PutAsync(key, new MemoryStream(original), cancellationToken: Ct);

        var overwrite = () => Store.PutAsync(key, new MemoryStream(Payload("tampered rendition")), cancellationToken: Ct);

        (await overwrite.Should().ThrowAsync<ObjectAlreadyExistsException>()).Which.Key.Should().Be(key);
        (await ReadAllAsync(key)).Should().Equal(original);
    }

    [Fact]
    public async Task Content_addressed_key_rejects_bytes_with_another_hash_and_leaves_nothing_behind()
    {
        var key = ObjectKeys.Native(NewId(), NewId(), Sha256Digest.Compute(Payload("declared")));

        var put = () => Store.PutAsync(key, new MemoryStream(Payload("actual")), cancellationToken: Ct);

        await put.Should().ThrowAsync<ObjectIntegrityException>();
        (await Store.HeadAsync(key, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Expected_sha256_or_length_mismatch_is_rejected_and_leaves_nothing_behind()
    {
        var bytes = Payload("export file");
        var ws = NewId();
        var shaKey = ObjectKeys.ExportFile(ws, NewId(), NewId(), "VOL001/NATIVES/ABC0000001.pdf");
        var lengthKey = ObjectKeys.ExportFile(ws, NewId(), NewId(), "VOL001/NATIVES/ABC0000002.pdf");

        var badSha = () => Store.PutAsync(shaKey, new MemoryStream(bytes), new PutObjectOptions { ExpectedSha256 = Sha256Digest.Compute([1]) }, Ct);
        var badLength = () => Store.PutAsync(lengthKey, new MemoryStream(bytes), new PutObjectOptions { ExpectedLength = bytes.Length + 1 }, Ct);

        await badSha.Should().ThrowAsync<ObjectIntegrityException>();
        await badLength.Should().ThrowAsync<ObjectIntegrityException>();
        (await Store.HeadAsync(shaKey, Ct)).Should().BeNull();
        (await Store.HeadAsync(lengthKey, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Multipart_upload_with_a_hash_mismatch_leaves_nothing_behind()
    {
        var size = (PartSizeBytes * 2) + 17;
        var key = ObjectKeys.ImportSource(NewId(), NewId(), Sha256Digest.Compute(Payload("not these bytes")));

        var put = () => Store.PutAsync(key, new GeneratedStream(size, 42), cancellationToken: Ct);

        await put.Should().ThrowAsync<ObjectIntegrityException>();
        (await Store.HeadAsync(key, Ct)).Should().BeNull();
        (await ListAsync(ObjectPrefix.Workspace(key.WorkspaceId!.Value))).Should().BeEmpty();
    }

    [Fact]
    public async Task Verified_read_detects_bytes_changed_behind_the_store()
    {
        var bytes = Payload("authentic native");
        var key = ObjectKeys.Native(NewId(), NewId(), Sha256Digest.Compute(bytes));
        var put = await Store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);

        await using (var ok = await Store.OpenReadVerifiedAsync(key, put.Sha256, put.Length, Ct))
        {
            await ok.CopyToAsync(Stream.Null, Ct);
        }

        await CorruptAsync(key, Payload("forged native!!!"));

        var read = async () =>
        {
            await using var stream = await Store.OpenReadVerifiedAsync(key, put.Sha256, put.Length, Ct);
            await stream.CopyToAsync(Stream.Null, Ct);
        };
        await read.Should().ThrowAsync<ObjectIntegrityException>();
    }

    // ---- streaming ----

    [Fact]
    public async Task Large_objects_stream_through_without_full_buffering()
    {
        var size = (PartSizeBytes * 8) + (PartSizeBytes / 3);
        var expected = await Sha256Digest.ComputeAsync(new GeneratedStream(size, 7), Ct);
        var key = ObjectKeys.Native(NewId(), NewId(), expected);

        // Warm up code paths and pools so one-time allocations do not count.
        await Store.PutAsync(ObjectKeys.JobScratch(NewId(), NewId(), "warmup"), new GeneratedStream(PartSizeBytes + 1, 1), cancellationToken: Ct);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var result = await Store.PutAsync(key, new GeneratedStream(size, 7), cancellationToken: Ct);
        var putAllocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        result.Sha256.Should().Be(expected);
        result.Length.Should().Be(size);
        (await Store.HeadAsync(key, Ct))!.Length.Should().Be(size);

        before = GC.GetTotalAllocatedBytes(precise: true);
        await using (var stream = await Store.OpenReadVerifiedAsync(key, expected, size, Ct))
        {
            var buffer = new byte[64 * 1024];
            while (await stream.ReadAsync(buffer, Ct) > 0)
            {
            }
        }

        var readAllocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        putAllocated.Should().BeLessThan(size / 2, "puts buffer at most one part, never the whole object");
        readAllocated.Should().BeLessThan(size / 2, "reads stream, never buffer the whole object");
    }

    // ---- listing and prefix deletion ----

    [Fact]
    public async Task List_prefix_returns_only_keys_under_the_prefix_in_ordinal_order()
    {
        var ws = NewId();
        var other = NewId();
        var doc = NewId();
        var rendition = NewId();
        var keys = new[]
        {
            ObjectKeys.Rendition(ws, doc, rendition, "p000002.png"),
            ObjectKeys.Rendition(ws, doc, rendition, "p000001.png"),
            ObjectKeys.Native(ws, doc, Sha256Digest.Compute(Payload("n"))),
            ObjectKeys.JobScratch(ws, NewId(), "a/b/c.bin"),
            ObjectKeys.Rendition(other, doc, rendition, "p000001.png"),
        };
        foreach (var key in keys)
        {
            var content = key.ContentSha256 is null ? Payload(key.Value) : Payload("n");
            await Store.PutAsync(key, new MemoryStream(content), cancellationToken: Ct);
        }

        var workspace = await ListAsync(ObjectPrefix.Workspace(ws));
        var document = await ListAsync(ObjectPrefix.Document(ws, doc));

        workspace.Select(l => l.Key.Value).Should().Equal(keys[..4].Select(k => k.Value).Order(StringComparer.Ordinal));
        document.Select(l => l.Key).Should().HaveCount(3).And.NotContain(keys[3]);
        workspace.Single(l => l.Key == keys[0]).Length.Should().Be(Encoding.UTF8.GetByteCount(keys[0].Value));
        (await ListAsync(ObjectPrefix.Workspace(other))).Select(l => l.Key).Should().Equal(keys[4]);
    }

    [Fact]
    public async Task Delete_prefix_removes_exactly_the_prefix_reports_counts_and_is_idempotent()
    {
        var ws = NewId();
        var other = NewId();
        var doc = NewId();
        var job = NewId();
        var docKeys = Enumerable.Range(1, 3).Select(i => ObjectKeys.Rendition(ws, doc, NewId(), $"p{i:D6}.png")).ToArray();
        var scratch = ObjectKeys.JobScratch(ws, job, "part.bin");
        var survivor = ObjectKeys.Rendition(ws, NewId(), NewId(), "p000001.png");
        var otherWorkspace = ObjectKeys.Rendition(other, doc, NewId(), "p000001.png");
        foreach (var key in docKeys.Append(scratch).Append(survivor).Append(otherWorkspace))
        {
            await Store.PutAsync(key, new MemoryStream(new byte[10]), cancellationToken: Ct);
        }

        var documentResult = await Store.DeletePrefixAsync(ObjectPrefix.Document(ws, doc), Ct);
        var scratchResult = await Store.DeletePrefixAsync(ObjectPrefix.JobScratch(ws, job), Ct);
        var again = await Store.DeletePrefixAsync(ObjectPrefix.Document(ws, doc), Ct);

        documentResult.ObjectCount.Should().Be(3);
        documentResult.ByteCount.Should().Be(30);
        scratchResult.ObjectCount.Should().Be(1);
        again.ObjectCount.Should().Be(0);
        (await ListAsync(ObjectPrefix.Document(ws, doc))).Should().BeEmpty();
        (await ListAsync(ObjectPrefix.Workspace(ws))).Select(l => l.Key).Should().Equal(survivor);

        var workspaceResult = await Store.DeletePrefixAsync(ObjectPrefix.Workspace(ws), Ct);
        workspaceResult.ObjectCount.Should().Be(1);
        (await ListAsync(ObjectPrefix.Workspace(ws))).Should().BeEmpty();
        (await Store.HeadAsync(otherWorkspace, Ct)).Should().NotBeNull("deleting one workspace never touches another");
    }

    [Fact]
    public async Task Delete_prefix_rejects_shapes_outside_adr_011_section_7()
    {
        var ws = NewId();
        var key = ObjectKeys.Native(ws, NewId(), Sha256Digest.Compute(Payload("x")));
        await Store.PutAsync(key, new MemoryStream(Payload("x")), cancellationToken: Ct);

        foreach (var prefix in new[]
        {
            $"ws/{ws:N}/docs/{key.DocumentId:N}/native/",
            $"ws/{ws:N}/imports/{NewId():N}/",
            "sys/certificates/",
        })
        {
            var delete = () => Store.DeletePrefixAsync(ObjectPrefix.Parse(prefix), Ct);
            await delete.Should().ThrowAsync<ArgumentException>();
        }

        (await Store.HeadAsync(key, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Listing_and_deletion_page_through_more_than_one_listing_page()
    {
        var ws = NewId();
        var job = NewId();
        var keys = Enumerable.Range(0, 1005).Select(i => ObjectKeys.JobScratch(ws, job, $"chunk-{i:D5}")).ToArray();
        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct }, async (key, ct) =>
            await Store.PutAsync(key, new MemoryStream([1]), cancellationToken: ct));

        (await ListAsync(ObjectPrefix.JobScratch(ws, job))).Should().HaveCount(keys.Length);
        (await Store.DeletePrefixAsync(ObjectPrefix.JobScratch(ws, job), Ct)).ObjectCount.Should().Be(keys.Length);
        (await ListAsync(ObjectPrefix.JobScratch(ws, job))).Should().BeEmpty();
    }

    // ---- concurrency ----

    [Fact]
    public async Task Concurrent_identical_puts_all_succeed_and_store_one_object()
    {
        var bytes = Payload(new string('z', 4096));
        var key = ObjectKeys.Native(NewId(), NewId(), Sha256Digest.Compute(bytes));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct), Ct)));

        results.Should().OnlyContain(r => r.Sha256 == Sha256Digest.Compute(bytes));
        results.Should().Contain(r => r.Outcome == PutOutcome.Created);
        (await ReadAllAsync(key)).Should().Equal(bytes);
        (await ListAsync(ObjectPrefix.Workspace(key.WorkspaceId!.Value))).Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_conflicting_puts_let_exactly_one_writer_win()
    {
        var key = ObjectKeys.Rendition(NewId(), NewId(), NewId(), "p000001.png");
        var contents = Enumerable.Range(0, 8).Select(i => Payload($"writer {i} " + new string('x', 2048))).ToArray();

        var attempts = contents.Select(c => Task.Run(async () =>
        {
            try
            {
                await Store.PutAsync(key, new MemoryStream(c), cancellationToken: Ct);
                return c;
            }
            catch (ObjectAlreadyExistsException)
            {
                return null;
            }
        }, Ct)).ToArray();
        var outcomes = await Task.WhenAll(attempts);

        var winners = outcomes.Where(o => o is not null).ToArray();
        winners.Should().ContainSingle("objects are write-once even under concurrent writers");
        (await ReadAllAsync(key)).Should().Equal(winners[0]);
    }

    // ---- presigned URLs (ADR-011 §5, ADR-015 D12) ----

    [Fact]
    public async Task Presigned_get_serves_one_native_as_an_attachment_within_the_ttl_bounds()
    {
        var bytes = Payload("<html>native served as download</html>");
        var key = ObjectKeys.Native(NewId(), NewId(), Sha256Digest.Compute(bytes));
        await Store.PutAsync(key, new MemoryStream(bytes), new PutObjectOptions { ContentType = "text/html" }, Ct);
        if (Signer.DeliveryMode == ObjectDeliveryMode.Stream)
        {
            var presign = () => Signer.PresignGetAsync(key, new PresignGetOptions("ABC0000001.html"), Ct);
            await presign.Should().ThrowAsync<NotSupportedException>("the filesystem provider is always streamed");
            return;
        }

        var issuedAt = DateTimeOffset.UtcNow;
        var url = await Signer.PresignGetAsync(key, new PresignGetOptions("ABC0000001.html"), Ct);

        url.Method.Should().Be("GET");
        url.ExpiresAt.Should().BeCloseTo(issuedAt.AddSeconds(60), TimeSpan.FromSeconds(5), "ADR-015 sets a 60 s default");
        url.ToString().Should().NotContain(url.Url.Query, "presigned URLs are never logged");

        using var response = await Http.GetAsync(url.Url, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(bytes);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileNameStar.Should().Be("ABC0000001.html");

        using var put = await Http.PutAsync(url.Url, new ByteArrayContent([1]), Ct);
        put.IsSuccessStatusCode.Should().BeFalse("a GET signature never authorizes a write");
        (await ReadAllAsync(key)).Should().Equal(bytes);

        var otherKey = ObjectKeys.Native(key.WorkspaceId!.Value, NewId(), key.ContentSha256!.Value);
        using var other = await Http.GetAsync(new Uri(url.Url.ToString().Replace(key.DocumentId!.Value.ToString("N"), otherKey.DocumentId!.Value.ToString("N"), StringComparison.Ordinal)), Ct);
        other.IsSuccessStatusCode.Should().BeFalse("a signature covers exactly one object");
    }

    [Fact]
    public async Task Presigned_get_rejects_out_of_bounds_ttls_and_non_download_areas()
    {
        var native = ObjectKeys.Native(NewId(), NewId(), Sha256Digest.Compute([1]));
        var rendition = ObjectKeys.Rendition(NewId(), NewId(), NewId(), "p000001.png");
        if (Signer.DeliveryMode == ObjectDeliveryMode.Stream)
        {
            return;
        }

        var tooLong = () => Signer.PresignGetAsync(native, new PresignGetOptions("a.pdf", TimeSpan.FromSeconds(301)), Ct);
        var tooShort = () => Signer.PresignGetAsync(native, new PresignGetOptions("a.pdf", TimeSpan.FromSeconds(29)), Ct);
        var viewerImage = () => Signer.PresignGetAsync(rendition, new PresignGetOptions("p.png"), Ct);
        var maximum = await Signer.PresignGetAsync(native, new PresignGetOptions("a.pdf", TimeSpan.FromSeconds(300)), Ct);

        await tooLong.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await tooShort.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await viewerImage.Should().ThrowAsync<ArgumentException>("viewer renditions are streamed, never presigned (ADR-015 D12.2)");
        maximum.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(300), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Presigned_put_is_limited_to_upload_staging_and_creates_the_object()
    {
        var ws = NewId();
        var upload = ObjectKeys.ImportUpload(ws, NewId(), NewId());
        var native = ObjectKeys.Native(ws, NewId(), Sha256Digest.Compute([1]));
        var bytes = Payload("volume.zip contents");
        if (Signer.DeliveryMode == ObjectDeliveryMode.Stream)
        {
            var presign = () => Signer.PresignPutAsync(upload, new PresignPutOptions(bytes.Length, "application/zip"), Ct);
            await presign.Should().ThrowAsync<NotSupportedException>();
            return;
        }

        var wrongArea = () => Signer.PresignPutAsync(native, new PresignPutOptions(1, "application/octet-stream"), Ct);
        var tooLong = () => Signer.PresignPutAsync(upload, new PresignPutOptions(bytes.Length, "application/zip", TimeSpan.FromSeconds(901)), Ct);
        await wrongArea.Should().ThrowAsync<ArgumentException>();
        await tooLong.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var url = await Signer.PresignPutAsync(upload, new PresignPutOptions(bytes.Length, "application/zip"), Ct);
        using var request = new HttpRequestMessage(HttpMethod.Put, url.Url) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/zip");
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        using var response = await Http.SendAsync(request, Ct);

        response.IsSuccessStatusCode.Should().BeTrue();
        (await ReadAllAsync(upload)).Should().Equal(bytes);
    }

    // ---- helpers ----

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && _store is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    protected static Guid NewId() => Guid.NewGuid();

    protected static byte[] Payload(string text) => Encoding.UTF8.GetBytes(text);

    protected static string NewInstallationPrefix() => "c" + Guid.NewGuid().ToString("N")[..12];

    private async Task<byte[]> ReadAllAsync(ObjectKey key, ByteRange? range = null)
    {
        await using var stream = await Store.OpenReadAsync(key, range, Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    private async Task<List<ObjectListing>> ListAsync(ObjectPrefix prefix)
    {
        var result = new List<ObjectListing>();
        await foreach (var item in Store.ListPrefixAsync(prefix, Ct))
        {
            result.Add(item);
        }

        return result;
    }
}
