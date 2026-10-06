using AwesomeAssertions;
using Opportunity.Application.Audit;
using Opportunity.Application.Security;
using Opportunity.Contracts.Import;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Data.Security;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E05-T06 / ADR-015 D6.2: a wall scoped to a custodian covers the documents an import adds or overlays with that
/// custodian in the import chunk's own transaction, and an overlay that changes coverage goes on the security lane.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ImportWallCoverageTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_custodian_wall_covers_imported_and_overlaid_documents_in_the_chunk_transaction()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 10);
        var ws = await h.WorkspaceAsync();
        var store = new DocumentSecurityStore(h.Db.AppDataSource);
        var created = await store.CreateWallAsync(ws, Guid.CreateVersion7(),
            new EthicalWallDefinition("Matter B", null, [Guid.CreateVersion7()], [], [], ["doe"], []), Guid.CreateVersion7(), Audit(ws), Ct);
        created.Status.Should().Be(SecurityWriteStatus.Created);
        var wallId = created.Value!.WallId;

        var append = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(
            ["BEGDOC", "CUSTODIAN"], ["WC-1", "Smith"], ["WC-2", " DOE "], ["WC-3", "Roe"])));
        (await h.RunAsync(append)).Status.Should().Be(JobStatus.Completed);
        (await Covered(h, ws, wallId)).Should().Equal("WC-2");

        var overlay = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "CUSTODIAN"], ["WC-1", "Doe"], ["WC-2", "Smith"])),
            mode: ImportMode.Overlay);
        (await h.RunAsync(overlay)).Status.Should().Be(JobStatus.Completed);
        (await Covered(h, ws, wallId)).Should().Equal("WC-1");
        var mask = (SearchChangeMask)await h.Db.ScalarAsync<short>(
            "SELECT change_mask FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND job_id = @job", ("ws", ws), ("job", overlay.JobId));
        mask.Should().HaveFlag(SearchChangeMask.Security, "coverage of existing documents changed: the priority lane re-projects them");
    }

    private static Task<List<string>> Covered(ImportHarness h, Guid ws, Guid wallId) => h.Db.ColumnAsync(
        $"""
        SELECT d.control_number FROM opportunity.document_wall w
        JOIN opportunity.document d ON d.workspace_id = w.workspace_id AND d.document_id = w.document_id
        WHERE w.workspace_id = '{ws}' AND w.wall_id = '{wallId}' ORDER BY 1
        """);

    private static AuditEvent Audit(Guid ws) => new()
    {
        WorkspaceId = ws,
        OccurredAt = DateTimeOffset.UtcNow,
        Category = AuditTaxonomy.Security.Category,
        Action = AuditTaxonomy.Security.WallCreated,
        ActorType = AuditActorType.User,
        ActorId = Guid.CreateVersion7().ToString(),
        ActorDisplay = "Test Admin",
        Outcome = AuditOutcome.Success,
    };
}
