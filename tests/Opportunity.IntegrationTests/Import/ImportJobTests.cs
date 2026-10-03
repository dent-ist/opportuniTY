using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Fields;
using Opportunity.Application.Import;
using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Import.Jobs;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T03 against PostgreSQL (app login, RLS on) and a file-system object store: preparation, chunked writes with
/// exactly one IndexChunkTask per committed chunk (no SearchOutbox rows), row-level errors, whole-file duplicates,
/// fields and choices created at load time, raw values, overlay with the Q-31 coding-field switch.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ImportJobTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] FillerHeader = ["BEGDOC", "CUSTODIAN", "NOTES"];

    private static readonly string[] MessyHeader = ["BEGDOC", "ENDDOC", "CUSTODIAN", "DATESENT", "TIMESENT", "FILESIZE", "TAGS", "NOTES", "PARENTID"];

    /// <summary>
    /// Ten rows in Windows-1252 (no byte-order mark) with the usual problems of real volumes: a bad date, a short row, a
    /// duplicate number in a later chunk (other case), a blank number, a bad size, a number that exists already and a
    /// retired one; plus a non-ASCII custodian, a newline character inside a value and a self-referencing family field.
    /// </summary>
    private static byte[] MessyDat() => Encoding.Latin1.GetBytes(ImportHarness.Dat(
        MessyHeader,
        ["DOC-0001", "DOC-0001", "Smith", "07/04/2019", "09:30 AM", "1,024", "Hot; Key", "first note", "DOC-0001"],
        ["DOC-0002", "DOC-0002", "Doe", "13/45/2019", "", "10", "", "", ""],
        ["DOC-0003", "DOC-0003", "Short row"],
        ["doc-0001", "doc-0001", "Smith", "", "", "", "", "duplicate in a later chunk", ""],
        ["", "", "Nobody", "", "", "", "", "blank control number", ""],
        ["DOC-0006", "DOC-0006", "Smith", "2019-07-05T10:00:00Z", "", "", "Hot", "", "DOC-0001"],
        ["DOC-0007", "DOC-0007", "Müller", "", "", "2048", "", "line one®line two", ""],
        ["DOC-0008", "DOC-0008", "Doe", "", "", "abc", "", "", ""],
        ["DOC-0100", "DOC-0100", "Doe", "", "", "", "", "exists already", ""],
        ["DOC-0200", "DOC-0200", "Doe", "", "", "", "", "retired", ""]));

    private static ImportProfileDefinition MessyProfile() => new()
    {
        Parsing = new ParsingDefaults { SourceTimeZone = "America/New_York" },
        UnmappedColumns = UnmappedColumnPolicy.CreateTextField,
        Columns =
        [
            new ColumnMapping
            {
                Column = "TAGS",
                Targets = [new MappingTarget { Kind = MappingTargetKind.NewField, NewField = new NewFieldSpec { Name = "Issues", Type = ImportFieldType.MultiChoice } }],
            },
        ],
    };

    [Fact]
    public async Task A_messy_load_file_imports_its_good_rows_in_chunks_and_reports_every_bad_row()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 3);
        var ws = await h.WorkspaceAsync();
        await h.Db.InsertDocumentAsync(ws, "DOC-0100");
        await h.Db.ExecuteAsync(
            "INSERT INTO opportunity.retired_control_number (workspace_id, control_number_norm, control_number, document_id) VALUES (@ws, 'DOC-0200', 'DOC-0200', @doc)",
            ("ws", ws), ("doc", Guid.CreateVersion7()));

        var batch = await h.StartAsync(ws, MessyDat(), MessyProfile());
        var job = await h.RunAsync(batch);

        job.Status.Should().Be(JobStatus.CompletedWithErrors, "rows with errors make the job complete with errors");
        job.Counters.ChunksTotal.Should().Be(4);
        job.Counters.ItemsApplied.Should().Be(3);
        job.Counters.ItemsFailed.Should().Be(7);

        var report = await h.BatchAsync(batch);
        report.Preparation!.RowsTotal.Should().Be(10);
        report.Preparation.DatEncoding.Should().Be("Windows1252");
        report.Preparation.FieldsCreated.Should().Be(3, "Custodian (well-known), Issues (profile) and NOTES (unmapped column policy)");
        report.Preparation.ChoicesCreated.Should().Be(2);
        (report.RowsImported, report.RowsOverlaid, report.RowsSkipped, report.RowsErrored).Should().Be((3L, 0L, 0L, 7L));
        report.CompletedAt.Should().NotBeNull();

        var errors = await h.IssuesAsync(batch);
        errors.Select(e => (e.RowNo, e.Code)).Should().Equal(
            (2L, "invalid-date"),
            (3L, "field-count-mismatch"),
            (4L, "duplicate-control-number"),
            (5L, "control-number-missing"),
            (8L, "invalid-integer"),
            (9L, "control-number-exists"),
            (10L, "control-number-retired"));
        errors.Single(e => e.RowNo == 4).Message.Should().Contain("row 1");
        errors.Single(e => e.RowNo == 2).Column.Should().Be("DATESENT");
        errors.Single(e => e.RowNo == 2).LineNo.Should().Be(3);
        errors.Single(e => e.RowNo == 9).ControlNumber.Should().Be("DOC-0100");

        // Fields and choices created at load time; values canonical, originals kept raw (ADR-003 R9).
        var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        var custodian = catalog.Fields.Single(f => f.Name == "Custodian");
        var issues = catalog.Fields.Single(f => f.Name == "Issues");
        var notes = catalog.Fields.Single(f => f.Name == "NOTES");
        issues.Type.Should().Be(FieldType.MultiChoice);
        var hot = catalog.ChoicesOf(issues.FieldId).Single(c => c.Name == "Hot").ChoiceId;
        var key = catalog.ChoicesOf(issues.FieldId).Single(c => c.Name == "Key").ChoiceId;

        var documents = await h.DocumentsAsync(ws);
        documents.Keys.Should().BeEquivalentTo(["DOC-0001", "DOC-0006", "DOC-0007", "DOC-0100"]);
        var first = JsonNode.Parse(documents["DOC-0001"].Metadata)!;
        first[FieldKey.For(custodian.FieldId)]!.GetValue<string>().Should().Be("Smith");
        first[FieldKey.For(issues.FieldId)]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(new[] { hot, key }.Order());
        var raw = JsonNode.Parse(documents["DOC-0001"].Raw!)!;
        raw[FieldKey.For(SystemFields.DateSent)]!["raw"]!.GetValue<string>().Should().Be("07/04/2019 09:30 AM");
        raw[FieldKey.For(SystemFields.DateSent)]!["tz"]!.GetValue<string>().Should().Be("America/New_York");
        raw[FieldKey.For(SystemFields.FileSize)]!["raw"]!.GetValue<string>().Should().Be("1,024");
        raw["s:ParentId"].Should().BeNull("a parent equal to the document itself means no parent (Q-56)");
        JsonNode.Parse(documents["DOC-0006"].Raw!)!["s:ParentId"]!["raw"]!.GetValue<string>().Should().Be("DOC-0001");
        var seventh = JsonNode.Parse(documents["DOC-0007"].Metadata)!;
        seventh[FieldKey.For(custodian.FieldId)]!.GetValue<string>().Should().Be("Müller");
        seventh[FieldKey.For(notes.FieldId)]!.GetValue<string>().Should().Be("line one\nline two");
        (await h.Db.ScalarAsync<DateTime>("SELECT date_sent FROM opportunity.document WHERE workspace_id = @ws AND control_number = 'DOC-0001'", ("ws", ws)))
            .Should().Be(new DateTime(2019, 7, 4, 13, 30, 0, DateTimeKind.Utc));
        (await h.Db.ScalarAsync<long>("SELECT file_size FROM opportunity.document WHERE workspace_id = @ws AND control_number = 'DOC-0001'", ("ws", ws)))
            .Should().Be(1024);

        // §12/§21: one IndexChunkTask per committed chunk, never SearchOutbox rows; members name the created documents.
        var tasks = await h.IndexTasksPerChunkAsync(batch);
        tasks.Should().HaveCount(4).And.OnlyContain(t => t.Value == 1);
        (await h.CountAsync("SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws", ws)).Should().Be(0);
        (await h.CountAsync("SELECT count(*) FROM opportunity.import_batch_member WHERE workspace_id = @ws AND action = 1", ws)).Should().Be(3);
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws AND first_import_batch_id IS NOT NULL", ws)).Should().Be(3);
        (await h.CountAsync("SELECT count(*) FROM opportunity.job_chunk_item_result WHERE workspace_id = @ws AND kind = 3", ws)).Should().Be(7);

        // Import.* audit (closed taxonomy, #115): started with the job, completed with the last chunk.
        (await h.Db.ColumnAsync($"SELECT category || '.' || action FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Import' ORDER BY recorded_at"))
            .Should().Equal("Import.Started", "Import.Completed");
    }

    [Fact]
    public async Task Duplicates_are_judged_across_the_whole_file_whichever_chunk_they_land_in()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 2);
        var ws = await h.WorkspaceAsync();
        string[] header = ["BEGDOC", "CUSTODIAN"];
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(
            header, ["A-1", "x"], ["A-2", "x"], ["A-3", "x"], [" a-2 ", "late copy"], ["A-5", "x"], ["A-1", "third chunk copy"], ["A-7", "x"]));

        var batch = await h.StartAsync(ws, dat);
        await h.PrepareAsync(batch);

        // Deliver the chunks in reverse: the earliest row of a number still wins.
        foreach (var chunk in (await h.Jobs.GetChunksAsync(ws, batch.JobId, cancellationToken: Ct)).OrderByDescending(c => c.Sequence))
        {
            await h.Consumer().HandleAsync(ImportHarness.Payload(chunk), ImportHarness.Received(chunk), Ct);
        }

        var errors = await h.IssuesAsync(batch);
        errors.Select(e => (e.RowNo, e.Code)).Should().Equal((4L, "duplicate-control-number"), (6L, "duplicate-control-number"));
        errors[0].Message.Should().Contain("row 2");
        errors[1].Message.Should().Contain("row 1");
        (await h.DocumentsAsync(ws)).Keys.Should().BeEquivalentTo(["A-1", "A-2", "A-3", "A-5", "A-7"]);
        JsonNode.Parse((await h.DocumentsAsync(ws))["A-1"].Metadata)!.ToJsonString().Should().NotContain("third chunk copy");

        // A second load of the same numbers in Append mode: every row collides with an existing document.
        var again = await h.StartAsync(ws, dat);
        await h.RunAsync(again);
        (await h.BatchAsync(again)).RowsErrored.Should().Be(7);
        (await h.IssuesAsync(again)).Select(e => e.Code).Distinct().Should().BeEquivalentTo(["control-number-exists", "duplicate-control-number"]);
    }

    [Fact]
    public async Task Overlay_updates_only_supplied_values_and_loads_admin_enabled_coding_fields_through_the_coding_store()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 10);
        var ws = await h.WorkspaceAsync();
        var responsive = (await h.Db.Fields.CreateFieldAsync(new NewField(ws, "Responsive", FieldType.SingleChoice, FieldStorage.Coding), Ct)).Value!;
        var yes = (await h.Db.Fields.AddChoiceAsync(ws, responsive.FieldId, "Yes", Ct)).Value!;
        await h.Db.Fields.AddChoiceAsync(ws, responsive.FieldId, "No", Ct);

        var append = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(
            ["BEGDOC", "CUSTODIAN", "FILESIZE"], ["OV-1", "Smith", "10"], ["OV-2", "Doe", "20"], ["OV-3", "Roe", "30"])));
        (await h.RunAsync(append)).Status.Should().Be(JobStatus.Completed);
        var before = await h.DocumentsAsync(ws);
        var custodian = (await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct)).Fields.Single(f => f.Name == "Custodian");

        var overlayDat = ImportHarness.Utf8Bom(ImportHarness.Dat(
            ["BEGDOC", "CUSTODIAN", "FILESIZE", "RESPONSIVE"],
            ["OV-1", "Smith-Jones", "10", "Yes"],
            ["OV-2", "Doe", "20", ""],
            ["OV-3", "", "31", ""],
            ["OV-9", "Nobody", "1", ""]));
        var codingProfile = new ImportProfileDefinition
        {
            Columns =
            [
                new ColumnMapping
                {
                    Column = "RESPONSIVE",
                    Targets = [new MappingTarget { Kind = MappingTargetKind.Field, FieldId = responsive.FieldId, FieldName = responsive.Name }],
                },
            ],
        };

        // Q-31: a coding field is not loadable unless the import enables it.
        var refused = () => h.StartAsync(ws, overlayDat, codingProfile, ImportMode.Overlay);
        await refused.Should().ThrowAsync<InvalidOperationException>().WithMessage("*coding or privilege field*");

        var overlay = await h.StartAsync(ws, overlayDat, codingProfile, ImportMode.Overlay, codingFields: [responsive.FieldId]);
        (await h.RunAsync(overlay)).Status.Should().Be(JobStatus.CompletedWithErrors);

        var report = await h.BatchAsync(overlay);
        (report.RowsImported, report.RowsOverlaid, report.RowsSkipped, report.RowsErrored).Should().Be((0L, 2L, 1L, 1L));
        (await h.IssuesAsync(overlay)).Select(e => (e.RowNo, e.Code)).Should().Equal((4L, "overlay-key-not-found"));

        var after = await h.DocumentsAsync(ws);
        after.Keys.Should().BeEquivalentTo(["OV-1", "OV-2", "OV-3"]);
        JsonNode.Parse(after["OV-1"].Metadata)![FieldKey.For(custodian.FieldId)]!.GetValue<string>().Should().Be("Smith-Jones");
        JsonNode.Parse(after["OV-3"].Metadata)![FieldKey.For(custodian.FieldId)]!.GetValue<string>().Should().Be("Roe", "a blank value never overwrites");
        (await h.Db.ScalarAsync<long>("SELECT file_size FROM opportunity.document WHERE workspace_id = @ws AND control_number = 'OV-3'", ("ws", ws))).Should().Be(31);
        after["OV-2"].Version.Should().Be(before["OV-2"].Version, "identical values change nothing");
        after["OV-3"].Version.Should().Be(before["OV-3"].Version + 1);
        after["OV-1"].Version.Should().BeGreaterThan(before["OV-1"].Version);

        var coding = await h.Db.Coding.GetCurrentAsync(ws, [after["OV-1"].DocumentId], Ct);
        coding.Single().Fields.Single(f => f.FieldId == responsive.FieldId).Value!.GetValue<int>().Should().Be(yes.ChoiceId);
        (await h.CountAsync("SELECT count(*) FROM opportunity.coding_event WHERE workspace_id = @ws AND actor_type = 3", ws)).Should().Be(1);
        (await h.CountAsync("SELECT count(*) FROM opportunity.import_batch_member WHERE workspace_id = @ws AND action = 2", ws)).Should().Be(2);
        (await h.CountAsync("SELECT count(*) FROM opportunity.import_batch_member WHERE workspace_id = @ws AND action = 3", ws)).Should().Be(1);
        (await h.Db.ScalarAsync<short>(
            "SELECT change_mask FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND job_id = @job", ("ws", ws), ("job", overlay.JobId)))
            .Should().Be(7, "content, metadata and coding changed");

        var audit = await h.Db.ColumnAsync(
            $"SELECT category || '.' || action || ':' || coalesce(details->>'Fields', '') FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Coding' ORDER BY recorded_at");
        audit.Should().Equal($"Coding.OverlayEnabled:{responsive.FieldId}", $"Coding.BulkChunkApplied:{responsive.FieldId}");

        // Append/overlay: new numbers are created, existing ones overlaid.
        var mixed = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "CUSTODIAN"], ["OV-1", "Smith"], ["OV-4", "New"])), mode: ImportMode.AppendOverlay);
        await h.RunAsync(mixed);
        var mixedReport = await h.BatchAsync(mixed);
        (mixedReport.RowsImported, mixedReport.RowsOverlaid).Should().Be((1L, 1L));
    }

    [Fact]
    public async Task A_preparation_that_dies_mid_file_is_taken_over_and_resumes_without_duplicates()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 500);
        var ws = await h.WorkspaceAsync();
        var filler = new string('x', 120);
        var rows = new List<string[]> { FillerHeader };
        rows.AddRange(Enumerable.Range(1, 3_000).Select(i => new[] { $"P-{i:D5}", "Smith", filler }));
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat([.. rows]));
        var batch = await h.StartAsync(ws, dat);

        var flaky = new FlakyObjectStore(h.Store) { FailReads = 1, FailAfterBytes = dat.Length / 2 };
        var healthy = h.Store;
        h.Store = flaky;
        var crashed = () => h.PrepareAsync(batch);
        await crashed.Should().ThrowAsync<IOException>();
        (await h.CountAsync("SELECT count(*) FROM opportunity.import_batch_key WHERE workspace_id = @ws", ws)).Should().BeInRange(1, 2_999);
        (await h.Jobs.GetAsync(ws, batch.JobId, Ct))!.Status.Should().Be(JobStatus.Preparing);
        h.Store = healthy;
        var other = new ImportJobPreparer(h.Batches, h.Jobs, h.Db.Fields, h.Workspaces, h.Store,
            new ImportJobOptions { RowsPerChunk = 500, WorkerId = "second-worker" }, Microsoft.Extensions.Logging.Abstractions.NullLogger<ImportJobPreparer>.Instance);
        (await other.PrepareAsync(ws, batch.ImportBatchId, Ct)).Should().Be(ImportPreparationOutcome.Skipped, "the dead worker's claim has not expired yet");

        // Another worker takes over once the claim expires.
        await h.Db.ExecuteAsync("UPDATE opportunity.import_batch SET prepare_claimed_until = now() - interval '1 minute' WHERE workspace_id = @ws", ("ws", ws));
        (await h.Batches.GetPreparableAsync(ws, 10, Ct)).Should().Equal(batch.ImportBatchId);
        (await other.PrepareAsync(ws, batch.ImportBatchId, Ct)).Should().Be(ImportPreparationOutcome.Started);
        await h.DeliverOpenChunksAsync(batch);

        (await h.Jobs.GetAsync(ws, batch.JobId, Ct))!.Status.Should().Be(JobStatus.Completed);
        (await h.CountAsync("SELECT count(*) FROM opportunity.import_batch_key WHERE workspace_id = @ws", ws)).Should().Be(3_000);
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ws)).Should().Be(3_000);
        (await h.IndexTasksPerChunkAsync(batch)).Should().HaveCount(6).And.OnlyContain(t => t.Value == 1);
        (await h.IssuesAsync(batch)).Should().BeEmpty();
    }
}
