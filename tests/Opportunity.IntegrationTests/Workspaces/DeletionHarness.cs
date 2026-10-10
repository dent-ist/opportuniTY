using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Authorization;
using Opportunity.Application.Coding;
using Opportunity.Application.Keys;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Storage;
using Opportunity.Application.Workspaces;
using Opportunity.Application.Workspaces.Deletion;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Data.Keys;
using Opportunity.Data.Workspaces;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Productions;
using Opportunity.Jobs.Lifecycle;
using Opportunity.Security.Keys;
using Opportunity.Storage;
using Opportunity.Storage.Encryption;
using Opportunity.Storage.FileSystem;

namespace Opportunity.IntegrationTests.Workspaces;

/// <summary>
/// Workspace deletion without OpenSearch (E20-T02): the migrated database, envelope-encrypted objects on the filesystem
/// provider with the local key provider, the real deletion store and coordinator, and an in-memory search index per
/// workspace (<see cref="FakeSearch"/>). The full-stack run with in-flight indexing is <c>WorkspaceDeletionInFlightTests</c>.
/// </summary>
internal sealed class DeletionHarness : IAsyncDisposable
{
    private readonly string _keys = Directory.CreateTempSubdirectory("opportunity-deletion-keys-").FullName;
    private readonly string _objects = Directory.CreateTempSubdirectory("opportunity-deletion-objects-").FullName;

    private readonly bool _ownsDatabase;

    private DeletionHarness(AuthorizationDatabase db, bool ownsDatabase)
    {
        _ownsDatabase = ownsDatabase;
        Db = db;
        Store = new WorkspaceDeletionStore(db.Core.AppDataSource);
        Bulk = BulkCodingHarness.Over(db);
        Productions = ProductionHarness.Over(db.Core);
        Holds = new PreservationLockService(new PreservationLockStore(db.Core.AppDataSource), TimeProvider.System);
        Keys = new WorkspaceKeyService(new WorkspaceDataKeyStore(db.Core.AppDataSource),
            new LocalKeyEncryptionKeyProvider(new LocalKeyStoreOptions { KeyDirectory = _keys }, new EnvironmentSecretProvider(_ => null)),
            new WorkspaceKeyOptions(), TimeProvider.System);
        Signer = new LocalSigningKeyProvider(new LocalKeyStoreOptions { KeyDirectory = _keys }, new EnvironmentSecretProvider(_ => null));
        Objects = new EnvelopeObjectStore(new FileSystemObjectStore(new FileSystemObjectStoreOptions { RootPath = _objects }), Keys);
    }

    public AuthorizationDatabase Db { get; }

    public WorkspaceDeletionStore Store { get; }

    public BulkCodingHarness Bulk { get; }

    public ProductionHarness Productions { get; }

    public PreservationLockService Holds { get; }

    public WorkspaceKeyService Keys { get; }

    public ISigningKeyProvider Signer { get; }

    public IObjectStore Objects { get; }

    public FakeSearch Search { get; } = new();

    public WorkspaceDeletionOptions Options { get; } = new()
    {
        WaitingPeriod = TimeSpan.Zero,
        AllowShortWaitingPeriod = true,
        DrainSettleDelay = TimeSpan.Zero,
        ResurrectionGuardDelay = TimeSpan.Zero,
        LeaseDuration = TimeSpan.FromSeconds(30),
        BatchSize = 3,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<DeletionHarness> CreateAsync(Migrations.MigrationPostgresFixture postgres) =>
        new(await AuthorizationDatabase.CreateAsync(postgres), ownsDatabase: true);

    /// <summary>Over another test's database (owned by the caller).</summary>
    public static DeletionHarness Over(AuthorizationDatabase db) => new(db, ownsDatabase: false);

    public WorkspaceDeletionCoordinator Coordinator() =>
        new(Store, Search, Objects, Keys, Options, TimeProvider.System, NullLogger<WorkspaceDeletionCoordinator>.Instance, Signer, BeforePurge);

    /// <summary>The pre-purge seam (#117 audit checkpoint): records each call and whether the index was still populated.</summary>
    public RecordingBeforeDeletion BeforePurge { get; } = new();

    public sealed class RecordingBeforeDeletion : IBeforeWorkspaceDeletion
    {
        public List<(Guid DeletionId, Guid WorkspaceId, bool SearchWasEmpty)> Calls { get; } = [];

        public IWorkspaceSearchPurge? Search { get; set; }

        public async Task BeforePurgeAsync(Guid deletionId, Guid workspaceId, CancellationToken cancellationToken)
        {
            var empty = Search is null || (await Search.CountAsync(workspaceId, cancellationToken)).IsEmpty;
            lock (Calls)
            {
                Calls.Add((deletionId, workspaceId, empty));
            }
        }
    }

    public WorkspaceDeletionService Service() => new(Store, new WorkspaceReader(Db.Core.AppDataSource), Options, TimeProvider.System);

    public static DeletionCaller Caller(Guid user, bool canApprove = false) => new(Principal(user), canApprove);

    public static SecurityPrincipal Principal(Guid user) => new() { UserId = user, DisplayName = "Deletion Tester", CorrelationId = "deletion-test" };

    /// <summary>A Workspace Admin who requests and a Retention Approver who approves the deletion (run starts at once).</summary>
    public async Task<WorkspaceDeletion> ApprovedAsync(Guid ws, DeletionRetentionProfile profile)
    {
        var requester = await Db.CreateUserAsync();
        await Db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, requester);
        var approver = await Db.CreateUserAsync();
        var name = await Db.Core.ScalarAsync<string>("SELECT name FROM opportunity.workspace WHERE workspace_id = @ws", ("ws", ws));
        var requested = await Service().RequestAsync(Principal(requester), ws,
            new WorkspaceDeletionRequest(profile, "Matter closed; return-or-destroy clause of the protective order", "PO ¶ 14", name), Ct);
        requested.Status.Should().Be(WorkspaceDeletionResultStatus.Ok, requested.Detail);
        var approved = await Service().ApproveAsync(Caller(approver, canApprove: true), requested.Deletion!.DeletionId, requested.Deletion.Version,
            "Approved per order of 2026-10-01", Ct);
        approved.Status.Should().Be(WorkspaceDeletionResultStatus.Ok, approved.Detail);
        return approved.Deletion!;
    }

    /// <summary>Drives the coordinator until the deletion stops changing; returns it.</summary>
    public async Task<WorkspaceDeletion> RunAsync(Guid deletionId, WorkspaceDeletionCoordinator? coordinator = null)
    {
        coordinator ??= Coordinator();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (await coordinator.StepAsync(deletionId, Ct))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the deletion run must finish");
        }

        return (await Store.GetAsync(deletionId, Ct))!;
    }

    /// <summary>
    /// A workspace with most kinds of records: fields and (system) choices, families with page sets and stored objects,
    /// interactive and bulk coding (events, outbox, jobs, chunks, snapshots), a finalized production with its Bates
    /// ledger, a production volume run (export rows and a file under productions/), envelope-encrypted objects in
    /// several storage areas, and a document in the search index.
    /// </summary>
    public async Task<Seeded> SeedAsync(Guid ws, string prefix)
    {
        var w = await Bulk.WorkspaceAsync(ws);
        var qc = await Bulk.MemberAsync(ws, WorkspaceRole.QcReviewer);
        var manager = await Productions.UserAsync(ws, WorkspaceRole.ProductionManager);
        var docs = await Productions.FamiliesAsync(ws, prefix, [(2, "pdf"), (1, "docx")], [(1, "xlsx")], [(0, "msg")]);
        foreach (var doc in docs)
        {
            await Db.Core.InsertStoredObjectAsync(ws, doc);
            await PutAsync(text => ObjectKeys.Native(ws, doc, text), doc.ToString());
        }

        await Bulk.InteractiveAsync(ws, qc, docs[0], CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true)));
        var bulkSnapshot = await Bulk.SnapshotAsync(ws, qc, docs);
        var job = await Bulk.StartAsync(ws, qc, bulkSnapshot.SnapshotId, new CodingChange(w.Responsive, Core.Coding.CodingOperationKind.Set, JsonValue.Create(false)));
        await Bulk.RunAsync(ws, job.JobId);

        var snapshot = await Productions.SnapshotAsync(ws, manager, docs);
        var draft = await Productions.CreateOkAsync(ws, manager, snapshot.SnapshotId, ProductionHarness.Spec(prefix + "P"));
        var allocated = await Productions.AllocateAsync(ws, manager, draft.ProductionId);
        var finalized = await Productions.Service().FinalizeAsync(ProductionHarness.Principal(manager), ws, draft.ProductionId, allocated.RowVersion, ProductionHarness.Unimaged(), Ct);
        finalized.Status.Should().Be(Opportunity.Production.Productions.ProductionOutcomeStatus.Ok, finalized.Reason);

        // A volume run of the production (as #104 records it): an export row with production_id and one produced file.
        var exportId = Guid.CreateVersion7();
        var runKey = ObjectKeys.ProductionFile(ws, draft.ProductionId, 1, exportId, "VOL001/DATA/LOADFILE.DAT");
        await Db.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.export (workspace_id, export_id, job_id, snapshot_id, name, settings, status, created_by, created_by_display,
                                            completed_at, documents_exported, documents_excluded, natives, texts, images, pages, file_count,
                                            total_bytes, manifest_sha256, production_id)
            VALUES (@ws, @id, @job, @snapshot, 'Volume 1', '{}', 2, @by, 'Production Manager', now(), 4, 0, 1, 4, 3, 4, 1, 10,
                    sha256('m'::bytea), @production)
            """,
            ("ws", ws), ("id", exportId), ("job", allocated.BatesJobId!.Value), ("snapshot", snapshot.SnapshotId), ("by", manager),
            ("production", draft.ProductionId));
        var objectId = Guid.CreateVersion7();
        await Db.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.stored_object (workspace_id, object_id, logical_key, area, sha256, size_bytes, key_id, encryption_scheme, state)
            VALUES (@ws, @id, @key, 9, sha256('v'::bytea), 10, 'wdk-v1', 2, 1)
            """,
            ("ws", ws), ("id", objectId), ("key", runKey.Value));
        await Db.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.export_file (workspace_id, export_id, file_id, path, kind, object_id, sha256, size_bytes, content_type)
            VALUES (@ws, @export, @file, 'VOL001/DATA/LOADFILE.DAT', 1, @object, sha256('v'::bytea), 10, 'text/plain')
            """,
            ("ws", ws), ("export", exportId), ("file", Guid.CreateVersion7()), ("object", objectId));
        await PutAsync(runKey, "produced load file");
        await PutAsync(text => ObjectKeys.Report(ws, Guid.CreateVersion7(), text), "report");
        await PutAsync(ObjectKeys.JobScratch(ws, job.JobId, "scratch.bin"), "scratch");
        Search.Index(ws, docs.Count);
        return new Seeded(docs, draft.ProductionId, exportId, runKey);
    }

    public async Task PutAsync(ObjectKey key, string text)
    {
        var bytes = Encoding.UTF8.GetBytes("synthetic test content " + text);
        await Objects.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);
    }

    /// <summary>A content-addressed object: the key is built from the content's SHA-256.</summary>
    public Task PutAsync(Func<Sha256Digest, ObjectKey> key, string text) =>
        PutAsync(key(Sha256Digest.Compute(Encoding.UTF8.GetBytes("synthetic test content " + text))), text);

    /// <summary>Rows per tenant table of a workspace, counted as the owner (bypasses nothing: sets the workspace context).</summary>
    public async Task<Dictionary<string, long>> RowsAsync(Guid ws)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in await Db.Core.ColumnAsync("SELECT t FROM opportunity.workspace_purge_tables() t"))
        {
            var n = await Db.Core.ScalarAsync<long>($"SELECT count(*) FROM opportunity.{table} WHERE workspace_id = @ws", ("ws", ws));
            if (n > 0)
            {
                counts[table] = n;
            }
        }

        return counts;
    }

    public async Task<List<string>> ObjectKeysAsync(Guid ws)
    {
        var keys = new List<string>();
        await foreach (var listing in Objects.ListPrefixAsync(ObjectPrefix.Workspace(ws), Ct))
        {
            keys.Add(listing.Key.Value);
        }

        return keys;
    }

    public async ValueTask DisposeAsync()
    {
        await Bulk.DisposeAsync();
        if (_ownsDatabase)
        {
            await Db.DisposeAsync();
        }

        Directory.Delete(_keys, recursive: true);
        Directory.Delete(_objects, recursive: true);
    }

    internal sealed record Seeded(IReadOnlyList<Guid> Documents, Guid ProductionId, Guid VolumeRunId, ObjectKey VolumeFile);

    /// <summary>An in-memory search index: documents per workspace; <see cref="OnPurge"/> runs at each purge pass.</summary>
    internal sealed class FakeSearch : IWorkspaceSearchPurge
    {
        private readonly ConcurrentDictionary<Guid, long> _documents = new();

        public int Passes { get; private set; }

        public Func<Guid, Task>? OnPurge { get; set; }

        public void Index(Guid ws, long documents) => _documents.AddOrUpdate(ws, documents, (_, n) => n + documents);

        public async Task<WorkspaceSearchPurgeResult> PurgeAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        {
            Passes++;
            if (OnPurge is { } hook)
            {
                await hook(workspaceId);
            }

            _documents.TryRemove(workspaceId, out var removed);
            return new WorkspaceSearchPurgeResult(0, removed);
        }

        public Task<WorkspaceSearchInventory> CountAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceSearchInventory(_documents.GetValueOrDefault(workspaceId), 0));
    }
}
