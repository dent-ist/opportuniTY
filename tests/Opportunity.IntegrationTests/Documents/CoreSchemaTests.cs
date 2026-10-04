using System.Diagnostics;
using System.Security.Cryptography;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Opportunity.Application.Documents;
using Opportunity.Core.Documents;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;
using Opportunity.Core.Workspaces;
using Opportunity.Data.Documents;
using Opportunity.Data.Migrations;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Documents;

/// <summary>E04-T02: workspace, document, object registry and page core schema.</summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class CoreSchemaTests(MigrationPostgresFixture postgres)
{
    /// <summary>
    /// Ceiling for loading 100K documents through staged binary COPY (container PostgreSQL 17, default settings).
    /// Recorded baseline 2026-10-02 on the development container: ~15 s end to end, of which ~6 s is the
    /// INSERT ... SELECT into the indexed table (~1.7 s of it the generated sort key). The ceiling leaves headroom for
    /// shared CI runners; a regression past it needs investigation, not a bigger number. Since E05-T03 the load runs as
    /// the RLS-bound app role; measured 2026-10-03 (3 alternating runs, noisy shared host): ~6.7 s bypassing RLS vs
    /// ~7.5 s under RLS (+10-15 %). The formal number belongs to E18-T03 (ADR-015 D7.4.2).
    /// </summary>
    private static readonly TimeSpan CopyBaselineCeiling = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_key_index_and_foreign_key_leads_with_workspace_id()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);

        var tables = await db.ColumnAsync(
            "SELECT relname::text FROM pg_class WHERE relnamespace = 'opportunity'::regnamespace AND relkind IN ('r', 'p') ORDER BY 1");
        tables.Should().Contain(["workspace", "document", "document_projection_state", "retired_control_number",
            "stored_object", "page_set", "page", "page_image"]);

        // P1, P2, P4: every primary key, unique constraint and secondary index leads with workspace_id (installation-level
        // tables, marked @global like the migrator's tenant-key lint, are exempt).
        var indexes = await db.ColumnAsync(
            """
            SELECT ic.relname::text
            FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = i.indkey[0]
            WHERE t.relnamespace = 'opportunity'::regnamespace AND a.attname <> 'workspace_id'
              AND coalesce(obj_description(t.oid, 'pg_class'), '') NOT LIKE '@global%'
            """);
        indexes.Should().BeEmpty();

        // P3: every foreign key is composite on workspace_id at both ends. A reference to an installation-level (@global)
        // table, such as the shared search index pool, has no workspace_id to include.
        var foreignKeys = await db.ColumnAsync(
            """
            SELECT c.conname::text
            FROM pg_constraint c
            JOIN pg_attribute fa ON fa.attrelid = c.conrelid AND fa.attnum = c.conkey[1]
            JOIN pg_attribute ta ON ta.attrelid = c.confrelid AND ta.attnum = c.confkey[1]
            WHERE c.connamespace = 'opportunity'::regnamespace AND c.contype = 'f'
              AND coalesce(obj_description(c.conrelid, 'pg_class'), '') NOT LIKE '@global%'
              AND coalesce(obj_description(c.confrelid, 'pg_class'), '') NOT LIKE '@global%'
              AND (fa.attname <> 'workspace_id' OR ta.attname <> 'workspace_id'
                   OR (cardinality(c.conkey) = 1 AND c.confrelid <> 'opportunity.workspace'::regclass))
            """);
        foreignKeys.Should().BeEmpty();

        // P5: no sequences on tenant tables; ADR-003 R12: no GIN index on document.metadata.
        (await db.ScalarAsync<long>("SELECT count(*) FROM pg_class WHERE relnamespace = 'opportunity'::regnamespace AND relkind = 'S'"))
            .Should().Be(0);
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'opportunity' AND indexdef ILIKE '%USING gin%'"))
            .Should().Be(0);

        await using var connection = await db.DataSource.OpenConnectionAsync(Ct);
        (await TenantKeyLint.FindViolationsAsync(connection, null, ["opportunity"], "workspace_id", Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Control_number_is_unique_on_its_normalized_form_within_a_workspace()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var workspaceA = await db.CreateWorkspaceAsync();
        var workspaceB = await db.CreateWorkspaceAsync();
        await db.InsertDocumentAsync(workspaceA, "abc0001");

        var duplicate = () => db.InsertDocumentAsync(workspaceA, " ABC0001\u00A0");

        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("document_control_number_norm_uq");
        var otherWorkspace = await db.InsertDocumentAsync(workspaceB, "ABC0001");
        otherWorkspace.ControlNumberNorm.Should().Be("ABC0001");

        // NFC: a decomposed spelling is the same control number as the composed one.
        var composed = await db.InsertDocumentAsync(workspaceA, "Re\u0301sume\u0301-1");
        composed.ControlNumberNorm.Should().Be("R\u00C9SUM\u00C9-1");
        var decomposedDuplicate = () => db.InsertDocumentAsync(workspaceA, "r\u00E9sum\u00E9-1");
        (await decomposedDuplicate.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("document_control_number_norm_uq");
    }

    [Fact]
    public async Task Database_rejects_a_control_number_norm_that_is_not_normalized()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var workspaceId = await db.CreateWorkspaceAsync();

        foreach (var badNorm in new[] { " ABC1", "ABC1 ", "ABC  1", "ABC\u00011" })
        {
            var act = () => db.InsertDocumentAsync(workspaceId, "X", d => d.ControlNumberNorm = badNorm);
            (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("document_control_number_norm_ck");
        }

        // Writers that bypass the repository cannot store a decomposed (non-NFC) form either.
        var decomposed = () => db.ExecuteAsync(
            "INSERT INTO opportunity.document (workspace_id, document_id, control_number, control_number_norm, family_id) VALUES (@ws, @id, 'x', @norm, @id)",
            ("ws", workspaceId), ("id", Guid.CreateVersion7()), ("norm", "E\u0301"));
        (await decomposed.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("document_control_number_norm_ck");
    }

    [Fact]
    public async Task Control_number_and_document_identity_are_immutable()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var workspaceId = await db.CreateWorkspaceAsync();
        var document = await db.InsertDocumentAsync(workspaceId, "ABC0001");

        foreach (var set in new[] { "control_number = 'ABC0002'", "control_number_norm = 'ABC0002'", "document_id = gen_random_uuid()" })
        {
            var act = () => db.ExecuteAsync(
                $"UPDATE opportunity.document SET {set} WHERE workspace_id = @ws AND document_id = @id",
                ("ws", workspaceId), ("id", document.DocumentId));
            (await act.Should().ThrowAsync<PostgresException>(set)).Which.SqlState.Should().Be(PostgresErrorCodes.IntegrityConstraintViolation);
        }

        // Re-assigning the same value and changing other columns is allowed.
        await db.ExecuteAsync(
            "UPDATE opportunity.document SET control_number = control_number, file_name = 'a.msg' WHERE workspace_id = @ws AND document_id = @id",
            ("ws", workspaceId), ("id", document.DocumentId));
        (await db.ScalarAsync<string>("SELECT control_number_sort_key FROM opportunity.document WHERE document_id = @id", ("id", document.DocumentId)))
            .Should().Be(ControlNumber.SortKey("ABC0001"));
    }

    [Fact]
    public async Task Retired_control_number_cannot_be_reused()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var workspaceId = await db.CreateWorkspaceAsync();
        await db.ExecuteAsync(
            "INSERT INTO opportunity.retired_control_number (workspace_id, control_number_norm, control_number, document_id) " +
            "VALUES (@ws, 'ABC0002', 'abc0002', gen_random_uuid())",
            ("ws", workspaceId));

        var reuse = () => db.InsertDocumentAsync(workspaceId, "abc0002");

        (await reuse.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        await db.InsertDocumentAsync(await db.CreateWorkspaceAsync(), "abc0002");
    }

    [Fact]
    public async Task Case_sensitivity_is_fixed_once_the_workspace_holds_a_document()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var workspaceId = await db.CreateWorkspaceAsync();
        const string toggle = "UPDATE opportunity.workspace SET control_number_case_sensitive = NOT control_number_case_sensitive WHERE workspace_id = @ws";
        await db.ExecuteAsync(toggle, ("ws", workspaceId));
        await db.InsertDocumentAsync(workspaceId, "ABC1");

        var act = () => db.ExecuteAsync(toggle, ("ws", workspaceId));

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.IntegrityConstraintViolation);
    }

    [Fact]
    public async Task Workspace_status_follows_the_adr014_lifecycle()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var workspaceId = await db.CreateWorkspaceAsync();

        foreach (var set in new[] { "status = 'Locked'", "status = 'Closed'", "closed_at = now()" })
        {
            var act = () => db.ExecuteAsync($"UPDATE opportunity.workspace SET {set} WHERE workspace_id = @ws", ("ws", workspaceId));
            (await act.Should().ThrowAsync<PostgresException>(set)).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        }

        await db.ExecuteAsync("UPDATE opportunity.workspace SET status = 'Closed', closed_at = now() WHERE workspace_id = @ws", ("ws", workspaceId));
        await db.ExecuteAsync("UPDATE opportunity.workspace SET status = 'Active', closed_at = NULL WHERE workspace_id = @ws", ("ws", workspaceId));
    }

    [Fact]
    public async Task Family_integrity_is_enforced_by_the_database()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var parent = await db.InsertDocumentAsync(ws, "ABC0001");
        var child = await db.InsertDocumentAsync(ws, "ABC0002", d => AttachTo(d, parent, parent, 1));
        await db.InsertDocumentAsync(ws, "ABC0003", d => AttachTo(d, parent, child, 2)); // nested: immediate parent is the child
        var other = await db.InsertDocumentAsync(ws, "XYZ0001");

        var cases = new (string Name, Action<Document> Configure, string Constraint)[]
        {
            ("sequence 0 that is not the family root", d => { d.FamilyId = parent.DocumentId; d.FamilySequence = 0; }, "document_family_root_ck"),
            ("member without a parent", d => { d.FamilyId = parent.DocumentId; d.FamilySequence = 5; }, "document_family_root_ck"),
            ("duplicate family sequence", d => AttachTo(d, parent, parent, 1), "document_family_sequence_uq"),
            ("parent from another family", d => AttachTo(d, parent, other, 6), "document_parent_fk"),
            ("family id that is not a root", d => AttachTo(d, child, child, 1), "document_family_root_fk"),
            ("family root that does not exist", d => { d.FamilyId = Guid.CreateVersion7(); d.FamilySequence = 1; d.ParentDocumentId = d.FamilyId; }, "document_family_root_fk"),
        };

        foreach (var (name, configure, constraint) in cases)
        {
            var act = () => db.InsertDocumentAsync(ws, "BAD-" + name, configure);
            (await act.Should().ThrowAsync<PostgresException>(name)).Which.ConstraintName.Should().Be(constraint, name);
        }

        // Family resolution (E09-T01) re-links whole families in one transaction with deferred constraints.
        await using var connection = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await connection.BeginTransactionAsync(Ct);
        await using var relink = new NpgsqlCommand(
            """
            SET CONSTRAINTS ALL DEFERRED;
            UPDATE opportunity.document SET family_id = @other, family_sequence = family_sequence + 1, parent_document_id = coalesce(parent_document_id, @other)
            WHERE workspace_id = @ws AND family_id = @parent;
            """, connection, tx);
        relink.Parameters.AddWithValue("ws", ws);
        relink.Parameters.AddWithValue("parent", parent.DocumentId);
        relink.Parameters.AddWithValue("other", other.DocumentId);
        await relink.ExecuteNonQueryAsync(Ct);
        await tx.CommitAsync(Ct);
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws AND family_id = @f", ("ws", ws), ("f", other.DocumentId)))
            .Should().Be(4);
    }

    [Fact]
    public async Task A_row_referencing_a_document_of_another_workspace_fails_on_foreign_key()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var workspaceA = await db.CreateWorkspaceAsync();
        var workspaceB = await db.CreateWorkspaceAsync();
        var documentA = await db.InsertDocumentAsync(workspaceA, "A-0001");
        var documentB = await db.InsertDocumentAsync(workspaceB, "B-0001");

        await AssertForeignKeyViolation(() => db.InsertPageSetAsync(workspaceB, documentA.DocumentId), "page_set_document_fk");
        await AssertForeignKeyViolation(() => db.InsertStoredObjectAsync(workspaceB, documentA.DocumentId), "stored_object_document_fk");
        await AssertForeignKeyViolation(
            () => db.ExecuteAsync(
                "INSERT INTO opportunity.document_projection_state (workspace_id, document_id) VALUES (@ws, @doc)",
                ("ws", workspaceB), ("doc", documentA.DocumentId)),
            "document_projection_state_document_fk");
        await AssertForeignKeyViolation(
            () => db.InsertDocumentAsync(workspaceB, "B-0002", d => AttachTo(d, documentA, documentA, 1)),
            "document_family_root_fk");

        // A document cannot point at an object or page set of another workspace (or of another document).
        var objectOfA = await db.InsertStoredObjectAsync(workspaceA, documentA.DocumentId);
        var pageSetOfA = await db.InsertPageSetAsync(workspaceA, documentA.DocumentId);
        documentB.NativeObjectId = objectOfA;
        await AssertForeignKeyViolation(() => db.Documents.UpdateAsync(documentB, cancellationToken: Ct), "document_native_object_fk");
        documentB.NativeObjectId = null;
        documentB.ActivePageSetId = pageSetOfA;
        await AssertForeignKeyViolation(() => db.Documents.UpdateAsync(documentB, cancellationToken: Ct), "document_active_page_set_fk");

        // Object keys are confined to the owning workspace's prefix (ADR-011 §1.4).
        var foreignKey = () => db.InsertStoredObjectAsync(workspaceB, documentB.DocumentId, keyWorkspaceId: workspaceA);
        (await foreignKey.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("stored_object_logical_key_ck");

        // The same references inside one workspace succeed.
        var pageSetOfB = await db.InsertPageSetAsync(workspaceB, documentB.DocumentId);
        documentB.ActivePageSetId = pageSetOfB;
        documentB.NativeObjectId = await db.InsertStoredObjectAsync(workspaceB, documentB.DocumentId);
        (await db.Documents.UpdateAsync(documentB, cancellationToken: Ct)).Outcome.Should().Be(DocumentWriteOutcome.Updated);
        await db.ExecuteAsync(
            "INSERT INTO opportunity.page (workspace_id, page_set_id, ordinal, document_id, width_pt, height_pt, color_mode) VALUES (@ws, @ps, 1, @doc, 612, 792, 1)",
            ("ws", workspaceB), ("ps", pageSetOfB), ("doc", documentB.DocumentId));
        await AssertForeignKeyViolation(
            () => db.ExecuteAsync(
                "INSERT INTO opportunity.page (workspace_id, page_set_id, ordinal, document_id, width_pt, height_pt, color_mode) VALUES (@ws, @ps, 1, @doc, 612, 792, 1)",
                ("ws", workspaceB), ("ps", pageSetOfA), ("doc", documentA.DocumentId)),
            "page_page_set_fk");
        await AssertForeignKeyViolation(
            () => db.ExecuteAsync(
                "INSERT INTO opportunity.page_image (workspace_id, page_set_id, ordinal, purpose, object_id, width_px, height_px, dpi_x, dpi_y, format) VALUES (@ws, @ps, 1, 1, @obj, 2550, 3300, 300, 300, 1)",
                ("ws", workspaceB), ("ps", pageSetOfB), ("obj", objectOfA)),
            "page_image_object_fk");
    }

    [Fact]
    public async Task DocumentVersion_increments_exactly_once_per_write_that_changes_a_projected_field()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var document = await db.InsertDocumentAsync(ws, "ABC0001", d => d.Metadata = """{"f1001": "Alice", "f1002": [1, 2]}""");
        (await db.Documents.GetVersionAsync(ws, document.DocumentId, Ct)).Should().Be(1);

        document.FileName = "memo.msg";
        (await db.Documents.UpdateAsync(document, cancellationToken: Ct)).Should().Be(new DocumentWriteResult(DocumentWriteOutcome.Updated, 2));

        // Re-applying identical values (an overlay with no differences) does not bump.
        (await db.Documents.UpdateAsync(document, cancellationToken: Ct)).Should().Be(new DocumentWriteResult(DocumentWriteOutcome.Unchanged, 2));

        // JSONB is compared semantically: key order and whitespace are not changes.
        document.Metadata = """{ "f1002":[1,2],  "f1001":"Alice" }""";
        (await db.Documents.UpdateAsync(document, cancellationToken: Ct)).Should().Be(new DocumentWriteResult(DocumentWriteOutcome.Unchanged, 2));

        // Raw strings are stored but are not a projection input.
        document.MetadataRaw = """{"f1003": {"raw": "03/01/2025", "fmt": "MM/dd/yyyy"}}""";
        (await db.Documents.UpdateAsync(document, cancellationToken: Ct)).Should().Be(new DocumentWriteResult(DocumentWriteOutcome.Unchanged, 2));
        (await db.ScalarAsync<string>("SELECT metadata_raw->'f1003'->>'raw' FROM opportunity.document WHERE document_id = @id", ("id", document.DocumentId)))
            .Should().Be("03/01/2025");

        // If-Match: a stale expected version writes nothing.
        document.Metadata = """{"f1001": "Bob"}""";
        (await db.Documents.UpdateAsync(document, expectedVersion: 1, Ct)).Should().Be(new DocumentWriteResult(DocumentWriteOutcome.VersionConflict, 2));
        (await db.ScalarAsync<string>("SELECT metadata->>'f1001' FROM opportunity.document WHERE document_id = @id", ("id", document.DocumentId)))
            .Should().Be("Alice");
        (await db.Documents.UpdateAsync(document, expectedVersion: 2, Ct)).Should().Be(new DocumentWriteResult(DocumentWriteOutcome.Updated, 3));

        // Writes addressed to another workspace never reach the document.
        var other = await db.CreateWorkspaceAsync();
        var foreign = Clone(document);
        foreign.WorkspaceId = other;
        foreign.FileName = "elsewhere";
        (await db.Documents.UpdateAsync(foreign, cancellationToken: Ct)).Outcome.Should().Be(DocumentWriteOutcome.NotFound);
        (await db.Documents.GetVersionAsync(other, document.DocumentId, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Every_projected_field_change_bumps_DocumentVersion()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var parent = await db.InsertDocumentAsync(ws, "P-0001");
        var document = await db.InsertDocumentAsync(ws, "D-0001");
        var objectId = await db.InsertStoredObjectAsync(ws, document.DocumentId);
        var textObjectId = await db.InsertStoredObjectAsync(ws, document.DocumentId);
        var pageSetId = await db.InsertPageSetAsync(ws, document.DocumentId);
        var instant = new DateTimeOffset(2025, 3, 1, 14, 5, 0, TimeSpan.FromHours(-5));

        // Group and thread rows exist before a document references them (V0016 foreign keys).
        var groupId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        await db.ExecuteAsync(
            """
            INSERT INTO opportunity.duplicate_group (workspace_id, duplicate_group_id, source, hash_kind, hash_value) VALUES (@ws, @g, 1, 1, 'DG-1');
            INSERT INTO opportunity.email_thread (workspace_id, email_thread_id, source, thread_key) VALUES (@ws, @t, 1, 'T-1');
            """,
            ("ws", ws), ("g", groupId), ("t", threadId));

        var changes = new (string Field, Action<Document> Change)[]
        {
            ("BegBates", d => d.BegBates = "ABC000001"),
            ("EndBates", d => d.EndBates = "ABC000002"),
            ("BegAttach", d => d.BegAttach = "D-0001"),
            ("EndAttach", d => d.EndAttach = "D-0003"),
            ("FamilyStatus", d => d.FamilyStatus = FamilyStatus.Resolved),
            ("DuplicateGroupId", d => d.DuplicateGroupId = groupId),
            ("IsDuplicatePrimary", d => d.IsDuplicatePrimary = true),
            ("EmailThreadId", d => { d.EmailThreadId = threadId; d.EmailThreadSource = EmailThreadSource.Upstream; }),
            ("EmailThreadSource", d => d.EmailThreadSource = EmailThreadSource.ConversationIndex),
            ("Md5", d => d.Md5 = new byte[16]),
            ("Sha1", d => d.Sha1 = new byte[20]),
            ("Sha256", d => d.Sha256 = SHA256.HashData([1])),
            ("Sha256 bytes", d => d.Sha256 = SHA256.HashData([2])),
            ("UpstreamDedupeHash", d => { d.UpstreamDedupeHash = "0a1b2c"; d.UpstreamDedupeHashKind = DuplicateHashKind.UpstreamDedupeHash; }),
            ("UpstreamDedupeHashKind", d => d.UpstreamDedupeHashKind = DuplicateHashKind.UpstreamEmailHash),
            ("FileName", d => d.FileName = "a.msg"),
            ("FileExtension", d => d.FileExtension = "msg"),
            ("FileType", d => d.FileType = "Email"),
            ("MimeType", d => d.MimeType = "application/vnd.ms-outlook"),
            ("FileSize", d => d.FileSize = 1024),
            ("PageCount", d => d.PageCount = 3),
            ("DateSent", d => d.DateSent = instant),
            ("DateReceived", d => d.DateReceived = instant),
            ("DateCreated", d => d.DateCreated = instant),
            ("DateLastModified", d => d.DateLastModified = instant),
            ("DocumentDate", d => { d.DocumentDate = instant; d.DocumentDateSource = DocumentDateSource.DateSent; }),
            ("DocumentDateSource", d => d.DocumentDateSource = DocumentDateSource.Upstream),
            ("FamilyDate", d => d.FamilyDate = instant),
            ("NativeObjectId", d => d.NativeObjectId = objectId),
            ("TextObjectId", d => d.TextObjectId = textObjectId),
            ("TextLength", d => d.TextLength = 42),
            ("ActivePageSetId", d => d.ActivePageSetId = pageSetId),
            ("TextTruncated", d => d.TextTruncated = true),
            ("TextMissing", d => d.TextMissing = true),
            ("NativeMissing", d => d.NativeMissing = true),
            ("ImagesIncomplete", d => d.ImagesIncomplete = true),
            ("TextEncodingWarning", d => d.TextEncodingWarning = true),
            ("Metadata", d => d.Metadata = """{"f1001": "x"}"""),
            ("Metadata value", d => d.Metadata = """{"f1001": "y"}"""),
            ("Family", d => AttachTo(d, parent, parent, 1)),
            ("Clear a value", d => d.BegBates = null),
        };

        var expected = 1L;
        foreach (var (field, change) in changes)
        {
            change(document);
            expected++;
            (await db.Documents.UpdateAsync(document, cancellationToken: Ct)).Should()
                .Be(new DocumentWriteResult(DocumentWriteOutcome.Updated, expected), field);
            (await db.Documents.UpdateAsync(document, cancellationToken: Ct)).Outcome.Should()
                .Be(DocumentWriteOutcome.Unchanged, field + " re-applied");
        }

        // Family sources are stored but not projected: family resolution writes the projected family columns (E09-T01).
        string[] familySources = ["BegAttachNorm", "EndAttachNorm", "ParentIdNorm", "GroupIdentifier", "AttachmentIdsNorm", "UpstreamFamilyDate"];
        document.ParentIdNorm = "D-0000";
        document.AttachmentIdsNorm = ["D-0002"];
        (await db.Documents.UpdateAsync(document, cancellationToken: Ct)).Should()
            .Be(new DocumentWriteResult(DocumentWriteOutcome.Unchanged, expected), "family sources are not projection inputs");

        // Every mutable property of Document is covered above (MetadataRaw and the family sources are non-projected).
        var covered = changes.Select(c => c.Field.Split(' ')[0]).Concat(["FamilyId", "ParentDocumentId", "FamilySequence", "MetadataRaw", .. familySources]);
        var mutable = typeof(Document).GetProperties().Select(p => p.Name)
            .Except(["WorkspaceId", "DocumentId", "ControlNumber", "ControlNumberNorm", "ControlNumberSortKey",
                "FirstImportBatchId", "CreatedAt", "UpdatedAt"]);
        mutable.Should().BeSubsetOf(covered);
    }

    [Fact]
    public async Task Natural_control_number_order_is_available_for_sorting()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        foreach (var controlNumber in new[] { "ABC10", "abc9", "ABC0011", "ABC1", "ABC9.10", "ABC9.2", "ABD1", "AB" })
        {
            await db.InsertDocumentAsync(ws, controlNumber);
        }

        var all = await db.Reads.ListByControlNumberAsync(ws, null, 100, Ct);

        all.Select(d => d.ControlNumber).Should().Equal("AB", "ABC1", "abc9", "ABC9.2", "ABC9.10", "ABC10", "ABC0011", "ABD1");
        all.Should().OnlyContain(d => d.DocumentVersion == 1);

        // Keyset pages concatenate to the same order.
        var paged = new List<DocumentListItem>();
        ControlNumberCursor? cursor = null;
        while (true)
        {
            var page = await db.Reads.ListByControlNumberAsync(ws, cursor, 3, Ct);
            if (page.Count == 0)
            {
                break;
            }

            paged.AddRange(page);
            cursor = new ControlNumberCursor(page[^1].ControlNumberSortKey, page[^1].DocumentId);
        }

        paged.Select(d => d.DocumentId).Should().Equal(all.Select(d => d.DocumentId));

        // The database sort key is the same function as the C# one (projected to search, ADR-007).
        foreach (var norm in new[] { "ABC9", "A1B22C333", "X" + new string('7', 25), "2024-01-02 V0003", "\u00C4BC9" })
        {
            (await db.ScalarAsync<string>("SELECT opportunity.control_number_sort_key(@n)", ("n", norm)))
                .Should().Be(ControlNumber.SortKey(norm), norm);
        }
    }

    [Fact]
    public async Task Inserting_100K_documents_via_COPY_completes_within_the_recorded_baseline()
    {
        const int count = 100_000;
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var documents = Enumerable.Range(1, count).Select(i =>
        {
            var d = Document.Create(ws, $"ABC{i:D7}", caseSensitive: false);
            d.FileName = $"file-{i}.msg";
            d.FileSize = 1000 + i;
            d.Sha256 = SHA256.HashData(BitConverter.GetBytes(i));
            d.DateSent = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i);
            d.Metadata = $$"""{"f1001": "Custodian {{i % 50}}", "f1002": ["a", "b"]}""";
            return d;
        }).ToList();

        var stopwatch = Stopwatch.StartNew();
        await db.Documents.InsertManyAsync(ws, documents, Ct);
        stopwatch.Stop();

        TestContext.Current.SendDiagnosticMessage($"COPY baseline: {count:N0} documents in {stopwatch.Elapsed.TotalSeconds:F2} s");
        TestContext.Current.TestOutputHelper?.WriteLine($"COPY baseline: {count:N0} documents in {stopwatch.Elapsed.TotalSeconds:F2} s");
        stopwatch.Elapsed.Should().BeLessThan(CopyBaselineCeiling);
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ("ws", ws))).Should().Be(count);
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.document_projection_state WHERE workspace_id = @ws AND document_version = 1", ("ws", ws)))
            .Should().Be(count);
        (await db.ScalarAsync<string>(
            "SELECT control_number_sort_key FROM opportunity.document WHERE workspace_id = @ws AND control_number_norm = 'ABC0000042'", ("ws", ws)))
            .Should().Be(ControlNumber.SortKey("ABC0000042"));
    }

    [Fact]
    public async Task Entities_map_with_EF_Core()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = Guid.CreateVersion7();
        await db.InDbContextAsync(ws, async context =>
        {
            context.Workspaces.Add(new Workspace
            {
                WorkspaceId = ws,
                Name = "Acme v. Widget",
                MatterNumber = "2026-001",
                DisplayTimeZone = "Europe/London",
                ControlNumberCaseSensitive = true,
            });
            await context.SaveChangesAsync(Ct);
        });

        var sent = new DateTimeOffset(2025, 3, 1, 14, 5, 0, TimeSpan.Zero);
        var document = await db.InsertDocumentAsync(ws, "abc0001", d =>
        {
            d.DateSent = sent;
            d.DocumentDate = sent;
            d.DocumentDateSource = DocumentDateSource.DateSent;
            d.Sha256 = SHA256.HashData([7]);
            d.Metadata = """{"f1001": "Alice"}""";
        });
        var objectId = await db.InsertStoredObjectAsync(ws, document.DocumentId);
        var pageSetId = await db.InsertPageSetAsync(ws, document.DocumentId);

        await db.InDbContextAsync(ws, async context =>
        {
            context.Pages.Add(new Page
            {
                WorkspaceId = ws,
                PageSetId = pageSetId,
                Ordinal = 1,
                DocumentId = document.DocumentId,
                ImageKey = "ABC0001",
                WidthPt = 595.28m,
                HeightPt = 841.89m,
                Rotation = 90,
                ColorMode = PageColorMode.Bitonal,
            });
            context.PageImages.Add(new PageImage
            {
                WorkspaceId = ws,
                PageSetId = pageSetId,
                Ordinal = 1,
                Purpose = PageImagePurpose.Original,
                ObjectId = objectId,
                WidthPx = 2480,
                HeightPx = 3508,
                DpiX = 300,
                DpiY = 300,
                Format = PageImageFormat.TiffG4,
            });
            await context.SaveChangesAsync(Ct);
        });

        await db.InDbContextAsync(ws, async context =>
        {
            var workspace = await context.Workspaces.SingleAsync(w => w.WorkspaceId == ws, Ct);
            workspace.Status.Should().Be(WorkspaceStatus.Active);
            workspace.Epoch.Should().Be(1);
            workspace.CreatedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));

            var loaded = await context.Documents.AsNoTracking().SingleAsync(d => d.WorkspaceId == ws && d.DocumentId == document.DocumentId, Ct);
            loaded.ControlNumber.Should().Be("abc0001");
            loaded.ControlNumberNorm.Should().Be("ABC0001");
            loaded.ControlNumberSortKey.Should().Be(ControlNumber.SortKey("ABC0001"));
            loaded.FamilyId.Should().Be(document.DocumentId);
            loaded.DateSent.Should().Be(sent);
            loaded.DocumentDateSource.Should().Be(DocumentDateSource.DateSent);
            loaded.Sha256.Should().Equal(document.Sha256);
            loaded.Metadata.Should().Contain("Alice");

            (await context.DocumentProjectionStates.SingleAsync(s => s.DocumentId == document.DocumentId, Ct)).DocumentVersion.Should().Be(1);
            (await context.StoredObjects.SingleAsync(o => o.ObjectId == objectId, Ct)).Area.Should().Be(ObjectArea.Native);
            (await context.PageSets.SingleAsync(p => p.PageSetId == pageSetId, Ct)).Source.Should().Be(PageSetSource.Imported);
            var page = await context.Pages.SingleAsync(p => p.PageSetId == pageSetId, Ct);
            page.WidthPt.Should().Be(595.28m);
            page.ColorMode.Should().Be(PageColorMode.Bitonal);
            (await context.PageImages.SingleAsync(p => p.PageSetId == pageSetId, Ct)).Format.Should().Be(PageImageFormat.TiffG4);

            // Document writes must go through the repository so DocumentVersion is maintained.
            var tracked = await context.Documents.SingleAsync(d => d.DocumentId == document.DocumentId, Ct);
            tracked.FileName = "bypass.msg";
            var bypass = () => context.SaveChangesAsync(Ct);
            await bypass.Should().ThrowAsync<InvalidOperationException>();
        });
    }

    private static void AttachTo(Document document, Document familyRoot, Document parent, int sequence)
    {
        document.FamilyId = familyRoot.DocumentId;
        document.ParentDocumentId = parent.DocumentId;
        document.FamilySequence = sequence;
    }

    private static Document Clone(Document document)
    {
        var copy = new Document();
        foreach (var property in typeof(Document).GetProperties().Where(p => p.CanWrite))
        {
            property.SetValue(copy, property.GetValue(document));
        }

        return copy;
    }

    private static async Task AssertForeignKeyViolation(Func<Task> act, string constraint)
    {
        var error = (await act.Should().ThrowAsync<PostgresException>(constraint)).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation, constraint);
        error.ConstraintName.Should().Be(constraint);
    }
}
