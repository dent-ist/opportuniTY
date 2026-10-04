using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Contracts.Import;
using Opportunity.Core.Jobs;
using Opportunity.Import.Jobs;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T06 over HTTP: pre-flight validation that writes nothing and lists its issues with a downloadable detail, the
/// import summary report (JSON, CSV, frozen with the job, audited) and the re-loadable error file in the source's own
/// delimiter profile and encoding, which loads again once its rows are fixed; plus 'Stop after N errors'.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ImportPreflightReportTests(MigrationPostgresFixture postgres)
{
    private const char Dc4 = ImportHarness.Dc4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Rows that change when anything is loaded: documents, objects, index work, imports and jobs.</summary>
    private static readonly string[] WriteTables =
    [
        "document", "stored_object", "index_chunk_task", "search_outbox", "import_batch", "import_batch_key", "import_row_issue", "job", "job_chunk",
        "page_set", "field_definition",
    ];

    [Fact]
    public async Task Preflight_writes_nothing_and_reports_every_check_with_a_downloadable_detail()
    {
        using var share = new Share();
        await using var h = await ImportHarness.CreateAsync(postgres, volumeRoot: share.Root);
        var ws = await h.WorkspaceAsync();

        // An existing document: appending it again is a collision.
        var existing = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "CUSTODIAN"], ["PF-EXIST", "Smith"])));
        (await h.RunAsync(existing)).Status.Should().Be(JobStatus.Completed);

        await using var factory = new ApiFactory();
        using var client = Client(factory, h, share.Root);
        var before = await CountsAsync(h, ws);
        var objectsBefore = Directory.GetFiles(h.StoreRoot, "*", SearchOption.AllDirectories).Length;

        var dat = ImportHarness.Utf8Bom(
            ImportHarness.Dat(
                ["BEGDOC", "CUSTODIAN", "DATESENT", "NATIVELINK"],
                ["PF-1", "Doe", "2020-01-02", @"NATIVES\a.pdf"],
                ["PF-2", "Doe", "not a date", ""],
                ["PF-1", "Again", "", ""],
                ["PF-EXIST", "Smith", "", ""],
                ["", "Blank", "", ""])
            + "þPF-6þ" + Dc4 + "þtoo few fieldsþ\r\n"
            + ImportHarness.Dat(["PF-7", "Doe", "", @"NATIVES\missing.pdf"], ["PF-8", "Doe", "", @"..\outside\secret.txt"]));
        using var response = await PostAsync(client, ws, "imports/preflight", dat, new { autoMap = true, profile = new { paths = new { volumeRoot = "VOL001" } } });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

        result.GetProperty("rowsRead").GetInt64().Should().Be(8);
        result.GetProperty("mode").GetString().Should().Be("append");
        result.GetProperty("blocking").GetBoolean().Should().BeTrue();
        var issues = result.GetProperty("issues").EnumerateArray()
            .Select(i => $"{i.GetProperty("row").GetInt64()}:{i.GetProperty("code").GetString()}:{i.GetProperty("severity").GetString()}")
            .ToList();
        issues.Should().Contain([
            "2:DATE_PARSE_FAILED:error",
            "3:DUPLICATE_CONTROL_NUMBER:error",
            "4:KEY_EXISTS:error",
            "5:REQUIRED_FIELD_MISSING:error",
            "6:FIELD_COUNT_MISMATCH:error",
            "7:NATIVE_MISSING:warning",
            "8:NATIVE_PATH_REJECTED:error",
        ]);
        issues.Should().NotContain(i => i.StartsWith("1:", StringComparison.Ordinal), "row 1 is clean and its native exists");
        result.GetProperty("errorCount").GetInt64().Should().Be(6);
        result.GetProperty("warningCount").GetInt64().Should().Be(1);
        result.GetProperty("issueCounts").EnumerateArray()
            .Single(c => c.GetProperty("code").GetString() == "KEY_EXISTS").GetProperty("count").GetInt64().Should().Be(1);

        // Nothing was written: no document, object, index task, outbox row, import or job.
        (await CountsAsync(h, ws)).Should().Equal(before);
        Directory.GetFiles(h.StoreRoot, "*", SearchOption.AllDirectories).Length.Should().Be(objectsBefore);

        // Every issue downloads as CSV through the gateway, audited.
        var preflightId = result.GetProperty("preflightId").GetGuid();
        using var download = await client.GetAsync(new Uri($"/api/v1/workspaces/{ws}/imports/preflight/{preflightId}/issues", UriKind.Relative), Ct);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        download.Headers.CacheControl!.NoStore.Should().BeTrue();
        var csv = Encoding.UTF8.GetString(await download.Content.ReadAsByteArrayAsync(Ct)).TrimStart('﻿');
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("Row,ControlNumber,Column,Severity,Code,Message");
        lines.Length.Should().Be(1 + 7);
        lines.Should().Contain(l => l.StartsWith("4,PF-EXIST,,error,KEY_EXISTS,", StringComparison.Ordinal));

        (await client.GetAsync(new Uri($"/api/v1/workspaces/{ws}/imports/preflight/{Guid.NewGuid()}/issues", UriKind.Relative), Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await h.Db.ColumnAsync($"SELECT action FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Import' AND action IN ('PreflightRun', 'ReportDownloaded') ORDER BY occurred_at"))
            .Should().Equal("PreflightRun", "ReportDownloaded");
    }

    [Fact]
    public async Task Preflight_applies_the_overlay_key_rules_of_the_import_mode()
    {
        using var share = new Share();
        await using var h = await ImportHarness.CreateAsync(postgres, volumeRoot: share.Root);
        var ws = await h.WorkspaceAsync();
        var existing = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "CUSTODIAN"], ["OV-1", "Smith"])));
        (await h.RunAsync(existing)).Status.Should().Be(JobStatus.Completed);

        await using var factory = new ApiFactory();
        using var client = Client(factory, h, share.Root);
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "CUSTODIAN"], ["OV-1", "Jones"], ["OV-2", "Doe"]));

        async Task<List<string>> KeyIssuesAsync(string mode)
        {
            using var response = await PostAsync(client, ws, "imports/preflight", dat, new { autoMap = true, profile = new { mode } });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
            result.GetProperty("mode").GetString().Should().Be(mode);
            return [.. result.GetProperty("issues").EnumerateArray()
                .Select(i => $"{i.GetProperty("row").GetInt64()}:{i.GetProperty("code").GetString()}")
                .Where(i => i.Contains(":KEY_", StringComparison.Ordinal))];
        }

        (await KeyIssuesAsync("append")).Should().Equal("1:KEY_EXISTS");
        (await KeyIssuesAsync("overlay")).Should().Equal("2:KEY_MISSING");
        (await KeyIssuesAsync("appendOverlay")).Should().BeEmpty();
    }

    [Fact]
    public async Task Preflight_samples_paths_beyond_the_full_check_limit_and_says_so()
    {
        using var share = new Share();
        var rows = Enumerable.Range(1, 30).Select(i => new[] { $"S-{i:D3}", @"NATIVES\a.pdf" }).ToArray();
        var runner = new Opportunity.Import.Preflight.ImportPreflightRunner(
            new NoKeysStore(), [new Opportunity.Import.Preflight.AppendKeyCollisionCheck()],
            new Opportunity.Import.Volumes.ImportVolumeOptions { VolumeShareRoot = share.Root },
            new Opportunity.Import.Preflight.ImportPreflightOptions { FullPathCheckRows = 10, PathSampleInterval = 5 });
        await using var h = await ImportHarness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();
        var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        var result = await runner.RunAsync(new Opportunity.Import.Preflight.ImportPreflightRequest
        {
            WorkspaceId = ws,
            Dat = new MemoryStream(ImportHarness.Utf8Bom(ImportHarness.Dat([["BEGDOC", "NATIVELINK"], .. rows]))),
            Profile = new ImportProfileDefinition { Paths = new PathSettings { VolumeRoot = "VOL001" } },
            Catalog = catalog,
        }, Ct);

        result.ErrorCount.Should().Be(0);
        result.Issues.Should().ContainSingle(i => i.Code == "PATHS_SAMPLED")
            .Which.Message.Should().Contain("14 of 30 rows", "rows 1-10, then 15, 20, 25 and 30");
    }

    [Fact]
    public async Task The_report_is_frozen_and_audited_and_the_fixed_error_file_loads_again()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 2);
        var ws = await h.WorkspaceAsync();
        await using var factory = new ApiFactory();
        using var client = Client(factory, h, null);

        // UTF-16LE with a byte-order mark: the error file must come back in the same encoding.
        static string Row(params string[] values) => string.Join(Dc4, values.Select(v => "þ" + v + "þ")) + "\r\n";
        var text = Row("BEGDOC", "CUSTODIAN", "DATESENT")
            + Row("ERR-1", "Smith", "2020-01-02")
            + Row("ERR-2", "Doe", "31/31/2020")
            + Row("ERR-3", "Lee")
            + Row("ERR-4", "Kim", "2020-02-03");
        var source = Utf16(text);
        using var started = await PostAsync(client, ws, "imports", source, new { name = "Volume E", autoMap = true }, "err-1");
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync(Ct));
        var importId = JsonDocument.Parse(await started.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("importId").GetGuid();
        (await h.RunAsync((await h.Batches.GetAsync(ws, importId, Ct))!)).Status.Should().Be(JobStatus.CompletedWithErrors);

        // The summary is embedded in the import and frozen with it.
        var resource = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/v1/workspaces/{ws}/imports/{importId}", UriKind.Relative), Ct)).RootElement;
        var summary = resource.GetProperty("summary");
        summary.GetProperty("final").GetBoolean().Should().BeTrue();
        summary.GetProperty("status").GetString().Should().Be("completedWithErrors");
        summary.GetProperty("rows").GetProperty("read").GetInt64().Should().Be(4);
        summary.GetProperty("rows").GetProperty("imported").GetInt64().Should().Be(2);
        summary.GetProperty("rows").GetProperty("errored").GetInt64().Should().Be(2);
        summary.GetProperty("errorFileRows").GetInt64().Should().Be(2);
        summary.GetProperty("elapsedSeconds").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        summary.GetProperty("natives").GetProperty("linked").GetInt64().Should().Be(0);
        summary.GetProperty("families").GetProperty("orphans").GetInt64().Should().Be(0);
        (await h.Db.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.import_batch WHERE workspace_id = @ws AND import_batch_id = @id AND report IS NOT NULL", ("ws", ws), ("id", importId)))
            .Should().Be(1);
        (await h.Db.ColumnAsync($"SELECT details->>'RowsErrored' || '/' || coalesce(details->>'TextMissing', '-') FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Import' AND action = 'Completed'"))
            .Should().Equal("2/0");

        var report = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/v1/workspaces/{ws}/imports/{importId}/report", UriKind.Relative), Ct)).RootElement;
        report.GetProperty("issueCounts").EnumerateArray().Select(c => c.GetProperty("code").GetString())
            .Should().BeEquivalentTo(["invalid-date", "field-count-mismatch"]);
        using (var reportCsv = await client.GetAsync(new Uri($"/api/v1/workspaces/{ws}/imports/{importId}/report.csv", UriKind.Relative), Ct))
        {
            reportCsv.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = Encoding.UTF8.GetString(await reportCsv.Content.ReadAsByteArrayAsync(Ct));
            body.Should().Contain("Rows,Errored,2\r\n").And.Contain("Rows,Imported,2\r\n").And.Contain("Errors,invalid-date,1\r\n");
        }

        // The error file: same encoding and byte-order mark, the header plus ImportError, the failing rows as they were.
        using var errorFile = await client.GetAsync(new Uri($"/api/v1/workspaces/{ws}/imports/{importId}/error-file", UriKind.Relative), Ct);
        errorFile.StatusCode.Should().Be(HttpStatusCode.OK, await errorFile.Content.ReadAsStringAsync(Ct));
        errorFile.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("VOL001_errors.dat");
        var bytes = await errorFile.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(2).Should().Equal(0xFF, 0xFE);
        var errors = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        var errorLines = errors.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        errorLines.Should().HaveCount(3);
        errorLines[0].Should().Be(Row("BEGDOC", "CUSTODIAN", "DATESENT", "ImportError").TrimEnd());
        errorLines[1].Should().StartWith(Row("ERR-2", "Doe", "31/31/2020").TrimEnd() + Dc4 + "þinvalid-date: ");
        errorLines[2].Should().StartWith(Row("ERR-3", "Lee").TrimEnd() + Dc4 + "þfield-count-mismatch: ");
        (await h.Db.ColumnAsync($"SELECT details->>'Kind' FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'ReportDownloaded' ORDER BY occurred_at"))
            .Should().Equal("report", "report.csv", "error-file");

        // Fix the rows in place and load the error file unchanged otherwise.
        var fixedText = errors
            .Replace("31/31/2020", "2020-03-04", StringComparison.Ordinal)
            .Replace(Row("ERR-3", "Lee").TrimEnd(), Row("ERR-3", "Lee", "2020-05-06").TrimEnd(), StringComparison.Ordinal);
        using var preflight = await PostAsync(client, ws, "imports/preflight", Utf16(fixedText), new { autoMap = true });
        var checkedFix = JsonDocument.Parse(await preflight.Content.ReadAsStringAsync(Ct)).RootElement;
        checkedFix.GetProperty("errorCount").GetInt64().Should().Be(0, await preflight.Content.ReadAsStringAsync(Ct));
        using var reload = await PostAsync(client, ws, "imports", Utf16(fixedText), new { name = "Volume E fixes", autoMap = true }, "err-2");
        reload.StatusCode.Should().Be(HttpStatusCode.Accepted, await reload.Content.ReadAsStringAsync(Ct));
        var reloadId = JsonDocument.Parse(await reload.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("importId").GetGuid();
        (await h.RunAsync((await h.Batches.GetAsync(ws, reloadId, Ct))!)).Status.Should().Be(JobStatus.Completed);
        var reloaded = (await h.Batches.GetAsync(ws, reloadId, Ct))!;
        (reloaded.RowsImported, reloaded.RowsErrored).Should().Be((2, 0));
        (await h.Db.ColumnAsync($"SELECT control_number FROM opportunity.document WHERE workspace_id = '{ws}' ORDER BY control_number"))
            .Should().Equal("ERR-1", "ERR-2", "ERR-3", "ERR-4");
        (await h.Db.ScalarAsync<long>("SELECT count(*) FROM opportunity.field_definition WHERE workspace_id = @ws AND name = 'ImportError'", ("ws", ws)))
            .Should().Be(0);
    }

    [Fact]
    public async Task Stop_after_N_errors_fails_the_import_once_the_limit_is_reached()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 2);
        var ws = await h.WorkspaceAsync();
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(
            ["BEGDOC", "DATESENT"], ["STOP-1", "bad"], ["STOP-2", "bad"], ["STOP-3", "bad"], ["STOP-4", "2020-01-01"], ["STOP-5", "2020-01-01"], ["STOP-6", "2020-01-01"]));
        var batch = await h.StartAsync(ws, dat, new ImportProfileDefinition { StopAfterErrors = 2 });

        var job = await h.RunAsync(batch);

        job.Status.Should().Be(JobStatus.Failed);
        job.StatusReason.Should().StartWith(ImportChunkExecutor.ErrorLimitReached);
        var stopped = await h.BatchAsync(batch);
        stopped.RowsErrored.Should().Be(2);
        stopped.RowsImported.Should().Be(0);
        stopped.CompletedAt.Should().NotBeNull();
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ws)).Should().Be(0);
        (await h.Db.ColumnAsync($"SELECT outcome || ':' || reason_code FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Import' AND action = 'Completed'"))
            .Should().Equal("Failure:" + ImportChunkExecutor.ErrorLimitReached);
    }

    private static byte[] Utf16(string text) => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];

    private static HttpClient Client(ApiFactory factory, ImportHarness h, string? shareRoot)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", h.Db.AppConnectionString);
            if (shareRoot is not null)
            {
                b.UseSetting("Import:VolumeShareRoot", shareRoot);
            }

            b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton(h.Store)));
        });
        return app.CreateClient();
    }

    private static async Task<List<long>> CountsAsync(ImportHarness h, Guid ws)
    {
        var counts = new List<long>();
        foreach (var table in WriteTables)
        {
            counts.Add(await h.Db.ScalarAsync<long>($"SELECT count(*) FROM opportunity.{table} WHERE workspace_id = @ws", ("ws", ws)));
        }

        return counts;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid ws, string path, byte[] dat, object request, string? key = null)
    {
        using var form = new MultipartFormDataContent("preflight-test-boundary");
        var file = new ByteArrayContent(dat);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "VOL001.dat");
        form.Add(new StringContent(JsonSerializer.Serialize(request, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"), "request");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workspaces/{ws}/{path}", UriKind.Relative)) { Content = form };
        if (key is not null)
        {
            message.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(message, Ct);
    }

    private sealed class NoKeysStore : Opportunity.Application.Import.IImportPreflightStore
    {
        public Task<IReadOnlyDictionary<string, Opportunity.Application.Import.ImportKeyState>> FindKeysAsync(
            Guid workspaceId, IReadOnlyCollection<string> controlNumberNorms, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, Opportunity.Application.Import.ImportKeyState>>(
                new Dictionary<string, Opportunity.Application.Import.ImportKeyState>());

        public Task SaveAsync(Opportunity.Application.Import.ImportPreflightRecord preflight, IReadOnlyList<ImportPreflightIssue> issues, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Opportunity.Application.Import.ImportPreflightRecord?> GetAsync(Guid workspaceId, Guid preflightId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ImportPreflightIssue>> GetIssuesAsync(
            Guid workspaceId, Guid preflightId, int afterIssueNo, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>An import share with VOL001/NATIVES/a.pdf and a file outside the volume.</summary>
    private sealed class Share : IDisposable
    {
        public Share()
        {
            Root = Path.Combine(Path.GetTempPath(), "opp-pf-share-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "VOL001", "NATIVES"));
            Directory.CreateDirectory(Path.Combine(Root, "outside"));
            File.WriteAllBytes(Path.Combine(Root, "VOL001", "NATIVES", "a.pdf"), Encoding.ASCII.GetBytes("%PDF-1.7 preflight native"));
            File.WriteAllText(Path.Combine(Root, "outside", "secret.txt"), "secret");
        }

        public string Root { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
