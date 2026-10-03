using System.Net;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;

using Opportunity.Application.Fields;
using Opportunity.Core.Fields;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Api;

/// <summary>E07-T02 / ADR-007 R8: field capability metadata over HTTP.</summary>
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
}
