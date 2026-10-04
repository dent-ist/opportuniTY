using System.Net;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Core.Fields;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Api;

/// <summary>E07-T02 / ADR-007 R8: field capability metadata over HTTP; choices and restricted-field omission (#186).</summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class FieldApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fields_list_query_names_and_capabilities()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        await using var factory = new ApiFactory();
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:App", db.AppConnectionString)).CreateClient();
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        await db.Fields.CreateFieldAsync(new NewField(ws, "Issues", FieldType.MultiChoice, FieldStorage.Coding), Ct);
        await db.Fields.CreateFieldAsync(new NewField(ws, "Amount", FieldType.Decimal, FieldStorage.Metadata, DecimalPrecision: 12, DecimalScale: 2), Ct);
        await db.Fields.CreateFieldAsync(new NewField(ws, "Internal Note", FieldType.Text, FieldStorage.Metadata, IsSearchable: false), Ct);

        using var response = await client.GetAsync(new Uri($"/api/v1/workspaces/{ws}/fields", UriKind.Relative), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var items = JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("queryName").GetString()!, i => i.Clone());

        JsonElement Caps(string name) => items[name].GetProperty("capabilities");

        items.Should().ContainKeys("controlnumber", "date", "filename", "issues", "amount", "internal_note");
        Caps("controlnumber").GetProperty("sortable").GetBoolean().Should().BeTrue();
        Caps("controlnumber").GetProperty("rangeable").GetBoolean().Should().BeTrue();
        Caps("filename").GetProperty("leadingWildcard").GetBoolean().Should().BeTrue();
        Caps("issues").GetProperty("aggregatable").GetBoolean().Should().BeTrue();
        Caps("issues").GetProperty("sortable").GetBoolean().Should().BeFalse("choice fields are not sortable in the MVP");
        Caps("amount").GetProperty("rangeable").GetBoolean().Should().BeTrue();
        Caps("internal_note").GetProperty("filterable").GetBoolean().Should().BeFalse("not searchable");
        items["issues"].GetProperty("storage").GetString().Should().Be("coding");
        items["issues"].GetProperty("type").GetString().Should().Be("multiChoice");
        items["date"].GetProperty("datePrecision").GetString().Should().Be("dateTime");
    }

    [Fact]
    public async Task Choice_fields_carry_their_choices_in_admin_order_and_other_fields_none()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        await using var factory = new ApiFactory();
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:App", db.AppConnectionString)).CreateClient();
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var responsive = (await db.Fields.CreateFieldAsync(new NewField(ws, "Responsive", FieldType.SingleChoice, FieldStorage.Coding), Ct)).Value!;
        var yes = (await db.Fields.AddChoiceAsync(ws, responsive.FieldId, "Responsive", Ct)).Value!;
        var no = (await db.Fields.AddChoiceAsync(ws, responsive.FieldId, "Not Responsive", Ct)).Value!;
        var later = (await db.Fields.AddChoiceAsync(ws, responsive.FieldId, "Needs Further Review", Ct)).Value!;
        (await db.Fields.ReorderChoicesAsync(ws, responsive.FieldId, [no.ChoiceId, yes.ChoiceId, later.ChoiceId], Ct)).Succeeded.Should().BeTrue();
        (await db.Fields.SetChoiceActiveAsync(ws, responsive.FieldId, later.ChoiceId, false, Ct)).Succeeded.Should().BeTrue();

        var items = await FieldsAsync(client, ws);

        var choices = items["responsive"].GetProperty("choices").EnumerateArray().ToList();
        choices.Select(c => c.GetProperty("name").GetString()).Should().Equal("Not Responsive", "Responsive", "Needs Further Review");
        choices.Select(c => c.GetProperty("isActive").GetBoolean()).Should().Equal(true, true, false);
        choices[0].GetProperty("choiceId").GetInt32().Should().Be(no.ChoiceId);
        items["controlnumber"].GetProperty("choices").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Fields_the_caller_may_not_see_are_omitted_with_their_choices_without_renaming_others()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var secret = (await db.Fields.CreateFieldAsync(new NewField(ws, "Custodian Notes", FieldType.MultiChoice, FieldStorage.Coding), Ct)).Value!;
        await db.Fields.AddChoiceAsync(ws, secret.FieldId, "Settlement", Ct);
        await db.Fields.CreateFieldAsync(new NewField(ws, "Custodian-Notes", FieldType.Text, FieldStorage.Metadata), Ct);
        await using var factory = new ApiFactory();
        using var open = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:App", db.AppConnectionString)).CreateClient();
        using var restricted = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", db.AppConnectionString);
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFieldAccessFilter>();
                services.AddSingleton<IFieldAccessFilter>(new HideFields(secret.FieldId));
            });
        }).CreateClient();

        var all = await FieldsAsync(open, ws);
        var visible = await FieldsAsync(restricted, ws);

        var secretName = all.Single(f => f.Value.GetProperty("fieldId").GetInt32() == secret.FieldId).Key;
        visible.Should().NotContainKey(secretName);
        visible.Values.Should().NotContain(f => f.GetProperty("fieldId").GetInt32() == secret.FieldId);
        visible.Keys.Should().BeEquivalentTo(all.Keys.Where(k => k != secretName), "omitting a field never renames another");
        (await (await restricted.GetAsync(new Uri($"/api/v1/workspaces/{ws}/fields", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct))
            .Should().NotContain("Settlement").And.NotContain("Custodian Notes");
    }

    private static async Task<Dictionary<string, JsonElement>> FieldsAsync(HttpClient client, Guid ws)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/workspaces/{ws}/fields", UriKind.Relative), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("queryName").GetString()!, i => i.Clone());
    }

    private sealed class HideFields(params int[] fieldIds) : IFieldAccessFilter
    {
        public ValueTask<IReadOnlySet<int>> RestrictedFieldIdsAsync(
            Guid workspaceId, SecurityPrincipal principal, FieldCatalog catalog, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlySet<int>>(fieldIds.ToHashSet());
    }
}
