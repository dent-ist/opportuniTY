using System.Globalization;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Fields;
using Opportunity.Core.Fields;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Fields;

/// <summary>
/// PostgreSQL implementation of <see cref="IFieldCatalogRepository"/> over <c>field_definition</c>, <c>choice</c> and
/// the <c>coding_layout*</c> tables (V0003). Nothing here touches <c>document</c>: renames and choice edits are O(1)
/// (ADR-003). Database triggers back up the retype and choice-deletion rules for writers that bypass this class.
/// </summary>
public sealed class FieldCatalogRepository(NpgsqlDataSource dataSource) : IFieldCatalogRepository
{
    private const string FieldColumns =
        """
        workspace_id, field_id, name, description, field_type, storage, is_system, is_multi_value, date_precision,
        decimal_precision, decimal_scale, text_analysis, is_security_affecting, security_class, is_searchable,
        search_slot, capabilities, column_name, is_hidden, is_deleted, deleted_at, created_at, updated_at, version
        """;

    private const string ChoiceColumns =
        "workspace_id, field_id, choice_id, name, sort_order, is_active, first_used_at, system_key";

    private const string RetypeConstraint = "field_definition_retype";
    private const string SystemChoiceCode = "system-choice";
    private const string SystemChoiceMessage = "Built-in choices can be renamed and reordered, but not deactivated or deleted.";
    private const string ChoiceInUseConstraint = "choice_in_use";

    public Task InitializeWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        InitializeAsync(workspaceId, seedTemplate: false, cancellationToken);

    public Task InitializeNewWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        InitializeAsync(workspaceId, seedTemplate: true, cancellationToken);

    private async Task InitializeAsync(Guid workspaceId, bool seedTemplate, CancellationToken cancellationToken)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var counter = tx.Command(
            "INSERT INTO opportunity.field_catalog_counter (workspace_id) VALUES (@ws) ON CONFLICT (workspace_id) DO NOTHING"))
        {
            counter.Parameters.AddWithValue("ws", workspaceId);
            await counter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Re-running adds system fields introduced since the workspace was created. One whose name a custom field
        // already uses (e.g. an "All Custodians" created by an earlier import) is left out: the custom field keeps working.
        foreach (var field in SystemFields.Create(workspaceId))
        {
            if (!await NameTakenAsync(tx, workspaceId, field.Name, field.FieldId, cancellationToken).ConfigureAwait(false))
            {
                await InsertFieldAsync(tx, field, onConflictDoNothing: true, cancellationToken).ConfigureAwait(false);
            }
        }

        // E13-T01: the privilege fields, their built-in choices and the Privileged class binding (V0045, idempotent).
        await using (var privilege = tx.Command("SELECT opportunity.provision_privilege_fields(@ws)"))
        {
            privilege.Parameters.AddWithValue("ws", workspaceId);
            await privilege.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (seedTemplate)
        {
            await WorkspaceTemplate.SeedAsync(tx, workspaceId, cancellationToken).ConfigureAwait(false);
        }

        await using (var layout = tx.Command(
            """
            INSERT INTO opportunity.coding_layout (workspace_id, layout_id, name, is_default)
            SELECT @ws, @id, 'Default', true
            WHERE NOT EXISTS (SELECT FROM opportunity.coding_layout WHERE workspace_id = @ws AND is_default)
            """))
        {
            layout.Parameters.AddWithValue("ws", workspaceId);
            layout.Parameters.AddWithValue("id", Guid.CreateVersion7());
            await layout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FieldCatalog> GetCatalogAsync(Guid workspaceId, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var catalog = await LoadCatalogAsync(tx, workspaceId, includeDeleted, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return catalog;
    }

    internal static async Task<FieldCatalog> LoadCatalogAsync(
        WorkspaceTransaction tx, Guid workspaceId, bool includeDeleted, CancellationToken cancellationToken)
        => await LoadCatalogAsync(tx, workspaceId, includeDeleted, null, false, cancellationToken).ConfigureAwait(false);

    internal static async Task<FieldCatalog> LoadCatalogAsync(
        WorkspaceTransaction tx, Guid workspaceId, bool includeDeleted, int[]? fieldIds, bool lockShared, CancellationToken cancellationToken)
    {
        var filter = (includeDeleted ? string.Empty : " AND NOT is_deleted") + (fieldIds is null ? string.Empty : " AND field_id = ANY(@ids)");
        var fields = new List<FieldDefinition>();
        await using (var command = tx.Command(
            $"SELECT {FieldColumns} FROM opportunity.field_definition WHERE workspace_id = @ws{filter} ORDER BY field_id"
            + (lockShared ? " FOR SHARE" : string.Empty)))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            if (fieldIds is not null)
            {
                command.Parameters.AddWithValue("ids", fieldIds);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                fields.Add(ReadField(reader));
            }
        }

        var choices = new List<Choice>();
        await using (var command = tx.Command(
            $"SELECT {ChoiceColumns} FROM opportunity.choice WHERE workspace_id = @ws AND field_id = ANY(@ids) ORDER BY field_id, sort_order"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", fields.Where(f => f.IsChoice).Select(f => f.FieldId).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                choices.Add(ReadChoice(reader));
            }
        }

        return new FieldCatalog(fields, choices);
    }

    public Task<CatalogResult<FieldDefinition>> CreateFieldAsync(NewField field, CancellationToken cancellationToken = default) =>
        CreateFieldAsync(field, CatalogWrite.None, cancellationToken);

    public async Task<CatalogResult<FieldDefinition>> CreateFieldAsync(NewField field, CatalogWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(write);
        var definition = new FieldDefinition
        {
            WorkspaceId = field.WorkspaceId,
            Name = field.Name?.Trim() ?? string.Empty,
            Description = field.Description,
            Type = field.Type,
            Storage = field.Storage,
            IsMultiValue = field.IsMultiValue || field.Type == FieldType.MultiChoice,
            DatePrecision = field.DatePrecision,
            DecimalPrecision = field.DecimalPrecision,
            DecimalScale = field.DecimalScale,
            TextAnalysis = field.Type == FieldType.Text ? field.TextAnalysis ?? TextAnalysis.Prose : field.TextAnalysis,
            IsSecurityAffecting = field.SecurityClass.HasValue,
            SecurityClass = field.SecurityClass,
            IsSearchable = field.IsSearchable,
        };

        var errors = FieldRules.ValidateDefinition(definition);
        if (errors.Count > 0)
        {
            return CatalogResult.Invalid<FieldDefinition>(errors);
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, field.WorkspaceId, cancellationToken).ConfigureAwait(false);
        int fieldId;
        await using (var counter = tx.Command(
            """
            UPDATE opportunity.field_catalog_counter SET next_field_id = next_field_id + 1
            WHERE workspace_id = @ws RETURNING next_field_id - 1
            """))
        {
            counter.Parameters.AddWithValue("ws", field.WorkspaceId);
            if (await counter.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not int allocated)
            {
                return CatalogResult.NotFound<FieldDefinition>();
            }

            fieldId = allocated;
        }

        var customCount = await ScalarAsync<long>(tx,
            "SELECT count(*) FROM opportunity.field_definition WHERE workspace_id = @ws AND NOT is_system AND NOT is_deleted",
            [("ws", field.WorkspaceId)], cancellationToken).ConfigureAwait(false);
        if (customCount >= FieldLimits.MaxCustomFieldsPerWorkspace)
        {
            return CatalogResult.Invalid<FieldDefinition>(
                [new("fieldId", "field-limit", $"A workspace has at most {FieldLimits.MaxCustomFieldsPerWorkspace} custom fields.")]);
        }

        if (await NameTakenAsync(tx, field.WorkspaceId, definition.Name, null, cancellationToken).ConfigureAwait(false))
        {
            return CatalogResult.Invalid<FieldDefinition>([DuplicateName(definition.Name)]);
        }

        definition.FieldId = fieldId;
        definition.SearchSlot = definition.IsSearchable
            ? await AllocateSlotAsync(tx, definition, null, cancellationToken).ConfigureAwait(false)
            : null;
        definition.Capabilities = FieldRules.CapabilitiesForSlot(definition.SearchSlot);
        var created = await InsertFieldAsync(tx, definition, onConflictDoNothing: false, cancellationToken).ConfigureAwait(false);
        await AuditAsync(tx, write, FieldResource(created!.FieldId), created.Version, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CatalogResult.Ok<FieldDefinition>(created!);
    }

    public Task<CatalogResult<FieldDefinition>> UpdateFieldAsync(FieldChange change, CancellationToken cancellationToken = default) =>
        UpdateFieldAsync(change, CatalogWrite.None, cancellationToken);

    public async Task<CatalogResult<FieldDefinition>> UpdateFieldAsync(FieldChange change, CatalogWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(write);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, change.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var current = await LockFieldAsync(tx, change.WorkspaceId, change.FieldId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.IsDeleted)
        {
            return CatalogResult.NotFound<FieldDefinition>();
        }

        if (Stale(current.Version, write))
        {
            return CatalogResult.VersionConflict<FieldDefinition>();
        }

        var updated = Copy(current);
        updated.Name = change.Name?.Trim() ?? current.Name;
        // An empty description clears it; null leaves it unchanged.
        updated.Description = change.Description is null ? current.Description
            : string.IsNullOrWhiteSpace(change.Description) ? null : change.Description.Trim();
        updated.IsHidden = change.IsHidden ?? current.IsHidden;
        if (change.Type is { } type && type != current.Type)
        {
            // A new type starts from that type's defaults unless the change supplies them.
            updated.Type = type;
            updated.IsMultiValue = type == FieldType.MultiChoice || (type is FieldType.Text or FieldType.Keyword && current.IsMultiValue);
            updated.DatePrecision = type == FieldType.Date ? Core.Fields.DatePrecision.DateTime : null;
            updated.DecimalPrecision = type == FieldType.Decimal ? FieldLimits.MaxDecimalPrecision : null;
            updated.DecimalScale = type == FieldType.Decimal ? (short)2 : null;
            updated.TextAnalysis = type == FieldType.Text ? current.TextAnalysis ?? TextAnalysis.Prose : null;
        }

        updated.IsMultiValue = change.IsMultiValue ?? updated.IsMultiValue;
        updated.DatePrecision = change.DatePrecision ?? updated.DatePrecision;
        updated.DecimalPrecision = change.DecimalPrecision ?? updated.DecimalPrecision;
        updated.DecimalScale = change.DecimalScale ?? updated.DecimalScale;

        var errors = FieldRules.ValidateDefinition(updated);
        if (errors.Count > 0)
        {
            return CatalogResult.Invalid<FieldDefinition>(errors);
        }

        var retyped = updated.Type != current.Type || updated.IsMultiValue != current.IsMultiValue
            || updated.DatePrecision != current.DatePrecision || updated.DecimalPrecision != current.DecimalPrecision
            || updated.DecimalScale != current.DecimalScale;
        if (retyped)
        {
            if (current.IsSystem)
            {
                return CatalogResult.Conflict<FieldDefinition>(new(current.Key, "system-field", "System fields cannot be retyped (ADR-003 R3)."));
            }

            if (await ScalarAsync<bool>(tx, "SELECT opportunity.field_has_values(@ws, @id, @storage)",
            [("ws", current.WorkspaceId), ("id", current.FieldId), ("storage", (short)current.Storage)], cancellationToken).ConfigureAwait(false))
            {
                return CatalogResult.Conflict<FieldDefinition>(new(current.Key, "field-has-values",
                    "The field holds values; its type cannot change. Create a new field instead (ADR-003 R7)."));
            }

            if (!updated.IsChoice && await ScalarAsync<bool>(tx,
                    "SELECT EXISTS (SELECT FROM opportunity.choice WHERE workspace_id = @ws AND field_id = @id)",
            [("ws", current.WorkspaceId), ("id", current.FieldId)], cancellationToken).ConfigureAwait(false))
            {
                return CatalogResult.Conflict<FieldDefinition>(new(current.Key, "field-has-choices", "Delete the field's choices first."));
            }

            var kindChanged = current.SearchSlot is not null && current.SearchSlot != FieldRules.OverflowSlot
                && !current.SearchSlot.StartsWith(FieldRules.SlotKind(updated.Type, updated.TextAnalysis) + ".", StringComparison.Ordinal);
            if (kindChanged || current.SearchSlot == FieldRules.OverflowSlot)
            {
                updated.SearchSlot = null; // releases the old slot before choosing a new one
                updated.SearchSlot = await AllocateSlotAsync(tx, updated, updated.FieldId, cancellationToken).ConfigureAwait(false);
            }

            updated.Capabilities = FieldRules.CapabilitiesForSlot(updated.SearchSlot);
        }

        if (!string.Equals(updated.Name, current.Name, StringComparison.OrdinalIgnoreCase)
            && await NameTakenAsync(tx, current.WorkspaceId, updated.Name, current.FieldId, cancellationToken).ConfigureAwait(false))
        {
            return CatalogResult.Invalid<FieldDefinition>([DuplicateName(updated.Name)]);
        }

        try
        {
            await using var command = tx.Command(
                $"""
                UPDATE opportunity.field_definition SET
                    name = @name, description = @description, is_hidden = @hidden, field_type = @type,
                    is_multi_value = @multi, date_precision = @date_precision, decimal_precision = @decimal_precision,
                    decimal_scale = @decimal_scale, text_analysis = @text_analysis, search_slot = @slot,
                    capabilities = @capabilities, updated_at = now(), version = version + 1
                WHERE workspace_id = @ws AND field_id = @id
                RETURNING {FieldColumns}
                """);
            command.Parameters.AddWithValue("ws", updated.WorkspaceId);
            command.Parameters.AddWithValue("id", updated.FieldId);
            command.Parameters.AddWithValue("name", updated.Name);
            command.Parameters.Add(Nullable("description", NpgsqlDbType.Text, updated.Description));
            command.Parameters.AddWithValue("hidden", updated.IsHidden);
            command.Parameters.AddWithValue("type", (short)updated.Type);
            command.Parameters.AddWithValue("multi", updated.IsMultiValue);
            command.Parameters.Add(Nullable("date_precision", NpgsqlDbType.Smallint, (short?)updated.DatePrecision));
            command.Parameters.Add(Nullable("decimal_precision", NpgsqlDbType.Smallint, updated.DecimalPrecision));
            command.Parameters.Add(Nullable("decimal_scale", NpgsqlDbType.Smallint, updated.DecimalScale));
            command.Parameters.Add(Nullable("text_analysis", NpgsqlDbType.Smallint, (short?)updated.TextAnalysis));
            command.Parameters.Add(Nullable("slot", NpgsqlDbType.Text, updated.SearchSlot));
            command.Parameters.AddWithValue("capabilities", (int)updated.Capabilities);
            var result = await ReadSingleFieldAsync(command, cancellationToken).ConfigureAwait(false);
            await AuditAsync(tx, write, FieldResource(result!.FieldId), result.Version, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CatalogResult.Ok<FieldDefinition>(result);
        }
        catch (PostgresException ex) when (ex.ConstraintName == RetypeConstraint)
        {
            return CatalogResult.Conflict<FieldDefinition>(new(current.Key, "field-has-values", ex.MessageText));
        }
    }

    public Task<CatalogResult<FieldDefinition>> DeleteFieldAsync(Guid workspaceId, int fieldId, CancellationToken cancellationToken = default) =>
        DeleteFieldAsync(workspaceId, fieldId, CatalogWrite.None, cancellationToken);

    public async Task<CatalogResult<FieldDefinition>> DeleteFieldAsync(
        Guid workspaceId, int fieldId, CatalogWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var current = await LockFieldAsync(tx, workspaceId, fieldId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.IsDeleted)
        {
            return CatalogResult.NotFound<FieldDefinition>();
        }

        if (Stale(current.Version, write))
        {
            return CatalogResult.VersionConflict<FieldDefinition>();
        }

        if (current.IsSystem)
        {
            return CatalogResult.Conflict<FieldDefinition>(new(current.Key, "system-field", "System fields cannot be deleted; hide them instead."));
        }

        // Layouts drop the field; conditions that depended on it are removed. The search slot stays reserved
        // (Draining) until the purge job has removed the values (ADR-003 R6, ADR-007 R4).
        await using (var layouts = tx.Command(
            """
            UPDATE opportunity.coding_layout l SET version = l.version + 1, updated_at = now()
            WHERE l.workspace_id = @ws
              AND EXISTS (SELECT FROM opportunity.coding_layout_field f
                          WHERE f.workspace_id = l.workspace_id AND f.layout_id = l.layout_id
                            AND (f.field_id = @id OR f.condition_field_id = @id));
            UPDATE opportunity.coding_layout_field SET condition_field_id = NULL, condition_choice_ids = NULL, condition_boolean = NULL
            WHERE workspace_id = @ws AND condition_field_id = @id;
            DELETE FROM opportunity.coding_layout_field WHERE workspace_id = @ws AND field_id = @id;
            """))
        {
            layouts.Parameters.AddWithValue("ws", workspaceId);
            layouts.Parameters.AddWithValue("id", fieldId);
            await layouts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = tx.Command(
            $"""
            UPDATE opportunity.field_definition SET is_deleted = true, deleted_at = now(), updated_at = now(), version = version + 1
            WHERE workspace_id = @ws AND field_id = @id RETURNING {FieldColumns}
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", fieldId);
        var deleted = await ReadSingleFieldAsync(command, cancellationToken).ConfigureAwait(false);
        await AuditAsync(tx, write, FieldResource(fieldId), deleted!.Version, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CatalogResult.Ok<FieldDefinition>(deleted!);
    }

    public Task<CatalogResult<Choice>> AddChoiceAsync(Guid workspaceId, int fieldId, string name, CancellationToken cancellationToken = default) =>
        AddChoiceAsync(workspaceId, fieldId, name, CatalogWrite.None, cancellationToken);

    public async Task<CatalogResult<Choice>> AddChoiceAsync(
        Guid workspaceId, int fieldId, string name, CatalogWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var trimmed = name?.Trim() ?? string.Empty;
        if (InvalidChoiceName(trimmed) is { } nameError)
        {
            return CatalogResult.Invalid<Choice>([nameError]);
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var field = await LockFieldAsync(tx, workspaceId, fieldId, cancellationToken).ConfigureAwait(false);
        if (field is null || field.IsDeleted)
        {
            return CatalogResult.NotFound<Choice>();
        }

        if (Stale(field.Version, write))
        {
            return CatalogResult.VersionConflict<Choice>();
        }

        if (!field.IsChoice)
        {
            return CatalogResult.Invalid<Choice>([new(field.Key, "not-choice-field", $"{field.Name} is not a choice field.")]);
        }

        var (count, taken) = await ChoiceStatsAsync(tx, workspaceId, fieldId, trimmed, null, cancellationToken).ConfigureAwait(false);
        if (taken)
        {
            return CatalogResult.Invalid<Choice>([new("name", "duplicate-name", $"A choice named '{trimmed}' already exists.")]);
        }

        if (count >= FieldLimits.MaxChoicesPerField)
        {
            return CatalogResult.Invalid<Choice>([new(field.Key, "choice-limit", $"A field has at most {FieldLimits.MaxChoicesPerField} choices.")]);
        }

        await using var command = tx.Command(
            $"""
            WITH id AS (
                UPDATE opportunity.field_catalog_counter SET next_choice_id = next_choice_id + 1
                WHERE workspace_id = @ws RETURNING next_choice_id - 1 AS choice_id)
            INSERT INTO opportunity.choice (workspace_id, field_id, choice_id, name, sort_order)
            SELECT @ws, @field, id.choice_id, @name,
                   coalesce((SELECT max(sort_order) + 1 FROM opportunity.choice WHERE workspace_id = @ws AND field_id = @field), 0)
            FROM id
            RETURNING {ChoiceColumns}
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("field", fieldId);
        command.Parameters.AddWithValue("name", trimmed);
        var choice = await ReadSingleChoiceAsync(command, cancellationToken).ConfigureAwait(false);
        await FinishFieldChangeAsync(tx, workspaceId, fieldId, write, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CatalogResult.Ok<Choice>(choice!);
    }

    public Task<CatalogResult<Choice>> RenameChoiceAsync(
        Guid workspaceId, int fieldId, int choiceId, string name, CancellationToken cancellationToken = default) =>
        UpdateChoiceAsync(workspaceId, fieldId, choiceId, name ?? string.Empty, null, CatalogWrite.None, cancellationToken);

    public Task<CatalogResult<Choice>> SetChoiceActiveAsync(
        Guid workspaceId, int fieldId, int choiceId, bool isActive, CancellationToken cancellationToken = default) =>
        UpdateChoiceAsync(workspaceId, fieldId, choiceId, null, isActive, CatalogWrite.None, cancellationToken);

    public async Task<CatalogResult<Choice>> UpdateChoiceAsync(
        Guid workspaceId, int fieldId, int choiceId, string? name, bool? isActive, CatalogWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var trimmed = name?.Trim();
        if (trimmed is not null && InvalidChoiceName(trimmed) is { } nameError)
        {
            return CatalogResult.Invalid<Choice>([nameError]);
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var field = await LockFieldAsync(tx, workspaceId, fieldId, cancellationToken).ConfigureAwait(false);
        if (field is null || field.IsDeleted)
        {
            return CatalogResult.NotFound<Choice>();
        }

        if (Stale(field.Version, write))
        {
            return CatalogResult.VersionConflict<Choice>();
        }

        if (trimmed is not null)
        {
            var (_, taken) = await ChoiceStatsAsync(tx, workspaceId, fieldId, trimmed, choiceId, cancellationToken).ConfigureAwait(false);
            if (taken)
            {
                return CatalogResult.Invalid<Choice>([new("name", "duplicate-name", $"A choice named '{trimmed}' already exists.")]);
            }
        }

        if (isActive == false && await IsSystemChoiceAsync(tx, workspaceId, fieldId, choiceId, cancellationToken).ConfigureAwait(false))
        {
            return CatalogResult.Conflict<Choice>(new(field.Key, SystemChoiceCode, SystemChoiceMessage));
        }

        Choice? choice;
        await using (var command = tx.Command(
            $"""
            UPDATE opportunity.choice SET name = coalesce(@name, name), is_active = coalesce(@active, is_active), updated_at = now()
            WHERE workspace_id = @ws AND field_id = @field AND choice_id = @choice
            RETURNING {ChoiceColumns}
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("field", fieldId);
            command.Parameters.AddWithValue("choice", choiceId);
            command.Parameters.Add(Nullable("name", NpgsqlDbType.Text, trimmed));
            command.Parameters.Add(Nullable("active", NpgsqlDbType.Boolean, isActive));
            choice = await ReadSingleChoiceAsync(command, cancellationToken).ConfigureAwait(false);
        }

        if (choice is null)
        {
            return CatalogResult.NotFound<Choice>();
        }

        await FinishFieldChangeAsync(tx, workspaceId, fieldId, write, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CatalogResult.Ok<Choice>(choice);
    }

    public Task<CatalogResult<IReadOnlyList<Choice>>> ReorderChoicesAsync(
        Guid workspaceId, int fieldId, IReadOnlyList<int> orderedChoiceIds, CancellationToken cancellationToken = default) =>
        ReorderChoicesAsync(workspaceId, fieldId, orderedChoiceIds, CatalogWrite.None, cancellationToken);

    public async Task<CatalogResult<IReadOnlyList<Choice>>> ReorderChoicesAsync(
        Guid workspaceId, int fieldId, IReadOnlyList<int> orderedChoiceIds, CatalogWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedChoiceIds);
        ArgumentNullException.ThrowIfNull(write);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var field = await LockFieldAsync(tx, workspaceId, fieldId, cancellationToken).ConfigureAwait(false);
        if (field is null || field.IsDeleted)
        {
            return CatalogResult.NotFound<IReadOnlyList<Choice>>();
        }

        if (Stale(field.Version, write))
        {
            return CatalogResult.VersionConflict<IReadOnlyList<Choice>>();
        }

        var catalog = await LoadCatalogAsync(tx, workspaceId, false, [fieldId], false, cancellationToken).ConfigureAwait(false);
        var existing = catalog.ChoicesOf(fieldId).Select(c => c.ChoiceId).Order().ToList();
        if (!existing.SequenceEqual(orderedChoiceIds.Order()))
        {
            return CatalogResult.Invalid<IReadOnlyList<Choice>>(
                [new(field.Key, "invalid-order", "The order must list every choice of the field exactly once.")]);
        }

        await using (var command = tx.Command(
            """
            UPDATE opportunity.choice c SET sort_order = o.ord - 1, updated_at = now()
            FROM unnest(@ids::integer[]) WITH ORDINALITY AS o(choice_id, ord)
            WHERE c.workspace_id = @ws AND c.field_id = @field AND c.choice_id = o.choice_id
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("field", fieldId);
            command.Parameters.AddWithValue("ids", orderedChoiceIds.ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var reordered = await LoadCatalogAsync(tx, workspaceId, false, [fieldId], false, cancellationToken).ConfigureAwait(false);
        await FinishFieldChangeAsync(tx, workspaceId, fieldId, write, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CatalogResult.Ok<IReadOnlyList<Choice>>(reordered.ChoicesOf(fieldId));
    }

    public Task<CatalogResult<Choice>> DeleteChoiceAsync(Guid workspaceId, int fieldId, int choiceId, CancellationToken cancellationToken = default) =>
        DeleteChoiceAsync(workspaceId, fieldId, choiceId, CatalogWrite.None, cancellationToken);

    public async Task<CatalogResult<Choice>> DeleteChoiceAsync(
        Guid workspaceId, int fieldId, int choiceId, CatalogWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var key = FieldKey.For(fieldId);
        var field = await LockFieldAsync(tx, workspaceId, fieldId, cancellationToken).ConfigureAwait(false);
        if (field is null || field.IsDeleted)
        {
            return CatalogResult.NotFound<Choice>();
        }

        if (Stale(field.Version, write))
        {
            return CatalogResult.VersionConflict<Choice>();
        }

        if (await ScalarAsync<bool>(tx,
                "SELECT EXISTS (SELECT FROM opportunity.coding_layout_field WHERE workspace_id = @ws AND condition_field_id = @field AND @choice = ANY(condition_choice_ids))",
            [("ws", workspaceId), ("field", fieldId), ("choice", choiceId)], cancellationToken).ConfigureAwait(false))
        {
            return CatalogResult.Conflict<Choice>(new(key, "choice-in-layout", "A coding layout condition uses this choice."));
        }

        if (await IsSystemChoiceAsync(tx, workspaceId, fieldId, choiceId, cancellationToken).ConfigureAwait(false))
        {
            return CatalogResult.Conflict<Choice>(new(key, SystemChoiceCode, SystemChoiceMessage));
        }

        try
        {
            Choice? deleted;
            await using (var command = tx.Command(
                $"DELETE FROM opportunity.choice WHERE workspace_id = @ws AND field_id = @field AND choice_id = @choice RETURNING {ChoiceColumns}"))
            {
                command.Parameters.AddWithValue("ws", workspaceId);
                command.Parameters.AddWithValue("field", fieldId);
                command.Parameters.AddWithValue("choice", choiceId);
                deleted = await ReadSingleChoiceAsync(command, cancellationToken).ConfigureAwait(false);
            }

            if (deleted is null)
            {
                return CatalogResult.NotFound<Choice>();
            }

            await FinishFieldChangeAsync(tx, workspaceId, fieldId, write, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CatalogResult.Ok<Choice>(deleted);
        }
        catch (PostgresException ex) when (ex.ConstraintName == ChoiceInUseConstraint || ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return CatalogResult.Conflict<Choice>(new(key, "choice-in-use", "The choice has been used; deactivate it instead (ADR-003 R8)."));
        }
    }

    public async Task<bool> FieldHasValuesAsync(Guid workspaceId, int fieldId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var result = await ScalarAsync<bool>(tx,
            """
            SELECT coalesce((SELECT opportunity.field_has_values(workspace_id, field_id, storage)
                             FROM opportunity.field_definition WHERE workspace_id = @ws AND field_id = @id), false)
            """,
            [("ws", workspaceId), ("id", fieldId)], cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<CatalogResult<CodingLayout>> SaveLayoutAsync(CodingLayout layout, CancellationToken cancellationToken = default) =>
        SaveLayoutAsync(layout, CatalogWrite.None, cancellationToken);

    public async Task<CatalogResult<CodingLayout>> SaveLayoutAsync(CodingLayout layout, CatalogWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(write);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, layout.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var catalog = await LoadCatalogAsync(tx, layout.WorkspaceId, false, cancellationToken).ConfigureAwait(false);
        var errors = CodingLayoutValidator.ValidateStructure(layout, catalog);
        if (errors.Count > 0)
        {
            return CatalogResult.Invalid<CodingLayout>(errors);
        }

        if (layout.LayoutId == Guid.Empty)
        {
            layout.LayoutId = Guid.CreateVersion7();
        }

        bool currentlyDefault;
        long? currentVersion;
        await using (var current = tx.Command(
            "SELECT is_default, version FROM opportunity.coding_layout WHERE workspace_id = @ws AND layout_id = @id FOR UPDATE"))
        {
            current.Parameters.AddWithValue("ws", layout.WorkspaceId);
            current.Parameters.AddWithValue("id", layout.LayoutId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var found = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            currentlyDefault = found && reader.GetBoolean(0);
            currentVersion = found ? reader.GetInt64(1) : null;
        }

        if (write.ExpectedVersion is not null && currentVersion is null)
        {
            return CatalogResult.NotFound<CodingLayout>();
        }

        if (currentVersion is { } version && Stale(version, write))
        {
            return CatalogResult.VersionConflict<CodingLayout>();
        }

        if (currentlyDefault && !layout.IsDefault)
        {
            return CatalogResult.Invalid<CodingLayout>(
                [new("isDefault", "default-required", "Make another layout the default instead; a workspace always has one.")]);
        }

        if (await ScalarAsync<bool>(tx,
                "SELECT EXISTS (SELECT FROM opportunity.coding_layout WHERE workspace_id = @ws AND name_norm = lower(btrim(@name)) AND layout_id <> @id)",
            [("ws", layout.WorkspaceId), ("name", layout.Name), ("id", layout.LayoutId)], cancellationToken).ConfigureAwait(false))
        {
            return CatalogResult.Invalid<CodingLayout>([new("name", "duplicate-name", $"A layout named '{layout.Name.Trim()}' already exists.")]);
        }

        await WriteLayoutAsync(tx, layout, cancellationToken).ConfigureAwait(false);

        var saved = (await LoadLayoutsAsync(tx, layout.WorkspaceId, layout.LayoutId, null, cancellationToken).ConfigureAwait(false)).Single();
        await AuditAsync(tx, write, saved.LayoutId.ToString(), saved.Version, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CatalogResult.Ok<CodingLayout>(saved);
    }

    /// <summary>Writes a validated layout (insert or replace) in the caller's transaction; a default layout demotes the old one.</summary>
    internal static async Task WriteLayoutAsync(WorkspaceTransaction tx, CodingLayout layout, CancellationToken cancellationToken)
    {
        await using (var batch = new NpgsqlBatch(tx.Connection, tx.Transaction))
        {
            if (layout.IsDefault)
            {
                batch.BatchCommands.Add(BatchCommand(
                    "UPDATE opportunity.coding_layout SET is_default = false, updated_at = now(), version = version + 1 WHERE workspace_id = $1 AND is_default AND layout_id <> $2",
                    layout.WorkspaceId, layout.LayoutId));
            }

            batch.BatchCommands.Add(BatchCommand(
                """
                INSERT INTO opportunity.coding_layout (workspace_id, layout_id, name, is_default) VALUES ($1, $2, $3, $4)
                ON CONFLICT (workspace_id, layout_id) DO UPDATE
                SET name = EXCLUDED.name, is_default = EXCLUDED.is_default, updated_at = now(), version = coding_layout.version + 1
                """,
                layout.WorkspaceId, layout.LayoutId, layout.Name.Trim(), layout.IsDefault));
            batch.BatchCommands.Add(BatchCommand(
                "DELETE FROM opportunity.coding_layout_section WHERE workspace_id = $1 AND layout_id = $2", layout.WorkspaceId, layout.LayoutId));
            batch.BatchCommands.Add(BatchCommand(
                "DELETE FROM opportunity.coding_layout_role WHERE workspace_id = $1 AND layout_id = $2", layout.WorkspaceId, layout.LayoutId));
            for (var s = 0; s < layout.Sections.Count; s++)
            {
                var section = layout.Sections[s];
                batch.BatchCommands.Add(BatchCommand(
                    "INSERT INTO opportunity.coding_layout_section (workspace_id, layout_id, section_id, title, sort_order) VALUES ($1, $2, $3, $4, $5)",
                    layout.WorkspaceId, layout.LayoutId, section.SectionId, section.Title.Trim(), s));
                for (var f = 0; f < section.Fields.Count; f++)
                {
                    var field = section.Fields[f];
                    batch.BatchCommands.Add(BatchCommand(
                        """
                        INSERT INTO opportunity.coding_layout_field
                            (workspace_id, layout_id, field_id, section_id, sort_order, is_required, is_read_only,
                             condition_field_id, condition_choice_ids, condition_boolean, apply_to_family_default)
                        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                        """,
                        layout.WorkspaceId, layout.LayoutId, field.FieldId, section.SectionId, f, field.IsRequired, field.IsReadOnly,
                        (object?)field.VisibleWhen?.FieldId, field.VisibleWhen?.ChoiceIds is { } ids ? ids.ToArray() : null,
                        (object?)field.VisibleWhen?.BooleanValue, field.ApplyToFamilyByDefault));
                }
            }

            foreach (var role in layout.Roles)
            {
                batch.BatchCommands.Add(BatchCommand(
                    "INSERT INTO opportunity.coding_layout_role (workspace_id, layout_id, role) VALUES ($1, $2, $3)",
                    layout.WorkspaceId, layout.LayoutId, role.Trim()));
            }

            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<CodingLayout?> GetLayoutAsync(Guid workspaceId, Guid layoutId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var layouts = await LoadLayoutsAsync(tx, workspaceId, layoutId, null, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return layouts.SingleOrDefault();
    }

    public async Task<IReadOnlyList<CodingLayout>> GetLayoutsAsync(Guid workspaceId, string? role = null, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var layouts = await LoadLayoutsAsync(tx, workspaceId, null, role, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return layouts;
    }

    public Task<CatalogResult<CodingLayout>> DeleteLayoutAsync(Guid workspaceId, Guid layoutId, CancellationToken cancellationToken = default) =>
        DeleteLayoutAsync(workspaceId, layoutId, CatalogWrite.None, cancellationToken);

    public async Task<CatalogResult<CodingLayout>> DeleteLayoutAsync(
        Guid workspaceId, Guid layoutId, CatalogWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var lockRow = tx.Command("SELECT FROM opportunity.coding_layout WHERE workspace_id = @ws AND layout_id = @id FOR UPDATE"))
        {
            lockRow.Parameters.AddWithValue("ws", workspaceId);
            lockRow.Parameters.AddWithValue("id", layoutId);
            await lockRow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var existing = (await LoadLayoutsAsync(tx, workspaceId, layoutId, null, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (existing is null)
        {
            return CatalogResult.NotFound<CodingLayout>();
        }

        if (Stale(existing.Version, write))
        {
            return CatalogResult.VersionConflict<CodingLayout>();
        }

        if (existing.IsDefault)
        {
            return CatalogResult.Conflict<CodingLayout>(new("isDefault", "default-layout", "The default layout cannot be deleted."));
        }

        await using (var command = tx.Command("DELETE FROM opportunity.coding_layout WHERE workspace_id = @ws AND layout_id = @id"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", layoutId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditAsync(tx, write, layoutId.ToString(), existing.Version, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CatalogResult.Ok<CodingLayout>(existing);
    }

    private static async Task<List<CodingLayout>> LoadLayoutsAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid? layoutId, string? role, CancellationToken cancellationToken)
    {
        var layouts = new Dictionary<Guid, CodingLayout>();
        await using (var command = tx.Command(
            """
            SELECT l.layout_id, l.name, l.is_default, l.updated_at, l.version
            FROM opportunity.coding_layout l
            WHERE l.workspace_id = @ws
              AND (@id::uuid IS NULL OR l.layout_id = @id)
              AND (@role::text IS NULL OR l.is_default
                   OR EXISTS (SELECT FROM opportunity.coding_layout_role r
                              WHERE r.workspace_id = l.workspace_id AND r.layout_id = l.layout_id AND r.role = @role))
            ORDER BY l.is_default DESC, l.name_norm
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.Add(Nullable("id", NpgsqlDbType.Uuid, layoutId));
            command.Parameters.Add(Nullable("role", NpgsqlDbType.Text, role));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var layout = new CodingLayout
                {
                    WorkspaceId = workspaceId,
                    LayoutId = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    IsDefault = reader.GetBoolean(2),
                    UpdatedAt = reader.GetFieldValue<DateTimeOffset>(3),
                    Version = reader.GetInt64(4),
                };
                layouts.Add(layout.LayoutId, layout);
            }
        }

        var ids = layouts.Keys.ToArray();
        var sections = new Dictionary<(Guid, Guid), CodingLayoutSection>();
        await using (var command = tx.Command(
            """
            SELECT layout_id, section_id, title FROM opportunity.coding_layout_section
            WHERE workspace_id = @ws AND layout_id = ANY(@ids) ORDER BY layout_id, sort_order
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var section = new CodingLayoutSection { SectionId = reader.GetGuid(1), Title = reader.GetString(2) };
                layouts[reader.GetGuid(0)].Sections.Add(section);
                sections.Add((reader.GetGuid(0), section.SectionId), section);
            }
        }

        await using (var command = tx.Command(
            """
            SELECT layout_id, section_id, field_id, is_required, is_read_only, condition_field_id, condition_choice_ids, condition_boolean,
                   apply_to_family_default
            FROM opportunity.coding_layout_field
            WHERE workspace_id = @ws AND layout_id = ANY(@ids) ORDER BY layout_id, section_id, sort_order
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sections[(reader.GetGuid(0), reader.GetGuid(1))].Fields.Add(new CodingLayoutField
                {
                    FieldId = reader.GetInt32(2),
                    IsRequired = reader.GetBoolean(3),
                    IsReadOnly = reader.GetBoolean(4),
                    VisibleWhen = reader.IsDBNull(5)
                        ? null
                        : new VisibilityCondition(
                            reader.GetInt32(5),
                            reader.IsDBNull(6) ? null : reader.GetFieldValue<int[]>(6),
                            reader.IsDBNull(7) ? null : reader.GetBoolean(7)),
                    ApplyToFamilyByDefault = reader.GetBoolean(8),
                });
            }
        }

        await using (var command = tx.Command(
            "SELECT layout_id, role FROM opportunity.coding_layout_role WHERE workspace_id = @ws AND layout_id = ANY(@ids) ORDER BY role"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                layouts[reader.GetGuid(0)].Roles.Add(reader.GetString(1));
            }
        }

        return [.. layouts.Values];
    }

    internal static async Task<FieldDefinition?> InsertFieldAsync(
        WorkspaceTransaction tx, FieldDefinition field, bool onConflictDoNothing, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            INSERT INTO opportunity.field_definition (
                workspace_id, field_id, name, description, field_type, storage, is_system, is_multi_value, date_precision,
                decimal_precision, decimal_scale, text_analysis, is_security_affecting, security_class, is_searchable,
                search_slot, capabilities, column_name)
            VALUES (@ws, @id, @name, @description, @type, @storage, @system, @multi, @date_precision,
                @decimal_precision, @decimal_scale, @text_analysis, @security, @security_class, @searchable,
                @slot, @capabilities, @column)
            {(onConflictDoNothing ? "ON CONFLICT (workspace_id, field_id) DO NOTHING" : string.Empty)}
            RETURNING {FieldColumns}
            """);
        command.Parameters.AddWithValue("ws", field.WorkspaceId);
        command.Parameters.AddWithValue("id", field.FieldId);
        command.Parameters.AddWithValue("name", field.Name);
        command.Parameters.Add(Nullable("description", NpgsqlDbType.Text, field.Description));
        command.Parameters.AddWithValue("type", (short)field.Type);
        command.Parameters.AddWithValue("storage", (short)field.Storage);
        command.Parameters.AddWithValue("system", field.IsSystem);
        command.Parameters.AddWithValue("multi", field.IsMultiValue);
        command.Parameters.Add(Nullable("date_precision", NpgsqlDbType.Smallint, (short?)field.DatePrecision));
        command.Parameters.Add(Nullable("decimal_precision", NpgsqlDbType.Smallint, field.DecimalPrecision));
        command.Parameters.Add(Nullable("decimal_scale", NpgsqlDbType.Smallint, field.DecimalScale));
        command.Parameters.Add(Nullable("text_analysis", NpgsqlDbType.Smallint, (short?)field.TextAnalysis));
        command.Parameters.AddWithValue("security", field.IsSecurityAffecting);
        command.Parameters.Add(Nullable("security_class", NpgsqlDbType.Smallint, (short?)field.SecurityClass));
        command.Parameters.AddWithValue("searchable", field.IsSearchable);
        command.Parameters.Add(Nullable("slot", NpgsqlDbType.Text, field.SearchSlot));
        command.Parameters.AddWithValue("capabilities", (int)field.Capabilities);
        command.Parameters.Add(Nullable("column", NpgsqlDbType.Text, field.ColumnName));
        return await ReadSingleFieldAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lowest free slot of the field's kind within its storage namespace, else overflow (ADR-007 R4/R6).</summary>
    internal static async Task<string> AllocateSlotAsync(
        WorkspaceTransaction tx, FieldDefinition field, int? exceptFieldId, CancellationToken cancellationToken)
    {
        var kind = FieldRules.SlotKind(field.Type, field.TextAnalysis);
        var used = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = tx.Command(
            """
            SELECT search_slot FROM opportunity.field_definition
            WHERE workspace_id = @ws AND storage = @storage AND starts_with(search_slot, @prefix) AND field_id <> @except
            """))
        {
            command.Parameters.AddWithValue("ws", field.WorkspaceId);
            command.Parameters.AddWithValue("storage", (short)field.Storage);
            command.Parameters.AddWithValue("prefix", kind + ".s");
            command.Parameters.AddWithValue("except", exceptFieldId ?? 0);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                used.Add(reader.GetString(0));
            }
        }

        for (var n = 1; n <= FieldRules.SlotBudgetsFor(field.Storage)[kind]; n++)
        {
            var slot = FieldRules.Slot(kind, n);
            if (!used.Contains(slot))
            {
                return slot;
            }
        }

        return FieldRules.OverflowSlot;
    }

    private static async Task<FieldDefinition?> LockFieldAsync(WorkspaceTransaction tx, Guid workspaceId, int fieldId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"SELECT {FieldColumns} FROM opportunity.field_definition WHERE workspace_id = @ws AND field_id = @id FOR UPDATE");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", fieldId);
        return await ReadSingleFieldAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> NameTakenAsync(WorkspaceTransaction tx, Guid workspaceId, string name, int? exceptFieldId, CancellationToken cancellationToken) =>
        await ScalarAsync<bool>(tx,
            """
            SELECT EXISTS (SELECT FROM opportunity.field_definition
                           WHERE workspace_id = @ws AND name_norm = lower(btrim(@name)) AND NOT is_deleted AND field_id <> @except)
            """,
            [("ws", workspaceId), ("name", name), ("except", exceptFieldId ?? 0)], cancellationToken).ConfigureAwait(false);

    private static async Task<bool> IsSystemChoiceAsync(
        WorkspaceTransaction tx, Guid workspaceId, int fieldId, int choiceId, CancellationToken cancellationToken) =>
        await ScalarAsync<bool>(tx,
            """
            SELECT EXISTS (SELECT FROM opportunity.choice
                           WHERE workspace_id = @ws AND field_id = @field AND choice_id = @choice AND system_key IS NOT NULL)
            """,
            [("ws", workspaceId), ("field", fieldId), ("choice", choiceId)], cancellationToken).ConfigureAwait(false);

    private static async Task<(long Count, bool NameTaken)> ChoiceStatsAsync(
        WorkspaceTransaction tx, Guid workspaceId, int fieldId, string name, int? exceptChoiceId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            SELECT count(*), coalesce(bool_or(name_norm = lower(btrim(@name)) AND choice_id <> @except), false)
            FROM opportunity.choice WHERE workspace_id = @ws AND field_id = @field
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("field", fieldId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("except", exceptChoiceId ?? 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetBoolean(1));
    }

    private static bool Stale(long currentVersion, CatalogWrite write) =>
        write.ExpectedVersion is { } expected && expected != currentVersion;

    private static string FieldResource(int fieldId) => fieldId.ToString(CultureInfo.InvariantCulture);

    /// <summary>A choice changed: the field's version moves on and the change is audited against the field.</summary>
    private static async Task FinishFieldChangeAsync(
        WorkspaceTransaction tx, Guid workspaceId, int fieldId, CatalogWrite write, CancellationToken cancellationToken)
    {
        var version = await ScalarAsync<long>(tx,
            """
            UPDATE opportunity.field_definition SET version = version + 1, updated_at = now()
            WHERE workspace_id = @ws AND field_id = @id RETURNING version
            """,
            [("ws", workspaceId), ("id", fieldId)], cancellationToken).ConfigureAwait(false);
        await AuditAsync(tx, write, FieldResource(fieldId), version, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AuditAsync(WorkspaceTransaction tx, CatalogWrite write, string resourceId, long version, CancellationToken cancellationToken)
    {
        if (write.Audit is not { } audit)
        {
            return;
        }

        var details = new Dictionary<string, string?>(audit.Details) { ["version"] = version.ToString(CultureInfo.InvariantCulture) };
        await AuditSql.InsertAsync(tx, audit with { ResourceId = resourceId, Details = details }, cancellationToken).ConfigureAwait(false);
    }

    private static FieldError? InvalidChoiceName(string name) =>
        name.Length == 0 || name.Length > FieldLimits.MaxChoiceNameLength || name.Any(char.IsControl)
            ? new FieldError("name", "invalid-name", $"Choice names are 1–{FieldLimits.MaxChoiceNameLength} characters without control characters.")
            : null;

    private static FieldError DuplicateName(string name) =>
        new("name", "duplicate-name", $"A field named '{name}' already exists in this workspace.");

    private static async Task<FieldDefinition?> ReadSingleFieldAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadField(reader) : null;
    }

    private static async Task<Choice?> ReadSingleChoiceAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadChoice(reader) : null;
    }

    private static FieldDefinition ReadField(NpgsqlDataReader reader) => new()
    {
        WorkspaceId = reader.GetGuid(0),
        FieldId = reader.GetInt32(1),
        Name = reader.GetString(2),
        Description = reader.IsDBNull(3) ? null : reader.GetString(3),
        Type = (FieldType)reader.GetInt16(4),
        Storage = (FieldStorage)reader.GetInt16(5),
        IsSystem = reader.GetBoolean(6),
        IsMultiValue = reader.GetBoolean(7),
        DatePrecision = reader.IsDBNull(8) ? null : (DatePrecision)reader.GetInt16(8),
        DecimalPrecision = reader.IsDBNull(9) ? null : reader.GetInt16(9),
        DecimalScale = reader.IsDBNull(10) ? null : reader.GetInt16(10),
        TextAnalysis = reader.IsDBNull(11) ? null : (TextAnalysis)reader.GetInt16(11),
        IsSecurityAffecting = reader.GetBoolean(12),
        SecurityClass = reader.IsDBNull(13) ? null : (SecurityClass)reader.GetInt16(13),
        IsSearchable = reader.GetBoolean(14),
        SearchSlot = reader.IsDBNull(15) ? null : reader.GetString(15),
        Capabilities = (FieldCapabilities)reader.GetInt32(16),
        ColumnName = reader.IsDBNull(17) ? null : reader.GetString(17),
        IsHidden = reader.GetBoolean(18),
        IsDeleted = reader.GetBoolean(19),
        DeletedAt = reader.IsDBNull(20) ? null : reader.GetFieldValue<DateTimeOffset>(20),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(21),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(22),
        Version = reader.GetInt64(23),
    };

    private static Choice ReadChoice(NpgsqlDataReader reader) => new()
    {
        WorkspaceId = reader.GetGuid(0),
        FieldId = reader.GetInt32(1),
        ChoiceId = reader.GetInt32(2),
        Name = reader.GetString(3),
        SortOrder = reader.GetInt32(4),
        IsActive = reader.GetBoolean(5),
        FirstUsedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        SystemKey = reader.IsDBNull(7) ? null : reader.GetString(7),
    };

    private static FieldDefinition Copy(FieldDefinition f) => new()
    {
        WorkspaceId = f.WorkspaceId,
        FieldId = f.FieldId,
        Name = f.Name,
        Description = f.Description,
        Type = f.Type,
        Storage = f.Storage,
        IsSystem = f.IsSystem,
        IsMultiValue = f.IsMultiValue,
        DatePrecision = f.DatePrecision,
        DecimalPrecision = f.DecimalPrecision,
        DecimalScale = f.DecimalScale,
        TextAnalysis = f.TextAnalysis,
        IsSecurityAffecting = f.IsSecurityAffecting,
        SecurityClass = f.SecurityClass,
        IsSearchable = f.IsSearchable,
        SearchSlot = f.SearchSlot,
        Capabilities = f.Capabilities,
        ColumnName = f.ColumnName,
        IsHidden = f.IsHidden,
        IsDeleted = f.IsDeleted,
        DeletedAt = f.DeletedAt,
        CreatedAt = f.CreatedAt,
        UpdatedAt = f.UpdatedAt,
        Version = f.Version,
    };

    internal static NpgsqlParameter Nullable<T>(string name, NpgsqlDbType type, T? value) =>
        new(name, type) { Value = (object?)value ?? DBNull.Value };

    internal static async Task<T> ScalarAsync<T>(
        WorkspaceTransaction tx, string sql, (string Name, object Value)[] parameters, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static NpgsqlBatchCommand BatchCommand(string sql, params object?[] values)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var value in values)
        {
            command.Parameters.Add(value switch
            {
                null => new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Unknown },
                _ => new NpgsqlParameter { Value = value },
            });
        }

        return command;
    }
}
