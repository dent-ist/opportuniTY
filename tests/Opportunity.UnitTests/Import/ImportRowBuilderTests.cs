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
}
