namespace Opportunity.Core.Productions;

/// <summary>Where an endorsement is stamped on a produced page (E12-T04).</summary>
public enum EndorsementPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

/// <summary>How a production member's confidentiality designation is decided (E12-T04).</summary>
public enum DesignationFamilyRule
{
    /// <summary>Every member of a family is produced with the highest designation in the family (the default).</summary>
    HighestInFamily,

    /// <summary>Each document keeps its own designation.</summary>
    Document,
}

/// <summary>Why a member carries its designation. Stored as smallint; values are fixed forever.</summary>
public enum DesignationSource : short
{
    /// <summary>Nothing is stamped: no value, or a level whose legend is empty (such as "None").</summary>
    None = 0,

    /// <summary>The document's own coded designation.</summary>
    Document = 1,

    /// <summary>Raised to the highest designation in its family (<see cref="DesignationFamilyRule.HighestInFamily"/>).</summary>
    Family = 2,

    /// <summary>Set for this production by a person, with an audited reason.</summary>
    Override = 3,
}

/// <summary>A designation level: a choice of the designation field and the legend stamped for it (empty: nothing stamped).</summary>
/// <param name="Rank">Position from lowest (0) to highest.</param>
public sealed record DesignationLevel(int ChoiceId, int Rank, string Legend);

/// <summary>One production member as the resolver reads it.</summary>
/// <param name="OwnChoiceId">The document's coded designation choice, null when not coded.</param>
/// <param name="Override">Set when the production overrides the member (its choice may be null: no designation).</param>
public readonly record struct DesignationMember(long Sequence, Guid FamilyKey, int? OwnChoiceId, DesignationOverride? Override = null);

public readonly record struct DesignationOverride(int? ChoiceId);

/// <summary>A member's resolved designation.</summary>
/// <param name="ChoiceId">The designation choice produced, null for none.</param>
/// <param name="Legend">The legend stamped and written to the load file (empty for none).</param>
/// <param name="Unlisted">The document carries a choice the specification's levels do not list (it cannot be produced).</param>
public readonly record struct ResolvedDesignation(long Sequence, int? ChoiceId, string Legend, DesignationSource Source, bool Unlisted);

/// <summary>
/// Decides each production member's confidentiality designation (E12-T04): a person's override for the production wins;
/// otherwise, under <see cref="DesignationFamilyRule.HighestInFamily"/>, the highest level (by rank) coded on any member
/// of the family in the production, else the document's own. The SQL in the production store implements the same rule
/// set-based; tests check the two agree. Pure and deterministic.
/// </summary>
public static class DesignationResolver
{
    public static IReadOnlyList<ResolvedDesignation> Resolve(
        IReadOnlyList<DesignationMember> members, IReadOnlyList<DesignationLevel> levels, DesignationFamilyRule rule)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(levels);
        var byChoice = levels.ToDictionary(l => l.ChoiceId);
        var familyBest = new Dictionary<Guid, DesignationLevel>();
        if (rule == DesignationFamilyRule.HighestInFamily)
        {
            foreach (var member in members)
            {
                if (member.OwnChoiceId is { } own && byChoice.TryGetValue(own, out var level)
                    && (!familyBest.TryGetValue(member.FamilyKey, out var best) || level.Rank > best.Rank))
                {
                    familyBest[member.FamilyKey] = level;
                }
            }
        }

        var resolved = new List<ResolvedDesignation>(members.Count);
        foreach (var member in members)
        {
            var unlisted = member.OwnChoiceId is { } c && !byChoice.ContainsKey(c);
            DesignationLevel? ownLevel = member.OwnChoiceId is { } o && byChoice.TryGetValue(o, out var l) ? l : null;
            if (member.Override is { } over)
            {
                var chosen = over.ChoiceId is { } oc && byChoice.TryGetValue(oc, out var ol) ? ol : null;
                resolved.Add(new ResolvedDesignation(member.Sequence, chosen?.ChoiceId, chosen?.Legend ?? string.Empty, DesignationSource.Override, unlisted));
                continue;
            }

            var effective = ownLevel;
            var source = ownLevel is null ? DesignationSource.None : DesignationSource.Document;
            if (familyBest.TryGetValue(member.FamilyKey, out var family) && (ownLevel is null || family.Rank > ownLevel.Rank))
            {
                effective = family;
                source = DesignationSource.Family;
            }

            if (effective is null || effective.Legend.Length == 0)
            {
                source = DesignationSource.None;
            }

            resolved.Add(new ResolvedDesignation(member.Sequence, effective?.ChoiceId, effective?.Legend ?? string.Empty, source, unlisted));
        }

        return resolved;
    }
}
