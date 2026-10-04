using System.Text;

using AwesomeAssertions;

using Opportunity.Core.Pages;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Import.Images;
using Opportunity.Import.Jobs;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Volumes;

using PageImageFormat = Opportunity.Core.Pages.PageImageFormat;

namespace Opportunity.UnitTests.Import;

/// <summary>E08-T05 building blocks: the OPT reader, the page image probe and the import-root jail for image paths.</summary>
public sealed class OptImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<OptRecord>> ReadAsync(byte[] bytes)
    {
        var result = new List<OptRecord>();
        await using var reader = OptReader.Open(new MemoryStream(bytes));
        while (await reader.ReadAsync(Ct) is { } record)
        {
            result.Add(record);
        }

        return result;
    }

    [Fact]
    public async Task The_OPT_reader_reads_breaks_page_counts_and_paths_with_commas()
    {
        var rows = await ReadAsync(Encoding.ASCII.GetBytes(
            "ABC0001,VOL001,IMAGES\\001\\ABC0001.tif,Y,,,3\r\n" +
            "ABC0002,VOL001,IMAGES\\001\\ABC0002.tif,,,,\r\n" +
            "\r\n" +
            "ABC0003,VOL001,IMAGES\\001\\Smith, J\\ABC0003.tif,,,,\n" +
            "ABC0004,VOL001,IMAGES\\ABC0004.tif,y,,,x\r\n" +
            "ABC0005,VOL001\r\n" +
            ",VOL001,IMAGES\\X.tif,Y,,,1"));

        rows.Select(r => (r.RowNumber, r.LineNumber, r.ImageKey, r.Path, r.DocumentBreak, r.PageCount)).Should().Equal(
            (1L, 1L, "ABC0001", "IMAGES\\001\\ABC0001.tif", true, (int?)3),
            (2L, 2L, "ABC0002", "IMAGES\\001\\ABC0002.tif", false, (int?)null),
            (3L, 4L, "ABC0003", "IMAGES\\001\\Smith, J\\ABC0003.tif", false, (int?)null),
            (4L, 5L, "ABC0004", "IMAGES\\ABC0004.tif", true, (int?)null),
            (5L, 6L, "ABC0005", string.Empty, false, (int?)null),
            (6L, 7L, string.Empty, "IMAGES\\X.tif", true, (int?)1));
        rows.Take(3).Should().OnlyContain(r => r.Problem == null);
        rows[3].Problem.Should().Contain("page count");
        rows[4].Problem.Should().Contain("column");
        rows[5].Problem.Should().Contain("no image key");
    }

    [Fact]
    public async Task The_OPT_reader_honours_byte_order_marks_and_falls_back_to_Windows_1252()
    {
        var utf16 = (await ReadAsync([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("ÄBC1,V,IMAGES\\ÄBC1.tif,Y,,,1\r\n")])).Single();
        utf16.ImageKey.Should().Be("ÄBC1");
        var bom = (await ReadAsync([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("ÄBC2,V,P.tif,Y,,,1\r\n")])).Single();
        bom.ImageKey.Should().Be("ÄBC2");
        var ansi = (await ReadAsync([0xC4, .. Encoding.ASCII.GetBytes("BC3,V,P.tif,Y,,,1\r\n")])).Single();
        ansi.ImageKey.Should().Be("ÄBC3");
        var longLine = (await ReadAsync(Encoding.ASCII.GetBytes(new string('A', OptReader.MaxLineBytes + 10) + "\r\nB,V,P.tif,Y,,,1\r\n")));
        longLine[0].Problem.Should().Contain("longer");
        longLine[1].ImageKey.Should().Be("B");
    }

    [Fact]
    public void The_probe_reads_generated_TIFF_G4_multi_page_TIFF_JPEG_and_PNG_pages()
    {
        var tiff = ImageProbe.Probe(new MemoryStream(PageImages.Tiff(["one"])))!;
        tiff.Format.Should().Be(PageImageFormat.TiffG4);
        tiff.TiffCompression.Should().Be(4);
        tiff.Frames.Should().ContainSingle().Which.Should().Be(new ImageFrame(2550, 3300, 300, 300, PageColorMode.Bitonal));
        tiff.Frames[0].WidthPt.Should().Be(612m);
        tiff.Frames[0].HeightPt.Should().Be(792m);

        ImageProbe.Probe(new MemoryStream(PageImages.Tiff(["1", "2", "3"])))!.Frames.Should().HaveCount(3);

        var jpeg = ImageProbe.Probe(new MemoryStream(PageImages.Jpeg("page")))!;
        (jpeg.Format, jpeg.ContentType).Should().Be((PageImageFormat.Jpeg, "image/jpeg"));
        jpeg.Frames.Single().Should().Be(new ImageFrame(850, 1100, 100, 100, PageColorMode.Gray));

        var png = ImageProbe.Probe(new MemoryStream(PageImages.Png("page")))!;
        (png.Format, png.ContentType).Should().Be((PageImageFormat.Png, "image/png"));
        png.Frames.Single().Should().Be(new ImageFrame(850, 1100, 100, 100, PageColorMode.Bitonal));
    }

    [Fact]
    public void The_probe_rejects_files_that_are_not_page_images_or_are_damaged()
    {
        ImageProbe.Probe(new MemoryStream("%PDF-1.4 not an image"u8.ToArray())).Should().BeNull();
        ImageProbe.Probe(new MemoryStream([])).Should().BeNull();
        var tiff = PageImages.Tiff(["page"]);
        ImageProbe.Probe(new MemoryStream(tiff[..12])).Should().BeNull();

        // An IFD that points to itself must not loop.
        var loop = (byte[])tiff.Clone();
        var ifd = BitConverter.ToUInt32(loop, 4);
        var count = BitConverter.ToUInt16(loop, (int)ifd);
        BitConverter.GetBytes(ifd).CopyTo(loop, (int)ifd + 2 + (count * 12));
        ImageProbe.Probe(new MemoryStream(loop)).Should().BeNull();

        var random = new Random(79);
        for (var i = 0; i < 500; i++)
        {
            var damaged = (byte[])tiff.Clone();
            for (var j = 0; j < 8; j++)
            {
                damaged[random.Next(8, Math.Min(damaged.Length, 600))] = (byte)random.Next(256);
            }

            var act = () => ImageProbe.Probe(new MemoryStream(damaged));
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Image_paths_resolve_inside_the_volume_root_with_either_separator_and_a_stripped_prefix()
    {
        using var share = new TempShare();
        var file = share.File("VOL001", "IMAGES", "IMG0001", "ABC0001.tif");
        ImportVolume.TryOpen(share.Options, "VOL001", out var volume, out var error).Should().BeTrue(error);

        foreach (var path in new[] { @"IMAGES\IMG0001\ABC0001.tif", "IMAGES/IMG0001/ABC0001.tif", @".\IMAGES\IMG0001\ABC0001.tif", @"IMAGES\\IMG0001\.\ABC0001.tif" })
        {
            volume!.TryResolve(path, null, out var full, out error).Should().BeTrue(path + ": " + error);
            full.Should().Be(file);
        }

        volume!.TryResolve(@"\\server\export\VOL001\IMAGES\IMG0001\ABC0001.tif", @"\\server\export\VOL001\", out var stripped, out error).Should().BeTrue(error);
        stripped.Should().Be(file);
    }

    [Theory]
    [InlineData(@"..\secret.tif")]
    [InlineData(@"IMAGES\..\..\secret.tif")]
    [InlineData("IMAGES/../../../etc/passwd")]
    [InlineData(@"IMAGES\ ..\x.tif")]
    [InlineData(@"C:\VOL001\IMAGES\x.tif")]
    [InlineData("c:x.tif")]
    [InlineData(@"\\server\share\x.tif")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\IMAGES\x.tif")]
    [InlineData("IMAGES\\x.tif:stream")]
    [InlineData("IMAGES\\x\u0000.tif")]
    [InlineData("")]
    [InlineData(@".\")]
    public void Traversal_absolute_UNC_and_stream_paths_are_rejected(string path)
    {
        using var share = new TempShare();
        share.File("VOL001", "IMAGES", "x.tif");
        ImportVolume.TryOpen(share.Options, "VOL001", out var volume, out _).Should().BeTrue();
        volume!.TryResolve(path, null, out var full, out var error).Should().BeFalse();
        full.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Random_paths_never_resolve_outside_the_volume_root()
    {
        using var share = new TempShare();
        share.File("VOL001", "IMAGES", "a.tif");
        share.File("outside.tif");
        ImportVolume.TryOpen(share.Options, "VOL001", out var volume, out _).Should().BeTrue();
        string[] pieces = ["..", ".", "IMAGES", "a.tif", "\\", "/", ":", "C:", "~", " ", "..\\", "../", "%2e%2e", "\u2024\u2024", "outside.tif", "VOL001"];
        var random = new Random(20261004);
        for (var i = 0; i < 5_000; i++)
        {
            var path = string.Concat(Enumerable.Range(0, random.Next(1, 8)).Select(_ => pieces[random.Next(pieces.Length)]));
            if (volume!.TryResolve(path, null, out var full, out _))
            {
                full!.StartsWith(volume.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal).Should().BeTrue(path);
            }
        }
    }

    [Fact]
    public void Symbolic_links_are_never_followed_even_inside_the_root()
    {
        using var share = new TempShare();
        var target = share.File("VOL001", "IMAGES", "real.tif");
        var outside = share.File("secret.tif");
        var links = Path.Combine(share.Root, "VOL001", "LINKS");
        Directory.CreateDirectory(links);
        File.CreateSymbolicLink(Path.Combine(links, "inside.tif"), target);
        File.CreateSymbolicLink(Path.Combine(links, "outside.tif"), outside);
        Directory.CreateSymbolicLink(Path.Combine(share.Root, "VOL001", "DIRLINK"), Path.GetDirectoryName(outside)!);
        ImportVolume.TryOpen(share.Options, "VOL001", out var volume, out _).Should().BeTrue();

        volume!.TryResolve(@"LINKS\inside.tif", null, out _, out var error).Should().BeFalse();
        error.Should().Contain("symbolic link");
        volume.TryResolve(@"LINKS\outside.tif", null, out _, out _).Should().BeFalse();
        volume.TryResolve(@"DIRLINK\secret.tif", null, out _, out _).Should().BeFalse();
        volume.TryResolve(@"IMAGES\real.tif", null, out _, out _).Should().BeTrue();
    }

    [Fact]
    public void A_volume_root_must_lie_in_the_configured_share()
    {
        using var share = new TempShare();
        share.File("VOL001", "x.tif");
        ImportVolume.TryOpen(new ImportVolumeOptions(), "VOL001", out _, out var error).Should().BeFalse();
        error.Should().Contain("Import:VolumeShareRoot");
        ImportVolume.TryOpen(share.Options, @"..\elsewhere", out _, out _).Should().BeFalse();
        ImportVolume.TryOpen(share.Options, Path.GetTempPath(), out _, out _).Should().BeFalse();
        ImportVolume.TryOpen(share.Options, "MISSING", out _, out _).Should().BeFalse();
        ImportVolume.TryOpen(share.Options, Path.Combine(share.Root, "VOL001"), out var absolute, out _).Should().BeTrue();
        absolute!.Root.Should().Be(Path.Combine(share.Root, "VOL001"));
        ImportVolume.TryOpen(share.Options, null, out var whole, out _).Should().BeTrue();
        whole!.Root.Should().Be(share.Root);
    }

    [Fact]
    public void Document_ids_of_an_import_row_are_deterministic_version_7_ids()
    {
        var batch = Guid.CreateVersion7();
        var id = ImportDocumentIds.For(batch, 42);
        id.Should().Be(ImportDocumentIds.For(batch, 42));
        id.Should().NotBe(ImportDocumentIds.For(batch, 43));
        id.Should().NotBe(ImportDocumentIds.For(Guid.CreateVersion7(), 42));
        id.Version.Should().Be(7);
        id.ToString("N")[..12].Should().Be(batch.ToString("N")[..12], "the timestamp is the import's");
    }

    private sealed class TempShare : IDisposable
    {
        public TempShare()
        {
            Root = Path.Combine(Path.GetTempPath(), "opp-share-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Options = new ImportVolumeOptions { VolumeShareRoot = Root };
        }

        public string Root { get; }

        public ImportVolumeOptions Options { get; }

        public string File(params string[] parts)
        {
            var path = Path.Combine([Root, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, [1, 2, 3]);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
