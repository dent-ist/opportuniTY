using System.Text.Json.Nodes;
using AwesomeAssertions;
using Npgsql;
using Opportunity.Application.Documents;
using Opportunity.Application.Fields;
using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Core.SearchWork;
using Opportunity.Data;
using Opportunity.Data.Documents;
using Opportunity.Data.Relationships;
using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Import.Mapping;
using Opportunity.IntegrationTests.Migrations;
using FieldType = Opportunity.Core.Fields.FieldType;

namespace Opportunity.IntegrationTests.Documents;

/// <summary>
/// E09-T02: upstream duplicate groups and email threads flow from mapped load-file rows into PostgreSQL inside the
/// import chunk's transaction, under row-level security (app login), and match the generator's ground truth.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class UpstreamRelationshipTests(MigrationPostgresFixture postgres)
{
    private const ulong Seed = 20261003;
    private const int ChunkSize = 40;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Upstream_groups_and_threads_from_a_generated_volume_match_ground_truth_across_chunks()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var relationships = new DocumentRelationshipRepository(db.AppDataSource);

        var profile = ProfileSerializer.WithDocumentCount(new CorpusProfile(), 300);
        List<GeneratedDocument> truth = [.. new CorpusGenerator(profile, Seed, 1).GenerateDocuments()];
        truth.Should().Contain(d => d.DuplicateGroupId != null).And.Contain(d => d.EmailThreadId != null);

        var catalog = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        var mapping = MappingCompiler.Compile(null, [.. VolumeWriter.StructuralColumns], catalog);
        mapping.Issues.Where(i => i.Severity == MappingIssueSeverity.Error).Should().BeEmpty();

        var formatter = new DatValueFormatter(new VolumeOptions());
        var row = 0L;
        foreach (var chunk in truth.Chunk(ChunkSize))
        {
            var documents = new List<Document>();
            var groups = new List<DuplicateGroupKey>();
            var threads = new List<EmailThreadKey>();
            foreach (var doc in chunk)
            {
                var mapped = mapping.Map(++row, LoadFileRow(doc, formatter));
                mapped.HasErrors.Should().BeFalse();
                var document = Document.Create(ws, mapped.ControlNumber!, caseSensitive: false);
                document.Metadata = Metadata(mapped).ToJsonString();
                var result = UpstreamRelationships.Apply(
                    document, UpstreamRelationshipExtractor.Extract(mapping, mapped), UpstreamRelationshipExtractor.Options(mapping));
                if (result.DuplicateGroup is { } group)
                {
                    groups.Add(group);
                }

                if (result.EmailThread is { } thread)
                {
                    threads.Add(thread);
                }

                documents.Add(document);
            }

            // One import chunk: documents, then their relationships, in one workspace transaction.
            await db.InWorkspaceAsync(ws, async tx =>
            {
                await DocumentRepository.InsertManyAsync(tx, documents, Ct);
                var sync = await RelationshipWriter.SyncAsync(tx, new RelationshipSync(groups, threads, [.. documents.Select(d => d.DocumentId)]), Ct);
                sync.OtherChangedDocuments.Should().BeEmpty("later chunks hold higher control numbers, so no earlier primary changes");
            });
        }

        var stored = await StoredAsync(db, ws);
        foreach (var doc in truth)
        {
            var actual = stored[doc.ControlNumber];
            actual.Group.Should().Be(
                doc.DuplicateGroupId is { } g ? RelationshipIds.DuplicateGroup(ws, DuplicateHashKind.UpstreamGroup, g) : (Guid?)null, doc.ControlNumber);
            actual.Thread.Should().Be(
                doc.EmailThreadId is { } t ? RelationshipIds.EmailThread(ws, EmailThreadSource.Upstream, t) : (Guid?)null, doc.ControlNumber);
            actual.AllCustodians.Should().Equal(doc.AllCustodians, "AllCustodians is stored verbatim as a multi-value system field");
        }

        // Every group: the ground-truth size, the lowest control number as primary (no family dates are loaded here).
        foreach (var members in truth.Where(d => d.DuplicateGroupId != null).GroupBy(d => d.DuplicateGroupId!))
        {
            var info = (await relationships.GetDuplicateGroupAsync(ws, RelationshipIds.DuplicateGroup(ws, DuplicateHashKind.UpstreamGroup, members.Key), Ct))!;
            info.Source.Should().Be(DuplicateGroupSource.Upstream);
            info.HashKind.Should().Be(DuplicateHashKind.UpstreamGroup);
            info.HashValue.Should().Be(members.Key);
            info.MemberCount.Should().Be(members.Count());
            var primary = members.MinBy(d => ControlNumber.SortKey(ControlNumber.Normalize(d.ControlNumber, false, null)), StringComparer.Ordinal)!;
            info.PrimaryDocumentId.Should().Be(stored[primary.ControlNumber].DocumentId);
            members.Select(d => stored[d.ControlNumber].Primary).Should().ContainSingle(p => p, "each member is its own family until E09-T01 links families");
        }

        stored.Values.Where(s => s.Group is null).Should().OnlyContain(s => !s.Primary);
        foreach (var members in truth.Where(d => d.EmailThreadId != null).GroupBy(d => d.EmailThreadId!))
        {
            (await relationships.GetEmailThreadAsync(ws, RelationshipIds.EmailThread(ws, EmailThreadSource.Upstream, members.Key), Ct))!
                .MemberCount.Should().Be(members.Count());
        }

        var report = await relationships.CheckConsistencyAsync(ws, cancellationToken: Ct);
        report.IsConsistent.Should().BeTrue(string.Join("; ", report.Findings.Where(f => f.Count > 0).Select(f => $"{f.Kind}: {string.Join(", ", f.Sample)}")));
        report.DuplicateGroups.Should().Be(truth.Where(d => d.DuplicateGroupId != null).Select(d => d.DuplicateGroupId).Distinct().LongCount());
        report.DocumentsInEmailThreads.Should().Be(truth.Count(d => d.EmailThreadId != null));
    }

    [Fact]
    public async Task A_lower_control_number_arriving_later_takes_over_as_primary_and_the_old_primary_is_reindexed()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var relationships = new DocumentRelationshipRepository(db.AppDataSource);

        var later = await ImportAsync(db, ws, ("ABC0002", "DG-1", "T-1"), ("ABC0003", "DG-1", null));
        later.Single(d => d.ControlNumber == "ABC0002").DocumentId.Should().NotBeEmpty();
        (await PrimaryAsync(db, ws, "ABC0002")).Should().BeTrue();
        var versionBefore = await db.Documents.GetVersionAsync(ws, later[0].DocumentId, Ct);

        // A second volume brings an earlier copy: it becomes primary, ABC0002 loses the flag and gets search work.
        var earlier = Document.Create(ws, "ABC0001", caseSensitive: false);
        var applied = UpstreamRelationships.Apply(earlier, new UpstreamRelationshipValues { DuplicateGroup = "DG-1" });
        RelationshipSyncResult? sync = null;
        await db.InWorkspaceAsync(ws, async tx =>
        {
            await DocumentRepository.InsertManyAsync(tx, [earlier], Ct);
            sync = await RelationshipWriter.SyncAsync(tx, new RelationshipSync([applied.DuplicateGroup!], [], [earlier.DocumentId]), Ct);
        });

        sync!.PrimaryFlagChanges.Should().Be(2);
        sync.OtherChangedDocuments.Should().Equal((later[0].DocumentId, versionBefore!.Value + 1));
        (await PrimaryAsync(db, ws, "ABC0001")).Should().BeTrue();
        (await PrimaryAsync(db, ws, "ABC0002")).Should().BeFalse();
        var group = (await relationships.GetDuplicateGroupAsync(ws, applied.DuplicateGroup!.DuplicateGroupId, Ct))!;
        group.MemberCount.Should().Be(3);
        group.PrimaryDocumentId.Should().Be(earlier.DocumentId);
        (await relationships.GetDuplicateMembersAsync(ws, group.DuplicateGroupId, 10, Ct))[0].Should().Be(earlier.DocumentId, "the primary is listed first");

        // The repository path (own transaction) also creates the SearchOutbox rows for the documents it changed.
        var outboxBefore = await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws", ("ws", ws));
        await db.ExecuteAsync("UPDATE opportunity.document SET is_duplicate_primary = true WHERE workspace_id = @ws AND control_number = 'ABC0003'", ("ws", ws));
        var repaired = await relationships.RecomputeAllAsync(ws, Ct);
        repaired.OtherChangedDocuments.Should().ContainSingle();
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND change_mask = @mask", ("ws", ws), ("mask", (short)SearchChangeMask.Relationships)))
            .Should().Be(outboxBefore + 1);
    }

    [Fact]
    public async Task An_overlay_that_moves_a_document_recomputes_and_removes_the_previous_group_and_thread()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var relationships = new DocumentRelationshipRepository(db.AppDataSource);
        var docs = await ImportAsync(db, ws, ("ABC0001", "DG-OLD", "T-OLD"));
        var oldGroup = docs[0].DuplicateGroupId!.Value;
        var oldThread = docs[0].EmailThreadId!.Value;

        var document = docs[0];
        var applied = UpstreamRelationships.Apply(document, new UpstreamRelationshipValues { DuplicateGroup = "DG-NEW", EmailThreadGroup = "T-NEW" });
        await db.InWorkspaceAsync(ws, async tx =>
        {
            await using (var update = tx.Command(
                "UPDATE opportunity.document SET duplicate_group_id = @g, email_thread_id = @t WHERE workspace_id = @ws AND document_id = @id"))
            {
                update.Parameters.AddWithValue("g", document.DuplicateGroupId!.Value);
                update.Parameters.AddWithValue("t", document.EmailThreadId!.Value);
                update.Parameters.AddWithValue("ws", ws);
                update.Parameters.AddWithValue("id", document.DocumentId);
                await update.ExecuteNonQueryAsync(Ct);
            }

            await RelationshipWriter.SyncAsync(tx, new RelationshipSync(
                [applied.DuplicateGroup!], [applied.EmailThread!], [document.DocumentId], [oldGroup], [oldThread]), Ct);
        });

        (await relationships.GetDuplicateGroupAsync(ws, oldGroup, Ct)).Should().BeNull("a group nothing references is removed");
        (await relationships.GetEmailThreadAsync(ws, oldThread, Ct)).Should().BeNull();
        (await relationships.GetDuplicateGroupAsync(ws, applied.DuplicateGroup!.DuplicateGroupId, Ct))!.MemberCount.Should().Be(1);
        (await relationships.CheckConsistencyAsync(ws, cancellationToken: Ct)).IsConsistent.Should().BeTrue();
    }

    [Fact]
    public async Task A_document_cannot_commit_a_group_or_thread_the_writer_did_not_record()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();

        var orphan = Document.Create(ws, "ABC0001", caseSensitive: false);
        UpstreamRelationships.Apply(orphan, new UpstreamRelationshipValues { DuplicateGroup = "DG-1" });
        var act = () => db.InWorkspaceAsync(ws, tx => DocumentRepository.InsertManyAsync(tx, [orphan], Ct));
        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("document_duplicate_group_fk");

        var threaded = Document.Create(ws, "ABC0002", caseSensitive: false);
        UpstreamRelationships.Apply(threaded, new UpstreamRelationshipValues { EmailThreadGroup = "T-1" });
        act = () => db.InWorkspaceAsync(ws, tx => DocumentRepository.InsertManyAsync(tx, [threaded], Ct));
        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("document_email_thread_fk");

        var hashOnly = Document.Create(ws, "ABC0003", caseSensitive: false);
        hashOnly.UpstreamDedupeHash = "0a1b";
        act = () => db.Documents.InsertAsync(hashOnly, Ct);
        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("document_upstream_dedupe_hash_kind_ck");
    }

    [Fact]
    public async Task Relationship_rows_are_confined_to_their_workspace_by_row_level_security()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var other = await db.CreateWorkspaceAsync();
        var docs = await ImportAsync(db, ws, ("ABC0001", "DG-1", "T-1"));
        var relationships = new DocumentRelationshipRepository(db.AppDataSource);

        (await relationships.GetDuplicateGroupAsync(other, docs[0].DuplicateGroupId!.Value, Ct)).Should().BeNull();
        (await relationships.GetEmailThreadAsync(other, docs[0].EmailThreadId!.Value, Ct)).Should().BeNull();
        (await relationships.CheckConsistencyAsync(other, cancellationToken: Ct)).DuplicateGroups.Should().Be(0);

        await db.InWorkspaceAsync(other, async tx =>
        {
            await using var count = tx.Command("SELECT (SELECT count(*) FROM opportunity.duplicate_group) + (SELECT count(*) FROM opportunity.email_thread)");
            ((long)(await count.ExecuteScalarAsync(Ct))!).Should().Be(0);
        });

        // A write addressed to another workspace is rejected by the policy's WITH CHECK.
        var act = () => db.InWorkspaceAsync(other, async tx =>
        {
            await using var insert = tx.Command(
                "INSERT INTO opportunity.duplicate_group (workspace_id, duplicate_group_id, source, hash_kind, hash_value) VALUES (@ws, @id, 1, 1, 'x')");
            insert.Parameters.AddWithValue("ws", ws);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync(Ct);
        });
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task The_consistency_report_finds_drift_and_recompute_repairs_what_it_owns()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var relationships = new DocumentRelationshipRepository(db.AppDataSource);
        var docs = await ImportAsync(db, ws, ("ABC0001", "DG-1", "T-1"), ("ABC0002", "DG-1", null), ("ABC0003", "DG-2", null), ("ABC0004", "DG-3", null));
        (await relationships.CheckConsistencyAsync(ws, cancellationToken: Ct)).IsConsistent.Should().BeTrue();

        // Drift a writer bypassing RelationshipWriter could leave behind (arranged as the superuser).
        var group1 = docs[0].DuplicateGroupId!.Value;
        await db.ExecuteAsync("UPDATE opportunity.duplicate_group SET member_count = 7 WHERE duplicate_group_id = @g", ("g", group1));
        await db.ExecuteAsync("UPDATE opportunity.document SET is_duplicate_primary = true WHERE document_id = @d", ("d", docs[1].DocumentId));
        await db.ExecuteAsync(
            "UPDATE opportunity.document SET upstream_dedupe_hash = 'aa', upstream_dedupe_hash_kind = 2 WHERE document_id = ANY(@d)",
            ("d", new[] { docs[0].DocumentId, docs[2].DocumentId }));
        await db.ExecuteAsync("UPDATE opportunity.document SET upstream_dedupe_hash = 'bb', upstream_dedupe_hash_kind = 2 WHERE document_id = @d", ("d", docs[1].DocumentId));
        await db.ExecuteAsync(
            "INSERT INTO opportunity.duplicate_group (workspace_id, duplicate_group_id, source, hash_kind, hash_value) VALUES (@ws, @id, 1, 1, 'EMPTY')",
            ("ws", ws), ("id", Guid.NewGuid()));
        var parent = docs[3];
        await db.ExecuteAsync(
            """
            UPDATE opportunity.document SET family_id = @p, parent_document_id = @p, family_sequence = 1, email_thread_id = @t, email_thread_source = 1
            WHERE document_id = @d
            """,
            ("p", parent.DocumentId), ("t", docs[0].EmailThreadId!.Value), ("d", docs[2].DocumentId));

        var report = await relationships.CheckConsistencyAsync(ws, cancellationToken: Ct);
        report.IsConsistent.Should().BeFalse();
        report.Finding(RelationshipFindingKind.DuplicateGroupCountMismatch).Sample.Should().ContainSingle(s => s.StartsWith(group1.ToString(), StringComparison.Ordinal));
        report.Finding(RelationshipFindingKind.DuplicateGroupPrimaryMismatch).Count.Should().Be(2, "DG-1 has a second flagged member; DG-2's member moved family");
        report.Finding(RelationshipFindingKind.EmptyDuplicateGroup).Count.Should().Be(1);
        report.Finding(RelationshipFindingKind.DuplicateGroupHashConflict).Count.Should().Be(1, "DG-1 holds hashes aa and bb");
        report.Finding(RelationshipFindingKind.DedupeHashSplitAcrossGroups).Sample.Should().Equal("aa: 2 groups");
        report.Finding(RelationshipFindingKind.EmailThreadCountMismatch).Count.Should().Be(1);
        report.Finding(RelationshipFindingKind.AttachmentInEmailThread).Sample.Should().Equal("ABC0003");
        report.Finding(RelationshipFindingKind.PrimaryWithoutGroup).Count.Should().Be(0);

        await relationships.RecomputeAllAsync(ws, Ct);

        var after = await relationships.CheckConsistencyAsync(ws, cancellationToken: Ct);
        after.Findings.Where(f => f.Count > 0).Select(f => f.Kind).Should().BeEquivalentTo(
            [RelationshipFindingKind.DuplicateGroupHashConflict, RelationshipFindingKind.DedupeHashSplitAcrossGroups, RelationshipFindingKind.AttachmentInEmailThread],
            "upstream data conflicts are reported for review, never silently rewritten (Q-09)");
    }

    [Fact]
    public async Task System_fields_seed_with_reserved_slots_and_never_collide_with_an_earlier_custom_field()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var catalog = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);

        catalog.Find(SystemFields.AllCustodians)!.SearchSlot.Should().Be("kw.s300");
        catalog.Find(SystemFields.AllPaths)!.SearchSlot.Should().Be("idt.s050");
        catalog.Find(SystemFields.EmailThreadGroup)!.ColumnName.Should().Be("email_thread_id");
        var custodian = (await db.Fields.CreateFieldAsync(new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata), Ct)).Value!;
        custodian.SearchSlot.Should().Be("kw.s001", "custom fields still take the lowest free slot");
        SearchFieldExpansion.Expand(await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct), custodian, new SearchFieldOptions(CustodianIncludesAllCustodians: true))
            .Select(f => f.SearchSlot).Should().Equal("kw.s001", "kw.s300");

        // A workspace that already created "All Custodians" as a custom field keeps it when system fields are re-seeded.
        var legacy = await db.CreateWorkspaceAsync();
        await db.ExecuteAsync("INSERT INTO opportunity.field_catalog_counter (workspace_id) VALUES (@ws)", ("ws", legacy));
        await db.Fields.CreateFieldAsync(new NewField(legacy, "All Custodians", FieldType.Keyword, FieldStorage.Metadata, IsMultiValue: true), Ct);
        await db.Fields.InitializeWorkspaceAsync(legacy, Ct);
        var legacyCatalog = await db.Fields.GetCatalogAsync(legacy, cancellationToken: Ct);
        legacyCatalog.Find(SystemFields.AllCustodians).Should().BeNull();
        legacyCatalog.Find(SystemFields.DuplicateCustodians).Should().NotBeNull();
    }

    private static string[] LoadFileRow(GeneratedDocument doc, DatValueFormatter formatter) =>
    [
        doc.ControlNumber, doc.BegAttach, doc.EndAttach, doc.ParentControlNumber ?? "", doc.FamilyId, doc.Custodian,
        formatter.Format(doc.AllCustodians.ToArray()), formatter.Format(doc.DuplicateCustodians.ToArray()),
        doc.DuplicateGroupId ?? "", doc.EmailThreadId ?? "", doc.Md5, doc.Content.Sha256, "", "",
    ];

    // What the import chunk (E08-T03) stores for Metadata-storage targets: canonical values keyed "f" + FieldId.
    private static JsonObject Metadata(MappedRow row)
    {
        var metadata = new JsonObject();
        foreach (var cell in row.Cells.Where(c => c.Target.FieldId is not null && c.Target.Definition.Storage == FieldStorage.Metadata && c.Value is not null))
        {
            metadata[FieldKey.For(cell.Target.FieldId!.Value)] = cell.Value!.DeepClone();
        }

        return metadata;
    }

    private static async Task<List<Document>> ImportAsync(CoreSchemaDatabase db, Guid ws, params (string ControlNumber, string? Group, string? Thread)[] rows)
    {
        var documents = new List<Document>();
        var groups = new List<DuplicateGroupKey>();
        var threads = new List<EmailThreadKey>();
        foreach (var (controlNumber, group, thread) in rows)
        {
            var document = Document.Create(ws, controlNumber, caseSensitive: false);
            var result = UpstreamRelationships.Apply(document, new UpstreamRelationshipValues { DuplicateGroup = group, EmailThreadGroup = thread });
            groups.AddRange(result.DuplicateGroup is { } g ? [g] : []);
            threads.AddRange(result.EmailThread is { } t ? [t] : []);
            documents.Add(document);
        }

        await db.InWorkspaceAsync(ws, async tx =>
        {
            await DocumentRepository.InsertManyAsync(tx, documents, Ct);
            await RelationshipWriter.SyncAsync(tx, new RelationshipSync(groups, threads, [.. documents.Select(d => d.DocumentId)]), Ct);
        });
        return documents;
    }

    private static Task<bool> PrimaryAsync(CoreSchemaDatabase db, Guid ws, string controlNumber) =>
        db.ScalarAsync<bool>(
            "SELECT is_duplicate_primary FROM opportunity.document WHERE workspace_id = @ws AND control_number = @cn", ("ws", ws), ("cn", controlNumber));

    private sealed record Stored(Guid DocumentId, Guid? Group, Guid? Thread, bool Primary, string[] AllCustodians);

    private static async Task<Dictionary<string, Stored>> StoredAsync(CoreSchemaDatabase db, Guid ws)
    {
        var result = new Dictionary<string, Stored>(StringComparer.Ordinal);
        await db.InWorkspaceAsync(ws, async tx =>
        {
            await using var command = tx.Command(
                $"""
                SELECT control_number, document_id, duplicate_group_id, email_thread_id, is_duplicate_primary,
                       coalesce(ARRAY(SELECT jsonb_array_elements_text(metadata -> 'f{SystemFields.AllCustodians}')), ARRAY[]::text[])
                FROM opportunity.document WHERE workspace_id = @ws
                """);
            command.Parameters.AddWithValue("ws", ws);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                result[reader.GetString(0)] = new Stored(
                    reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.GetBoolean(4), reader.GetFieldValue<string[]>(5));
            }
        });
        return result;
    }
}
