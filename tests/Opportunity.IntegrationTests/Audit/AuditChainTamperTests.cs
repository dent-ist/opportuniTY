using AwesomeAssertions;

using Opportunity.Application.Audit;
using Opportunity.Application.Audit.Chain;
using Opportunity.Hosting.Operations;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>
/// E14-T03 acceptance: <c>audit verify</c> detects modification, deletion and reordering of any event, with the tests
/// tampering with the database directly as a superuser with triggers disabled, and checkpoint signatures verify with
/// the exported public key.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class AuditChainTamperTests(MigrationPostgresFixture postgres)
{
    private const int Events = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("details", "details = '{\"N\":\"forged\"}'")]
    [InlineData("actor", "actor_id = 'someone-else'")]
    [InlineData("time", "occurred_at = occurred_at - interval '1 second'")]
    [InlineData("recorded", "recorded_at = recorded_at - interval '1 microsecond'")]
    [InlineData("outcome", "outcome = 'Denied', reason_code = 'Walled'")]
    [InlineData("client ip", "client_ip = '198.51.100.1'")]
    [InlineData("session", "session_id_hash = NULL")]
    public async Task A_modified_column_of_any_event_is_detected(string name, string assignment)
    {
        await using var h = await SealedChainAsync();
        var ws = await WorkspaceAsync(h);
        await h.TamperAsync($"UPDATE audit.audit_event SET {assignment} WHERE workspace_id = '{ws}' AND sequence = 4");

        var report = await h.VerifyAsync(ws);
        report.Intact.Should().BeFalse(name);
        report.Issues.Should().ContainSingle().Which.Should().Match<AuditChainIssue>(i => i.Kind == AuditChainIssueKind.EventModified && i.Sequence == 4);
    }

    [Fact]
    public async Task A_rehashed_modification_breaks_the_link_and_a_rewritten_chain_contradicts_the_signed_checkpoint()
    {
        await using var h = await SealedChainAsync();
        var ws = await WorkspaceAsync(h);

        // The attacker recomputes event 4's hash after editing it: event 5 no longer links to it.
        await RewriteAsync(h, ws, fromSequence: 4, edit: "UPDATE audit.audit_event SET actor_id = 'forged' WHERE workspace_id = '{0}' AND sequence = 4", relink: false);
        var link = await h.VerifyAsync(ws);
        link.Issues.Select(i => (i.Kind, i.Sequence)).Should().Equal(
            (AuditChainIssueKind.ChainLinkBroken, 5L), (AuditChainIssueKind.CheckpointMismatch, (long)Events));

        // Recomputing the whole rest of the chain hides that, but not from the signed checkpoint.
        await RewriteAsync(h, ws, fromSequence: 4, edit: null, relink: true);
        var rewritten = await h.VerifyAsync(ws);
        rewritten.Issues.Should().ContainSingle().Which.Kind.Should().Be(AuditChainIssueKind.CheckpointMismatch);
    }

    [Fact]
    public async Task A_deleted_event_anywhere_is_detected_including_the_newest()
    {
        await using var h = await SealedChainAsync();
        var ws = await WorkspaceAsync(h);
        await h.TamperAsync($"DELETE FROM audit.audit_event WHERE workspace_id = '{ws}' AND sequence = 5");
        var middle = await h.VerifyAsync(ws);
        middle.Issues.Select(i => (i.Kind, i.Sequence)).Should().Equal((AuditChainIssueKind.EventsMissing, 5L));

        await using var h2 = await SealedChainAsync();
        var ws2 = await WorkspaceAsync(h2);
        await h2.TamperAsync($"DELETE FROM audit.audit_event WHERE workspace_id = '{ws2}' AND sequence >= 9");
        var tail = await h2.VerifyAsync(ws2);
        tail.Issues.Select(i => (i.Kind, i.Sequence)).Should().Equal((AuditChainIssueKind.TailMissing, 9L));

        // Deleting the whole month (dropping the partition) instead of using the purge is detected the same way.
        await using var h3 = await SealedChainAsync();
        var ws3 = await WorkspaceAsync(h3);
        var partition = "audit_event_p" + DateTime.UtcNow.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        await h3.TamperAsync($"ALTER TABLE audit.audit_event DETACH PARTITION audit.{partition}; DROP TABLE audit.{partition}");
        (await h3.VerifyAsync(ws3)).Issues.Should().ContainSingle().Which.Kind.Should().Be(AuditChainIssueKind.TailMissing);
    }

    [Fact]
    public async Task Reordered_events_are_detected()
    {
        // Swap the chain positions of two events (their sequence and hashes move, the content stays).
        await using var h = await SealedChainAsync();
        var ws = await WorkspaceAsync(h);
        await h.TamperAsync(
            $"""
            WITH a AS (SELECT event_id, sequence, prev_hash, event_hash FROM audit.audit_event WHERE workspace_id = '{ws}' AND sequence = 3),
                 b AS (SELECT event_id, sequence, prev_hash, event_hash FROM audit.audit_event WHERE workspace_id = '{ws}' AND sequence = 6)
            UPDATE audit.audit_event e
            SET sequence = CASE WHEN e.event_id = a.event_id THEN b.sequence ELSE a.sequence END,
                prev_hash = CASE WHEN e.event_id = a.event_id THEN b.prev_hash ELSE a.prev_hash END,
                event_hash = CASE WHEN e.event_id = a.event_id THEN b.event_hash ELSE a.event_hash END
            FROM a, b WHERE e.event_id IN (a.event_id, b.event_id)
            """);
        var swapped = await h.VerifyAsync(ws);
        swapped.Issues.Select(i => (i.Kind, i.Sequence)).Should().Equal((AuditChainIssueKind.EventModified, 3L), (AuditChainIssueKind.EventModified, 6L));

        // Swap the content but keep the positions (timestamps exchanged): each event's hash no longer matches.
        await using var h2 = await SealedChainAsync();
        var ws2 = await WorkspaceAsync(h2);
        await h2.TamperAsync(
            $"""
            WITH a AS (SELECT event_id, recorded_at FROM audit.audit_event WHERE workspace_id = '{ws2}' AND sequence = 2),
                 b AS (SELECT event_id, recorded_at FROM audit.audit_event WHERE workspace_id = '{ws2}' AND sequence = 7)
            UPDATE audit.audit_event e
            SET recorded_at = CASE WHEN e.event_id = a.event_id THEN b.recorded_at ELSE a.recorded_at END
            FROM a, b WHERE e.event_id IN (a.event_id, b.event_id)
            """);
        (await h2.VerifyAsync(ws2)).Issues.Select(i => i.Sequence).Should().Equal(2L, 7L);

        // Two events claiming one position.
        await using var h3 = await SealedChainAsync();
        var ws3 = await WorkspaceAsync(h3);
        await h3.TamperAsync($"UPDATE audit.audit_event SET sequence = 8 WHERE workspace_id = '{ws3}' AND sequence = 9");
        (await h3.VerifyAsync(ws3)).Issues.Select(i => i.Kind).Should().Contain(AuditChainIssueKind.DuplicateSequence)
            .And.Contain(AuditChainIssueKind.EventsMissing);
    }

    [Fact]
    public async Task Checkpoint_tampering_and_the_wrong_key_are_detected()
    {
        await using var h = await SealedChainAsync();
        var ws = await WorkspaceAsync(h);
        await h.WriteAsync(ws, 2);
        await h.Sealer.CheckpointAsync(AuditCheckpointReason.Manual, ws, Ct);
        (await h.VerifyAsync(ws)).Checkpoints.Should().Be(2);

        await h.TamperAsync($"UPDATE audit.checkpoint SET reason = 'Scheduled' WHERE chain_id = '{ws}' AND sequence = {Events}");
        (await h.VerifyAsync(ws)).Issues.Should().ContainSingle().Which.Should().Match<AuditChainIssue>(
            i => i.Kind == AuditChainIssueKind.CheckpointSignatureInvalid && i.Sequence == Events);

        await h.TamperAsync($"DELETE FROM audit.checkpoint WHERE chain_id = '{ws}' AND sequence = {Events}");
        (await h.VerifyAsync(ws)).Issues.Should().ContainSingle().Which.Kind.Should().Be(AuditChainIssueKind.CheckpointMissing);

        await using var other = await AuditChainHarness.CreateAsync(postgres);
        await other.Signer.SignAsync(Application.Keys.SigningKeyPurposes.AuditCheckpoint, new byte[1], Ct);
        var wrongKey = await h.Verifier.VerifyAsync(ws, other.ProviderKeys, Ct);
        wrongKey.Issues.Select(i => i.Kind).Should().Contain(AuditChainIssueKind.CheckpointSignatureInvalid);
        var noKey = await h.Verifier.VerifyAsync(ws, new PemCheckpointKeys([]), Ct);
        noKey.Issues.Select(i => i.Kind).Should().OnlyContain(k => k == AuditChainIssueKind.CheckpointKeyUnavailable || k == AuditChainIssueKind.CheckpointMissing);
    }

    [Fact]
    public async Task No_checkpoint_is_signed_over_a_broken_chain_and_other_chains_keep_theirs()
    {
        await using var h = await SealedChainAsync();
        var broken = await WorkspaceAsync(h);
        var healthy = await h.Db.CreateWorkspaceAsync();
        await h.TamperAsync($"DELETE FROM audit.audit_event WHERE workspace_id = '{broken}' AND sequence = 11");
        await h.WriteAsync(broken, 1);
        await h.WriteAsync(healthy, 2);

        var created = await h.Sealer.CheckpointAsync(AuditCheckpointReason.Scheduled, cancellationToken: Ct);
        created.Select(c => c.ChainId).Should().Equal(healthy);
        var direct = () => h.Sealer.CheckpointAsync(AuditCheckpointReason.Manual, broken, Ct);
        (await direct.Should().ThrowAsync<Data.Audit.AuditChainBrokenException>()).Which.ChainId.Should().Be(broken);
        (await h.VerifyAsync(broken)).Issues.Select(i => i.Kind).Should().Contain(AuditChainIssueKind.EventsMissing);
    }

    [Fact]
    public async Task A_forged_purge_record_does_not_excuse_a_deletion()
    {
        await using var h = await SealedChainAsync();
        var ws = await WorkspaceAsync(h);
        await h.TamperAsync(
            $"""
            INSERT INTO audit.chain_gap (chain_id, first_sequence, last_sequence, last_event_hash, partition_name)
            SELECT '{ws}', 5, 5, event_hash, 'audit_event_p201001' FROM audit.audit_event WHERE workspace_id = '{ws}' AND sequence = 5;
            DELETE FROM audit.audit_event WHERE workspace_id = '{ws}' AND sequence = 5;
            """);
        var report = await h.VerifyAsync(ws);
        report.Issues.Select(i => i.Kind).Should().Equal(AuditChainIssueKind.PurgeRecordUnverified, AuditChainIssueKind.EventsMissing);
    }

    [Fact]
    public async Task The_cli_verifies_with_the_exported_public_key_exports_checkpoints_and_records_the_outcome()
    {
        await using var h = await SealedChainAsync();
        var ws = await WorkspaceAsync(h);
        var config = h.Configuration();

        // seal --checkpoint (what the retention job runs before a purge)
        await h.WriteAsync(ws, 1);
        var sealOutput = new StringWriter();
        (await AuditOperationsCli.RunAsync(["seal", "--checkpoint", "--reason", "before-purge", "--operator", "alice"], sealOutput, TextWriter.Null, config, Ct))
            .Should().Be(AuditOperationsCli.ExitSuccess);
        sealOutput.ToString().Should().Contain($"checkpoint\t{ws}\t{Events + 2}");

        // keys public-key, written to a file the way an exhibit would carry it.
        var pemOutput = new StringWriter();
        (await KeyOperationsCli.RunAsync(["public-key", "--purpose", "audit-checkpoint"], pemOutput, TextWriter.Null, config, Ct)).Should().Be(0);
        var pemFile = Path.Combine(h.KeyDirectory, "audit-checkpoint-v1.pem");
        await File.WriteAllTextAsync(pemFile, pemOutput.ToString(), Ct);

        var verifyOutput = new StringWriter();
        (await AuditOperationsCli.RunAsync(["verify", "--public-key", pemFile, "--operator", "alice"], verifyOutput, TextWriter.Null, config, Ct))
            .Should().Be(AuditOperationsCli.ExitSuccess, verifyOutput.ToString());
        verifyOutput.ToString().Should().Contain($"{ws}\tINTACT\t{Events + 2}\t{Events + 2}").And.Contain("intact, 0 broken");
        var verified = (await AuditSamples.ReadAllAsync(h.Db, ws)).Where(e => e.Event.Action == AuditTaxonomy.Audit.Verified).Should().ContainSingle().Subject;
        verified.Event.Outcome.Should().Be(AuditOutcome.Success);
        verified.Event.ActorDisplay.Should().Be("Operations CLI (alice)");

        var csv = new StringWriter();
        (await AuditOperationsCli.RunAsync(["checkpoints", "--workspace", ws.ToString()], csv, TextWriter.Null, config, Ct)).Should().Be(0);
        csv.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(3, "a header and two checkpoints");
        csv.ToString().Should().Contain(",BeforePurge,").And.Contain("audit-checkpoint-v1,ES256");
        var json = new StringWriter();
        (await AuditOperationsCli.RunAsync(["checkpoints", "--workspace", ws.ToString(), "--format", "json"], json, TextWriter.Null, config, Ct)).Should().Be(0);
        foreach (var line in json.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            document.RootElement.GetProperty("payload").GetProperty("Format").GetString().Should().Be(AuditChainFormat.CheckpointFormat);
        }

        // Tamper, then the CLI fails the chain and records Integrity.ChainBroken.
        await h.TamperAsync($"UPDATE audit.audit_event SET actor_display = 'Someone Else' WHERE workspace_id = '{ws}' AND sequence = 2");
        var broken = new StringWriter();
        (await AuditOperationsCli.RunAsync(["verify", "--workspace", ws.ToString(), "--public-key", pemFile, "--operator", "alice"], broken, TextWriter.Null, config, Ct))
            .Should().Be(AuditOperationsCli.ExitBroken);
        broken.ToString().Should().Contain($"{ws}\tBROKEN").And.Contain("EventModified\t2\t");
        var events = await AuditSamples.ReadAllAsync(h.Db, ws);
        events.Should().ContainSingle(e => e.Event.Action == AuditTaxonomy.Integrity.ChainBroken && e.Event.ReasonCode == nameof(AuditChainIssueKind.EventModified));
        events.Count(e => e.Event.Action == AuditTaxonomy.Audit.Verified).Should().Be(2);

        // Usage errors.
        (await AuditOperationsCli.RunAsync(["verify"], TextWriter.Null, TextWriter.Null, config, Ct)).Should().Be(AuditOperationsCli.ExitUsage, "--operator is required");
        (await AuditOperationsCli.RunAsync(["checkpoints"], TextWriter.Null, TextWriter.Null, config, Ct)).Should().Be(AuditOperationsCli.ExitUsage);
        (await AuditOperationsCli.RunAsync(["verify", "--operator", "a", "--public-key", "/nonexistent.pem"], TextWriter.Null, TextWriter.Null, config, Ct))
            .Should().Be(AuditOperationsCli.ExitUsage);
    }

    /// <summary>A workspace chain of <see cref="Events"/> sealed events with one signed checkpoint at its head.</summary>
    private async Task<AuditChainHarness> SealedChainAsync()
    {
        var h = await AuditChainHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.WriteAsync(ws, Events);
        (await h.Sealer.CheckpointAsync(AuditCheckpointReason.Manual, ws, Ct)).Should().ContainSingle().Which.Sequence.Should().Be(Events);
        await h.Sealer.SealAsync(ws, Ct); // the CheckpointCreated event, after the checkpoint
        var report = await h.VerifyAsync(ws);
        report.Intact.Should().BeTrue(string.Join("; ", report.Issues.Select(i => i.Message)));
        return h;
    }

    private static async Task<Guid> WorkspaceAsync(AuditChainHarness h) =>
        Guid.Parse((await h.Db.ColumnAsync("SELECT chain_id::text FROM audit.chain_head WHERE workspace_id IS NOT NULL")).Single());

    /// <summary>
    /// The informed attacker: optionally edits, then recomputes the hash of every event from
    /// <paramref name="fromSequence"/> (and with <paramref name="relink"/> the PrevHash links too), so the chain alone
    /// looks consistent.
    /// </summary>
    private static async Task RewriteAsync(AuditChainHarness h, Guid ws, long fromSequence, string? edit, bool relink)
    {
        if (edit is not null)
        {
            await h.TamperAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, edit, ws));
        }

        var rows = new List<(long Sequence, AuditChainEnvelope Envelope)>();
        await using (var command = h.SealerSource.CreateCommand(
            $"SELECT {Data.Audit.AuditChainSql.EnvelopeColumns} FROM audit.audit_event WHERE workspace_id = '{ws}' AND sequence >= {fromSequence} ORDER BY sequence"))
        {
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                var row = Data.Audit.AuditChainSql.Read(reader);
                rows.Add((row.Sequence!.Value, row.Envelope with { Sequence = row.Sequence.Value, PrevHash = row.PrevHash! }));
            }
        }

        byte[]? prev = null;
        foreach (var (sequence, envelope) in rows)
        {
            var e = relink && prev is not null ? envelope with { PrevHash = prev } : envelope;
            var hash = AuditChainFormat.EventHash(e);
            await h.TamperAsync(
                $"UPDATE audit.audit_event SET prev_hash = '\\x{AuditChainFormat.Hex(e.PrevHash)}', event_hash = '\\x{AuditChainFormat.Hex(hash)}' "
                + $"WHERE workspace_id = '{ws}' AND sequence = {sequence}");
            prev = hash;
            if (!relink)
            {
                break;
            }
        }
    }
}
