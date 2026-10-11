using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Audit;
using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Authorization;
using Opportunity.Application.Keys;
using Opportunity.Core.Security;
using Opportunity.Data.Audit;
using Opportunity.Hosting.Operations;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>
/// E14-T06 through the real API host, PDP, audit store and hash chain: the ethical-wall proof lists every audited attempt
/// by a walled user (directly or through an IdP group) on walled content with its outcome and says whether the wall held;
/// the access review lists users and groups with roles, walls and last access; both are generated only with
/// <c>Compliance.Report</c> and audited. The audit export carries signed checkpoints and verifies offline with
/// <c>audit verify-export</c>, which reports any change to the file.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ComplianceReportApiTests(MigrationPostgresFixture postgres)
{
    private const string Group = "Opposing Counsel Team";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_wall_proof_shows_walled_attempts_by_outcome_and_the_access_review_lists_roles_walls_and_last_access()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var auditor = await User(db, "Avery Auditor");
        var walled = await User(db, "Wally Walled");
        var grouped = await User(db, "Gray Grouped", [Group]);
        var reviewer = await User(db, "Riley Reviewer");
        await db.AssignAsync(ws, WorkspaceRole.Auditor, auditor);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, walled);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        await db.AssignGroupAsync(ws, WorkspaceRole.Reviewer, Group);
        var walledDocument = await db.DocumentAsync(ws);
        var openDocument = await db.DocumentAsync(ws);
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var wall = await db.WallAsync(ws, [walled], [Group], [walledDocument]);
        var wallName = await db.Core.ScalarAsync<string>("SELECT name FROM opportunity.ethical_wall WHERE wall_id = @w", ("w", wall));

        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}";

        // Real attempts: the walls hold (404 like a missing document); attempts outside the walls are not part of the proof.
        (await SendAsync(client, $"{root}/documents/{walledDocument}", walled)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(client, $"{root}/documents/{walledDocument}/coding", grouped, Group)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(client, $"{root}/documents/{openDocument}", walled)).StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        (await SendAsync(client, $"{root}/documents/{walledDocument}", reviewer)).StatusCode.Should().NotBe(HttpStatusCode.NotFound);

        // What a failed wall and an emergency access would leave in the trail, and an access from before the user was walled.
        var writer = new PostgresAuditEventWriter(db.Core.AppDataSource);
        var breach = DocumentEvent(ws, walled, walledDocument, AuditTaxonomy.Document.Viewed, AuditOutcome.Success);
        await writer.WriteAsync(breach, Ct);
        await writer.WriteAsync(DocumentEvent(ws, grouped, walledDocument, AuditTaxonomy.Document.Retrieved, AuditOutcome.Success) with
        {
            AccessPath = AuditAccessPath.BreakGlass,
        }, Ct);
        await writer.WriteAsync(DocumentEvent(ws, walled, walledDocument, AuditTaxonomy.Document.Viewed, AuditOutcome.Success) with
        {
            OccurredAt = startedAt.AddMinutes(-5),
        }, Ct);

        var deniedInTrail = await db.Core.ScalarAsync<long>(
            """
            SELECT count(*) FROM audit.audit_event
            WHERE workspace_id = @ws AND resource_type = 'Document' AND resource_id = @doc AND outcome = 'Denied' AND actor_id = ANY (@actors)
            """,
            ("ws", ws), ("doc", walledDocument.ToString()), ("actors", new[] { walled.ToString(), grouped.ToString() }));
        deniedInTrail.Should().BeGreaterThanOrEqualTo(2, "both refused attempts are audited");

        // Only Compliance.Report generates the reports; non-members get the usual 404.
        await (await SendAsync(client, root + "/compliance/wall-proof", reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, root + "/compliance/access-review/export", reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, root + "/compliance/wall-proof", await db.CreateUserAsync())).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await SendAsync(client, $"{root}/compliance/wall-proof?from={Iso(startedAt)}&to={Iso(startedAt)}", auditor))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        using (var proof = await JsonAsync(await SendAsync(client, root + "/compliance/wall-proof", auditor)))
        {
            var r = proof.RootElement;
            r.GetProperty("held").GetBoolean().Should().BeFalse("a walled user was served walled content");
            var counts = r.GetProperty("counts");
            counts.GetProperty("denied").GetInt64().Should().Be(deniedInTrail);
            counts.GetProperty("allowed").GetInt64().Should().Be(1, "the access from before the user joined the wall does not count");
            counts.GetProperty("breakGlass").GetInt64().Should().Be(1);
            counts.GetProperty("attempts").GetInt64().Should().Be(deniedInTrail + 2);
            var attempts = r.GetProperty("attempts").EnumerateArray().ToList();
            attempts.Should().HaveCount((int)(deniedInTrail + 2));
            attempts[0].GetProperty("result").GetString().Should().Be("Allowed", "breaches are listed first");
            attempts[0].GetProperty("eventId").GetGuid().Should().Be(breach.EventId);
            attempts.Should().OnlyContain(a => a.GetProperty("documentId").GetGuid() == walledDocument);
            attempts.Should().OnlyContain(a => a.GetProperty("wallIds").EnumerateArray().Single().GetGuid() == wall);
            attempts.Select(a => a.GetProperty("userId").GetGuid()).Distinct().Should().BeEquivalentTo([walled, grouped]);
            var walls = r.GetProperty("walls").EnumerateArray().ToList();
            walls.Should().ContainSingle();
            walls[0].GetProperty("documentsCovered").GetInt64().Should().Be(1);
            walls[0].GetProperty("members").GetArrayLength().Should().Be(2);
        }

        // A period before any attempt: nothing to show, the walls held.
        using (var quiet = await JsonAsync(await SendAsync(client,
                   $"{root}/compliance/wall-proof?from={Iso(startedAt.AddDays(-2))}&to={Iso(startedAt.AddDays(-1))}", auditor)))
        {
            quiet.RootElement.GetProperty("held").GetBoolean().Should().BeTrue();
            quiet.RootElement.GetProperty("counts").GetProperty("attempts").GetInt64().Should().Be(0);
        }

        using (var csv = await SendAsync(client, root + "/compliance/wall-proof/export", auditor))
        {
            csv.StatusCode.Should().Be(HttpStatusCode.OK);
            csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
            csv.Content.Headers.ContentDisposition!.FileName.Should().StartWith("ethical-wall-proof-");
            var text = Encoding.UTF8.GetString((await csv.Content.ReadAsByteArrayAsync(Ct)).AsSpan(3));
            text.Should().Contain("Result,Not held: walled content was served to a walled user");
            text.Should().Contain($",Allowed,{walled},").And.Contain($",BreakGlass,{grouped},").And.Contain(wallName);
            text.Split("\r\n").Count(l => l.Contains($",{walledDocument},", StringComparison.Ordinal)).Should().Be((int)(deniedInTrail + 2));
        }

        (await db.Core.ColumnAsync(
            $"""
            SELECT concat_ws('|', actor_id, details->>'format', details->>'allowed', details->>'result')
            FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'WallProofGenerated' ORDER BY occurred_at
            """)).Should().Equal($"{auditor}|Json|1|NotHeld", $"{auditor}|Json|0|Held", $"{auditor}|Csv|1|NotHeld");

        // Access review: direct and group roles, walls with their source, last workspace access and sign-in.
        using (var review = await JsonAsync(await SendAsync(client, root + "/compliance/access-review", auditor)))
        {
            var entries = review.RootElement.GetProperty("entries").EnumerateArray().ToList();
            review.RootElement.GetProperty("users").GetInt32().Should().Be(4);
            review.RootElement.GetProperty("groups").GetInt32().Should().Be(1);
            var w = entries.Single(e => e.GetProperty("userId").ValueKind != JsonValueKind.Null && e.GetProperty("userId").GetGuid() == walled);
            w.GetProperty("displayName").GetString().Should().Be("Wally Walled");
            w.GetProperty("roles").EnumerateArray().Single().GetProperty("role").GetString().Should().Be("Reviewer");
            w.GetProperty("walls").EnumerateArray().Single().GetProperty("viaGroup").ValueKind.Should().Be(JsonValueKind.Null);
            w.GetProperty("lastAccessAt").GetDateTimeOffset().Should().BeAfter(startedAt);
            w.GetProperty("lastSignInAt").ValueKind.Should().NotBe(JsonValueKind.Null);
            var g = entries.Single(e => e.GetProperty("userId").ValueKind != JsonValueKind.Null && e.GetProperty("userId").GetGuid() == grouped);
            g.GetProperty("roles").EnumerateArray().Single().GetProperty("viaGroup").GetString().Should().Be(Group);
            g.GetProperty("walls").EnumerateArray().Single().GetProperty("viaGroup").GetString().Should().Be(Group);
            var group = entries.Single(e => e.GetProperty("principal").GetString() == "Group");
            group.GetProperty("group").GetString().Should().Be(Group);
            group.GetProperty("knownMembers").GetInt32().Should().Be(1);
            group.GetProperty("walls").EnumerateArray().Single().GetProperty("wallId").GetGuid().Should().Be(wall);
            var a = entries.Single(e => e.GetProperty("userId").ValueKind != JsonValueKind.Null && e.GetProperty("userId").GetGuid() == auditor);
            a.GetProperty("roles").EnumerateArray().Single().GetProperty("displayName").GetString().Should().Be("Auditor (read-only)");
            a.GetProperty("walls").GetArrayLength().Should().Be(0);
        }

        using (var csv = await SendAsync(client, root + "/compliance/access-review/export", auditor))
        {
            csv.StatusCode.Should().Be(HttpStatusCode.OK);
            var lines = Encoding.UTF8.GetString((await csv.Content.ReadAsByteArrayAsync(Ct)).AsSpan(3)).Split("\r\n");
            lines.Should().Contain(l => l.StartsWith($"User,{grouped},,Gray Grouped,", StringComparison.Ordinal)
                && l.Contains($"Reviewer via group {Group}", StringComparison.Ordinal) && l.Contains($"{wallName} via group {Group}", StringComparison.Ordinal));
            lines.Should().Contain(l => l.StartsWith($"Group,,{Group},", StringComparison.Ordinal));
        }

        (await db.Core.ColumnAsync(
            $"""
            SELECT concat_ws('|', details->>'format', details->>'users', details->>'groups')
            FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'AccessReviewGenerated' ORDER BY occurred_at
            """)).Should().Equal("Json|4|1", "Csv|4|1");
    }

    [Fact]
    public async Task The_audit_export_carries_signed_checkpoints_verifies_offline_and_reports_tampering()
    {
        await using var harness = await AuditChainHarness.CreateAsync(postgres);
        var db = AuthorizationDatabase.Over(harness.Db);
        var ws = await db.Core.CreateWorkspaceAsync();
        var auditor = await db.CreateUserAsync();
        var reviewer = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Auditor, auditor);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        // Five events under checkpoint 1; five more (one with search text) under checkpoint 2; two sealed after it. Requested
        // checkpoints add their own Audit.CheckpointCreated event to the chain, so positions are read back below.
        await harness.WriteAsync(ws, 5);
        await harness.Sealer.CheckpointAsync(AuditCheckpointReason.Manual, ws, Ct);
        await Task.Delay(20, Ct);
        var middle = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await Task.Delay(5, Ct);
        await harness.WriteAsync(ws, 4);
        await harness.Writer.WriteAsync(AuditSamples.Event(ws) with
        {
            Category = AuditTaxonomy.SearchCategory,
            Action = "Executed",
            RestrictedDetails = new Dictionary<string, string?> { ["QueryText"] = "\"board minutes\" w/5 merger" },
        }, Ct);
        await harness.Sealer.CheckpointAsync(AuditCheckpointReason.Manual, ws, Ct);
        await harness.WriteAsync(ws, 2);
        await harness.Sealer.SealAsync(ws, Ct);
        var checkpoints = (await db.Core.ColumnAsync($"SELECT sequence::text FROM audit.checkpoint WHERE chain_id = '{ws}' ORDER BY sequence"))
            .Select(long.Parse).Order().ToList();
        checkpoints.Should().HaveCount(2);
        var last = await db.Core.ScalarAsync<long>("SELECT max(sequence) FROM audit.audit_event WHERE workspace_id = @ws", ("ws", ws));

        var directory = Directory.CreateTempSubdirectory("opportunity-audit-export-").FullName;
        try
        {
            var publicKey = Path.Combine(directory, "audit-checkpoint-v1.pem");
            var key = await harness.Signer.GetPublicKeyAsync(SigningKeyPurposes.AuditCheckpoint, 1, Ct);
            await File.WriteAllTextAsync(publicKey, $"# {key.KeyId} ES256\n" + new string(PemEncoding.Write("PUBLIC KEY", key.SubjectPublicKeyInfo)), Ct);

            await using var factory = Factory(db, harness.KeyDirectory);
            using var client = factory.CreateClient();
            var root = $"/api/v1/workspaces/{ws}";
            await (await SendAsync(client, root + "/compliance/audit-export", reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

            // The whole chain.
            var whole = Path.Combine(directory, "whole.jsonl");
            Guid exportEvent;
            using (var response = await SendAsync(client, root + "/compliance/audit-export", auditor))
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
                response.Content.Headers.ContentType!.MediaType.Should().Be(AuditExportFormat.ContentType);
                response.Content.Headers.ContentDisposition!.FileName.Should().StartWith($"audit-export-{ws}-");
                await File.WriteAllBytesAsync(whole, await response.Content.ReadAsByteArrayAsync(Ct), Ct);
            }

            var lines = await File.ReadAllLinesAsync(whole, Ct);
            using (var manifest = JsonDocument.Parse(lines[0]))
            {
                var m = manifest.RootElement;
                m.GetProperty("record").GetString().Should().Be("manifest");
                m.GetProperty("fromSequence").GetInt64().Should().Be(1);
                m.GetProperty("toSequence").GetInt64().Should().Be(last);
                m.GetProperty("events").GetInt64().Should().Be(last);
                m.GetProperty("checkpoints").GetInt32().Should().Be(2);
                m.GetProperty("eventsAfterLastCheckpoint").GetInt64().Should().Be(last - checkpoints[1]);
                m.GetProperty("withheldEvents").GetInt64().Should().Be(0, "the Auditor may read search text");
                var verification = m.GetProperty("verification");
                verification.GetProperty("intact").GetBoolean().Should().BeTrue(verification.ToString());
                verification.GetProperty("signaturesVerified").GetInt32().Should().Be(2);
                exportEvent = m.GetProperty("auditEventId").GetGuid();
            }

            lines.Count(l => l.StartsWith("{\"record\":\"checkpoint\"", StringComparison.Ordinal)).Should().Be(2);
            lines.Should().Contain(l => l.StartsWith("{\"record\":\"publicKey\"", StringComparison.Ordinal));
            lines.Should().Contain(l => l.Contains("board minutes", StringComparison.Ordinal));

            var (code, output) = await VerifyExportAsync("--file", whole, "--public-key", publicKey);
            code.Should().Be(AuditOperationsCli.ExitSuccess, output);
            output.Should().Contain($"{ws}\tINTACT\t{last}\t1\t{last}\t");
            (code, output) = await VerifyExportAsync("--file", whole);
            code.Should().Be(AuditOperationsCli.ExitSuccess, output);
            output.Should().Contain("embedded in the export");

            (await db.Core.ColumnAsync(
                $"""
                SELECT concat_ws('|', actor_id, resource_type, details->>'fromSequence', details->>'toSequence', details->>'events', details->>'result')
                FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Audit' AND action = 'Exported' AND event_id = '{exportEvent}'
                """)).Should().Equal($"{auditor}|AuditChain|1|{last}|{last}|Intact");

            // A range: anchored by checkpoint 1, verifiable on its own.
            var range = Path.Combine(directory, "range.jsonl");
            using (var response = await SendAsync(client, $"{root}/compliance/audit-export?from={Uri.EscapeDataString(Iso(middle))}", auditor))
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
                await File.WriteAllBytesAsync(range, await response.Content.ReadAsByteArrayAsync(Ct), Ct);
            }

            using (var manifest = JsonDocument.Parse((await File.ReadAllLinesAsync(range, Ct))[0]))
            {
                manifest.RootElement.GetProperty("fromSequence").GetInt64().Should().Be(checkpoints[0] + 1);
                manifest.RootElement.GetProperty("events").GetInt64().Should().Be(last - checkpoints[0]);
                manifest.RootElement.GetProperty("checkpoints").GetInt32().Should().Be(2, "the anchor and the checkpoint over 6..10");
            }

            (code, output) = await VerifyExportAsync("--file", range, "--public-key", publicKey);
            code.Should().Be(AuditOperationsCli.ExitSuccess, output);
            output.Should().Contain($"{ws}\tINTACT\t{last - checkpoints[0]}\t{checkpoints[0] + 1}\t{last}\t");

            // Tampering with the file is reported: a changed event, a removed event, a foreign key.
            var changed = Path.Combine(directory, "changed.jsonl");
            var eventLine = Array.FindIndex(lines, l => l.Contains("\"Sequence\":\"3\"", StringComparison.Ordinal));
            await File.WriteAllLinesAsync(changed, lines.Select((l, i) => i == eventLine ? l.Replace("Rev Iewer", "Someone Else", StringComparison.Ordinal) : l), Ct);
            (code, output) = await VerifyExportAsync("--file", changed, "--public-key", publicKey);
            code.Should().Be(AuditOperationsCli.ExitBroken, output);
            output.Should().Contain("BROKEN").And.Contain("EventModified\t3");

            var removed = Path.Combine(directory, "removed.jsonl");
            await File.WriteAllLinesAsync(removed, lines.Where((_, i) => i != eventLine), Ct);
            (code, output) = await VerifyExportAsync("--file", removed, "--public-key", publicKey);
            code.Should().Be(AuditOperationsCli.ExitBroken, output);
            output.Should().Contain("EventsMissing\t3");

            var foreign = Path.Combine(directory, "foreign.pem");
            using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                await File.WriteAllTextAsync(foreign, "# audit-checkpoint-v1 ES256\n" + new string(PemEncoding.Write("PUBLIC KEY", other.ExportSubjectPublicKeyInfo())), Ct);
            }

            (code, output) = await VerifyExportAsync("--file", whole, "--public-key", foreign);
            code.Should().Be(AuditOperationsCli.ExitBroken, output);
            output.Should().Contain("CheckpointSignatureInvalid");

            (await VerifyExportAsync("--file", Path.Combine(directory, "missing.jsonl"))).Code.Should().Be(AuditOperationsCli.ExitUsage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        // The application role reads the purge records of its own chain only (V0063).
        await db.Core.InWorkspaceAsync(ws, async tx =>
        {
            await using var command = tx.Command("SELECT count(*) FROM audit.chain_gap");
            ((long)(await command.ExecuteScalarAsync(Ct))!).Should().Be(0);
        });
    }

    private static async Task<(int Code, string Output)> VerifyExportAsync(params string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var code = await AuditOperationsCli.RunAsync(["verify-export", .. args], output, error, configuration: null, Ct);
        return (code, output + error.ToString());
    }

    private static async Task<Guid> User(AuthorizationDatabase db, string name, string[]? groups = null)
    {
        var id = await db.CreateUserAsync(groups: groups);
        await db.Core.ExecuteAsync("UPDATE opportunity.app_user SET display_name = @name WHERE user_id = @id", ("name", name), ("id", id));
        return id;
    }

    private static AuditEvent DocumentEvent(Guid ws, Guid user, Guid document, string action, AuditOutcome outcome) => new()
    {
        WorkspaceId = ws,
        OccurredAt = DateTimeOffset.UtcNow,
        Category = AuditTaxonomy.Document.Category,
        Action = action,
        ActorType = AuditActorType.User,
        ActorId = user.ToString(),
        ActorDisplay = "Synthetic",
        ResourceType = "Document",
        ResourceId = document.ToString(),
        Outcome = outcome,
        CorrelationId = "corr-" + Guid.CreateVersion7().ToString("N"),
    };

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            return JsonDocument.Parse(body);
        }
    }

    private static WebApplicationFactory<Program> Factory(AuthorizationDatabase db, string? keyDirectory = null) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            if (keyDirectory is not null)
            {
                builder.UseSetting("KeyManagement:Local:KeyDirectory", keyDirectory);
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
            });
        });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string url, Guid user, string? groups = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (groups is not null)
        {
            request.Headers.Add(TestAuthentication.GroupsHeader, groups);
        }

        return await client.SendAsync(request, Ct);
    }
}
