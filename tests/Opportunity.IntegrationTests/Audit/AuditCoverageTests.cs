using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Security;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>
/// E14-T02 catalogue gate (no containers): every route the API host maps is in <see cref="AuditCoverageCatalog"/>, and a
/// route its endpoint metadata marks as protected content, search execution or administration names the event it writes.
/// </summary>
public sealed class AuditCoverageCatalogTests
{
    [Fact]
    public async Task Every_mapped_route_is_classified_and_no_route_that_must_be_audited_is_exempt()
    {
        await using var factory = new ApiFactory();
        using (factory.CreateClient())
        {
        }

        var endpoints = new Dictionary<string, RouteEndpoint>(StringComparer.Ordinal);
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).Trim('/');
            if (pattern.Contains("/test-", StringComparison.Ordinal))
            {
                continue; // ApiFactory's convention probes, not product routes
            }

            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
            {
                endpoints[$"{method} {pattern}"] = endpoint;
            }
        }

        var cases = AuditCoverageCatalog.Cases;
        cases.Select(c => c.Key).Should().OnlyHaveUniqueItems();
        endpoints.Keys.Except(cases.Select(c => c.Key)).Should().BeEmpty(
            "a new endpoint needs a case in AuditCoverageCatalog: the event it writes and how it is proved, or why it writes none");
        cases.Select(c => c.Key).Except(endpoints.Keys).Should().BeEmpty("the catalogue lists only routes the API maps");

        var tests = typeof(AuditCoverageCatalogTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var c in cases)
        {
            var mustAudit = AuditCoverageCatalog.MustAudit(endpoints[c.Key], c.Method);
            switch (c.Coverage)
            {
                case AuditCoverage.NotAudited when mustAudit is not null:
                    problems.Add($"{c.Key}: must be audited ({mustAudit}) but is listed as not audited");
                    break;
                case AuditCoverage.NotAudited when string.IsNullOrWhiteSpace(c.Note):
                    problems.Add($"{c.Key}: states no reason for writing no event");
                    break;
                case AuditCoverage.CoveredBy when c.Note is null || !tests.Contains(c.Note):
                    problems.Add($"{c.Key}: names no existing test ({c.Note})");
                    break;
                case AuditCoverage.Probe when RouteAttackCatalog.Cases.SingleOrDefault(r => r.Key == c.Key) is not { Probes.Count: > 0 }:
                    problems.Add($"{c.Key}: has no attack-suite probe to send");
                    break;
                case AuditCoverage.Custom when c.Custom is null:
                    problems.Add($"{c.Key}: custom case without a request");
                    break;
            }

            if (c.Coverage != AuditCoverage.NotAudited
                && (c.Event?.Split('.', 2) is not [var category, var action] || !Application.Audit.AuditTaxonomy.IsDefined(category, action)))
            {
                problems.Add($"{c.Key}: '{c.Event}' is not an ADR-013 event");
            }
        }

        problems.Should().BeEmpty();
        cases.Count(c => AuditCoverageCatalog.MustAudit(endpoints[c.Key], c.Method) is not null).Should().BeGreaterThan(60);
    }
}

/// <summary>
/// E14-T02 (CI gate): every route of <see cref="AuditCoverageCatalog"/> that must write an audit event is called through
/// the real API host and its event is read back from <c>audit.audit_event</c> by the request's correlation ID, with the
/// complete envelope (actor, workspace, object, action, outcome, UTC time, client IP, session hash, correlation ID). The
/// same request from a non-member is refused and that denied attempt is audited too. A route that writes no event fails.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class AuditCoverageTests(AttackWorldFixture fixture) : IClassFixture<AttackWorldFixture>
{
    private static readonly byte[] SessionHashKey = new byte[32];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AttackWorld W => fixture.World;

    [Fact]
    public async Task Every_audited_route_records_a_complete_event_and_a_non_members_attempt_is_audited_as_denied()
    {
        var w = W;
        var failures = new List<string>();
        var report = new StringBuilder("# Audit coverage (E14-T02)\n\n| Route | Event | Proved by |\n|---|---|---|\n");
        var audited = 0;
        foreach (var c in AuditCoverageCatalog.Cases.Where(c => c.Coverage is AuditCoverage.Probe or AuditCoverage.Custom).OrderBy(c => c.Method == "DELETE"))
        {
            AuditRequest request;
            if (c.Coverage == AuditCoverage.Probe)
            {
                var probe = AuditCoverageCatalog.AttackProbe(c);
                request = new AuditRequest(probe.Method, probe.Url(w.A, w.A), w.Attacker,
                    probe.Body is null ? null : () => probe.Body(w.A, w.A), probe.IfMatch);
            }
            else
            {
                request = await c.Custom!(w);
            }

            // The denied attempt first: it changes nothing.
            var denied = await SendAsync(request with { Actor = w.Victim });
            if (denied.Status != HttpStatusCode.NotFound)
            {
                failures.Add($"{c.Key}: a non-member got {(int)denied.Status}, not 404");
            }

            Expect(failures, c.Key + " (non-member)", denied, w.Victim, w.A.WorkspaceId, "AuthZ.Denied", "Denied");

            var answer = await SendAsync(request);
            if ((int)answer.Status is < 200 or >= 300)
            {
                failures.Add($"{c.Key}: answered {(int)answer.Status} {answer.Text}");
                continue;
            }

            Expect(failures, c.Key, answer, request.Actor, w.A.WorkspaceId, c.Event!, "Success");
            audited++;
            report.Append(CultureInfo.InvariantCulture, $"| `{c.Key}` | {c.Event} | {c.Coverage} |\n");
        }

        foreach (var c in AuditCoverageCatalog.Cases.Where(c => c.Coverage is AuditCoverage.Scenario or AuditCoverage.CoveredBy))
        {
            report.Append(CultureInfo.InvariantCulture, $"| `{c.Key}` | {c.Event} | {c.Coverage}{(c.Note is null ? string.Empty : ": " + c.Note)} |\n");
        }

        TestContext.Current.AddAttachment("audit-coverage.md", report.ToString());
        failures.Should().BeEmpty();
        audited.Should().BeGreaterThan(70);
    }

    [Fact]
    public async Task Workspace_creation_holds_and_deletion_requests_are_audited_with_their_denied_attempts()
    {
        var w = W;
        var failures = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        const string V1 = "/api/v1";
        const string Ws = V1 + "/workspaces/{workspaceId}";

        // Workspace creation: installation administrators with MFA; anyone else is refused on the system chain.
        var name = "Audit lifecycle " + Guid.NewGuid().ToString("N")[..8];
        var create = new AuditRequest(HttpMethod.Post, V1 + "/workspaces", w.Attacker,
            () => AttackWorld.Json(new JsonObject { ["name"] = name, ["displayTimeZone"] = "UTC" }), Amr: "mfa");
        var refused = await SendAsync(create with { Actor = w.Victim }, AttackWorld.InstallationAdminGroup + "-not");
        Expect(failures, "POST /workspaces (no installation permission)", refused, w.Victim, null, "AuthZ.Denied", "Denied", HttpStatusCode.Forbidden);
        var created = await SendAsync(create, AttackWorld.InstallationAdminGroup);
        var ws = Guid.Parse(Json(created).GetProperty("workspaceId").GetString()!);
        Expect(failures, "POST /workspaces", created, w.Attacker, ws, "Workspace.Created", "Success", HttpStatusCode.Created);
        covered.Add("POST " + V1 + "/workspaces");
        var root = $"{V1}/workspaces/{ws}";
        var partner = await w.Db.CreateUserAsync();
        await w.Db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, partner);

        async Task<Answer> StepAsync(string key, AuditRequest request, string auditEvent, HttpStatusCode status, bool deniedAttempt = true)
        {
            if (deniedAttempt)
            {
                var attempt = await SendAsync(request with { Actor = w.Victim });
                Expect(failures, key + " (non-member)", attempt, w.Victim, ws, "AuthZ.Denied", "Denied", HttpStatusCode.NotFound);
            }

            var answer = await SendAsync(request);
            Expect(failures, key, answer, request.Actor, ws, auditEvent, "Success", status);
            covered.Add(key);
            return answer;
        }

        // Legal hold: placed, release requested, withdrawn, requested again and approved by a second person.
        var placed = await StepAsync("POST " + Ws + "/preservation-locks",
            new AuditRequest(HttpMethod.Post, root + "/preservation-locks", w.Attacker,
                () => AttackWorld.Json(new JsonObject { ["reason"] = "Audit coverage hold", ["releaseRequiresApproval"] = true })),
            "Workspace.HoldPlaced", HttpStatusCode.Created);
        var release = $"{root}/preservation-locks/{Json(placed).GetProperty("lockId").GetString()}/release";
        var reason = () => AttackWorld.Json(new JsonObject { ["reason"] = "Matter resolved" });
        await StepAsync("POST " + Ws + "/preservation-locks/{lockId}/release",
            new AuditRequest(HttpMethod.Post, release, w.Attacker, reason, "\"1\""), "Workspace.HoldReleaseRequested", HttpStatusCode.OK);
        await StepAsync("POST " + Ws + "/preservation-locks/{lockId}/release/cancel",
            new AuditRequest(HttpMethod.Post, release + "/cancel", w.Attacker, IfMatch: "\"2\""), "Workspace.HoldReleaseCancelled", HttpStatusCode.OK);
        await StepAsync("POST " + Ws + "/preservation-locks/{lockId}/release",
            new AuditRequest(HttpMethod.Post, release, w.Attacker, reason, "\"3\""), "Workspace.HoldReleaseRequested", HttpStatusCode.OK, deniedAttempt: false);
        await StepAsync("POST " + Ws + "/preservation-locks/{lockId}/release/approve",
            new AuditRequest(HttpMethod.Post, release + "/approve", partner, IfMatch: "\"4\""), "Workspace.HoldReleased", HttpStatusCode.OK);

        // Deletion: requested, approved by a Retention Approver with MFA (a refused approval is audited), withdrawn.
        var requested = await StepAsync("POST " + Ws + "/deletions",
            new AuditRequest(HttpMethod.Post, root + "/deletions", w.Attacker,
                () => AttackWorld.Json(new JsonObject { ["reason"] = "Audit coverage: matter closed", ["confirmName"] = name })),
            "Workspace.DeletionRequested", HttpStatusCode.Created);
        var deletion = $"{V1}/workspace-deletions/{Json(requested).GetProperty("deletionId").GetString()}";
        var approver = await w.Db.CreateUserAsync();
        var approve = new AuditRequest(HttpMethod.Post, deletion + "/approve", approver, IfMatch: "\"1\"", Amr: "mfa");
        var unapproved = await SendAsync(approve with { Actor = w.Victim });
        Expect(failures, "approve (no installation permission)", unapproved, w.Victim, null, "AuthZ.Denied", "Denied", HttpStatusCode.Forbidden);
        Expect(failures, "POST " + V1 + "/workspace-deletions/{deletionId}/approve", await SendAsync(approve, AttackWorld.RetentionApproverGroup),
            approver, ws, "Workspace.DeletionApproved", "Success", HttpStatusCode.OK);
        covered.Add("POST " + V1 + "/workspace-deletions/{deletionId}/approve");
        Expect(failures, "POST " + V1 + "/workspace-deletions/{deletionId}/cancel",
            await SendAsync(new AuditRequest(HttpMethod.Post, deletion + "/cancel", w.Attacker, IfMatch: "\"2\"")),
            w.Attacker, ws, "Workspace.DeletionCancelled", "Success", HttpStatusCode.OK);
        covered.Add("POST " + V1 + "/workspace-deletions/{deletionId}/cancel");

        failures.Should().BeEmpty();
        covered.Should().BeEquivalentTo(AuditCoverageCatalog.Cases.Where(c => c.Coverage == AuditCoverage.Scenario).Select(c => c.Key),
            "the scenario proves every Scenario case of the catalogue");
    }

    [Fact]
    public async Task No_application_role_including_workspace_admin_can_edit_or_delete_audit_events()
    {
        var w = W;

        // 1. The API offers no way to change the trail: no state-changing route under any audit path, and no permission or
        //    role that grants more than reading it (Workspace Admin holds every workspace permission).
        var routes = w.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? string.Empty).Contains("audit", StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
            .ToList();
        routes.Should().OnlyContain(m => m == "GET", "the audit trail is read-only over the API");
        PermissionCatalog.All.Where(p => p.Name.StartsWith("Audit.", StringComparison.Ordinal)).Select(p => p.Permission)
            .Should().BeEquivalentTo([Permission.AuditRead, Permission.AuditReadSearchText], "no permission edits or deletes audit events");
        RoleCatalog.All.Single(r => r.Role == WorkspaceRole.WorkspaceAdmin).Grants
            .Where(p => PermissionCatalog.Get(p).Name.StartsWith("Audit.", StringComparison.Ordinal))
            .Should().OnlyContain(p => p == Permission.AuditRead || p == Permission.AuditReadSearchText);

        await using var admin = NpgsqlDataSource.Create(w.Db.Core.ConnectionString);
        Guid eventId;
        await using (var first = admin.CreateCommand("SELECT event_id FROM audit.audit_event WHERE workspace_id = @ws LIMIT 1"))
        {
            first.Parameters.AddWithValue("ws", w.A.WorkspaceId);
            eventId = (Guid)(await first.ExecuteScalarAsync(Ct))!;
        }

        var before = await TrailAsync(w.A.WorkspaceId);
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            foreach (var url in new[] { $"/api/v1/workspaces/{w.A.WorkspaceId}/audit-events/{eventId}", $"/api/v1/workspaces/{w.A.WorkspaceId}/audit-events" })
            {
                using var response = await w.SendAsync(method, url, w.Attacker, AttackWorld.Json(new JsonObject { ["actorId"] = "forged" }), ifMatch: "*");
                ((int)response.StatusCode).Should().BeOneOf([404, 405], "{0} {1} as Workspace Admin", method, url);
            }
        }

        var after = await TrailAsync(w.A.WorkspaceId);
        after.Should().ContainKeys(before.Keys, "no audit event of the workspace disappeared");
        before.Should().AllSatisfy(e => after[e.Key].Should().Be(e.Value, "audit event {0} is unchanged", e.Key));

        // 2. In PostgreSQL every application role (the opportunity_* group roles and the API's own login) is refused UPDATE,
        //    DELETE and TRUNCATE of audit events and owns nothing it could disable the guarding triggers on; only the
        //    superuser and the schema owner, which run migrations and never the application, are left out (and the
        //    triggers stop the owner too, AuditStoreTests).
        var roles = new List<string> { new NpgsqlConnectionStringBuilder(w.Db.Core.AppConnectionString).Username! };
        await using (var list = admin.CreateCommand(
            """
            SELECT r.rolname FROM pg_roles r
            WHERE NOT r.rolsuper AND r.rolname LIKE 'opportunity\_%'
              AND r.oid <> (SELECT relowner FROM pg_class WHERE oid = 'audit.audit_event'::regclass)
            ORDER BY 1
            """))
        await using (var reader = await list.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                roles.Add(reader.GetString(0));
            }
        }

        roles.Should().Contain(["opportunity_app", "opportunity_readonly", "opportunity_audit_sealer", "opportunity_audit_retention"]);
        foreach (var role in roles)
        {
            foreach (var sql in new[]
            {
                $"UPDATE audit.audit_event SET actor_id = 'forged' WHERE event_id = '{eventId}'",
                $"UPDATE audit.audit_event SET details = '{{}}' WHERE event_id = '{eventId}'",
                $"DELETE FROM audit.audit_event WHERE event_id = '{eventId}'",
                "TRUNCATE audit.audit_event",
                "ALTER TABLE audit.audit_event DISABLE TRIGGER USER",
            })
            {
                await using var connection = await admin.OpenConnectionAsync(Ct);
                await using var tx = await connection.BeginTransactionAsync(Ct);
                await using (var setRole = new NpgsqlCommand($"SET LOCAL ROLE \"{role}\"; SELECT set_config('app.workspace_id', '{w.A.WorkspaceId}', true)", connection, tx))
                {
                    await setRole.ExecuteNonQueryAsync(Ct);
                }

                await using var command = new NpgsqlCommand(sql, connection, tx);
                var act = () => command.ExecuteNonQueryAsync(Ct);
                (await act.Should().ThrowAsync<PostgresException>("{0} as {1}", sql, role)).Which.SqlState
                    .Should().BeOneOf([PostgresErrorCodes.InsufficientPrivilege, "42501"], "{0} as {1}", sql, role);
            }
        }

        (await TrailAsync(w.A.WorkspaceId)).Should().ContainKey(eventId);
    }

    /// <summary>One request with a fresh correlation ID, and the audit events that carry it.</summary>
    private sealed record Answer(HttpStatusCode Status, string Text, string CorrelationId, IReadOnlyList<StoredEvent> Events);

    private sealed record StoredEvent(
        Guid? WorkspaceId, DateTimeOffset OccurredAt, string Name, string ActorType, string ActorId, string? ClientIp, byte[]? SessionIdHash,
        string? ResourceType, string? ResourceId, string Outcome, string? ReasonCode, string CorrelationId);

    private async Task<Answer> SendAsync(AuditRequest request, string? groups = null)
    {
        var correlation = "audit-coverage-" + Guid.NewGuid().ToString("N");
        using var response = await W.SendAsync(
            request.Method, request.Url, request.Actor, request.Body?.Invoke(), request.IfMatch, groups, request.Amr, correlation);
        var text = response.Content.Headers.ContentType?.MediaType is { } media && (media.Contains("json", StringComparison.Ordinal) || media.StartsWith("text/", StringComparison.Ordinal))
            ? await response.Content.ReadAsStringAsync(Ct)
            : string.Empty;
        return new Answer(response.StatusCode, text, correlation, await EventsAsync(correlation));
    }

    private async Task<IReadOnlyList<StoredEvent>> EventsAsync(string correlationId)
    {
        await using var dataSource = NpgsqlDataSource.Create(W.Db.Core.ConnectionString);
        await using var command = dataSource.CreateCommand(
            """
            SELECT workspace_id, occurred_at, category || '.' || action, actor_type, actor_id, host(client_ip), session_id_hash,
                   resource_type, resource_id, outcome, reason_code, correlation_id
            FROM audit.audit_event WHERE correlation_id = @c ORDER BY occurred_at, event_id
            """);
        command.Parameters.AddWithValue("c", correlationId);
        var events = new List<StoredEvent>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            events.Add(new StoredEvent(
                reader.IsDBNull(0) ? null : reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11)));
        }

        return events;
    }

    /// <summary>Every event of the workspace's chain by ID, with a digest of its content columns.</summary>
    private async Task<Dictionary<Guid, string>> TrailAsync(Guid workspaceId)
    {
        await using var dataSource = NpgsqlDataSource.Create(W.Db.Core.ConnectionString);
        await using var command = dataSource.CreateCommand(
            """
            SELECT event_id, md5(concat_ws('|', occurred_at, category, action, actor_id, resource_type, resource_id, outcome, correlation_id, details::text))
            FROM audit.audit_event WHERE workspace_id = @ws
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        var trail = new Dictionary<Guid, string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            trail[reader.GetGuid(0)] = reader.GetString(1);
        }

        return trail;
    }

    /// <summary>
    /// The answer has the expected status and an event <paramref name="expected"/> of <paramref name="actor"/>, and every
    /// event of the actor in the request carries the complete ADR-013 §4 envelope.
    /// </summary>
    private static void Expect(
        List<string> failures, string label, Answer answer, Guid actor, Guid? workspaceId, string expected, string outcome, HttpStatusCode? status = null)
    {
        if (status is { } s && answer.Status != s)
        {
            failures.Add($"{label}: answered {(int)answer.Status}, expected {(int)s}: {answer.Text}");
            return;
        }

        var own = answer.Events.Where(e => e.ActorId == actor.ToString()).ToList();
        if (!own.Any(e => e.Name == expected && e.Outcome == outcome))
        {
            failures.Add($"{label}: no {expected} ({outcome}) event; the request wrote [{string.Join(", ", answer.Events.Select(e => $"{e.Name} {e.Outcome}"))}]");
            return;
        }

        var session = HMACSHA256.HashData(SessionHashKey, Encoding.ASCII.GetBytes(AttackWorld.SessionOf(actor).ToString("N")));
        foreach (var e in own)
        {
            var missing = new List<string>();
            if (e.ActorType != "User")
            {
                missing.Add("actor type " + e.ActorType);
            }

            if (e.WorkspaceId != workspaceId)
            {
                missing.Add($"workspace {e.WorkspaceId}");
            }

            if (string.IsNullOrEmpty(e.ResourceType) || string.IsNullOrEmpty(e.ResourceId))
            {
                missing.Add("object");
            }

            if (e.Outcome != "Success" && string.IsNullOrEmpty(e.ReasonCode))
            {
                missing.Add("reason");
            }

            if (e.OccurredAt.Offset != TimeSpan.Zero || (DateTimeOffset.UtcNow - e.OccurredAt).Duration() > TimeSpan.FromMinutes(5))
            {
                missing.Add($"UTC time {e.OccurredAt:O}");
            }

            if (e.ClientIp != AttackWorld.ClientAddress.ToString())
            {
                missing.Add($"client IP {e.ClientIp}");
            }

            if (e.SessionIdHash is null || !e.SessionIdHash.AsSpan().SequenceEqual(session))
            {
                missing.Add("session hash");
            }

            if (e.CorrelationId != answer.CorrelationId)
            {
                missing.Add("correlation ID");
            }

            if (missing.Count > 0)
            {
                failures.Add($"{label}: {e.Name} lacks {string.Join(", ", missing)}");
            }
        }
    }

    private static JsonElement Json(Answer answer)
    {
        using var document = JsonDocument.Parse(answer.Text);
        return document.RootElement.Clone();
    }
}
