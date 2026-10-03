using System.Globalization;
using System.Net;
using System.Web;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Content;
using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Storage;
using Opportunity.IntegrationTests.Telemetry;
using Opportunity.Storage;
using Opportunity.Storage.S3;

namespace Opportunity.IntegrationTests.Content;

/// <summary>
/// E05-T04 presigned native delivery against a real S3-compatible store (credentials enforced) and the log-scrubbing
/// proof: the URL is single-object, GET-only, expires in 60 s, forces an attachment of type octet-stream, is returned
/// only in the <c>Location</c> of a <c>no-store</c> 303 after the PDP and the audit, and never appears in logs, spans or
/// audit records.
/// </summary>
[Collection(S3StoreGroup.Name)]
public sealed class PresignedNativeDeliveryTests(S3StoreFixture s3, MigrationPostgresFixture postgres) : IClassFixture<MigrationPostgresFixture>
{
    private readonly InMemoryAuditEventWriter _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Native_download_redirects_to_a_short_lived_single_object_url_that_never_reaches_logs_traces_or_audit()
    {
        var s3Options = s3.Options("gateway-" + Guid.NewGuid().ToString("N")[..8], S3ObjectStoreOptions.MinPartSizeBytes);
        using var store = new S3ObjectStore(s3Options);
        await using var db = await ContentDatabase.CreateAsync(postgres, store);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var manager = await db.Security.CreateUserAsync();
        var reviewer = await db.Security.CreateUserAsync();
        await db.Security.AssignAsync(ws, WorkspaceRole.ProductionManager, manager);
        await db.Security.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        var doc = await db.DocumentAsync(ws);
        await using var factory = Factory(db, s3Options);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var path = $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}/native";

        await (await ProtectedContentGatewayTests.GetAsync(client, path, reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        using var redirect = await ProtectedContentGatewayTests.GetAsync(client, path, manager);
        redirect.StatusCode.Should().Be(HttpStatusCode.SeeOther);
        redirect.Headers.CacheControl!.NoStore.Should().BeTrue();
        redirect.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
        (await redirect.Content.ReadAsByteArrayAsync(Ct)).Should().BeEmpty();
        var url = redirect.Headers.Location!;
        url.IsAbsoluteUri.Should().BeTrue();
        url.AbsolutePath.Should().NotContain(doc.ControlNumber, "the key is system-generated, not the control number");
        var query = HttpUtility.ParseQueryString(url.Query);
        var signature = query["X-Amz-Signature"];
        signature.Should().NotBeNullOrEmpty();
        int.Parse(query["X-Amz-Expires"]!, CultureInfo.InvariantCulture).Should().BeInRange(59, 61, "ADR-015 D12.3 default TTL of 60 s (the SDK rounds the absolute expiry by up to a second)");

        // The URL works for exactly this object, with the forced download headers.
        using var browser = new HttpClient();
        using var fetched = await browser.GetAsync(url, Ct);
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        (await fetched.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(doc.Native);
        fetched.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        fetched.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        fetched.Content.Headers.ContentDisposition.FileNameStar.Should().Be(doc.ControlNumber + ".docx");

        // GET only, and only that key.
        using var put = await browser.PutAsync(url, new ByteArrayContent([1, 2, 3]), Ct);
        put.IsSuccessStatusCode.Should().BeFalse();
        var otherKey = new UriBuilder(url) { Path = url.AbsolutePath.Replace("/native/", "/text/", StringComparison.Ordinal) }.Uri;
        using var other = await browser.GetAsync(otherKey, Ct);
        other.IsSuccessStatusCode.Should().BeFalse();

        var download = _audit.Events.Should().ContainSingle(e => e.Outcome == AuditOutcome.Success).Subject;
        (download.Action, download.Details["deliveryMode"], download.Details["objectId"]).Should().Be(("NativeDownloaded", "Presign", doc.NativeObjectId.ToString()));
        redirect.Headers.GetValues(ProtectedContentGateway.RetrievalIdHeader).Should().Equal(download.EventId.ToString());

        // Log scrubbing: nothing the API logged, traced or audited carries the URL or its signature.
        await factory.Capture.WaitForSpanAsync(s => s.Kind == System.Diagnostics.ActivityKind.Server && s.DisplayName.Contains("native", StringComparison.Ordinal), Ct);
        var needles = new[] { signature!, url.Query.TrimStart('?'), url.AbsoluteUri };
        var logText = factory.Capture.Logs.SelectMany(l => l.Text()).ToList();
        var spanText = factory.Capture.Spans.Snapshot().SelectMany(s => factory.Capture.TextOf(s.TraceId)).ToList();
        var auditText = _audit.Events.SelectMany(e => e.Details.Values.Append(e.ReasonCode).Append(e.CorrelationId)).OfType<string>().ToList();
        logText.Should().NotBeEmpty();
        spanText.Should().NotBeEmpty();
        foreach (var needle in needles)
        {
            logText.Should().NotContain(t => t.Contains(needle, StringComparison.Ordinal), "logs");
            spanText.Should().NotContain(t => t.Contains(needle, StringComparison.Ordinal), "traces");
            auditText.Should().NotContain(t => t.Contains(needle, StringComparison.Ordinal), "audit");
        }

        logText.Concat(spanText).Should().NotContain(t => t.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Viewer_renditions_are_streamed_from_the_app_origin_even_with_presigning_enabled()
    {
        var s3Options = s3.Options("gateway-" + Guid.NewGuid().ToString("N")[..8], S3ObjectStoreOptions.MinPartSizeBytes);
        using var store = new S3ObjectStore(s3Options);
        await using var db = await ContentDatabase.CreateAsync(postgres, store);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var reviewer = await db.Security.CreateUserAsync();
        await db.Security.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        var doc = await db.DocumentAsync(ws);
        await using var factory = Factory(db, s3Options);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var image = await ProtectedContentGatewayTests.GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{doc.DocumentId}/pages/1/image", reviewer);

        image.StatusCode.Should().Be(HttpStatusCode.OK);
        (await image.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(doc.PageImage);
        ProtectedContentGatewayTests.AssertProtectedContentHeaders(image);
    }

    /// <summary>The telemetry-enabled API host (its <see cref="TelemetryApiFactory.Capture"/> sees every log and span) with S3 and presigned natives.</summary>
    private Host Factory(ContentDatabase db, S3ObjectStoreOptions s3Options)
    {
        var telemetry = new TelemetryApiFactory(db.Security.Core.AppConnectionString);
        return new Host(telemetry, telemetry.WithWebHostBuilder(builder => Configure(builder, s3Options)));
    }

    private void Configure(IWebHostBuilder builder, S3ObjectStoreOptions s3Options)
    {
        builder.UseSetting("ObjectStorage:Provider", nameof(ObjectStorageProvider.S3));
        builder.UseSetting("ObjectStorage:S3:ServiceUrl", s3Options.ServiceUrl!.ToString());
        builder.UseSetting("ObjectStorage:S3:Region", s3Options.Region);
        builder.UseSetting("ObjectStorage:S3:Bucket", s3Options.Bucket);
        builder.UseSetting("ObjectStorage:S3:InstallationPrefix", s3Options.InstallationPrefix);
        builder.UseSetting("ObjectStorage:S3:AccessKey", s3Options.AccessKey);
        builder.UseSetting("ObjectStorage:S3:SecretKey", s3Options.SecretKey);
        builder.UseSetting("ProtectedContent:NativeDelivery", nameof(NativeDelivery.Presign));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISecurityStateReader>();
            services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
            services.AddSingleton<IAuditEventWriter>(_audit);
        });
    }

    private sealed class Host(TelemetryApiFactory telemetry, WebApplicationFactory<Program> app) : IAsyncDisposable
    {
        public TelemetryCapture Capture => telemetry.Capture;

        public HttpClient CreateClient(WebApplicationFactoryClientOptions options) => app.CreateClient(options);

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            await telemetry.DisposeAsync();
        }
    }
}
