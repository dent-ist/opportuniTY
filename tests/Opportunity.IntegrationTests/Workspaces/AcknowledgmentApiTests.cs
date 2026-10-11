using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Workspaces;

/// <summary>
/// E20-T03 through the real API host, the PostgreSQL PDP and audit store: once a workspace publishes acknowledgment text,
/// every workspace route answers 403 <c>acknowledgment-required</c> to a member who has not accepted the current version
/// (except reading and accepting it, and the workspace descriptor); accepting stores the version, the text hash, the user
/// and the time and is audited; a new version requires accepting again; administrators see and export who accepted which
/// version.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class AcknowledgmentApiTests(MigrationPostgresFixture postgres)
{
    private const string Title = "Protective order acknowledgment (Exhibit A)";
    private const string Text = "I have read the Stipulated Protective Order entered in this matter.\r\nI agree to be bound by it.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Content_routes_answer_acknowledgment_required_until_the_current_version_is_accepted_and_again_after_a_new_version()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        var reviewer = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        var document = await db.DocumentAsync(ws);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}";
        var contentRoutes = new[] { $"{root}/documents/{document}", $"{root}/documents/{document}/text", $"{root}/jobs", $"{root}/fields" };

        // Nothing published: nothing is required and the routes work as before.
        using (var state = await JsonAsync(await SendAsync(client, HttpMethod.Get, root + "/acknowledgment", reviewer)))
        {
            state.RootElement.GetProperty("required").GetBoolean().Should().BeFalse();
            state.RootElement.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        }

        (await SendAsync(client, HttpMethod.Get, root + "/jobs", reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);

        var v1 = await PublishAsync(client, root, admin, "\"0\"", Title, Text);
        v1.GetProperty("version").GetInt32().Should().Be(1);
        var hash1 = v1.GetProperty("textSha256").GetString()!;
        hash1.Should().Be(Sha256(Title + "\n\n" + Text.Replace("\r\n", "\n", StringComparison.Ordinal)), "the hash is SHA-256 of title LF LF text");

        // The publisher too, and every member, is blocked until accepting; non-members still get the same 404 as before.
        foreach (var user in new[] { reviewer, admin })
        {
            foreach (var route in contentRoutes)
            {
                var problem = await (await SendAsync(client, HttpMethod.Get, route, user))
                    .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "acknowledgment-required");
                problem.GetProperty("acknowledgmentVersion").GetInt32().Should().Be(1);
                problem.GetProperty("acknowledgmentUrl").GetString().Should().Be($"{root}/acknowledgment");
            }
        }

        await (await SendAsync(client, HttpMethod.Post, root + "/searches", reviewer, """{"query":"memo"}"""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "acknowledgment-required");
        await (await SendAsync(client, HttpMethod.Get, root + "/jobs", await db.CreateUserAsync())).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await SendAsync(client, HttpMethod.Get, root + "/acknowledgment", await db.CreateUserAsync())).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        // The workspace descriptor and the acknowledgment stay readable, so the text can be shown.
        using (var workspace = await JsonAsync(await SendAsync(client, HttpMethod.Get, root, reviewer)))
        {
            workspace.RootElement.GetProperty("acknowledgmentPending").GetBoolean().Should().BeTrue();
        }

        using (var state = await JsonAsync(await SendAsync(client, HttpMethod.Get, root + "/acknowledgment", reviewer)))
        {
            var r = state.RootElement;
            r.GetProperty("required").GetBoolean().Should().BeTrue();
            r.GetProperty("acknowledged").GetBoolean().Should().BeFalse();
            r.GetProperty("version").GetInt32().Should().Be(1);
            r.GetProperty("title").GetString().Should().Be(Title);
            r.GetProperty("text").GetString().Should().Be("I have read the Stipulated Protective Order entered in this matter.\nI agree to be bound by it.");
            r.GetProperty("textSha256").GetString().Should().Be(hash1);
        }

        // Accepting needs the version and hash that were shown.
        await (await SendAsync(client, HttpMethod.Post, root + "/acknowledgment/acceptances", reviewer, """{"version":1}"""))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await SendAsync(client, HttpMethod.Post, root + "/acknowledgment/acceptances", reviewer, Accept(1, new string('0', 64))))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "acknowledgment-outdated");

        DateTimeOffset acceptedAt;
        using (var accepted = await SendAsync(client, HttpMethod.Post, root + "/acknowledgment/acceptances", reviewer, Accept(1, hash1)))
        {
            accepted.StatusCode.Should().Be(HttpStatusCode.Created);
            using var json = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(Ct));
            json.RootElement.GetProperty("version").GetInt32().Should().Be(1);
            json.RootElement.GetProperty("textSha256").GetString().Should().Be(hash1);
            acceptedAt = json.RootElement.GetProperty("acceptedAt").GetDateTimeOffset();
        }

        (await SendAsync(client, HttpMethod.Post, root + "/acknowledgment/acceptances", reviewer, Accept(1, hash1))).StatusCode
            .Should().Be(HttpStatusCode.OK, "accepting again changes nothing");

        // The reviewer is through; the admin, who has not accepted, is still blocked.
        (await SendAsync(client, HttpMethod.Get, root + "/jobs", reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Get, $"{root}/documents/{document}", reviewer)).StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        await (await SendAsync(client, HttpMethod.Get, root + "/jobs", admin)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "acknowledgment-required");
        using (var workspace = await JsonAsync(await SendAsync(client, HttpMethod.Get, root, reviewer)))
        {
            workspace.RootElement.GetProperty("acknowledgmentPending").GetBoolean().Should().BeFalse();
        }

        // Accepting stores the version, the hash of the text, the user and the time.
        (await db.Core.ColumnAsync($"SELECT concat_ws('|', user_id, version, text_sha256) FROM opportunity.acknowledgment WHERE workspace_id = '{ws}'"))
            .Should().Equal($"{reviewer}|1|{hash1}");
        (await db.Core.ScalarAsync<DateTime>($"SELECT accepted_at FROM opportunity.acknowledgment WHERE workspace_id = '{ws}'"))
            .Should().BeCloseTo(acceptedAt.UtcDateTime, TimeSpan.FromMilliseconds(1));

        // A new version requires accepting again; the old version can no longer be accepted.
        await AcceptAsync(client, root, admin, 1, hash1);
        var v2 = await PublishAsync(client, root, admin, "\"1\"", Title, Text + "\nI also confirm I have no conflict of interest.");
        var hash2 = v2.GetProperty("textSha256").GetString()!;
        hash2.Should().NotBe(hash1);
        foreach (var user in new[] { reviewer, admin })
        {
            (await (await SendAsync(client, HttpMethod.Get, root + "/jobs", user)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "acknowledgment-required"))
                .GetProperty("acknowledgmentVersion").GetInt32().Should().Be(2);
        }

        await (await SendAsync(client, HttpMethod.Post, root + "/acknowledgment/acceptances", reviewer, Accept(1, hash1)))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "acknowledgment-outdated");
        await AcceptAsync(client, root, reviewer, 2, hash2);
        (await SendAsync(client, HttpMethod.Get, root + "/jobs", reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Every publication and acceptance is audited with the version and its hash; every refusal is AuthZ.Denied.
        var audit = await db.Core.ColumnAsync(
            $"""
            SELECT concat_ws('|', action, actor_id, resource_type, resource_id, details->>'version', details->>'textSha256')
            FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Security' ORDER BY recorded_at
            """);
        audit.Should().Equal(
            $"AcknowledgmentPublished|{admin}|Workspace|{ws}|1|{hash1}",
            $"AcknowledgmentAccepted|{reviewer}|Workspace|{ws}|1|{hash1}",
            $"AcknowledgmentAccepted|{admin}|Workspace|{ws}|1|{hash1}",
            $"AcknowledgmentPublished|{admin}|Workspace|{ws}|2|{hash2}",
            $"AcknowledgmentAccepted|{reviewer}|Workspace|{ws}|2|{hash2}");
        (await db.Core.ColumnAsync($"SELECT a.audit_event_id::text FROM opportunity.acknowledgment a WHERE a.workspace_id = '{ws}' AND a.user_id = '{reviewer}' AND a.version = 2"))
            .Should().Equal(await db.Core.ColumnAsync(
                $"SELECT event_id::text FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'AcknowledgmentAccepted' AND actor_id = '{reviewer}' AND details->>'version' = '2'"));
        (await db.Core.ScalarAsync<long>(
            $"""
            SELECT count(*) FROM audit.audit_event
            WHERE workspace_id = '{ws}' AND action = 'Denied' AND outcome = 'Denied' AND reason_code = 'AcknowledgmentRequired'
            """)).Should().BeGreaterThan(0);
        (await db.Core.ColumnAsync($"SELECT details::text FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Security'"))
            .Should().NotContain(d => d.Contains("Protective", StringComparison.Ordinal) || d.Contains("bound", StringComparison.Ordinal),
                "the text itself never goes to audit");
    }

    [Fact]
    public async Task Every_workspace_route_except_reading_and_accepting_the_text_is_behind_the_gate()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}";
        await PublishAsync(client, root, admin, "\"0\"", Title, Text);

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.GetParameter("workspaceId") is not null)
            .ToList();
        endpoints.Count.Should().BeGreaterThan(100, "the sweep sees every workspace route");
        string[] open = ["GET /api/v1/workspaces/{workspaceId}/", "GET /api/v1/workspaces/{workspaceId}/acknowledgment",
            "POST /api/v1/workspaces/{workspaceId}/acknowledgment/acceptances"];
        // Break-glass activation grants nothing by itself (the holder is not a member until it is active, and then meets the
        // gate on every content route), so it is neither gated nor on the list.
        const string breakGlass = "POST /api/v1/workspaces/{workspaceId}/security/break-glass/activations";
        var reached = new List<string>();
        foreach (var endpoint in endpoints)
        {
            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                var key = $"{method} /{endpoint.RoutePattern.RawText!.TrimStart('/')}";
                // Routing only selects a form endpoint for a form body; any body reaches PEP-1, which runs before binding.
                var form = endpoint.Metadata.GetMetadata<IAcceptsMetadata>() is { ContentTypes.Count: > 0 } accepts
                    && !accepts.ContentTypes.Any(t => t.Contains("json", StringComparison.Ordinal));
                using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(Url(endpoint.RoutePattern, ws), UriKind.Relative));
                request.Headers.Add(TestAuthentication.UserHeader, admin.ToString());
                if (method is not "GET" and not "DELETE")
                {
                    request.Content = form ? new MultipartFormDataContent { { new StringContent("{}"), "request" } } : new StringContent("{}", Encoding.UTF8, "application/json");
                }

                using var response = await client.SendAsync(request, Ct);
                var body = await response.Content.ReadAsStringAsync(Ct);
                var gated = response.StatusCode == HttpStatusCode.Forbidden && body.Contains("\"acknowledgment-required\"", StringComparison.Ordinal);
                if (key != breakGlass && gated == open.Contains(key))
                {
                    reached.Add($"{key} -> {(int)response.StatusCode}");
                }
            }
        }

        reached.Should().BeEmpty("only {0} are served before the acknowledgment", string.Join(", ", open));
    }

    [Fact]
    public async Task Admins_publish_versions_and_see_and_export_who_acknowledged_which_version()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        var current = await db.CreateUserAsync();
        var outdated = await db.CreateUserAsync();
        var pending = await db.CreateUserAsync();
        var viaGroup = await db.CreateUserAsync(groups: ["reviewers"]);
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, current);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, outdated);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, pending);
        await db.AssignGroupAsync(ws, WorkspaceRole.Reviewer, "reviewers");
        foreach (var (user, name) in new[] { (admin, "Avery Admin"), (current, "Casey Current"), (outdated, "Oakley Outdated"), (pending, "Parker Pending"), (viaGroup, "Gray Group") })
        {
            await db.Core.ExecuteAsync("UPDATE opportunity.app_user SET display_name = @name, email = @email WHERE user_id = @id",
                ("name", name), ("email", name.Split(' ')[0].ToLowerInvariant() + "@example.test"), ("id", user));
        }

        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}";
        var versions = root + "/acknowledgment-versions";

        // Publishing is versioned (If-Match), validated and needs Workspace.ManageAcknowledgments.
        await (await SendAsync(client, HttpMethod.Post, versions, admin, Publish(Title, Text))).ShouldBeProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
        await (await SendAsync(client, HttpMethod.Post, versions, admin, Publish(" ", Text), "\"0\"")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await SendAsync(client, HttpMethod.Post, versions, admin, Publish("Two\nlines", Text), "\"0\"")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await SendAsync(client, HttpMethod.Post, versions, admin, Publish(Title, "  "), "\"0\"")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await SendAsync(client, HttpMethod.Post, versions, current, Publish(Title, Text), "\"0\"")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        var hash1 = (await PublishAsync(client, root, admin, "\"0\"", Title, Text)).GetProperty("textSha256").GetString()!;

        // The publisher, too, accepts before administering the workspace further.
        await (await SendAsync(client, HttpMethod.Get, versions, admin)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "acknowledgment-required");
        await AcceptAsync(client, root, outdated, 1, hash1);
        await AcceptAsync(client, root, admin, 1, hash1);
        await (await SendAsync(client, HttpMethod.Post, versions, admin, Publish(Title, "Other"), "\"0\"")).ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");
        var hash2 = (await PublishAsync(client, root, admin, "\"1\"", Title, Text + "\nVersion two.")).GetProperty("textSha256").GetString()!;
        await AcceptAsync(client, root, admin, 2, hash2);
        await AcceptAsync(client, root, current, 2, hash2);
        await AcceptAsync(client, root, viaGroup, 2, hash2, "reviewers");

        // Members without the permission get 403 even after accepting.
        await (await SendAsync(client, HttpMethod.Get, versions, current)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Get, root + "/acknowledgment-roster", current)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Get, root + "/acknowledgment-roster/export", current)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        using (var response = await SendAsync(client, HttpMethod.Get, versions, admin))
        {
            response.Headers.ETag!.Tag.Should().Be("\"2\"");
            using var list = await JsonAsync(response);
            list.RootElement.GetProperty("currentVersion").GetInt32().Should().Be(2);
            list.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => $"{i.GetProperty("version").GetInt32()}|{i.GetProperty("acceptedCount").GetInt32()}|{i.GetProperty("isCurrent").GetBoolean()}|{i.GetProperty("publishedBy").GetProperty("displayName").GetString()}")
                .Should().Equal("2|3|True|Avery Admin", "1|2|False|Avery Admin");
        }

        using (var one = await JsonAsync(await SendAsync(client, HttpMethod.Get, versions + "/1", admin)))
        {
            one.RootElement.GetProperty("text").GetString().Should().Be(Text.Replace("\r\n", "\n", StringComparison.Ordinal));
            one.RootElement.GetProperty("textSha256").GetString().Should().Be(hash1);
        }

        await (await SendAsync(client, HttpMethod.Get, versions + "/3", admin)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        // The roster: who accepted which version, by name, paged.
        var entries = new List<string>();
        string? cursor = null;
        do
        {
            using var page = await JsonAsync(await SendAsync(client, HttpMethod.Get,
                root + "/acknowledgment-roster?limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), admin));
            page.RootElement.GetProperty("total").GetProperty("value").GetInt64().Should().Be(5);
            foreach (var item in page.RootElement.GetProperty("items").EnumerateArray())
            {
                entries.Add(string.Join('|',
                    item.GetProperty("displayName").GetString(),
                    item.GetProperty("status").GetString(),
                    item.GetProperty("directMember").GetBoolean(),
                    string.Join(',', item.GetProperty("acceptances").EnumerateArray().Select(a => a.GetProperty("version").GetInt32()))));
            }

            cursor = page.RootElement.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        entries.Should().Equal(
            "Avery Admin|current|True|2,1",
            "Casey Current|current|True|2",
            "Gray Group|current|False|2",
            "Oakley Outdated|outdated|True|1",
            "Parker Pending|pending|True|");

        // The roster exports as CSV (one row per acceptance, plus pending members), audited before the first byte.
        string csv;
        using (var export = await SendAsync(client, HttpMethod.Get, root + "/acknowledgment-roster/export", admin))
        {
            export.StatusCode.Should().Be(HttpStatusCode.OK);
            export.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
            export.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
            csv = Encoding.UTF8.GetString(await export.Content.ReadAsByteArrayAsync(Ct)).TrimStart('﻿');
        }

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("User ID,Name,Email,Direct Member,Status,Version,Current Version,Text SHA-256,Accepted At (UTC)");
        lines.Skip(1).Select(l => string.Join('|', l.Split(',')[1], l.Split(',')[4], l.Split(',')[5], l.Split(',')[7])).Should().Equal(
            $"Avery Admin|Accepted|2|{hash2}",
            $"Avery Admin|Superseded|1|{hash1}",
            $"Casey Current|Accepted|2|{hash2}",
            $"Gray Group|Accepted|2|{hash2}",
            $"Oakley Outdated|Superseded|1|{hash1}",
            "Parker Pending|Pending||");
        lines[1].Should().StartWith(admin.ToString("D", CultureInfo.InvariantCulture) + ",Avery Admin,avery@example.test,Yes,");
        (await db.Core.ColumnAsync(
            $"""
            SELECT concat_ws('|', action, actor_id, details->>'rows', details->>'members')
            FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'AcknowledgmentRosterExported'
            """)).Should().Equal($"AcknowledgmentRosterExported|{admin}|6|5");

        // Acknowledgments are append-only for the application.
        var refused = async () => await db.Core.InWorkspaceAsync(ws, async tx =>
        {
            await using var delete = tx.Command("DELETE FROM opportunity.acknowledgment WHERE workspace_id = @ws");
            delete.Parameters.AddWithValue("ws", ws);
            await delete.ExecuteNonQueryAsync(Ct);
        });
        await refused.Should().ThrowAsync<Npgsql.PostgresException>();
    }

    private static string Url(RoutePattern pattern, Guid ws)
    {
        var segments = pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart { Name: "workspaceId" } => ws.ToString(),
            RoutePatternParameterPart parameter when parameter.ParameterPolicies.Any(p => p.Content?.StartsWith("int", StringComparison.Ordinal) == true)
                || parameter.Name is "version" or "pageNumber" or "chunkIndex" or "fieldId" or "choiceId" => "1",
            RoutePatternParameterPart => Guid.CreateVersion7().ToString(),
            _ => string.Empty,
        })));
        return "/" + string.Join('/', segments);
    }

    private static string Publish(string title, string text) => JsonSerializer.Serialize(new { title, text });

    private static string Accept(int version, string hash) => JsonSerializer.Serialize(new { version, textSha256 = hash });

    private static async Task<JsonElement> PublishAsync(HttpClient client, string root, Guid admin, string ifMatch, string title, string text)
    {
        using var response = await SendAsync(client, HttpMethod.Post, root + "/acknowledgment-versions", admin, Publish(title, text), ifMatch);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task AcceptAsync(HttpClient client, string root, Guid user, int version, string hash, string? groups = null)
    {
        using var response = await SendAsync(client, HttpMethod.Post, root + "/acknowledgment/acceptances", user, Accept(version, hash), groups: groups);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static WebApplicationFactory<Program> Factory(AuthorizationDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
            });
        });

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid user, string? body = null, string? ifMatch = null, string? groups = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (groups is not null)
        {
            request.Headers.Add(TestAuthentication.GroupsHeader, groups);
        }
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.IsSuccessStatusCode.Should().BeTrue(text);
            return JsonDocument.Parse(text);
        }
    }
}
