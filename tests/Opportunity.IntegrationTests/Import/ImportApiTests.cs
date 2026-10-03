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

        // Row-level errors, paged in row order.
        var first = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/v1/workspaces/{ws}/imports/{importId}/errors?limit=1", UriKind.Relative), Ct)).RootElement;
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/api/v1/workspaces/{ws}/imports/{importId}/errors?limit=1&cursor={Uri.EscapeDataString(cursor!)}", UriKind.Relative), Ct)).RootElement;
        static string Describe(JsonElement page) => string.Join(';', page.GetProperty("items").EnumerateArray().Select(i =>
            $"{i.GetProperty("row").GetInt64()},{i.GetProperty("line").GetInt64()},{i.GetProperty("severity").GetString()},{i.GetProperty("controlNumber").GetString()},{(i.GetProperty("column").ValueKind == JsonValueKind.Null ? "" : i.GetProperty("column").GetString())},{i.GetProperty("code").GetString()}"));
        Describe(first).Should().Be("2,3,error,API-2,DATESENT,invalid-date");
        Describe(second).Should().Be("3,4,error,API-1,,duplicate-control-number");
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

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
