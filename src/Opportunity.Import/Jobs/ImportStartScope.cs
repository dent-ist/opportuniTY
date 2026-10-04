using Opportunity.Core.Fields;
using Opportunity.Import.Mapping;

namespace Opportunity.Import.Jobs;

/// <summary>
/// What the start of an import authorized, re-applied by the worker. The mapping is recompiled against the field catalog
/// of the moment (a field renamed or deleted since the start resolves differently), so the worker never does more than
/// the batch records: field and choice creation only when the start granted it (<c>may_create_fields</c>), and
/// coding/privilege fields only from the batch's <c>coding_overlay_field_ids</c> (Q-31).
/// </summary>
public static class ImportStartScope
{
    public const string FieldCreationNotAuthorized = "field-creation-not-authorized";

    public const string CodingFieldNotEnabled = "coding-field-not-enabled";

    /// <summary>The new fields the mapping would create.</summary>
    public static IReadOnlyList<string> FieldsToCreate(CompiledMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return [.. mapping.Targets.Where(t => t.CreatesField is not null).Select(t => t.CreatesField!.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Coding or privilege-affecting fields the mapping loads that the import did not enable (Q-31).</summary>
    public static IReadOnlyList<int> CodingFieldsNotEnabled(CompiledMapping mapping, IReadOnlyCollection<int> enabled)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(enabled);
        return [.. mapping.Targets
            .Where(t => t.Usable && t.FieldId is not null && (t.Definition.Storage == FieldStorage.Coding || t.Definition.IsSecurityAffecting))
            .Select(t => t.FieldId!.Value)
            .Where(id => !enabled.Contains(id))
            .Distinct()
            .Order()];
    }
}
