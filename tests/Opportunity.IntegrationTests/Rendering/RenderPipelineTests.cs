using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Audit;
using Opportunity.Application.Content;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Storage;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Import;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.Pages;
using Opportunity.Data.Documents;
using Opportunity.Data.Rendering;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs;
using Opportunity.Rendering.Jobs;
using Opportunity.Rendering.Renderers;
using Opportunity.Rendering.Sandboxing;

using PageImageFormat = Opportunity.Core.Pages.PageImageFormat;

namespace Opportunity.IntegrationTests.Rendering;

/// <summary>
/// E11-T02 against PostgreSQL (app login, RLS on) and a file-system object store: a hand-made volume (synthetic G4
/// TIFF, JPEG and PDF files, removed afterwards) is imported, the render coordinator creates and plans the render job
/// and the real chunk consumer runs <see cref="RenderChunkExecutor"/>, without a broker.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class RenderPipelineTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_import_gets_review_images_and_thumbnails_for_its_TIFF_pages_and_a_Rendered_set_for_its_PDF()
    {
        using var share = new Share();
        share.Write(@"IMAGES\T-001.tif", PageImages.Tiff(["T-001 p1", "T-001 p2", "T-001 p3"], width: 850, height: 1100, dpi: 100));
        share.Write(@"IMAGES\J-001.jpg", PageImages.Jpeg("J-001"));
        share.Write(@"NATIVES\P-001.pdf", SyntheticPdf.Create(4, label: "P-001"));
        await using var h = await ImportHarness.CreateAsync(postgres, volumeRoot: share.Root);
        var ws = await h.WorkspaceAsync();
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(
            ["BEGDOC", "NATIVELINK"],
            ["T-001", ""],
            ["J-001", ""],
            ["P-001", @"NATIVES\P-001.pdf"]));
        var opt = Encoding.ASCII.GetBytes("T-001,VOL001,IMAGES\\T-001.tif,Y,,,3\r\nJ-001,VOL001,IMAGES\\J-001.jpg,Y,,,1\r\n");
        var batch = await h.StartAsync(ws, dat, Share.Profile(), opt: opt);
        (await h.RunAsync(batch)).Status.Should().BeOneOf(JobStatus.Completed, JobStatus.CompletedWithErrors);
        var documents = await h.DocumentsAsync(ws);

        var r = new RenderHarness(h);
        (await r.Coordinator().RunOnceAsync(ws, Ct)).Should().Be(2, "the job is created for the finished import, then planned");
        var job = await r.RenderJobAsync(ws);
        job.Status.Should().Be(JobStatus.Running);
        job.Counters.ChunksTotal.Should().Be(1);
        await r.DeliverOpenChunksAsync(ws, job.JobId);

        job = (await h.Jobs.GetAsync(ws, job.JobId, Ct))!;
        var items = await h.Jobs.GetItemResultsAsync(new JobItemResultQuery(ws, job.JobId), Ct);
        var chunkErrors = await h.Jobs.GetChunksAsync(ws, job.JobId, cancellationToken: Ct);
        job.Status.Should().Be(JobStatus.Completed, string.Join(";", items.Select(i => i.Result.ReasonCode + ":" + i.Result.Detail)) + string.Join(";", chunkErrors.Select(c => c.LastError)));
        job.Counters.ItemsApplied.Should().Be(3);

        // The multi-page TIFF: one review PNG and one thumbnail per Page row of the Imported set, frame by frame.
        var tiff = await r.RastersAsync(ws, documents["T-001"].DocumentId);
        tiff.Source.Should().Be(PageSetSource.Imported);
        tiff.Pages.Should().Equal(
            (1, PageImagePurpose.Review, 850, 1100, PageImageFormat.Png), (1, PageImagePurpose.Thumbnail, 155, 200, PageImageFormat.Png),
            (2, PageImagePurpose.Review, 850, 1100, PageImageFormat.Png), (2, PageImagePurpose.Thumbnail, 155, 200, PageImageFormat.Png),
            (3, PageImagePurpose.Review, 850, 1100, PageImageFormat.Png), (3, PageImagePurpose.Thumbnail, 155, 200, PageImageFormat.Png));

        // A JPEG page is displayable as it is: only a thumbnail.
        (await r.RastersAsync(ws, documents["J-001"].DocumentId)).Pages.Should().Equal((1, PageImagePurpose.Thumbnail, 155, 200, PageImageFormat.Png));

        // The PDF native without page images: a Ready Rendered set, now active, with page geometry for redactions.
        var pdf = await r.RastersAsync(ws, documents["P-001"].DocumentId);
        pdf.Source.Should().Be(PageSetSource.Rendered);
        pdf.Status.Should().Be(PageSetStatus.Ready);
        pdf.Geometry.Should().Equal(Enumerable.Range(1, 4).Select(i => (i, 612m, 792m)));
        pdf.Pages.Should().HaveCount(8).And.Contain((4, PageImagePurpose.Review, 1275, 1650, PageImageFormat.Png));

        // The viewer's page-image route now finds a PNG for TIFF page 2 and for PDF page 3.
        var catalog = new DocumentContentCatalog(h.Db.AppDataSource);
        (await catalog.FindAsync(ws, documents["T-001"].DocumentId, ContentRendition.PageImage, 2, Ct))!.Location!.ContentType.Should().Be("image/png");
        (await catalog.FindAsync(ws, documents["P-001"].DocumentId, ContentRendition.Thumbnail, 3, Ct))!.Location!.ContentType.Should().Be("image/png");

        // Nothing more to do: no second job for the import, and a replayed delivery changes nothing.
        var objects = await r.RenditionObjectsAsync(ws);
        objects.Should().Be(6 + 1 + 8);
        (await r.Coordinator().RunOnceAsync(ws, Ct)).Should().Be(0);
        (await h.CountAsync("SELECT count(*) FROM opportunity.job WHERE workspace_id = @ws AND job_type = 'Render'", ws)).Should().Be(1);
        (await r.RenditionObjectsAsync(ws)).Should().Be(objects);
    }

    [Fact]
    public async Task A_500_page_PDF_renders_idempotently_when_its_chunk_is_retried_after_a_storage_failure()
    {
        using var share = new Share();
        share.Write(@"NATIVES\BIG.pdf", SyntheticPdf.Create(500, widthPt: 144, heightPt: 180, label: "BIG"));
        await using var h = await ImportHarness.CreateAsync(postgres, volumeRoot: share.Root);
        var ws = await h.WorkspaceAsync();
        var batch = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "NATIVELINK"], ["BIG-001", @"NATIVES\BIG.pdf"])), Share.Profile());
        (await h.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
        var documentId = (await h.DocumentsAsync(ws))["BIG-001"].DocumentId;

        var failing = new FailingPutStore(h.Store) { FailAfterPuts = 600 };
        var r = new RenderHarness(h, failing);
        await r.Coordinator().RunOnceAsync(ws, Ct);
        var job = await r.RenderJobAsync(ws);
        await r.DeliverOpenChunksAsync(ws, job.JobId);

        var chunk = (await h.Jobs.GetChunksAsync(ws, job.JobId, cancellationToken: Ct)).Single();
        chunk.Status.Should().Be(JobChunkStatus.RetryWait, "the storage failure is transient " + chunk.LastError);
        (await h.CountAsync("SELECT count(*) FROM opportunity.page_set WHERE workspace_id = @ws AND source = 2", ws)).Should().Be(0);
        (await r.StoredUnderRenditionsAsync(ws, documentId)).Should().Be(600, "the failed attempt left its objects in storage");

        // The retry re-puts the same keys (identical bytes) and commits every page once.
        await h.Db.ExecuteAsync("UPDATE opportunity.job_chunk SET available_at = now() - interval '1 minute' WHERE workspace_id = @ws", ("ws", ws));
        await r.DeliverOpenChunksAsync(ws, job.JobId);
        (await h.Jobs.GetAsync(ws, job.JobId, Ct))!.Status.Should().Be(JobStatus.Completed);

        var rendered = await r.RastersAsync(ws, documentId);
        rendered.Source.Should().Be(PageSetSource.Rendered);
        rendered.Status.Should().Be(PageSetStatus.Ready);
        rendered.PageSetId.Should().Be(RenderIds.NativePageSet(documentId, await r.NativeShaAsync(ws, documentId), r.Renderer.Identity));
        rendered.Geometry.Should().HaveCount(500).And.OnlyContain(g => g.WidthPt == 144m && g.HeightPt == 180m);
        rendered.Pages.Should().HaveCount(1_000);
        rendered.Pages.Where(p => p.Purpose == PageImagePurpose.Review).Should().OnlyContain(p => p.Width == 300 && p.Height == 375);
        (await r.RenditionObjectsAsync(ws)).Should().Be(1_000);
        (await r.StoredUnderRenditionsAsync(ws, documentId)).Should().Be(1_000, "the retry wrote no extra objects");
    }

    [Fact]
    public async Task A_document_over_a_sandbox_limit_is_killed_alone_and_fails_at_the_chunk_attempt_ceiling()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The render sandbox is Linux-only.");
        using var share = new Share();
        share.Write(@"NATIVES\OK.pdf", SyntheticPdf.Create(2, label: "OK"));
        share.Write(@"NATIVES\BOMB.png", PageImages.Png("bomb", 7_000, 7_000, 300));
        await using var h = await ImportHarness.CreateAsync(postgres, volumeRoot: share.Root);
        var ws = await h.WorkspaceAsync();
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "NATIVELINK"], ["OK-001", @"NATIVES\OK.pdf"], ["BOMB-001", @"NATIVES\BOMB.png"]));
        (await h.RunAsync(await h.StartAsync(ws, dat, Share.Profile()))).Status.Should().Be(JobStatus.Completed);
        var documents = await h.DocumentsAsync(ws);

        // The decoded 49-megapixel frame needs far more than the 160 MiB this sandbox may use.
        var sandbox = new SandboxedRenderer(null, new RenderSandboxOptions { MemoryBytes = 160L << 20, ProtectWorkerWithoutLandlock = false },
            NullLogger<SandboxedRenderer>.Instance);
        var r = new RenderHarness(h, renderer: sandbox);
        await r.Coordinator().RunOnceAsync(ws, Ct);
        var job = await r.RenderJobAsync(ws);
        await h.Db.ExecuteAsync("UPDATE opportunity.job_chunk SET max_attempts = 2 WHERE workspace_id = @ws", ("ws", ws));

        // Attempt 1: the bomb's process is killed, the worker carries on, and the chunk is retried (it may be a busy host).
        await r.DeliverOpenChunksAsync(ws, job.JobId);
        var chunk = (await h.Jobs.GetChunksAsync(ws, job.JobId, cancellationToken: Ct)).Single();
        chunk.Status.Should().Be(JobChunkStatus.RetryWait, chunk.LastError);
        chunk.LastError.Should().Contain(RenderErrorCodes.MemoryLimit);
        (await h.CountAsync("SELECT count(*) FROM opportunity.page_set WHERE workspace_id = @ws AND source = 2", ws)).Should().Be(0);

        // Attempt 2 is the ceiling: the bomb fails as a document; the other document of the chunk renders and commits.
        await h.Db.ExecuteAsync("UPDATE opportunity.job_chunk SET available_at = now() - interval '1 minute' WHERE workspace_id = @ws", ("ws", ws));
        await r.DeliverOpenChunksAsync(ws, job.JobId);
        job = (await h.Jobs.GetAsync(ws, job.JobId, Ct))!;
        job.Status.Should().Be(JobStatus.CompletedWithErrors);
        var items = await h.Jobs.GetItemResultsAsync(new JobItemResultQuery(ws, job.JobId), Ct);
        items.Should().ContainSingle().Which.Result.Should().BeEquivalentTo(new { DocumentId = documents["BOMB-001"].DocumentId, ReasonCode = RenderErrorCodes.MemoryLimit });
        var ok = await r.RastersAsync(ws, documents["OK-001"].DocumentId);
        ok.Status.Should().Be(PageSetStatus.Ready);
        ok.Pages.Should().HaveCount(4);
        (await h.CountAsync("SELECT count(*) FROM opportunity.page_set WHERE workspace_id = @ws AND source = 2 AND status = 3", ws)).Should().Be(1, "the bomb has a Failed set");
    }

    [Fact]
    public async Task The_render_sandbox_cannot_reach_PostgreSQL_or_the_internet()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The render sandbox is Linux-only.");
        var pg = new Npgsql.NpgsqlConnectionStringBuilder(postgres.AdminConnectionString);
        var host = System.Net.IPAddress.TryParse(pg.Host, out _) ? pg.Host! : (await System.Net.Dns.GetHostAddressesAsync(pg.Host!, Ct))[0].ToString();
        var pgEndpoint = $"{host}:{pg.Port}";
        using (var reachable = new System.Net.Sockets.TcpClient())
        {
            await reachable.ConnectAsync(host, pg.Port, Ct);
        }

        var work = Path.Combine(Path.GetTempPath(), "opp-sandbox-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sandbox = new SandboxedRenderer(null, new RenderSandboxOptions { ProtectWorkerWithoutLandlock = false }, NullLogger<SandboxedRenderer>.Instance);

            var report = await sandbox.ProbeAsync(work, [pgEndpoint, "1.1.1.1:443", "example.com:80"], Ct);

            report.ConnectedEndpoints.Should().BeEmpty("the worker reaches PostgreSQL at {0}, its render process does not", pgEndpoint);
            report.EndpointErrors.Should().HaveCount(3);
            report.OpenedUdpSocket.Should().BeFalse();
            report.Controls.Seccomp.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>The render side over an import harness's database and object store.</summary>
    private sealed class RenderHarness(ImportHarness import, IObjectStore? store = null, IRenderer? renderer = null)
    {
        public IRenderer Renderer { get; } = renderer ?? new RasterRenderer();

        public RenderRepository Store { get; } = new(import.Db.AppDataSource);

        public RenderJobOptions Options { get; } = new() { PagesPerHeartbeat = 100 };

        public RenderCoordinator Coordinator() => new(Store, import.Jobs, Renderer, Options, NullLogger<RenderCoordinator>.Instance);

        public JobChunkConsumer Consumer() => new(
            import.Chunks,
            [new RenderChunkExecutor(Store, store ?? import.Store, Renderer, Options, NullLogger<RenderChunkExecutor>.Instance)],
            new InMemoryAuditEventWriter(), new JobLeaseOptions(), new JobChunkConsumerOptions { WorkerId = "render-test-worker" },
            NullMessageProcessingMeter.Instance, TimeProvider.System, NullLogger<JobChunkConsumer>.Instance);

        public async Task<JobInfo> RenderJobAsync(Guid ws)
        {
            var id = await import.Db.ScalarAsync<Guid>("SELECT job_id FROM opportunity.job WHERE workspace_id = @ws AND job_type = 'Render'", ("ws", ws));
            return (await import.Jobs.GetAsync(ws, id, Ct))!;
        }

        public async Task DeliverOpenChunksAsync(Guid ws, Guid jobId)
        {
            foreach (var chunk in await import.Jobs.GetChunksAsync(ws, jobId, cancellationToken: Ct))
            {
                if (chunk.Status is JobChunkStatus.Pending or JobChunkStatus.Dispatched or JobChunkStatus.RetryWait)
                {
                    var payload = new JobChunkMessage { ChunkId = chunk.ChunkId, Sequence = chunk.Sequence, Operation = JobChunkOperation.RenderChunk };
                    var received = new ReceivedMessage(
                        new MessageEnvelope
                        {
                            MessageId = Guid.CreateVersion7(),
                            MessageType = MessageTypes.JobChunk,
                            SchemaVersion = new SchemaVersion(1, 0),
                            WorkspaceId = chunk.WorkspaceId,
                            JobId = chunk.JobId,
                            CorrelationId = $"corr-{chunk.JobId:N}",
                            IdempotencyKey = chunk.IdempotencyKey,
                            CreatedAt = DateTimeOffset.UtcNow,
                            Attempt = chunk.AttemptCount,
                            Payload = JsonSerializer.SerializeToElement(payload, MessageJson.PayloadOptions),
                        },
                        payload, WorkQueues.Rendering, Redelivered: false, DeliveryCount: 0, TransportRetry: 0);
                    await Consumer().HandleAsync(payload, received, Ct);
                }
            }
        }

        /// <summary>The active page set of a document with its derived rasters (ordinal, purpose, size, format) and page geometry.</summary>
        public async Task<ActiveSet> RastersAsync(Guid ws, Guid documentId)
        {
            await using var connection = await import.Db.AppDataSource.OpenConnectionAsync(Ct);
            await using var tx = await connection.BeginTransactionAsync(Ct);
            await using (var context = new Npgsql.NpgsqlCommand("SELECT set_config('app.workspace_id', @ws, true)", connection, tx))
            {
                context.Parameters.AddWithValue("ws", ws.ToString());
                await context.ExecuteNonQueryAsync(Ct);
            }

            Guid setId;
            PageSetSource source;
            PageSetStatus status;
            await using (var command = new Npgsql.NpgsqlCommand(
                """
                SELECT ps.page_set_id, ps.source, ps.status FROM opportunity.document d
                JOIN opportunity.page_set ps ON ps.workspace_id = d.workspace_id AND ps.page_set_id = d.active_page_set_id
                WHERE d.workspace_id = @ws AND d.document_id = @doc
                """, connection, tx))
            {
                command.Parameters.AddWithValue("ws", ws);
                command.Parameters.AddWithValue("doc", documentId);
                await using var reader = await command.ExecuteReaderAsync(Ct);
                (await reader.ReadAsync(Ct)).Should().BeTrue();
                (setId, source, status) = (reader.GetGuid(0), (PageSetSource)reader.GetInt16(1), (PageSetStatus)reader.GetInt16(2));
            }

            var pages = new List<(int, PageImagePurpose, int, int, PageImageFormat)>();
            await using (var command = new Npgsql.NpgsqlCommand(
                """
                SELECT ordinal, purpose, width_px, height_px, format FROM opportunity.page_image
                WHERE workspace_id = @ws AND page_set_id = @ps AND purpose IN (2, 4) ORDER BY ordinal, purpose
                """, connection, tx))
            {
                command.Parameters.AddWithValue("ws", ws);
                command.Parameters.AddWithValue("ps", setId);
                await using var reader = await command.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                {
                    pages.Add((reader.GetInt32(0), (PageImagePurpose)reader.GetInt16(1), reader.GetInt32(2), reader.GetInt32(3), (PageImageFormat)reader.GetInt16(4)));
                }
            }

            var geometry = new List<(int, decimal, decimal)>();
            await using (var command = new Npgsql.NpgsqlCommand(
                "SELECT ordinal, width_pt, height_pt FROM opportunity.page WHERE workspace_id = @ws AND page_set_id = @ps ORDER BY ordinal", connection, tx))
            {
                command.Parameters.AddWithValue("ws", ws);
                command.Parameters.AddWithValue("ps", setId);
                await using var reader = await command.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                {
                    geometry.Add((reader.GetInt32(0), reader.GetDecimal(1), reader.GetDecimal(2)));
                }
            }

            await tx.CommitAsync(Ct);
            return new ActiveSet(setId, source, status, pages, geometry);
        }

        public Task<long> RenditionObjectsAsync(Guid ws) =>
            import.CountAsync("SELECT count(*) FROM opportunity.stored_object WHERE workspace_id = @ws AND area = 4", ws);

        public async Task<int> StoredUnderRenditionsAsync(Guid ws, Guid documentId)
        {
            var count = 0;
            await foreach (var _ in import.Store.ListPrefixAsync(ObjectPrefix.Parse($"ws/{ws:N}/docs/{documentId:N}/rend/"), Ct))
            {
                count++;
            }

            return count;
        }

        public async Task<byte[]> NativeShaAsync(Guid ws, Guid documentId) => await import.Db.ScalarAsync<byte[]>(
            """
            SELECT o.sha256 FROM opportunity.document d
            JOIN opportunity.stored_object o ON o.workspace_id = d.workspace_id AND o.object_id = d.native_object_id
            WHERE d.workspace_id = @ws AND d.document_id = @doc
            """, ("ws", ws), ("doc", documentId));
    }

    private sealed record ActiveSet(
        Guid PageSetId,
        PageSetSource Source,
        PageSetStatus Status,
        List<(int Ordinal, PageImagePurpose Purpose, int Width, int Height, PageImageFormat Format)> Pages,
        List<(int Ordinal, decimal WidthPt, decimal HeightPt)> Geometry);

    /// <summary>Puts fail once (an outage) after <see cref="FailAfterPuts"/> successful ones.</summary>
    private sealed class FailingPutStore(IObjectStore inner) : IObjectStore
    {
        private int _puts;

        public int FailAfterPuts { get; set; } = int.MaxValue;

        public Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _puts) == FailAfterPuts + 1)
            {
                throw new IOException("Simulated object storage outage.");
            }

            return inner.PutAsync(key, content, options, cancellationToken);
        }

        public Task<Stream> OpenReadAsync(ObjectKey key, ByteRange? range = null, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default) => inner.HeadAsync(key, cancellationToken);

        public IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
            inner.ListPrefixAsync(prefix, cancellationToken);

        public Task<DeletePrefixResult> DeletePrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
            inner.DeletePrefixAsync(prefix, cancellationToken);
    }

    /// <summary>An import share with volume VOL001 in a temp directory removed on dispose.</summary>
    private sealed class Share : IDisposable
    {
        public Share()
        {
            Root = Directory.CreateTempSubdirectory("opp-render-share-").FullName;
            Directory.CreateDirectory(Path.Combine(Root, "VOL001", "IMAGES"));
            Directory.CreateDirectory(Path.Combine(Root, "VOL001", "NATIVES"));
        }

        public string Root { get; }

        public static ImportProfileDefinition Profile() => new() { Paths = new PathSettings { VolumeRoot = "VOL001" } };

        public void Write(string relativePath, byte[] content) =>
            File.WriteAllBytes(Path.Combine(Root, "VOL001", relativePath.Replace('\\', Path.DirectorySeparatorChar)), content);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
