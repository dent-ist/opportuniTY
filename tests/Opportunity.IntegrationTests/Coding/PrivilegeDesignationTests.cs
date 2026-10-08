using System.Net;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Data.Migrations;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// E13-T01 against PostgreSQL (app login, RLS on): the privilege system fields are provisioned in every workspace
/// (and backfilled into existing ones by V0045), bound to the Privileged restriction class through the #51 binding,
/// protected from deletion; Withhold and Redact without a basis are refused by the coding store on every write path;
/// every change is a CodingEvent with actor, time, prior and new value.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class PrivilegeDesignationTests(MigrationPostgresFixture postgres)
{
    private static readonly Guid Reviewer = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_workspace_gets_the_privilege_fields_as_security_affecting_system_fields_bound_to_Privileged()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);

        var catalog = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        foreach (var (fieldId, name, type, multi) in PrivilegeFields.Definitions)
        {
            var field = catalog.Find(fieldId)!;
            field.Should().BeEquivalentTo(new
            {
                Name = name,
                Type = type,
                IsMultiValue = multi,
                Storage = FieldStorage.Coding,
                IsSystem = true,
                IsSecurityAffecting = true,
                SecurityClass = (SecurityClass?)SecurityClass.PrivilegeStatus,
                SearchSlot = PrivilegeFields.ReservedSlots[fieldId],
                Capabilities = FieldRules.CapabilitiesForSlot(PrivilegeFields.ReservedSlots[fieldId]),
            }, o => o.ExcludingMissingMembers());
        }

        catalog.Find(PrivilegeFields.Description)!.TextAnalysis.Should().Be(TextAnalysis.Prose);
        catalog.ChoicesOf(PrivilegeFields.LogCategory).Should().BeEmpty("admins fill the log categories");
        PrivilegeFields.BuiltInChoices.GroupBy(c => c.FieldId).Should().AllSatisfy(g =>
            catalog.ChoicesOf(g.Key).Select(c => (c.Name, c.SystemKey)).Should().Equal(g.Select(c => (c.Name, (string?)c.Key))));
        catalog.Fields.Where(f => PrivilegeFields.IsPrivilegeField(f.FieldId)).Should().HaveCount(5, "provisioning is idempotent");

        var rules = await db.ColumnAsync(
            $"SELECT r.class_key || ':' || c.system_key FROM opportunity.restriction_class_rule r JOIN opportunity.choice c USING (workspace_id, field_id, choice_id) WHERE r.workspace_id = '{ws}' ORDER BY 1");
        rules.Should().Equal(PrivilegeFields.PrivilegedClassKeys.Select(k => "Privileged:" + k).Order(StringComparer.Ordinal));

        // A custom coding field still starts at the bottom of its kind's slots (the privilege fields hold the top).
        var notes = (await db.Fields.CreateFieldAsync(new NewField(ws, "Notes", FieldType.Text, FieldStorage.Coding), Ct)).Value!;
        notes.SearchSlot.Should().Be("txt.s001");
    }

    [Fact]
    public async Task Privilege_fields_are_extendable_but_never_deleted_retyped_or_stripped_of_their_built_in_choices()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var catalog = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        var withhold = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, PrivilegeFields.Keys.Withhold)!.Value;
        var other = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.Other)!.Value;

        (await db.Fields.DeleteFieldAsync(ws, PrivilegeFields.Status, Ct)).Errors.Should().ContainSingle().Which.Code.Should().Be("system-field");
        (await db.Fields.UpdateFieldAsync(new FieldChange(ws, PrivilegeFields.Basis) { Type = FieldType.SingleChoice, IsMultiValue = false }, Ct))
            .Outcome.Should().Be(CatalogOutcome.Conflict);
        (await db.Fields.DeleteChoiceAsync(ws, PrivilegeFields.Status, withhold, Ct)).Errors.Should().ContainSingle().Which.Code.Should().Be("system-choice");
        (await db.Fields.SetChoiceActiveAsync(ws, PrivilegeFields.Basis, other, false, Ct)).Errors.Should().ContainSingle().Which.Code.Should().Be("system-choice");

        // Renaming keeps the meaning: code finds built-in choices by key.
        (await db.Fields.UpdateFieldAsync(new FieldChange(ws, PrivilegeFields.Status) { Name = "Priv Call" }, Ct)).Succeeded.Should().BeTrue();
        (await db.Fields.RenameChoiceAsync(ws, PrivilegeFields.Status, withhold, "Withheld in full", Ct)).Value!.SystemKey.Should().Be(PrivilegeFields.Keys.Withhold);
        var added = (await db.Fields.AddChoiceAsync(ws, PrivilegeFields.Basis, "Joint Defense", Ct)).Value!;
        added.SystemKey.Should().BeNull();
        (await db.Fields.DeleteChoiceAsync(ws, PrivilegeFields.Basis, added.ChoiceId, Ct)).Succeeded.Should().BeTrue();

        // The database refuses the same changes from a writer that bypasses the repository.
        foreach (var sql in new[]
        {
            $"DELETE FROM opportunity.choice WHERE workspace_id = '{ws}' AND choice_id = {withhold}",
            $"UPDATE opportunity.choice SET is_active = false WHERE workspace_id = '{ws}' AND choice_id = {other}",
            $"UPDATE opportunity.choice SET system_key = NULL WHERE workspace_id = '{ws}' AND choice_id = {other}",
        })
        {
            var act = () => db.InWorkspaceAsync(ws, async tx =>
            {
                await using var command = tx.Command(sql);
                await command.ExecuteNonQueryAsync(Ct);
            });
            (await act.Should().ThrowAsync<PostgresException>(sql)).Which.ConstraintName.Should().Be("choice_system");
        }
    }

    [Fact]
    public async Task Withhold_or_Redact_without_a_basis_is_rejected_and_every_change_is_a_coding_event()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var catalog = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        int Status(string key) => PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, key)!.Value;
        var attorneyClient = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.AttorneyClient)!.Value;
        var doc = (await db.InsertDocumentAsync(ws, "DOC0001")).DocumentId;

        foreach (var key in new[] { PrivilegeFields.Keys.Withhold, PrivilegeFields.Keys.Redact })
        {
            var refused = await db.Coding.ApplyAsync(Interactive(ws, doc, SetStatus(Status(key))), Ct);
            refused.Outcome.Should().Be(CodingWriteOutcome.Invalid);
            refused.Errors.Should().ContainSingle().Which.Should().BeEquivalentTo(
                new { Field = FieldKey.For(PrivilegeFields.Basis), Code = PrivilegeFields.BasisRequiredCode });
        }

        (await db.Coding.GetEventsAsync(new CodingEventQuery(ws) { DocumentId = doc }, Ct)).Events.Should().BeEmpty("a refused write writes nothing");
        (await db.Coding.ApplyAsync(Interactive(ws, doc, SetStatus(Status(PrivilegeFields.Keys.NeedsSecondLevelReview))), Ct))
            .Outcome.Should().Be(CodingWriteOutcome.Applied, "only Withhold and Redact need a basis");

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var withheld = await db.Coding.ApplyAsync(Interactive(ws, doc,
            SetStatus(Status(PrivilegeFields.Keys.Withhold)),
            CodingFieldOperation.AddChoices(PrivilegeFields.Basis, attorneyClient),
            CodingFieldOperation.Set(PrivilegeFields.Description, JsonValue.Create("Email seeking legal advice on the supply contract"))), Ct);
        withheld.Outcome.Should().Be(CodingWriteOutcome.Applied);
        withheld.TouchesSecurityAffectingField.Should().BeTrue();
        (await ClassesAsync(db, ws, doc)).Should().Equal(RestrictionClasses.Privileged);

        // Clearing the basis of a withheld document is refused as well.
        (await db.Coding.ApplyAsync(Interactive(ws, doc, CodingFieldOperation.RemoveChoices(PrivilegeFields.Basis, attorneyClient)), Ct))
            .Outcome.Should().Be(CodingWriteOutcome.Invalid);
        (await db.Coding.ApplyAsync(Interactive(ws, doc, SetStatus(Status(PrivilegeFields.Keys.NotPrivileged))), Ct))
            .Outcome.Should().Be(CodingWriteOutcome.Applied);
        (await ClassesAsync(db, ws, doc)).Should().BeEmpty();

        // AC 2: one CodingEvent per change, with actor, timestamp, prior and new value.
        var events = (await db.Coding.GetEventsAsync(new CodingEventQuery(ws) { DocumentId = doc, FieldId = PrivilegeFields.Status }, Ct)).Events;
        events.Select(e => (e.PriorValue?.ToJsonString(), e.NewValue?.ToJsonString())).Should().Equal(
            (null, Text(Status(PrivilegeFields.Keys.NeedsSecondLevelReview))),
            (Text(Status(PrivilegeFields.Keys.NeedsSecondLevelReview)), Text(Status(PrivilegeFields.Keys.Withhold))),
            (Text(Status(PrivilegeFields.Keys.Withhold)), Text(Status(PrivilegeFields.Keys.NotPrivileged))));
        events.Should().OnlyContain(e => e.ActorId == Reviewer && e.ActorType == CodingActorType.Human && e.OccurredAt > before
            && e.Kind == CodingEventKind.ValueChanged);
        (await db.Coding.GetEventsAsync(new CodingEventQuery(ws) { DocumentId = doc, FieldId = PrivilegeFields.Description }, Ct)).Events
            .Should().ContainSingle().Which.NewValue!.GetValue<string>().Should().StartWith("Email seeking");
    }

    [Fact]
    public async Task A_write_over_several_documents_leaves_a_document_without_a_basis_unchanged_and_reports_it()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var catalog = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        var withhold = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, PrivilegeFields.Keys.Withhold)!.Value;
        var workProduct = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.WorkProduct)!.Value;
        var withBasis = (await db.InsertDocumentAsync(ws, "DOC0001")).DocumentId;
        var withoutBasis = (await db.InsertDocumentAsync(ws, "DOC0002")).DocumentId;
        (await db.Coding.ApplyAsync(Interactive(ws, withBasis, CodingFieldOperation.AddChoices(PrivilegeFields.Basis, workProduct)), Ct))
            .Outcome.Should().Be(CodingWriteOutcome.Applied);

        var result = await db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "propagation-" + Guid.CreateVersion7().ToString("N"),
            Actor = new CodingActor(Reviewer, CodingActorType.SystemRule),
            Documents = [new CodingTarget(withBasis), new CodingTarget(withoutBasis)],
            Operations = [SetStatus(withhold)],
        }, Ct);

        result.Outcome.Should().Be(CodingWriteOutcome.Applied);
        result.Documents.Single(d => d.DocumentId == withBasis).Outcome.Should().Be(DocumentCodingOutcome.Changed);
        var rejected = result.Documents.Single(d => d.DocumentId == withoutBasis);
        rejected.Outcome.Should().Be(DocumentCodingOutcome.Rejected);
        rejected.Error!.Code.Should().Be(PrivilegeFields.BasisRequiredCode);
        (await db.Coding.GetCurrentAsync(ws, [withoutBasis], Ct)).Single().Fields.Should().BeEmpty();
        (await ClassesAsync(db, ws, withBasis)).Should().Equal(RestrictionClasses.Privileged);
        (await ClassesAsync(db, ws, withoutBasis)).Should().BeEmpty();
    }

    [Fact]
    public async Task Existing_workspaces_are_backfilled_by_the_migration_without_touching_their_custom_fields()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            await new PostgresMigrator(source, MigrationCatalog.Scripts.Where(s => s.Version < 45)).MigrateAsync(Ct);
        }

        // A V0044 workspace with its own "Privilege Status" coding field holding the slot the system field would take.
        var ws = Guid.CreateVersion7();
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                $"""
                INSERT INTO opportunity.workspace (workspace_id, name, matter_number, display_time_zone, control_number_case_sensitive)
                VALUES ('{ws}', 'Legacy matter', 'M-1', 'UTC', false);
                INSERT INTO opportunity.field_catalog_counter (workspace_id, next_field_id, next_choice_id) VALUES ('{ws}', 1001, 2);
                INSERT INTO opportunity.field_definition (workspace_id, field_id, name, field_type, storage, is_searchable, search_slot, capabilities)
                VALUES ('{ws}', 1000, 'Privilege Status', 7, 3, true, 'ch.s100', 266);
                INSERT INTO opportunity.choice (workspace_id, field_id, choice_id, name, sort_order) VALUES ('{ws}', 1000, 1, 'Privileged', 0);
                """, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            (await new PostgresMigrator(source).MigrateAsync(Ct)).Applied.Select(s => s.Version).Should().Contain(45);
        }

        await using var db = CoreSchemaDatabase.Over(connectionString, await CoreSchemaDatabase.CreateLoginAsync(connectionString, "opportunity_app"));
        var catalog = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        catalog.Find(1000)!.Name.Should().Be("Privilege Status", "the custom field is left alone");
        catalog.Find(1000)!.SearchSlot.Should().Be("ch.s100");
        catalog.ChoicesOf(1000).Should().ContainSingle().Which.ChoiceId.Should().Be(1);
        catalog.Find(PrivilegeFields.Status)!.Name.Should().Be("Privilege Status (System)");
        catalog.Find(PrivilegeFields.Status)!.SearchSlot.Should().Be("ch.s001", "its reserved slot was taken");
        catalog.Find(PrivilegeFields.Basis)!.Name.Should().Be("Privilege Basis");
        catalog.Find(PrivilegeFields.Basis)!.SearchSlot.Should().Be("ch.s099");
        catalog.ChoicesOf(PrivilegeFields.Status).Select(c => c.ChoiceId).Should().OnlyContain(id => id >= 2, "choice ids continue the counter");
        PrivilegeFields.BasisRequiredStatuses(catalog).Should().HaveCount(2);

        // Ensuring the fields again (as an import does) adds the missing structural system fields and nothing else.
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var again = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        again.Fields.Where(f => PrivilegeFields.IsPrivilegeField(f.FieldId)).Select(f => (f.FieldId, f.Name))
            .Should().Equal(catalog.Fields.Where(f => PrivilegeFields.IsPrivilegeField(f.FieldId)).OrderBy(f => f.FieldId).Select(f => (f.FieldId, f.Name)));
        again.ChoicesOf(PrivilegeFields.Status).Should().HaveCount(4);
    }

    [Fact]
    public async Task The_coding_api_refuses_a_withhold_without_a_basis_and_only_privilege_coders_edit_the_fields()
    {
        await using var core = await CoreSchemaDatabase.CreateAsync(postgres);
        var w = await CodingApiHarness.WorkspaceAsync(core);
        var reviewer = await CodingApiHarness.MemberAsync(core, w.Id, WorkspaceRole.Reviewer);
        var privilegeReviewer = await CodingApiHarness.MemberAsync(core, w.Id, WorkspaceRole.PrivilegeReviewer);
        var doc = await CodingApiHarness.DocumentAsync(core, w.Id);
        var catalog = await core.Fields.GetCatalogAsync(w.Id, cancellationToken: Ct);
        var withhold = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, PrivilegeFields.Keys.Withhold)!.Value;
        var commonInterest = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.CommonInterest)!.Value;
        await using var factory = CodingApiHarness.Factory(core.AppConnectionString);
        using var client = factory.CreateClient();
        var url = CodingApiHarness.CodingUrl(w.Id, doc);

        using (var read = await CodingApiHarness.GetAsync(client, url, reviewer))
        {
            var coding = await CodingApiHarness.JsonAsync(read);
            foreach (var fieldId in PrivilegeFields.ReservedSlots.Keys)
            {
                var field = CodingApiHarness.FieldOf(coding, fieldId);
                field.GetProperty("isSecurityAffecting").GetBoolean().Should().BeTrue();
                field.GetProperty("editable").GetBoolean().Should().BeFalse("reviewers lack Coding.WritePrivilege");
            }
        }

        using (var denied = await CodingApiHarness.PutAsync(client, url, reviewer, CodingApiHarness.Set(PrivilegeFields.Status, withhold), "\"1\""))
        {
            denied.StatusCode.Should().NotBe(HttpStatusCode.OK);
        }

        using (var invalid = await CodingApiHarness.PutAsync(client, url, privilegeReviewer, CodingApiHarness.Set(PrivilegeFields.Status, withhold), "\"1\""))
        {
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, await invalid.Content.ReadAsStringAsync(Ct));
            var problem = await CodingApiHarness.JsonAsync(invalid);
            problem.GetProperty("errors").TryGetProperty(FieldKey.For(PrivilegeFields.Basis), out _).Should().BeTrue();
            problem.GetProperty("fieldErrors")[0].GetProperty("code").GetString().Should().Be(PrivilegeFields.BasisRequiredCode);
        }

        using (var saved = await CodingApiHarness.PutAsync(client, url, privilegeReviewer,
            CodingApiHarness.Save((PrivilegeFields.Status, "set", withhold), (PrivilegeFields.Basis, "addChoices", new JsonArray(commonInterest))), "\"1\""))
        {
            saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task A_mass_edit_to_withhold_codes_documents_with_a_basis_and_reports_the_others_as_failed()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var user = await h.MemberAsync(w.Id, WorkspaceRole.PrivilegeReviewer);
        var docs = await h.DocumentsAsync(w.Id, 3);
        var catalog = await h.Db.Core.Fields.GetCatalogAsync(w.Id, cancellationToken: Ct);
        var withhold = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, PrivilegeFields.Keys.Withhold)!.Value;
        var attorneyClient = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.AttorneyClient)!.Value;
        (await h.InteractiveAsync(w.Id, user, docs[0], CodingFieldOperation.AddChoices(PrivilegeFields.Basis, attorneyClient)))
            .Outcome.Should().Be(CodingWriteOutcome.Applied);
        var snapshot = await h.SnapshotAsync(w.Id, user, docs);

        var job = await h.StartAsync(w.Id, user, snapshot.SnapshotId,
            new CodingChange(PrivilegeFields.Status, CodingOperationKind.Set, JsonValue.Create(withhold)));
        var done = await h.RunAsync(w.Id, job.JobId);

        done.Counters.ItemsApplied.Should().Be(1);
        done.Counters.ItemsFailed.Should().Be(2);
        var failed = await h.ReportAsync(w.Id, user, job.JobId, BulkCodingOutcome.Failed);
        failed.Items.Select(i => i.DocumentId).Should().BeEquivalentTo([docs[1], docs[2]]);
        failed.Items.Should().OnlyContain(i => i.ReasonCode == "PrivilegeBasisRequired");
        (await h.ValuesAsync(w.Id, docs, PrivilegeFields.Status)).Where(v => v.Value is not null).Should().ContainSingle().Which.Key.Should().Be(docs[0]);
    }

    private static string Text(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static CodingFieldOperation SetStatus(int choiceId) =>
        CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(choiceId));

    private static CodingWriteRequest Interactive(Guid ws, Guid documentId, params CodingFieldOperation[] operations) => new()
    {
        WorkspaceId = ws,
        IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
        Actor = new CodingActor(Reviewer, CodingActorType.Human),
        Documents = [new CodingTarget(documentId)],
        Operations = operations,
    };

    private static Task<List<string>> ClassesAsync(CoreSchemaDatabase db, Guid ws, Guid documentId) =>
        db.ColumnAsync($"SELECT class_key FROM opportunity.document_restriction WHERE workspace_id = '{ws}' AND document_id = '{documentId}' ORDER BY 1");
}
