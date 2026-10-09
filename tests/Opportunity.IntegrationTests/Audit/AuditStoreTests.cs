using System.Globalization;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Data;
using Opportunity.Data.Audit;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>
/// E14-T01 / ADR-013 §1: the audit store is append-only for every application role, scoped by RLS to one chain per
/// transaction, partitioned ahead of time, and leaves the database only through the retention role's partition drop.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class AuditStoreTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_app_role_can_insert_and_read_but_never_update_delete_or_truncate_audit_rows()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var writer = new PostgresAuditEventWriter(db.AppDataSource);
        var written = AuditSamples.Event(ws);
        await writer.WriteAsync(written, Ct);

        foreach (var sql in new[]
        {
            "UPDATE audit.audit_event SET actor_id = 'someone-else'",
            "UPDATE audit.audit_event SET sequence = 1",
            "DELETE FROM audit.audit_event",
            "TRUNCATE audit.audit_event",
            $"DELETE FROM audit.{CurrentPartition()}",
            $"SELECT count(*) FROM audit.{CurrentPartition()}",
        })
        {
            var error = await AuditSamples.FailsInWorkspaceAsync(db.AppDataSource, ws, sql);
            error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
        }

        // Privileges only: no UPDATE/DELETE/TRUNCATE on the parent, nothing at all on any partition.
        (await db.ColumnAsync(
            """
            SELECT p.privilege || ' ' || c.relname
            FROM pg_class c CROSS JOIN unnest(ARRAY['UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS p(privilege)
            WHERE c.relnamespace = 'audit'::regnamespace AND c.relkind IN ('r', 'p')
              AND has_table_privilege('opportunity_app', c.oid, p.privilege)
            UNION ALL
            SELECT 'any ' || c.relname FROM pg_class c
            CROSS JOIN unnest(ARRAY['opportunity_app', 'opportunity_readonly', 'opportunity_audit_sealer', 'opportunity_audit_retention']) r(role)
            WHERE c.relnamespace = 'audit'::regnamespace AND c.relispartition
              AND has_table_privilege(r.role, c.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
            """)).Should().BeEmpty();
        (await db.ScalarAsync<bool>("SELECT has_schema_privilege('opportunity_readonly', 'audit', 'USAGE')"))
            .Should().BeFalse("audit holds search text (Q-16); the support role cannot read it");

        var stored = (await AuditSamples.ReadAllAsync(db, ws)).Should().ContainSingle().Subject;
        stored.Event.Should().BeEquivalentTo(written, o => o.Excluding(e => e.SessionIdHash));
        stored.Event.SessionIdHash!.Value.ToArray().Should().Equal(written.SessionIdHash!.Value.ToArray());
        stored.Sequence.Should().BeNull("the chain columns stay null until the M3 sealer (E14-T03)");
    }

    [Fact]
    public async Task Triggers_keep_rows_immutable_even_for_the_owner_and_let_the_sealer_set_chain_columns_once()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        await new PostgresAuditEventWriter(db.AppDataSource).WriteAsync(AuditSamples.Event(ws), Ct);

        // The owner (here the superuser) bypasses privileges but not the triggers.
        foreach (var sql in new[]
        {
            "UPDATE audit.audit_event SET actor_id = 'forged'",
            "UPDATE audit.audit_event SET details = '{}'",
            "DELETE FROM audit.audit_event",
            "TRUNCATE audit.audit_event",
        })
        {
            var act = () => db.ExecuteAsync(sql);
            (await act.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        // Insert cannot pre-seal or backdate the database time.
        var preSealed = () => db.ExecuteAsync(
            """
            INSERT INTO audit.audit_event (event_id, schema_version, occurred_at, category, action, actor_type, actor_id,
                                           actor_display, outcome, correlation_id, sequence)
            VALUES (gen_random_uuid(), 1, now(), 'Auth', 'SignIn', 'User', 'u', 'U', 'Success', 'c', 1)
            """);
        (await preSealed.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        await db.ExecuteAsync(
            """
            INSERT INTO audit.audit_event (event_id, schema_version, occurred_at, recorded_at, category, action, actor_type,
                                           actor_id, actor_display, outcome, correlation_id)
            VALUES (gen_random_uuid(), 1, now(), '2001-01-01', 'Auth', 'SignIn', 'User', 'u', 'U', 'Success', 'c')
            """);
        (await db.ScalarAsync<long>("SELECT count(*) FROM audit.audit_event WHERE recorded_at < now() - interval '1 hour'")).Should().Be(0);

        // The sealer (E14-T03) may set the reserved columns of any chain once, and nothing else.
        await using var sealer = NpgsqlDataSource.Create(await CoreSchemaDatabase.CreateLoginAsync(db.ConnectionString, "opportunity_audit_sealer"));
        await using (var seal = sealer.CreateCommand(
            "UPDATE audit.audit_event SET sequence = 1, prev_hash = decode(repeat('00', 32), 'hex'), " +
            "event_hash = sha256('e'::bytea), sealed_at = now() WHERE workspace_id = @ws"))
        {
            seal.Parameters.AddWithValue("ws", ws);
            (await seal.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        }

        foreach (var sql in new[]
        {
            "UPDATE audit.audit_event SET sequence = 2 WHERE sequence = 1",
            "UPDATE audit.audit_event SET event_hash = sha256('x'::bytea) WHERE sequence = 1",
            "UPDATE audit.audit_event SET actor_id = 'forged'",
            "DELETE FROM audit.audit_event",
        })
        {
            await using var command = sealer.CreateCommand(sql);
            var act = () => command.ExecuteNonQueryAsync(Ct);
            (await act.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    [Fact]
    public async Task Each_transaction_reads_and_writes_only_its_own_chain()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var a = await db.CreateWorkspaceAsync();
        var b = await db.CreateWorkspaceAsync();
        var writer = new PostgresAuditEventWriter(db.AppDataSource);
        await writer.WriteAsync(AuditSamples.Event(a), Ct);
        await writer.WriteAsync(AuditSamples.Event(b), Ct);
        await writer.WriteAsync(AuditSamples.Event(null), Ct);

        (await AuditSamples.ReadAllAsync(db, a)).Should().ContainSingle().Which.Event.WorkspaceId.Should().Be(a);
        (await AuditSamples.ReadAllAsync(db, null)).Should().ContainSingle().Which.Event.WorkspaceId.Should().BeNull();

        await using (var tx = await WorkspaceTransaction.BeginAsync(db.AppDataSource, a, Ct))
        {
            await using var count = tx.Command("SELECT count(*) FROM audit.audit_event");
            (await count.ExecuteScalarAsync(Ct)).Should().Be(1L, "RLS hides workspace b and the system chain");
        }

        // The store refuses a cross-chain event, and RLS would too.
        await using (var tx = await WorkspaceTransaction.BeginAsync(db.AppDataSource, a, Ct))
        {
            var wrongChain = () => AuditSql.InsertAsync(tx, AuditSamples.Event(b), Ct);
            await wrongChain.Should().ThrowAsync<ArgumentException>();
        }

        var raw = await AuditSamples.FailsInWorkspaceAsync(db.AppDataSource, a,
            $"""
            INSERT INTO audit.audit_event (event_id, schema_version, workspace_id, occurred_at, category, action, actor_type,
                                           actor_id, actor_display, outcome, correlation_id)
            VALUES (gen_random_uuid(), 1, '{b}', now(), 'Auth', 'SignIn', 'User', 'u', 'U', 'Success', 'c')
            """);
        raw.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        raw.MessageText.Should().Contain("row-level security");
    }

    [Fact]
    public async Task The_taxonomy_in_code_and_in_the_store_are_the_same_closed_list()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);

        (await db.ColumnAsync("SELECT category || '.' || action FROM audit.audit_action"))
            .Should().BeEquivalentTo(AuditTaxonomy.All.Select(t => $"{t.Category}.{t.Action}"));

        var unknown = () => new PostgresAuditEventWriter(db.AppDataSource).WriteAsync(AuditSamples.Event(null) with { Action = "Teleported" }, Ct).AsTask();
        await unknown.Should().ThrowAsync<ArgumentException>();
        var raw = () => db.ExecuteAsync(
            """
            INSERT INTO audit.audit_event (event_id, schema_version, occurred_at, category, action, actor_type, actor_id,
                                           actor_display, outcome, correlation_id)
            VALUES (gen_random_uuid(), 1, now(), 'Auth', 'Teleported', 'User', 'u', 'U', 'Success', 'c')
            """);
        (await raw.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);

        var deniedWithoutReason = () => db.ExecuteAsync(
            """
            INSERT INTO audit.audit_event (event_id, schema_version, occurred_at, category, action, actor_type, actor_id,
                                           actor_display, outcome, correlation_id)
            VALUES (gen_random_uuid(), 1, now(), 'AuthZ', 'Denied', 'User', 'u', 'U', 'Denied', 'c')
            """);
        (await deniedWithoutReason.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Partitions_exist_ahead_and_the_maintenance_service_extends_them_automatically()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var month = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

        // The migration covers last month through at least 3 months ahead (ADR-013 §1.1).
        (await db.ColumnAsync("SELECT relname::text FROM pg_class WHERE relnamespace = 'audit'::regnamespace AND relispartition"))
            .Should().Contain(Enumerable.Range(-1, 5).Select(i => PartitionName(month.AddMonths(i))));

        // The hosted service runs at start-up as the app role and keeps the horizon moving.
        var options = new PartitionMaintenanceOptions { AuditMonthsAhead = 9, CodingEventMonthsAhead = 30, Interval = TimeSpan.FromHours(1) };
        var services = new ServiceCollection()
            .AddSingleton(db.AppDataSource)
            .AddPartitionMaintenance(options)
            .AddLogging()
            .BuildServiceProvider();
        await using (services)
        {
            var hosted = services.GetServices<IHostedService>().OfType<PartitionMaintenanceService>().Single();
            await hosted.StartAsync(Ct);
            var horizon = month.AddMonths(10);
            for (var i = 0; i < 100 && await HorizonAsync(db) < horizon; i++)
            {
                await Task.Delay(100, Ct);
            }

            await hosted.StopAsync(Ct);
            (await HorizonAsync(db)).Should().Be(horizon);
        }

        (await db.ScalarAsync<long>(
            $"SELECT count(*) FROM pg_class WHERE relname = 'coding_event_p{month.AddMonths(30):yyyyMM}'")).Should().Be(1);

        // Idempotent, and safe when replicas race.
        var maintenance = new PartitionMaintenance(db.AppDataSource, options);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => maintenance.EnsureAsync(Ct)));
        results.Should().OnlyContain(r => r.AuditCreated == 0 && r.CodingEventCreated == 0 && r.AuditHorizon == month.AddMonths(10));
    }

    [Fact]
    public async Task Without_a_database_the_maintenance_service_stands_down()
    {
        var services = new ServiceCollection()
            .AddSingleton<NpgsqlDataSource>(_ => throw new InvalidOperationException("Connection string 'App' is not configured."))
            .AddPartitionMaintenance()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        await using (services)
        {
            var hosted = (PartitionMaintenanceService)services.GetServices<IHostedService>().Single();
            await hosted.StartAsync(Ct);
            await hosted.ExecuteTask!;
            await hosted.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Only_the_retention_role_drops_a_partition_and_only_after_the_retention_period()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var closed = await db.CreateWorkspaceAsync();
        var active = await db.CreateWorkspaceAsync();
        await db.ExecuteAsync("UPDATE opportunity.workspace SET status = 'Closed', closed_at = '2012-06-30' WHERE workspace_id = @ws", ("ws", closed));

        // Two old months (a missing partition is never created implicitly: insert fails without one).
        var tooOld = () => InsertAtAsync(db, closed, "2010-01-15");
        (await tooOld.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("no partition");
        foreach (var m in new[] { "201001", "201002" })
        {
            await db.ExecuteAsync(
                $"CREATE TABLE audit.audit_event_p{m} PARTITION OF audit.audit_event " +
                $"FOR VALUES FROM ('{m[..4]}-{m[4..]}-01 00:00:00+00') TO ('{m[..4]}-{int.Parse(m[4..], CultureInfo.InvariantCulture) + 1:D2}-01 00:00:00+00')");
        }

        await InsertAtAsync(db, closed, "2010-01-15");
        await InsertAtAsync(db, null, "2010-01-20");
        await InsertAtAsync(db, active, "2010-02-10");

        var app = await AuditSamples.FailsInWorkspaceAsync(db.AppDataSource, closed, "SELECT audit.drop_expired_partition('2010-01-01')");
        app.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        await using var retention = NpgsqlDataSource.Create(await CoreSchemaDatabase.CreateLoginAsync(db.ConnectionString, "opportunity_audit_retention"));
        async Task<long> DropAsync(string month)
        {
            await using var command = retention.CreateCommand($"SELECT audit.drop_expired_partition('{month}')");
            return (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        var current = () => DropAsync(DateTime.UtcNow.ToString("yyyy-MM-01", CultureInfo.InvariantCulture));
        (await current.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("within the 7 year retention period");
        var openMatter = () => DropAsync("2010-02-01");
        (await openMatter.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain(active.ToString());

        // E14-T03: the purge removes only what the hash chain and a signed checkpoint cover (the retention job seals first).
        await AuditChainTestSupport.SealAndCheckpointAsync(db);
        (await DropAsync("2010-01-01")).Should().Be(2);
        (await db.ScalarAsync<long>("SELECT count(*) FROM pg_class WHERE relname = 'audit_event_p201001'")).Should().Be(0);
        var purged = (await AuditSamples.ReadAllAsync(db, null)).Single(e => e.Event.Action == AuditTaxonomy.Audit.Purged);
        purged.Event.Details.Should().Contain("Partition", "audit_event_p201001").And.Contain("Events", "2").And.Contain("RetentionYears", "7");

        // A longer installation period (Q-16 is configurable per installation) protects even closed matters.
        await db.ExecuteAsync("UPDATE opportunity.workspace SET status = 'Closed', closed_at = '2012-06-30' WHERE workspace_id = @ws", ("ws", active));
        await db.ExecuteAsync("UPDATE audit.retention_policy SET years_after_close = 20");
        var longer = () => DropAsync("2010-02-01");
        (await longer.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("within the 20 year retention period");
    }

    private static string CurrentPartition() => PartitionName(DateOnly.FromDateTime(DateTime.UtcNow));

    private static string PartitionName(DateOnly month) => "audit_event_p" + month.ToString("yyyyMM", CultureInfo.InvariantCulture);

    private static async Task<DateOnly> HorizonAsync(CoreSchemaDatabase db)
    {
        await using var command = db.DataSource.CreateCommand("SELECT audit.partition_horizon()");
        return (DateOnly)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static Task InsertAtAsync(CoreSchemaDatabase db, Guid? workspaceId, string occurredAt) => db.ExecuteAsync(
        $"""
        INSERT INTO audit.audit_event (event_id, schema_version, workspace_id, occurred_at, category, action, actor_type,
                                       actor_id, actor_display, outcome, correlation_id)
        VALUES (gen_random_uuid(), 1, {(workspaceId is null ? "NULL" : $"'{workspaceId}'")}, '{occurredAt}', 'Auth', 'SignIn',
                'User', 'u', 'U', 'Success', 'c')
        """);
}
