using System.Text;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

using Npgsql;

using Opportunity.Application.Authorization;
using Opportunity.Application.Keys;
using Opportunity.Application.Storage;
using Opportunity.Application.Workspaces;
using Opportunity.Data.Keys;
using Opportunity.Data.Workspaces;
using Opportunity.Hosting.Operations;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Security.Keys;
using Opportunity.Storage;
using Opportunity.Storage.Encryption;
using Opportunity.Storage.FileSystem;

namespace Opportunity.IntegrationTests.Keys;

/// <summary>
/// E05-T09 acceptance: workspace data keys in PostgreSQL (V0052) wrapped by KEKs of the local key provider, objects
/// envelope-encrypted through the filesystem provider, and the operator CLI (<c>keys …</c>) driving switching to a
/// dedicated key, the rewrap job, KEK rotation and pruning, and crypto-shredding, each audited.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class WorkspaceKeyLifecycleTests(MigrationPostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _keys = Directory.CreateTempSubdirectory("opportunity-keys-").FullName;
    private readonly string _objects = Directory.CreateTempSubdirectory("opportunity-objects-").FullName;
    private CoreSchemaDatabase _db = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _db = await CoreSchemaDatabase.CreateAsync(postgres);

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        Directory.Delete(_keys, recursive: true);
        Directory.Delete(_objects, recursive: true);
    }

    [Fact]
    public async Task Switching_a_workspace_to_a_dedicated_key_affects_new_objects_and_the_rewrap_job_moves_its_older_keys()
    {
        var ws = await _db.CreateWorkspaceAsync();
        var (beforeKey, before) = await PutAsync(Store(), ws, "before");

        (await CliAsync("dedicate", "--workspace", ws.ToString(), "--operator", "test-operator")).Should().Be(0);
        var (afterKey, after) = await PutAsync(Store(), ws, "after");

        var reader = Store();
        (await reader.HeadAsync(beforeKey, Ct))!.KeyId.Should().Be("wdk-v1");
        (await reader.HeadAsync(afterKey, Ct))!.KeyId.Should().Be("wdk-v2", "new objects use the dedicated key");
        var rows = await Rows(ws);
        rows.Select(r => (r.Version, r.KekId, r.State)).Should().Equal(
            (1, KeyEncryptionKeyIds.Installation, WorkspaceDataKeyState.Retired),
            (2, KeyEncryptionKeyIds.ForWorkspace(ws), WorkspaceDataKeyState.Active));

        (await CliAsync("rewrap", "--workspace", ws.ToString(), "--operator", "test-operator")).Should().Be(0);

        (await Rows(ws)).Should().AllSatisfy(r => r.KekId.Should().Be(KeyEncryptionKeyIds.ForWorkspace(ws)), "the rewrap job moved v1");
        (await ReadAsync(Store(), beforeKey)).Should().Equal(before);
        (await ReadAsync(Store(), afterKey)).Should().Equal(after);
        (await _db.ColumnAsync($"SELECT action || ':' || coalesce(details->>'operation', '') FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Admin' ORDER BY occurred_at, event_id"))
            .Should().Equal("KeyCreated:Create", "KeyCreated:UseDedicatedKek", "KeyRotated:Rewrap");
        await AssertNoKeyMaterialInAuditAsync(ws);
    }

    [Fact]
    public async Task Kek_rotation_rewraps_every_workspace_then_prunes_the_old_version_without_touching_stored_objects()
    {
        var first = await _db.CreateWorkspaceAsync();
        var second = await _db.CreateWorkspaceAsync();
        var (firstKey, firstBytes) = await PutAsync(Store(), first, "first");
        var (secondKey, secondBytes) = await PutAsync(Store(), second, "second");
        var storedBefore = await File.ReadAllBytesAsync(Path.Combine(_objects, "objects", firstKey.Value), Ct);

        (await CliAsync("rotate-kek", "--kek", "installation", "--operator", "test-operator")).Should().Be(0);
        (await CliAsync("prune-kek", "--kek", "installation", "--operator", "test-operator")).Should().Be(0);
        (await Keks().DescribeAsync("installation", Ct))!.Versions.Should().Equal([1, 2], "v1 still wraps data keys until the rewrap ran");

        (await CliAsync("rewrap", "--operator", "test-operator")).Should().Be(0);
        (await CliAsync("prune-kek", "--kek", "installation", "--operator", "test-operator")).Should().Be(0);

        (await Keks().DescribeAsync("installation", Ct))!.Versions.Should().Equal(2);
        (await Rows(first)).Concat(await Rows(second)).Should().AllSatisfy(r => r.KekVersion.Should().Be(2));
        (await File.ReadAllBytesAsync(Path.Combine(_objects, "objects", firstKey.Value), Ct)).Should().Equal(storedBefore, "no object is re-encrypted");
        (await ReadAsync(Store(), firstKey)).Should().Equal(firstBytes);
        (await ReadAsync(Store(), secondKey)).Should().Equal(secondBytes);
        (await _db.ColumnAsync("SELECT action || ':' || resource_id || ':' || (details->>'kekVersion') FROM audit.audit_event WHERE workspace_id IS NULL AND category = 'Admin' ORDER BY occurred_at, event_id"))
            .Should().Equal("KeyRotated:installation:2", "KeyDestroyed:installation:1");
        (await _db.ScalarAsync<long>($"SELECT count(*) FROM audit.audit_event WHERE workspace_id IN ('{first}', '{second}') AND action = 'KeyRotated' AND details->>'operation' = 'Rewrap'"))
            .Should().Be(2);
    }

    [Fact]
    public async Task Destroying_a_workspace_s_keys_shreds_every_object_is_audited_and_is_refused_under_a_legal_hold()
    {
        var ws = await _db.CreateWorkspaceAsync();
        var held = await _db.CreateWorkspaceAsync();
        var (key, _) = await PutAsync(Store(), ws, "doomed");
        var (heldKey, heldBytes) = await PutAsync(Store(), held, "kept");
        (await CliAsync("dedicate", "--workspace", ws.ToString(), "--operator", "test-operator")).Should().Be(0);
        (await new PreservationLockService(new PreservationLockStore(_db.AppDataSource), TimeProvider.System)
            .PlaceAsync(Admin(), held, new PreservationLockRequest("Preservation letter received", "PL-54", false), Ct)).Status
            .Should().Be(PreservationLockStatus.Ok);

        (await CliAsync("destroy", "--workspace", ws.ToString(), "--operator", "test-operator")).Should().Be(KeyOperationsCli.ExitUsage, "--confirm is required");
        (await CliAsync("destroy", "--workspace", held.ToString(), "--operator", "test-operator", "--confirm", held.ToString()))
            .Should().Be(KeyOperationsCli.ExitRefused);
        (await CliAsync("destroy", "--workspace", ws.ToString(), "--operator", "test-operator", "--confirm", ws.ToString())).Should().Be(0);

        (await Rows(ws)).Should().AllSatisfy(r =>
        {
            r.State.Should().Be(WorkspaceDataKeyState.Destroyed);
            r.WrappedKey.Should().BeNull();
        });
        (await Keks().DescribeAsync(KeyEncryptionKeyIds.ForWorkspace(ws), Ct)).Should().BeNull("the dedicated KEK is destroyed too");
        await FluentActions.Awaiting(() => ReadAsync(Store(), key)).Should().ThrowAsync<KeyUnavailableException>();
        await FluentActions.Awaiting(() => PutAsync(Store(), ws, "late")).Should().ThrowAsync<KeyUnavailableException>();
        (await ReadAsync(Store(), heldKey)).Should().Equal(heldBytes, "the held workspace keeps its keys");
        (await _db.ColumnAsync($"SELECT actor_display || '|' || (details->>'dataKeysDestroyed') FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'KeyDestroyed'"))
            .Should().Equal("Operations CLI (test-operator)|2");

        // The database keeps a destroyed key destroyed, whatever the role.
        var revive = () => _db.ExecuteAsync($"UPDATE opportunity.workspace_data_key SET state = 2, wrapped_key = '\\x00112233445566778899aabbccddeeff', destroyed_at = NULL WHERE workspace_id = '{ws}'");
        (await revive.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        var shredHeld = () => _db.ExecuteAsync($"UPDATE opportunity.workspace_data_key SET state = 3, wrapped_key = NULL, destroyed_at = now() WHERE workspace_id = '{held}'");
        (await shredHeld.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PreservationLockViolation.SqlState);
        await AssertNoKeyMaterialInAuditAsync(ws);
    }

    [Fact]
    public async Task Concurrent_first_writes_create_exactly_one_data_key_and_the_status_command_lists_it()
    {
        var ws = await _db.CreateWorkspaceAsync();

        var keys = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => (await Ring().GetCurrentAsync(ws, Ct)).Material.ToArray()));

        keys.Should().AllSatisfy(k => k.Should().Equal(keys[0]));
        (await Rows(ws)).Should().ContainSingle();
        (await _db.ScalarAsync<long>($"SELECT count(*) FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'KeyCreated'")).Should().Be(1);
        var output = new StringWriter();
        (await KeyOperationsCli.RunAsync(["status", "--workspace", ws.ToString()], output, TextWriter.Null, Configuration(), Ct)).Should().Be(0);
        output.ToString().Should().Contain("wdk-v1\tActive\tinstallation\t1");
    }

    private async Task<int> CliAsync(params string[] args)
    {
        var error = new StringWriter();
        var exit = await KeyOperationsCli.RunAsync(args, TextWriter.Null, error, Configuration(), Ct);
        return exit;
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = _db.AppConnectionString,
            ["KeyManagement:Local:KeyDirectory"] = _keys,
        })
        .Build();

    private LocalKeyEncryptionKeyProvider Keks() =>
        new(new LocalKeyStoreOptions { KeyDirectory = _keys }, new EnvironmentSecretProvider(_ => null));

    /// <summary>A fresh key ring (no cache), like a new process.</summary>
    private WorkspaceKeyService Ring() =>
        new(new WorkspaceDataKeyStore(_db.AppDataSource), Keks(), new WorkspaceKeyOptions(), TimeProvider.System);

    private EnvelopeObjectStore Store() =>
        new(new FileSystemObjectStore(new FileSystemObjectStoreOptions { RootPath = _objects }), Ring());

    private Task<IReadOnlyList<WorkspaceDataKeyRecord>> Rows(Guid ws) => new WorkspaceDataKeyStore(_db.AppDataSource).ListAsync(ws, Ct);

    private static async Task<(ObjectKey Key, byte[] Bytes)> PutAsync(EnvelopeObjectStore store, Guid ws, string text)
    {
        var bytes = Encoding.UTF8.GetBytes($"synthetic test document {text} " + new string('x', 100_000));
        var key = ObjectKeys.Native(ws, Guid.NewGuid(), Sha256Digest.Compute(bytes));
        await store.PutAsync(key, new MemoryStream(bytes), cancellationToken: Ct);
        return (key, bytes);
    }

    private static async Task<byte[]> ReadAsync(EnvelopeObjectStore store, ObjectKey key)
    {
        await using var stream = await store.OpenReadAsync(key, cancellationToken: Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    private async Task AssertNoKeyMaterialInAuditAsync(Guid ws)
    {
        var details = string.Join('\n', await _db.ColumnAsync($"SELECT details::text FROM audit.audit_event WHERE workspace_id = '{ws}' OR workspace_id IS NULL"));
        foreach (var row in await new WorkspaceDataKeyStore(_db.AppDataSource).ListAsync(ws, Ct))
        {
            if (row.WrappedKey is { } wrapped)
            {
                details.Should().NotContain(Convert.ToBase64String(wrapped)).And.NotContain(Convert.ToHexString(wrapped).ToLowerInvariant());
            }
        }

        details.Should().NotContainAny(["wrapped", "ciphertext", "material"]);
    }

    private static SecurityPrincipal Admin() => new()
    {
        UserId = Guid.CreateVersion7(),
        DisplayName = "Hold Admin",
        CorrelationId = "key-lifecycle-test",
    };
}
