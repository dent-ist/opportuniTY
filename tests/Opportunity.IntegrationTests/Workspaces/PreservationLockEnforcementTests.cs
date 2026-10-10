using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Authorization;
using Opportunity.Application.Workspaces;
using Opportunity.Core.Security;
using Opportunity.Data;
using Opportunity.Data.Workspaces;
using Opportunity.IntegrationTests.Audit;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Productions;

namespace Opportunity.IntegrationTests.Workspaces;

/// <summary>
/// E20-T01 / ADR-014 §2.3: a preservation lock is enforced by PostgreSQL itself (V0048), so no code path, present or
/// future, application role or owner, can delete a held workspace's preserved records. The first test pins which tables are
/// guarded: a new tenant table fails it until it is classified, which is what keeps new delete paths from bypassing the
/// hold.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class PreservationLockEnforcementTests(MigrationPostgresFixture postgres)
{
    /// <summary>Preserved records (ADR-014 §2.3): a DELETE of a held workspace's rows, or any TRUNCATE while a hold exists, fails.</summary>
    public static readonly string[] Guarded =
    [
        "acknowledgment", "acknowledgment_version", "bates_range", "coding_event", "document", "document_overlay_event", "document_set_snapshot",
        "document_set_snapshot_page", "export", "export_document", "export_file", "import_batch", "page", "page_image", "page_set", "privilege_log",
        "privilege_log_entry", "production", "production_qc_exception", "production_qc_run", "redaction_revision",
        "review_batch", "review_batch_checkout", "review_batch_document", "review_batch_set", "search_term_report", "stored_object",
        "workspace_data_key",
    ];

    private const string CurrentState = "current state that review work replaces; its history is guarded (coding_event, overlay events, redaction revisions)";
    private const string Configuration = "workspace configuration administrators edit; not evidence (ADR-014 §5 keeps it out of the preserved records)";
    private const string Operational = "operational queue, lease, cache or idempotency record; nothing of a matter's record lives only here";
    private const string Never = "nothing deletes it and its parent record is guarded";

    /// <summary>Tenant tables a hold does not freeze, with the reason. A new table must be added here or to <see cref="Guarded"/>.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["preservation_lock"] = "the holds themselves: never deleted by any role (REVOKE + trigger)",
        ["workspace"] = "the registry: its own triggers refuse DELETE and the move to Deleting/Purged while held",
        ["retired_control_number"] = Never,
        ["document_projection_state"] = CurrentState,
        ["document_coding_field"] = CurrentState,
        ["document_coding_choice"] = CurrentState,
        ["document_restriction"] = CurrentState,
        ["document_wall"] = CurrentState,
        ["document_redaction_state"] = CurrentState,
        ["document_family_attachment"] = CurrentState,
        ["family_issue"] = CurrentState,
        ["duplicate_group"] = CurrentState,
        ["email_thread"] = CurrentState,
        ["production_document"] = "a draft production's plan, replaced when the draft is re-planned; frozen with the production (production_document_frozen_guard)",
        ["production_designation_override"] = "a draft production's per-document designation override; every change is audited and a trigger locks it once the production is finalized (E12-T04)",
        ["search_term_report_term"] = "results of a report, replaced by a rerun over the same frozen set; the report itself is guarded",
        ["search_term_report_document"] = "results of a report, replaced by a rerun over the same frozen set; the report itself is guarded",
        ["search_term_report_hit"] = "results of a report, replaced by a rerun over the same frozen set; the report itself is guarded",
        ["field_catalog_counter"] = Configuration,
        ["field_definition"] = Configuration,
        ["choice"] = Configuration,
        ["coding_layout"] = Configuration,
        ["coding_layout_section"] = Configuration,
        ["coding_layout_field"] = Configuration,
        ["coding_layout_role"] = Configuration,
        ["workspace_role_assignment"] = Configuration,
        ["restriction_class"] = Configuration,
        ["restriction_class_grant"] = Configuration,
        ["restriction_class_rule"] = Configuration,
        ["ethical_wall"] = Configuration,
        ["ethical_wall_member"] = Configuration,
        ["ethical_wall_scope"] = Configuration,
        ["field_security"] = Configuration,
        ["break_glass_activation"] = Configuration,
        ["redaction_set"] = Configuration,
        ["redaction_reason"] = Configuration,
        ["dedupe_policy"] = Configuration,
        ["import_profile"] = Configuration,
        ["saved_search_folder"] = Configuration,
        ["saved_search"] = Configuration,
        ["saved_search_share"] = Configuration,
        ["grid_view"] = Configuration,
        ["grid_layout"] = Configuration,
        ["highlight_set"] = Configuration,
        ["highlight_set_selection"] = Configuration,
        ["privilege_log_template"] = Configuration,
        ["query_history"] = Configuration,
        ["job"] = Operational,
        ["job_chunk"] = Operational,
        ["job_chunk_item_result"] = Operational,
        ["coding_write"] = Operational,
        ["search_outbox"] = Operational,
        ["index_chunk_task"] = Operational,
        ["workspace_index_placement"] = Operational,
        ["workspace_search_generation"] = Operational,
        ["workspace_search_watermark"] = Operational,
        ["search_session"] = Operational,
        ["search_cursor"] = Operational,
        ["dead_letter"] = Operational,
        ["render_request"] = Operational,
        ["search_reindex"] = Operational,
        ["import_batch_chunk"] = Operational,
        ["import_batch_key"] = Operational,
        ["import_batch_member"] = Operational,
        ["import_batch_image"] = Operational,
        ["import_batch_bates"] = Operational,
        ["import_row_issue"] = Never,
        ["import_preflight"] = "a pre-flight check's 48-hour result before an import exists",
        ["import_preflight_issue"] = "a pre-flight check's 48-hour result before an import exists",
        ["coding_propagation_preview"] = Operational,
        ["document_set_snapshot_stage"] = Operational,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_tenant_table_is_guarded_or_exempt_for_a_stated_reason_and_guards_cover_every_partition()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var tables = await db.ColumnAsync(
            """
            SELECT c.relname::text
            FROM pg_class c
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'workspace_id' AND NOT a.attisdropped
            WHERE c.relnamespace = 'opportunity'::regnamespace AND c.relkind IN ('r', 'p') AND NOT c.relispartition
            ORDER BY 1
            """);
        Guarded.Should().OnlyHaveUniqueItems().And.NotIntersectWith(Exempt.Keys);
        tables.Except(Guarded).Except(Exempt.Keys).Should().BeEmpty("every tenant table must be classified: guarded by the legal hold or exempt with a reason");
        Guarded.Concat(Exempt.Keys).Except(tables).Should().BeEmpty("the lists name existing tables only");

        var triggers = await db.ColumnAsync(
            """
            SELECT c.relname || '|' || t.tgname || '|' || p.proname
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_proc p ON p.oid = t.tgfoid
            WHERE c.relnamespace = 'opportunity'::regnamespace AND NOT t.tgisinternal AND t.tgname LIKE 'preservation_%'
              AND NOT c.relispartition
            """);
        foreach (var table in Guarded)
        {
            var guard = table switch
            {
                "stored_object" => "preservation_artifact_delete_guard",
                "coding_event" => "preservation_row_delete_guard",
                _ => "preservation_delete_guard",
            };
            triggers.Should().Contain($"{table}|preservation_delete_guard|{guard}", table);
            triggers.Should().Contain($"{table}|preservation_truncate_guard|preservation_truncate_guard", table);
        }

        triggers.Where(t => t.Contains("|preservation_delete_guard|", StringComparison.Ordinal)).Should().HaveCount(Guarded.Length);

        // A partitioned table (coding_event) guards by row, a trigger every partition inherits, including future ones.
        var partitions = await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_inherits WHERE inhparent = 'opportunity.coding_event'::regclass");
        var guardedPartitions = await db.ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_trigger t JOIN pg_inherits i ON i.inhrelid = t.tgrelid
            WHERE i.inhparent = 'opportunity.coding_event'::regclass AND t.tgname = 'preservation_delete_guard'
            """);
        partitions.Should().BeGreaterThan(0);
        guardedPartitions.Should().Be(partitions);
        await db.ExecuteAsync("SELECT opportunity.coding_event_ensure_partitions(now() + interval '30 months')");
        (await db.ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_inherits i
            WHERE i.inhparent = 'opportunity.coding_event'::regclass
              AND NOT EXISTS (SELECT FROM pg_trigger t WHERE t.tgrelid = i.inhrelid AND t.tgname = 'preservation_delete_guard')
            """)).Should().Be(0, "partitions created later get the guard too");
    }

    [Fact]
    public async Task A_held_workspace_refuses_every_delete_of_its_records_by_any_role_until_the_hold_is_released()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var held = await db.CreateWorkspaceAsync();
        var other = await db.CreateWorkspaceAsync();
        var document = (await db.InsertDocumentAsync(held, "HOLD-0001")).DocumentId;
        var bare = (await db.InsertDocumentAsync(held, "HOLD-0002")).DocumentId;
        var pageSet = await db.InsertPageSetAsync(held, document);
        await db.InsertStoredObjectAsync(held, document);
        var otherDocument = (await db.InsertDocumentAsync(other, "FREE-0001")).DocumentId;
        await db.InsertPageSetAsync(other, otherDocument);

        var store = new PreservationLockStore(db.AppDataSource);
        var service = new PreservationLockService(store, TimeProvider.System);
        var admin = Principal();
        var placed = await service.PlaceAsync(admin, held, new PreservationLockRequest("Preservation letter received", "PL-7", false), Ct);
        placed.Status.Should().Be(PreservationLockStatus.Ok);
        (await store.IsLockedAsync(held, Ct)).Should().BeTrue();
        (await store.IsLockedAsync(other, Ct)).Should().BeFalse();

        // The application role, in the workspace's own context.
        foreach (var sql in new[]
        {
            "DELETE FROM opportunity.stored_object WHERE workspace_id = @ws",
            "DELETE FROM opportunity.page_set WHERE workspace_id = @ws",
            // Its projection state (current state, not guarded) first, or the foreign key would refuse before the guard.
            $"DELETE FROM opportunity.document_projection_state WHERE workspace_id = @ws AND document_id = '{bare}'; "
                + $"DELETE FROM opportunity.document WHERE workspace_id = @ws AND document_id = '{bare}'",
            "SELECT opportunity.assert_workspace_not_preserved(@ws, 'test')",
        })
        {
            var refused = await FailsAsync(db.AppDataSource, held, sql);
            refused.SqlState.Should().Be(PreservationLockViolation.SqlState, sql);
            refused.MessageText.Should().Contain(held.ToString(), "the refusal names the held workspace");
            PreservationLockViolation.TryGet(refused, out var violation).Should().BeTrue();
            violation!.WorkspaceId.Should().Be(held);
        }

        // The owner (the migrator login, bound by FORCE RLS) and a superuser are refused too: the guard is a trigger, not a grant.
        foreach (var sql in new[]
        {
            $"DELETE FROM opportunity.document_projection_state WHERE workspace_id = '{held}' AND document_id = '{bare}'; "
                + $"DELETE FROM opportunity.document WHERE workspace_id = '{held}' AND document_id = '{bare}'",
            "TRUNCATE opportunity.page CASCADE",
            $"UPDATE opportunity.workspace SET status = 'Deleting', closed_at = now() WHERE workspace_id = '{held}'",
            $"UPDATE opportunity.workspace SET status = 'Purged', closed_at = now() WHERE workspace_id = '{held}'",
            $"DELETE FROM opportunity.workspace WHERE workspace_id = '{held}'",
            $"DELETE FROM opportunity.preservation_lock WHERE workspace_id = '{held}'",
        })
        {
            var act = () => db.ExecuteAsync(sql);
            var refused = (await act.Should().ThrowAsync<PostgresException>(sql)).Which;
            refused.SqlState.Should().BeOneOf([PreservationLockViolation.SqlState, PostgresErrorCodes.InsufficientPrivilege], sql);
        }

        // The hold covers its workspace only; and the import writer's cleanup of objects whose document it never inserted
        // still works under a hold (no preserved record is involved).
        await db.InWorkspaceAsync(other, async tx =>
        {
            await using var delete = tx.Command("DELETE FROM opportunity.page_set WHERE workspace_id = @ws");
            delete.Parameters.AddWithValue("ws", other);
            (await delete.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        });
        await db.InWorkspaceAsync(held, async tx =>
        {
            var orphan = Guid.CreateVersion7();
            await using var insert = tx.Command(
                """
                INSERT INTO opportunity.stored_object (workspace_id, object_id, logical_key, area, document_id, sha256, size_bytes, key_id,
                                                       encryption_scheme, state)
                VALUES (@ws, @id, 'ws/' || replace(@ws::text, '-', '') || '/docs/orphan/native/x', 1, @doc, sha256('x'::bytea), 1, 'installation', 1, 1)
                """);
            insert.Parameters.AddWithValue("ws", held);
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("doc", orphan);
            await insert.ExecuteNonQueryAsync(Ct);
            await using var delete = tx.Command("DELETE FROM opportunity.stored_object WHERE workspace_id = @ws AND document_id = @doc");
            delete.Parameters.AddWithValue("ws", held);
            delete.Parameters.AddWithValue("doc", orphan);
            (await delete.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        });

        (await db.ScalarAsync<long>($"SELECT count(*) FROM opportunity.page_set WHERE workspace_id = '{held}'")).Should().Be(1, "nothing was deleted");
        (await db.ScalarAsync<string>($"SELECT status FROM opportunity.workspace WHERE workspace_id = '{held}'")).Should().Be("Active");

        // Released: the same delete goes through.
        var released = await service.RequestReleaseAsync(admin, held, placed.Lock!.LockId, placed.Lock.Version, "Matter settled; hold lifted by counsel", Ct);
        released.Status.Should().Be(PreservationLockStatus.Ok);
        released.Lock!.ReleasedAt.Should().NotBeNull();
        (await store.IsLockedAsync(held, Ct)).Should().BeFalse();
        await db.InWorkspaceAsync(held, async tx =>
        {
            await using var delete = tx.Command("DELETE FROM opportunity.page_set WHERE workspace_id = @ws AND page_set_id = @id");
            delete.Parameters.AddWithValue("ws", held);
            delete.Parameters.AddWithValue("id", pageSet);
            (await delete.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        });

        // A released hold is history: it never changes again and is never deleted.
        var changed = () => db.ExecuteAsync($"UPDATE opportunity.preservation_lock SET reason = 'x', version = version + 1 WHERE workspace_id = '{held}'");
        (await changed.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("released");
    }

    [Fact]
    public async Task Overlapping_holds_all_have_to_be_released_and_a_hold_placed_meanwhile_waits_for_a_running_delete()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var document = (await db.InsertDocumentAsync(ws, "HOLD-0002")).DocumentId;
        await db.InsertPageSetAsync(ws, document);
        await db.InsertPageSetAsync(ws, document);
        var store = new PreservationLockStore(db.AppDataSource);
        var service = new PreservationLockService(store, TimeProvider.System);
        var admin = Principal();

        // A delete that started before the hold commits first; the hold's placement waits for it (workspace row lock).
        await using (var deleting = await WorkspaceTransaction.BeginAsync(db.AppDataSource, ws, Ct))
        {
            await using (var delete = deleting.Command("DELETE FROM opportunity.page_set WHERE workspace_id = @ws AND page_set_id = (SELECT page_set_id FROM opportunity.page_set WHERE workspace_id = @ws ORDER BY page_set_id LIMIT 1)"))
            {
                delete.Parameters.AddWithValue("ws", ws);
                (await delete.ExecuteNonQueryAsync(Ct)).Should().Be(1);
            }

            var placing = service.PlaceAsync(admin, ws, new PreservationLockRequest("Litigation hold A", null, true), Ct);
            await Task.Delay(300, Ct);
            placing.IsCompleted.Should().BeFalse("placing a hold waits for a delete in flight");
            await deleting.CommitAsync(Ct);
            (await placing).Status.Should().Be(PreservationLockStatus.Ok);
        }

        var second = await service.PlaceAsync(admin, ws, new PreservationLockRequest("Regulator's preservation notice", "REG-2", false), Ct);
        (await db.ScalarAsync<int>($"SELECT active_preservation_locks FROM opportunity.workspace WHERE workspace_id = '{ws}'")).Should().Be(2);
        await service.RequestReleaseAsync(admin, ws, second.Lock!.LockId, 1, "Notice withdrawn", Ct);
        (await store.IsLockedAsync(ws, Ct)).Should().BeTrue("the first hold is still active");
        (await FailsAsync(db.AppDataSource, ws, "DELETE FROM opportunity.page_set WHERE workspace_id = @ws")).SqlState.Should().Be(PreservationLockViolation.SqlState);
    }

    [Fact]
    public async Task The_audit_purge_refuses_a_partition_with_events_of_a_held_workspace()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        await db.ExecuteAsync("UPDATE opportunity.workspace SET status = 'Closed', closed_at = '2012-06-30' WHERE workspace_id = @ws", ("ws", ws));
        await db.ExecuteAsync(
            "CREATE TABLE audit.audit_event_p201003 PARTITION OF audit.audit_event FOR VALUES FROM ('2010-03-01 00:00:00+00') TO ('2010-04-01 00:00:00+00')");
        await db.ExecuteAsync(
            $"""
            INSERT INTO audit.audit_event (event_id, schema_version, workspace_id, occurred_at, category, action, actor_type,
                                           actor_id, actor_display, outcome, correlation_id)
            VALUES (gen_random_uuid(), 1, '{ws}', '2010-03-15', 'Auth', 'SignIn', 'User', 'u', 'U', 'Success', 'c')
            """);
        await PreservationLockTestSql.PlaceAsync(db, ws);

        await using var retention = NpgsqlDataSource.Create(await CoreSchemaDatabase.CreateLoginAsync(db.ConnectionString, "opportunity_audit_retention"));
        await using (var drop = retention.CreateCommand("SELECT audit.drop_expired_partition('2010-03-01')"))
        {
            var act = () => drop.ExecuteScalarAsync(Ct);
            var refused = (await act.Should().ThrowAsync<PostgresException>()).Which;
            refused.SqlState.Should().Be(PreservationLockViolation.SqlState);
            refused.MessageText.Should().Contain(ws.ToString()).And.Contain("preservation lock");
        }

        await PreservationLockTestSql.ReleaseAllAsync(db, ws);
        await AuditChainTestSupport.SealAndCheckpointAsync(db);
        await using (var drop = retention.CreateCommand("SELECT audit.drop_expired_partition('2010-03-01')"))
        {
            ((long)(await drop.ExecuteScalarAsync(Ct))!).Should().Be(1);
        }
    }

    [Fact]
    public async Task Snapshot_retention_keeps_every_frozen_set_of_a_held_workspace()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        var docs = await h.FamiliesAsync(ws, "SNAP", [(1, "pdf")], [(1, "pdf")]);
        var snapshot = await h.SnapshotAsync(ws, user, docs);
        await PreservationLockTestSql.PlaceAsync(h.Db, ws);

        (await h.Snapshots.ExpireAsync(ws, TimeSpan.Zero, TimeSpan.Zero, 100, Ct)).Should().BeEmpty("a held workspace keeps its frozen sets");
        (await h.Db.ScalarAsync<long>($"SELECT count(*) FROM opportunity.document_set_snapshot_page WHERE workspace_id = '{ws}'")).Should().BeGreaterThan(0);

        await PreservationLockTestSql.ReleaseAllAsync(h.Db, ws);
        (await h.Snapshots.ExpireAsync(ws, TimeSpan.Zero, TimeSpan.Zero, 100, Ct)).Should().Equal(snapshot.SnapshotId);
    }

    private static SecurityPrincipal Principal() => new()
    {
        UserId = Guid.CreateVersion7(),
        DisplayName = "Hold Admin",
        CorrelationId = "preservation-test",
    };

    private static async Task<PostgresException> FailsAsync(NpgsqlDataSource dataSource, Guid workspaceId, string sql)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, Ct);
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", workspaceId);
        var act = () => command.ExecuteNonQueryAsync(Ct);
        return (await act.Should().ThrowAsync<PostgresException>(sql)).Which;
    }
}

/// <summary>Places and releases holds by SQL (as the owner) for tests of other features.</summary>
internal static class PreservationLockTestSql
{
    public static async Task<Guid> PlaceAsync(CoreSchemaDatabase db, Guid workspaceId)
    {
        var lockId = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            INSERT INTO opportunity.preservation_lock (workspace_id, lock_id, reason, release_requires_approval, placed_by)
            VALUES (@ws, @id, 'Test hold', false, @by)
            """,
            ("ws", workspaceId), ("id", lockId), ("by", Guid.CreateVersion7()));
        return lockId;
    }

    public static Task ReleaseAllAsync(CoreSchemaDatabase db, Guid workspaceId) => db.ExecuteAsync(
        """
        UPDATE opportunity.preservation_lock
           SET release_requested_by = placed_by, release_requested_at = now(), release_reason = 'Test release', released_at = now(),
               version = version + 1
         WHERE workspace_id = @ws AND released_at IS NULL
        """,
        ("ws", workspaceId));
}
