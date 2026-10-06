using AwesomeAssertions;

using Opportunity.Application.Productions;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Productions;
using Opportunity.Core.Snapshots;
using Opportunity.Production.Productions;

namespace Opportunity.UnitTests.Productions;

/// <summary>E12-T02: the stored specification states every value (defaults filled in), canonically, and invalid ones are refused.</summary>
public class ProductionSpecificationRulesTests
{
    private static readonly FieldCatalog Catalog = new(
    [
        new FieldDefinition { FieldId = SystemFields.ControlNumber, Name = "Control Number", Type = FieldType.Text, Storage = FieldStorage.Metadata },
        new FieldDefinition { FieldId = 1001, Name = "Custodian", Type = FieldType.Text, Storage = FieldStorage.Metadata },
    ], []);

    private static readonly HashSet<int> None = [];

    [Fact]
    public void Defaults_are_filled_in_and_equal_specifications_have_equal_bytes_and_hashes()
    {
        var a = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings(" ABC ")), Catalog, None, out var errors)!;
        var b = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC"), IncludeText: true), Catalog, None, out _)!;

        errors.Should().BeEmpty();
        a.Json.Should().Be(b.Json);
        a.Sha256.Should().Equal(b.Sha256);
        var spec = a.Specification;
        spec.Bates.Should().Be(new ProductionBatesSettings("ABC", 1, 7, string.Empty, BatesLevelResource.Page));
        spec.Images.Should().Be(new ProductionImageSettings(ProductionImageFormatResource.TiffG4, ProductionImageFormatResource.Jpeg, 300));
        spec.DefaultOutput.Should().Be(ProductionOutputResource.Image);
        spec.FileTypeRules.Should().HaveCount(2, "spreadsheets and audio/video are produced natively by default");
        spec.LoadFile!.Fields!.Select(f => f.Header).Should().Equal("Control Number", "NativePath", "TextPath");
        spec.LoadFile.Should().BeEquivalentTo(new { Delimiters = "concordance", Encoding = "utf-8", DateFormat = "yyyy-MM-dd", TimeZone = "UTC" });
        spec.Endorsements!.Items!.Select(e => (e.Position, e.Template)).Should().Equal(
            (EndorsementPositionResource.BottomLeft, "{confidentiality}"), (EndorsementPositionResource.BottomRight, "{bates}"));
        ProductionSpecificationRules.Deserialize(a.Json).Should().BeEquivalentTo(spec);
        ProductionSpecificationRules.Serialize(ProductionSpecificationRules.Deserialize(a.Json)).Should().Be(a.Json, "a stored specification round-trips byte for byte");

        var other = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC", StartNumber: 2)), Catalog, None, out _)!;
        other.Sha256.Should().NotEqual(a.Sha256);
    }

    [Fact]
    public void File_type_rules_decide_the_output_by_extension()
    {
        var spec = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC"),
            DefaultOutput: ProductionOutputResource.Image,
            FileTypeRules: [new([".XLSX", "csv"], ProductionOutputResource.Native), new(["exe"], ProductionOutputResource.Placeholder)]), Catalog, None, out _)!;
        var output = ProductionSpecificationRules.OutputFor(spec.Specification);

        output("xlsx").Should().Be(ProductionOutputKind.Native);
        output(".CSV").Should().Be(ProductionOutputKind.Native);
        output("exe").Should().Be(ProductionOutputKind.Placeholder);
        output("pdf").Should().Be(ProductionOutputKind.Image);
        output(null).Should().Be(ProductionOutputKind.Image);
        spec.Specification.FileTypeRules![0].Extensions.Should().Equal("csv", "xlsx");
    }

    [Theory]
    [InlineData("specification.bates", "A B", 1, 7)]
    [InlineData("specification.bates", "ABC", 1, 13)]
    [InlineData("specification.bates.startNumber", "ABC", 1000, 3)]
    [InlineData("specification.bates.startNumber", "ABC", 0, 7)]
    public void Invalid_bates_numbering_is_refused(string key, string prefix, long start, int padding)
    {
        ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings(prefix, start, padding)), Catalog, None, out var errors)
            .Should().BeNull();
        errors.Should().ContainKey(key);
    }

    [Fact]
    public void Invalid_options_are_refused_with_their_member_names()
    {
        var input = new ProductionSpecification(
            new ProductionBatesSettings("ABC"),
            new ProductionImageSettings(Dpi: 20),
            FileTypeRules: [new(["xlsx"], ProductionOutputResource.Native), new(["XLSX"], ProductionOutputResource.Image)],
            LoadFile: new ProductionLoadFileSettings([new ExportFieldRequest(FieldId: 9999)], TimeZone: "Mars/Olympus", DateFormat: "hh"),
            Endorsements: new ProductionEndorsementSettings([new(EndorsementPositionResource.TopLeft, "{secret}")], FontSize: 40));

        ProductionSpecificationRules.Normalize(input, Catalog, None, out var errors).Should().BeNull();
        errors.Keys.Should().BeEquivalentTo(
            "specification.images.dpi", "specification.fileTypeRules[1]", "specification.loadFile.fields[0]", "specification.loadFile.timeZone",
            "specification.loadFile.dateFormat", "specification.endorsements.fontSize", "specification.endorsements.items[0]");
        ProductionSpecificationRules.Normalize(null, Catalog, None, out errors).Should().BeNull();
        errors.Should().ContainKey("specification.bates");
    }

    [Fact]
    public void Restricted_fields_do_not_exist_for_the_load_file()
    {
        ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC"),
            LoadFile: new ProductionLoadFileSettings([new ExportFieldRequest(FieldId: 1001)])), Catalog, new HashSet<int> { 1001 }, out var errors).Should().BeNull();
        errors.Should().ContainKey("specification.loadFile.fields[0]");
    }

    [Fact]
    public void The_manifest_records_specification_hashes_and_software_and_reads_back()
    {
        var normalized = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC")), Catalog, None, out _)!;
        var production = new ProductionRecord
        {
            WorkspaceId = Guid.CreateVersion7(),
            ProductionId = Guid.CreateVersion7(),
            LineageId = Guid.CreateVersion7(),
            Version = 2,
            Name = "Production Volume 1",
            SnapshotId = Guid.CreateVersion7(),
            SpecificationJson = normalized.Json,
            SpecificationSha256 = normalized.Sha256,
            BatesPrefix = "ABC",
            BatesSuffix = string.Empty,
            BatesPadding = 7,
            BatesStart = 1,
            Status = ProductionStatus.Draft,
            RowVersion = 3,
            BatesState = BatesAllocationState.Allocated,
            BatesFirst = 1,
            BatesLast = 42,
            BatesDocuments = 10,
            BatesUnits = 42,
            AssignmentsSha256 = new byte[32],
            CreatedBy = Guid.CreateVersion7(),
            CreatedByDisplay = "Tester",
            CreatedAt = DateTimeOffset.UnixEpoch,
            ModifiedAt = DateTimeOffset.UnixEpoch,
        };
        var snapshot = new SnapshotRecord
        {
            WorkspaceId = production.WorkspaceId,
            SnapshotId = production.SnapshotId,
            Status = SnapshotStatus.Ready,
            Name = "Frozen",
            Purpose = SnapshotPurpose.Production,
            SourceKind = SnapshotSourceKind.DocumentIds,
            DocumentCount = 10,
            RootSha256 = new byte[32],
            CreatedBy = production.CreatedBy,
            CreatedByDisplay = "Tester",
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var software = new ProductionSoftware("1.2.3", ".NET 10.0.0", BatesAssignmentHasher.Prefix, null, null);
        var at = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

        var (json, sha) = ProductionManifest.Build(production, snapshot, null, software, production.CreatedBy, at);
        var again = ProductionManifest.Build(production, snapshot, null, software, production.CreatedBy, at);

        again.Json.Should().Be(json);
        sha.Should().Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        json.Should().Contain("\"software\":{\"opportunity\":\"1.2.3\",\"runtime\":\".NET 10.0.0\",\"batesAllocator\":\"opportunity.bates.v1\",\"renderer\":null,\"volumeWriter\":null}");
        var values = ProductionManifest.Read(json);
        values.SpecificationJson.Should().Be(normalized.Json);
        values.SpecificationSha256.Should().Be(Convert.ToHexStringLower(normalized.Sha256));
        (values.FirstNumber, values.LastNumber, values.Documents).Should().Be((1L, 42L, 10L));
    }
}
