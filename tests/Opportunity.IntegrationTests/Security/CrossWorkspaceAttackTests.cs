using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Messaging;
using Opportunity.Contracts.Api;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Exports;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Security;

/// <summary>Builds the attack world once for the class (it imports, indexes and exports in three workspaces).</summary>
public sealed class AttackWorldFixture(OpenSearchFixture openSearch, MigrationPostgresFixture postgres) : IAsyncLifetime
{
    internal AttackWorld World { get; private set; } = null!;

    public async ValueTask InitializeAsync() => World = await AttackWorld.CreateAsync(openSearch, postgres);

    public async ValueTask DisposeAsync()
    {
        if (World is not null)
        {
            await World.DisposeAsync();
        }
    }
}

/// <summary>
/// E05-T05 cross-workspace attack suite (CI gate, baseline §23/§26 "0 unauthorized protected-resource retrievals"):
/// every route of the endpoint data source is listed in <see cref="RouteAttackCatalog"/>, and every identifier position
/// of every workspace route is attacked with another workspace's identifiers (one the attacker administers, one they
/// have no role in). A foreign identifier must answer exactly like an identifier that exists nowhere (same status, same
/// body: no existence oracle) and never with data; replayed search handles and cursors are 404 and audited (Q-62);
/// forged job messages are rejected.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class CrossWorkspaceAttackTests(AttackWorldFixture fixture) : IClassFixture<AttackWorldFixture>
{
    private const string NotFoundDetail = "The resource does not exist.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AttackWorld W => fixture.World;

    [Fact]
    public void Every_mapped_route_has_an_attack_case_and_every_case_names_a_mapped_route()
    {
        var mapped = MappedRoutes();
        var listed = RouteAttackCatalog.Cases.Select(c => c.Key).ToList();
        listed.Should().OnlyHaveUniqueItems();
        mapped.Except(listed).Should().BeEmpty("a new endpoint needs a case in RouteAttackCatalog (probes for its identifiers, or the reason it has none)");
        listed.Except(mapped).Should().BeEmpty("the catalog lists only routes the API maps");

        foreach (var route in RouteAttackCatalog.Cases)
        {
            if (route.Pattern.Contains("{workspaceId}", StringComparison.Ordinal))
            {
                route.Probes.Should().NotBeEmpty("{0}: every workspace route is attacked", route.Key);
                // Page numbers, chunk indexes, field and choice ids (per-workspace counters, ADR-003 R4: every workspace has
                // field 1000 and choice 1), restriction class keys and redaction reason codes are workspace-local keys, and
                // user ids are installation-level (role assignment, E05-T08): none is an identifier another workspace could leak.
                var identifiers = route.Pattern.Split('/').Where(s => s.StartsWith('{')
                    && s is not "{workspaceId}" and not "{pageNumber}" and not "{chunkIndex}" and not "{fieldId}" and not "{choiceId}"
                        and not "{classKey}" and not "{reasonCode}" and not "{userId}");
                if (identifiers.Any())
                {
                    route.Probes.Should().Contain(p => p.HasForeignIdentifier, "{0}: its path identifiers are substituted", route.Key);
                }
            }
            else
            {
                route.NoWorkspaceIdentifier.Should().NotBeNullOrWhiteSpace("{0}: a route outside a workspace states why it takes no workspace identifier", route.Key);
            }
        }
    }

    [Fact]
    public async Task Foreign_identifiers_in_every_position_answer_exactly_like_unknown_ones_and_never_return_data()
    {
        var w = W;
        var failures = new List<string>();
        var probes = 0;
        // Deleting runs last: the own probe deletes a profile.
        foreach (var route in RouteAttackCatalog.Cases.Where(c => c.Probes.Count > 0).OrderBy(c => c.Method == "DELETE"))
        {
            foreach (var probe in route.Probes)
            {
                var label = $"{route.Key} [{probe.Name}]";
                probes++;

                // 1. Own identifiers: the probe is well-formed and reaches the resource (so a 404 below means something).
                var own = await SendAsync(probe, w.A, w.A, w.Attacker);
                if (probe.OwnStatus is { } expected ? own.Status != expected : own.IsGenericNotFound)
                {
                    failures.Add($"{label}: own identifiers answered {(int)own.Status} (expected {(probe.OwnStatus is { } e ? (int)e : "not 404")}): {own.Text}");
                    continue;
                }

                if (probe.Expectation == ForeignExpectation.EmptySet && Size(own) == 0)
                {
                    failures.Add($"{label}: own identifiers produced an empty set, so the foreign comparison proves nothing: {own.Text}");
                }

                LeakCheck(label + " own", own, failures);

                // 2. The workspace segment: a workspace the attacker has no role in vs one that does not exist.
                var nonMember = await SendAsync(probe, w.C, w.C, w.Attacker);
                var missingWorkspace = await SendAsync(probe, Unknown(w.C), Unknown(w.C), w.Attacker);
                if (nonMember.Status != HttpStatusCode.NotFound || !nonMember.SameAs(missingWorkspace))
                {
                    failures.Add($"{label}: non-member workspace answered {(int)nonMember.Status} {nonMember.Normalized}, missing workspace {(int)missingWorkspace.Status} {missingWorkspace.Normalized}");
                }

                if (!probe.HasForeignIdentifier)
                {
                    continue;
                }

                // 3. Every identifier of the probe replaced by another workspace's (administered: B; no role: C) or by unknown ones.
                var unknown = await SendAsync(probe, w.A, Unknown(w.A), w.Attacker);
                foreach (var foreign in new[] { w.B, w.C })
                {
                    var attack = await SendAsync(probe, w.A, foreign, w.Attacker);
                    LeakCheck($"{label} with {foreign.Name}'s identifiers", attack, failures);
                    var problem = probe.Expectation switch
                    {
                        ForeignExpectation.NotFound when attack.Status != HttpStatusCode.NotFound => "is not 404",
                        ForeignExpectation.SameAsUnknown when (int)attack.Status is >= 200 and < 300 => "is a success",
                        ForeignExpectation.EmptySet when (int)attack.Status is < 200 or >= 300 || Size(attack) != 0 || Size(unknown) != 0 => "is not an empty set",
                        _ when probe.Expectation != ForeignExpectation.EmptySet && !attack.SameAs(unknown) => "differs from the unknown identifier's answer",
                        _ when probe.Expectation == ForeignExpectation.EmptySet && attack.Status != unknown.Status => "differs from the unknown identifier's status",
                        _ => null,
                    };
                    if (problem is not null)
                    {
                        failures.Add($"{label} with {foreign.Name}'s identifiers {problem}: {(int)attack.Status} {attack.Normalized} vs unknown {(int)unknown.Status} {unknown.Normalized}");
                    }
                }
            }
        }

        probes.Should().BeGreaterThan(40);
        failures.Should().BeEmpty();

        // Nothing changed in the other workspaces: no profile deleted or renamed, nothing coded.
        foreach (var foreign in new[] { w.B, w.C })
        {
            (await w.Db.Core.ScalarAsync<long>(
                "SELECT count(*) FROM opportunity.import_profile WHERE workspace_id = @ws AND profile_id IN (@p, @s) AND name NOT LIKE 'Renamed%'",
                ("ws", foreign.WorkspaceId), ("p", foreign.ImportProfileId), ("s", foreign.SpareProfileId))).Should().Be(2, foreign.Name);
            (await w.Db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.coding_event WHERE workspace_id = @ws", ("ws", foreign.WorkspaceId)))
                .Should().Be(0, "{0}'s coding is untouched", foreign.Name);
        }
    }

    [Fact]
    public async Task Collections_outside_a_workspace_never_list_another_users_or_workspaces_data()
    {
        var w = W;
        using (var list = await w.SendAsync(HttpMethod.Get, "/api/v1/workspaces?limit=500", w.Attacker))
        {
            var text = await list.Content.ReadAsStringAsync(Ct);
            list.StatusCode.Should().Be(HttpStatusCode.OK, text);
            text.Should().Contain(w.A.WorkspaceId.ToString()).And.Contain(w.B.WorkspaceId.ToString()).And.NotContain(w.C.WorkspaceId.ToString());
        }

        const string key = "attack-suite.secret";
        using (var set = await w.SendAsync(HttpMethod.Put, $"/api/v1/me/preferences/{key}", w.Victim, AttackWorld.Json(JsonValue.Create("victim-value")!)))
        {
            set.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using (var read = await w.SendAsync(HttpMethod.Get, "/api/v1/me/preferences", w.Attacker))
        {
            (await read.Content.ReadAsStringAsync(Ct)).Should().NotContain("victim-value");
        }

        (await w.SendAsync(HttpMethod.Delete, $"/api/v1/me/preferences/{key}", w.Attacker)).Dispose();
        using (var own = await w.SendAsync(HttpMethod.Get, "/api/v1/me/preferences", w.Victim))
        {
            (await own.Content.ReadAsStringAsync(Ct)).Should().Contain("victim-value", "another user's delete addresses their own profile only");
        }
    }

    [Fact]
    public async Task Replayed_search_handles_and_cursors_are_404_and_audited_and_foreign_collection_cursors_reveal_nothing()
    {
        var w = W;
        var colleague = await w.Db.CreateUserAsync();
        await w.Db.AssignAsync(w.A.WorkspaceId, WorkspaceRole.WorkspaceAdmin, colleague);
        var ws = w.A.WorkspaceId;

        // The attacker's handle and cursor, replayed by a colleague of the same workspace.
        var replayed = await SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/searches/{w.A.SearchId:N}/pages?cursor={w.A.SearchCursor}", colleague);
        var missing = await SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/searches/{Guid.NewGuid():N}/pages?cursor={Guid.NewGuid():N}", colleague);
        replayed.Status.Should().Be(HttpStatusCode.NotFound);
        replayed.SameAs(missing).Should().BeTrue("a replayed handle is indistinguishable from a missing one");
        (await DeniedAsync(ws, colleague, "SearchHandleMismatch")).Should().Be(1, "the replayed handle is audited");

        // The attacker's cursor on the colleague's own handle.
        var own = await w.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", colleague, HttpStatusCode.OK,
            new JsonObject { ["query"] = "", ["pageSize"] = 1 });
        var ownHandle = own.GetProperty("searchId").GetString();
        var foreignCursor = await SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/searches/{ownHandle}/pages?cursor={w.A.SearchCursor}", colleague);
        var unknownCursor = await SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/searches/{ownHandle}/pages?cursor={Guid.NewGuid():N}", colleague);
        foreignCursor.Status.Should().Be(HttpStatusCode.NotFound);
        foreignCursor.SameAs(unknownCursor).Should().BeTrue("a replayed cursor is indistinguishable from an unknown one");
        (await DeniedAsync(ws, colleague, "SearchCursorMismatch")).Should().Be(1,
            "the cursor bound to another user's search is audited; an unknown or expired cursor is not (Q-71)");
        (await SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/searches/{ownHandle}/pages?cursor={own.GetProperty("nextCursor").GetString()}", colleague))
            .Status.Should().Be(HttpStatusCode.OK, "the handle's own cursor still works");

        // Collection cursors are bound to (user, workspace): another user's or workspace's cursor is the malformed-cursor answer.
        using var first = await w.SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/snapshots?limit=1", w.Attacker);
        var page = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct)).RootElement;
        var cursor = page.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNull("the attacker has more than one snapshot in A");
        var malformed = await SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/snapshots?limit=1&cursor=bm90LWEtY3Vyc29y", colleague);
        malformed.Status.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/snapshots?limit=1&cursor={cursor}", colleague)).SameAs(malformed).Should().BeTrue();
        (await SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{w.B.WorkspaceId}/snapshots?limit=1&cursor={cursor}", w.Attacker)).SameAs(malformed).Should().BeTrue();
    }

    [Fact]
    public async Task A_job_message_naming_another_workspace_is_rejected_and_runs_nothing()
    {
        var w = W;
        var creation = await w.Exports.CreateAsync(w.B.WorkspaceId, w.Attacker,
            new CreateExportRequest(w.B.ExportSnapshotId, [new(FieldId: Core.Fields.SystemFields.ControlNumber)]));
        await w.Exports.CoordinateAsync(w.B.WorkspaceId);
        var chunk = (await w.Import.Jobs.GetChunksAsync(w.B.WorkspaceId, creation.Job.JobId, cancellationToken: Ct))[0];
        chunk.Status.Should().Be(JobChunkStatus.Pending);

        // The envelope claims workspace A for B's chunk (and the job of A's import): RLS finds no such chunk in A.
        foreach (var (workspace, job) in new[] { (w.A.WorkspaceId, creation.Job.JobId), (w.A.WorkspaceId, w.A.ImportJobId), (w.B.WorkspaceId, w.B.ImportJobId) })
        {
            var forged = ExportHarness.Received(chunk);
            forged = forged with { Envelope = forged.Envelope with { WorkspaceId = workspace, JobId = job } };
            var act = () => w.Exports.Consumer().HandleAsync(ExportHarness.Payload(chunk), forged, Ct);
            await act.Should().ThrowAsync<PermanentMessageException>("{0}/{1}", workspace, job);
        }

        (await w.Import.Jobs.GetChunksAsync(w.B.WorkspaceId, creation.Job.JobId, cancellationToken: Ct))[0].Status
            .Should().Be(JobChunkStatus.Pending, "a forged message runs nothing");
        (await w.Exports.Exports.GetFilesAsync(w.B.WorkspaceId, creation.Export.ExportId, Opportunity.Api.Exports.ExportEndpoints.DeliveredKinds, null, 100, Ct))
            .Should().BeEmpty();
    }

    private Task<long> DeniedAsync(Guid ws, Guid actor, string reason) =>
        W.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND actor_id = @actor AND category = 'AuthZ' AND action = 'Denied' AND reason_code = @reason",
            ("ws", ws), ("actor", actor.ToString()), ("reason", reason));

    private static WorkspaceResources Unknown(WorkspaceResources like) => WorkspaceResources.Unknown(like.Fields) with { Name = like.Name };

    private async Task<Answer> SendAsync(RouteProbe probe, WorkspaceResources own, WorkspaceResources target, Guid user)
    {
        using var response = await W.SendAsync(probe.Method, probe.Url(own, target), user, probe.Body?.Invoke(own, target), probe.IfMatch);
        return await Answer.ReadAsync(response, target);
    }

    private async Task<Answer> SendAsync(HttpMethod method, string url, Guid user)
    {
        using var response = await W.SendAsync(method, url, user);
        return await Answer.ReadAsync(response, null);
    }

    /// <summary>A 2xx response of the attacker's own workspace never names another workspace's identifiers.</summary>
    private void LeakCheck(string label, Answer answer, List<string> failures)
    {
        if ((int)answer.Status is < 200 or >= 300 || !answer.IsText)
        {
            return;
        }

        foreach (var foreign in new[] { W.B, W.C })
        {
            if (foreign.IdentifierSpellings().FirstOrDefault(id => answer.Text.Contains(id, StringComparison.OrdinalIgnoreCase)) is { } leaked)
            {
                failures.Add($"{label}: the response carries {foreign.Name}'s identifier {leaked}");
            }
        }
    }

    /// <summary>The size of a set-valued answer: a snapshot's frozen count, or a search page's hits, total and facet buckets.</summary>
    private static long Size(Answer answer)
    {
        if (!answer.IsJson)
        {
            return -1;
        }

        var root = JsonDocument.Parse(answer.Text).RootElement;
        if (root.TryGetProperty("documentCount", out var count))
        {
            return count.ValueKind == JsonValueKind.Number ? count.GetInt64() : -1;
        }

        if (root.TryGetProperty("items", out var items) && root.TryGetProperty("total", out var total))
        {
            var buckets = root.TryGetProperty("facets", out var facets)
                ? facets.EnumerateArray().Sum(f => f.GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty("count").GetInt64()))
                : 0;
            return items.GetArrayLength() + total.GetProperty("value").GetInt64() + buckets;
        }

        return -1;
    }

    private static List<string> MappedRoutes(IServiceProvider services)
    {
        var routes = new List<string>();
        foreach (var endpoint in services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).Trim('/');
            if (pattern.Contains("/test-", StringComparison.Ordinal))
            {
                continue; // ApiFactory's convention probes (tests/.../Api), not product routes
            }

            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
            {
                routes.Add($"{method} {pattern}");
            }
        }

        return [.. routes.Distinct()];
    }

    private List<string> MappedRoutes()
    {
        using var _ = W.Factory.CreateClient();
        return MappedRoutes(W.Factory.Services);
    }

    /// <summary>A response reduced to what an attacker can observe, with the request's identifiers and the trace ID masked.</summary>
    private sealed record Answer(HttpStatusCode Status, string? MediaType, string Text, string Normalized)
    {
        public bool IsJson => MediaType is not null && MediaType.Contains("json", StringComparison.Ordinal);

        public bool IsText => IsJson || MediaType?.StartsWith("text/", StringComparison.Ordinal) == true;

        /// <summary>The PEP-1 / gateway "does not exist" problem.</summary>
        public bool IsGenericNotFound => Status == HttpStatusCode.NotFound && Text.Contains(NotFoundDetail, StringComparison.Ordinal);

        public bool SameAs(Answer other) => Status == other.Status && MediaType == other.MediaType && Normalized == other.Normalized;

        public static async Task<Answer> ReadAsync(HttpResponseMessage response, WorkspaceResources? target)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
            var text = mediaType is not null && (mediaType.Contains("json", StringComparison.Ordinal) || mediaType.StartsWith("text/", StringComparison.Ordinal))
                ? Encoding.UTF8.GetString(bytes)
                : Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 64)));
            var normalized = text;
            if (mediaType?.Contains("json", StringComparison.Ordinal) == true && JsonNode.Parse(text) is JsonObject json)
            {
                json.Remove("traceId");
                normalized = json.ToJsonString();
            }

            foreach (var id in target?.IdentifierSpellings() ?? [])
            {
                normalized = normalized.Replace(id, "<id>", StringComparison.OrdinalIgnoreCase);
            }

            return new Answer(response.StatusCode, mediaType, text, normalized);
        }
    }
}
