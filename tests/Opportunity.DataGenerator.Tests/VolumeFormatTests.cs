using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

using AwesomeAssertions;

using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Volumes;

namespace Opportunity.DataGenerator.Tests;

/// <summary>Structural validity of the hand-encoded natives, page images and encodings.</summary>
public class VolumeFormatTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(258)]
    [InlineData(259)]
    [InlineData(260)]
    [InlineData(261)]
    [InlineData(117_700)]
    public void Fixed_huffman_zero_runs_inflate_to_the_requested_length(int count)
    {
        byte[] inflated = Inflate(PageImages.ZlibZeros(count));
        inflated.Should().HaveCount(count);
        inflated.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void Png_pages_have_valid_chunks_and_decode_to_white()
    {
        byte[] png = PageImages.Png("OPP0000000001 page 1 of 1");
        png.AsSpan(0, 8).ToArray().Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        int pos = 8;
        var idat = new MemoryStream();
        var types = new List<string>();
        while (pos < png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + length));
            Crc32.Compute(png.AsSpan(pos + 4, 4 + length)).Should().Be(crc, type);
            if (type == "IDAT")
            {
                idat.Write(png, pos + 8, length);
            }

            types.Add(type);
            pos += 12 + length;
        }

        types.Should().Equal("IHDR", "PLTE", "pHYs", "tEXt", "IDAT", "IEND");
        Inflate(idat.ToArray()).Should().HaveCount(PageImages.RasterHeight * (1 + ((PageImages.RasterWidth + 7) / 8))).And.OnlyContain(b => b == 0);
    }

    [Fact]
    public void Tiff_pages_are_g4_with_one_ifd_per_page()
    {
        byte[] tiff = PageImages.Tiff(["A page 1 of 3", "A.0002 page 2 of 3", "A.0003 page 3 of 3"]);
        Encoding.ASCII.GetString(tiff, 0, 2).Should().Be("II");
        BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(2)).Should().Be(42);
        uint ifd = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        int pages = 0;
        while (ifd != 0)
        {
            (ifd % 2).Should().Be(0);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan((int)ifd));
            var tags = new Dictionary<int, uint>();
            for (int i = 0; i < count; i++)
            {
                int e = (int)ifd + 2 + (i * 12);
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(e + 2));
                tags[BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(e))] = type == 3
                    ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(e + 8))
                    : BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(e + 8));
            }

            tags.Keys.Should().BeInAscendingOrder();
            tags[256].Should().Be(PageImages.TiffWidth);
            tags[257].Should().Be(PageImages.TiffHeight);
            tags[259].Should().Be(4, "CCITT Group 4");
            tags[297].Should().Be((uint)pages);
            byte[] strip = tiff.AsSpan((int)tags[273], (int)tags[279]).ToArray();
            strip.Length.Should().Be((PageImages.TiffHeight + 24 + 7) / 8);
            Encoding.ASCII.GetString(tiff, (int)tags[270], 4).Should().StartWith("A");
            pages++;
            ifd = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan((int)ifd + 2 + (count * 12)));
        }

        pages.Should().Be(3);
    }

    [Fact]
    public void Jpeg_pages_have_the_expected_markers_and_dimensions()
    {
        byte[] jpg = PageImages.Jpeg("comment");
        jpg[..2].Should().Equal(0xFF, 0xD8);
        jpg[^2..].Should().Equal(0xFF, 0xD9);
        int sof = jpg.AsSpan().IndexOf([(byte)0xFF, (byte)0xC0]);
        BinaryPrimitives.ReadUInt16BigEndian(jpg.AsSpan(sof + 5)).Should().Be(PageImages.RasterHeight);
        BinaryPrimitives.ReadUInt16BigEndian(jpg.AsSpan(sof + 7)).Should().Be(PageImages.RasterWidth);
        PageImages.Jpeg("comment").Should().Equal(jpg);
    }

    [Theory]
    [InlineData("pdf", false)]
    [InlineData("docx", false)]
    [InlineData("xlsx", false)]
    [InlineData("zip", false)]
    [InlineData("msg", false)]
    [InlineData("html", false)]
    [InlineData("csv", false)]
    [InlineData("txt", false)]
    [InlineData("png", false)]
    [InlineData("jpg", false)]
    [InlineData("pptx", true)]
    public void Natives_are_valid_files_of_the_claimed_type_and_a_pure_function_of_content(string fileType, bool placeholder)
    {
        DocumentContent content = Content(fileType, 1);
        const string Excerpt = "From: Ana <ana@corp.example>\nHello (world) & <friends> þ 漢字\n\nSecond paragraph.";
        NativeFile native = NativeFactory.Build(content, Excerpt);
        native.IsPlaceholder.Should().Be(placeholder);
        native.Extension.Should().Be(fileType == "msg" ? "eml" : fileType);
        NativeFactory.Build(Content(fileType, 1), Excerpt).Bytes.Should().Equal(native.Bytes);
        NativeFactory.Build(Content(fileType, 2), Excerpt).Bytes.Should().NotEqual(native.Bytes, "the content key is embedded");
        string key = NativeFactory.ContentKeyHex(content);
        byte[] b = native.Bytes;
        switch (fileType)
        {
            case "pdf":
                string pdf = Encoding.Latin1.GetString(b);
                pdf.Should().StartWith("%PDF-1.4").And.EndWith("%%EOF\n").And.Contain(key);
                int xref = int.Parse(pdf[(pdf.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
                pdf[xref..].Should().StartWith("xref");
                string[] entries = pdf[xref..].Split('\n')[3..8];
                for (int i = 0; i < entries.Length; i++)
                {
                    pdf[int.Parse(entries[i][..10], System.Globalization.CultureInfo.InvariantCulture)..].Should().StartWith($"{i + 1} 0 obj");
                }

                break;
            case "docx":
            case "xlsx":
            case "zip":
                using (var zip = new ZipArchive(new MemoryStream(b), ZipArchiveMode.Read))
                {
                    var names = zip.Entries.Select(e => e.FullName).ToList();
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        using var reader = new StreamReader(entry.Open());
                        string text = reader.ReadToEnd();
                        if (entry.FullName.EndsWith(".xml", StringComparison.Ordinal) || entry.FullName.EndsWith(".rels", StringComparison.Ordinal))
                        {
                            System.Xml.Linq.XDocument.Parse(text);
                        }
                    }

                    names.Should().Contain(fileType switch { "docx" => "word/document.xml", "xlsx" => "xl/worksheets/sheet1.xml", _ => "contents.txt" });
                }

                break;
            case "msg":
                string eml = Encoding.ASCII.GetString(b);
                eml.Should().Contain("MIME-Version: 1.0\r\n").And.Contain("X-Opportunity-Content-Key: " + key);
                string body = eml[(eml.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
                Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace("\r\n", "", StringComparison.Ordinal))).Should().Be(Excerpt);
                b.Should().OnlyContain(x => x < 0x80);
                break;
            case "html":
                Encoding.UTF8.GetString(b).Should().StartWith("<!DOCTYPE html>").And.Contain("Hello (world) &amp; &lt;friends&gt;").And.Contain(key);
                break;
            case "png":
                b[..4].Should().Equal(0x89, 0x50, 0x4E, 0x47);
                break;
            case "jpg":
                b[..2].Should().Equal(0xFF, 0xD8);
                break;
            case "pptx":
                Encoding.UTF8.GetString(b).Should().StartWith(NativeFactory.PlaceholderSignature);
                break;
            default:
                Encoding.UTF8.GetString(b).Should().Contain("Hello").And.Contain(key);
                break;
        }
    }

    [Fact]
    public void Windows_1252_maps_the_c1_range_and_replaces_unrepresentable_characters()
    {
        const string Value = "þ®¶€ž漢😀a";
        byte[] bytes = LoadFileEncodings.GetBytes(LoadFileEncoding.Windows1252, Value);
        bytes.Should().Equal(0xFE, 0xAE, 0xB6, 0x80, 0x9E, (byte)'?', (byte)'?', (byte)'a');
        LoadFileEncodings.GetString(LoadFileEncoding.Windows1252, bytes).Should().Be("þ®¶€ž??a");
        LoadFileEncodings.GetBytes(LoadFileEncoding.Utf16Le, "a", withPreamble: true).Should().Equal(0xFF, 0xFE, (byte)'a', 0);
        LoadFileEncodings.GetBytes(LoadFileEncoding.Utf8Bom, "þ", withPreamble: true).Should().Equal(0xEF, 0xBB, 0xBF, 0xC3, 0xBE);
    }

    private static DocumentContent Content(string fileType, ulong key)
    {
        object?[] fields = new object?[FieldCatalog.StandardCount];
        fields[FieldCatalog.FileName] = "Report þ.®" + fileType;
        fields[FieldCatalog.Subject] = "Quarterly 漢 review\nline two";
        fields[FieldCatalog.From] = "Jürgen \"JJ\" Müller <jm@corp.example>";
        fields[FieldCatalog.To] = new[] { "Ana <ana@corp.example>", "Bo Li <bo@ext.example>" };
        fields[FieldCatalog.DateSent] = new DateTimeOffset(2020, 2, 3, 4, 5, 6, TimeSpan.FromHours(-5));
        fields[FieldCatalog.MessageId] = "<m1@corp.example>";
        return new DocumentContent
        {
            ContentKey = new UInt128(key, key * 7),
            Kind = fileType == "msg" ? DocumentKind.Email : DocumentKind.EDocument,
            FileType = fileType,
            Md5 = "",
            Sha256 = "",
            Text = TextSpec.Empty,
            Fields = fields,
        };
    }

    private static byte[] Inflate(byte[] zlib)
    {
        using var input = new ZLibStream(new MemoryStream(zlib), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
