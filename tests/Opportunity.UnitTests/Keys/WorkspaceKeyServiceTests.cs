using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Keys;
using Opportunity.Application.Workspaces;
using Opportunity.Security.Keys;

namespace Opportunity.UnitTests.Keys;

public sealed class WorkspaceKeyServiceTests : IDisposable
{
    private static readonly OperationsActor Operator = OperationsActor.Cli("test-operator");

    private readonly string _dir = Directory.CreateTempSubdirectory("opportunity-keys-").FullName;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-09T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly InMemoryWorkspaceDataKeyStore _store = new();
    private readonly InMemoryAuditEventWriter _installationAudit = new();
    private readonly LocalKeyEncryptionKeyProvider _keks;

    public WorkspaceKeyServiceTests()
    {
        _keks = new LocalKeyEncryptionKeyProvider(new LocalKeyStoreOptions { KeyDirectory = _dir }, new EnvironmentSecretProvider(_ => null));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task The_first_object_creates_version_1_under_the_installation_kek_once_and_audits_it()
    {
        var ws = Guid.NewGuid();
        var service = Service();

        var first = await service.GetCurrentAsync(ws, Ct);
        var again = await Service().GetCurrentAsync(ws, Ct);

        first.Version.Should().Be(1);
        first.KeyId.Should().Be("wdk-v1");
        again.Material.ToArray().Should().Equal(first.Material.ToArray());
        first.ToString().Should().NotContain(Convert.ToHexString(first.Material));
        var row = (await _store.ListAsync(ws, Ct)).Should().ContainSingle().Subject;
        row.KekId.Should().Be(KeyEncryptionKeyIds.Installation);
        _store.AuditEvents.Should().ContainSingle(e => e.Action == AuditTaxonomy.Admin.KeyCreated && e.WorkspaceId == ws);
    }

    [Fact]
    public async Task Rotation_and_dedicated_keys_change_the_active_key_while_old_versions_still_open()
    {
        var ws = Guid.NewGuid();
        var service = Service();
        var v1 = (await service.GetCurrentAsync(ws, Ct)).Material.ToArray();

        await service.RotateAsync(ws, Operator, Ct);
        var dedicated = await service.UseDedicatedKeyAsync(ws, Operator, Ct);
        (await service.UseDedicatedKeyAsync(ws, Operator, Ct)).Version.Should().Be(dedicated.Version, "already dedicated: a no-op");

        dedicated.Version.Should().Be(3);
        dedicated.KekId.Should().Be(KeyEncryptionKeyIds.ForWorkspace(ws));
        (await service.GetCurrentAsync(ws, Ct)).Version.Should().Be(3);
        (await Service().GetAsync(ws, 1, Ct)).Material.ToArray().Should().Equal(v1);
        (await _store.ListAsync(ws, Ct)).Select(r => r.State).Should().Equal(
            WorkspaceDataKeyState.Retired, WorkspaceDataKeyState.Retired, WorkspaceDataKeyState.Active);
    }

    [Fact]
    public async Task The_rewrap_job_moves_every_version_to_the_newest_version_of_its_workspace_kek_without_changing_keys()
    {
        var ws = Guid.NewGuid();
        var other = Guid.NewGuid();
        var service = Service();
        var v1 = (await service.GetCurrentAsync(ws, Ct)).Material.ToArray();
        await service.GetCurrentAsync(other, Ct);
        await service.UseDedicatedKeyAsync(ws, Operator, Ct);
        await service.RotateKekAsync(KeyEncryptionKeyIds.Installation, Operator, Ct);

        var result = await service.RewrapAsync(null, Operator, Ct);
        var again = await service.RewrapAsync(null, Operator, Ct);

        result.Should().BeEquivalentTo(new KeyRewrapResult(2, 3, 2, []));
        again.Rewrapped.Should().Be(0, "the job is idempotent");
        (await _store.ListAsync(ws, Ct)).Should().AllSatisfy(r => r.KekId.Should().Be(KeyEncryptionKeyIds.ForWorkspace(ws)));
        (await _store.ListAsync(other, Ct)).Should().ContainSingle().Which.KekVersion.Should().Be(2);
        (await Service().GetAsync(ws, 1, Ct)).Material.ToArray().Should().Equal(v1, "rewrapping never changes a data key");
        _installationAudit.Events.Should().ContainSingle(e => e.Action == AuditTaxonomy.Admin.KeyRotated && e.WorkspaceId == null);

        (await service.PruneKekAsync(KeyEncryptionKeyIds.Installation, Operator, Ct)).Should().Equal(1);
        (await _keks.DescribeAsync(KeyEncryptionKeyIds.Installation, Ct))!.Versions.Should().Equal(2);
        (await Service().GetAsync(other, 1, Ct)).Version.Should().Be(1);
    }

    [Fact]
    public async Task Pruning_keeps_kek_versions_that_still_wrap_a_data_key()
    {
        var ws = Guid.NewGuid();
        var service = Service();
        await service.GetCurrentAsync(ws, Ct);
        await service.RotateKekAsync(KeyEncryptionKeyIds.Installation, Operator, Ct);

        (await service.PruneKekAsync(KeyEncryptionKeyIds.Installation, Operator, Ct)).Should().BeEmpty("v1 still wraps a data key until the rewrap runs");
        (await Service().GetAsync(ws, 1, Ct)).Version.Should().Be(1);
    }

    [Fact]
    public async Task Crypto_shredding_destroys_every_data_key_and_the_dedicated_kek_and_is_refused_under_a_hold()
    {
        var ws = Guid.NewGuid();
        var held = Guid.NewGuid();
        var service = Service();
        await service.GetCurrentAsync(ws, Ct);
        await service.UseDedicatedKeyAsync(ws, Operator, Ct);
        await service.GetCurrentAsync(held, Ct);
        _store.Held.Add(held);

        var result = await service.DestroyWorkspaceKeysAsync(ws, Operator, Ct);

        result.Should().Be(new WorkspaceKeyDestruction(ws, 2, true));
        (await _keks.DescribeAsync(KeyEncryptionKeyIds.ForWorkspace(ws), Ct)).Should().BeNull();
        await FluentActions.Awaiting(async () => await service.GetAsync(ws, 1, Ct)).Should().ThrowAsync<KeyUnavailableException>();
        await FluentActions.Awaiting(async () => await Service().GetCurrentAsync(ws, Ct)).Should().ThrowAsync<KeyUnavailableException>(
            "a shredded workspace cannot store new objects");
        var audit = _store.AuditEvents[^1];
        audit.Action.Should().Be(AuditTaxonomy.Admin.KeyDestroyed);
        audit.ActorDisplay.Should().Contain("test-operator");

        await FluentActions.Awaiting(() => service.DestroyWorkspaceKeysAsync(held, Operator, Ct)).Should().ThrowAsync<PreservationLockedException>();
        (await Service().GetCurrentAsync(held, Ct)).Version.Should().Be(1);
    }

    [Fact]
    public async Task A_wrapped_key_moved_to_another_workspace_or_version_does_not_open()
    {
        var ws = Guid.NewGuid();
        var victim = Guid.NewGuid();
        var service = Service();
        await service.GetCurrentAsync(ws, Ct);
        var row = (await _store.GetActiveAsync(ws, Ct))!;

        _store.Replace(row with { WorkspaceId = victim });
        _store.Replace(row with { Version = 2, State = WorkspaceDataKeyState.Retired });

        await FluentActions.Awaiting(async () => await Service().GetAsync(victim, 1, Ct)).Should().ThrowAsync<KeyUnavailableException>();
        await FluentActions.Awaiting(async () => await Service().GetAsync(ws, 2, Ct)).Should().ThrowAsync<KeyUnavailableException>();
    }

    [Fact]
    public async Task Unwrapped_keys_are_cached_for_the_configured_time_only()
    {
        var ws = Guid.NewGuid();
        var service = Service();
        await service.GetCurrentAsync(ws, Ct);
        await Service().RotateAsync(ws, Operator, Ct);

        (await service.GetCurrentAsync(ws, Ct)).Version.Should().Be(1, "the active version is cached");
        _time.Advance(TimeSpan.FromMinutes(2));
        (await service.GetCurrentAsync(ws, Ct)).Version.Should().Be(2);
    }

    private WorkspaceKeyService Service() =>
        new(_store, _keks, new WorkspaceKeyOptions { DataKeyCacheTtl = TimeSpan.FromMinutes(1) }, _time, _installationAudit);
}
