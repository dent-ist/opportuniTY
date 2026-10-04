using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Fields;
using Opportunity.Core.Fields;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Documents;

/// <summary>E04-T03: field definitions, choices and coding layouts (ADR-003, ADR-007, Q-11).</summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class FieldCatalogTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Initializing_a_workspace_seeds_system_fields_and_one_default_layout_idempotently()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();

        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);

        var catalog = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        catalog.Fields.Should().HaveCount(SystemFields.Create(ws).Count);
        catalog.Fields.Should().OnlyContain(f => f.IsSystem && f.FieldId < 1000 && (f.Storage == FieldStorage.Column) == (f.ColumnName != null));
        catalog.Find(SystemFields.ControlNumber)!.Capabilities.Should().HaveFlag(FieldCapabilities.Sortable);

        var layouts = await db.Fields.GetLayoutsAsync(ws, cancellationToken: Ct);
        layouts.Should().ContainSingle().Which.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Custom_field_ids_start_at_1000_are_never_reused_and_get_typed_search_slots()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await InitializedWorkspaceAsync(db);

        var custodian = await CreateAsync(db, new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata, IsMultiValue: true));
        var subject = await CreateAsync(db, new NewField(ws, "Subject", FieldType.Text, FieldStorage.Metadata));
        var notes = await CreateAsync(db, new NewField(ws, "Reviewer Notes", FieldType.Text, FieldStorage.Coding));
        var tags = await CreateAsync(db, new NewField(ws, "Tags", FieldType.Keyword, FieldStorage.Metadata, IsSearchable: false));

        custodian.FieldId.Should().Be(1000);
        custodian.Key.Should().Be("f1000");
        custodian.SearchSlot.Should().Be("kw.s001");
        custodian.Capabilities.Should().HaveFlag(FieldCapabilities.Aggregatable);
        subject.SearchSlot.Should().Be("txt.s001");
        subject.TextAnalysis.Should().Be(TextAnalysis.Prose);
        notes.SearchSlot.Should().Be("txt.s001", "coding fields have their own slot namespace");
        tags.SearchSlot.Should().BeNull();
        tags.Capabilities.Should().Be(FieldCapabilities.None);

        (await db.Fields.DeleteFieldAsync(ws, tags.FieldId, Ct)).Succeeded.Should().BeTrue();
        var next = await CreateAsync(db, new NewField(ws, "Tags", FieldType.Keyword, FieldStorage.Metadata));
        next.FieldId.Should().Be(1004, "deleted ids are never reused (ADR-003 R4)");
        next.SearchSlot.Should().Be("kw.s002");

        // A deleted field keeps its slot (Draining) until its values are purged.
        (await db.Fields.DeleteFieldAsync(ws, custodian.FieldId, Ct)).Succeeded.Should().BeTrue();
        (await CreateAsync(db, new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata))).SearchSlot.Should().Be("kw.s003");
    }

    [Fact]
    public async Task Field_names_are_unique_case_insensitively_and_invalid_definitions_are_rejected()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await InitializedWorkspaceAsync(db);
        await CreateAsync(db, new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata));

        var duplicate = await db.Fields.CreateFieldAsync(new NewField(ws, " custodian ", FieldType.Text, FieldStorage.Metadata), Ct);
        duplicate.Outcome.Should().Be(CatalogOutcome.Invalid);
        duplicate.Errors.Single().Code.Should().Be("duplicate-name");

        // System field names count too.
        (await db.Fields.CreateFieldAsync(new NewField(ws, "control number", FieldType.Text, FieldStorage.Metadata), Ct))
            .Errors.Single().Code.Should().Be("duplicate-name");

        var invalid = await db.Fields.CreateFieldAsync(new NewField(ws, "Amount", FieldType.Decimal, FieldStorage.Metadata, DecimalPrecision: 20, DecimalScale: 2), Ct);
        invalid.Outcome.Should().Be(CatalogOutcome.Invalid);
        invalid.Errors.Single().Field.Should().Be("decimalPrecision");

        (await db.Fields.CreateFieldAsync(new NewField(ws, "Sneaky", FieldType.Text, FieldStorage.Column), Ct))
            .Errors.Single().Code.Should().Be("column-storage-reserved");

        (await db.Fields.CreateFieldAsync(new NewField(Guid.CreateVersion7(), "Orphan", FieldType.Text, FieldStorage.Metadata), Ct))
            .Outcome.Should().Be(CatalogOutcome.NotFound);
    }

    [Fact]
    public async Task Security_affecting_fields_are_flagged_with_their_class()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await InitializedWorkspaceAsync(db);

        var privilege = await CreateAsync(db, new NewField(ws, "Privilege Status", FieldType.SingleChoice, FieldStorage.Coding,
            SecurityClass: SecurityClass.PrivilegeStatus));
        var confidentiality = await CreateAsync(db, new NewField(ws, "Confidentiality", FieldType.SingleChoice, FieldStorage.Coding,
            SecurityClass: SecurityClass.ConfidentialityDesignation));
        var responsive = await CreateAsync(db, new NewField(ws, "Responsive", FieldType.Boolean, FieldStorage.Coding));

        var catalog = await db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        catalog.Fields.Where(f => f.IsSecurityAffecting).Select(f => (f.FieldId, f.SecurityClass)).Should().BeEquivalentTo(
            [(privilege.FieldId, (SecurityClass?)SecurityClass.PrivilegeStatus), (confidentiality.FieldId, (SecurityClass?)SecurityClass.ConfidentialityDesignation)]);
        catalog.Find(responsive.FieldId)!.IsSecurityAffecting.Should().BeFalse();

        // The database rejects a security flag without a class, or on a structural column.
        var act = () => db.ExecuteAsync(
            "UPDATE opportunity.field_definition SET is_security_affecting = true WHERE workspace_id = @ws AND field_id = @id",
            ("ws", ws), ("id", responsive.FieldId));
        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("field_definition_security_ck");
    }

    [Fact]
    public async Task Creating_or_renaming_a_field_or_choice_never_rewrites_document_rows()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await InitializedWorkspaceAsync(db);
        var custodian = await CreateAsync(db, new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata));
        var issue = await CreateAsync(db, new NewField(ws, "Issue", FieldType.SingleChoice, FieldStorage.Metadata));
        var choice = (await db.Fields.AddChoiceAsync(ws, issue.FieldId, "Pricing", Ct)).Value!;
        for (var i = 0; i < 20; i++)
        {
            await db.InsertDocumentAsync(ws, $"DOC{i:D4}", d => d.Metadata = $$"""{"f1000": "Custodian {{i}}", "f1001": {{choice.ChoiceId}}}""");
        }

        var before = await DocumentRowVersionsAsync(db, ws);

        (await db.Fields.UpdateFieldAsync(new FieldChange(ws, custodian.FieldId) { Name = "Record Owner", IsHidden = true }, Ct))
            .Value!.Name.Should().Be("Record Owner");
        (await db.Fields.RenameChoiceAsync(ws, issue.FieldId, choice.ChoiceId, "Price Fixing", Ct)).Value!.Name.Should().Be("Price Fixing");
        (await db.Fields.AddChoiceAsync(ws, issue.FieldId, "Market Allocation", Ct)).Succeeded.Should().BeTrue();
        await CreateAsync(db, new NewField(ws, "Date Produced", FieldType.Date, FieldStorage.Metadata, DatePrecision: DatePrecision.Date));

        (await DocumentRowVersionsAsync(db, ws)).Should().Equal(before);
        (await db.Documents.GetVersionAsync(ws, (await db.Reads.ListByControlNumberAsync(ws, null, 1, Ct))[0].DocumentId, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Type_changes_are_rejected_once_a_field_holds_values()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await InitializedWorkspaceAsync(db);
        var amount = await CreateAsync(db, new NewField(ws, "Amount", FieldType.Text, FieldStorage.Metadata));

        // No values yet: retype is allowed and moves the field to a slot of its new kind.
        var retyped = await db.Fields.UpdateFieldAsync(
            new FieldChange(ws, amount.FieldId) { Type = FieldType.Decimal, DecimalPrecision = 12, DecimalScale = 2 }, Ct);
        retyped.Value!.Type.Should().Be(FieldType.Decimal);
        retyped.Value.SearchSlot.Should().Be("dec.s001");
        retyped.Value.Capabilities.Should().HaveFlag(FieldCapabilities.Rangeable);

        await db.InsertDocumentAsync(ws, "DOC1", d => d.Metadata = """{"f1000": 12.5}""");

        var rejected = await db.Fields.UpdateFieldAsync(new FieldChange(ws, amount.FieldId) { Type = FieldType.Integer }, Ct);
        rejected.Outcome.Should().Be(CatalogOutcome.Conflict);
        rejected.Errors.Single().Should().Match<FieldError>(e => e.Code == "field-has-values" && e.Field == "f1000");
        (await db.Fields.UpdateFieldAsync(new FieldChange(ws, amount.FieldId) { DecimalScale = 4 }, Ct)).Outcome.Should().Be(CatalogOutcome.Conflict);

        // Renames remain allowed, and writers that bypass the repository hit the database guard.
        (await db.Fields.UpdateFieldAsync(new FieldChange(ws, amount.FieldId) { Name = "Invoice Amount" }, Ct)).Succeeded.Should().BeTrue();
        var direct = () => db.ExecuteAsync(
            "UPDATE opportunity.field_definition SET field_type = 2, decimal_precision = NULL, decimal_scale = NULL WHERE workspace_id = @ws AND field_id = @id",
            ("ws", ws), ("id", amount.FieldId));
        (await direct.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("field_definition_retype");

        // System fields are renameable and hideable but never retyped or deleted.
        (await db.Fields.UpdateFieldAsync(new FieldChange(ws, SystemFields.FileName) { Name = "Original File Name", IsHidden = true }, Ct))
            .Succeeded.Should().BeTrue();
        (await db.Fields.UpdateFieldAsync(new FieldChange(ws, SystemFields.FileName) { Type = FieldType.Keyword }, Ct))
            .Errors.Single().Code.Should().Be("system-field");
        (await db.Fields.DeleteFieldAsync(ws, SystemFields.FileName, Ct)).Errors.Single().Code.Should().Be("system-field");
    }

    [Fact]
    public async Task Choices_can_be_reordered_and_deactivated_and_only_unused_choices_deleted()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await InitializedWorkspaceAsync(db);
        var issues = await CreateAsync(db, new NewField(ws, "Issues", FieldType.MultiChoice, FieldStorage.Metadata));
        var a = (await db.Fields.AddChoiceAsync(ws, issues.FieldId, "Alpha", Ct)).Value!;
        var b = (await db.Fields.AddChoiceAsync(ws, issues.FieldId, "Beta", Ct)).Value!;
        var c = (await db.Fields.AddChoiceAsync(ws, issues.FieldId, "Gamma", Ct)).Value!;
        new[] { a.SortOrder, b.SortOrder, c.SortOrder }.Should().Equal(0, 1, 2);

        (await db.Fields.AddChoiceAsync(ws, issues.FieldId, " BETA ", Ct)).Errors.Single().Code.Should().Be("duplicate-name");
        (await db.Fields.AddChoiceAsync(ws, SystemFields.FileName, "x", Ct)).Errors.Single().Code.Should().Be("not-choice-field");

        var reordered = await db.Fields.ReorderChoicesAsync(ws, issues.FieldId, [c.ChoiceId, a.ChoiceId, b.ChoiceId], Ct);
        reordered.Value!.Select(x => x.Name).Should().Equal("Gamma", "Alpha", "Beta");
        (await db.Fields.ReorderChoicesAsync(ws, issues.FieldId, [c.ChoiceId, a.ChoiceId], Ct)).Errors.Single().Code.Should().Be("invalid-order");

        // Unused: deletable.
        (await db.Fields.DeleteChoiceAsync(ws, issues.FieldId, c.ChoiceId, Ct)).Succeeded.Should().BeTrue();

        // Held by a document: delete is refused; deactivation works and keeps the value valid.
        await db.InsertDocumentAsync(ws, "DOC1", d => d.Metadata = $$"""{"f1000": [{{a.ChoiceId}}]}""");
        var refused = await db.Fields.DeleteChoiceAsync(ws, issues.FieldId, a.ChoiceId, Ct);
        refused.Outcome.Should().Be(CatalogOutcome.Conflict);
        refused.Errors.Single().Code.Should().Be("choice-in-use");
        (await db.Fields.SetChoiceActiveAsync(ws, issues.FieldId, a.ChoiceId, false, Ct)).Value!.IsActive.Should().BeFalse();

        // Once used, a choice stays undeletable even after no document holds it any more (sticky first use).
        await db.ExecuteAsync(
            "UPDATE opportunity.choice SET first_used_at = now() WHERE workspace_id = @ws AND choice_id = @id", ("ws", ws), ("id", b.ChoiceId));
        (await db.Fields.DeleteChoiceAsync(ws, issues.FieldId, b.ChoiceId, Ct)).Errors.Single().Code.Should().Be("choice-in-use");

        // A choice-typed field with choices cannot become a non-choice field, even without values.
        var empty = await CreateAsync(db, new NewField(ws, "Status", FieldType.SingleChoice, FieldStorage.Coding));
        await db.Fields.AddChoiceAsync(ws, empty.FieldId, "Open", Ct);
        (await db.Fields.UpdateFieldAsync(new FieldChange(ws, empty.FieldId) { Type = FieldType.Keyword }, Ct))
            .Errors.Single().Code.Should().Be("field-has-choices");
    }

    [Fact]
    public async Task Layouts_with_conditional_required_fields_validate_and_round_trip()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await InitializedWorkspaceAsync(db);
        var privileged = await CreateAsync(db, new NewField(ws, "Privileged", FieldType.SingleChoice, FieldStorage.Coding,
            SecurityClass: SecurityClass.PrivilegeStatus));
        var yes = (await db.Fields.AddChoiceAsync(ws, privileged.FieldId, "Yes", Ct)).Value!;
        await db.Fields.AddChoiceAsync(ws, privileged.FieldId, "No", Ct);
        var basis = await CreateAsync(db, new NewField(ws, "Privilege Basis", FieldType.MultiChoice, FieldStorage.Coding));
        var custodian = await CreateAsync(db, new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata));

        var layout = new CodingLayout
        {
            WorkspaceId = ws,
            Name = "Privilege Review",
            Roles = { "admin", "privilege-reviewer" },
            Sections =
            {
                new CodingLayoutSection
                {
                    SectionId = Guid.CreateVersion7(),
                    Title = "Document",
                    Fields = { new CodingLayoutField { FieldId = custodian.FieldId, IsReadOnly = true } },
                },
                new CodingLayoutSection
                {
                    SectionId = Guid.CreateVersion7(),
                    Title = "Privilege",
                    Fields =
                    {
                        new CodingLayoutField { FieldId = privileged.FieldId, IsRequired = true },
                        new CodingLayoutField
                        {
                            FieldId = basis.FieldId,
                            IsRequired = true,
                            VisibleWhen = new VisibilityCondition(privileged.FieldId, [yes.ChoiceId]),
                        },
                    },
                },
            },
        };

        var saved = await db.Fields.SaveLayoutAsync(layout, Ct);
        saved.Succeeded.Should().BeTrue(string.Join("; ", saved.Errors));
        var loaded = await db.Fields.GetLayoutAsync(ws, saved.Value!.LayoutId, Ct);
        loaded.Should().BeEquivalentTo(layout, o => o.Excluding(l => l.UpdatedAt).Excluding(l => l.LayoutId).WithStrictOrdering());
        loaded!.Sections[1].Fields[1].VisibleWhen!.ChoiceIds.Should().Equal(yes.ChoiceId);

        // Role assignment: the role's layouts plus the default.
        (await db.Fields.GetLayoutsAsync(ws, "privilege-reviewer", Ct)).Select(l => l.Name).Should().Equal("Default", "Privilege Review");
        (await db.Fields.GetLayoutsAsync(ws, "reviewer", Ct)).Select(l => l.Name).Should().Equal("Default");

        // Invalid structure is rejected with field-level errors and nothing is written.
        layout.Sections[1].Fields[1].VisibleWhen = new VisibilityCondition(custodian.FieldId, [yes.ChoiceId]);
        var invalid = await db.Fields.SaveLayoutAsync(layout, Ct);
        invalid.Outcome.Should().Be(CatalogOutcome.Invalid);
        invalid.Errors.Should().ContainSingle(e => e.Field == basis.Key && e.Code == "invalid-condition-field");
        (await db.Fields.GetLayoutAsync(ws, layout.LayoutId, Ct))!.Sections[1].Fields[1].VisibleWhen!.FieldId.Should().Be(privileged.FieldId);

        // Exactly one default: promoting this layout demotes the old one; the default cannot be deleted or un-defaulted.
        layout.Sections[1].Fields[1].VisibleWhen = new VisibilityCondition(privileged.FieldId, [yes.ChoiceId]);
        layout.IsDefault = true;
        (await db.Fields.SaveLayoutAsync(layout, Ct)).Succeeded.Should().BeTrue();
        (await db.Fields.GetLayoutsAsync(ws, cancellationToken: Ct)).Where(l => l.IsDefault).Select(l => l.Name).Should().Equal("Privilege Review");
        (await db.Fields.DeleteLayoutAsync(ws, layout.LayoutId, Ct)).Errors.Single().Code.Should().Be("default-layout");
        layout.IsDefault = false;
        (await db.Fields.SaveLayoutAsync(layout, Ct)).Errors.Single().Code.Should().Be("default-required");
        var oldDefault = (await db.Fields.GetLayoutsAsync(ws, cancellationToken: Ct)).Single(l => l.Name == "Default");
        (await db.Fields.DeleteLayoutAsync(ws, oldDefault.LayoutId, Ct)).Succeeded.Should().BeTrue();

        // Deleting a field removes it from layouts and drops conditions that depended on it.
        (await db.Fields.DeleteFieldAsync(ws, privileged.FieldId, Ct)).Succeeded.Should().BeTrue();
        var afterDelete = (await db.Fields.GetLayoutAsync(ws, layout.LayoutId, Ct))!;
        afterDelete.AllFields.Select(f => f.FieldId).Should().Equal(custodian.FieldId, basis.FieldId);
        afterDelete.AllFields.Should().OnlyContain(f => f.VisibleWhen == null);
    }

    [Fact]
    public async Task Field_definitions_choices_and_layouts_are_workspace_scoped()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var wsA = await InitializedWorkspaceAsync(db);
        var wsB = await InitializedWorkspaceAsync(db);
        var issueA = await CreateAsync(db, new NewField(wsA, "Issue", FieldType.SingleChoice, FieldStorage.Coding));
        var issueB = await CreateAsync(db, new NewField(wsB, "Issue", FieldType.SingleChoice, FieldStorage.Coding));
        issueB.FieldId.Should().Be(issueA.FieldId, "field ids are per workspace");
        var choiceA = (await db.Fields.AddChoiceAsync(wsA, issueA.FieldId, "Pricing", Ct)).Value!;

        (await db.Fields.GetCatalogAsync(wsB, cancellationToken: Ct)).ChoicesOf(issueB.FieldId).Should().BeEmpty();
        (await db.Fields.RenameChoiceAsync(wsB, issueB.FieldId, choiceA.ChoiceId, "Hijacked", Ct)).Outcome.Should().Be(CatalogOutcome.NotFound);
        (await db.Fields.DeleteChoiceAsync(wsB, issueB.FieldId, choiceA.ChoiceId, Ct)).Outcome.Should().Be(CatalogOutcome.NotFound);

        // Composite keys make cross-workspace references impossible at the database level.
        var crossChoice = () => db.ExecuteAsync(
            "INSERT INTO opportunity.choice (workspace_id, field_id, choice_id, name, sort_order) VALUES (@ws, 4242, 99, 'x', 0)", ("ws", wsB));
        (await crossChoice.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().BeOneOf(
            PostgresErrorCodes.ForeignKeyViolation, PostgresErrorCodes.IntegrityConstraintViolation);

        var layoutB = (await db.Fields.GetLayoutsAsync(wsB, cancellationToken: Ct)).Single();
        var sectionId = Guid.CreateVersion7();
        await db.ExecuteAsync(
            "INSERT INTO opportunity.coding_layout_section (workspace_id, layout_id, section_id, title, sort_order) VALUES (@ws, @layout, @section, 'S', 0)",
            ("ws", wsB), ("layout", layoutB.LayoutId), ("section", sectionId));
        var crossLayoutField = () => db.ExecuteAsync(
            "INSERT INTO opportunity.coding_layout_field (workspace_id, layout_id, field_id, section_id, sort_order) VALUES (@ws, @layout, 1001, @section, 0)",
            ("ws", wsA), ("layout", layoutB.LayoutId), ("section", sectionId));
        (await crossLayoutField.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    private static async Task<Guid> InitializedWorkspaceAsync(CoreSchemaDatabase db)
    {
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        return ws;
    }

    private static async Task<FieldDefinition> CreateAsync(CoreSchemaDatabase db, NewField field)
    {
        var result = await db.Fields.CreateFieldAsync(field, Ct);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors));
        return result.Value!;
    }

    private static Task<List<string>> DocumentRowVersionsAsync(CoreSchemaDatabase db, Guid ws) =>
        db.ColumnAsync(
            $"SELECT document_id::text || ':' || xmin::text || ':' || updated_at::text FROM opportunity.document WHERE workspace_id = '{ws}' ORDER BY document_id");
}
