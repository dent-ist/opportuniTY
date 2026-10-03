using System.Text.Json.Nodes;

using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Documents;

/// <summary>E04-T04: interim coding current state and CodingEvent provenance (§27, ADR-001, ADR-010 §8, Q-07, Q-08).</summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class CodingStoreTests(MigrationPostgresFixture postgres)
{
    private static readonly Guid Reviewer = Guid.CreateVersion7();
    private static readonly Guid OtherReviewer = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Interactive_write_changes_state_events_and_version_together_and_only_when_a_value_changes()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var doc = await db.InsertDocumentAsync(w.Id, "DOC0001");
        var documentRow = await DocumentRowAsync(db, doc.DocumentId);

        var result = await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId,
            CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true)),
            CodingFieldOperation.AddChoices(w.Issues, w.Pricing),
            CodingFieldOperation.Set(w.Notes, JsonValue.Create("Key memo"))), Ct);

        result.Outcome.Should().Be(CodingWriteOutcome.Applied);
        result.Documents.Single().Should().BeEquivalentTo(new DocumentCodingResult(doc.DocumentId, DocumentCodingOutcome.Changed, 2, []));
        result.EventsWritten.Should().Be(3);
        (await db.Documents.GetVersionAsync(w.Id, doc.DocumentId, Ct)).Should().Be(2, "one bump per document per transaction");

        var current = (await db.Coding.GetCurrentAsync(w.Id, [doc.DocumentId], Ct)).Single();
        current.DocumentVersion.Should().Be(2);
        Values(current).Should().BeEquivalentTo(new Dictionary<int, string?>
        {
            [w.Responsive] = "true",
            [w.Issues] = $"[{w.Pricing}]",
            [w.Notes] = "\"Key memo\"",
        });
        current.Fields.Should().OnlyContain(f => f.ChangedAtVersion == 2 && f.ChangedByJobId == null && f.ChangedBy == Reviewer);

        var events = (await db.Coding.GetEventsAsync(new CodingEventQuery(w.Id) { DocumentId = doc.DocumentId }, Ct)).Events;
        events.Should().HaveCount(3).And.OnlyContain(e =>
            e.Kind == CodingEventKind.ValueChanged && e.PriorValue == null && e.DocumentVersion == 2
            && e.ActorType == CodingActorType.Human && e.ActorId == Reviewer && e.JobId == null);

        // Coding never rewrites the wide document tuple (ADR-003 R2).
        (await DocumentRowAsync(db, doc.DocumentId)).Should().Be(documentRow);

        // Re-applying the same state under a new key changes nothing: no event, no version bump.
        var same = await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId,
            CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true)),
            CodingFieldOperation.AddChoices(w.Issues, w.Pricing)), Ct);
        same.Documents.Single().Should().BeEquivalentTo(new DocumentCodingResult(doc.DocumentId, DocumentCodingOutcome.Unchanged, 2, []));
        same.EventsWritten.Should().Be(0);

        // Clearing is a change; the state row stays (value null) so its ChangedAtVersion remains known for Q-07.
        var cleared = await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId, CodingFieldOperation.Clear(w.Notes)), Ct);
        cleared.Documents.Single().DocumentVersion.Should().Be(3);
        var notes = (await db.Coding.GetCurrentAsync(w.Id, [doc.DocumentId], Ct)).Single().Fields.Single(f => f.FieldId == w.Notes);
        notes.Value.Should().BeNull();
        notes.ChangedAtVersion.Should().Be(3);
        var clearEvent = (await db.Coding.GetEventsAsync(new CodingEventQuery(w.Id) { FieldId = w.Notes }, Ct)).Events[^1];
        clearEvent.PriorValue!.GetValue<string>().Should().Be("Key memo");
        clearEvent.NewValue.Should().BeNull();
    }

    [Fact]
    public async Task A_failure_inside_the_write_leaves_no_state_event_version_or_idempotency_record()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var doc = await db.InsertDocumentAsync(w.Id, "DOC0001");

        // Make the final statement of the write (the CodingEvent insert) fail.
        await db.ExecuteAsync(
            """
            CREATE FUNCTION opportunity.test_fail_event() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN RAISE EXCEPTION 'injected failure'; END $$;
            CREATE TRIGGER test_fail_event BEFORE INSERT ON opportunity.coding_event FOR EACH ROW EXECUTE FUNCTION opportunity.test_fail_event();
            """);
        var request = Interactive(w.Id, doc.DocumentId, CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true)));

        var act = () => db.Coding.ApplyAsync(request, Ct);
        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be("injected failure");

        (await db.Documents.GetVersionAsync(w.Id, doc.DocumentId, Ct)).Should().Be(1);
        (await CountAsync(db, "document_coding_field")).Should().Be(0);
        (await CountAsync(db, "coding_event")).Should().Be(0);
        (await CountAsync(db, "coding_write")).Should().Be(0);

        // The same key works once the fault is gone.
        await db.ExecuteAsync("DROP TRIGGER test_fail_event ON opportunity.coding_event");
        (await db.Coding.ApplyAsync(request, Ct)).Outcome.Should().Be(CodingWriteOutcome.Applied);
        (await CountAsync(db, "coding_event")).Should().Be(1);
    }

    [Fact]
    public async Task Reapplying_the_same_chunk_creates_no_duplicate_events()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var docs = await DocumentsAsync(db, w.Id, 50);
        var job = Guid.CreateVersion7();
        var chunk = Bulk(w.Id, job, "chunk-1", docs.Select(d => new CodingTarget(d, 1)), CodingFieldOperation.Set(w.Responsive, JsonValue.Create(false)));

        var first = await db.Coding.ApplyAsync(chunk, Ct);
        var second = await db.Coding.ApplyAsync(chunk, Ct);

        first.Outcome.Should().Be(CodingWriteOutcome.Applied);
        first.EventsWritten.Should().Be(50);
        second.Outcome.Should().Be(CodingWriteOutcome.Replayed);
        second.EventsWritten.Should().Be(50, "the replay reports what the original write did");
        second.Documents.Should().HaveCount(50).And.OnlyContain(d => d.Outcome == DocumentCodingOutcome.Changed && d.DocumentVersion == 2);
        (await CountAsync(db, "coding_event")).Should().Be(50);
        (await db.Documents.GetVersionAsync(w.Id, docs[0], Ct)).Should().Be(2);

        // Concurrent redelivery of another chunk: exactly one applies.
        var chunk2 = chunk with { IdempotencyKey = "chunk-2", Operations = [CodingFieldOperation.AddChoices(w.Issues, w.Pricing)] };
        var racers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => db.Coding.ApplyAsync(chunk2, Ct)));
        racers.Count(r => r.Outcome == CodingWriteOutcome.Applied).Should().Be(1);
        racers.Count(r => r.Outcome == CodingWriteOutcome.Replayed).Should().Be(3);
        (await CountAsync(db, "coding_event")).Should().Be(100);

        // The same key for a different request is refused.
        var reuse = await db.Coding.ApplyAsync(chunk with { Operations = [CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true))] }, Ct);
        reuse.Outcome.Should().Be(CodingWriteOutcome.IdempotencyKeyReuse);
        (await CountAsync(db, "coding_event")).Should().Be(100);
    }

    [Fact]
    public async Task Bulk_chunk_writes_one_event_per_change_and_one_version_bump_per_document()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var docs = await DocumentsAsync(db, w.Id, 1_000);
        var job = Guid.CreateVersion7();
        // Half the documents already hold one of the values: those fields are unchanged and get no event.
        await db.Coding.ApplyAsync(Bulk(w.Id, job, "seed", docs.Take(500).Select(d => new CodingTarget(d, 1)),
            CodingFieldOperation.AddChoices(w.Issues, w.Pricing)), Ct);

        var result = await db.Coding.ApplyAsync(Bulk(w.Id, job, "chunk", docs.Select(d => new CodingTarget(d, 1)),
            CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true)),
            CodingFieldOperation.AddChoices(w.Issues, w.Pricing)), Ct);

        result.Outcome.Should().Be(CodingWriteOutcome.Applied);
        result.EventsWritten.Should().Be(1_000 + 500);
        result.Documents.Should().OnlyContain(d => d.Outcome == DocumentCodingOutcome.Changed);
        result.Documents.Take(500).Should().OnlyContain(d => d.DocumentVersion == 3);
        result.Documents.Skip(500).Should().OnlyContain(d => d.DocumentVersion == 2);
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.document_projection_state WHERE workspace_id = @ws AND document_version = 3", ("ws", w.Id)))
            .Should().Be(500);
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.coding_event WHERE workspace_id = @ws AND job_id = @job AND actor_type = 2",
            ("ws", w.Id), ("job", job))).Should().Be(1_500 + 500);

        var state = await db.Coding.GetCurrentAsync(w.Id, docs, Ct);
        state.Should().HaveCount(1_000).And.OnlyContain(d => d.Fields.All(f => f.ChangedByJobId == job));
    }

    [Fact]
    public async Task Bulk_skips_fields_edited_after_the_job_baseline_and_records_the_skip()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var docs = await DocumentsAsync(db, w.Id, 4);
        var (edited, untouched, bothEdited, deleted) = (docs[0], docs[1], docs[2], docs[3]);
        var job = Guid.CreateVersion7();
        const long Baseline = 1; // DocumentVersion frozen in the job's snapshot

        // After the snapshot: a reviewer changes Responsive on one document and both fields on another; one is deleted.
        await db.Coding.ApplyAsync(Interactive(w.Id, edited, CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true))), Ct);
        await db.Coding.ApplyAsync(Interactive(w.Id, bothEdited,
            CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true)), CodingFieldOperation.AddChoices(w.Issues, w.Antitrust)), Ct);
        await db.ExecuteAsync(
            "UPDATE opportunity.document_projection_state SET is_deleted = true, deleted_at = now(), document_version = document_version + 1 WHERE document_id = @id",
            ("id", deleted));

        var result = await db.Coding.ApplyAsync(Bulk(w.Id, job, "chunk-1", docs.Select(d => new CodingTarget(d, Baseline)),
            CodingFieldOperation.Set(w.Responsive, JsonValue.Create(false)),
            CodingFieldOperation.AddChoices(w.Issues, w.Pricing)), Ct);

        var byDoc = result.Documents.ToDictionary(d => d.DocumentId);
        byDoc[edited].Outcome.Should().Be(DocumentCodingOutcome.Skipped);
        byDoc[edited].DocumentVersion.Should().Be(3, "the other field still changed");
        byDoc[edited].SkippedFieldIds.Should().Equal(w.Responsive);
        byDoc[untouched].Should().BeEquivalentTo(new DocumentCodingResult(untouched, DocumentCodingOutcome.Changed, 2, []));
        byDoc[bothEdited].Outcome.Should().Be(DocumentCodingOutcome.Skipped);
        byDoc[bothEdited].DocumentVersion.Should().Be(2, "nothing changed, so no bump");
        byDoc[bothEdited].SkippedFieldIds.Should().BeEquivalentTo([w.Responsive, w.Issues]);
        byDoc[deleted].Outcome.Should().Be(DocumentCodingOutcome.NotFound);

        // The reviewer's values survive; the job's other field applied.
        var current = (await db.Coding.GetCurrentAsync(w.Id, [edited, bothEdited], Ct)).ToDictionary(d => d.DocumentId);
        Values(current[edited]).Should().BeEquivalentTo(new Dictionary<int, string?> { [w.Responsive] = "true", [w.Issues] = $"[{w.Pricing}]" });
        Values(current[bothEdited]).Should().BeEquivalentTo(new Dictionary<int, string?> { [w.Responsive] = "true", [w.Issues] = $"[{w.Antitrust}]" });

        // Skips are in coding history and reportable per job.
        var skips = (await db.Coding.GetEventsAsync(new CodingEventQuery(w.Id) { JobId = job, Kind = CodingEventKind.BulkSkippedConcurrentEdit }, Ct)).Events;
        skips.Select(s => (s.DocumentId, s.FieldId)).Should().BeEquivalentTo([(edited, w.Responsive), (bothEdited, w.Responsive), (bothEdited, w.Issues)]);
        skips.Single(s => s.DocumentId == edited).PriorValue!.GetValue<bool>().Should().BeTrue();
        skips.Single(s => s.DocumentId == edited).NewValue!.GetValue<bool>().Should().BeFalse();

        // A later chunk of the same job is not blocked by the job's own earlier change (ChangedByJobId = this job).
        var again = await db.Coding.ApplyAsync(Bulk(w.Id, job, "chunk-2", [new CodingTarget(untouched, Baseline)],
            CodingFieldOperation.RemoveChoices(w.Issues, w.Pricing)), Ct);
        again.Documents.Single().Should().BeEquivalentTo(new DocumentCodingResult(untouched, DocumentCodingOutcome.Changed, 3, []));

        // A different job with the old baseline is blocked by that change.
        var otherJob = await db.Coding.ApplyAsync(Bulk(w.Id, Guid.CreateVersion7(), "other-1", [new CodingTarget(untouched, Baseline)],
            CodingFieldOperation.AddChoices(w.Issues, w.Pricing)), Ct);
        otherJob.Documents.Single().SkippedFieldIds.Should().Equal(w.Issues);
    }

    [Fact]
    public async Task Coding_events_can_be_filtered_by_actor_type_and_paged()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var docs = await DocumentsAsync(db, w.Id, 5);
        foreach (var doc in docs)
        {
            await db.Coding.ApplyAsync(Interactive(w.Id, doc, CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true))), Ct);
        }

        await db.Coding.ApplyAsync(Bulk(w.Id, Guid.CreateVersion7(), "bulk", docs.Select(d => new CodingTarget(d, 2)),
            CodingFieldOperation.AddChoices(w.Issues, w.Pricing)), Ct);
        await db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = w.Id,
            IdempotencyKey = "rule-1",
            Actor = new CodingActor(Guid.Empty, CodingActorType.SystemRule),
            Documents = [new CodingTarget(docs[0])],
            Operations = [CodingFieldOperation.Set(w.Notes, JsonValue.Create("auto"))],
        }, Ct);

        async Task<int> CountBy(CodingActorType type) =>
            (await db.Coding.GetEventsAsync(new CodingEventQuery(w.Id) { ActorType = type, Limit = 1000 }, Ct)).Events.Count;

        (await CountBy(CodingActorType.Human)).Should().Be(5);
        (await CountBy(CodingActorType.BulkHuman)).Should().Be(5);
        (await CountBy(CodingActorType.SystemRule)).Should().Be(1);
        (await CountBy(CodingActorType.Model)).Should().Be(0);

        var all = new List<CodingEvent>();
        CodingEventCursor? cursor = null;
        do
        {
            var page = await db.Coding.GetEventsAsync(new CodingEventQuery(w.Id) { Limit = 4, After = cursor }, Ct);
            all.AddRange(page.Events);
            cursor = page.Next;
        }
        while (cursor is not null);

        all.Should().HaveCount(11);
        all.Select(e => e.EventId).Should().OnlyHaveUniqueItems();
        all.Select(e => e.OccurredAt).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Values_as_of_any_document_version_are_reproducible_from_provenance()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var doc = await db.InsertDocumentAsync(w.Id, "DOC0001");
        await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId,
            CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true)), CodingFieldOperation.AddChoices(w.Issues, w.Pricing)), Ct); // v2
        await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId, CodingFieldOperation.AddChoices(w.Issues, w.Antitrust)), Ct); // v3
        await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId, CodingFieldOperation.Clear(w.Responsive)), Ct); // v4

        (await db.Coding.GetValuesAsOfVersionAsync(w.Id, doc.DocumentId, 1, Ct)).Should().BeEmpty();
        Render(await db.Coding.GetValuesAsOfVersionAsync(w.Id, doc.DocumentId, 2, Ct)).Should().BeEquivalentTo(
            new Dictionary<int, string> { [w.Responsive] = "true", [w.Issues] = $"[{w.Pricing}]" });
        Render(await db.Coding.GetValuesAsOfVersionAsync(w.Id, doc.DocumentId, 3, Ct)).Should().BeEquivalentTo(
            new Dictionary<int, string> { [w.Responsive] = "true", [w.Issues] = $"[{Math.Min(w.Pricing, w.Antitrust)},{Math.Max(w.Pricing, w.Antitrust)}]" });
        Render(await db.Coding.GetValuesAsOfVersionAsync(w.Id, doc.DocumentId, 4, Ct)).Should().BeEquivalentTo(
            new Dictionary<int, string> { [w.Issues] = $"[{Math.Min(w.Pricing, w.Antitrust)},{Math.Max(w.Pricing, w.Antitrust)}]" });

        // History matches current state at the current version.
        var current = (await db.Coding.GetCurrentAsync(w.Id, [doc.DocumentId], Ct)).Single();
        Render(await db.Coding.GetValuesAsOfVersionAsync(w.Id, doc.DocumentId, current.DocumentVersion, Ct))
            .Should().BeEquivalentTo(Values(current).Where(v => v.Value != null).ToDictionary(v => v.Key, v => v.Value!));
    }

    [Fact]
    public async Task Invalid_writes_are_rejected_with_field_level_errors_and_write_nothing()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var doc = await db.InsertDocumentAsync(w.Id, "DOC0001");

        async Task<IReadOnlyList<FieldError>> Errors(CodingWriteRequest request)
        {
            var result = await db.Coding.ApplyAsync(request, Ct);
            result.Outcome.Should().Be(CodingWriteOutcome.Invalid);
            return result.Errors;
        }

        (await Errors(Interactive(w.Id, doc.DocumentId,
            CodingFieldOperation.Set(w.Responsive, JsonValue.Create("Yes")),
            CodingFieldOperation.Set(w.Custodian, JsonValue.Create("Alice")),
            CodingFieldOperation.Set(4242, JsonValue.Create(1)),
            CodingFieldOperation.AddChoices(w.Privilege, w.Privileged),
            CodingFieldOperation.Set(w.Issues, new JsonArray(w.Retired)))))
            .Select(e => (e.Field, e.Code)).Should().BeEquivalentTo(
            [
                (FieldKey.For(w.Responsive), "invalid-boolean"),
                (FieldKey.For(w.Custodian), "not-coding-field"),
                ("f4242", "unknown-field"),
                (FieldKey.For(w.Privilege), "not-multi-choice"),
                (FieldKey.For(w.Issues), "inactive-choice"),
            ]);

        (await Errors(Interactive(w.Id, doc.DocumentId, CodingFieldOperation.Set(w.Issues, new JsonArray(w.Privileged)))))
            .Single().Code.Should().Be("unknown-choice", "a choice of another field");

        (await Errors(Interactive(w.Id, doc.DocumentId, CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true))) with
        {
            Actor = new CodingActor(Reviewer, CodingActorType.Model),
        })).Single().Code.Should().Be("actor-type-reserved");
        (await Errors(Bulk(w.Id, Guid.CreateVersion7(), "k", [new CodingTarget(doc.DocumentId)], CodingFieldOperation.Clear(w.Notes))))
            .Single().Code.Should().Be("baseline-required");
        (await Errors(Bulk(w.Id, Guid.CreateVersion7(), "k", [new CodingTarget(doc.DocumentId, 1)], CodingFieldOperation.Clear(w.Notes)) with
        {
            JobId = null,
        })).Select(e => e.Code).Should().Contain("job-required");

        (await CountAsync(db, "coding_write")).Should().Be(0);
        (await db.Documents.GetVersionAsync(w.Id, doc.DocumentId, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task If_match_conflicts_and_unknown_documents_write_nothing()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var doc = await db.InsertDocumentAsync(w.Id, "DOC0001");
        var set = CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true));

        var conflict = await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId, set) with { ExpectedVersion = 7 }, Ct);
        conflict.Outcome.Should().Be(CodingWriteOutcome.VersionConflict);
        conflict.Documents.Single().DocumentVersion.Should().Be(1);
        (await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId, set) with { ExpectedVersion = 1 }, Ct))
            .Documents.Single().DocumentVersion.Should().Be(2);

        (await db.Coding.ApplyAsync(Interactive(w.Id, Guid.CreateVersion7(), set), Ct)).Outcome.Should().Be(CodingWriteOutcome.NotFound);
        (await CountAsync(db, "coding_write")).Should().Be(1);
    }

    [Fact]
    public async Task Choices_in_coding_use_cannot_be_deleted_and_deactivated_choices_stay_on_documents()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await WorkspaceAsync(db);
        var doc = await db.InsertDocumentAsync(w.Id, "DOC0001");
        var unused = (await db.Fields.AddChoiceAsync(w.Id, w.Issues, "Unused", Ct)).Value!;
        await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId, CodingFieldOperation.AddChoices(w.Issues, w.Pricing, w.Antitrust)), Ct);

        (await db.Fields.DeleteChoiceAsync(w.Id, w.Issues, w.Pricing, Ct)).Errors.Single().Code.Should().Be("choice-in-use");
        (await db.Fields.DeleteChoiceAsync(w.Id, w.Issues, unused.ChoiceId, Ct)).Succeeded.Should().BeTrue();

        // Removed from every document, a used choice still cannot be deleted: history references it.
        await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId, CodingFieldOperation.RemoveChoices(w.Issues, w.Antitrust)), Ct);
        (await db.Fields.DeleteChoiceAsync(w.Id, w.Issues, w.Antitrust, Ct)).Errors.Single().Code.Should().Be("choice-in-use");

        // Deactivated: kept where assigned, not assignable, still removable.
        await db.Fields.SetChoiceActiveAsync(w.Id, w.Issues, w.Pricing, false, Ct);
        var second = await db.InsertDocumentAsync(w.Id, "DOC0002");
        (await db.Coding.ApplyAsync(Interactive(w.Id, second.DocumentId, CodingFieldOperation.AddChoices(w.Issues, w.Pricing)), Ct))
            .Errors.Single().Code.Should().Be("inactive-choice");
        Values((await db.Coding.GetCurrentAsync(w.Id, [doc.DocumentId], Ct)).Single())[w.Issues].Should().Be($"[{w.Pricing}]");
        (await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId, CodingFieldOperation.RemoveChoices(w.Issues, w.Pricing)), Ct))
            .Documents.Single().Outcome.Should().Be(DocumentCodingOutcome.Changed);

        // A coding field that holds (or held) values cannot be retyped.
        (await db.Fields.UpdateFieldAsync(new FieldChange(w.Id, w.Notes) { Type = FieldType.Keyword }, Ct)).Succeeded.Should().BeTrue();
        await db.Coding.ApplyAsync(Interactive(w.Id, doc.DocumentId, CodingFieldOperation.Set(w.Notes, JsonValue.Create("x"))), Ct);
        (await db.Fields.UpdateFieldAsync(new FieldChange(w.Id, w.Notes) { Type = FieldType.Text }, Ct))
            .Errors.Single().Code.Should().Be("field-has-values");
    }

    [Fact]
    public async Task Cross_workspace_coding_references_are_impossible()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var a = await WorkspaceAsync(db);
        var b = await WorkspaceAsync(db);
        var docA = await db.InsertDocumentAsync(a.Id, "DOC0001");
        var docB = await db.InsertDocumentAsync(b.Id, "DOC0001");

        // Through the repository, workspace A's document is simply not found in workspace B.
        (await db.Coding.ApplyAsync(Interactive(b.Id, docA.DocumentId, CodingFieldOperation.Set(b.Responsive, JsonValue.Create(true))), Ct))
            .Outcome.Should().Be(CodingWriteOutcome.NotFound);
        (await db.Coding.GetCurrentAsync(b.Id, [docA.DocumentId], Ct)).Should().BeEmpty();

        // In the schema, composite keys reject every cross-workspace reference.
        await ExpectForeignKeyViolation(db,
            "INSERT INTO opportunity.document_coding_field (workspace_id, document_id, field_id, value, changed_at_version, changed_by, changed_at) VALUES (@ws, @doc, @field, 'true', 1, @actor, now())",
            "document_coding_field_document_fk", ("ws", b.Id), ("doc", docA.DocumentId), ("field", b.Responsive), ("actor", Reviewer));
        await ExpectForeignKeyViolation(db,
            "INSERT INTO opportunity.document_coding_field (workspace_id, document_id, field_id, value, changed_at_version, changed_by, changed_at) VALUES (@ws, @doc, @field, '\"x\"', 1, @actor, now())",
            "document_coding_field_field_fk", ("ws", b.Id), ("doc", docB.DocumentId), ("field", b.Custodian), ("actor", Reviewer));

        await db.Coding.ApplyAsync(Interactive(b.Id, docB.DocumentId, CodingFieldOperation.AddChoices(b.Issues, b.Pricing)), Ct);
        await ExpectForeignKeyViolation(db,
            "INSERT INTO opportunity.document_coding_choice (workspace_id, document_id, field_id, choice_id) VALUES (@ws, @doc, @field, @choice)",
            "document_coding_choice_field_fk", ("ws", a.Id), ("doc", docB.DocumentId), ("field", a.Issues), ("choice", a.Antitrust));
        // A choice of another field (here the single-choice privilege field) cannot be stored under Issues either.
        await ExpectForeignKeyViolation(db,
            "INSERT INTO opportunity.document_coding_choice (workspace_id, document_id, field_id, choice_id) VALUES (@ws, @doc, @field, @choice)",
            "document_coding_choice_choice_fk", ("ws", b.Id), ("doc", docB.DocumentId), ("field", b.Issues), ("choice", b.Privileged));
        var writeB = await db.ScalarAsync<Guid>("SELECT write_id FROM opportunity.coding_write WHERE workspace_id = @ws", ("ws", b.Id));
        await ExpectForeignKeyViolation(db,
            """
            INSERT INTO opportunity.coding_event (workspace_id, occurred_at, event_id, write_id, document_id, field_id, event_kind,
                new_value, document_version, actor_id, actor_type, idempotency_key)
            SELECT @ws, now(), gen_random_uuid(), write_id, @doc, @field, 1, 'true', 2, actor_id, actor_type, idempotency_key
            FROM opportunity.coding_write WHERE write_id = @write
            """,
            "coding_event_write_fk", ("ws", a.Id), ("doc", docA.DocumentId), ("field", a.Responsive), ("write", writeB));
    }

    [Fact]
    public async Task Coding_tables_are_workspace_keyed_and_coding_event_is_time_partitioned_and_append_only()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);

        foreach (var table in new[] { "document_coding_field", "document_coding_choice", "coding_write", "coding_event" })
        {
            (await db.ScalarAsync<string>(
                """
                SELECT a.attname::text FROM pg_constraint c JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
                WHERE c.conrelid = ('opportunity.' || @t)::regclass AND c.contype = 'p'
                """, ("t", table))).Should().Be("workspace_id", table);
        }

        (await db.ScalarAsync<string>(
            "SELECT pg_get_partkeydef('opportunity.coding_event'::regclass)")).Should().Be("RANGE (occurred_at)");
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_inherits WHERE inhparent = 'opportunity.coding_event'::regclass")).Should().BeGreaterThanOrEqualTo(25);
        (await db.ScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT FROM pg_inherits i JOIN pg_class p ON p.oid = i.inhrelid
                           WHERE i.inhparent = 'opportunity.coding_event'::regclass
                             AND p.relname = 'coding_event_p' || to_char(now() AT TIME ZONE 'UTC', 'YYYYMM'))
            """)).Should().BeTrue();
        (await db.ScalarAsync<int>("SELECT opportunity.coding_event_ensure_partitions(now() + interval '24 months')")).Should().Be(0);
        (await db.ScalarAsync<int>("SELECT opportunity.coding_event_ensure_partitions(now() + interval '25 months')")).Should().Be(1);

        foreach (var table in new[] { "opportunity.coding_event", "opportunity.coding_write", "opportunity.coding_event_p" + DateTime.UtcNow.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture) })
        {
            foreach (var privilege in new[] { "UPDATE", "DELETE" })
            {
                (await db.ScalarAsync<bool>("SELECT has_table_privilege('opportunity_app', @t, @p)", ("t", table), ("p", privilege)))
                    .Should().BeFalse($"{table} is append-only ({privilege})");
            }

            (await db.ScalarAsync<bool>("SELECT has_table_privilege('opportunity_app', @t, 'INSERT')", ("t", table))).Should().BeTrue();
        }

        (await db.ScalarAsync<bool>("SELECT has_table_privilege('opportunity_app', 'opportunity.document_coding_field', 'UPDATE')"))
            .Should().BeTrue();
    }

    private static CodingWriteRequest Interactive(Guid ws, Guid documentId, params CodingFieldOperation[] operations) => new()
    {
        WorkspaceId = ws,
        IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
        Actor = new CodingActor(Reviewer, CodingActorType.Human),
        Documents = [new CodingTarget(documentId)],
        Operations = operations,
    };

    private static CodingWriteRequest Bulk(
        Guid ws, Guid jobId, string key, IEnumerable<CodingTarget> targets, params CodingFieldOperation[] operations) => new()
        {
            WorkspaceId = ws,
            IdempotencyKey = key,
            Actor = new CodingActor(OtherReviewer, CodingActorType.BulkHuman),
            JobId = jobId,
            Documents = [.. targets],
            Operations = operations,
        };

    private static Dictionary<int, string?> Values(DocumentCoding coding) =>
        coding.Fields.ToDictionary(f => f.FieldId, f => f.Value?.ToJsonString());

    private static Dictionary<int, string> Render(IReadOnlyDictionary<int, JsonNode> values) =>
        values.ToDictionary(v => v.Key, v => v.Value.ToJsonString());

    private static async Task<List<Guid>> DocumentsAsync(CoreSchemaDatabase db, Guid ws, int count)
    {
        var documents = Enumerable.Range(1, count)
            .Select(i => Core.Documents.Document.Create(ws, $"DOC{i:D6}", caseSensitive: false))
            .ToList();
        await db.Documents.InsertManyAsync(ws, documents, Ct);
        return [.. documents.Select(d => d.DocumentId).Order()];
    }

    private static Task<long> CountAsync(CoreSchemaDatabase db, string table) =>
        db.ScalarAsync<long>($"SELECT count(*) FROM opportunity.{table}");

    private static Task<string> DocumentRowAsync(CoreSchemaDatabase db, Guid documentId) =>
        db.ScalarAsync<string>("SELECT xmin::text || ':' || updated_at::text FROM opportunity.document WHERE document_id = @id", ("id", documentId));

    private static async Task ExpectForeignKeyViolation(
        CoreSchemaDatabase db, string sql, string constraint, params (string Name, object Value)[] parameters)
    {
        var act = () => db.ExecuteAsync(sql, parameters);
        var error = (await act.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        error.ConstraintName.Should().Be(constraint);
    }

    private static async Task<TestWorkspace> WorkspaceAsync(CoreSchemaDatabase db)
    {
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);

        async Task<int> Field(NewField field) => (await db.Fields.CreateFieldAsync(field, Ct)).Value!.FieldId;
        async Task<int> Choice(int fieldId, string name) => (await db.Fields.AddChoiceAsync(ws, fieldId, name, Ct)).Value!.ChoiceId;

        var responsive = await Field(new NewField(ws, "Responsive", FieldType.Boolean, FieldStorage.Coding));
        var issues = await Field(new NewField(ws, "Issues", FieldType.MultiChoice, FieldStorage.Coding));
        var privilege = await Field(new NewField(ws, "Privilege Status", FieldType.SingleChoice, FieldStorage.Coding,
            SecurityClass: SecurityClass.PrivilegeStatus));
        var notes = await Field(new NewField(ws, "Notes", FieldType.Text, FieldStorage.Coding));
        var custodian = await Field(new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata));
        var pricing = await Choice(issues, "Pricing");
        var antitrust = await Choice(issues, "Antitrust");
        var retired = await Choice(issues, "Retired");
        await db.Fields.SetChoiceActiveAsync(ws, issues, retired, false, Ct);
        var privileged = await Choice(privilege, "Privileged");
        return new TestWorkspace(ws, responsive, issues, privilege, notes, custodian, pricing, antitrust, retired, privileged);
    }

    private sealed record TestWorkspace(
        Guid Id, int Responsive, int Issues, int Privilege, int Notes, int Custodian, int Pricing, int Antitrust, int Retired, int Privileged);
}
