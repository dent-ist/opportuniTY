using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;

namespace Opportunity.Import.Mapping;

/// <summary>
/// Reads the upstream duplicate and thread identifiers of a mapped row (E09-T02) for
/// <see cref="UpstreamRelationships.Apply"/>. Cells in error are ignored: such a row goes to the error file.
/// </summary>
public static class UpstreamRelationshipExtractor
{
    public static UpstreamRelationshipValues Extract(CompiledMapping mapping, MappedRow row)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(row);
        string? Structural(StructuralTarget target) => Text(row.Cells.FirstOrDefault(c => c.Target.Structural == target && c.Error is null));
        string? Field(int fieldId) => Text(row.Cells.FirstOrDefault(c => c.Target.FieldId == fieldId && c.Error is null));

        // A row is an attachment when it names a parent (a self-reference is already cleared by the mapping) or its
        // attachment range starts at another document. Group-identifier families are only known after the family
        // post-pass (E09-T01), which owns the final ADR-009 R20 check.
        var begAttach = Field(SystemFields.BegAttach);
        var isAttachment = Structural(StructuralTarget.ParentId) is not null
            || (begAttach is not null && row.ControlNumberNorm is not null
                && ControlNumber.TryNormalize(
                    begAttach, mapping.ControlNumberCaseSensitive, mapping.EffectiveProfile.ControlNumberPrefix, out var begNorm, out _)
                && begNorm != row.ControlNumberNorm);

        return new UpstreamRelationshipValues
        {
            DuplicateGroup = Structural(StructuralTarget.DuplicateGroupId),
            DedupeHash = Structural(StructuralTarget.DedupeHash),
            EmailHash = Structural(StructuralTarget.EmailHash),
            EmailThreadGroup = Structural(StructuralTarget.EmailThreadId),
            ConversationIndex = Field(SystemFields.ConversationIndex),
            IsAttachment = isAttachment,
        };
    }

    /// <summary>The profile's relationship options in the form <see cref="UpstreamRelationships"/> takes.</summary>
    public static UpstreamRelationshipOptions Options(CompiledMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return new UpstreamRelationshipOptions(mapping.EffectiveProfile.Relationships?.DeriveEmailThreadFromConversationIndex ?? false);
    }

    private static string? Text(MappedCell? cell) =>
        cell?.Value is { } value && value.GetValueKind() == System.Text.Json.JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text
            ? text
            : null;
}
