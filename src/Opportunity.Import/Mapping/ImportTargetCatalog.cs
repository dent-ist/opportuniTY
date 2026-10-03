using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;

namespace Opportunity.Import.Mapping;

/// <summary>The targets the mapping grid offers, structural targets first (guide §5.1 step 3).</summary>
public static class ImportTargetCatalog
{
    private static readonly int[] LeadingSystemFields =
        [SystemFields.ControlNumber, SystemFields.BegBates, SystemFields.EndBates, SystemFields.BegAttach, SystemFields.EndAttach];

    private static readonly StructuralTarget[] LeadingStructural =
        [StructuralTarget.ParentId, StructuralTarget.GroupId, StructuralTarget.NativePath, StructuralTarget.TextPath, StructuralTarget.FolderPath];

    private static readonly int[] Hashes = [SystemFields.Md5, SystemFields.Sha1, SystemFields.Sha256];

    private static readonly StructuralTarget[] Upstream =
        [StructuralTarget.DuplicateGroupId, StructuralTarget.DedupeHash, StructuralTarget.EmailHash, StructuralTarget.EmailThreadId];

    public static IReadOnlyList<ImportTargetResource> List(FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var live = catalog.Fields.Where(f => !f.IsDeleted).ToDictionary(f => f.FieldId);
        var result = new List<ImportTargetResource>();
        void AddField(int id)
        {
            if (live.Remove(id, out var field) && !ImportTargets.ComputedSystemFields.Contains(id))
            {
                result.Add(Field(field, structural: true));
            }
        }

        foreach (var id in LeadingSystemFields)
        {
            AddField(id);
        }

        result.AddRange(LeadingStructural.Select(Structural));
        foreach (var id in Hashes)
        {
            AddField(id);
        }

        result.AddRange(Upstream.Select(Structural));
        foreach (var field in live.Values.Where(f => f.IsSystem && !ImportTargets.ComputedSystemFields.Contains(f.FieldId)).OrderBy(f => f.FieldId))
        {
            result.Add(Field(field, structural: false));
        }

        foreach (var field in live.Values.Where(f => !f.IsSystem).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(Field(field, structural: false));
        }

        return result;
    }

    private static ImportTargetResource Structural(StructuralTarget target)
    {
        var definition = ImportTargets.Definition(target);
        return new ImportTargetResource(
            new MappingTarget { Kind = MappingTargetKind.Structural, Structural = target },
            ImportTargets.Label(target),
            ImportTargets.TypeLabel(definition),
            false,
            IsStructural: true,
            IsCodingField: false,
            IsSecurityAffecting: false,
            AutoMapEligible: true,
            ImportTargets.StructuralAliases[target]);
    }

    private static ImportTargetResource Field(FieldDefinition field, bool structural)
    {
        var known = ImportTargets.WellKnownFields.FirstOrDefault(w => ImportTargets.NormalizeName(w.Name) == ImportTargets.NormalizeName(field.Name));
        var coding = field.Storage == FieldStorage.Coding;
        return new ImportTargetResource(
            new MappingTarget { Kind = MappingTargetKind.Field, FieldId = field.FieldId, FieldName = field.Name },
            field.Name,
            ImportTargets.TypeLabel(field),
            field.IsMultiValue,
            structural,
            coding,
            field.IsSecurityAffecting,
            AutoMapEligible: !coding && !field.IsSecurityAffecting && field.Type != FieldType.User,
            field.IsSystem ? ImportTargets.SystemFieldAliases.GetValueOrDefault(field.FieldId) ?? [] : known?.Aliases ?? []);
    }
}
