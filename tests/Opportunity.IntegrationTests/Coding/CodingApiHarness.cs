using System.Net.Http.Headers;
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
using Opportunity.Application.Fields;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Data.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Documents;

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// A workspace with one field of each kind the coding API validates: Boolean, multiple choice (one inactive choice),
/// text, a date (day precision) and a security-affecting confidentiality designation (Q-11).
/// </summary>
internal sealed record CodingWorkspace(
    Guid Id,
    int Responsive,
    int Issues,
    int IssueA,
    int IssueB,
    int IssueRetired,
    int Notes,
    int ReviewDate,
    int Confidentiality,
    int Confidential,
    int AttorneysEyesOnly);

/// <summary>Arranges coding API tests against the real API host (PDP, coding store and audit store on PostgreSQL).</summary>
internal static class CodingApiHarness
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<CodingWorkspace> WorkspaceAsync(CoreSchemaDatabase core, Guid? workspaceId = null)
    {
        var ws = workspaceId ?? await core.CreateWorkspaceAsync();
        var fields = core.Fields;
        await fields.InitializeWorkspaceAsync(ws, Ct);
        async Task<int> Field(NewField field) => (await fields.CreateFieldAsync(field, Ct)).Value!.FieldId;
        async Task<int> Choice(int field, string name) => (await fields.AddChoiceAsync(ws, field, name, Ct)).Value!.ChoiceId;

        var responsive = await Field(new NewField(ws, "Responsive", FieldType.Boolean, FieldStorage.Coding));
        var issues = await Field(new NewField(ws, "Issues", FieldType.MultiChoice, FieldStorage.Coding, IsMultiValue: true));
        var a = await Choice(issues, "Supply contract");
        var b = await Choice(issues, "Pricing");
        var retired = await Choice(issues, "Retired issue");
        (await fields.SetChoiceActiveAsync(ws, issues, retired, false, Ct)).Succeeded.Should().BeTrue();
        var notes = await Field(new NewField(ws, "Reviewer Notes", FieldType.Text, FieldStorage.Coding));
        var reviewDate = await Field(new NewField(ws, "Review Date", FieldType.Date, FieldStorage.Coding, DatePrecision: DatePrecision.Date));
        var confidentiality = await Field(new NewField(ws, "Confidentiality", FieldType.SingleChoice, FieldStorage.Coding,
            SecurityClass: SecurityClass.ConfidentialityDesignation));
        var confidential = await Choice(confidentiality, "Confidential");
        var aeo = await Choice(confidentiality, "Attorneys' Eyes Only");
        return new CodingWorkspace(ws, responsive, issues, a, b, retired, notes, reviewDate, confidentiality, confidential, aeo);
    }

    /// <summary>A signed-up user with a display name and a role in the workspace.</summary>
    public static async Task<Guid> MemberAsync(CoreSchemaDatabase core, Guid workspaceId, WorkspaceRole role, string? displayName = null)
    {
        var id = Guid.CreateVersion7();
        await core.ExecuteAsync(
            """
            INSERT INTO opportunity.app_user (user_id, issuer, subject, display_name, groups, groups_refreshed_at, last_sign_in_at)
            VALUES (@id, 'https://idp.test', @id::text, @name, '{}', now(), now())
            """,
            ("id", id), ("name", displayName ?? "Reviewer " + id.ToString("N")[..6]));
        await core.ExecuteAsync(
            "INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, user_id) VALUES (@ws, @id, @role, @user)",
            ("ws", workspaceId), ("id", Guid.CreateVersion7()), ("role", role.Key()), ("user", id));
        return id;
    }

    public static Task<Guid> DocumentAsync(CoreSchemaDatabase core, Guid workspaceId) =>
        core.InsertDocumentAsync(workspaceId, "CODE" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant())
            .ContinueWith(t => t.Result.DocumentId, TaskScheduler.Default);

    /// <summary>The API host on the test database: real PDP state reader, coding store and audit store.</summary>
    public static WebApplicationFactory<Program> Factory(
        string appConnectionString, Action<IWebHostBuilder>? builder = null, Action<IServiceCollection>? services = null) =>
        new ApiFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:App", appConnectionString);
            builder?.Invoke(b);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<ISecurityStateReader>();
                s.AddSingleton<ISecurityStateReader, PostgresSecurityStateReader>();
                services?.Invoke(s);
            });
        });

    public static string CodingUrl(Guid workspaceId, Guid documentId, Guid? layoutId = null) =>
        $"/api/v1/workspaces/{workspaceId}/documents/{documentId}/coding" + (layoutId is { } l ? $"?layoutId={l}" : string.Empty);

    public static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, Guid user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        return await client.SendAsync(request, Ct);
    }

    public static async Task<HttpResponseMessage> PutAsync(
        HttpClient client, string url, Guid user, object body, string? ifMatch, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(url, UriKind.Relative))
        {
            Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
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

    /// <summary>A save body: <c>{ changes: [{ fieldId, operation, value }], layoutId }</c>.</summary>
    public static JsonObject Save(params (int FieldId, string Operation, JsonNode? Value)[] changes) => new()
    {
        ["changes"] = new JsonArray([.. changes.Select(c => (JsonNode)new JsonObject
        {
            ["fieldId"] = c.FieldId,
            ["operation"] = c.Operation,
            ["value"] = c.Value?.DeepClone(),
        })]),
    };

    public static JsonObject Set(int fieldId, JsonNode? value) => Save((fieldId, "set", value));

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    public static string? ETag(HttpResponseMessage response) => response.Headers.ETag?.ToString();

    public static JsonElement FieldOf(JsonElement coding, int fieldId) =>
        coding.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("fieldId").GetInt32() == fieldId);
}

/// <summary>
/// A stand-in for the E05-T06 binding: the confidentiality designation drives <c>Confidential</c> and
/// <c>AttorneysEyesOnly</c> by choice name; nothing else is bound.
/// </summary>
internal sealed class ConfidentialityBinding : IRestrictionClassBinding
{
    private static readonly IReadOnlySet<string> Bound = new HashSet<string>(StringComparer.Ordinal) { "Confidential", "AttorneysEyesOnly" };

    public IReadOnlySet<string> BoundClasses(FieldCatalog catalog) => Bound;

    public IReadOnlySet<string> Derive(FieldCatalog catalog, IReadOnlyDictionary<int, JsonNode> securityValues)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (fieldId, value) in securityValues)
        {
            if (catalog.Find(fieldId)?.SecurityClass != SecurityClass.ConfidentialityDesignation)
            {
                continue;
            }

            foreach (var choiceId in FieldValues.ChoiceIds(value))
            {
                switch (catalog.ChoicesOf(fieldId).FirstOrDefault(c => c.ChoiceId == choiceId)?.Name)
                {
                    case "Confidential":
                        classes.Add("Confidential");
                        break;
                    case "Attorneys' Eyes Only":
                        classes.Add("AttorneysEyesOnly");
                        break;
                }
            }
        }

        return classes;
    }
}

internal static class HttpHeadersExtensions
{
    public static string? Single(this HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
}
