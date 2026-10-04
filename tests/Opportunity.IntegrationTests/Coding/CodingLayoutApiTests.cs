using System.Net;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Application.Fields;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// <c>GET …/coding-layouts</c> (E04-T03, E16-T05) through the real API host and PDP: the coding pane's layouts in
/// order with sections, required, read-only and conditional fields; role assignment decides which layouts a reviewer
/// sees; fields the caller may not see are omitted; non-members get the workspace 404.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class CodingLayoutApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Layouts_are_listed_with_sections_and_field_rules_for_the_callers_roles()
    {
        await using var core = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(core);
        var reviewer = await MemberAsync(core, w.Id, WorkspaceRole.Reviewer);
        var privilegeReviewer = await MemberAsync(core, w.Id, WorkspaceRole.PrivilegeReviewer);
        var admin = await MemberAsync(core, w.Id, WorkspaceRole.WorkspaceAdmin);
        var outsider = await MemberAsync(core, await core.CreateWorkspaceAsync(), WorkspaceRole.Reviewer);

        var firstPass = await SaveAsync(core, w, "First Pass Review", ["Reviewer"], s =>
        {
            s.Fields.Add(new CodingLayoutField { FieldId = w.Responsive, IsRequired = true });
            s.Fields.Add(new CodingLayoutField { FieldId = w.Notes, VisibleWhen = new VisibilityCondition(w.Responsive, BooleanValue: true) });
            s.Fields.Add(new CodingLayoutField { FieldId = w.Issues, IsReadOnly = true });
        });
        await SaveAsync(core, w, "Privilege Review", ["PrivilegeReviewer"], s =>
            s.Fields.Add(new CodingLayoutField
            {
                FieldId = w.Notes,
                VisibleWhen = new VisibilityCondition(w.Confidentiality, ChoiceIds: [w.Confidential, w.AttorneysEyesOnly]),
            }));
        // The privilege layout's condition names Confidentiality, so it must be in the layout too.
        await using var factory = Factory(core.AppConnectionString);
        using var client = factory.CreateClient();

        var forReviewer = await LayoutsAsync(client, w.Id, reviewer);
        forReviewer.Select(Name).Should().Equal("Default", "First Pass Review");
        var layout = forReviewer[1];
        layout.GetProperty("layoutId").GetGuid().Should().Be(firstPass.LayoutId);
        layout.GetProperty("isDefault").GetBoolean().Should().BeFalse();
        var fields = layout.GetProperty("sections")[0].GetProperty("fields").EnumerateArray().ToList();
        layout.GetProperty("sections")[0].GetProperty("title").GetString().Should().Be("Coding");
        fields.Select(f => f.GetProperty("fieldId").GetInt32()).Should().Equal(w.Responsive, w.Notes, w.Issues);
        fields[0].GetProperty("isRequired").GetBoolean().Should().BeTrue();
        fields[0].GetProperty("visibleWhen").ValueKind.Should().Be(JsonValueKind.Null);
        fields[1].GetProperty("visibleWhen").GetProperty("fieldId").GetInt32().Should().Be(w.Responsive);
        fields[1].GetProperty("visibleWhen").GetProperty("booleanValue").GetBoolean().Should().BeTrue();
        fields[2].GetProperty("isReadOnly").GetBoolean().Should().BeTrue();

        (await LayoutsAsync(client, w.Id, privilegeReviewer)).Select(Name).Should().Equal("Default", "Privilege Review");
        (await LayoutsAsync(client, w.Id, admin)).Select(Name).Should().Equal("Default", "First Pass Review", "Privilege Review");
        using var denied = await GetAsync(client, $"/api/v1/workspaces/{w.Id}/coding-layouts", outsider);
        denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Fields_the_caller_may_not_see_are_left_out_of_every_layout()
    {
        await using var core = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(core);
        var reviewer = await MemberAsync(core, w.Id, WorkspaceRole.Reviewer);
        await SaveAsync(core, w, "First Pass Review", ["Reviewer"], s =>
        {
            s.Fields.Add(new CodingLayoutField { FieldId = w.Responsive });
            s.Fields.Add(new CodingLayoutField { FieldId = w.Notes });
        });
        await using var factory = Factory(core.AppConnectionString, services: s =>
        {
            s.RemoveAll<IFieldAccessFilter>();
            s.AddSingleton<IFieldAccessFilter>(new HideFields(w.Notes));
        });
        using var client = factory.CreateClient();

        var layouts = await LayoutsAsync(client, w.Id, reviewer);
        layouts[1].GetProperty("sections")[0].GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("fieldId").GetInt32()).Should().Equal(w.Responsive);
    }

    private static string Name(JsonElement layout) => layout.GetProperty("name").GetString()!;

    private sealed class HideFields(params int[] fieldIds) : IFieldAccessFilter
    {
        public ValueTask<IReadOnlySet<int>> RestrictedFieldIdsAsync(
            Guid workspaceId, SecurityPrincipal principal, FieldCatalog catalog, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlySet<int>>(fieldIds.ToHashSet());
    }

    private static async Task<CodingLayout> SaveAsync(
        CoreSchemaDatabase core, CodingWorkspace w, string name, string[] roles, Action<CodingLayoutSection> fields)
    {
        var section = new CodingLayoutSection { SectionId = Guid.CreateVersion7(), Title = "Coding" };
        fields(section);
        if (section.Fields.Any(f => f.VisibleWhen?.FieldId == w.Confidentiality))
        {
            section.Fields.Insert(0, new CodingLayoutField { FieldId = w.Confidentiality });
        }

        var layout = new CodingLayout { WorkspaceId = w.Id, LayoutId = Guid.CreateVersion7(), Name = name, Sections = { section } };
        layout.Roles.AddRange(roles);
        var saved = await core.Fields.SaveLayoutAsync(layout, Ct);
        saved.Succeeded.Should().BeTrue(string.Join("; ", saved.Errors.Select(e => e.Message)));
        return saved.Value!;
    }

    private static async Task<List<JsonElement>> LayoutsAsync(HttpClient client, Guid workspaceId, Guid user)
    {
        using var response = await GetAsync(client, $"/api/v1/workspaces/{workspaceId}/coding-layouts", user);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return [.. JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }
}
