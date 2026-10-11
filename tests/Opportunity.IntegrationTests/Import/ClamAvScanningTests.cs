using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Import;
using Opportunity.Contracts.Import;
using Opportunity.Core.Jobs;
using Opportunity.Import.Jobs;
using Opportunity.Import.Scanning;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.Malware;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T09 with the real default adapter: <see cref="ClamAvScanner"/> against ClamAV <c>clamd</c> (Docker Hub image
/// pinned in <c>versions.env</c>, bundled signatures). The EICAR test file is Infected (<c>Eicar-Test-Signature</c>),
/// clean bytes are Clean, a stream over clamd's own <c>StreamMaxLength</c> or over the adapter's limit is Unscannable
/// (<c>oversize</c>), the VERSION answer gives the signature date for the health check, and an import through the real
/// scanner quarantines the EICAR native.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ClamAvScanningTests(MigrationPostgresFixture postgres, ClamAvFixture clamav) : IClassFixture<ClamAvFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClamAvScanner Scanner(long maxScanBytes = 2L * 1024 * 1024 * 1024) =>
        new(new MalwareScanningOptions { Mode = MalwareScanningMode.ClamAv, Host = clamav.Host, Port = clamav.Port, MaxScanBytes = maxScanBytes });

    [Fact]
    public async Task Clamd_reports_eicar_as_infected_clean_bytes_as_clean_and_oversized_streams_as_unscannable()
    {
        var scanner = Scanner();

        var status = await scanner.GetStatusAsync(Ct);
        status.Reachable.Should().BeTrue();
        status.Version.Should().StartWith("ClamAV ");
        status.SignaturesPublishedAt.Should().NotBeNull("the VERSION answer carries the database date the health check ages");

        var infected = await scanner.ScanAsync(new MemoryStream(SignatureMalwareScanner.Eicar), SignatureMalwareScanner.Eicar.Length, Ct);
        (infected.Verdict, infected.Detail).Should().Be((MalwareScanVerdict.Infected, "Eicar-Test-Signature"));
        infected.Engine.Should().Be(status.Version);

        var clean = Encoding.ASCII.GetBytes("%PDF-1.7 an ordinary document");
        (await scanner.ScanAsync(new MemoryStream(clean), clean.Length, Ct)).Verdict.Should().Be(MalwareScanVerdict.Clean);

        // Over clamd's StreamMaxLength (1 MiB in the fixture): clamd refuses mid-stream and says why.
        var large = new byte[3 * 1024 * 1024];
        var refused = await scanner.ScanAsync(new MemoryStream(large), large.Length, Ct);
        refused.Verdict.Should().Be(MalwareScanVerdict.Unscannable);
        refused.Detail.Should().StartWith(MalwareScanReasons.Oversize);

        // Over the adapter's own limit: nothing is sent.
        var capped = await Scanner(maxScanBytes: 16).ScanAsync(new MemoryStream(clean), clean.Length, Ct);
        (capped.Verdict, capped.Detail!.Split(':')[0]).Should().Be((MalwareScanVerdict.Unscannable, MalwareScanReasons.Oversize));
    }

    [Fact]
    public async Task An_import_through_clamd_quarantines_the_eicar_native()
    {
        var share = Path.Combine(Path.GetTempPath(), "opp-clamav-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(share, "VOL001", "NATIVES"));
            await File.WriteAllBytesAsync(Path.Combine(share, "VOL001", "NATIVES", "eicar.com"), SignatureMalwareScanner.Eicar, Ct);
            await File.WriteAllTextAsync(Path.Combine(share, "VOL001", "NATIVES", "memo.txt"), "An ordinary memo.", Ct);
            await using var h = await ImportHarness.CreateAsync(postgres, volumeRoot: share);
            h.Screening = new IngestScreening(new ImportLimits(), Scanner());
            var ws = await h.WorkspaceAsync();
            var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(
                ["BEGDOC", "NATIVELINK"],
                ["C-001", @"NATIVES\eicar.com"],
                ["C-002", @"NATIVES\memo.txt"]));

            var batch = await h.StartAsync(ws, dat, new ImportProfileDefinition { Paths = new PathSettings { VolumeRoot = "VOL001" } });
            (await h.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);

            (await h.Db.ColumnAsync(
                $"""
                SELECT d.control_number || ':' || o.state || ':' || coalesce(o.scan_verdict::text, '-') || ':' || coalesce(o.scan_detail, '-')
                FROM opportunity.document d JOIN opportunity.stored_object o ON o.workspace_id = d.workspace_id AND o.object_id = d.native_object_id
                WHERE d.workspace_id = '{ws}' ORDER BY d.control_number
                """)).Should().Equal("C-001:2:2:Eicar-Test-Signature", "C-002:1:1:-");
            (await h.Db.ColumnAsync(
                $"SELECT details->>'Signatures' FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Import' AND action = 'MalwareDetected'"))
                .Should().Equal("Eicar-Test-Signature");
        }
        finally
        {
            Directory.Delete(share, recursive: true);
        }
    }
}
