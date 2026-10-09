using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Application.Storage;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Import.Jobs;
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

    [Fact]
    public async Task A_request_part_sent_as_a_json_file_part_is_honoured()
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

        // Browser FormData sends a Blob, and many HTTP clients send JSON, as a file part. It used to be ignored, so the
        // import ran with default settings instead of the caller's profile (found by the vertical-slice E2E suite).
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(Header, ["PART-1", "Smith", "2020-01-02"]));
        using var response = await PostAsync(
            client, ws, dat, new { name = "Sent as a part", profile = new { controlNumberPrefix = "P-" } }, "part-1", requestAsFile: true);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var started = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        started.GetProperty("name").GetString().Should().Be("Sent as a part");

        var batch = (await h.Batches.GetAsync(ws, started.GetProperty("importId").GetGuid(), Ct))!;
        await h.RunAsync(batch);
        (await h.Db.ScalarAsync<long>("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws AND control_number = 'P-PART-1'", ("ws", ws)))
            .Should().Be(1);
    }

    private static readonly string[] NotesHeader = ["BEGDOC", "NOTES", "EXTRA"];

    /// <summary>NOTES reuses the existing field "Reviewer Notes" by name (a new-field target whose name exists creates nothing).</summary>
    private static object NotesColumn() =>
        new { column = "NOTES", targets = new[] { new { kind = "newField", newField = new { name = "Reviewer Notes", type = "text" } } } };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_field_renamed_or_deleted_after_a_start_without_ManageFields_is_not_created_and_the_import_fails(bool delete)
    {
        await using var h = await ImportHarness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();
        var notes = (await h.Db.Fields.CreateFieldAsync(new NewField(ws, "Reviewer Notes", FieldType.Text, FieldStorage.Metadata), Ct)).Value!;
        await using var factory = new ApiFactory();
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", h.Db.AppConnectionString);
            b.ConfigureTestServices(s =>
            {
                s.Replace(ServiceDescriptor.Singleton(h.Store));
                PermissionsWithout(s, Permission.WorkspaceManageFields, Permission.ImportOverlay);
            });
        });
        using var client = app.CreateClient();
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(NotesHeader[..2], ["TOC-1", "a note"], ["TOC-2", "another"]));

        // The principal holds Import.Run but not Workspace.ManageFields: a mapping that creates a field is refused ...
        using (var refused = await PostAsync(client, ws, ImportHarness.Utf8Bom(ImportHarness.Dat(NotesHeader, ["TOC-1", "a note", "x"])),
            new { autoMap = true, profile = new { columns = new object[] { NotesColumn(), NewTextColumn("EXTRA", "Extra Field") } } }, "toc-refused"))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        // ... and one that only reuses existing fields is accepted.
        using var response = await PostAsync(client, ws, dat, new { autoMap = true, profile = new { columns = new object[] { NotesColumn() } } }, "toc-1");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var importId = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("importId").GetGuid();
        var batch = (await h.Batches.GetAsync(ws, importId, Ct))!;
        batch.MayCreateFields.Should().BeFalse();

        // Between start and preparation the mapped field goes away: the recompiled mapping would now create it.
        var changed = delete
            ? await h.Db.Fields.DeleteFieldAsync(ws, notes.FieldId, Ct)
            : await h.Db.Fields.UpdateFieldAsync(new FieldChange(ws, notes.FieldId) { Name = "Reviewer Notes (old)" }, Ct);
        changed.Succeeded.Should().BeTrue();

        (await h.PrepareAsync(batch)).Should().Be(ImportPreparationOutcome.Failed);

        var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        catalog.Fields.Where(f => !f.IsDeleted && f.Name == "Reviewer Notes").Should().BeEmpty("field creation was never authorized");
        var job = (await h.Jobs.GetAsync(ws, batch.JobId, Ct))!;
        job.Status.Should().Be(JobStatus.Failed);
        job.StatusReason.Should().StartWith(ImportStartScope.FieldCreationNotAuthorized);
        (await h.Db.ColumnAsync(
            $"SELECT action || ':' || outcome || ':' || reason_code FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Import' AND action = 'Completed'"))
            .Should().Equal("Completed:Failure:" + ImportStartScope.FieldCreationNotAuthorized);
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ws)).Should().Be(0);
    }

    [Fact]
    public async Task A_start_authorized_to_create_fields_still_creates_a_mapped_field_renamed_before_preparation()
    {
        await using var h = await ImportHarness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();
        var notes = (await h.Db.Fields.CreateFieldAsync(new NewField(ws, "Reviewer Notes", FieldType.Text, FieldStorage.Metadata), Ct)).Value!;
        await using var factory = new ApiFactory();
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", h.Db.AppConnectionString);
            b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton(h.Store)));
        });
        using var client = app.CreateClient();

        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(NotesHeader, ["TOC-1", "a note", "x"], ["TOC-2", "another", "y"]));
        using var response = await PostAsync(client, ws, dat,
            new { autoMap = true, profile = new { columns = new object[] { NotesColumn(), NewTextColumn("EXTRA", "Extra Field") } } }, "toc-ok");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var importId = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("importId").GetGuid();
        var batch = (await h.Batches.GetAsync(ws, importId, Ct))!;
        batch.MayCreateFields.Should().BeTrue();

        (await h.Db.Fields.UpdateFieldAsync(new FieldChange(ws, notes.FieldId) { Name = "Reviewer Notes (old)" }, Ct)).Succeeded.Should().BeTrue();

        (await h.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
        var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        catalog.Fields.Where(f => !f.IsDeleted).Select(f => f.Name).Should().Contain(["Reviewer Notes", "Reviewer Notes (old)", "Extra Field"]);
        (await h.BatchAsync(batch)).Preparation!.FieldsCreated.Should().Be(2);
    }

    private static object NewTextColumn(string column, string name) =>
        new { column, targets = new[] { new { kind = "newField", newField = new { name, type = "text" } } } };

    /// <summary>The real PDP, except that the given workspace permissions are never granted.</summary>
    private static void PermissionsWithout(IServiceCollection services, params Permission[] withheld)
    {
        var real = services.Last(d => d.ServiceType == typeof(IAuthorizationService));
        services.RemoveAll<IAuthorizationService>();
        services.AddScoped<IAuthorizationService>(sp => new WithheldPermissions(
            (IAuthorizationService)(real.ImplementationType is { } type
                ? ActivatorUtilities.CreateInstance(sp, type)
                : real.ImplementationFactory!(sp)),
            [.. withheld]));
    }

    private sealed class WithheldPermissions(IAuthorizationService inner, HashSet<Permission> withheld) : IAuthorizationService
    {
        public Task<AuthorizationDecision> AuthorizeAsync(
            SecurityPrincipal principal, Guid workspaceId, Permission permission, CancellationToken cancellationToken = default) =>
            withheld.Contains(permission)
                ? Task.FromResult(AuthorizationDecision.Deny(AuthorizationReasons.PermissionNotGranted))
                : inner.AuthorizeAsync(principal, workspaceId, permission, cancellationToken);

        public Task<AuthorizationDecision> AuthorizeAsync(
            SecurityPrincipal principal, Guid workspaceId, Permission permission, Guid documentId, CancellationToken cancellationToken = default) =>
            withheld.Contains(permission)
                ? Task.FromResult(AuthorizationDecision.Deny(AuthorizationReasons.PermissionNotGranted))
                : inner.AuthorizeAsync(principal, workspaceId, permission, documentId, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, AuthorizationDecision>> AuthorizeManyAsync(
            SecurityPrincipal principal, Guid workspaceId, Permission permission, IReadOnlyCollection<Guid> documentIds,
            DenialAudit audit = DenialAudit.PerDocument, CancellationToken cancellationToken = default) =>
            inner.AuthorizeManyAsync(principal, workspaceId, permission, documentIds, audit, cancellationToken);

        public Task<AuthorizationDecision> AuthorizeMembershipAsync(
            SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default) =>
            inner.AuthorizeMembershipAsync(principal, workspaceId, cancellationToken);

        public Task<AuthorizationDecision> AuthorizeBreakGlassHolderAsync(
            SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default) =>
            inner.AuthorizeBreakGlassHolderAsync(principal, workspaceId, cancellationToken);

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default) =>
            inner.GetEffectivePermissionsAsync(principal, workspaceId, cancellationToken);

        public Task<VisibilityResult> GetVisibilityAsync(
            SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default) =>
            inner.GetVisibilityAsync(principal, workspaceId, cancellationToken);

        public Task<DocumentAccessCheck> GetDocumentAccessCheckAsync(
            SecurityPrincipal principal, Guid workspaceId, Permission permission, CancellationToken cancellationToken = default) =>
            inner.GetDocumentAccessCheckAsync(principal, workspaceId, permission, cancellationToken);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid ws, byte[] dat, object request, string key, bool requestAsFile = false)
    {
        using var form = new MultipartFormDataContent("import-test-boundary");
        var file = new ByteArrayContent(dat);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "VOL001.dat");
        var json = new StringContent(JsonSerializer.Serialize(request, JsonSerializerOptions.Web), Encoding.UTF8, "application/json");
        if (requestAsFile)
        {
            form.Add(json, "request", "request.json");
        }
        else
        {
            form.Add(json, "request");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workspaces/{ws}/imports", UriKind.Relative)) { Content = form };
        message.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(message, Ct);
    }
}
