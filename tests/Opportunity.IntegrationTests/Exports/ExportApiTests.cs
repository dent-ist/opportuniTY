using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;

using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Import.Volumes;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Content;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Exports;

/// <summary>
/// E12-T01 through the real API host, the PostgreSQL PDP and a filesystem object store: <c>POST …/exports</c> answers 202
/// with the job (Export.Create, Idempotency-Key), the worker writes the volume, and the protected-content gateway serves
/// the files (single range) and the whole package as a ZIP to the export's creator with Export.Download only, each
/// download audited.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ExportApiTests(MigrationPostgresFixture postgres)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_export_is_created_with_202_and_its_files_and_package_download_through_the_gateway_for_its_creator_only()
    {
        var root = ExportRoundTripTests.TempDirectory("opp-export-api-");
        try
        {
            CorpusRunner.Run(ProfileSerializer.WithDocumentCount(new CorpusProfile(), 6), 20261007, root, new CorpusRunOptions
            {
                Threads = 1,
                WriteGroundTruth = false,
                SinkFactories = [context => new VolumeWriter(context, root, new VolumeOptions())],
            });
            await using var h = await ExportHarness.CreateAsync(postgres, documentsPerChunk: 4);
            h.Import.Volumes = new ImportVolumeOptions { VolumeShareRoot = root };
            var ws = await h.Import.WorkspaceAsync();
            var batch = await h.Import.StartAsync(ws, await File.ReadAllBytesAsync(Path.Combine(root, "VOL001", "DATA", "VOL001.dat"), Ct),
                new() { Paths = new() { VolumeRoot = "VOL001" } }, name: "VOL001.dat",
                opt: await File.ReadAllBytesAsync(Path.Combine(root, "VOL001", "DATA", "VOL001.opt"), Ct));
            (await h.Import.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
            var manager = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
            var otherManager = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
            var reviewer = await h.UserAsync(ws, WorkspaceRole.Reviewer);
            var snapshot = await h.SnapshotAsync(ws, manager, await ExportRoundTripTests.DocumentIdsAsync(h, ws));

            await using var factory = new ApiFactory().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:App", h.Db.AppConnectionString);
                builder.UseSetting("ObjectStorage:Provider", "FileSystem");
                builder.UseSetting("ObjectStorage:FileSystem:RootPath", h.Import.StoreRoot);
                builder.UseSetting("Snapshots:BackgroundEnabled", "false");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ISecurityStateReader>();
                    services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
                });
            });
            using var client = factory.CreateClient();
            var url = $"/api/v1/workspaces/{ws}/exports";
            var body = JsonSerializer.Serialize(new CreateExportRequest(snapshot.SnapshotId,
                [new(FieldId: SystemFields.ControlNumber, Header: "BEGDOC"), new(FieldId: SystemFields.FileName), new(Column: ExportColumnResource.ParentId)]), Json);

            // Creating needs Export.Create; the request is validated.
            (await SendAsync(client, HttpMethod.Post, url, reviewer, body, "key-r")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using (var invalid = await SendAsync(client, HttpMethod.Post, url, manager,
                JsonSerializer.Serialize(new CreateExportRequest(snapshot.SnapshotId, [new(FieldId: 987_654)], Delimiters: "pipes"), Json), "key-i"))
            {
                invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("errors");
                problem.TryGetProperty("fields[0]", out _).Should().BeTrue();
                problem.TryGetProperty("delimiters", out _).Should().BeTrue();
            }

            ExportResource created;
            using (var accepted = await SendAsync(client, HttpMethod.Post, url, manager, body, "key-1"))
            {
                accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
                created = (await accepted.Content.ReadFromJsonAsync<ExportResource>(Json, Ct))!;
                accepted.Headers.Location!.ToString().Should().Be($"/api/v1/workspaces/{ws}/jobs/{created.Job.JobId}");
            }

            created.Status.Should().Be(ExportResourceStatus.Running);
            created.Job.JobType.Should().Be(JobResourceType.Export);
            created.Job.TargetSnapshotId.Should().Be(snapshot.SnapshotId);
            created.Settings.Fields.Select(f => f.Header).Should().Equal("BEGDOC", "File Name", "ParentID", "NativePath", "TextPath");
            using (var retried = await SendAsync(client, HttpMethod.Post, url, manager, body, "key-1"))
            {
                (await retried.Content.ReadFromJsonAsync<ExportResource>(Json, Ct))!.ExportId.Should().Be(created.ExportId, "the Idempotency-Key replays");
            }

            // Files are not served before the export completed.
            (await SendAsync(client, HttpMethod.Get, $"{url}/{created.ExportId}/package", manager)).StatusCode.Should().Be(HttpStatusCode.Conflict);

            // The export worker runs the job.
            await h.CoordinateAsync(ws);
            await h.DeliverOpenChunksAsync(ws, created.Job.JobId);
            await h.CoordinateAsync(ws);

            using (var get = await SendAsync(client, HttpMethod.Get, $"{url}/{created.ExportId}", manager))
            {
                var export = (await get.Content.ReadFromJsonAsync<ExportResource>(Json, Ct))!;
                export.Status.Should().Be(ExportResourceStatus.Completed);
                export.Report!.DocumentsExported.Should().Be(6);
                export.Job.Status.Should().Be(JobResourceStatus.Completed);
            }

            (await SendAsync(client, HttpMethod.Get, $"{url}/{created.ExportId}", reviewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            using (var exclusions = await SendAsync(client, HttpMethod.Get, $"{url}/{created.ExportId}/exclusions", manager))
            {
                (await exclusions.Content.ReadFromJsonAsync<CursorPage<ExportExclusionResource>>(Json, Ct))!.Items.Should().BeEmpty();
            }

            CursorPage<ExportFileResource> listing;
            using (var files = await SendAsync(client, HttpMethod.Get, $"{url}/{created.ExportId}/files?limit=500", manager))
            {
                files.StatusCode.Should().Be(HttpStatusCode.OK);
                listing = (await files.Content.ReadFromJsonAsync<CursorPage<ExportFileResource>>(Json, Ct))!;
            }

            listing.Items.Select(f => f.Path).Should().Contain(["MANIFEST.json", "MANIFEST.csv", "EXCLUSIONS.csv", "VOL001/DATA/VOL001.dat", "VOL001/DATA/VOL001.opt"]);
            listing.Items.Should().Contain(f => f.Kind == ExportFileResourceKind.Native && f.Path.StartsWith("VOL001/NATIVES/NATIVE0001/", StringComparison.Ordinal));
            listing.Items.Should().NotContain(f => f.Path.StartsWith("parts/", StringComparison.Ordinal));

            // One file, whole and as a range, verified against the listed SHA-256.
            var native = listing.Items.First(f => f.Kind == ExportFileResourceKind.Native);
            var fileUrl = $"{url}/{created.ExportId}/files/{native.FileId}/content";
            byte[] whole;
            using (var full = await SendAsync(client, HttpMethod.Get, fileUrl, manager))
            {
                full.StatusCode.Should().Be(HttpStatusCode.OK);
                full.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
                ProtectedContentGatewayTests.AssertProtectedContentHeaders(full);
                whole = await full.Content.ReadAsByteArrayAsync(Ct);
                Convert.ToHexStringLower(SHA256.HashData(whole)).Should().Be(native.Sha256);
            }

            using (var partial = await SendAsync(client, HttpMethod.Get, fileUrl, manager, range: "bytes=2-9"))
            {
                partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
                (await partial.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(whole[2..10]);
                partial.Content.Headers.ContentRange!.ToString().Should().Be($"bytes 2-9/{whole.Length}");
            }

            (await SendAsync(client, HttpMethod.Get, fileUrl, manager, range: $"bytes={whole.Length + 10}-")).StatusCode
                .Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);

            // The package: every listed file under its path, intact.
            using (var package = await SendAsync(client, HttpMethod.Get, $"{url}/{created.ExportId}/package", manager))
            {
                package.StatusCode.Should().Be(HttpStatusCode.OK);
                package.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
                await using var zipStream = new MemoryStream(await package.Content.ReadAsByteArrayAsync(Ct));
                await using var zip = await ZipArchive.CreateAsync(zipStream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: null, Ct);
                zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(listing.Items.Select(f => f.Path));
                foreach (var entry in zip.Entries)
                {
                    await using var content = await entry.OpenAsync(Ct);
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(content, Ct)).Should().Be(listing.Items.Single(f => f.Path == entry.FullName).Sha256, entry.FullName);
                }
            }

            // Only the creator, and only with Export.Download.
            (await SendAsync(client, HttpMethod.Get, $"{url}/{created.ExportId}/package", otherManager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await SendAsync(client, HttpMethod.Get, fileUrl, otherManager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await SendAsync(client, HttpMethod.Get, $"{url}/{created.ExportId}/package", reviewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await SendAsync(client, HttpMethod.Get, $"{url}/{created.ExportId}/files", reviewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var downloads = await h.Db.ColumnAsync(
                $"SELECT actor_id || '|' || coalesce(details->>'Range', '') || '|' || coalesce(details->>'Package', '') FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Export' AND action = 'Downloaded'");
            downloads.Should().BeEquivalentTo([$"{manager}||", $"{manager}|2-9|", $"{manager}||zip"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid user, string? body = null, string? key = null, string? range = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }
}
