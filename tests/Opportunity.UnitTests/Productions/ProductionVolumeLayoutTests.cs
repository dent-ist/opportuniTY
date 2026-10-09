using AwesomeAssertions;

using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Pages;
using Opportunity.Production.Exports;
using Opportunity.Production.Productions;
using Opportunity.Production.Volumes;

namespace Opportunity.UnitTests.Productions;

/// <summary>
/// E12-T05: the production volume layout (ticket review: page images named by page Bates, natives and text by
/// ProdBegBates, <c>&lt;prefix&gt;_VOL001</c>, at most N files a folder), the default DAT field set, the volume's load-file
/// settings derived from the frozen specification, and the production-only DAT columns that exports refuse.
/// </summary>
public class ProductionVolumeLayoutTests
{
    private static readonly FieldCatalog Catalog = new(
    [
        .. SystemFields.Create(Guid.Empty),
        new FieldDefinition { FieldId = 1001, Name = "Custodian", Type = FieldType.Keyword, Storage = FieldStorage.Metadata },
        new FieldDefinition { FieldId = 1002, Name = "Subject", Type = FieldType.Text, Storage = FieldStorage.Metadata },
        new FieldDefinition { FieldId = 1003, Name = "From", Type = FieldType.Text, Storage = FieldStorage.Metadata, IsDeleted = true },
    ], []);

    [Fact]
    public void Images_are_named_by_page_Bates_natives_and_text_by_ProdBegBates_in_folders_of_at_most_N_files()
    {
        var spec = Normalize(ProductionHarnessSpec("ABC") with { LoadFile = new ProductionLoadFileSettings(Volume: new ExportVolumeRequest(MaxFilesPerFolder: 2)) });
        var settings = ProductionVolumeSettings.ExportSettingsOf(spec);
        var layout = new ProductionVolumeLayout(settings);

        layout.Volume.Should().Be("ABC_VOL001");
        layout.DatPath.Should().Be("ABC_VOL001/DATA/ABC_VOL001.dat");
        layout.OptPath.Should().Be("ABC_VOL001/DATA/ABC_VOL001.opt");
        layout.ImagePath(0, "ABC0000001", PageImageFormat.TiffG4).Should().Be("ABC_VOL001/IMAGES/IMG0001/ABC0000001.tif");
        layout.ImagePath(1, "ABC0000002", PageImageFormat.Jpeg).Should().Be("ABC_VOL001/IMAGES/IMG0001/ABC0000002.jpg");
        layout.ImagePath(2, "ABC0000003", PageImageFormat.TiffG4).Should().Be("ABC_VOL001/IMAGES/IMG0002/ABC0000003.tif", "two images a folder");
        layout.NativePath(3, "ABC0000007", "xlsx").Should().Be("ABC_VOL001/NATIVES/NATIVE0002/ABC0000007.xlsx");
        layout.TextPath(1, "ABC0000001").Should().Be("ABC_VOL001/TEXT/TEXT0001/ABC0000001.txt");
        layout.LoadFilePath(layout.TextPath(1, "ABC0000001")).Should().Be(@"ABC_VOL001\TEXT\TEXT0001\ABC0000001.txt");
        ProductionSpecificationRules.DefaultVolumePrefix("A.B-C_d.e").Should().Be("A_B-C_d_e_VOL");
        ProductionSpecificationRules.DefaultVolumePrefix("ABCDEFGHIJKLMNOPQRSTUVWXYZ").Should().Be("ABCDEFGHIJKLMNOP_VOL");
    }

    [Fact]
    public void The_default_DAT_has_the_production_field_set_the_workspace_has_in_order()
    {
        var spec = Normalize(ProductionHarnessSpec("ABC"));
        var settings = ProductionVolumeSettings.ExportSettingsOf(spec);

        settings.Columns.Select(c => c.Header).Should().Equal(
            "ProdBegBates", "ProdEndBates", "ProdBegAttach", "ProdEndAttach", "Custodian", "AllCustodians", "FileName", "FileExtension", "DateSent",
            "DateCreated", "DateLastModified", "Subject", "MD5Hash", "Confidentiality", "Redacted", "PageCount", "NativeLink", "TextLink");
        settings.Columns.Single(c => c.Header == "Custodian").FieldId.Should().Be(1001);
        settings.Columns.Single(c => c.Header == "MD5Hash").FieldId.Should().Be(SystemFields.Md5);
        settings.Columns.Single(c => c.Header == "PageCount").Kind.Should().Be(ExportColumnKind.ProducedPages);
        settings.Columns.Single(c => c.Header == "NativeLink").Kind.Should().Be(ExportColumnKind.NativePath);
        settings.Should().BeEquivalentTo(new { Delimiters = "concordance", Encoding = "utf-8", PathSeparator = "\\", IncludeNatives = true, IncludeText = true });

        var noNatives = Normalize(ProductionHarnessSpec("ABC") with { FileTypeRules = [], IncludeText = false });
        ProductionVolumeSettings.ExportSettingsOf(noNatives).Columns.Select(c => c.Header).Should().NotContain(["NativeLink", "TextLink"]);
    }

    [Fact]
    public void Production_columns_are_refused_in_exports_and_placeholder_texts_are_validated()
    {
        var request = new CreateExportRequest(Guid.NewGuid(), [new ExportFieldRequest(Column: ExportColumnResource.ProdBegBates)]);
        ExportSettingsRules.Normalize(request, Catalog, new HashSet<int>(), out var errors).Should().BeNull();
        errors.Should().ContainKey("fields[0]");

        ProductionSpecificationRules.Normalize(ProductionHarnessSpec("ABC") with
        {
            Placeholders = new ProductionPlaceholderSettings(Withheld: "Withheld {reason}"),
        }, Catalog, new HashSet<int>(), out var placeholderErrors).Should().BeNull();
        placeholderErrors.Should().ContainKey("specification.placeholders.withheld");
        ProductionVolumeSettings.Fill("Withheld – {confidentiality} – {bates}", "ABC0000009", "CONFIDENTIAL", "P1")
            .Should().Be("Withheld – CONFIDENTIAL – ABC0000009");

        var colors = Normalize(ProductionHarnessSpec("ABC") with { Images = new ProductionImageSettings(ColorFileTypes: [".PNG", "jpg"]) });
        colors.Images!.ColorFileTypes.Should().Equal("jpg", "png");
    }

    private static ProductionSpecification ProductionHarnessSpec(string prefix) => new(new ProductionBatesSettings(prefix));

    private static ProductionSpecification Normalize(ProductionSpecification input)
    {
        var normalized = ProductionSpecificationRules.Normalize(input, Catalog, new HashSet<int>(), out var errors);
        errors.Should().BeEmpty();
        return normalized!.Specification;
    }
}
