using Opportunity.Core.Fields;
using Opportunity.Core.Security;

namespace Opportunity.Data.Fields;

/// <summary>
/// The default workspace template (familiarity guide §3.4, E13-T01): coding fields an experienced reviewer recognises
/// and the First Pass Review (default) and Privilege Review layouts, seeded into a new workspace after its system and
/// privilege fields. The template fields are ordinary custom fields: admins rename, extend or retire them. Confidentiality
/// Designation is security-affecting and bound to the built-in Confidential and AttorneysEyesOnly classes through the
/// #51 binding (<c>restriction_class_rule</c>).
/// </summary>
internal static class WorkspaceTemplate
{
    public const string FirstPassReview = "First Pass Review";
    public const string PrivilegeReview = "Privilege Review";

    private sealed record TemplateField(
        string Name, FieldType Type, string Description, SecurityClass? SecurityClass = null, TextAnalysis? Analysis = null,
        params (string Name, string? ClassKey)[] Choices);

    private static readonly TemplateField[] Fields =
    [
        new("Responsiveness", FieldType.SingleChoice, "Whether the document is responsive to the requests.", null, null,
            ("Responsive", null), ("Not Responsive", null), ("Needs Further Review", null)),
        new("Confidentiality Designation", FieldType.SingleChoice, "Confidentiality designation under the protective order.",
            SecurityClass.ConfidentialityDesignation, null,
            ("None", null), ("CONFIDENTIAL", RestrictionClasses.Confidential), ("HIGHLY CONFIDENTIAL – AEO", RestrictionClasses.AttorneysEyesOnly)),
        new("Issues", FieldType.MultiChoice, "Issues the document relates to."),
        new("Key Document", FieldType.Boolean, "A key or hot document."),
        new("Reviewer Comments", FieldType.Text, "Notes for the review team.", null, TextAnalysis.Prose),
    ];

    /// <summary>Seeds the template unless the workspace already has custom fields or layouts. Runs in the caller's transaction.</summary>
    public static async Task SeedAsync(WorkspaceTransaction tx, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (await FieldCatalogRepository.ScalarAsync<bool>(tx,
                """
                SELECT EXISTS (SELECT FROM opportunity.field_definition WHERE workspace_id = @ws AND NOT is_system)
                    OR EXISTS (SELECT FROM opportunity.coding_layout WHERE workspace_id = @ws)
                """,
                [("ws", workspaceId)], cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var template in Fields)
        {
            var fieldId = await FieldCatalogRepository.ScalarAsync<int>(tx,
                "UPDATE opportunity.field_catalog_counter SET next_field_id = next_field_id + 1 WHERE workspace_id = @ws RETURNING next_field_id - 1",
                [("ws", workspaceId)], cancellationToken).ConfigureAwait(false);
            var definition = new FieldDefinition
            {
                WorkspaceId = workspaceId,
                FieldId = fieldId,
                Name = template.Name,
                Description = template.Description,
                Type = template.Type,
                Storage = FieldStorage.Coding,
                IsMultiValue = template.Type == FieldType.MultiChoice,
                TextAnalysis = template.Analysis,
                IsSecurityAffecting = template.SecurityClass.HasValue,
                SecurityClass = template.SecurityClass,
                IsSearchable = true,
            };
            definition.SearchSlot = await FieldCatalogRepository.AllocateSlotAsync(tx, definition, null, cancellationToken).ConfigureAwait(false);
            definition.Capabilities = FieldRules.CapabilitiesForSlot(definition.SearchSlot);
            await FieldCatalogRepository.InsertFieldAsync(tx, definition, onConflictDoNothing: false, cancellationToken).ConfigureAwait(false);
            ids[template.Name] = fieldId;

            for (var i = 0; i < template.Choices.Length; i++)
            {
                var (name, classKey) = template.Choices[i];
                await using var choice = tx.Command(
                    """
                    WITH id AS (
                        UPDATE opportunity.field_catalog_counter SET next_choice_id = next_choice_id + 1
                        WHERE workspace_id = @ws RETURNING next_choice_id - 1 AS choice_id),
                    inserted AS (
                        INSERT INTO opportunity.choice (workspace_id, field_id, choice_id, name, sort_order)
                        SELECT @ws, @field, id.choice_id, @name, @sort FROM id
                        RETURNING choice_id)
                    INSERT INTO opportunity.restriction_class_rule (workspace_id, class_key, field_id, choice_id)
                    SELECT @ws, c.class_key, @field, inserted.choice_id
                    FROM inserted JOIN opportunity.restriction_class c ON c.workspace_id = @ws AND c.class_key = @class
                    """);
                choice.Parameters.AddWithValue("ws", workspaceId);
                choice.Parameters.AddWithValue("field", fieldId);
                choice.Parameters.AddWithValue("name", name);
                choice.Parameters.AddWithValue("sort", i);
                choice.Parameters.Add(FieldCatalogRepository.Nullable("class", NpgsqlTypes.NpgsqlDbType.Text, classKey));
                await choice.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var catalog = await FieldCatalogRepository.LoadCatalogAsync(tx, workspaceId, false, cancellationToken).ConfigureAwait(false);
        foreach (var layout in Layouts(workspaceId, catalog, ids))
        {
            var errors = CodingLayoutValidator.ValidateStructure(layout, catalog);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException("The default workspace template is invalid: " + string.Join("; ", errors.Select(e => e.Message)));
            }

            await FieldCatalogRepository.WriteLayoutAsync(tx, layout, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<CodingLayout> Layouts(Guid workspaceId, FieldCatalog catalog, Dictionary<string, int> ids)
    {
        // Privilege Basis is shown, and required, while Privilege Status is Withhold or Redact (AC 1 in the coding pane).
        var basisCondition = PrivilegeFields.BasisRequiredStatuses(catalog) is { Count: > 0 } statuses
            ? new VisibilityCondition(PrivilegeFields.Status, [.. statuses.Order()])
            : null;
        bool Present(int fieldId) => catalog.Find(fieldId) is { IsDeleted: false, Storage: FieldStorage.Coding };

        IEnumerable<CodingLayoutField> Privilege(bool full)
        {
            if (Present(PrivilegeFields.Status))
            {
                yield return new CodingLayoutField { FieldId = PrivilegeFields.Status };
                if (Present(PrivilegeFields.Basis) && basisCondition is not null)
                {
                    yield return new CodingLayoutField { FieldId = PrivilegeFields.Basis, IsRequired = true, VisibleWhen = basisCondition };
                }
            }

            if (!full)
            {
                yield break;
            }

            foreach (var id in new[] { PrivilegeFields.Description, PrivilegeFields.AttorneysInvolved, PrivilegeFields.LogCategory }.Where(Present))
            {
                yield return new CodingLayoutField { FieldId = id };
            }
        }

        CodingLayoutField Field(string name) => new() { FieldId = ids[name] };

        yield return new CodingLayout
        {
            WorkspaceId = workspaceId,
            LayoutId = Guid.CreateVersion7(),
            Name = FirstPassReview,
            IsDefault = true,
            Sections =
            {
                new CodingLayoutSection
                {
                    SectionId = Guid.CreateVersion7(),
                    Title = "Responsiveness",
                    Fields = { Field("Responsiveness"), Field("Issues"), Field("Key Document") },
                },
                new CodingLayoutSection
                {
                    SectionId = Guid.CreateVersion7(),
                    Title = "Privilege and confidentiality",
                    Fields = [.. Privilege(full: false), Field("Confidentiality Designation")],
                },
                new CodingLayoutSection
                {
                    SectionId = Guid.CreateVersion7(),
                    Title = "Comments",
                    Fields = { Field("Reviewer Comments") },
                },
            },
        };

        yield return new CodingLayout
        {
            WorkspaceId = workspaceId,
            LayoutId = Guid.CreateVersion7(),
            Name = PrivilegeReview,
            Roles = { WorkspaceRole.PrivilegeReviewer.Key() },
            Sections =
            {
                new CodingLayoutSection
                {
                    SectionId = Guid.CreateVersion7(),
                    Title = "Privilege",
                    Fields = [.. Privilege(full: true)],
                },
                new CodingLayoutSection
                {
                    SectionId = Guid.CreateVersion7(),
                    Title = "Confidentiality",
                    Fields = { Field("Confidentiality Designation") },
                },
            },
        };
    }
}
