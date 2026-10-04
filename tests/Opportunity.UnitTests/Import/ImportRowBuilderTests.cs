using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Import;
using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Import.Jobs;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;

using static Opportunity.UnitTests.Import.Mapping.MappingTestSupport;

namespace Opportunity.UnitTests.Import;

/// <summary>E08-T03: one parsed DAT record → the row a chunk writes (columns, metadata, raw values, coding, issues).</summary>
public class ImportRowBuilderTests
{
    private static readonly Guid Batch = Guid.Parse("0199a8a0-0000-7000-8000-00000000bbbb");

    private static async Task<(CompiledMapping Mapping, List<DatRecord> Records)> ParseAsync(string dat, FieldCatalog catalog, ImportProfileDefinition? profile = null)
    {
        await using var reader = await DatReader.OpenAsync(Utf8(dat), new DatReaderOptions { ReturnRejectedRecords = true, MaxRejectedRows = null },
            cancellationToken: TestContext.Current.CancellationToken);
        var mapping = MappingCompiler.Compile(profile, reader.Header.Names, catalog);
        var records = new List<DatRecord>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken) is { } record)
        {
            records.Add(record);
        }

        return (mapping, records);
    }

    [Fact]
    public async Task Structural_columns_metadata_and_raw_values_are_separated()
    {
        var custodian = Custom(1000, "Custodian", FieldType.Keyword);
        var (mapping, records) = await ParseAsync(Concordance(
            ["BEGDOC", "ENDDOC", "CUSTODIAN", "DOCDATE", "FILESIZE", "MD5HASH", "DEDUPEHASH", "FOLDERPATH"],
            ["ABC-1", "ABC-2", "Smith", "2020-01-02T03:04:05Z", "1,000", "D41D8CD98F00B204E9800998ECF8427E", "ABCDEF", @"\Mail\Inbox"]), Catalog(custodian));

        var row = ImportRowBuilder.Build(mapping, records[0], 1, 2, Workspace, Batch, new HashSet<int>());

        row.HasErrors.Should().BeFalse();
        row.ControlNumberNorm.Should().Be("ABC-1");
        var d = row.Document!;
        (d.BegBates, d.EndBates, d.FileSize).Should().Be(("ABC-1", "ABC-2", 1000L));
        d.DocumentDate.Should().Be(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero));
        d.DocumentDateSource.Should().Be(DocumentDateSource.Upstream);
        Convert.ToHexStringLower(d.Md5!).Should().Be("d41d8cd98f00b204e9800998ecf8427e");
        d.UpstreamDedupeHash.Should().Be("abcdef");
        row.SuppliedColumns.Should().BeEquivalentTo(
            ["beg_bates", "end_bates", "document_date", "document_date_source", "file_size", "md5", "upstream_dedupe_hash", "upstream_dedupe_hash_kind"]);
        JsonNode.Parse(d.Metadata)![FieldKey.For(custodian.FieldId)]!.GetValue<string>().Should().Be("Smith");
        var raw = JsonNode.Parse(d.MetadataRaw!)!;
        raw[FieldKey.For(SystemFields.FileSize)]!["raw"]!.GetValue<string>().Should().Be("1,000");
        raw[FieldKey.For(SystemFields.FileSize)]!["batch"]!.GetValue<string>().Should().Be(Batch.ToString());
        raw["s:FolderPath"]!["raw"]!.GetValue<string>().Should().Be(@"\Mail\Inbox");
    }

    [Fact]
    public async Task Family_sources_are_stored_normalized_with_the_import_prefix()
    {
        var profile = new ImportProfileDefinition { ControlNumberPrefix = "v1-" };
        var (mapping, records) = await ParseAsync(Concordance(
            ["BEGDOC", "BegAttach", "EndAttach", "ParentID", "GroupIdentifier", "AttachmentIDs", "FamilyDate"],
            ["abc-2", "abc-1", "abc-4", "abc-1", " Fam 7 ", "abc-3; abc-4;;abc-3", "2021-05-06T07:08:09Z"]), Catalog(), profile);

        var row = ImportRowBuilder.Build(mapping, records[0], 1, 2, Workspace, Batch, new HashSet<int>());

        row.HasErrors.Should().BeFalse();
        var d = row.Document!;
        (d.BegAttach, d.BegAttachNorm, d.EndAttachNorm, d.ParentIdNorm, d.GroupIdentifier).Should().Be(("abc-1", "V1-ABC-1", "V1-ABC-4", "V1-ABC-1", "Fam 7"));
        d.AttachmentIdsNorm.Should().Equal("V1-ABC-3", "V1-ABC-4");
        (d.FamilyDate, d.UpstreamFamilyDate).Should().Be(((DateTimeOffset?)null, new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.Zero)));
        row.SuppliedColumns.Should().Contain(["beg_attach_norm", "end_attach_norm", "parent_id_norm", "group_identifier", "attachment_ids_norm", "upstream_family_date"])
            .And.NotContain("family_date", "FamilyDate is derived by family resolution");
    }

    [Fact]
    public async Task Coding_fields_load_only_when_enabled_and_rejected_records_keep_their_parser_errors()
    {
        var responsive = Custom(1000, "Responsive", FieldType.Keyword, storage: FieldStorage.Coding);
        var profile = new ImportProfileDefinition
        {
            Overlay = new OverlaySettings { AllowCodingFieldOverlay = true },
            Columns = [new ColumnMapping { Column = "RESP", Targets = [FieldTarget(1000, "Responsive")] }],
        };
        var (mapping, records) = await ParseAsync(Concordance(["BEGDOC", "RESP"], ["A-1", "yes"], ["A-2"]), Catalog(responsive), profile);

        var enabled = ImportRowBuilder.Build(mapping, records[0], 1, 2, Workspace, Batch, new HashSet<int> { 1000 });
        enabled.HasErrors.Should().BeFalse();
        enabled.Coding.Should().ContainSingle().Which.FieldId.Should().Be(1000);
        JsonNode.Parse(enabled.Document!.Metadata)!.AsObject().Should().BeEmpty("coding never lives in metadata (ADR-003 R2)");

        var disabled = ImportRowBuilder.Build(mapping, records[0], 1, 2, Workspace, Batch, new HashSet<int>());
        disabled.Issues.Should().ContainSingle(i => i.Code == "coding-field-not-enabled" && i.Severity == ImportIssueSeverity.Error);
        disabled.Document.Should().BeNull();

        var rejected = ImportRowBuilder.Build(mapping, records[1], 2, 3, Workspace, Batch, new HashSet<int>());
        rejected.HasErrors.Should().BeTrue();
        rejected.ControlNumber.Should().Be("A-2");
        rejected.Issues.Should().ContainSingle(i => i.Code == "field-count-mismatch");
    }

    [Theory]
    [InlineData(ImportMode.Append, true, false)]
    [InlineData(ImportMode.Overlay, false, false)]
    [InlineData(ImportMode.Overlay, true, true)]
    [InlineData(ImportMode.AppendOverlay, true, true)]
    public async Task Blank_values_clear_only_overlays_that_ask_for_it(ImportMode mode, bool overwrite, bool expectCleared)
    {
        var custodian = Custom(1000, "Custodian", FieldType.Keyword);
        var notes = Custom(1001, "Notes", FieldType.Keyword, storage: FieldStorage.Coding);
        var profile = new ImportProfileDefinition
        {
            Mode = mode,
            Overlay = new OverlaySettings { BlankValuesOverwrite = overwrite, AllowCodingFields = true },
            Columns =
            [
                new ColumnMapping { Column = "NOTES", Targets = [FieldTarget(1001, "Notes")] },
                new ColumnMapping { Column = "PARENT", Targets = [Structural(StructuralTarget.ParentId)] },
            ],
        };
        var (mapping, records) = await ParseAsync(Concordance(["BEGDOC", "CUSTODIAN", "FILESIZE", "NOTES", "PARENT"], ["A-1", "", " ", "", ""]),
            Catalog(custodian, notes), profile);

        var row = ImportRowBuilder.Build(mapping, records[0], 1, 2, Workspace, Batch, new HashSet<int> { 1001 });

        row.HasErrors.Should().BeFalse();
        if (expectCleared)
        {
            row.ClearedMetadataKeys.Should().Equal(FieldKey.For(1000));
            row.SuppliedColumns.Should().Contain(["file_size", "parent_id_norm"]);
            row.Coding.Should().ContainSingle().Which.Should().Match<ImportCodingValue>(c => c.FieldId == 1001 && c.Value == null);
        }
        else
        {
            row.ClearedMetadataKeys.Should().BeEmpty();
            row.SuppliedColumns.Should().NotContain(["file_size", "parent_id_norm"]);
            row.Coding.Should().BeEmpty("a blank value never overwrites by default");
        }
    }

    [Fact]
    public async Task Multi_value_merge_marks_multiple_choice_values_of_overlays_only()
    {
        var tags = Custom(1000, "Tags", FieldType.MultiChoice);
        var issues = Custom(1001, "Issues", FieldType.MultiChoice, storage: FieldStorage.Coding);
        Choice[] choices =
        [
            new() { WorkspaceId = Workspace, FieldId = 1000, ChoiceId = 10, Name = "Hot", IsActive = true },
            new() { WorkspaceId = Workspace, FieldId = 1001, ChoiceId = 20, Name = "Pricing", IsActive = true },
        ];
        static ImportProfileDefinition Profile(ImportMode mode, OverlayMultiValue multi) => new()
        {
            Mode = mode,
            Overlay = new OverlaySettings { MultiValue = multi, AllowCodingFields = true },
            Columns =
            [
                new ColumnMapping { Column = "TAGS", Targets = [FieldTarget(1000, "Tags")] },
                new ColumnMapping { Column = "ISSUES", Targets = [FieldTarget(1001, "Issues")] },
            ],
        };
        var dat = Concordance(["BEGDOC", "TAGS", "ISSUES"], ["A-1", "Hot", "Pricing"]);

        foreach (var (mode, multi, merge) in new[]
                 {
                     (ImportMode.AppendOverlay, OverlayMultiValue.Merge, true),
                     (ImportMode.Overlay, OverlayMultiValue.Replace, false),
                     (ImportMode.Append, OverlayMultiValue.Merge, false),
                 })
        {
            var (mapping, records) = await ParseAsync(dat, Catalog([tags, issues], choices), Profile(mode, multi));
            var row = ImportRowBuilder.Build(mapping, records[0], 1, 2, Workspace, Batch, new HashSet<int> { 1001 });
            row.HasErrors.Should().BeFalse(string.Join("; ", row.Issues.Select(i => i.Message)));
            row.MergedMetadataKeys.Should().BeEquivalentTo(merge ? [FieldKey.For(1000)] : Array.Empty<string>());
            row.Coding.Single().Merge.Should().Be(merge);
        }
    }

    [Theory]
    [InlineData(ImportMode.Append, true, ImportKeyDecision.KeyExists, ImportKeyRules.KeyExists)]
    [InlineData(ImportMode.Append, false, ImportKeyDecision.Create, null)]
    [InlineData(ImportMode.Overlay, true, ImportKeyDecision.Overlay, null)]
    [InlineData(ImportMode.Overlay, false, ImportKeyDecision.KeyMissing, ImportKeyRules.KeyMissing)]
    [InlineData(ImportMode.AppendOverlay, true, ImportKeyDecision.Overlay, null)]
    [InlineData(ImportMode.AppendOverlay, false, ImportKeyDecision.Create, null)]
    public void Key_collisions_follow_the_import_mode(ImportMode mode, bool exists, ImportKeyDecision decision, string? code)
    {
        ImportKeyRules.Decide(mode, exists).Should().Be(decision);
        ImportKeyRules.Issue(decision, "A-1")?.Code.Should().Be(code);
        (ImportKeyRules.Issue(decision, "A-1") is null).Should().Be(code is null);
    }
}
