using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

using Opportunity.Application.Import;
using Opportunity.Import.Jobs;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Scanning;
using Opportunity.Import.Volumes;

namespace Opportunity.UnitTests.Import.Volumes;

/// <summary>
/// E08-T09: magic-byte type verification, archive and decompression-bomb limits, the configurable ingest limits, the
/// DAT record and field limits, and the ClamAV adapter's protocol reading (the adapter against a real clamd is in the
/// integration suite, <c>ClamAvScanningTests</c>).
/// </summary>
public sealed class IngestHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, ContentSniffer.WindowsExecutable)]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02 }, ContentSniffer.ElfExecutable)]
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED, 0xFE }, ContentSniffer.MachOExecutable)]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 }, ContentSniffer.Gzip)]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, ContentSniffer.SevenZip)]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, ContentSniffer.Rar)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, ContentSniffer.Zip)]
    public void Executables_and_archives_are_sniffed(byte[] head, string expected) => ContentSniffer.Sniff(head).Should().Be(expected);

    [Theory]
    [InlineData(@"NATIVES\0001.pdf", ContentSniffer.Pdf, false)]
    [InlineData(@"NATIVES\0001.PDF", ContentSniffer.Pdf, false)]
    [InlineData("memo.docx", ContentSniffer.Zip, false)]
    [InlineData("memo.doc", ContentSniffer.OleStorage, false)]
    [InlineData("setup.exe", ContentSniffer.WindowsExecutable, false)]
    [InlineData("libfoo.so", ContentSniffer.ElfExecutable, false)]
    [InlineData("notes.txt", ContentSniffer.OctetStream, false)]
    [InlineData("scan.pdf", ContentSniffer.OctetStream, false)]
    [InlineData("noextension", ContentSniffer.WindowsExecutable, false)]
    [InlineData("invoice.pdf", ContentSniffer.WindowsExecutable, true)]
    [InlineData("photo.jpg", ContentSniffer.ElfExecutable, true)]
    [InlineData("readme.txt", ContentSniffer.MachOExecutable, true)]
    [InlineData("report.pdf", ContentSniffer.Zip, true)]
    [InlineData("sheet.xlsx", ContentSniffer.OleStorage, true)]
    [InlineData("picture.png", ContentSniffer.Jpeg, true)]
    public void Bytes_that_contradict_the_extension_are_a_mismatch(string name, string sniffed, bool mismatch) =>
        (FileTypeVerifier.Mismatch(name, sniffed) is not null).Should().Be(mismatch);

    [Fact]
    public void An_ordinary_document_archive_is_within_the_limits()
    {
        var docx = Zip(("word/document.xml", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<w:p>text</w:p>", 1_000)))));
        ArchiveInspector.Inspect(new MemoryStream(docx), new ImportLimits()).Should().BeNull();
    }

    [Fact]
    public void A_decompression_bomb_exceeds_the_ratio()
    {
        var bomb = Zip(("payload.bin", new byte[20 * 1024 * 1024]));
        var verdict = ArchiveInspector.Inspect(new MemoryStream(bomb), new ImportLimits())!;
        verdict.Verdict.Should().Be(MalwareScanVerdict.Unscannable);
        verdict.Detail.Should().StartWith(MalwareScanReasons.ArchiveRatio);
        ArchiveInspector.Inspect(new MemoryStream(bomb), new ImportLimits { MaxArchiveRatio = 5_000 }).Should().BeNull("the ratio is configurable");
    }

    [Fact]
    public void Too_many_entries_and_too_deep_nesting_are_violations()
    {
        var many = Zip([.. Enumerable.Range(0, 50).Select(i => ($"f{i}.txt", new byte[] { 1 }))]);
        ArchiveInspector.Inspect(new MemoryStream(many), new ImportLimits { MaxArchiveEntries = 49 })!.Detail.Should().StartWith(MalwareScanReasons.ArchiveEntries);
        ArchiveInspector.Inspect(new MemoryStream(many), new ImportLimits { MaxArchiveEntries = 50 }).Should().BeNull();

        var nested = Zip(("inner.txt", "x"u8.ToArray()));
        for (var level = 0; level < 4; level++)
        {
            nested = Zip(($"level{level}.zip", nested));
        }

        // Five archives deep: the outer one and four nested ones.
        ArchiveInspector.Inspect(new MemoryStream(nested), new ImportLimits { MaxArchiveDepth = 4 })!.Detail.Should().StartWith(MalwareScanReasons.ArchiveDepth);
        ArchiveInspector.Inspect(new MemoryStream(nested), new ImportLimits { MaxArchiveDepth = 5 }).Should().BeNull();
    }

    [Fact]
    public void A_damaged_zip_is_left_to_the_scanner() =>
        ArchiveInspector.Inspect(new MemoryStream([0x50, 0x4B, 0x03, 0x04, 1, 2, 3]), new ImportLimits()).Should().BeNull();

    [Fact]
    public void Limits_and_scanning_bind_from_configuration_and_reject_nonsense()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Import:Limits:MaxRecordBytes"] = "1048576",
            ["Import:Limits:MaxFieldCount"] = "500",
            ["Import:Limits:MaxNativeBytes"] = "1073741824",
            ["Import:Limits:MaxArchiveRatio"] = "250.5",
            ["Import:MalwareScanning:Mode"] = "ClamAv",
            ["Import:MalwareScanning:Host"] = "scanner.internal",
            ["Import:MalwareScanning:MaxSignatureAge"] = "1.00:00:00",
        }).Build();

        var limits = ImportLimits.FromConfiguration(configuration);
        (limits.MaxRecordBytes, limits.MaxFieldCount, limits.MaxNativeBytes, limits.MaxArchiveRatio).Should().Be((1_048_576, 500, 1L << 30, 250.5));
        limits.MaxArchiveEntries.Should().Be(new ImportLimits().MaxArchiveEntries);
        var scanning = MalwareScanningOptions.FromConfiguration(configuration);
        (scanning.Mode, scanning.Host, scanning.Port, scanning.MaxSignatureAge).Should().Be((MalwareScanningMode.ClamAv, "scanner.internal", 3310, TimeSpan.FromDays(1)));
        MalwareScanningOptions.FromConfiguration(new ConfigurationBuilder().Build()).Mode.Should().Be(MalwareScanningMode.Off);

        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Import:Limits:MaxFieldCount"] = "0" }).Build();
        FluentActions.Invoking(() => ImportLimits.FromConfiguration(bad)).Should().Throw<InvalidOperationException>();
        var mode = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Import:MalwareScanning:Mode"] = "Icap" }).Build();
        FluentActions.Invoking(() => MalwareScanningOptions.FromConfiguration(mode)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Dat_records_over_the_byte_or_field_limit_are_rejected_even_when_they_fit_the_read_buffer()
    {
        var dat = "þBEGDOCþ\u0014þTITLEþ\r\nþA-1þ\u0014þokþ\r\nþA-2þ\u0014þ" + new string('x', 120) + "þ\r\nþA-3þ\u0014þaþ\u0014þbþ\u0014þcþ\r\nþA-4þ\u0014þfineþ\r\n";
        var options = new DatReaderOptions { MaxRecordBytes = 100, MaxFieldCount = 2, ReturnRejectedRecords = true };
        await using var reader = await DatReader.OpenAsync(new MemoryStream(Encoding.UTF8.GetBytes(dat)), options, cancellationToken: Ct);
        var records = new List<DatRecord>();
        await foreach (var record in reader.ReadAllAsync(Ct))
        {
            records.Add(record);
        }

        records.Select(r => (r.RowNumber, r.IsRejected)).Should().Equal((1L, false), (2L, true), (3L, true), (4L, false));
        records[1].Issues.Single().Kind.Should().Be(DatIssueKind.RecordTooLong);
        records[2].Issues.Should().Contain(i => i.Kind == DatIssueKind.TooManyFields);
        records[3].Values.Should().Equal("A-4", "fine");
    }

    [Theory]
    [InlineData("stream: OK", MalwareScanVerdict.Clean, null)]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND", MalwareScanVerdict.Infected, "Win.Test.EICAR_HDB-1")]
    [InlineData("stream: Heuristics.Encrypted.PDF FOUND", MalwareScanVerdict.Unscannable, "encrypted: Heuristics.Encrypted.PDF")]
    [InlineData("stream: Heuristics.Limits.Exceeded.MaxFileSize FOUND", MalwareScanVerdict.Unscannable, "scanner-limit: Heuristics.Limits.Exceeded.MaxFileSize")]
    [InlineData("INSTREAM size limit exceeded. ERROR", MalwareScanVerdict.Unscannable, "oversize: the scanner's stream limit was exceeded")]
    public void Clamd_answers_map_to_verdicts(string answer, MalwareScanVerdict verdict, string? detail)
    {
        var result = ClamAvScanner.Interpret(answer + "\0", "ClamAV 1.4.6/28136");
        (result.Verdict, result.Detail, result.Engine).Should().Be((verdict, detail, "ClamAV 1.4.6/28136"));
    }

    [Fact]
    public void Other_clamd_errors_retry_instead_of_judging() =>
        FluentActions.Invoking(() => ClamAvScanner.Interpret("Can't allocate memory ERROR", "ClamAV"))
            .Should().Throw<MalwareScannerUnavailableException>();

    [Theory]
    [InlineData("ClamAV 1.4.6/28136/Sun Sep 27 06:26:12 2026", "ClamAV 1.4.6/28136", "2026-09-27T06:26:12Z")]
    [InlineData("ClamAV 1.4.6/28150/Mon Oct  5 08:01:02 2026\n", "ClamAV 1.4.6/28150", "2026-10-05T08:01:02Z")]
    [InlineData("ClamAV 1.4.6", "ClamAV 1.4.6", null)]
    public void The_version_answer_gives_engine_and_signature_date(string answer, string version, string? published)
    {
        var (v, p) = ClamAvScanner.ParseVersion(answer);
        v.Should().Be(version);
        p.Should().Be(published is null ? null : DateTimeOffset.Parse(published, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task An_unreachable_clamd_is_unavailable_not_a_verdict()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var scanner = new ClamAvScanner(new MalwareScanningOptions
        {
            Mode = MalwareScanningMode.ClamAv,
            Host = "127.0.0.1",
            Port = port,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });
        (await scanner.GetStatusAsync(Ct)).Reachable.Should().BeFalse();
        await FluentActions.Awaiting(() => scanner.ScanAsync(new MemoryStream([1, 2, 3]), 3, Ct))
            .Should().ThrowAsync<MalwareScannerUnavailableException>();
    }

    [Fact]
    public async Task Without_a_scanner_only_the_archive_limits_screen()
    {
        var path = Path.Combine(Path.GetTempPath(), "opp-screen-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await File.WriteAllBytesAsync(path, Zip(("payload.bin", new byte[20 * 1024 * 1024])), Ct);
            var screening = new IngestScreening(new ImportLimits(), null);
            (await screening.ScreenAsync(path, ContentSniffer.Zip, Ct))!.Verdict.Should().Be(MalwareScanVerdict.Unscannable);
            (await screening.ScreenAsync(path, ContentSniffer.OctetStream, Ct)).Should().BeNull("only ZIP natives are inspected");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                entry.Write(content);
            }
        }

        return buffer.ToArray();
    }
}
