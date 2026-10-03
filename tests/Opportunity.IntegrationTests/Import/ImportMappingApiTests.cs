using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;

using Opportunity.Core.Fields;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T02 over HTTP and PostgreSQL (app login, RLS on): saved import profiles with ETags, export/copy between
/// workspaces, mapping targets and the multipart mapping preview re-applying a saved profile to a later load.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ImportMappingApiTests(MigrationPostgresFixture postgres)
{
    private const char Dc4 = '\u0014';

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Dat(params string[][] rows) =>
        string.Concat(rows.Select(r => string.Join(Dc4, r.Select(v => "þ" + v + "þ")) + "\r\n"));

    private static readonly string FirstVolume = Dat(
        ["BEGDOC", "ENDDOC", "CUSTODIAN", "AllCustodians", "DATESENT", "TIMESENT", "Confidential"],
        ["VOL001-0001", "VOL001-0001", "Smith", "Smith; Doe; Smith", "07/04/2019", "09:30 AM", "Y"],
        ["VOL001-0002", "VOL001-0002", "Doe", "", "00/00/0000", "", "maybe"]);

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(CoreSchemaDatabase db, ApiFactory factory, HttpClient client)
        {
            Db = db;
            Factory = factory;
            Client = client;
        }

        public CoreSchemaDatabase Db { get; }

        public ApiFactory Factory { get; }

        public HttpClient Client { get; }

        public static async Task<Harness> CreateAsync(MigrationPostgresFixture postgres)
        {
            var db = await CoreSchemaDatabase.CreateAsync(postgres);
            var factory = new ApiFactory();
            var configured = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:App", db.AppConnectionString));
            return new Harness(db, factory, configured.CreateClient());
        }

        public async Task<Guid> WorkspaceAsync()
        {
            var ws = await Db.CreateWorkspaceAsync();
            await Db.Fields.InitializeWorkspaceAsync(ws, Ct);
            return ws;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private static Uri Url(Guid ws, string path) => new($"/api/v1/workspaces/{ws}{path}", UriKind.Relative);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<JsonElement> PreviewAsync(HttpClient client, Guid ws, string dat, object request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(dat)]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "volume.dat");
        form.Add(new StringContent(JsonSerializer.Serialize(request, JsonSerializerOptions.Web)), "request");
        using var response = await client.PostAsync(Url(ws, "/import-mapping-previews"), form, Ct);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync(Ct));
        return await JsonAsync(response);
    }

    private static JsonElement Column(JsonElement preview, string name) =>
        preview.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("column").GetString() == name);

    private static JsonElement Cell(JsonElement row, string column) =>
        row.GetProperty("cells").EnumerateArray().First(c => c.GetProperty("column").GetString() == column);

    [Fact]
    public async Task Preview_auto_maps_coerces_and_counts_errors_then_the_profile_is_saved_and_re_applied_to_a_later_load()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();

        var first = await PreviewAsync(h.Client, ws, FirstVolume, new
        {
            profile = new
            {
                parsing = new { sourceTimeZone = "America/New_York" },
                columns = new object[]
                {
                    new { column = "Confidential", targets = new[] { new { kind = "newField", newField = new { name = "Confidential Flag", type = "boolean" } } } },
                },
            },
        });

        first.GetProperty("canImport").GetBoolean().Should().BeTrue();
        first.GetProperty("file").GetProperty("column").GetProperty("decimal").GetInt32().Should().Be(20);
        var begdoc = Column(first, "BEGDOC").GetProperty("targets");
        begdoc.EnumerateArray().Select(t => t.GetProperty("label").GetString()).Should().Equal("Control Number", "Beg Bates");
        begdoc[0].GetProperty("matchedBy").GetString().Should().Be("alias");
        begdoc[0].GetProperty("alias").GetString().Should().Be("BEGDOC");
        Column(first, "TIMESENT").GetProperty("status").GetString().Should().Be("mergedIntoDate");
        Column(first, "Confidential").GetProperty("errorCount").GetInt32().Should().Be(1);
        Column(first, "DATESENT").GetProperty("errorCount").GetInt32().Should().Be(0);
        var rows = first.GetProperty("rows");
        Cell(rows[0], "DATESENT").GetProperty("value").GetString().Should().Be("2019-07-04T13:30:00Z");
        Cell(rows[0], "AllCustodians").GetProperty("value").EnumerateArray().Select(v => v.GetString()).Should().Equal("Smith", "Doe");
        Cell(rows[1], "DATESENT").GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
        Cell(rows[1], "DATESENT").GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        Cell(rows[1], "Confidential").GetProperty("error").GetProperty("code").GetString().Should().Be("invalid-boolean");
        first.GetProperty("newFields").EnumerateArray().Select(f => f.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["Custodian", "All Custodians", "Confidential Flag"]);

        // Save as Import profile.
        var effective = JsonNode.Parse(first.GetProperty("effectiveProfile").GetRawText());
        using var created = await h.Client.PostAsJsonAsync(Url(ws, "/import-profiles"),
            new JsonObject { ["name"] = "Vendor A volumes", ["description"] = "DC4/þ/®, US dates", ["definition"] = effective }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.ETag!.Tag.Should().Be("\"1\"");
        var profileId = (await JsonAsync(created)).GetProperty("profileId").GetGuid();
        created.Headers.Location!.ToString().Should().EndWith($"/import-profiles/{profileId}");

        // A later load with a renamed and an extra column: missing and new columns are explicit; mappings are kept.
        var second = Dat(
            ["BEGDOC", "ENDDOC", "Custodian Name", "AllCustodians", "DATESENT", "TIMESENT", "Confidential", "Folder"],
            ["VOL002-0001", "VOL002-0001", "Roe", "Roe;Roe", "12/01/2019", "11:00 PM", "N", @"Inbox\Projects"]);
        var reapplied = await PreviewAsync(h.Client, ws, second, new { profileId });

        reapplied.GetProperty("missingColumns").EnumerateArray().Select(c => c.GetString()).Should().Equal("CUSTODIAN");
        reapplied.GetProperty("newColumns").EnumerateArray().Select(c => c.GetString()).Should().Equal("Custodian Name", "Folder");
        Column(reapplied, "BEGDOC").GetProperty("targets")[0].GetProperty("matchedBy").GetString().Should().Be("profile");
        Column(reapplied, "Folder").GetProperty("targets")[0].GetProperty("label").GetString().Should().Be("Folder Path");
        var row = reapplied.GetProperty("rows")[0];
        Cell(row, "DATESENT").GetProperty("value").GetString().Should().Be("2019-12-02T04:00:00Z");
        Cell(row, "AllCustodians").GetProperty("value").EnumerateArray().Select(v => v.GetString()).Should().Equal("Roe");
        Cell(row, "Confidential").GetProperty("value").GetBoolean().Should().BeFalse();
        reapplied.GetProperty("effectiveProfile").GetProperty("columns").EnumerateArray()
            .Should().Contain(c => c.GetProperty("column").GetString() == "CUSTODIAN");
    }

    [Fact]
    public async Task Profiles_are_versioned_with_etags_unique_by_name_and_deleted_with_if_match()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();
        var body = new { name = "Pilcrow vendor", definition = new { loadFile = new { delimiters = "concordance-pilcrow" }, mode = "appendOverlay" } };

        using var created = await h.Client.PostAsJsonAsync(Url(ws, "/import-profiles"), body, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await JsonAsync(created)).GetProperty("profileId").GetGuid();
        using var duplicate = await h.Client.PostAsJsonAsync(Url(ws, "/import-profiles"), body with { name = " PILCROW VENDOR " }, Ct);
        await duplicate.ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");

        using var list = await h.Client.GetAsync(Url(ws, "/import-profiles"), Ct);
        var page = await JsonAsync(list);
        page.GetProperty("items").EnumerateArray().Single().GetProperty("mode").GetString().Should().Be("appendOverlay");
        page.GetProperty("total").GetProperty("value").GetInt64().Should().Be(1);

        var put = new HttpRequestMessage(HttpMethod.Put, Url(ws, $"/import-profiles/{id}")) { Content = JsonContent.Create(body with { name = "Pilcrow vendor v2" }) };
        using (var missingIfMatch = await h.Client.SendAsync(put, Ct))
        {
            await missingIfMatch.ShouldBeProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
        }

        using (var updated = await h.Client.SendAsync(Versioned(HttpMethod.Put, Url(ws, $"/import-profiles/{id}"), "\"1\"", body with { name = "Pilcrow vendor v2" }), Ct))
        {
            updated.StatusCode.Should().Be(HttpStatusCode.OK);
            updated.Headers.ETag!.Tag.Should().Be("\"2\"");
            (await JsonAsync(updated)).GetProperty("name").GetString().Should().Be("Pilcrow vendor v2");
        }

        using (var stale = await h.Client.SendAsync(Versioned(HttpMethod.Delete, Url(ws, $"/import-profiles/{id}"), "\"1\""), Ct))
        {
            await stale.ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");
        }

        using (var deleted = await h.Client.SendAsync(Versioned(HttpMethod.Delete, Url(ws, $"/import-profiles/{id}"), "\"2\""), Ct))
        {
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using var gone = await h.Client.GetAsync(Url(ws, $"/import-profiles/{id}"), Ct);
        await gone.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task Invalid_profiles_are_rejected_with_field_level_validation_errors()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();

        using var response = await h.Client.PostAsJsonAsync(Url(ws, "/import-profiles"), new
        {
            name = "",
            definition = new
            {
                parsing = new { sourceTimeZone = "Mars/Olympus" },
                columns = new object[] { new { column = "A", targets = new[] { new { kind = "field" } } } },
            },
        }, Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        var errors = (await JsonAsync(response)).GetProperty("errors");
        errors.GetProperty("name")[0].GetString().Should().StartWith("invalid-name");
        errors.GetProperty("definition.columns[0].targets[0]")[0].GetString().Should().StartWith("invalid-target");
        errors.GetProperty("definition")[0].GetString().Should().StartWith("invalid-time-zone");
    }

    [Fact]
    public async Task An_exported_profile_copies_into_another_workspace_and_profiles_never_leak_across_workspaces()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var a = await h.WorkspaceAsync();
        var b = await h.WorkspaceAsync();
        await h.Db.Fields.CreateFieldAsync(new Application.Fields.NewField(b, "Spacer", FieldType.Keyword, FieldStorage.Metadata), Ct);
        var custodianA = (await h.Db.Fields.CreateFieldAsync(new Application.Fields.NewField(a, "Custodian", FieldType.Keyword, FieldStorage.Metadata), Ct)).Value!;
        var custodianB = (await h.Db.Fields.CreateFieldAsync(new Application.Fields.NewField(b, "Custodian", FieldType.Keyword, FieldStorage.Metadata), Ct)).Value!;
        custodianB.FieldId.Should().NotBe(custodianA.FieldId);

        using var created = await h.Client.PostAsJsonAsync(Url(a, "/import-profiles"), new
        {
            name = "Shared layout",
            definition = new
            {
                columns = new object[]
                {
                    new { column = "DOCID", targets = new[] { new { kind = "field", fieldId = 1, fieldName = "Control Number" } } },
                    new { column = "CUST", targets = new[] { new { kind = "field", fieldId = custodianA.FieldId, fieldName = "Custodian" } } },
                },
            },
        }, Ct);
        var id = (await JsonAsync(created)).GetProperty("profileId").GetGuid();

        using (var foreign = await h.Client.GetAsync(Url(b, $"/import-profiles/{id}"), Ct))
        {
            await foreign.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        }

        var export = await h.Client.GetStringAsync(Url(a, $"/import-profiles/{id}"), Ct);
        using var copy = await h.Client.PostAsync(Url(b, "/import-profiles"), new StringContent(export, Encoding.UTF8, "application/json"), Ct);
        copy.StatusCode.Should().Be(HttpStatusCode.Created);
        var copyId = (await JsonAsync(copy)).GetProperty("profileId").GetGuid();

        var preview = await PreviewAsync(h.Client, b, Dat(["DOCID", "CUST"], ["X1", "Smith"]), new { profileId = copyId, autoMap = false });
        var target = Column(preview, "CUST").GetProperty("targets")[0];
        target.GetProperty("target").GetProperty("fieldId").GetInt32().Should().Be(custodianB.FieldId);
        target.GetProperty("resolution").GetString().Should().Be("resolvedByName");
        preview.GetProperty("canImport").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Targets_list_structural_targets_first_and_the_preview_validates_its_input()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();

        var targets = await JsonAsync(await h.Client.GetAsync(Url(ws, "/import-targets"), Ct));
        targets.GetProperty("items")[0].GetProperty("label").GetString().Should().Be("Control Number");
        targets.GetProperty("items").EnumerateArray().Should().Contain(t => t.GetProperty("label").GetString() == "Family/Group ID");

        await PreviewAsync(h.Client, ws, FirstVolume, new { rows = 0 }, HttpStatusCode.BadRequest);
        await PreviewAsync(h.Client, ws, FirstVolume, new { profileId = Guid.CreateVersion7() }, HttpStatusCode.NotFound);
        var partial = await PreviewAsync(h.Client, ws, FirstVolume[..^10], new { sampleIsPartial = true });
        partial.GetProperty("rows").GetArrayLength().Should().Be(1);
    }

    private static HttpRequestMessage Versioned(HttpMethod method, Uri uri, string etag, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
        return request;
    }
}
