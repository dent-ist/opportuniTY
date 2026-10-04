using System.IO.Compression;
using System.Text;

using AwesomeAssertions;

using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Pages;
using Opportunity.Import.LoadFiles;
using Opportunity.Production.Exports;
using Opportunity.UnitTests.Import;

namespace Opportunity.UnitTests.Exports;

/// <summary>E12-T01: the export's DAT/OPT conventions read back through the importer's parser, the volume layout and settings.</summary>
public sealed class ExportLoadFileTests
{
    public static TheoryData<string> Presets => new(DelimiterProfile.Presets.Select(p => p.Name));

    [Theory]
    [MemberData(nameof(Presets))]
    public async Task Dat_rows_read_back_through_the_import_parser_with_every_value_intact(string preset)
    {
        DelimiterProfile.TryGetPreset(preset, out var profile).Should().BeTrue();
        string[] header = ["Control Number", "Subject", "To"];
        string[][] rows =
        [
            ["ABC0001", "Quarterly þ results, \"draft\"", "a@example.test;b@example.test"],
            ["ABC0002", "Line one\r\nLine two\nLine three", ""],
            ["ABC0003", "Pricing \u0014 ¶ comma, here", "Zoë Ångström"],
        ];
        var text = LoadFileText.DatRow(profile, header) + string.Concat(rows.Select(r => LoadFileText.DatRow(profile, r)));
        var bytes = (byte[])[.. LoadFileEncodings.Preamble(LoadFileEncodingKind.Utf16LE), .. LoadFileText.Encode(LoadFileEncodingKind.Utf16LE, text)];

        var parsed = await DatTestSupport.ParseAsync(bytes, new DatReaderOptions { Profile = profile });

        parsed.Issues.Should().BeEmpty();
        parsed.Reader.Header.Names.Should().Equal(header);
        // Concordance carries line breaks as ® (read back as \n); CSV keeps them literally inside the qualifier.
        parsed.Values.Should().BeEquivalentTo(
            rows.Select(r => r.Select(v => profile.Newline is null ? v : v.Replace("\r\n", "\n", StringComparison.Ordinal)).ToList()),
            o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData("=SUM(A1:A3)", "'=SUM(A1:A3)")]
    [InlineData("+1 555 0100", "'+1 555 0100")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@import", "'@import")]
    [InlineData("\tlead", "'\tlead")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void Formula_leading_cells_are_neutralized(string value, string expected)
    {
        LoadFileText.Neutralize(value).Should().Be(expected);
        LoadFileText.DatRow(DelimiterProfile.Csv, [value], [true]).Should().Be("\"" + expected.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"\r\n");
        LoadFileText.DatRow(DelimiterProfile.Csv, [value], [false]).Should().Be("\"" + value + "\"\r\n");
    }

    [Fact]
    public void Opt_rows_follow_the_opticon_layout_with_crlf()
    {
        LoadFileText.OptRow("ABC0001", "VOL001", @"VOL001\IMAGES\IMG0001\ABC0001.tif", true, 3)
            .Should().Be("ABC0001,VOL001,VOL001\\IMAGES\\IMG0001\\ABC0001.tif,Y,,,3\r\n");
        LoadFileText.OptRow("ABC0001.0002", "VOL001", "VOL001/IMAGES/IMG0001/ABC0001.0002.tif", false, null)
            .Should().Be("ABC0001.0002,VOL001,VOL001/IMAGES/IMG0001/ABC0001.0002.tif,,,,\r\n");
        LoadFileText.CsvRow("A,B", "=x", "q\"uote").Should().Be("\"A,B\",'=x,\"q\"\"uote\"\r\n");
    }

    [Fact]
    public void The_layout_is_deterministic_by_ordinal_and_names_files_by_control_number()
    {
        var layout = new ExportLayout(new ExportSettings { MaxFilesPerFolder = 2, VolumeStart = 7, VolumePadding = 3 });

        layout.Volume.Should().Be("VOL007");
        layout.DatPath.Should().Be("VOL007/DATA/VOL007.dat");
        layout.OptPath.Should().Be("VOL007/DATA/VOL007.opt");
        layout.NativePath(1, "ABC0001", "pdf").Should().Be("VOL007/NATIVES/NATIVE0001/ABC0001.pdf");
        layout.NativePath(2, "ABC0002", "pdf").Should().Be("VOL007/NATIVES/NATIVE0001/ABC0002.pdf");
        layout.NativePath(3, "ABC0003", "pdf").Should().Be("VOL007/NATIVES/NATIVE0002/ABC0003.pdf");
        layout.TextPath(5, "ABC0005").Should().Be("VOL007/TEXT/TEXT0003/ABC0005.txt");
        layout.ImagePath(1, ExportLayout.PageKey("ABC0001", 2), PageImageFormat.TiffG4).Should().Be("VOL007/IMAGES/IMG0001/ABC0001.0002.tif");
        layout.LoadFilePath("VOL007/TEXT/TEXT0003/ABC0005.txt").Should().Be(@"VOL007\TEXT\TEXT0003\ABC0005.txt");
        new ExportLayout(new ExportSettings { PathSeparator = "/" }).LoadFilePath("VOL001/DATA/x").Should().Be("VOL001/DATA/x");
    }

    [Theory]
    [InlineData("ABC0001", 9, "ABC0001")]
    [InlineData(@"ABC\0001", 9, "ABC_0001_9")]
    [InlineData("A:B*C?", 12, "A_B_C__12")]
    [InlineData("CON", 3, "_CON_3")]
    [InlineData("trailing. ", 4, "trailing_4")]
    public void File_stems_are_safe_and_never_collide(string controlNumber, long ordinal, string expected) =>
        ExportLayout.FileStem(controlNumber, ordinal).Should().Be(expected);

    [Fact]
    public void Object_keys_carry_generated_names_only()
    {
        var ws = Guid.CreateVersion7();
        var key = ExportLayout.ChunkObjectKey(ws, Guid.CreateVersion7(), Guid.CreateVersion7(), 12, 3, "0000000001-native");
        key.Area.Should().Be(Opportunity.Application.Storage.ObjectArea.Export);
        key.WorkspaceId.Should().Be(ws);
        key.Value.Should().EndWith("/c000012-3/0000000001-native");
        ExportLayout.NativeExtension(".PDF", null).Should().Be("pdf");
        ExportLayout.NativeExtension("../x", "application/pdf").Should().Be("pdf");
        ExportLayout.NativeExtension(null, null).Should().Be("bin");
    }

    [Fact]
    public void Settings_default_to_the_familiar_layout_and_append_path_columns()
    {
        var settings = ExportSettingsRules.Normalize(
            new CreateExportRequest(Guid.CreateVersion7(), [new(FieldId: SystemFields.ControlNumber, Header: "BEGDOC"), new(Column: ExportColumnResource.FamilyId)]),
            Catalog(), new HashSet<int>(), out var errors);

        errors.Should().BeEmpty();
        settings!.VolumeName.Should().Be("VOL001");
        settings.Delimiters.Should().Be("concordance");
        settings.Encoding.Should().Be("utf-8");
        settings.PathSeparator.Should().Be("\\");
        settings.MaxFilesPerFolder.Should().Be(1_000);
        settings.Columns.Select(c => c.Header).Should().Equal("BEGDOC", "FamilyID", "NativePath", "TextPath");
        ExportSettings.Deserialize(settings.Serialize()).Serialize().Should().Be(settings.Serialize());
    }

    [Fact]
    public void Settings_reject_unknown_or_hidden_fields_duplicate_headers_and_bad_options()
    {
        var restricted = new HashSet<int> { 1001 };
        var settings = ExportSettingsRules.Normalize(
            new CreateExportRequest(
                Guid.CreateVersion7(),
                [new(FieldId: 9999), new(FieldId: 1001), new(FieldId: SystemFields.ControlNumber), new(FieldId: SystemFields.FileName, Header: "control number"),
                 new(FieldId: 1, Column: ExportColumnResource.ParentId), new(Column: ExportColumnResource.TextPath)],
                Delimiters: "pipes", Encoding: "ebcdic", IncludeText: false, PathSeparator: "|",
                Volume: new ExportVolumeRequest(Prefix: "VOL 1", Padding: 9)),
            Catalog(), restricted, out var errors);

        settings.Should().BeNull();
        errors.Keys.Should().Contain(["fields[0]", "fields[1]", "fields[4]", "fields[5]", "fields", "delimiters", "encoding", "pathSeparator",
            "volume.prefix", "volume.padding"]);
    }

    [Fact]
    public async Task The_streaming_zip_reads_back_with_every_entry_and_crc()
    {
        using var output = new MemoryStream();
        var zip = new StreamingZipWriter(output);
        var big = new byte[200_000];
        new Random(7).NextBytes(big);
        await zip.AddEntryAsync("MANIFEST.json", new MemoryStream(Encoding.UTF8.GetBytes("{}")), TestContext.Current.CancellationToken);
        await zip.AddEntryAsync("VOL001/NATIVES/NATIVE0001/Zoë.bin", new MemoryStream(big), TestContext.Current.CancellationToken);
        await zip.AddEntryAsync("VOL001/TEXT/TEXT0001/empty.txt", new MemoryStream(), TestContext.Current.CancellationToken);
        await zip.FinishAsync(TestContext.Current.CancellationToken);

        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        archive.Entries.Select(e => e.FullName).Should().Equal("MANIFEST.json", "VOL001/NATIVES/NATIVE0001/Zoë.bin", "VOL001/TEXT/TEXT0001/empty.txt");
        await using var read = await archive.Entries[1].OpenAsync(TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, TestContext.Current.CancellationToken);
        copy.ToArray().Should().Equal(big);
        archive.Entries[2].Length.Should().Be(0);
    }

    private static FieldCatalog Catalog()
    {
        var ws = Guid.CreateVersion7();
        return new FieldCatalog(
            [.. SystemFields.Create(ws), new FieldDefinition { WorkspaceId = ws, FieldId = 1001, Name = "Secret Notes", Type = FieldType.Text, Storage = FieldStorage.Coding }],
            []);
    }
}
