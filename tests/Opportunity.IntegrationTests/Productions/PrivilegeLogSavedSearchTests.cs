using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Application.Coding;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Snapshots;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// #182 through the real API host: a privilege log of a saved search (frozen as the generator sees it), the entries
/// with their metadata columns and privilege basis, the per-document description edited through coding (audited, with
/// history), and a regeneration that is the next version of the same log with a diff of what changed.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class PrivilegeLogSavedSearchTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_saved_search_log_lists_privileged_documents_and_a_regeneration_shows_what_changed()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var db = h.Search.Db.Core;
        var ws = await h.Search.WorkspaceAsync();
        await db.Fields.InitializeNewWorkspaceAsync(ws, Ct);
        var reviewer = await h.MemberAsync(ws, WorkspaceRole.PrivilegeReviewer);
        var plain = await h.MemberAsync(ws, WorkspaceRole.Reviewer);
        var withheld = await h.DocumentAsync(ws, "SSL-0001", "supply agreement advice");
        var redacted = await h.DocumentAsync(ws, "SSL-0002", "supply agreement draft");
        var later = await h.DocumentAsync(ws, "SSL-0003", "supply agreement notes");
        await h.DocumentAsync(ws, "SSL-0004", "supply agreement invoice");
        await h.DocumentAsync(ws, "SSL-0005", "unrelated withheld memo");

        var catalog = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        int Status(string key) => PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, key)!.Value;
        var attorneyClient = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.AttorneyClient)!.Value;
        var workProduct = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.WorkProduct)!.Value;
        async Task CodeAsync(Guid document, string status, int basis) => (await db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "ssl-" + Guid.CreateVersion7().ToString("N"),
            Actor = new CodingActor(reviewer, CodingActorType.Human),
            Documents = [new CodingTarget(document)],
            Operations =
            [
                CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(Status(status))),
                CodingFieldOperation.AddChoices(PrivilegeFields.Basis, basis),
            ],
        }, Ct)).Outcome.Should().Be(CodingWriteOutcome.Applied);

        await CodeAsync(withheld, PrivilegeFields.Keys.Withhold, attorneyClient);
        await CodeAsync(redacted, PrivilegeFields.Keys.Redact, workProduct);

        await using var factory = Factory(h);
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}";
        var savedSearchId = (await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/saved-searches", reviewer,
            new JsonObject { ["name"] = "Supply agreement", ["query"] = "supply" }), HttpStatusCode.Created)).GetProperty("savedSearchId").GetGuid();
        var generate = new JsonObject { ["savedSearchId"] = savedSearchId.ToString() };

        // AC 1: the saved search's privileged documents with metadata columns and the privilege basis.
        var first = (await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", reviewer, generate), HttpStatusCode.Created))
            .GetProperty("log");
        var firstId = first.GetProperty("logId").GetGuid();
        first.GetProperty("source").GetString().Should().Be("savedSearch");
        first.GetProperty("savedSearchId").GetGuid().Should().Be(savedSearchId);
        first.GetProperty("metadata").GetProperty("savedSearchName").GetString().Should().Be("Supply agreement");
        var columns = first.GetProperty("metadata").GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToList();
        columns.Should().Contain(["Privilege Basis", "Description"]);
        var description = first.GetProperty("descriptionColumn").GetInt32();
        columns[description].Should().Be("Description");
        var basis = columns.IndexOf("Privilege Basis");
        var entries = await EntriesAsync(client, root, firstId, reviewer);
        entries.Select(e => e.DocumentId).Should().Equal(withheld, redacted);
        entries.Select(e => e.Cells[basis]).Should().Equal("Attorney-Client", "Work Product");
        (await JsonAsync(await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs?savedSearchId={savedSearchId}", reviewer), HttpStatusCode.OK))
            .GetProperty("items").GetArrayLength().Should().Be(1);
        var unchangedFirst = await JsonAsync(await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{firstId}/changes", reviewer), HttpStatusCode.OK);
        unchangedFirst.GetProperty("previousLogId").ValueKind.Should().Be(JsonValueKind.Null, "version 1 has nothing to compare with");

        // Generating again over unchanged coding is the same version, although the saved search was frozen anew.
        (await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", reviewer, generate), HttpStatusCode.OK))
            .GetProperty("log").GetProperty("logId").GetGuid().Should().Be(firstId);

        // AC 2: the per-document description is Privilege Description, edited through coding: audited, with history.
        var coding = await JsonAsync(await SendAsync(client, HttpMethod.Get, root + $"/documents/{withheld}/coding", reviewer), HttpStatusCode.OK);
        coding.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldId").GetInt32() == PrivilegeFields.Description)
            .GetProperty("editable").GetBoolean().Should().BeTrue();
        var version = coding.GetProperty("documentVersion").GetInt64();
        Task<long> CodingAuditsAsync() => db.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Coding' AND action = 'Changed' AND actor_id = @user",
            ("ws", ws), ("user", reviewer.ToString()));
        var audited = await CodingAuditsAsync();
        using (var saved = await SendAsync(client, HttpMethod.Put, root + $"/documents/{withheld}/coding", reviewer, new JsonObject
        {
            ["changes"] = new JsonArray(new JsonObject
            {
                ["fieldId"] = PrivilegeFields.Description,
                ["operation"] = "set",
                ["value"] = "Email from counsel giving legal advice on the supply agreement",
            }),
        }, $"\"{version}\"", "ssl-description-1"))
        {
            saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        }

        (await CodingAuditsAsync()).Should().Be(audited + 1);
        var history = await JsonAsync(await SendAsync(client, HttpMethod.Get,
            root + $"/documents/{withheld}/coding-history?fieldId={PrivilegeFields.Description}", reviewer), HttpStatusCode.OK);
        history.GetProperty("items").EnumerateArray().Single().GetProperty("newValue").GetString()
            .Should().Be("Email from counsel giving legal advice on the supply agreement");

        // A reviewer without Coding.WritePrivilege cannot change it (and cannot see the log at all).
        (await JsonAsync(await SendAsync(client, HttpMethod.Get, root + $"/documents/{withheld}/coding", plain), HttpStatusCode.OK))
            .GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldId").GetInt32() == PrivilegeFields.Description)
            .GetProperty("editable").GetBoolean().Should().BeFalse();
        (await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{firstId}", plain)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{firstId}/changes", plain)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // AC 3: after coding changes, regenerating is version 2 of the same log, with a diff of what changed.
        await CodeAsync(later, PrivilegeFields.Keys.Withhold, workProduct);
        await CodeAsync(redacted, PrivilegeFields.Keys.NotPrivileged, attorneyClient);
        var second = (await JsonAsync(await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", reviewer, generate), HttpStatusCode.Created))
            .GetProperty("log");
        second.GetProperty("version").GetInt32().Should().Be(2);
        var secondId = second.GetProperty("logId").GetGuid();
        var changes = await JsonAsync(await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{secondId}/changes?limit=2", reviewer), HttpStatusCode.OK);
        changes.GetProperty("previousLogId").GetGuid().Should().Be(firstId);
        changes.GetProperty("previousVersion").GetInt32().Should().Be(1);
        (changes.GetProperty("added").GetInt32(), changes.GetProperty("removed").GetInt32(), changes.GetProperty("changed").GetInt32(),
            changes.GetProperty("unchanged").GetInt32()).Should().Be((1, 1, 1, 0));
        var items = changes.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items[0].GetProperty("kind").GetString().Should().Be("changed");
        items[0].GetProperty("documentId").GetGuid().Should().Be(withheld);
        var cell = items[0].GetProperty("changedCells").EnumerateArray().Single();
        cell.GetProperty("column").GetString().Should().Be("Description");
        cell.GetProperty("before").GetString().Should().BeEmpty();
        cell.GetProperty("after").GetString().Should().Be("Email from counsel giving legal advice on the supply agreement");
        items[1].GetProperty("kind").GetString().Should().Be("added");
        items[1].GetProperty("documentId").GetGuid().Should().Be(later);
        var cursor = changes.GetProperty("nextCursor").GetString();
        var rest = await JsonAsync(await SendAsync(client, HttpMethod.Get,
            root + $"/privilege-logs/{secondId}/changes?limit=2&cursor={Uri.EscapeDataString(cursor!)}", reviewer), HttpStatusCode.OK);
        var removed = rest.GetProperty("items").EnumerateArray().Single();
        removed.GetProperty("kind").GetString().Should().Be("removed");
        removed.GetProperty("documentId").GetGuid().Should().Be(redacted);
        removed.GetProperty("previousOrdinal").GetInt32().Should().Be(2);
        rest.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        // Explicit comparisons: with itself nothing changed; an unknown version is not found.
        var self = await JsonAsync(await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{secondId}/changes?against={secondId}", reviewer),
            HttpStatusCode.OK);
        self.GetProperty("items").GetArrayLength().Should().Be(0);
        self.GetProperty("unchanged").GetInt32().Should().Be(2);
        (await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{secondId}/changes?against={Guid.CreateVersion7()}", reviewer))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Sources: one at a time; an unknown saved search is not found.
        (await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", reviewer,
            new JsonObject { ["savedSearchId"] = savedSearchId.ToString(), ["snapshotId"] = second.GetProperty("snapshotId").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(client, HttpMethod.Post, root + "/privilege-logs", reviewer, new JsonObject { ["savedSearchId"] = Guid.CreateVersion7().ToString() }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static WebApplicationFactory<Program> Factory(SnapshotHarness h) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", h.Search.Db.Core.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OpenSearch", h.Search.Options.Endpoint!.ToString());
            builder.UseSetting("OpenSearch:IndexPrefix", h.Search.Scope.Prefix);
            builder.UseSetting("OpenSearch:Placement:CacheTtl", "00:00:00");
            builder.UseSetting("OpenSearch:Search:FieldCatalogCacheTtl", "00:00:00");
            builder.UseSetting("Snapshots:BackgroundEnabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader>(h.Search.Db.Reader);
            });
        });

    private sealed record Entry(Guid DocumentId, string[] Cells);

    private static async Task<List<Entry>> EntriesAsync(HttpClient client, string root, Guid logId, Guid user)
    {
        var page = await JsonAsync(await SendAsync(client, HttpMethod.Get, root + $"/privilege-logs/{logId}/entries?limit=500", user), HttpStatusCode.OK);
        return
        [
            .. page.GetProperty("items").EnumerateArray().Select(e => new Entry(e.GetProperty("documentId").GetGuid(),
                [.. e.GetProperty("cells").EnumerateArray().Select(c => c.GetString()!)])),
        ];
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(expected, text);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid user, JsonNode? body = null, string? ifMatch = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request, Ct);
    }
}
