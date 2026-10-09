using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Keys;
using Opportunity.Application.Workspaces;
using Opportunity.Application.Workspaces.Deletion;
using Opportunity.Core.Workspaces;
using Opportunity.Data.Workspaces;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs.Lifecycle;

namespace Opportunity.IntegrationTests.Workspaces;

/// <summary>
/// E20-T02 against PostgreSQL, envelope-encrypted object storage and the key provider: the fenced, verified and certified
/// deletion of a workspace with most kinds of records, under both retention profiles; the legal hold refusing a request
/// and halting a run; the write fence; the purge order over the live schema. The run with OpenSearch and in-flight bulk
/// and index work is <see cref="WorkspaceDeletionInFlightTests"/>.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class WorkspaceDeletionTests(MigrationPostgresFixture postgres)
{
    /// <summary>Tenant tables the purge leaves by design: the hold records (never deleted) and the destroyed key records.</summary>
    private static readonly string[] Kept = ["preservation_lock", "workspace_data_key"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_purge_order_covers_every_tenant_table_and_puts_each_table_before_the_tables_it_references()
    {
        await using var h = await DeletionHarness.CreateAsync(postgres);
        var (tables, references) = await h.Store.GetPurgeSchemaAsync(Ct);
        var plan = WorkspacePurgePlan.Order(tables, references);

        plan.Should().BeEquivalentTo(tables).And.OnlyHaveUniqueItems();
        tables.Should().NotContain(["workspace", .. Kept], "the registry, holds and key records are never purged");
        var position = plan.Select((t, i) => (t, i)).ToDictionary(p => p.t, p => p.i, StringComparer.Ordinal);
        foreach (var reference in references.Where(r => position.ContainsKey(r.Table) && position.ContainsKey(r.ReferencedTable) && r.Table != r.ReferencedTable))
        {
            if (WorkspacePurgePlan.DocumentCluster.Contains(reference.Table) && WorkspacePurgePlan.DocumentCluster.Contains(reference.ReferencedTable))
            {
                continue;
            }

            position[reference.Table].Should().BeLessThan(position[reference.ReferencedTable],
                "{0} references {1} and must be purged first", reference.Table, reference.ReferencedTable);
        }
    }

    [Fact]
    public async Task Purge_all_removes_every_row_object_and_index_entry_crypto_shreds_the_keys_and_certifies_while_audit_survives()
    {
        await using var h = await DeletionHarness.CreateAsync(postgres);
        var ws = await h.Db.Core.CreateWorkspaceAsync();
        var neighbor = await h.Db.Core.CreateWorkspaceAsync();
        await h.SeedAsync(ws, "DEL");
        await h.SeedAsync(neighbor, "KEEP");
        var neighborRows = await h.RowsAsync(neighbor);
        var neighborObjects = await h.ObjectKeysAsync(neighbor);
        (await h.ObjectKeysAsync(ws)).Should().HaveCountGreaterThan(5);

        var deletion = await h.ApprovedAsync(ws, DeletionRetentionProfile.PurgeAll);
        var before = await h.RowsAsync(ws);
        before.Keys.Should().Contain(["document", "page_set", "stored_object", "coding_event", "job", "job_chunk", "production", "bates_range",
            "export", "export_file", "document_set_snapshot", "document_set_snapshot_page", "choice", "field_definition", "search_outbox"]);
        h.BeforePurge.Search = h.Search;
        var done = await h.RunAsync(deletion.DeletionId);

        done.Status.Should().Be(WorkspaceDeletionStatus.Completed, done.Error);
        done.HasCertificate.Should().BeTrue();
        h.BeforePurge.Calls.Should().Equal([(deletion.DeletionId, ws, false)], "the pre-purge seam runs once, before anything is purged");
        (await h.Db.Core.ScalarAsync<string>("SELECT status FROM opportunity.workspace WHERE workspace_id = @ws", ("ws", ws)))
            .Should().Be(nameof(WorkspaceStatus.Purged), "the workspace row stays as the tombstone");
        (await h.RowsAsync(ws)).Keys.Except(Kept).Should().BeEmpty("no row of the workspace is left");
        (await h.ObjectKeysAsync(ws)).Should().BeEmpty();
        (await h.Search.CountAsync(ws, Ct)).IsEmpty.Should().BeTrue();
        h.Search.Passes.Should().BeGreaterThanOrEqualTo(2, "the search purge runs again after the resurrection guard delay");
        (await h.Db.Core.ColumnAsync($"SELECT state::text FROM opportunity.workspace_data_key WHERE workspace_id = '{ws}'"))
            .Should().NotBeEmpty().And.AllBe("3", "crypto-shredding destroys every data key");

        // Nothing of the neighbour changed.
        (await h.RowsAsync(neighbor)).Should().BeEquivalentTo(neighborRows);
        (await h.ObjectKeysAsync(neighbor)).Should().BeEquivalentTo(neighborObjects);

        // The certificate: in PostgreSQL and object storage with the same SHA-256, signed, counts before and after.
        var certificate = (await h.Store.GetCertificateAsync(deletion.DeletionId, Ct))!;
        SHA256.HashData(Encoding.UTF8.GetBytes(certificate.CertificateJson)).Should().Equal(certificate.Sha256);
        await using (var stored = await h.Objects.OpenReadAsync(Application.Storage.ObjectKey.Parse(certificate.ObjectKey!), cancellationToken: Ct))
        using (var reader = new StreamReader(stored))
        {
            (await reader.ReadToEndAsync(Ct)).Should().Be(certificate.CertificateJson);
        }

        (await h.Signer.VerifyAsync(new KeySignature(SigningKeyPurposes.AuditCheckpoint, 1, KeySignature.Es256, certificate.Signature!),
            Encoding.UTF8.GetBytes(certificate.CertificateJson), Ct)).Should().BeTrue();
        using (var json = JsonDocument.Parse(certificate.CertificateJson))
        {
            var root = json.RootElement;
            root.GetProperty("workspace").GetProperty("id").GetGuid().Should().Be(ws);
            root.GetProperty("retentionProfile").GetString().Should().Be("PurgeAll");
            root.GetProperty("request").GetProperty("reason").GetString().Should().StartWith("Matter closed");
            root.GetProperty("approval").GetProperty("approvedBy").GetProperty("userId").GetGuid().Should().Be(deletion.ApprovedBy!.Value);
            var stores = root.GetProperty("stores").EnumerateArray().ToDictionary(s => s.GetProperty("store").GetString()!);
            stores["PostgreSQL"].GetProperty("before").GetInt64().Should().Be(before.Where(p => !Kept.Contains(p.Key)).Sum(p => p.Value));
            stores["PostgreSQL"].GetProperty("after").GetInt64().Should().Be(0);
            stores["OpenSearch"].GetProperty("before").GetInt64().Should().Be(4);
            stores["OpenSearch"].GetProperty("after").GetInt64().Should().Be(0);
            stores["ObjectStorage"].GetProperty("before").GetInt64().Should().BeGreaterThan(5);
            stores["ObjectStorage"].GetProperty("after").GetInt64().Should().Be(0);
            stores["Keys"].GetProperty("destroyed").GetInt64().Should().BeGreaterThan(0);
            var residuals = root.GetProperty("residuals").EnumerateArray().Select(r => r.GetProperty("kind").GetString()).ToList();
            residuals.Should().Contain(["Backups", "OffSiteCopies", "DeliveredProductions", "AuditTrail"]).And.NotContain("RetainedRecords");
            root.GetProperty("residuals")[0].GetProperty("expiresBy").GetString().Should().NotBeNullOrEmpty();
            certificate.CertificateJson.Should().NotContain("DEL0001", "the certificate never carries document metadata");
        }

        // The audit trail of the workspace survives, including the deletion's own record, and the installation chain has the hash.
        var actions = await h.Db.Core.ColumnAsync(
            $"SELECT action FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Workspace' AND resource_type = 'WorkspaceDeletion' ORDER BY occurred_at, event_id");
        actions.Should().StartWith(["DeletionRequested", "DeletionApproved", "DeletionStarted"]).And.EndWith("Deleted")
            .And.Contain("DeletionStepCompleted");
        (await h.Db.Core.ScalarAsync<long>($"SELECT count(*) FROM audit.audit_event WHERE workspace_id = '{ws}' AND category <> 'Workspace'"))
            .Should().BeGreaterThan(0, "the matter's audit trail is kept (Q-16)");
        (await h.Db.Core.ScalarAsync<string>(
            "SELECT details->>'certificateSha256' FROM audit.audit_event WHERE workspace_id IS NULL AND action = 'Deleted' AND resource_id = @id",
            ("id", deletion.DeletionId.ToString())))
            .Should().Be(Convert.ToHexStringLower(certificate.Sha256));
        (await h.Db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.workspace_deletion_step WHERE deletion_id = @id", ("id", deletion.DeletionId)))
            .Should().Be(Enum.GetValues<DeletionStep>().Length);

        // Running the coordinator again changes nothing (idempotent), and the record cannot be deleted.
        (await h.Coordinator().StepAsync(deletion.DeletionId, Ct)).Should().BeFalse();
        var refused = async () => await h.Db.Core.ExecuteAsync("DELETE FROM opportunity.destruction_certificate");
        await refused.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task Retain_records_keeps_productions_their_volume_outputs_and_keys_and_removes_everything_else()
    {
        await using var h = await DeletionHarness.CreateAsync(postgres);
        var ws = await h.Db.Core.CreateWorkspaceAsync();
        var seeded = await h.SeedAsync(ws, "RET");
        var productions = await h.Db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.production WHERE workspace_id = @ws", ("ws", ws));
        var members = await h.Db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.production_document WHERE workspace_id = @ws", ("ws", ws));

        var deletion = await h.ApprovedAsync(ws, DeletionRetentionProfile.RetainRecords);
        var done = await h.RunAsync(deletion.DeletionId);

        done.Status.Should().Be(WorkspaceDeletionStatus.Completed, done.Error);
        var left = await h.RowsAsync(ws);
        left.Keys.Except(Kept).Should().BeSubsetOf(["production", "production_document", "bates_range", "export", "export_file", "stored_object",
            "document_set_snapshot", "document_set_snapshot_page", "job"]);
        left["production"].Should().Be(productions);
        left["production_document"].Should().Be(members);
        left["export"].Should().Be(1, "the volume run is kept");
        left["stored_object"].Should().Be(1, "only the produced file's registry row is kept");
        left.Should().NotContainKeys("document", "page_set", "coding_event", "choice", "field_definition", "job_chunk", "search_outbox");
        (await h.ObjectKeysAsync(ws)).Should().Equal(seeded.VolumeFile.Value);
        (await h.Objects.OpenReadAsync(seeded.VolumeFile, cancellationToken: Ct)).Dispose();
        (await h.Db.Core.ColumnAsync($"SELECT state::text FROM opportunity.workspace_data_key WHERE workspace_id = '{ws}'"))
            .Should().Contain("1", "retained productions still need their data key");

        using var json = JsonDocument.Parse((await h.Store.GetCertificateAsync(deletion.DeletionId, Ct))!.CertificateJson);
        json.RootElement.GetProperty("residuals").EnumerateArray().Select(r => r.GetProperty("kind").GetString()).Should().Contain("RetainedRecords");
        var keys = json.RootElement.GetProperty("stores").EnumerateArray().Single(s => s.GetProperty("store").GetString() == "Keys");
        keys.GetProperty("status").GetString().Should().Be("RetainedForRetainedRecords");
    }

    [Fact]
    public async Task A_legal_hold_refuses_the_request_and_a_hold_placed_during_the_run_halts_it_until_released()
    {
        await using var h = await DeletionHarness.CreateAsync(postgres);
        var ws = await h.Db.Core.CreateWorkspaceAsync();
        await h.SeedAsync(ws, "HLD");
        var counsel = await h.Db.CreateUserAsync();
        await h.Db.AssignAsync(ws, Core.Security.WorkspaceRole.WorkspaceAdmin, counsel);

        // Requests (and approvals) are refused while a hold is active.
        var hold = (await h.Holds.PlaceAsync(DeletionHarness.Principal(counsel), ws,
            new PreservationLockRequest("Preservation letter received", null, false), Ct)).Lock!;
        var name = await h.Db.Core.ScalarAsync<string>("SELECT name FROM opportunity.workspace WHERE workspace_id = @ws", ("ws", ws));
        var refused = async () => await h.Service().RequestAsync(DeletionHarness.Principal(counsel), ws,
            new WorkspaceDeletionRequest(DeletionRetentionProfile.PurgeAll, "End of matter", null, name), Ct);
        await refused.Should().ThrowAsync<PreservationLockedException>();
        (await h.Holds.RequestReleaseAsync(DeletionHarness.Principal(counsel), ws, hold.LockId, hold.Version, "Matter settled", Ct))
            .Status.Should().Be(PreservationLockStatus.Ok);

        // A hold placed while the run purges search halts it before the database purge.
        var deletion = await h.ApprovedAsync(ws, DeletionRetentionProfile.PurgeAll);
        PreservationLock? midRun = null;
        h.Search.OnPurge = async id =>
        {
            midRun ??= (await h.Holds.PlaceAsync(DeletionHarness.Principal(counsel), id, new PreservationLockRequest("Subpoena served", null, false), Ct)).Lock;
        };
        var halted = await h.RunAsync(deletion.DeletionId);
        halted.Status.Should().Be(WorkspaceDeletionStatus.Halted);
        halted.Step.Should().Be(DeletionStep.DatabasePurge);
        (await h.RowsAsync(ws)).Should().ContainKey("document", "nothing after the hold was destroyed");
        (await h.ObjectKeysAsync(ws)).Should().NotBeEmpty();
        (await h.Db.Core.ScalarAsync<long>(
            $"SELECT count(*) FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'DeletionHalted'")).Should().Be(1);
        (await h.RunAsync(deletion.DeletionId)).Status.Should().Be(WorkspaceDeletionStatus.Halted, "it waits while the hold is active");

        // Released: the run resumes where it stopped and finishes.
        (await h.Holds.RequestReleaseAsync(DeletionHarness.Principal(counsel), ws, midRun!.LockId, midRun.Version, "Subpoena withdrawn", Ct))
            .Status.Should().Be(PreservationLockStatus.Ok);
        var done = await h.RunAsync(deletion.DeletionId);
        done.Status.Should().Be(WorkspaceDeletionStatus.Completed, done.Error);
        (await h.RowsAsync(ws)).Keys.Except(Kept).Should().BeEmpty();
        (await h.Db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.preservation_lock WHERE workspace_id = @ws", ("ws", ws)))
            .Should().Be(2, "legal hold records are never deleted");
    }

    [Fact]
    public async Task The_fence_refuses_new_work_for_a_workspace_being_deleted_and_the_requester_cannot_approve()
    {
        await using var h = await DeletionHarness.CreateAsync(postgres);
        var ws = await h.Db.Core.CreateWorkspaceAsync();
        var other = await h.Db.Core.CreateWorkspaceAsync();
        var requester = await h.Db.CreateUserAsync();
        var name = await h.Db.Core.ScalarAsync<string>("SELECT name FROM opportunity.workspace WHERE workspace_id = @ws", ("ws", ws));
        var service = h.Service();

        (await service.RequestAsync(DeletionHarness.Principal(requester), ws,
            new WorkspaceDeletionRequest(null, "End of matter", null, "not the name"), Ct)).Status.Should().Be(WorkspaceDeletionResultStatus.Invalid);
        var requested = (await service.RequestAsync(DeletionHarness.Principal(requester), ws,
            new WorkspaceDeletionRequest(null, "End of matter", null, name), Ct)).Deletion!;
        requested.RetentionProfile.Should().Be(DeletionRetentionProfile.RetainRecords, "Q-23: records are retained by default");
        (await service.RequestAsync(DeletionHarness.Principal(requester), ws,
            new WorkspaceDeletionRequest(null, "Again", null, name), Ct)).Status.Should().Be(WorkspaceDeletionResultStatus.Conflict);
        (await service.ApproveAsync(DeletionHarness.Caller(requester, canApprove: true), requested.DeletionId, requested.Version, null, Ct))
            .Status.Should().Be(WorkspaceDeletionResultStatus.SecondPersonRequired, "Q-23: a second person approves");
        var approver = await h.Db.CreateUserAsync();
        var approved = (await service.ApproveAsync(DeletionHarness.Caller(approver, canApprove: true), requested.DeletionId, requested.Version, null, Ct))
            .Deletion!;
        (await service.CancelAsync(DeletionHarness.Caller(Guid.CreateVersion7()), approved.DeletionId, approved.Version, Ct))
            .Status.Should().Be(WorkspaceDeletionResultStatus.NotFound, "only the requester and approvers see a deletion");

        // Fence the workspace (the run's first step) and stop there.
        var coordinator = h.Coordinator();
        var options = h.Options;
        options.DrainSettleDelay = TimeSpan.FromHours(1);
        await coordinator.StepAsync(approved.DeletionId, Ct);
        (await h.Store.GetAsync(approved.DeletionId, Ct))!.Step.Should().Be(DeletionStep.Drain);
        (await service.CancelAsync(DeletionHarness.Caller(requester), approved.DeletionId, approved.Version + 1, Ct))
            .Status.Should().Be(WorkspaceDeletionResultStatus.Conflict, "a started run cannot be cancelled");
        (await h.Db.Core.ScalarAsync<long>("SELECT epoch FROM opportunity.workspace WHERE workspace_id = @ws", ("ws", ws))).Should().Be(2);

        foreach (var insert in new[]
        {
            "INSERT INTO opportunity.job (workspace_id, job_id, job_type, parameters, initiated_by) VALUES (@ws, gen_random_uuid(), 'BulkCoding', '{}', @user)",
            "INSERT INTO opportunity.document (workspace_id, document_id, control_number, control_number_norm, family_id) "
                + "SELECT @ws, d, 'LATE0001', 'LATE0001', d FROM gen_random_uuid() d",
        })
        {
            var write = async () => await h.Db.Core.ExecuteAsync(insert, ("ws", ws), ("user", requester));
            (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(WorkspaceFenceViolation.SqlState);
            await h.Db.Core.ExecuteAsync(insert, ("ws", other), ("user", requester));
        }
    }
}
