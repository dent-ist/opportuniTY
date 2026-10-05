using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Coding;
using Opportunity.Application.Import;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Storage;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Import;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Data.Import;
using Opportunity.Data.Jobs;
using Opportunity.Data.Workspaces;
using Opportunity.Import.Jobs;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;
using Opportunity.Import.Volumes;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif
using Opportunity.Storage;
using Opportunity.Storage.FileSystem;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// The import pipeline without a broker: a migrated database used through the RLS-bound app login, a file-system object
/// store in a temp directory, the preparer and the real chunk consumer with <see cref="ImportChunkExecutor"/>, fed with
/// deliveries the way the transport hands them over.
/// </summary>
internal sealed class ImportHarness : IAsyncDisposable
{
    public const char Dc4 = '\u0014';

    private readonly bool _ownsDatabase;

    private ImportHarness(CoreSchemaDatabase db, string storeRoot, ImportJobOptions options, bool ownsDatabase = true, IRestrictionClassBinding? restrictions = null)
    {
        _ownsDatabase = ownsDatabase;
        Db = db;
        StoreRoot = storeRoot;
        Options = options;
        Store = new FileSystemObjectStore(new FileSystemObjectStoreOptions { RootPath = storeRoot });
        Batches = new ImportBatchRepository(db.AppDataSource, restrictions);
        Jobs = new JobRepository(db.AppDataSource);
        Chunks = new JobChunkRepository(db.AppDataSource);
        Workspaces = new WorkspaceReader(db.AppDataSource);
    }

    public CoreSchemaDatabase Db { get; }

    public string StoreRoot { get; }

    public ImportJobOptions Options { get; }

    public IObjectStore Store { get; set; }

    public ImportBatchRepository Batches { get; }

    public JobRepository Jobs { get; }

    public JobChunkRepository Chunks { get; }

    public WorkspaceReader Workspaces { get; }

    public InMemoryAuditEventWriter RejectionAudit { get; } = new();

    /// <summary>The import worker's volume share: where native and text (E08-T04) and OPT image (E08-T05) paths resolve.</summary>
    public ImportVolumeOptions Volumes { get; set; } = new();

    public static readonly Guid User = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ImportHarness> CreateAsync(MigrationPostgresFixture postgres, int rowsPerChunk = 500, string? volumeRoot = null, int? textCap = null)
    {
        var db = await CoreSchemaDatabase.CreateAsync(postgres);
        return Over(db, rowsPerChunk, volumeRoot, textCap, ownsDatabase: true);
    }

    /// <summary>The pipeline over a database another harness owns (and disposes).</summary>
    public static ImportHarness Over(
        CoreSchemaDatabase db, int rowsPerChunk = 500, string? volumeRoot = null, int? textCap = null, bool ownsDatabase = false,
        IRestrictionClassBinding? restrictions = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "opp-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new ImportJobOptions
        {
            RowsPerChunk = rowsPerChunk,
            KeyBatchSize = 7,
            IndexedTextCap = textCap ?? new ImportJobOptions().IndexedTextCap,
        };
        return new ImportHarness(db, root, options, ownsDatabase, restrictions) { Volumes = new ImportVolumeOptions { VolumeShareRoot = volumeRoot } };
    }

    public async Task<Guid> WorkspaceAsync(bool caseSensitive = false)
    {
        var ws = await Db.CreateWorkspaceAsync(caseSensitive);
        await Db.Fields.InitializeWorkspaceAsync(ws, Ct);
        return ws;
    }

    public ImportJobPreparer Preparer() =>
        new(Batches, Jobs, Db.Fields, Workspaces, Store, Options, NullLogger<ImportJobPreparer>.Instance, Volumes);

    public ImportChunkExecutor Executor() => new(Batches, Db.Fields, Workspaces, Store, Options, Volumes, Jobs);

    /// <summary>A Concordance DAT (þ qualifier, DC4 separator, CRLF rows).</summary>
    public static string Dat(params string[][] rows) =>
        string.Concat(rows.Select(r => string.Join(Dc4, r.Select(v => "þ" + v + "þ")) + "\r\n"));

    public static byte[] Utf8Bom(string text) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)];

    /// <summary>What the API does on start: freeze the auto-mapped profile, upload the DAT, create batch and job.</summary>
    public async Task<ImportBatchRecord> StartAsync(
        Guid ws, byte[] dat, ImportProfileDefinition? profile = null, ImportMode mode = ImportMode.Append,
        IReadOnlyList<int>? codingFields = null, string name = "volume.dat", bool? mayCreateFields = null, byte[]? opt = null)
    {
        codingFields ??= [];
        profile = (profile ?? new ImportProfileDefinition()) with { Mode = mode };
        profile = profile with { Overlay = profile.Overlay with { AllowCodingFields = codingFields.Count > 0 } };
        var catalog = await Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        var issues = new List<MappingIssue>();
        var options = ImportSource.ReaderOptions(profile, null, issues);
        CompiledMapping mapping;
        await using (var reader = await DatReader.OpenAsync(new MemoryStream(dat), options, cancellationToken: Ct))
        {
            mapping = MappingCompiler.Compile(profile, reader.Header.Names, catalog, new MappingOptions { MultiValueDelimiter = options.Profile.MultiValue });
        }

        if (mapping.HasErrors)
        {
            throw new InvalidOperationException(string.Join("; ", mapping.Issues.Select(i => i.Message)));
        }

        var id = Guid.CreateVersion7();
        var sha = SHA256.HashData(dat);
        var key = ObjectKeys.ImportSource(ws, id, Sha256Digest.FromBytes(sha));
        await Store.PutAsync(key, new MemoryStream(dat), cancellationToken: Ct);
        var optSource = opt is null ? null : await PutOptAsync(ws, id, opt);
        var creation = await Batches.CreateAsync(new NewImportBatch
        {
            WorkspaceId = ws,
            ImportBatchId = id,
            Name = name + " import",
            Mode = mode,
            SourceFileName = name,
            SourceObjectKey = key.Value,
            SourceSha256 = sha,
            SourceSize = dat.Length,
            Opt = optSource,
            ProfileJson = ImportProfileRules.Serialize(mapping.EffectiveProfile),
            CodingOverlayFieldIds = codingFields,
            // Like the API: granted exactly when the start-time mapping creates fields or choices (Workspace.ManageFields).
            MayCreateFields = mayCreateFields
                ?? (mapping.Targets.Any(t => t.CreatesField is not null) || mapping.Columns.Any(c => c.Parsing?.CreateMissingChoices == true)),
            InitiatedBy = User,
            AuditTemplate = StartedAudit(),
        }, Ct);
        return creation.Batch;
    }

    /// <summary>What the API does for an OPT without a DAT: an overlay of the pages of existing documents.</summary>
    public async Task<ImportBatchRecord> StartImagesOnlyAsync(Guid ws, byte[] opt, ImportProfileDefinition? profile = null, string name = "images.opt")
    {
        var id = Guid.CreateVersion7();
        var source = await PutOptAsync(ws, id, opt, name);
        var creation = await Batches.CreateAsync(new NewImportBatch
        {
            WorkspaceId = ws,
            ImportBatchId = id,
            Name = name + " import",
            Mode = ImportMode.Overlay,
            SourceFileName = name,
            SourceObjectKey = source.ObjectKey,
            SourceSha256 = source.Sha256,
            SourceSize = source.Size,
            Opt = source,
            ImagesOnly = true,
            ProfileJson = ImportProfileRules.Serialize((profile ?? new ImportProfileDefinition()) with { Mode = ImportMode.Overlay }),
            InitiatedBy = User,
            AuditTemplate = StartedAudit(),
        }, Ct);
        return creation.Batch;
    }

    private async Task<ImportOptSource> PutOptAsync(Guid ws, Guid importId, byte[] opt, string name = "volume.opt")
    {
        var sha = SHA256.HashData(opt);
        var key = ObjectKeys.ImportSource(ws, importId, Sha256Digest.FromBytes(sha));
        await Store.PutAsync(key, new MemoryStream(opt), cancellationToken: Ct);
        return new ImportOptSource(name, key.Value, sha, opt.Length);
    }

    private static AuditEvent StartedAudit() => new()
    {
        OccurredAt = DateTimeOffset.UtcNow,
        Category = AuditTaxonomy.Import.Category,
        Action = AuditTaxonomy.Import.Started,
        ActorType = AuditActorType.User,
        ActorId = User.ToString(),
        ActorDisplay = "Import Tester",
        Outcome = AuditOutcome.Success,
    };

    public Task<ImportPreparationOutcome> PrepareAsync(ImportBatchRecord batch) => Preparer().PrepareAsync(batch.WorkspaceId, batch.ImportBatchId, Ct);

    /// <summary>Prepares and then delivers every open chunk once, in order.</summary>
    public async Task<JobInfo> RunAsync(ImportBatchRecord batch)
    {
        await PrepareAsync(batch);
        await DeliverOpenChunksAsync(batch);
        return (await Jobs.GetAsync(batch.WorkspaceId, batch.JobId, Ct))!;
    }

    public async Task<int> DeliverOpenChunksAsync(ImportBatchRecord batch, JobChunkConsumer? consumer = null)
    {
        consumer ??= Consumer();
        var delivered = 0;
        foreach (var chunk in await Jobs.GetChunksAsync(batch.WorkspaceId, batch.JobId, cancellationToken: Ct))
        {
            if (chunk.Status is JobChunkStatus.Pending or JobChunkStatus.Dispatched or JobChunkStatus.RetryWait)
            {
                await consumer.HandleAsync(Payload(chunk), Received(chunk), Ct);
                delivered++;
            }
        }

        return delivered;
    }

    public JobChunkConsumer Consumer(
#if OPPORTUNITY_FAILPOINTS
        IFaultInjector? faults = null,
#endif
        JobLeaseOptions? lease = null) => new(
            Chunks, [Executor()], RejectionAudit, lease ?? new JobLeaseOptions(), new JobChunkConsumerOptions { WorkerId = "import-test-worker" },
            NullMessageProcessingMeter.Instance, TimeProvider.System, NullLogger<JobChunkConsumer>.Instance
#if OPPORTUNITY_FAILPOINTS
            , metrics: null, faults
#endif
            );

    public static JobChunkMessage Payload(JobChunkInfo chunk) =>
        new() { ChunkId = chunk.ChunkId, Sequence = chunk.Sequence, Operation = JobChunkOperation.ImportChunk };

    public static ReceivedMessage Received(JobChunkInfo chunk, bool redelivered = false) => new(
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
            Payload = JsonSerializer.SerializeToElement(Payload(chunk), MessageJson.PayloadOptions),
        },
        Payload(chunk), WorkQueues.Import, redelivered, DeliveryCount: 0, TransportRetry: 0);

    public Task<ImportBatchRecord> BatchAsync(ImportBatchRecord batch) => Batches.GetAsync(batch.WorkspaceId, batch.ImportBatchId, Ct)!;

    public async Task<List<ImportRowIssueRecord>> IssuesAsync(ImportBatchRecord batch, ImportIssueSeverity? severity = ImportIssueSeverity.Error) =>
        [.. await Batches.GetRowIssuesAsync(batch.WorkspaceId, batch.ImportBatchId, severity, null, 10_000, Ct)];

    public Task<long> CountAsync(string sql, Guid ws) => Db.ScalarAsync<long>(sql, ("ws", ws));

    /// <summary>The number of IndexChunkTasks per committed chunk of the job (superuser read).</summary>
    public async Task<Dictionary<Guid, long>> IndexTasksPerChunkAsync(ImportBatchRecord batch)
    {
        var result = new Dictionary<Guid, long>();
        await using var command = Db.DataSource.CreateCommand(
            "SELECT chunk_id, count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND job_id = @job GROUP BY chunk_id");
        command.Parameters.AddWithValue("ws", batch.WorkspaceId);
        command.Parameters.AddWithValue("job", batch.JobId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result[reader.GetGuid(0)] = reader.GetInt64(1);
        }

        return result;
    }

    public async Task<Dictionary<string, (Guid DocumentId, string Metadata, string? Raw, long Version)>> DocumentsAsync(Guid ws)
    {
        var result = new Dictionary<string, (Guid, string, string?, long)>(StringComparer.Ordinal);
        await using var command = Db.DataSource.CreateCommand(
            """
            SELECT d.control_number, d.document_id, d.metadata::text, d.metadata_raw::text, s.document_version
            FROM opportunity.document d JOIN opportunity.document_projection_state s USING (workspace_id, document_id)
            WHERE d.workspace_id = @ws
            """);
        command.Parameters.AddWithValue("ws", ws);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result[reader.GetString(0)] = (reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4));
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDatabase)
        {
            await Db.DisposeAsync();
        }

        if (Directory.Exists(StoreRoot))
        {
            Directory.Delete(StoreRoot, recursive: true);
        }
    }
}

/// <summary>Object store wrapper whose reads fail while <see cref="FailReads"/> is above zero (a crash or outage mid-read).</summary>
internal sealed class FlakyObjectStore(IObjectStore inner) : IObjectStore
{
    public int FailReads { get; set; }

    /// <summary>Bytes a failing read delivers before it breaks.</summary>
    public int FailAfterBytes { get; set; } = 64;

    public Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default) =>
        inner.PutAsync(key, content, options, cancellationToken);

    public async Task<Stream> OpenReadAsync(ObjectKey key, ByteRange? range = null, CancellationToken cancellationToken = default)
    {
        var stream = await inner.OpenReadAsync(key, range, cancellationToken);
        if (FailReads <= 0)
        {
            return stream;
        }

        FailReads--;
        return new BreakingStream(stream, FailAfterBytes);
    }

    public Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default) => inner.HeadAsync(key, cancellationToken);

    public IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
        inner.ListPrefixAsync(prefix, cancellationToken);

    public Task<DeletePrefixResult> DeletePrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
        inner.DeletePrefixAsync(prefix, cancellationToken);

    private sealed class BreakingStream(Stream inner, int failAfter) : Stream
    {
        private int _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= failAfter)
            {
                throw new IOException("Simulated storage outage mid-read.");
            }

            var n = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, failAfter - _read)], cancellationToken);
            _read += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
