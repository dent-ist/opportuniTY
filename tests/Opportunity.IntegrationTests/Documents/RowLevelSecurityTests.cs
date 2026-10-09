using System.Globalization;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Dapper;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Core.Coding;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Core.Pages;
using Opportunity.Data;
using Opportunity.Data.Migrations;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Documents;

/// <summary>
/// E05-T03 / ADR-015 D7: PostgreSQL row-level security is the workspace-isolation backstop. These tests run as a login
/// in <c>opportunity_app</c> (NOBYPASSRLS, owns nothing), the role every API and worker host uses.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class RowLevelSecurityTests(MigrationPostgresFixture postgres)
{
    /// <summary>Tables with a workspace_id that deliberately deviate from the single isolation policy, with the reason.</summary>
    private static readonly Dictionary<string, string> PolicyAllowList = new(StringComparer.Ordinal)
    {
        ["workspace"] = "workspace registry: reads are installation-level (ADR-015 D7.1), writes need the workspace context",
    };

    /// <summary>Reviewed SECURITY DEFINER functions (ADR-015 D7.4.4).</summary>
    private static readonly string[] SecurityDefinerAllowList =
        [
            "coding_event_ensure_partitions", "search_work_drop_expired_partitions", "search_work_ensure_partitions",
            // Workspace deletion (E20-T02, V0054): purge and count one deleting workspace's rows across tables.
            "workspace_purge_batch", "workspace_purge_counts",
        ];

    private const string IsolationExpression = "(workspace_id = (NULLIF(current_setting('app.workspace_id'::text, true), ''::text))::uuid)";

    private static readonly string CurrentPartition =
        "opportunity.coding_event_p" + DateTime.UtcNow.ToString("yyyyMM", CultureInfo.InvariantCulture);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_table_with_a_workspace_id_has_forced_rls_and_the_isolation_policy()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);

        var tables = await RowsAsync(db.DataSource,
            """
            SELECT c.relname::text, c.relrowsecurity::text, c.relforcerowsecurity::text
            FROM pg_class c
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'workspace_id' AND NOT a.attisdropped
            WHERE c.relnamespace = 'opportunity'::regnamespace AND c.relkind IN ('r', 'p')
            ORDER BY 1
            """);
        tables.Should().HaveCountGreaterThan(40, "core, field, coding tables and the coding_event partitions");
        tables.Should().OnlyContain(t => t[1] == "true" && t[2] == "true", "RLS is enabled and forced everywhere");

        var policies = await RowsAsync(db.DataSource,
            """
            SELECT c.relname::text, p.polname::text, p.polcmd::text, p.polpermissive::text,
                   coalesce(pg_get_expr(p.polqual, p.polrelid), ''), coalesce(pg_get_expr(p.polwithcheck, p.polrelid), ''),
                   array_to_string(p.polroles::regrole[], ',')
            FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
            WHERE c.relnamespace = 'opportunity'::regnamespace
            ORDER BY 1, 2
            """);
        foreach (var table in tables.Select(t => t[0]).Where(t => !PolicyAllowList.ContainsKey(t)))
        {
            policies.Where(p => p[0] == table).Should().ContainSingle(table).Which.Should().Equal(
                [table, "workspace_isolation", "*", "true", IsolationExpression, IsolationExpression, "-"], table);
        }

        policies.Where(p => p[0] == "workspace").Select(p => string.Join('|', p[1..6])).Should().BeEquivalentTo(
            "workspace_registry_read|r|true|true|",
            $"workspace_isolation_insert|a|true||{IsolationExpression}",
            $"workspace_isolation_update|w|true|{IsolationExpression}|{IsolationExpression}",
            $"workspace_isolation_delete|d|true|{IsolationExpression}|");

        // No views (or only security_invoker ones), and only reviewed SECURITY DEFINER functions with a pinned search_path.
        (await db.ColumnAsync(
            "SELECT relname::text FROM pg_class WHERE relnamespace = 'opportunity'::regnamespace AND relkind IN ('v', 'm') " +
            "AND NOT coalesce(reloptions @> '{security_invoker=true}', false)")).Should().BeEmpty();
        (await db.ColumnAsync(
            "SELECT proname::text FROM pg_proc WHERE pronamespace = 'opportunity'::regnamespace AND prosecdef " +
            "AND array_to_string(proconfig, ',') LIKE '%search_path=pg_catalog, pg_temp%'"))
            .Should().BeEquivalentTo(SecurityDefinerAllowList);
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_proc WHERE pronamespace = 'opportunity'::regnamespace AND prosecdef")).Should().Be(SecurityDefinerAllowList.Length);

        // Runtime roles cannot bypass RLS and have no privilege on any child partition.
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_roles WHERE rolname IN ('opportunity_app', 'opportunity_readonly') AND NOT rolbypassrls AND NOT rolsuper"))
            .Should().Be(2);
        (await db.ColumnAsync(
            """
            SELECT c.relname::text FROM pg_class c CROSS JOIN (VALUES ('opportunity_app'), ('opportunity_readonly')) r(role)
            WHERE c.relnamespace = 'opportunity'::regnamespace AND c.relispartition AND c.relkind IN ('r', 'p')
              AND has_table_privilege(r.role, c.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
            """)).Should().BeEmpty();

        await using var connection = await db.DataSource.OpenConnectionAsync(Ct);
        (await RowLevelSecurityLint.FindViolationsAsync(
            connection, null, ["opportunity"], "workspace_id", ["opportunity_app", "opportunity_readonly"], Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_without_a_workspace_context_sees_no_tenant_rows_and_cannot_write()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var a = await SeedAsync(db);
        var b = await SeedAsync(db);
        var tables = await TenantParentTablesAsync(db);
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.coding_event")).Should().BeGreaterThan(0);

        await using var app = new NpgsqlConnection(db.AppConnectionString);
        await app.OpenAsync(Ct);
        foreach (var table in tables)
        {
            (await app.ExecuteScalarAsync<long>($"SELECT count(*) FROM opportunity.{table}")).Should().Be(0, table);
        }

        // An empty context (e.g. a reset GUC on a pooled connection) is the same as none.
        await using (var tx = await app.BeginTransactionAsync(Ct))
        {
            await app.ExecuteAsync("SELECT set_config('app.workspace_id', '', true)", transaction: tx);
            (await app.ExecuteScalarAsync<long>("SELECT count(*) FROM opportunity.document", transaction: tx)).Should().Be(0);
        }

        // The workspace registry stays readable (installation level) but not writable without the context.
        (await app.ExecuteScalarAsync<long>("SELECT count(*) FROM opportunity.workspace")).Should().Be(2);
        (await app.ExecuteAsync("UPDATE opportunity.workspace SET name = 'renamed'")).Should().Be(0);
        (await app.ExecuteAsync("UPDATE opportunity.document_projection_state SET document_version = document_version + 1")).Should().Be(0);
        (await app.ExecuteAsync("DELETE FROM opportunity.document_coding_choice")).Should().Be(0);
        await ExpectRlsViolation(() => app.ExecuteAsync(
            "INSERT INTO opportunity.retired_control_number (workspace_id, control_number_norm, control_number, document_id) " +
            "VALUES (@ws, 'X1', 'X1', gen_random_uuid())", new { ws = a.Id }));
        await ExpectRlsViolation(() => app.ExecuteAsync(
            "INSERT INTO opportunity.workspace (workspace_id, name, display_time_zone) VALUES (gen_random_uuid(), 'rogue', 'UTC')"));

        // Repositories always bind a context, so they keep working.
        (await db.Documents.GetVersionAsync(b.Id, b.DocumentId, Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Queries_missing_the_workspace_predicate_stay_inside_the_context_for_raw_SQL_Dapper_and_EF_Core()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var a = await SeedAsync(db, documents: 3);
        var b = await SeedAsync(db, documents: 5);

        await db.InWorkspaceAsync(a.Id, async tx =>
        {
            // Raw SQL (Npgsql) with no WHERE: only workspace A is visible or touched.
            await using (var count = tx.Command("SELECT count(*) FROM opportunity.document"))
            {
                ((long)(await count.ExecuteScalarAsync(Ct))!).Should().Be(3);
            }

            await using (var bump = tx.Command("UPDATE opportunity.document_projection_state SET document_version = document_version + 10"))
            {
                (await bump.ExecuteNonQueryAsync(Ct)).Should().Be(3);
            }

            await using (var delete = tx.Command("DELETE FROM opportunity.document_coding_choice"))
            {
                (await delete.ExecuteNonQueryAsync(Ct)).Should().Be(1);
            }

            // Dapper: a bugged join and an explicit predicate on the other workspace.
            var ids = await tx.Connection.QueryAsync<Guid>(
                "SELECT d.workspace_id FROM opportunity.document d JOIN opportunity.coding_event e USING (document_id)", transaction: tx.Transaction);
            ids.Should().OnlyContain(id => id == a.Id).And.NotBeEmpty();
            (await tx.Connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM opportunity.field_definition WHERE workspace_id = @ws", new { ws = b.Id }, tx.Transaction)).Should().Be(0);
            (await tx.Connection.ExecuteAsync(
                "UPDATE opportunity.workspace SET name = 'hijacked' WHERE workspace_id = @ws", new { ws = b.Id }, tx.Transaction)).Should().Be(0);

            // EF Core with the global filters switched off: RLS still confines it.
            await using var context = tx.CreateDbContext();
            (await context.Documents.IgnoreQueryFilters().CountAsync(Ct)).Should().Be(3);
            (await context.Pages.IgnoreQueryFilters().Where(p => p.WorkspaceId == b.Id).CountAsync(Ct)).Should().Be(0);
            (await context.Documents.Where(d => d.WorkspaceId == b.Id).CountAsync(Ct)).Should().Be(0);
        });

        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.document_projection_state WHERE workspace_id = @ws AND document_version > 10", ("ws", b.Id)))
            .Should().Be(0, "workspace B was not touched");
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.document_coding_choice WHERE workspace_id = @ws", ("ws", b.Id)))
            .Should().Be(1);
        (await db.ScalarAsync<string>("SELECT name FROM opportunity.workspace WHERE workspace_id = @ws", ("ws", b.Id))).Should().NotBe("hijacked");

        // Repositories asked about another workspace's rows under their own context find nothing.
        (await db.Documents.GetVersionAsync(a.Id, b.DocumentId, Ct)).Should().BeNull();
        (await db.Coding.GetCurrentAsync(a.Id, [b.DocumentId], Ct)).Should().OnlyContain(c => c.Fields.Count == 0);
        (await db.Coding.GetEventsAsync(new CodingEventQuery(a.Id) { DocumentId = b.DocumentId }, Ct)).Events.Should().BeEmpty();
        (await db.Fields.GetCatalogAsync(a.Id, cancellationToken: Ct)).Fields.Should().NotContain(f => f.WorkspaceId == b.Id);
    }

    [Fact]
    public async Task Cross_workspace_writes_fail_at_the_database()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var a = await SeedAsync(db);
        var b = await SeedAsync(db);

        // Raw SQL: inserting or moving a row into another workspace violates WITH CHECK.
        await ExpectRlsViolation(() => db.InWorkspaceAsync(a.Id, async tx =>
        {
            await using var insert = tx.Command(
                "INSERT INTO opportunity.retired_control_number (workspace_id, control_number_norm, control_number, document_id) " +
                "VALUES (@ws, 'X1', 'X1', gen_random_uuid())");
            insert.Parameters.AddWithValue("ws", b.Id);
            await insert.ExecuteNonQueryAsync(Ct);
        }));
        await ExpectRlsViolation(() => db.InWorkspaceAsync(a.Id, async tx =>
        {
            await using var move = tx.Command("UPDATE opportunity.coding_layout_role SET workspace_id = @ws; " +
                "UPDATE opportunity.field_catalog_counter SET workspace_id = @ws");
            move.Parameters.AddWithValue("ws", b.Id);
            await move.ExecuteNonQueryAsync(Ct);
        }));

        // Dapper.
        await ExpectRlsViolation(() => db.InWorkspaceAsync(a.Id, tx => tx.Connection.ExecuteAsync(
            "INSERT INTO opportunity.page_set (workspace_id, page_set_id, document_id, source, page_count, status) VALUES (@ws, gen_random_uuid(), @doc, 1, 1, 1)",
            new { ws = b.Id, doc = b.DocumentId }, tx.Transaction)));

        // EF Core.
        var efWrite = () => db.InDbContextAsync(a.Id, async context =>
        {
            context.PageSets.Add(new PageSet
            {
                WorkspaceId = b.Id,
                PageSetId = Guid.CreateVersion7(),
                DocumentId = b.DocumentId,
                Source = PageSetSource.Imported,
            });
            await context.SaveChangesAsync(Ct);
        });
        (await efWrite.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        // A repository fed a request for workspace B under workspace A's id writes nothing to B.
        var request = new CodingWriteRequest
        {
            WorkspaceId = a.Id,
            IdempotencyKey = "cross-" + Guid.CreateVersion7().ToString("N"),
            Actor = new CodingActor(Guid.CreateVersion7(), CodingActorType.Human),
            Documents = [new CodingTarget(b.DocumentId)],
            Operations = [CodingFieldOperation.AddChoices(a.FieldId, a.ChoiceId)],
        };
        var cross = await db.Coding.ApplyAsync(request, Ct);
        cross.Documents.Should().OnlyContain(d => d.Outcome == DocumentCodingOutcome.NotFound);
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.coding_event WHERE document_id = @d", ("d", b.DocumentId)))
            .Should().Be(1, "only B's own seed event");
    }

    [Fact]
    public async Task Bulk_COPY_goes_through_a_staging_table_and_direct_COPY_into_a_tenant_table_is_rejected()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var a = await SeedAsync(db);
        var b = await SeedAsync(db);

        var batch = Enumerable.Range(1, 500).Select(i => Document.Create(a.Id, $"BULK{i:D5}", caseSensitive: false)).ToList();
        await db.Documents.InsertManyAsync(a.Id, batch, Ct);
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ("ws", a.Id))).Should().Be(501);

        // PostgreSQL refuses COPY FROM into a table whose RLS applies to the caller (ADR-015 D7.4.2).
        var direct = () => db.InWorkspaceAsync(a.Id, async tx =>
        {
            await using var importer = await tx.Connection.BeginBinaryImportAsync(
                "COPY opportunity.retired_control_number (workspace_id, control_number_norm, control_number, document_id) FROM STDIN (FORMAT BINARY)", Ct);
            await importer.CompleteAsync(Ct);
        });
        (await direct.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.FeatureNotSupported);

        // Staged rows for another workspace are stopped by WITH CHECK on the INSERT ... SELECT.
        await ExpectRlsViolation(() => db.InWorkspaceAsync(a.Id, async tx =>
        {
            await using (var stage = tx.Command(
                "CREATE TEMP TABLE rcn_stage ON COMMIT DROP AS SELECT * FROM opportunity.retired_control_number WITH NO DATA"))
            {
                await stage.ExecuteNonQueryAsync(Ct);
            }

            await using (var importer = await tx.Connection.BeginBinaryImportAsync(
                "COPY rcn_stage (workspace_id, control_number_norm, control_number, document_id) FROM STDIN (FORMAT BINARY)", Ct))
            {
                await importer.WriteRowAsync(Ct, b.Id, "SMUGGLED1", "SMUGGLED1", Guid.CreateVersion7());
                await importer.CompleteAsync(Ct);
            }

            await using var insert = tx.Command("INSERT INTO opportunity.retired_control_number SELECT * FROM rcn_stage");
            await insert.ExecuteNonQueryAsync(Ct);
        }));

        // The repository refuses a mixed batch before it reaches the database.
        var mixed = () => db.Documents.InsertManyAsync(a.Id, [Document.Create(b.Id, "MIXED1", caseSensitive: false)], Ct);
        await mixed.Should().ThrowAsync<ArgumentException>();

        // COPY ... TO STDOUT is filtered by the policy.
        var exported = 0;
        await db.InWorkspaceAsync(a.Id, async tx =>
        {
            using var reader = await tx.Connection.BeginTextExportAsync("COPY opportunity.document (document_id) TO STDOUT", Ct);
            while (await reader.ReadLineAsync(Ct) is not null)
            {
                exported++;
            }
        });
        exported.Should().Be(501);
    }

    [Fact]
    public async Task Partitions_cannot_be_queried_directly_to_bypass_the_parent_policy()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var a = await SeedAsync(db);
        (await db.ScalarAsync<long>($"SELECT count(*) FROM {CurrentPartition}")).Should().Be(1);
        var readonlyLogin = await CoreSchemaDatabase.CreateLoginAsync(db.ConnectionString, "opportunity_readonly");
        var readonlyConnectionString = new NpgsqlConnectionStringBuilder(readonlyLogin) { Pooling = false }.ConnectionString;

        foreach (var connectionString in new[] { db.AppConnectionString, readonlyConnectionString })
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            await ExpectPermissionDenied(() => connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {CurrentPartition}"));
            await ExpectPermissionDenied(() => connection.ExecuteAsync($"DELETE FROM {CurrentPartition}"));
        }

        await ExpectPermissionDenied(() => db.InWorkspaceAsync(a.Id, tx => tx.Connection.ExecuteScalarAsync<long>(
            $"SELECT count(*) FROM {CurrentPartition}", transaction: tx.Transaction)));

        // Partitions created later by the maintenance function are locked down the same way.
        await using (var app = new NpgsqlConnection(db.AppConnectionString))
        {
            await app.OpenAsync(Ct);
            (await app.ExecuteScalarAsync<int>("SELECT opportunity.coding_event_ensure_partitions(now() + interval '30 months')")).Should().BeGreaterThan(0);
        }

        var future = "opportunity.coding_event_p" + DateTime.UtcNow.AddMonths(30).ToString("yyyyMM", CultureInfo.InvariantCulture);
        (await db.ScalarAsync<bool>($"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = '{future}'::regclass")).Should().BeTrue();
        (await db.ScalarAsync<bool>($"SELECT has_table_privilege('opportunity_app', '{future}', 'SELECT')")).Should().BeFalse();
    }

    [Theory]
    [InlineData("ALTER TABLE opportunity.document DISABLE ROW LEVEL SECURITY")]
    [InlineData("ALTER TABLE opportunity.document NO FORCE ROW LEVEL SECURITY")]
    [InlineData("DROP POLICY workspace_isolation ON opportunity.document")]
    [InlineData("CREATE POLICY open_door ON opportunity.document USING (true)")]
    [InlineData("ALTER TABLE opportunity.document OWNER TO CURRENT_USER")]
    [InlineData("ALTER ROLE CURRENT_USER BYPASSRLS")]
    [InlineData("SET ROLE postgres")]
    [InlineData("SET ROLE opportunity_readonly")]
    [InlineData("SET SESSION AUTHORIZATION postgres")]
    [InlineData("SET row_security = off; SELECT count(*) FROM opportunity.document")]
    [InlineData("TRUNCATE opportunity.document_coding_choice")]
    [InlineData("CREATE VIEW opportunity.leak AS SELECT * FROM opportunity.document")]
    [InlineData("SELECT opportunity.enable_workspace_rls('opportunity.document')")]
    public async Task The_app_role_cannot_disable_or_step_around_rls(string sql)
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var a = await SeedAsync(db);
        var appConnectionString = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Pooling = false }.ConnectionString;

        await using var connection = new NpgsqlConnection(appConnectionString);
        await connection.OpenAsync(Ct);
        await ExpectPermissionDenied(() => connection.ExecuteAsync(sql));

        // Objects the role may create (temp views and functions) run with its own rights, so RLS still applies.
        await connection.ExecuteAsync("CREATE TEMP VIEW peek AS SELECT * FROM opportunity.document");
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM peek")).Should().Be(0);
        (await connection.ExecuteScalarAsync<bool>("SELECT rolbypassrls OR rolsuper FROM pg_roles WHERE rolname = current_user")).Should().BeFalse();
        (await db.ScalarAsync<bool>("SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = 'opportunity.document'::regclass"))
            .Should().BeTrue();
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.document_coding_choice WHERE workspace_id = @ws", ("ws", a.Id))).Should().Be(1);
    }

    [Fact]
    public async Task A_non_superuser_owner_runs_the_migrator_and_is_itself_bound_by_forced_rls()
    {
        var superuser = await postgres.CreateDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(superuser).Database!;
        var ownerLogin = await CoreSchemaDatabase.CreateLoginAsync(superuser, null, "NOSUPERUSER NOBYPASSRLS CREATEROLE");
        var owner = new NpgsqlConnectionStringBuilder(ownerLogin) { Pooling = false }.ConnectionString;
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(superuser) { Pooling = false }.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            var ownerName = new NpgsqlConnectionStringBuilder(owner).Username;
            await admin.ExecuteAsync($"ALTER DATABASE {database} OWNER TO {ownerName}");
        }

        await using (var ownerSource = NpgsqlDataSource.Create(owner))
        {
            (await new PostgresMigrator(ownerSource).MigrateAsync(Ct)).EndVersion.Should().Be(MigrationCatalog.LatestVersion);
            (await new PostgresMigrator(ownerSource).MigrateAsync(Ct)).WasNoOp.Should().BeTrue();
        }

        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync(Ct);
        (await connection.ExecuteScalarAsync<bool>(
            "SELECT bool_and(c.relowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)) FROM pg_class c " +
            "WHERE c.relnamespace = 'opportunity'::regnamespace AND c.relkind IN ('r', 'p')")).Should().BeTrue();

        var ws = Guid.CreateVersion7();
        await using (var tx = await connection.BeginTransactionAsync(Ct))
        {
            await connection.ExecuteAsync("SELECT set_config('app.workspace_id', @ws, true)", new { ws = ws.ToString() }, tx);
            await connection.ExecuteAsync(
                "INSERT INTO opportunity.workspace (workspace_id, name, display_time_zone) VALUES (@ws, 'Owned', 'UTC')", new { ws }, tx);
            await connection.ExecuteAsync(
                "INSERT INTO opportunity.retired_control_number (workspace_id, control_number_norm, control_number, document_id) " +
                "VALUES (@ws, 'R1', 'R1', gen_random_uuid())", new { ws }, tx);
            (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM opportunity.retired_control_number", transaction: tx)).Should().Be(1);
            await tx.CommitAsync(Ct);
        }

        // FORCE ROW LEVEL SECURITY: even the owning login sees nothing without a context.
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM opportunity.retired_control_number")).Should().Be(0);
    }

    private static async Task<List<string>> TenantParentTablesAsync(CoreSchemaDatabase db) => await db.ColumnAsync(
        """
        SELECT c.relname::text FROM pg_class c
        JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'workspace_id'
        WHERE c.relnamespace = 'opportunity'::regnamespace AND c.relkind IN ('r', 'p') AND NOT c.relispartition
          AND c.relname <> 'workspace'
        ORDER BY 1
        """);

    /// <summary>
    /// A workspace with the initial field catalogue and default layout, documents, an object, a page set with a page and
    /// image, a retired number, a choice field and one coding write (state, choice row, write, event) on the first document.
    /// </summary>
    private static async Task<SeededWorkspace> SeedAsync(CoreSchemaDatabase db, int documents = 1)
    {
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var field = (await db.Fields.CreateFieldAsync(new NewField(ws, "Issues", FieldType.MultiChoice, FieldStorage.Coding), Ct)).Value!.FieldId;
        var choice = (await db.Fields.AddChoiceAsync(ws, field, "Pricing", Ct)).Value!.ChoiceId;
        var docs = new List<Guid>();
        for (var i = 1; i <= documents; i++)
        {
            docs.Add((await db.InsertDocumentAsync(ws, $"DOC{i:D4}")).DocumentId);
        }

        var objectId = await db.InsertStoredObjectAsync(ws, docs[0]);
        var pageSetId = await db.InsertPageSetAsync(ws, docs[0]);
        await db.ExecuteAsync(
            "INSERT INTO opportunity.page (workspace_id, page_set_id, ordinal, document_id, width_pt, height_pt, color_mode) VALUES (@ws, @ps, 1, @doc, 612, 792, 1);" +
            "INSERT INTO opportunity.page_image (workspace_id, page_set_id, ordinal, purpose, object_id, width_px, height_px, dpi_x, dpi_y, format) VALUES (@ws, @ps, 1, 1, @obj, 100, 100, 72, 72, 1);" +
            "INSERT INTO opportunity.retired_control_number (workspace_id, control_number_norm, control_number, document_id) VALUES (@ws, 'OLD1', 'OLD1', gen_random_uuid())",
            ("ws", ws), ("ps", pageSetId), ("doc", docs[0]), ("obj", objectId));

        var write = await db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "seed-" + Guid.CreateVersion7().ToString("N"),
            Actor = new CodingActor(Guid.CreateVersion7(), CodingActorType.Human),
            Documents = [new CodingTarget(docs[0])],
            Operations = [CodingFieldOperation.AddChoices(field, choice)],
        }, Ct);
        write.Outcome.Should().Be(CodingWriteOutcome.Applied);
        return new SeededWorkspace(ws, docs[0], field, choice);
    }

    private static async Task<List<string[]>> RowsAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetString)]);
        }

        return rows;
    }

    private static async Task ExpectRlsViolation(Func<Task> act)
    {
        var error = (await act.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        error.MessageText.Should().Contain("row-level security");
    }

    private static async Task ExpectPermissionDenied(Func<Task> act) =>
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

    private sealed record SeededWorkspace(Guid Id, Guid DocumentId, int FieldId, int ChoiceId);
}
