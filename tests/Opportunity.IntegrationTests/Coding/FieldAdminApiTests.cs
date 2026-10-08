using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// Field and coding layout administration (E04-T06) through the real API host, PDP and audit store: create, edit,
/// retype and retire fields with If-Match; choices are added, renamed, reordered, deactivated and only deleted while
/// unused; a type change is refused once documents hold values; layouts are created, replaced and deleted with their
/// rules validated (Q-48 never on security-affecting fields); every change is audited and needs Workspace.ManageFields.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class FieldAdminApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fields_and_choices_are_administered_with_versions_and_audited()
    {
        await using var core = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(core);
        var admin = await MemberAsync(core, w.Id, WorkspaceRole.WorkspaceAdmin);
        var reviewer = await MemberAsync(core, w.Id, WorkspaceRole.Reviewer);
        await using var factory = Factory(core.AppConnectionString);
        using var client = factory.CreateClient();
        var fields = $"/api/v1/workspaces/{w.Id}/fields";

        // Reviewers cannot administer fields.
        using (var denied = await SendAsync(client, HttpMethod.Post, fields, reviewer, new { displayName = "Hot", type = "boolean", storage = "coding" }))
        {
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using var created = await SendAsync(client, HttpMethod.Post, fields, admin,
            new { displayName = "Withholding Reason", type = "singleChoice", storage = "coding", description = "Why withheld" });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var field = await JsonAsync(created);
        var id = field.GetProperty("fieldId").GetInt32();
        var url = $"{fields}/{id}";
        field.GetProperty("version").GetInt64().Should().Be(1);
        ETag(created).Should().Be("\"1\"");
        field.GetProperty("limitations").EnumerateArray().Select(l => l.GetString()).Should().Contain("This field will not be sortable.");

        // Choices: add two, reorder, deactivate one; every change moves the field's version on.
        var etag = ETag(created)!;
        foreach (var name in new[] { "Attorney-Client", "Work Product" })
        {
            using var added = await SendAsync(client, HttpMethod.Post, url + "/choices", admin, new { name }, etag);
            added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));
            etag = ETag(added)!;
        }

        using (var stale = await SendAsync(client, HttpMethod.Post, url + "/choices", admin, new { name = "Other" }, "\"1\""))
        {
            stale.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        }

        using (var missing = await SendAsync(client, HttpMethod.Post, url + "/choices", admin, new { name = "Other" }))
        {
            missing.StatusCode.Should().Be((HttpStatusCode)428);
        }

        var choices = (await JsonAsync(await SendAsync(client, HttpMethod.Get, url, admin))).GetProperty("choices").EnumerateArray()
            .Select(c => c.GetProperty("choiceId").GetInt32()).ToList();
        using (var reordered = await SendAsync(client, HttpMethod.Put, url + "/choice-order", admin, new { choiceIds = new[] { choices[1], choices[0] } }, etag))
        {
            reordered.StatusCode.Should().Be(HttpStatusCode.OK);
            (await JsonAsync(reordered)).GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("name").GetString())
                .Should().Equal("Work Product", "Attorney-Client");
            etag = ETag(reordered)!;
        }

        using (var deactivated = await SendAsync(client, HttpMethod.Put, $"{url}/choices/{choices[1]}", admin, new { isActive = false }, etag))
        {
            deactivated.StatusCode.Should().Be(HttpStatusCode.OK);
            (await JsonAsync(deactivated)).GetProperty("choices")[0].GetProperty("isActive").GetBoolean().Should().BeFalse();
            etag = ETag(deactivated)!;
        }

        using (var deleted = await SendAsync(client, HttpMethod.Delete, $"{url}/choices/{choices[0]}", admin, null, etag))
        {
            deleted.StatusCode.Should().Be(HttpStatusCode.OK, "a choice that was never used can be deleted");
            etag = ETag(deleted)!;
        }

        // A used choice can only be deactivated.
        await MarkUsedAsync(core, w.Id, w.IssueRetired);
        using (var inUse = await SendAsync(client, HttpMethod.Delete, $"/api/v1/workspaces/{w.Id}/fields/{w.Issues}/choices/{w.IssueRetired}", admin, null, "*"))
        {
            inUse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        // Retype is allowed while no document holds a value; afterwards it is refused with an explanation.
        using var amountCreated = await SendAsync(client, HttpMethod.Post, fields, admin, new { displayName = "Amount", type = "text", storage = "metadata" });
        var amount = (await JsonAsync(amountCreated)).GetProperty("fieldId").GetInt32();
        using (var retyped = await SendAsync(client, HttpMethod.Put, $"{fields}/{amount}", admin,
                   new { displayName = "Amount", isHidden = false, type = "integer" }, ETag(amountCreated)))
        {
            retyped.StatusCode.Should().Be(HttpStatusCode.OK, await retyped.Content.ReadAsStringAsync(Ct));
            (await JsonAsync(retyped)).GetProperty("capabilities").GetProperty("rangeable").GetBoolean().Should().BeTrue();
        }

        await core.InsertDocumentAsync(w.Id, "ADM0001", d => d.Metadata = $$"""{"f{{amount}}": 12}""");
        var current = await SendAsync(client, HttpMethod.Get, $"{fields}/{amount}", admin);
        (await JsonAsync(current)).GetProperty("hasValues").GetBoolean().Should().BeTrue();
        using (var refused = await SendAsync(client, HttpMethod.Put, $"{fields}/{amount}", admin,
                   new { displayName = "Invoice Amount", isHidden = false, type = "decimal" }, ETag(current)))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("holds values");
        }

        using (var renamed = await SendAsync(client, HttpMethod.Put, $"{fields}/{amount}", admin,
                   new { displayName = "Invoice Amount", isHidden = true, type = "integer" }, ETag(current)))
        {
            renamed.StatusCode.Should().Be(HttpStatusCode.OK, "a rename never touches documents");
        }

        current.Dispose();

        // System fields are never retired; custom fields are.
        using (var system = await SendAsync(client, HttpMethod.Delete, $"{fields}/1", admin, null, "*"))
        {
            system.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        using (var retired = await SendAsync(client, HttpMethod.Delete, url, admin, null, etag))
        {
            retired.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using (var gone = await SendAsync(client, HttpMethod.Get, url, admin))
        {
            gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        var actions = await core.ScalarAsync<string>(
            "SELECT string_agg(action, ',' ORDER BY occurred_at) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Workspace' AND resource_id = @id",
            ("ws", w.Id), ("id", id.ToString(CultureInfo.InvariantCulture)));
        actions.Split(',').Should().Equal(
            "Field.Created", "Field.Modified", "Field.Modified", "Field.Modified", "Field.Modified", "Field.Modified", "Field.Retired");
    }

    [Fact]
    public async Task Layouts_are_created_replaced_and_deleted_with_their_rules_validated()
    {
        await using var core = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(core);
        var admin = await MemberAsync(core, w.Id, WorkspaceRole.WorkspaceAdmin);
        await using var factory = Factory(core.AppConnectionString);
        using var client = factory.CreateClient();
        var layouts = $"/api/v1/workspaces/{w.Id}/coding-layouts";

        object Layout(string name, bool applyNotes, int familyField) => new
        {
            name,
            isDefault = false,
            roles = new[] { "Reviewer" },
            sections = new object[]
            {
                new
                {
                    title = "Responsiveness",
                    fields = new object[]
                    {
                        new { fieldId = w.Responsive, isRequired = true, isReadOnly = false, applyToFamilyByDefault = true },
                        new
                        {
                            fieldId = w.Notes, isRequired = applyNotes, isReadOnly = false,
                            visibleWhen = new { fieldId = w.Responsive, booleanValue = true }, applyToFamilyByDefault = false,
                        },
                    },
                },
                new
                {
                    title = "Confidentiality",
                    fields = new object[] { new { fieldId = familyField, isRequired = false, isReadOnly = false, applyToFamilyByDefault = familyField == w.Confidentiality } },
                },
            },
        };

        using (var invalid = await SendAsync(client, HttpMethod.Post, layouts, admin, Layout("First Pass", false, w.Confidentiality)))
        {
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Q-48: a security-affecting field never applies to the family by default");
            (await invalid.Content.ReadAsStringAsync(Ct)).Should().Contain($"f{w.Confidentiality}");
        }

        using var created = await SendAsync(client, HttpMethod.Post, layouts, admin, Layout("First Pass", false, w.Issues));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var layout = await JsonAsync(created);
        var url = $"{layouts}/{layout.GetProperty("layoutId").GetGuid()}";
        layout.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("Reviewer");
        layout.GetProperty("sections")[0].GetProperty("fields")[0].GetProperty("applyToFamilyByDefault").GetBoolean().Should().BeTrue();

        using (var replaced = await SendAsync(client, HttpMethod.Put, url, admin, Layout("First Pass Review", true, w.Issues), ETag(created)))
        {
            replaced.StatusCode.Should().Be(HttpStatusCode.OK, await replaced.Content.ReadAsStringAsync(Ct));
            ETag(replaced).Should().Be("\"2\"");
            var fields = (await JsonAsync(replaced)).GetProperty("sections")[0].GetProperty("fields");
            fields[1].GetProperty("isRequired").GetBoolean().Should().BeTrue();
            fields[1].GetProperty("visibleWhen").GetProperty("fieldId").GetInt32().Should().Be(w.Responsive);
        }

        using (var stale = await SendAsync(client, HttpMethod.Delete, url, admin, null, ETag(created)))
        {
            stale.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        }

        var defaultId = await core.ScalarAsync<Guid>(
            "SELECT layout_id FROM opportunity.coding_layout WHERE workspace_id = @ws AND is_default", ("ws", w.Id));
        using (var keepDefault = await SendAsync(client, HttpMethod.Delete, $"{layouts}/{defaultId}", admin, null, "*"))
        {
            keepDefault.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        using (var deleted = await SendAsync(client, HttpMethod.Delete, url, admin, null, "\"2\""))
        {
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        var audited = await core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND action LIKE 'CodingLayout.%'", ("ws", w.Id));
        audited.Should().Be(3);
    }

    private static Task MarkUsedAsync(CoreSchemaDatabase core, Guid workspaceId, int choiceId) =>
        core.ExecuteAsync("UPDATE opportunity.choice SET first_used_at = now() WHERE workspace_id = @ws AND choice_id = @id",
            ("ws", workspaceId), ("id", choiceId));

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid user, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (body is not null)
        {
            request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
