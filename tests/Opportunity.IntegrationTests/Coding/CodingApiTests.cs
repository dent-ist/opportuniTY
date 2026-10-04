using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Opportunity.Application.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Content;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// E10-T01 through the real API host on PostgreSQL: reading and saving a document's coding with If-Match / ETag on its
/// DocumentVersion, Idempotency-Key replays, 412 conflicts with enough detail to reconcile, permissions, hidden
/// documents, value and layout validation, audit, and the §24 security path with no index worker running.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class CodingApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Read_then_save_returns_the_new_version_and_indexing_state_and_a_retry_with_the_same_key_writes_nothing()
    {
        await using var core = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(core);
        var reviewer = await MemberAsync(core, w.Id, WorkspaceRole.Reviewer, "Riley Reviewer");
        var doc = await DocumentAsync(core, w.Id);
        await using var factory = Factory(core.AppConnectionString);
        using var client = factory.CreateClient();
        var url = CodingUrl(w.Id, doc);

        using (var read = await GetAsync(client, url, reviewer))
        {
            read.StatusCode.Should().Be(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(Ct));
            ETag(read).Should().Be("\"1\"");
            var coding = await JsonAsync(read);
            coding.GetProperty("documentVersion").GetInt64().Should().Be(1);
            coding.GetProperty("projectedVersion").GetInt64().Should().Be(1);
            coding.GetProperty("indexingState").GetString().Should().Be("indexed");
            coding.GetProperty("lastEditor").ValueKind.Should().Be(JsonValueKind.Null);
            FieldOf(coding, w.Responsive).GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
            FieldOf(coding, w.Responsive).GetProperty("editable").GetBoolean().Should().BeTrue();
            FieldOf(coding, w.Confidentiality).GetProperty("editable").GetBoolean().Should().BeFalse("reviewers lack Coding.WritePrivilege");
            FieldOf(coding, w.Confidentiality).GetProperty("isSecurityAffecting").GetBoolean().Should().BeTrue();
        }

        var body = Save((w.Responsive, "set", true), (w.Notes, "set", "Discusses the supply contract."), (w.Issues, "addChoices", new JsonArray(w.IssueA)));
        JsonElement saved;
        using (var save = await PutAsync(client, url, reviewer, body, "\"1\"", "save-1"))
        {
            save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync(Ct));
            ETag(save).Should().Be("\"2\"");
            saved = await JsonAsync(save);
        }

        saved.GetProperty("documentVersion").GetInt64().Should().Be(2);
        saved.GetProperty("indexingState").GetString().Should().Be("pending", "no index worker runs in this test");
        saved.GetProperty("projectedVersion").GetInt64().Should().Be(1);
        FieldOf(saved, w.Responsive).GetProperty("value").GetBoolean().Should().BeTrue();
        FieldOf(saved, w.Issues).GetProperty("value").EnumerateArray().Select(v => v.GetInt32()).Should().Equal(w.IssueA);
        FieldOf(saved, w.Notes).GetProperty("changedAtVersion").GetInt64().Should().Be(2);
        var editor = saved.GetProperty("lastEditor");
        editor.GetProperty("userId").GetGuid().Should().Be(reviewer);
        editor.GetProperty("displayName").GetString().Should().Be("Riley Reviewer");
        editor.GetProperty("documentVersion").GetInt64().Should().Be(2);

        // One transaction wrote the coding, one interactive-lane outbox row and one Coding.Changed audit event.
        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND document_id = @doc AND document_version = 2 AND lane = 2 AND status = 1",
            ("ws", w.Id), ("doc", doc))).Should().Be(1);
        (await CodingEventsAsync(core, w.Id, doc)).Should().Be(3);
        (await core.ColumnAsync(
            $"SELECT action || ':' || resource_id || ':' || actor_id || ':' || (details->>'Fields') FROM audit.audit_event WHERE workspace_id = '{w.Id}' AND category = 'Coding'"))
            .Should().Equal($"Changed:{doc}:{reviewer}:{string.Join(',', new[] { w.Responsive, w.Issues, w.Notes }.Order())}");

        // The client retries (e.g. the response was lost): same key, same request → the original result, nothing written.
        using (var retry = await PutAsync(client, url, reviewer, body, "\"1\"", "save-1"))
        {
            retry.StatusCode.Should().Be(HttpStatusCode.OK, await retry.Content.ReadAsStringAsync(Ct));
            retry.Headers.Single("Idempotent-Replayed").Should().Be("true");
            ETag(retry).Should().Be("\"2\"");
            (await JsonAsync(retry)).GetProperty("documentVersion").GetInt64().Should().Be(2);
        }

        (await CodingEventsAsync(core, w.Id, doc)).Should().Be(3);
        (await core.ScalarAsync<long>($"SELECT count(*) FROM audit.audit_event WHERE workspace_id = '{w.Id}' AND category = 'Coding'")).Should().Be(1);

        await (await PutAsync(client, url, reviewer, Set(w.Responsive, false), "\"2\"", "save-1"))
            .ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency-key-reuse");
        await (await PutAsync(client, url, reviewer, Set(w.Responsive, false), "\"2\"", "bad key"))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "idempotency-key-invalid");

        // Without a key every save is its own write; an unchanged value bumps nothing.
        using (var same = await PutAsync(client, url, reviewer, Set(w.Responsive, true), "\"2\""))
        {
            same.StatusCode.Should().Be(HttpStatusCode.OK);
            ETag(same).Should().Be("\"2\"");
        }
    }

    [Fact]
    public async Task A_stale_if_match_returns_412_with_the_current_version_last_editor_and_values_and_writes_nothing()
    {
        await using var core = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(core);
        var alice = await MemberAsync(core, w.Id, WorkspaceRole.Reviewer, "Alice Example");
        var bob = await MemberAsync(core, w.Id, WorkspaceRole.Reviewer, "Bob Example");
        var doc = await DocumentAsync(core, w.Id);
        await using var factory = Factory(core.AppConnectionString);
        using var client = factory.CreateClient();
        var url = CodingUrl(w.Id, doc);

        (await PutAsync(client, url, alice, Set(w.Responsive, true), "\"1\"")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var stale = await PutAsync(client, url, bob, Set(w.Responsive, false), "\"1\"");
        var problem = await stale.ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");
        ETag(stale).Should().Be("\"2\"", "the client can retry against the current version");
        problem.GetProperty("currentVersion").GetInt64().Should().Be(2);
        problem.GetProperty("lastEditor").GetProperty("userId").GetGuid().Should().Be(alice);
        problem.GetProperty("lastEditor").GetProperty("displayName").GetString().Should().Be("Alice Example");
        var current = problem.GetProperty("current");
        current.GetProperty("documentVersion").GetInt64().Should().Be(2);
        FieldOf(current, w.Responsive).GetProperty("value").GetBoolean().Should().BeTrue();
        (await VersionAsync(core, w.Id, doc)).Should().Be(2);
        (await CodingEventsAsync(core, w.Id, doc)).Should().Be(1);

        await (await PutAsync(client, url, bob, Set(w.Responsive, false), null))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
        await (await PutAsync(client, url, bob, Set(w.Responsive, false), "W/\"2\""))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");
        await (await PutAsync(client, url, bob, Set(w.Responsive, false), "\"not-a-version\""))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");

        // Reconciled: bob saves against the version the 412 named. "*" saves regardless of the version.
        using (var reconciled = await PutAsync(client, url, bob, Set(w.Responsive, false), ETag(stale)))
        {
            reconciled.StatusCode.Should().Be(HttpStatusCode.OK);
            ETag(reconciled).Should().Be("\"3\"");
        }

        using (var any = await PutAsync(client, url, alice, Set(w.Notes, "Checked."), "*"))
        {
            any.StatusCode.Should().Be(HttpStatusCode.OK);
            ETag(any).Should().Be("\"4\"");
        }
    }

    /// <summary>
    /// 100 reviewers save the same document from the same read: exactly one wins. Then 100 reviewers do
    /// read-modify-write on four shared documents, reconciling each 412 from its problem body: no update is lost.
    /// </summary>
    [Fact]
    public async Task One_hundred_concurrent_reviewers_lose_no_update()
    {
        const int Reviewers = 100;
        const int Documents = 4;
        await using var core = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(core);
        var reviewers = new List<Guid>();
        for (var i = 0; i < Reviewers; i++)
        {
            reviewers.Add(await MemberAsync(core, w.Id, WorkspaceRole.Reviewer));
        }

        var contested = await DocumentAsync(core, w.Id);
        var shared = new List<Guid>();
        for (var i = 0; i < Documents; i++)
        {
            shared.Add(await DocumentAsync(core, w.Id));
        }

        // One API replica's pool, below the container's max_connections.
        var pooled = new NpgsqlConnectionStringBuilder(core.AppConnectionString) { MaxPoolSize = 30 }.ConnectionString;
        await using var factory = Factory(pooled);
        using var client = factory.CreateClient();

        var statuses = await Task.WhenAll(reviewers.Select(async r =>
        {
            using var response = await PutAsync(client, CodingUrl(w.Id, contested), r, Set(w.Notes, "by " + r), "\"1\"");
            return response.StatusCode;
        }));
        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(1);
        statuses.Count(s => s == HttpStatusCode.PreconditionFailed).Should().Be(Reviewers - 1);
        (await VersionAsync(core, w.Id, contested)).Should().Be(2);
        (await CodingEventsAsync(core, w.Id, contested)).Should().Be(1);

        var attempts = new ConcurrentBag<int>();
        await Task.WhenAll(reviewers.Select(async (r, i) =>
        {
            var doc = shared[i % Documents];
            var url = CodingUrl(w.Id, doc);
            using var read = await GetAsync(client, url, r);
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            var etag = ETag(read);
            var notes = FieldOf(await JsonAsync(read), w.Notes).GetProperty("value");
            for (var attempt = 1; ; attempt++)
            {
                var text = (notes.ValueKind == JsonValueKind.String ? notes.GetString() + "|" : string.Empty) + r.ToString("N");
                using var save = await PutAsync(client, url, r, Set(w.Notes, text), etag);
                if (save.StatusCode == HttpStatusCode.OK)
                {
                    attempts.Add(attempt);
                    return;
                }

                save.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed, await save.Content.ReadAsStringAsync(Ct));
                attempt.Should().BeLessThan(Reviewers * 2);
                etag = ETag(save);
                notes = FieldOf((await JsonAsync(save)).GetProperty("current"), w.Notes).GetProperty("value");
            }
        }));

        attempts.Should().HaveCount(Reviewers);
        attempts.Max().Should().BeGreaterThan(1, "the reviewers really did collide");
        for (var d = 0; d < Documents; d++)
        {
            var mine = reviewers.Where((_, i) => i % Documents == d).Select(r => r.ToString("N")).ToList();
            (await VersionAsync(core, w.Id, shared[d])).Should().Be(1 + mine.Count);
            using var final = await GetAsync(client, CodingUrl(w.Id, shared[d]), reviewers[0]);
            var tokens = FieldOf(await JsonAsync(final), w.Notes).GetProperty("value").GetString()!.Split('|');
            tokens.Should().BeEquivalentTo(mine, "every reviewer's update survives");
        }
    }

    [Fact]
    public async Task Coding_needs_coding_write_and_security_affecting_fields_need_write_privilege()
    {
        await using var core = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(core);
        var other = await WorkspaceAsync(core);
        var reviewer = await MemberAsync(core, w.Id, WorkspaceRole.Reviewer);
        var privilege = await MemberAsync(core, w.Id, WorkspaceRole.PrivilegeReviewer);
        var manager = await MemberAsync(core, w.Id, WorkspaceRole.ProductionManager);
        var outsider = await MemberAsync(core, other.Id, WorkspaceRole.WorkspaceAdmin);
        var doc = await DocumentAsync(core, w.Id);
        await using var factory = Factory(core.AppConnectionString);
        using var client = factory.CreateClient();
        var url = CodingUrl(w.Id, doc);

        (await GetAsync(client, url, manager)).StatusCode.Should().Be(HttpStatusCode.OK, "Document.View is enough to read coding");
        await (await PutAsync(client, url, manager, Set(w.Responsive, true), "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await PutAsync(client, url, reviewer, Set(w.Confidentiality, w.Confidential), "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await PutAsync(client, url, reviewer, Save((w.Responsive, "set", true), (w.Confidentiality, "set", w.Confidential)), "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await VersionAsync(core, w.Id, doc)).Should().Be(1, "a refused save writes nothing");

        using (var allowed = await PutAsync(client, url, privilege, Set(w.Confidentiality, w.Confidential), "\"1\""))
        {
            allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync(Ct));
            FieldOf(await JsonAsync(allowed), w.Confidentiality).GetProperty("editable").GetBoolean().Should().BeTrue();
        }

        // The security-affecting change takes the security lane (L0) and is audited with old and new values.
        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND document_id = @doc AND lane = 1",
            ("ws", w.Id), ("doc", doc))).Should().Be(1);
        (await core.ColumnAsync(
            $"SELECT (details->>'SecurityAffecting') || ':' || (details->>'Field.{w.Confidentiality}.New') FROM audit.audit_event WHERE workspace_id = '{w.Id}' AND category = 'Coding'"))
            .Should().Equal($"true:{w.Confidential}");

        // Not a member, another workspace's document, unknown and malformed ids: the same 404.
        await (await GetAsync(client, url, outsider)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await PutAsync(client, url, outsider, Set(w.Responsive, true), "\"2\"")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        var foreign = await DocumentAsync(core, other.Id);
        await (await GetAsync(client, CodingUrl(w.Id, foreign), reviewer)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await PutAsync(client, CodingUrl(w.Id, foreign), reviewer, Set(w.Responsive, true), "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await GetAsync(client, CodingUrl(w.Id, Guid.NewGuid()), reviewer)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await GetAsync(client, $"/api/v1/workspaces/{w.Id}/documents/not-a-guid/coding", reviewer))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task A_hidden_document_answers_404_like_one_that_does_not_exist()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var core = db.Security.Core;
        var w = await WorkspaceAsync(core);
        var reviewer = await MemberAsync(core, w.Id, WorkspaceRole.Reviewer);
        var privilege = await MemberAsync(core, w.Id, WorkspaceRole.PrivilegeReviewer);
        var restricted = await db.Security.DocumentAsync(w.Id, "AttorneysEyesOnly");
        var walled = await db.Security.DocumentAsync(w.Id);
        await db.Security.WallAsync(w.Id, [reviewer], [], [walled]);
        await using var factory = Factory(core.AppConnectionString);
        using var client = factory.CreateClient();

        foreach (var doc in new[] { restricted, walled, Guid.NewGuid() })
        {
            var missing = await (await GetAsync(client, CodingUrl(w.Id, doc), reviewer)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
            missing.GetProperty("detail").GetString().Should().Be("The document does not exist.", "coding answers like every other document route");
            await (await PutAsync(client, CodingUrl(w.Id, doc), reviewer, Set(w.Responsive, true), "\"1\""))
                .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        }

        (await VersionAsync(core, w.Id, restricted)).Should().Be(1);
        (await GetAsync(client, CodingUrl(w.Id, restricted), privilege)).StatusCode.Should().Be(HttpStatusCode.OK, "AEO is granted to Privilege Reviewers");
        (await GetAsync(client, CodingUrl(w.Id, walled), privilege)).StatusCode.Should().Be(HttpStatusCode.OK, "the wall covers only the reviewer");
    }

    [Fact]
    public async Task Values_are_validated_per_field_type_and_choice_and_against_the_layout()
    {
        await using var core = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(core);
        var reviewer = await MemberAsync(core, w.Id, WorkspaceRole.Reviewer);
        var doc = await DocumentAsync(core, w.Id);
        var catalog = await core.Fields.GetCatalogAsync(w.Id, cancellationToken: Ct);
        var metadata = catalog.Fields.First(f => f.Storage != FieldStorage.Coding).FieldId;
        await using var factory = Factory(core.AppConnectionString);
        using var client = factory.CreateClient();
        var url = CodingUrl(w.Id, doc);

        async Task Rejected(JsonObject body, string field, string code)
        {
            var problem = await (await PutAsync(client, url, reviewer, body, "*")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
            problem.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue(problem.ToString());
            problem.GetProperty("fieldErrors").EnumerateArray()
                .Should().Contain(e => e.GetProperty("field").GetString() == field && e.GetProperty("code").GetString() == code, problem.ToString());
        }

        await Rejected(Save((w.Issues, "addChoices", new JsonArray(w.IssueRetired))), FieldKey.For(w.Issues), "inactive-choice");
        await Rejected(Set(w.Issues, new JsonArray(w.IssueA, w.IssueRetired)), FieldKey.For(w.Issues), "inactive-choice");
        await Rejected(Save((w.Responsive, "addChoices", new JsonArray(w.IssueA))), FieldKey.For(w.Responsive), "not-multi-choice");
        await Rejected(Set(w.ReviewDate, "2024-05-01T10:00:00Z"), FieldKey.For(w.ReviewDate), "invalid-date");
        await Rejected(Set(w.ReviewDate, "2024-02-30"), FieldKey.For(w.ReviewDate), "invalid-date");
        await Rejected(Set(w.Confidentiality, 999_999), FieldKey.For(w.Confidentiality), "unknown-choice");
        await Rejected(Set(99_999, true), FieldKey.For(99_999), "unknown-field");
        await Rejected(Set(metadata, "x"), FieldKey.For(metadata), "not-coding-field");
        await (await PutAsync(client, url, reviewer, Save((w.Responsive, "set", true), (w.Responsive, "set", false)), "*"))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await PutAsync(client, url, reviewer, """{"changes":[]}""", "*")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        (await VersionAsync(core, w.Id, doc)).Should().Be(1, "nothing invalid was written");

        // Multiple choice: add and remove are state-based; dates at day precision; set null clears.
        using (var multi = await PutAsync(client, url, reviewer,
                   Save((w.Issues, "addChoices", new JsonArray(w.IssueA, w.IssueB)), (w.ReviewDate, "set", "2024-05-01")), "\"1\""))
        {
            multi.StatusCode.Should().Be(HttpStatusCode.OK, await multi.Content.ReadAsStringAsync(Ct));
            var coding = await JsonAsync(multi);
            FieldOf(coding, w.Issues).GetProperty("value").EnumerateArray().Select(v => v.GetInt32()).Should().BeEquivalentTo([w.IssueA, w.IssueB]);
            FieldOf(coding, w.ReviewDate).GetProperty("value").GetString().Should().Be("2024-05-01");
        }

        using (var removed = await PutAsync(client, url, reviewer,
                   Save((w.Issues, "removeChoices", new JsonArray(w.IssueA, w.IssueRetired)), (w.ReviewDate, "set", null)), "\"2\""))
        {
            removed.StatusCode.Should().Be(HttpStatusCode.OK, await removed.Content.ReadAsStringAsync(Ct));
            var coding = await JsonAsync(removed);
            FieldOf(coding, w.Issues).GetProperty("value").EnumerateArray().Select(v => v.GetInt32()).Should().Equal(w.IssueB);
            FieldOf(coding, w.ReviewDate).GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
        }

        // A layout: Responsive required; Notes shown (and required) only when Responsive is true; Issues read-only.
        var layout = new CodingLayout
        {
            WorkspaceId = w.Id,
            LayoutId = Guid.CreateVersion7(),
            Name = "First pass",
            Sections =
            {
                new CodingLayoutSection
                {
                    SectionId = Guid.CreateVersion7(),
                    Title = "Responsiveness",
                    Fields =
                    {
                        new CodingLayoutField { FieldId = w.Responsive, IsRequired = true },
                        new CodingLayoutField { FieldId = w.Notes, IsRequired = true, VisibleWhen = new VisibilityCondition(w.Responsive, BooleanValue: true) },
                        new CodingLayoutField { FieldId = w.Issues, IsReadOnly = true },
                    },
                },
            },
        };
        (await core.Fields.SaveLayoutAsync(layout, Ct)).Succeeded.Should().BeTrue();
        var layoutUrl = CodingUrl(w.Id, doc, layout.LayoutId);

        using (var read = await GetAsync(client, layoutUrl, reviewer))
        {
            var coding = await JsonAsync(read);
            coding.GetProperty("layoutId").GetGuid().Should().Be(layout.LayoutId);
            coding.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("fieldId").GetInt32()).Should().Equal(w.Responsive, w.Notes, w.Issues);
            FieldOf(coding, w.Issues).GetProperty("editable").GetBoolean().Should().BeFalse();
        }

        JsonObject WithLayout(JsonObject body)
        {
            body["layoutId"] = layout.LayoutId.ToString();
            return body;
        }

        await Rejected(WithLayout(Set(w.ReviewDate, "2024-05-02")), FieldKey.For(w.ReviewDate), "not-in-layout");
        await Rejected(WithLayout(Save((w.Issues, "addChoices", new JsonArray(w.IssueA)))), FieldKey.For(w.Issues), "not-in-layout");
        await Rejected(WithLayout(Set(w.Responsive, true)), FieldKey.For(w.Notes), "required");
        using (var complete = await PutAsync(client, url, reviewer,
                   WithLayout(Save((w.Responsive, "set", true), (w.Notes, "set", "Pricing terms."))), "\"3\""))
        {
            complete.StatusCode.Should().Be(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync(Ct));
        }

        await (await GetAsync(client, CodingUrl(w.Id, doc, Guid.NewGuid()), reviewer)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
    }

    /// <summary>
    /// §24 rule 1 / ADR-015 D6.1 with no index worker: the save that sets the confidentiality designation commits the
    /// restriction class it drives in the same transaction, so the very next content request is denied by the gateway
    /// while the search work is still pending. Changing it back restores access the same way.
    /// </summary>
    [Fact]
    public async Task A_security_affecting_save_is_enforced_by_the_gateway_immediately_while_the_index_worker_is_paused()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var core = db.Security.Core;
        var ws = await core.CreateWorkspaceAsync();
        var w = await WorkspaceAsync(core, ws);
        var reviewer = await MemberAsync(core, ws, WorkspaceRole.Reviewer);
        var privilege = await MemberAsync(core, ws, WorkspaceRole.PrivilegeReviewer);
        var doc = (await db.DocumentAsync(ws)).DocumentId;
        await using var factory = Factory(core.AppConnectionString,
            b =>
            {
                b.UseSetting("ObjectStorage:Provider", "FileSystem");
                b.UseSetting("ObjectStorage:FileSystem:RootPath", db.StoreRoot);
            },
            s => s.AddSingleton<IRestrictionClassBinding, ConfidentialityBinding>());
        using var client = factory.CreateClient();
        var text = $"/api/v1/workspaces/{ws}/documents/{doc}/text";
        var url = CodingUrl(ws, doc);

        (await GetAsync(client, text, reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var save = await PutAsync(client, url, privilege, Set(w.Confidentiality, w.AttorneysEyesOnly), "\"1\""))
        {
            save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync(Ct));
            (await JsonAsync(save)).GetProperty("indexingState").GetString().Should().Be("pending");
        }

        (await ClassesAsync(core, ws, doc)).Should().Equal("AttorneysEyesOnly");
        (await core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND document_id = @doc AND lane = 1 AND status <> 4",
            ("ws", ws), ("doc", doc))).Should().Be(1, "the security-lane search work is still pending");

        await (await GetAsync(client, text, reviewer)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await GetAsync(client, $"/api/v1/workspaces/{ws}/documents/{doc}/pages/1/image", reviewer))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await GetAsync(client, url, reviewer)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await PutAsync(client, url, reviewer, Set(w.Responsive, true), "\"2\"")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        (await GetAsync(client, text, privilege)).StatusCode.Should().Be(HttpStatusCode.OK, "AEO is granted to Privilege Reviewers");
        (await core.ColumnAsync(
            $"SELECT details->>'RestrictionClasses.Added' FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Coding'"))
            .Should().Equal("AttorneysEyesOnly");

        // Downgraded to Confidential (granted to reviewers): the same save swaps the classes and access returns at once.
        (await PutAsync(client, url, privilege, Set(w.Confidentiality, w.Confidential), "\"2\"")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ClassesAsync(core, ws, doc)).Should().Equal("Confidential");
        (await GetAsync(client, text, reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);

        // A class applied outside the binding is not touched by coding; clearing the designation removes the bound one.
        await core.ExecuteAsync(
            "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, 'Privileged')",
            ("ws", ws), ("doc", doc));
        (await PutAsync(client, url, privilege, Set(w.Confidentiality, null), "\"3\"")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ClassesAsync(core, ws, doc)).Should().Equal("Privileged");
    }

    private static Task<long> VersionAsync(CoreSchemaDatabase core, Guid ws, Guid doc) =>
        core.ScalarAsync<long>(
            "SELECT document_version FROM opportunity.document_projection_state WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", ws), ("doc", doc));

    private static Task<long> CodingEventsAsync(CoreSchemaDatabase core, Guid ws, Guid doc) =>
        core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.coding_event WHERE workspace_id = @ws AND document_id = @doc", ("ws", ws), ("doc", doc));

    private static Task<List<string>> ClassesAsync(CoreSchemaDatabase core, Guid ws, Guid doc) =>
        core.ColumnAsync($"SELECT class_key FROM opportunity.document_restriction WHERE workspace_id = '{ws}' AND document_id = '{doc}' ORDER BY class_key");
}
