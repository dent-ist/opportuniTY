using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Import;
using Opportunity.Contracts.Import;
using Opportunity.Core.Coding;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Core.Security;
using Opportunity.Data.SearchWork;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Content;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T07 against PostgreSQL (app login, RLS on): Append / Overlay / Append-Overlay key collisions (row errors and the
/// pre-flight checker agree), DocumentVersion bumps with one IndexChunkTask per chunk and old/new values recorded for
/// audit, "blank values overwrite" and multi-value replace/merge, overlays concurrent with interactive coding, family
/// re-resolution, and a security-affecting overlay enforced by the content gateway before any index work ran (§24).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ImportOverlayTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Dat(params string[][] rows) => ImportHarness.Utf8Bom(ImportHarness.Dat(rows));

    [Fact]
    public async Task Append_refuses_existing_keys_overlay_refuses_missing_ones_and_pre_flight_reports_the_same_rows()
    {
        await using var h = await ImportHarness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();
        (await h.RunAsync(await h.StartAsync(ws, Dat(["BEGDOC", "CUSTODIAN"], ["K-1", "Smith"], ["K-2", "Doe"])))).Status.Should().Be(JobStatus.Completed);
        var checker = new ImportKeyCollisionChecker(h.Batches);
        ImportKeyProbe[] probes = [new(1, "K-2", "K-2"), new(2, "K-3", "K-3")];

        var append = await h.StartAsync(ws, Dat(["BEGDOC", "CUSTODIAN"], ["K-2", "Again"], ["K-3", "New"]));
        (await h.RunAsync(append)).Status.Should().Be(JobStatus.CompletedWithErrors);
        (await h.IssuesAsync(append)).Select(e => (e.RowNo, e.Code)).Should().Equal((1L, ImportKeyRules.KeyExists));
        (await h.BatchAsync(append)).RowsImported.Should().Be(1);
        var appendCheck = await checker.CheckAsync(ws, ImportMode.Append, OverlaySettings.ControlNumberKey, [new(1, "K-2", "K-2"), new(2, "K-4", "K-4")], Ct);
        appendCheck.Collisions.Select(c => (c.RowNo, c.Issue.Code)).Should().Equal((1L, ImportKeyRules.KeyExists));
        (appendCheck.WillCreate, appendCheck.AlreadyExists).Should().Be((1L, 1L));

        var overlay = await h.StartAsync(ws, Dat(["BEGDOC", "CUSTODIAN"], ["K-1", "Smith-Jones"], ["K-9", "Nobody"]), mode: ImportMode.Overlay);
        (await h.RunAsync(overlay)).Status.Should().Be(JobStatus.CompletedWithErrors);
        (await h.IssuesAsync(overlay)).Select(e => (e.RowNo, e.Code)).Should().Equal((2L, ImportKeyRules.KeyMissing));
        var overlayCheck = await checker.CheckAsync(ws, ImportMode.Overlay, OverlaySettings.ControlNumberKey, [new(1, "K-1", "K-1"), new(2, "K-9", "K-9")], Ct);
        overlayCheck.Collisions.Select(c => (c.RowNo, c.Issue.Code)).Should().Equal((2L, ImportKeyRules.KeyMissing));
        (overlayCheck.WillUpdate, overlayCheck.NotFound).Should().Be((1L, 1L));

        // Append-Overlay does both, with no collision errors.
        var both = await h.StartAsync(ws, Dat(["BEGDOC", "CUSTODIAN"], ["K-1", "Smith"], ["K-5", "New"]), mode: ImportMode.AppendOverlay);
        (await h.RunAsync(both)).Status.Should().Be(JobStatus.Completed);
        var report = await h.BatchAsync(both);
        (report.RowsImported, report.RowsOverlaid, report.RowsErrored).Should().Be((1L, 1L, 0L));
        (await checker.CheckAsync(ws, ImportMode.AppendOverlay, OverlaySettings.ControlNumberKey, probes, Ct)).Collisions.Should().BeEmpty();
        await checker.Invoking(c => c.CheckAsync(ws, ImportMode.Overlay, "Custodian", probes, Ct))
            .Should().ThrowAsync<ArgumentException>().WithMessage($"{ImportKeyRules.KeyNotUnique}*");
    }

    [Fact]
    public async Task Overlays_bump_versions_reindex_record_old_and_new_values_and_honour_blank_and_multi_value_options()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 10);
        var ws = await h.WorkspaceAsync();
        var fields = h.Db.Fields;
        var tags = (await fields.CreateFieldAsync(new NewField(ws, "Tags", FieldType.MultiChoice, FieldStorage.Metadata, IsMultiValue: true), Ct)).Value!;
        var hot = (await fields.AddChoiceAsync(ws, tags.FieldId, "Hot", Ct)).Value!.ChoiceId;
        var cold = (await fields.AddChoiceAsync(ws, tags.FieldId, "Cold", Ct)).Value!.ChoiceId;
        var issues = (await fields.CreateFieldAsync(new NewField(ws, "Issues", FieldType.MultiChoice, FieldStorage.Coding, IsMultiValue: true), Ct)).Value!;
        var pricing = (await fields.AddChoiceAsync(ws, issues.FieldId, "Pricing", Ct)).Value!.ChoiceId;
        var supply = (await fields.AddChoiceAsync(ws, issues.FieldId, "Supply", Ct)).Value!.ChoiceId;
        var tagsKey = FieldKey.For(tags.FieldId);
        ColumnMapping Tags() => new() { Column = "TAGS", Targets = [new MappingTarget { Kind = MappingTargetKind.Field, FieldId = tags.FieldId, FieldName = tags.Name }] };

        var load = await h.StartAsync(ws, Dat(["BEGDOC", "CUSTODIAN", "FILESIZE", "TAGS"], ["OV-1", "Smith", "10", "Hot"], ["OV-2", "Doe", "20", "Hot"]),
            new ImportProfileDefinition { Columns = [Tags()] });
        (await h.RunAsync(load)).Status.Should().Be(JobStatus.Completed);
        var docs = await h.DocumentsAsync(ws);
        var ov1 = docs["OV-1"].DocumentId;
        var custodianKey = FieldKey.For((await fields.GetCatalogAsync(ws, cancellationToken: Ct)).Fields.Single(f => f.Name == "Custodian").FieldId);

        // A reviewer codes OV-1 first; overlays without coding fields never touch it.
        (await h.Db.Coding.ApplyAsync(Write(ws, ov1, CodingFieldOperation.Set(issues.FieldId, new JsonArray(pricing))), Ct)).Outcome
            .Should().Be(CodingWriteOutcome.Applied);
        var coded = (await h.DocumentsAsync(ws))["OV-1"].Version;

        // Default options: blanks leave existing values, multi-value replaces.
        var first = await h.StartAsync(ws, Dat(["BEGDOC", "CUSTODIAN", "FILESIZE", "TAGS"], ["OV-1", "Smith-Jones", "", "Cold"], ["OV-2", "Doe", "20", "Hot"]),
            new ImportProfileDefinition { Columns = [Tags()] }, ImportMode.Overlay);
        (await h.RunAsync(first)).Status.Should().Be(JobStatus.Completed);
        var report = await h.BatchAsync(first);
        (report.RowsOverlaid, report.RowsSkipped).Should().Be((1L, 1L));
        var after = await h.DocumentsAsync(ws);
        after["OV-1"].Version.Should().Be(coded + 1);
        after["OV-2"].Version.Should().Be(docs["OV-2"].Version, "identical values change nothing");
        var meta = JsonNode.Parse(after["OV-1"].Metadata)!;
        meta[custodianKey]!.GetValue<string>().Should().Be("Smith-Jones");
        ChoiceIds(meta[tagsKey]).Should().Equal(cold);
        (await h.Db.ScalarAsync<long>("SELECT file_size FROM opportunity.document WHERE workspace_id = @ws AND document_id = @d", ("ws", ws), ("d", ov1)))
            .Should().Be(10, "a blank value leaves the existing value");
        ChoiceIds((await h.Db.Coding.GetCurrentAsync(ws, [ov1], Ct)).Single().Fields.Single().Value).Should().Equal(pricing);

        // Reindexed through the chunk's one IndexChunkTask (§21), which resolves to the changed document only.
        (await h.IndexTasksPerChunkAsync(first)).Values.Should().Equal(1L);
        (await new IndexTaskMembershipReader(h.Db.AppDataSource).ReadPageAsync(ws, ChunkMembership.ImportRows(first.ImportBatchId, 1, 2), null, 100, Ct))
            .Should().Equal(ov1);

        // Old and new values of the changed keys, with the version the overlay created; audit references them.
        var events = await OverlayEventsAsync(h, ws, first.ImportBatchId);
        events.Should().ContainSingle();
        var (row, version, changes) = events[0];
        (row, version).Should().Be((1L, coded + 1));
        changes[custodianKey]!["old"]!.GetValue<string>().Should().Be("Smith");
        changes[custodianKey]!["new"]!.GetValue<string>().Should().Be("Smith-Jones");
        ChoiceIds(changes[tagsKey]!["old"]).Should().Equal(hot);
        ChoiceIds(changes[tagsKey]!["new"]).Should().Equal(cold);
        changes.AsObject().Select(p => p.Key).Should().BeEquivalentTo([custodianKey, tagsKey]);
        (await h.Db.ColumnAsync(
            $"SELECT details->>'Documents' || ':' || (details->>'Changed') FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Import' AND action = 'Overlaid' AND job_id = '{first.JobId}'"))
            .Should().Equal($"1:{string.Join(',', new[] { custodianKey, tagsKey }.Order(StringComparer.Ordinal))}");

        // "Clear existing values", multi-value merge and an enabled coding field merged with the reviewer's choice.
        var codingColumn = new ColumnMapping { Column = "ISSUES", Targets = [new MappingTarget { Kind = MappingTargetKind.Field, FieldId = issues.FieldId, FieldName = issues.Name }] };
        var second = await h.StartAsync(ws, Dat(["BEGDOC", "CUSTODIAN", "FILESIZE", "TAGS", "ISSUES"], ["OV-1", "", "", "Hot", "Supply"]),
            new ImportProfileDefinition
            {
                Overlay = new OverlaySettings { BlankValuesOverwrite = true, MultiValue = OverlayMultiValue.Merge },
                Columns = [Tags(), codingColumn],
            },
            ImportMode.Overlay, codingFields: [issues.FieldId]);
        (await h.RunAsync(second)).Status.Should().Be(JobStatus.Completed);
        meta = JsonNode.Parse((await h.DocumentsAsync(ws))["OV-1"].Metadata)!;
        meta[custodianKey].Should().BeNull("the blank cleared the custodian");
        ChoiceIds(meta[tagsKey]).Should().BeEquivalentTo([hot, cold]);
        (await h.Db.ScalarAsync<long>("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws AND document_id = @d AND file_size IS NULL", ("ws", ws), ("d", ov1)))
            .Should().Be(1, "the blank cleared the file size");
        ChoiceIds((await h.Db.Coding.GetCurrentAsync(ws, [ov1], Ct)).Single().Fields.Single().Value).Should().BeEquivalentTo([pricing, supply]);
        var cleared = (await OverlayEventsAsync(h, ws, second.ImportBatchId)).Single().Changes;
        cleared[custodianKey]!["new"].Should().BeNull();
        cleared["file_size"]!["old"]!.GetValue<long>().Should().Be(10);
    }

    [Fact]
    public async Task An_overlay_concurrent_with_interactive_coding_loses_neither_change()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 5);
        var ws = await h.WorkspaceAsync();
        var responsive = (await h.Db.Fields.CreateFieldAsync(new NewField(ws, "Responsive", FieldType.SingleChoice, FieldStorage.Coding), Ct)).Value!;
        var yes = (await h.Db.Fields.AddChoiceAsync(ws, responsive.FieldId, "Yes", Ct)).Value!.ChoiceId;
        var numbers = Enumerable.Range(1, 20).Select(i => $"CC-{i:D3}").ToList();
        (await h.RunAsync(await h.StartAsync(ws, Dat([["BEGDOC", "CUSTODIAN"], .. numbers.Select(n => new[] { n, "Before" })])))).Status.Should().Be(JobStatus.Completed);
        var docs = await h.DocumentsAsync(ws);
        var custodianKey = FieldKey.For((await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct)).Fields.Single(f => f.Name == "Custodian").FieldId);

        var overlay = await h.StartAsync(ws, Dat([["BEGDOC", "CUSTODIAN"], .. numbers.Select(n => new[] { n, "After" })]), mode: ImportMode.Overlay);
        await h.PrepareAsync(overlay);

        // The first document is held by an in-flight transaction, so both writers queue on its row lock.
        var first = docs[numbers[0]].DocumentId;
        await using var holder = await h.Db.DataSource.OpenConnectionAsync(Ct);
        await using var hold = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new Npgsql.NpgsqlCommand(
            "SELECT 1 FROM opportunity.document_projection_state WHERE workspace_id = @ws AND document_id = @d FOR UPDATE", holder, hold))
        {
            lockRow.Parameters.AddWithValue("ws", ws);
            lockRow.Parameters.AddWithValue("d", first);
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        var coding = Task.Run(async () =>
        {
            foreach (var n in numbers)
            {
                var result = await h.Db.Coding.ApplyAsync(Write(ws, docs[n].DocumentId, CodingFieldOperation.Set(responsive.FieldId, JsonValue.Create(yes))), Ct);
                result.Outcome.Should().Be(CodingWriteOutcome.Applied);
            }
        }, Ct);
        var import = Task.Run(() => h.DeliverOpenChunksAsync(overlay), Ct);
        await Task.Delay(300, Ct);
        await hold.CommitAsync(Ct);
        await Task.WhenAll(coding, import);

        (await h.Jobs.GetAsync(ws, overlay.JobId, Ct))!.Status.Should().Be(JobStatus.Completed);
        var after = await h.DocumentsAsync(ws);
        var current = (await h.Db.Coding.GetCurrentAsync(ws, [.. numbers.Select(n => docs[n].DocumentId)], Ct)).ToDictionary(c => c.DocumentId);
        foreach (var n in numbers)
        {
            JsonNode.Parse(after[n].Metadata)![custodianKey]!.GetValue<string>().Should().Be("After", n);
            current[docs[n].DocumentId].Fields.Single().Value!.GetValue<int>().Should().Be(yes, n);
            after[n].Version.Should().Be(docs[n].Version + 2, "each writer bumped the version once");
        }

        (await h.CountAsync("SELECT count(*) FROM opportunity.coding_event WHERE workspace_id = @ws", ws)).Should().Be(20);
        (await h.CountAsync("SELECT count(*) FROM opportunity.document_overlay_event WHERE workspace_id = @ws", ws)).Should().Be(20);
    }

    [Fact]
    public async Task Overlays_that_supply_family_members_or_sources_re_resolve_the_families()
    {
        await using var h = await ImportHarness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();
        string[] header = ["BEGDOC", "BegAttach", "EndAttach", "ParentID"];
        var first = await h.StartAsync(ws, Dat(header, ["P-1", "P-1", "P-3", ""], ["P-3", "P-1", "P-3", ""], ["C-1", "", "", ""], ["C-2", "", "", ""]));
        (await h.RunAsync(first)).Status.Should().Be(JobStatus.Completed);
        (await h.Batches.GetFamilyIssuesAsync(ws, first.ImportBatchId, null, 10, Ct)).Select(i => (i.ControlNumber, i.Kind))
            .Should().Equal(("P-1", FamilyIssueKind.RangeGap));

        // Append-Overlay supplies the missing member P-2; the family's documents are overlaid unchanged.
        var second = await h.StartAsync(ws, Dat(header, ["P-1", "P-1", "P-3", ""], ["P-2", "P-1", "P-3", ""]), mode: ImportMode.AppendOverlay);
        (await h.RunAsync(second)).Status.Should().Be(JobStatus.Completed);
        (await h.Batches.GetFamilyIssuesAsync(ws, first.ImportBatchId, null, 10, Ct)).Should().BeEmpty();
        var families = await FamiliesAsync(h, ws);
        families["P-2"].Should().Be(("P-1", FamilyStatus.Resolved));
        families["P-3"].Should().Be(("P-1", FamilyStatus.Resolved));

        // Overlay adds a parent link between two existing documents.
        var third = await h.StartAsync(ws, Dat(["BEGDOC", "ParentID"], ["C-2", "C-1"]), mode: ImportMode.Overlay);
        (await h.RunAsync(third)).Status.Should().Be(JobStatus.Completed);
        (await h.BatchAsync(third)).RowsOverlaid.Should().Be(1);
        (await FamiliesAsync(h, ws))["C-2"].Should().Be(("C-1", FamilyStatus.Resolved));
    }

    /// <summary>
    /// §24 / Q-31: an admin-enabled overlay of the confidentiality designation recomputes the document's restriction
    /// classes in the chunk's own transaction, so the gateway denies a reviewer while the index task is still pending.
    /// Covered: restriction classes derived from coding-storage security-affecting fields through
    /// <see cref="IRestrictionClassBinding"/> (a stand-in for E05-T06's binding); full document-level security is #51.
    /// </summary>
    [Fact]
    public async Task A_security_affecting_overlay_is_enforced_before_index_completion()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var core = db.Security.Core;
        var ws = await core.CreateWorkspaceAsync();
        var w = await CodingApiHarness.WorkspaceAsync(core, ws);
        var reviewer = await CodingApiHarness.MemberAsync(core, ws, WorkspaceRole.Reviewer);
        var privilege = await CodingApiHarness.MemberAsync(core, ws, WorkspaceRole.PrivilegeReviewer);
        var doc = (await db.DocumentAsync(ws)).DocumentId;
        var controlNumber = (await core.ColumnAsync($"SELECT control_number FROM opportunity.document WHERE workspace_id = '{ws}' AND document_id = '{doc}'")).Single();
        await using var h = ImportHarness.Over(core, restrictions: new ConfidentialityBinding());
        h.Store = db.Store;
        await using var factory = CodingApiHarness.Factory(core.AppConnectionString,
            b =>
            {
                b.UseSetting("ObjectStorage:Provider", "FileSystem");
                b.UseSetting("ObjectStorage:FileSystem:RootPath", db.StoreRoot);
            },
            s => s.AddSingleton<IRestrictionClassBinding, ConfidentialityBinding>());
        using var client = factory.CreateClient();
        var text = $"/api/v1/workspaces/{ws}/documents/{doc}/text";
        (await CodingApiHarness.GetAsync(client, text, reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);

        var profile = new ImportProfileDefinition
        {
            Columns = [new ColumnMapping { Column = "CONF", Targets = [new MappingTarget { Kind = MappingTargetKind.Field, FieldId = w.Confidentiality, FieldName = "Confidentiality" }] }],
        };
        var overlay = await h.StartAsync(ws, Dat(["BEGDOC", "CONF"], [controlNumber, "Attorneys' Eyes Only"]), profile, ImportMode.Overlay, codingFields: [w.Confidentiality]);
        (await h.RunAsync(overlay)).Status.Should().Be(JobStatus.Completed);

        (await core.ColumnAsync($"SELECT class_key FROM opportunity.document_restriction WHERE workspace_id = '{ws}' AND document_id = '{doc}'"))
            .Should().Equal("AttorneysEyesOnly");
        var task = (await core.ColumnAsync(
            $"SELECT status::text || ':' || change_mask::text || ':' || lane::text FROM opportunity.index_chunk_task WHERE workspace_id = '{ws}' AND job_id = '{overlay.JobId}'")).Single();
        task.Should().NotStartWith(((short)IndexChunkTaskStatus.Applied).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":", "no index work ran");
        (short.Parse(task.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture) & (short)SearchChangeMask.Security).Should().NotBe(0);
        task.Split(':')[2].Should().Be("3", "security-affecting index work takes the SecurityBulk lane");

        await (await CodingApiHarness.GetAsync(client, text, reviewer)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        (await CodingApiHarness.GetAsync(client, text, privilege)).StatusCode.Should().Be(HttpStatusCode.OK, "AEO is granted to Privilege Reviewers");
        (await core.ColumnAsync(
            $"SELECT details->>'RestrictionClasses.Added' FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Coding' AND action = 'BulkChunkApplied'"))
            .Should().Equal("AttorneysEyesOnly");
    }

    private static CodingWriteRequest Write(Guid ws, Guid documentId, CodingFieldOperation operation) => new()
    {
        WorkspaceId = ws,
        IdempotencyKey = "test:" + Guid.NewGuid().ToString("N"),
        Actor = new CodingActor(ImportHarness.User, CodingActorType.Human),
        Documents = [new CodingTarget(documentId)],
        Operations = [operation],
    };

    private static List<int> ChoiceIds(JsonNode? value) => [.. FieldValues.ChoiceIds(value)];

    private static async Task<List<(long Row, long Version, JsonNode Changes)>> OverlayEventsAsync(ImportHarness h, Guid ws, Guid batch)
    {
        var result = new List<(long, long, JsonNode)>();
        await using var command = h.Db.DataSource.CreateCommand(
            "SELECT row_no, document_version, changes::text FROM opportunity.document_overlay_event WHERE workspace_id = @ws AND import_batch_id = @b ORDER BY row_no");
        command.Parameters.AddWithValue("ws", ws);
        command.Parameters.AddWithValue("b", batch);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result.Add((reader.GetInt64(0), reader.GetInt64(1), JsonNode.Parse(reader.GetString(2))!));
        }

        return result;
    }

    private static async Task<Dictionary<string, (string Family, FamilyStatus Status)>> FamiliesAsync(ImportHarness h, Guid ws)
    {
        var result = new Dictionary<string, (string, FamilyStatus)>(StringComparer.Ordinal);
        await using var command = h.Db.DataSource.CreateCommand(
            """
            SELECT d.control_number, f.control_number, d.family_status FROM opportunity.document d
            JOIN opportunity.document f ON f.workspace_id = d.workspace_id AND f.document_id = d.family_id
            WHERE d.workspace_id = @ws
            """);
        command.Parameters.AddWithValue("ws", ws);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result[reader.GetString(0)] = (reader.GetString(1), (FamilyStatus)reader.GetInt16(2));
        }

        return result;
    }
}
