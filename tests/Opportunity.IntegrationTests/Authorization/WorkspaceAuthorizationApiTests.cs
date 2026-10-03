using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Authorization;

/// <summary>
/// E05-T02 PEP-1 through the real API host and the PostgreSQL PDP: every workspace route from the OpenAPI document
/// answers a non-member, an unknown workspace and a malformed ID with the same 404 (no enumeration), each audited as
/// <c>AuthZ.Denied</c>; members get exactly their role's permissions.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed partial class WorkspaceAuthorizationApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryAuditEventWriter _audit = new();

    [Fact]
    public async Task Every_workspace_route_in_the_openapi_document_is_404_for_non_members()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var member = await db.CreateUserAsync();
        var outsider = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, member);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        var operations = await WorkspaceOperationsAsync(client);
        operations.Select(o => o.Path).Should().Contain(CommittedWorkspacePaths(), "the committed OpenAPI document is covered");

        foreach (var (method, path) in operations)
        {
            var bodies = new List<string>();
            foreach (var target in new[] { ws.ToString(), Guid.CreateVersion7().ToString(), "not-a-guid" })
            {
                _audit.Clear();
                using var response = await SendAsync(client, method, Expand(path, target), outsider);
                var problem = await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
                bodies.Add(problem.GetProperty("detail").GetString() + "|" + problem.GetProperty("title").GetString());
                if (target == ws.ToString())
                {
                    _audit.Events.Should().ContainSingle($"{method} {path}").Which.ReasonCode.Should().Be(AuthorizationReasons.NotAMember);
                }
            }

            bodies.Distinct().Should().ContainSingle("{0} {1}: a non-member cannot tell an existing workspace from a missing one", method, path);

            using var anonymous = await SendAsync(client, method, Expand(path, ws.ToString()), user: null);
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "{0} {1}", method, path);

            using var asMember = await SendAsync(client, method, Expand(path, ws.ToString()), member);
            var body = await asMember.Content.ReadAsStringAsync(Ct);
            (asMember.StatusCode == HttpStatusCode.NotFound && body.Contains("The resource does not exist.", StringComparison.Ordinal))
                .Should().BeFalse("{0} {1}: PEP-1 admits a member", method, path);
        }
    }

    [Fact]
    public async Task Get_workspace_returns_the_callers_effective_permissions()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var reviewer = await db.CreateUserAsync();
        var groupAuditor = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        await db.AssignGroupAsync(ws, WorkspaceRole.Auditor, "cn=audit");
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        using var asReviewer = JsonDocument.Parse(await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", reviewer)).Content.ReadAsStringAsync(Ct));
        asReviewer.RootElement.GetProperty("workspaceId").GetGuid().Should().Be(ws);
        asReviewer.RootElement.GetProperty("status").GetString().Should().Be("active");
        Permissions(asReviewer).Should().BeEquivalentTo(RoleCatalog.Get(WorkspaceRole.Reviewer).Grants.Select(p => p.Name()));
        asReviewer.RootElement.GetProperty("breakGlassActive").GetBoolean().Should().BeFalse();

        using var asAuditor = JsonDocument.Parse(await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", groupAuditor, "cn=audit")).Content.ReadAsStringAsync(Ct));
        Permissions(asAuditor).Should().BeEquivalentTo(RoleCatalog.Get(WorkspaceRole.Auditor).Grants.Select(p => p.Name()));
    }

    [Fact]
    public async Task Break_glass_holder_is_a_member_only_while_activated_and_sees_read_permissions_only()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var responder = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.BreakGlass, responder);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", responder)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await db.ActivateBreakGlassAsync(ws, responder, TimeSpan.FromMinutes(60));
        using var active = JsonDocument.Parse(await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", responder)).Content.ReadAsStringAsync(Ct));
        Permissions(active).Should().BeEquivalentTo(["Document.View", "Search.Execute", "Audit.Read"]);
        active.RootElement.GetProperty("breakGlassActive").GetBoolean().Should().BeTrue();
        (await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/query-validations", responder, body: """{"query":"a"}"""))
            .StatusCode.Should().Be(HttpStatusCode.OK, "Search.Execute is a break-glass read permission");
    }

    [Fact]
    public async Task Jobs_are_visible_to_their_initiator_and_to_job_view_all_only()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var owner = await db.CreateUserAsync();
        var otherReviewer = await db.CreateUserAsync();
        var auditor = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, owner);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, otherReviewer);
        await db.AssignAsync(ws, WorkspaceRole.Auditor, auditor);
        var jobs = new Data.Jobs.JobRepository(db.Core.DataSource);
        var job = (await jobs.CreateAsync(new NewJob { WorkspaceId = ws, JobType = JobType.BulkCoding, InitiatedBy = owner, TargetSnapshotId = Guid.CreateVersion7() }, Ct)).Job;
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var url = $"/api/v1/workspaces/{ws}/jobs/{job.JobId}";

        (await SendAsync(client, HttpMethod.Get, url, owner)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Get, url, auditor)).StatusCode.Should().Be(HttpStatusCode.OK);
        _audit.Clear();
        await (await SendAsync(client, HttpMethod.Get, url, otherReviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        _audit.Events.Should().ContainSingle().Which.Details["permission"].Should().Be("Job.ViewAll");
    }

    [Fact]
    public async Task Removing_a_role_takes_effect_on_the_next_request()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var user = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, user);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", user)).StatusCode.Should().Be(HttpStatusCode.OK);
        await db.Core.ExecuteAsync("DELETE FROM opportunity.workspace_role_assignment WHERE user_id = @user", ("user", user));
        (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", user)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private WebApplicationFactory<Program> Factory(AuthorizationDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
                services.AddSingleton<IAuditEventWriter>(_audit);
            });
        });

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid? user, string? groups = null, string? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        if (user is { } id)
        {
            request.Headers.Add(TestAuthentication.UserHeader, id.ToString());
            if (groups is not null)
            {
                request.Headers.Add(TestAuthentication.GroupsHeader, groups);
            }
        }
        else
        {
            request.Headers.Add(TestAuthentication.AnonymousHeader, "1");
        }

        if (method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<List<(HttpMethod Method, string Path)>> WorkspaceOperationsAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct));
        var operations = new List<(HttpMethod, string)>();
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject().Where(p => p.Name.Contains("{workspaceId}", StringComparison.Ordinal)))
        {
            foreach (var operation in path.Value.EnumerateObject().Where(o => o.Name is "get" or "post" or "put" or "patch" or "delete"))
            {
                operations.Add((new HttpMethod(operation.Name.ToUpperInvariant()), path.Name));
            }
        }

        operations.Should().NotBeEmpty();
        return operations;
    }

    private static IEnumerable<string> CommittedWorkspacePaths()
    {
        var file = Path.Combine(RepositoryRoot(), "src", "Opportunity.Api", "openapi", "opportunity-api-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        return [.. document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).Where(p => p.Contains("{workspaceId}", StringComparison.Ordinal))];
    }

    private static string Expand(string path, string workspace) =>
        RouteParameter().Replace(path.Replace("{workspaceId}", workspace, StringComparison.Ordinal), _ => Guid.CreateVersion7().ToString());

    private static List<string> Permissions(JsonDocument document) =>
        [.. document.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Opportunity.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex RouteParameter();
}
