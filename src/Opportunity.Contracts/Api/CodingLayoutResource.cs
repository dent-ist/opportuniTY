namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}/coding-layouts</c> (E04-T03, E16-T05): a coding layout the caller may use in
/// the coding pane. Fields are addressed by id; their names, types and choices come from <c>GET …/fields</c>.
/// </summary>
/// <param name="LayoutId">Stable id; send it as <c>layoutId</c> when reading and saving coding.</param>
/// <param name="Name">Display name, unique in the workspace.</param>
/// <param name="IsDefault">The workspace's default layout (exactly one; always listed).</param>
/// <param name="Sections">Sections in display order.</param>
public sealed record CodingLayoutResource(
    Guid LayoutId,
    string Name,
    bool IsDefault,
    IReadOnlyList<CodingLayoutSectionResource> Sections);

/// <param name="Title">Section heading.</param>
/// <param name="Fields">Fields in display order; fields the caller may not see are omitted.</param>
public sealed record CodingLayoutSectionResource(Guid SectionId, string Title, IReadOnlyList<CodingLayoutFieldResource> Fields);

/// <param name="IsRequired">Must have a value while visible.</param>
/// <param name="IsReadOnly">Shown but never editable in this layout.</param>
/// <param name="VisibleWhen">Shown only while the condition holds; null means always shown.</param>
public sealed record CodingLayoutFieldResource(
    int FieldId,
    bool IsRequired,
    bool IsReadOnly,
    CodingLayoutConditionResource? VisibleWhen);

/// <summary>
/// "Show when field <see cref="FieldId"/> has any of <see cref="ChoiceIds"/>" (choice field) or "… equals
/// <see cref="BooleanValue"/>" (Yes/No field). The controlling field is in the same layout.
/// </summary>
public sealed record CodingLayoutConditionResource(int FieldId, IReadOnlyList<int>? ChoiceIds, bool? BooleanValue);
