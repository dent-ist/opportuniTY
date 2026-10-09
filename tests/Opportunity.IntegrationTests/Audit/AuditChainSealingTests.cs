using System.Globalization;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Keys;
using Opportunity.Data;
using Opportunity.Data.Audit;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Workspaces;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>
/// E14-T03 / ADR-013 §3: the sealer chains every committed event once per workspace (and the system chain), stays
/// correct with concurrent writers and competing sealers, signs checkpoints, and the purge only removes what a signed
/// checkpoint covers.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class AuditChainSealingTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_workspace_and_the_system_chain_get_their_own_gapless_chain()
    {
        await using var h = await AuditChainHarness.CreateAsync(postgres);
        var a = await h.Db.CreateWorkspaceAsync();
        var b = await h.Db.CreateWorkspaceAsync();
        await h.WriteAsync(a, 5);
        await h.WriteAsync(b, 3);
        await h.WriteAsync(null, 2);

        var result = await h.Sealer.SealAsync(cancellationToken: Ct);
        result.Should().Be(new AuditSealResult(3, 10, 0));
        (await h.Sealer.SealAsync(cancellationToken: Ct)).Sealed.Should().Be(0, "every event is sealed exactly once");

        foreach (var (ws, count) in new (Guid?, int)[] { (a, 5), (b, 3), (null, 2) })
        {
            var rows = await ChainAsync(h.Db, ws);
            rows.Select(r => r.Sequence).Should().Equal(Enumerable.Range(1, count).Select(i => (long)i));
            rows[0].PrevHash.Should().Equal(new byte[32], "genesis PrevHash is 32 zero bytes");
            for (var i = 1; i < rows.Count; i++)
            {
                rows[i].PrevHash.Should().Equal(rows[i - 1].EventHash);
            }

            var report = await h.VerifyAsync(ws);
            report.Intact.Should().BeTrue(string.Join("; ", report.Issues.Select(x => x.Message)));
            report.Events.Should().Be(count);
            report.LastEventHash.Should().Equal(rows[^1].EventHash);
        }

        // The app role still reads its events through the store, now with the sequence set.
        (await AuditSamples.ReadAllAsync(h.Db, a)).Should().OnlyContain(e => e.Sequence != null && e.SealedAt != null);
    }

    [Fact]
    public async Task A_transaction_that_commits_late_is_chained_after_the_events_sealed_before_it()
    {
        await using var h = await AuditChainHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        var late = AuditSamples.Event(ws);

        // Inserted first (earliest recorded_at) but committed after other events were sealed.
        await using (var tx = await WorkspaceTransaction.BeginAsync(h.Db.AppDataSource, ws, Ct))
        {
            await AuditSql.InsertAsync(tx, late, Ct);
            await h.WriteAsync(ws, 3);
            (await h.Sealer.SealAsync(ws, Ct)).Sealed.Should().Be(3, "an uncommitted event is invisible to the sealer");
            await tx.CommitAsync(Ct);
        }

        (await h.Sealer.SealAsync(ws, Ct)).Sealed.Should().Be(1);
        var rows = await ChainAsync(h.Db, ws);
        rows.Should().HaveCount(4);
        rows[^1].EventId.Should().Be(late.EventId, "chain order is the order events became visible, not recorded_at");
        (await h.VerifyAsync(ws)).Intact.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_writers_and_competing_sealers_produce_one_gapless_chain_per_workspace()
    {
        await using var h = await AuditChainHarness.CreateAsync(postgres, new AuditChainOptions { BatchSize = 25 });
        var workspaces = new[] { await h.Db.CreateWorkspaceAsync(), await h.Db.CreateWorkspaceAsync(), await h.Db.CreateWorkspaceAsync() };
        await using var pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(h.Db.AppConnectionString) { MaxPoolSize = 30 }.ConnectionString);
        var writer = new PostgresAuditEventWriter(pool);
        const int Writers = 24;
        const int EventsPerWriter = 25;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sealers = new[] { h.NewSealer(new AuditChainOptions { BatchSize = 25 }), h.NewSealer(new AuditChainOptions { BatchSize = 25 }), h.Sealer };
        long busy = 0;
        var sealing = sealers.Select(sealer => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var result = await sealer.SealAsync(cancellationToken: CancellationToken.None);
                Interlocked.Add(ref busy, result.Busy);
            }
        }, CancellationToken.None)).ToArray();

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < EventsPerWriter; i++)
            {
                await writer.WriteAsync(AuditSamples.Event(w % 4 == 3 ? null : workspaces[w % 3]), Ct);
            }
        }, Ct)));
        await stop.CancelAsync();
        await Task.WhenAll(sealing);
        await h.Sealer.SealAsync(cancellationToken: Ct);

        (await h.Db.ScalarAsync<long>("SELECT count(*) FROM audit.audit_event WHERE sequence IS NULL")).Should().Be(0);
        foreach (var ws in workspaces.Cast<Guid?>().Append(null))
        {
            var rows = await ChainAsync(h.Db, ws);
            rows.Select(r => r.Sequence).Should().Equal(Enumerable.Range(1, rows.Count).Select(i => (long)i), "positions are unique and gapless");
            var report = await h.VerifyAsync(ws);
            report.Intact.Should().BeTrue(string.Join("; ", report.Issues.Select(x => x.Message)));
        }

        (await h.Db.ScalarAsync<long>("SELECT count(*) FROM audit.audit_event")).Should().Be(Writers * EventsPerWriter);
        TestContext.Current.SendDiagnosticMessage($"competing sealers skipped a busy chain {busy} times");
    }

    [Fact]
    public async Task Checkpoints_are_signed_only_when_a_chain_advanced_and_cover_contiguous_ranges()
    {
        await using var h = await AuditChainHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.WriteAsync(ws, 7);

        var first = (await h.Sealer.CheckpointAsync(AuditCheckpointReason.Scheduled, cancellationToken: Ct)).Should().ContainSingle().Subject;
        first.Data.FromSequence.Should().Be(0);
        first.Sequence.Should().Be(7);
        first.KeyId.Should().Be("audit-checkpoint-v1");
        first.Algorithm.Should().Be(KeySignature.Es256);
        (await h.Sealer.CheckpointAsync(AuditCheckpointReason.Scheduled, cancellationToken: Ct)).Should().BeEmpty("the chain did not advance");
        (await h.Db.ScalarAsync<long>($"SELECT count(*) FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'CheckpointCreated'"))
            .Should().Be(0, "scheduled checkpoints are not audited, or every checkpoint would call for the next one");

        // After a key rotation the next checkpoint uses v2; v1 keeps verifying.
        await h.Signer.RotateAsync(SigningKeyPurposes.AuditCheckpoint, Ct);
        await h.WriteAsync(ws, 2);
        var second = (await h.Sealer.CheckpointAsync(AuditCheckpointReason.Manual, ws, Ct)).Should().ContainSingle().Subject;
        second.Data.FromSequence.Should().Be(7);
        second.Sequence.Should().Be(9);
        second.KeyId.Should().Be("audit-checkpoint-v2");
        var audited = (await AuditSamples.ReadAllAsync(h.Db, ws)).Single(e => e.Event.Action == AuditTaxonomy.Audit.CheckpointCreated);
        audited.Event.Details.Should().Contain("Reason", "Manual").And.Contain("Sequence", "9").And.Contain("KeyId", "audit-checkpoint-v2");

        var report = await h.VerifyAsync(ws);
        report.Intact.Should().BeTrue(string.Join("; ", report.Issues.Select(x => x.Message)));
        report.Checkpoints.Should().Be(2);
        report.LastCheckpointSequence.Should().Be(9);
        report.EventsAfterLastCheckpoint.Should().Be(0);
        report.UnsealedEvents.Should().Be(1, "the CheckpointCreated event waits for the next sealing round");
    }

    [Fact]
    public async Task Only_the_sealer_role_moves_heads_and_appends_checkpoints_and_nobody_changes_them()
    {
        await using var h = await AuditChainHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        var other = await h.Db.CreateWorkspaceAsync();
        await h.WriteAsync(ws, 2);
        await h.WriteAsync(other, 1);
        await h.Sealer.CheckpointAsync(AuditCheckpointReason.Manual, cancellationToken: Ct);

        foreach (var sql in new[]
        {
            "UPDATE audit.chain_head SET last_sequence = 99",
            "INSERT INTO audit.chain_head (chain_id) VALUES (gen_random_uuid())",
            "DELETE FROM audit.checkpoint",
            "UPDATE audit.checkpoint SET reason = 'Manual'",
            "SELECT count(*) FROM audit.chain_gap",
            "INSERT INTO audit.chain_gap (chain_id, first_sequence, last_sequence, last_event_hash, partition_name) VALUES (gen_random_uuid(), 1, 1, sha256(''::bytea), 'x')",
        })
        {
            (await AuditSamples.FailsInWorkspaceAsync(h.Db.AppDataSource, ws, sql)).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
        }

        // The app role reads its own chain's checkpoints only (RLS).
        await using (var tx = await WorkspaceTransaction.BeginAsync(h.Db.AppDataSource, ws, Ct))
        {
            await using var command = tx.Command("SELECT count(*) FROM audit.checkpoint");
            ((long)(await command.ExecuteScalarAsync(Ct))!).Should().Be(1);
        }

        // The sealer and the owner cannot rewrite, delete or move a head backwards.
        foreach (var sql in new[]
        {
            "UPDATE audit.checkpoint SET signature = '\\x00'",
            "DELETE FROM audit.checkpoint",
            "TRUNCATE audit.checkpoint",
            "UPDATE audit.chain_head SET last_sequence = 0",
            "DELETE FROM audit.chain_head",
            "INSERT INTO audit.chain_gap (chain_id, first_sequence, last_sequence, last_event_hash, partition_name) VALUES (gen_random_uuid(), 1, 1, sha256(''::bytea), 'x')",
        })
        {
            await using var command = h.SealerSource.CreateCommand(sql);
            var act = () => command.ExecuteNonQueryAsync(Ct);
            (await act.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
            var owner = () => h.Db.ExecuteAsync(sql);
            if (!sql.StartsWith("INSERT", StringComparison.Ordinal))
            {
                (await owner.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
            }
        }
    }

    [Fact]
    public async Task The_purge_drops_only_sealed_checkpointed_months_and_records_the_gaps_it_leaves()
    {
        await using var h = await AuditChainHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.ExecuteAsync("UPDATE opportunity.workspace SET status = 'Closed', closed_at = '2012-06-30' WHERE workspace_id = @ws", ("ws", ws));
        await h.Db.ExecuteAsync(
            "CREATE TABLE audit.audit_event_p201001 PARTITION OF audit.audit_event FOR VALUES FROM ('2010-01-01 00:00:00+00') TO ('2010-02-01 00:00:00+00')");

        // Interleaved old and current events: the purge removes positions 1, 3 and 4 of the chain, not a prefix.
        await InsertAtAsync(h.Db, ws, "2010-01-10");
        await h.WriteAsync(ws, 1);
        await InsertAtAsync(h.Db, ws, "2010-01-11");
        await InsertAtAsync(h.Db, ws, "2010-01-12");
        await h.WriteAsync(ws, 2);
        await InsertAtAsync(h.Db, null, "2010-01-13");

        await using var retention = NpgsqlDataSource.Create(await CoreSchemaDatabase.CreateLoginAsync(h.Db.ConnectionString, "opportunity_audit_retention"));
        async Task<long> DropAsync()
        {
            await using var command = retention.CreateCommand("SELECT audit.drop_expired_partition('2010-01-01')");
            return (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        var unsealed = () => DropAsync();
        var refused = (await unsealed.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be(PostgresErrorCodes.ObjectNotInPrerequisiteState);
        refused.MessageText.Should().Contain("not yet sealed");

        await h.Sealer.SealAsync(cancellationToken: Ct);
        var uncheckpointed = () => DropAsync();
        (await uncheckpointed.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("no signed checkpoint covers");

        // Legal hold still wins (V0048), whatever the chain state.
        await PreservationLockTestSql.PlaceAsync(h.Db, ws);
        await h.Sealer.CheckpointAsync(AuditCheckpointReason.BeforePurge, cancellationToken: Ct);
        var held = () => DropAsync();
        (await held.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("O0423");
        await PreservationLockTestSql.ReleaseAllAsync(h.Db, ws);
        await h.Sealer.CheckpointAsync(AuditCheckpointReason.BeforePurge, cancellationToken: Ct);

        (await DropAsync()).Should().Be(4);

        var gaps = await h.Db.ColumnAsync(
            "SELECT coalesce(workspace_id::text, 'system') || ':' || first_sequence || '-' || last_sequence FROM audit.chain_gap g "
            + "LEFT JOIN LATERAL (SELECT NULLIF(g.chain_id, '00000000-0000-0000-0000-000000000000') AS workspace_id) w ON true ORDER BY 1");
        gaps.Should().BeEquivalentTo($"{ws}:1-1", $"{ws}:3-4", "system:1-1");
        var purged = (await AuditSamples.ReadAllAsync(h.Db, null)).Single(e => e.Event.Action == AuditTaxonomy.Audit.Purged);
        purged.Event.Details.Should().ContainKey("GapDigest").And.Contain("Chains", "2").And.Contain("Events", "4");

        // Every chain still verifies: the purged positions are accounted for and the survivors link to their anchors.
        await h.Sealer.CheckpointAsync(AuditCheckpointReason.Manual, cancellationToken: Ct);
        foreach (var chain in new Guid?[] { ws, null })
        {
            var report = await h.VerifyAsync(chain);
            report.Intact.Should().BeTrue(string.Join("; ", report.Issues.Select(x => x.Message)));
        }

        (await h.VerifyAsync(ws)).PurgedEvents.Should().Be(3);
    }

    [Fact]
    public async Task The_dispatcher_loop_seals_within_seconds_and_stands_down_without_a_sealer_login()
    {
        await using var h = await AuditChainHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.WriteAsync(ws, 3);

        var services = new ServiceCollection()
            .AddSingleton<ISigningKeyProvider>(h.Signer)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddAuditChainSealing(_ => NpgsqlDataSource.Create(h.SealerConnectionString),
                new AuditChainOptions { SealInterval = TimeSpan.FromMilliseconds(100), CheckpointInterval = TimeSpan.FromMilliseconds(300) })
            .BuildServiceProvider();
        await using (services)
        {
            var hosted = services.GetServices<IHostedService>().OfType<AuditChainSealerService>().Single();
            await hosted.StartAsync(Ct);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline
                   && await h.Db.ScalarAsync<long>($"SELECT count(*) FROM audit.checkpoint WHERE chain_id = '{ws}'") == 0)
            {
                await Task.Delay(100, Ct);
            }

            await hosted.StopAsync(Ct);
        }

        (await h.Db.ScalarAsync<long>($"SELECT count(*) FROM audit.audit_event WHERE workspace_id = '{ws}' AND sequence IS NULL")).Should().Be(0);
        (await h.Db.ScalarAsync<long>($"SELECT count(*) FROM audit.checkpoint WHERE chain_id = '{ws}' AND reason = 'Scheduled'")).Should().Be(1);

        var unconfigured = new ServiceCollection()
            .AddSingleton<ISigningKeyProvider>(h.Signer)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddAuditChainSealing(_ => throw new InvalidOperationException("Connection string 'AuditSealer' is not configured."))
            .BuildServiceProvider();
        await using (unconfigured)
        {
            var hosted = unconfigured.GetServices<IHostedService>().OfType<AuditChainSealerService>().Single();
            await hosted.StartAsync(Ct);
            await hosted.ExecuteTask!;
            await hosted.StopAsync(Ct);
        }
    }

    internal sealed record ChainRow(Guid EventId, long Sequence, byte[] PrevHash, byte[] EventHash);

    internal static async Task<List<ChainRow>> ChainAsync(CoreSchemaDatabase db, Guid? workspaceId)
    {
        await using var command = db.DataSource.CreateCommand(
            $"SELECT event_id, sequence, prev_hash, event_hash FROM audit.audit_event WHERE {(workspaceId is null ? "workspace_id IS NULL" : $"workspace_id = '{workspaceId}'")} "
            + "AND sequence IS NOT NULL ORDER BY sequence");
        var rows = new List<ChainRow>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new ChainRow(reader.GetGuid(0), reader.GetInt64(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3)));
        }

        return rows;
    }

    internal static Task InsertAtAsync(CoreSchemaDatabase db, Guid? workspaceId, string occurredAt) => db.ExecuteAsync(
        string.Create(CultureInfo.InvariantCulture,
            $"""
            INSERT INTO audit.audit_event (event_id, schema_version, workspace_id, occurred_at, category, action, actor_type,
                                           actor_id, actor_display, outcome, correlation_id)
            VALUES (gen_random_uuid(), 1, {(workspaceId is null ? "NULL" : $"'{workspaceId}'")}, '{occurredAt}', 'Auth', 'SignIn',
                    'User', 'u', 'U', 'Success', 'c')
            """));
}
