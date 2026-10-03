using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Storage;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T03 over HTTP: start an import (multipart, Idempotency-Key, 202 + job Location), let the import worker's
/// preparer and chunk consumer run it, then monitor it and download its row-level errors as CSV.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ImportApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Header = ["BEGDOC", "CUSTODIAN", "DATESENT"];

    [Fact]
    public async Task An_import_starts_with_202_runs_in_the_worker_and_reports_counts_and_downloadable_errors()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 2);
        var ws = await h.WorkspaceAsync();
        await using var factory = new ApiFactory();
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", h.Db.AppConnectionString);
            b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton(h.Store)));
        });
        using var client = app.CreateClient();

        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(Header, ["API-1", "Smith", "2020-01-02"], ["API-2", "Doe", "not a date"], ["API-1", "Again", ""]));
        using var response = await PostAsync(client, ws, dat, new { name = "Volume 1", autoMap = true }, "start-1");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var started = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var importId = started.GetProperty("importId").GetGuid();
        var jobId = started.GetProperty("job").GetProperty("jobId").GetGuid();
        response.Headers.Location!.ToString().Should().Be($"/api/v1/workspaces/{ws}/jobs/{jobId}");
        started.GetProperty("name").GetString().Should().Be("Volume 1");
        started.GetProperty("job").GetProperty("status").GetString().Should().Be("created");

        // A retried start with the same key replays the response; no second import.
        using (var replay = await PostAsync(client, ws, dat, new { name = "Volume 1", autoMap = true }, "start-1"))
        {
            replay.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        (await h.CountAsync("SELECT count(*) FROM opportunity.import_batch WHERE workspace_id = @ws", ws)).Should().Be(1);

        // The import worker: preparation, then the chunks.
        var batch = (await h.Batches.GetAsync(ws, importId, Ct))!;
        await h.RunAsync(batch);

        var resource = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/v1/workspaces/{ws}/imports/{importId}", UriKind.Relative), Ct)).RootElement;
        var report = resource.GetProperty("report");
        report.GetProperty("rowsRead").GetInt64().Should().Be(3);
        report.GetProperty("rowsImported").GetInt64().Should().Be(1);
        report.GetProperty("rowsErrored").GetInt64().Should().Be(2);
        resource.GetProperty("job").GetProperty("status").GetString().Should().Be("completedWithErrors");
        resource.GetProperty("job").GetProperty("indexed").GetProperty("indexTasksTotal").GetInt64().Should().Be(2);

        var list = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/v1/workspaces/{ws}/imports?limit=10", UriKind.Relative), Ct)).RootElement;
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("importId").GetGuid()).Should().Equal(importId);

        using var csv = await client.GetAsync(new Uri($"/api/v1/workspaces/{ws}/imports/{importId}/errors", UriKind.Relative), Ct);
        csv.StatusCode.Should().Be(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var lines = (await csv.Content.ReadAsStringAsync(Ct)).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("Row,Line,Severity,ControlNumber,Column,Code,Message");
        lines.Skip(1).Select(l => string.Join(',', l.Split(',').Take(6))).Should().Equal(
            "2,3,Error,API-2,DATESENT,invalid-date",
            "3,4,Error,API-1,,duplicate-control-number");

        (await client.GetAsync(new Uri($"/api/v1/workspaces/{ws}/imports/{Guid.NewGuid()}", UriKind.Relative), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_profile_that_does_not_fit_the_file_is_refused_before_anything_is_stored()
    {
        await using var h = await ImportHarness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();
        await using var factory = new ApiFactory();
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", h.Db.AppConnectionString);
            b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton(h.Store)));
        });
        using var client = app.CreateClient();

        // No column maps to Control Number.
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(["Something", "Else"], ["a", "b"]));
        using var response = await PostAsync(client, ws, dat, new { autoMap = true }, "bad-1");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("control-number-unmapped");
        (await h.CountAsync("SELECT count(*) FROM opportunity.import_batch WHERE workspace_id = @ws", ws)).Should().Be(0);
        (await h.CountAsync("SELECT count(*) FROM opportunity.job WHERE workspace_id = @ws", ws)).Should().Be(0);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid ws, byte[] dat, object request, string key)
    {
        using var form = new MultipartFormDataContent("import-test-boundary");
        var file = new ByteArrayContent(dat);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "VOL001.dat");
        form.Add(new StringContent(JsonSerializer.Serialize(request, JsonSerializerOptions.Web), Encoding.UTF8), "request");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workspaces/{ws}/imports", UriKind.Relative)) { Content = form };
        message.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(message, Ct);
    }
}
