namespace Opportunity.Core.Fields;

/// <summary>A consistent read of a workspace's field definitions and choices, used by validators and coercion.</summary>
public sealed class FieldCatalog
{
    private static readonly IReadOnlyList<Choice> NoChoices = [];

    private readonly Dictionary<int, FieldDefinition> _fields;
    private readonly Dictionary<int, IReadOnlyList<Choice>> _choices;

    public FieldCatalog(IEnumerable<FieldDefinition> fields, IEnumerable<Choice> choices)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(choices);
        _fields = fields.ToDictionary(f => f.FieldId);
        _choices = choices
            .GroupBy(c => c.FieldId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Choice>)[.. g.OrderBy(c => c.SortOrder).ThenBy(c => c.ChoiceId)]);
    }

    public IReadOnlyCollection<FieldDefinition> Fields => _fields.Values;

    public FieldDefinition? Find(int fieldId) => _fields.GetValueOrDefault(fieldId);

    /// <summary>Choices of a field in display order (inactive included).</summary>
    public IReadOnlyList<Choice> ChoicesOf(int fieldId) => _choices.GetValueOrDefault(fieldId) ?? NoChoices;

    /// <summary>
    /// This catalogue without <paramref name="fieldIds"/> and their choices: what a principal with field-level
    /// restrictions binds against (E05-T06), so a hidden field answers exactly like one that does not exist.
    /// </summary>
    public FieldCatalog Without(IReadOnlySet<int> fieldIds)
    {
        ArgumentNullException.ThrowIfNull(fieldIds);
        return fieldIds.Count == 0 || !fieldIds.Any(_fields.ContainsKey)
            ? this
            : new FieldCatalog(
                _fields.Values.Where(f => !fieldIds.Contains(f.FieldId)),
                _choices.Where(c => !fieldIds.Contains(c.Key)).SelectMany(c => c.Value));
    }
}
