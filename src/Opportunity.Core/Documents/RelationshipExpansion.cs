namespace Opportunity.Core.Documents;

/// <summary>
/// Which relationships to add to a document set (E09-T03, ADR-002 §5.2.1, ADR-009 R20): the families, duplicate groups
/// and email threads of the set's documents. One definition for every consumer: interactive search
/// (<c>Opportunity.Search.Querying.RelationshipExpansionQuery</c>, over the projection) and snapshot materialization
/// (<c>Opportunity.Data.Relationships.RelationshipExpansionSql</c>, over the authoritative PostgreSQL relationships).
/// </summary>
/// <remarks>
/// <para>Semantics (<see cref="Steps"/>), from the base set ("seeds": search hits, explicit IDs or a source snapshot's
/// members), each step adding only documents not already in the set and labelled with that step's reason:</para>
/// <list type="number">
/// <item><b>Family</b> of the seeds (same <c>FamilyId</c>);</item>
/// <item><b>Duplicates</b> of the seeds (same <c>DuplicateGroupId</c>);</item>
/// <item><b>Thread</b> of the seeds (same <c>EmailThreadId</c>);</item>
/// <item><b>Family</b> of what steps 2 and 3 added, when family is asked together with duplicates or thread, so expanded
/// emails bring their attachments (attachments carry no thread id, ADR-009 R20).</item>
/// </list>
/// <para>A document reached by several steps keeps the first label, so a hit's own attachment is "family" even when it
/// shares the hit's duplicate group (attachments inherit their parent's group, ADR-009 R14). Expansion never adds a
/// document the person may not see: expanded members are authorized exactly like hits (Q-11, Q-13, Q-52), and counts
/// exclude them.</para>
/// </remarks>
public readonly record struct RelationshipExpansion(bool Family, bool Duplicates, bool Thread)
{
    public static RelationshipExpansion None => default;

    public bool IsNone => !Family && !Duplicates && !Thread;

    /// <summary>Family of the duplicate- and thread-expanded documents (step 4).</summary>
    public bool FamilyOfExpanded => Family && (Duplicates || Thread);

    /// <summary>The stored form (smallint bit flags: 1 family, 2 duplicates, 4 thread). Values are fixed forever.</summary>
    public short Flags => (short)((Family ? 1 : 0) | (Duplicates ? 2 : 0) | (Thread ? 4 : 0));

    public static RelationshipExpansion FromFlags(short flags) => new((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);

    /// <summary>The steps in order; each adds only documents no earlier step (or the base set) holds.</summary>
    public IReadOnlyList<RelationshipExpansionStep> Steps
    {
        get
        {
            var steps = new List<RelationshipExpansionStep>(4);
            if (Family)
            {
                steps.Add(new RelationshipExpansionStep(RelationshipKind.Family, ExpansionSeed.Base));
            }

            if (Duplicates)
            {
                steps.Add(new RelationshipExpansionStep(RelationshipKind.Duplicate, ExpansionSeed.Base));
            }

            if (Thread)
            {
                steps.Add(new RelationshipExpansionStep(RelationshipKind.Thread, ExpansionSeed.Base));
            }

            if (FamilyOfExpanded)
            {
                steps.Add(new RelationshipExpansionStep(RelationshipKind.Family, ExpansionSeed.DuplicateAndThreadAdditions));
            }

            return steps;
        }
    }

    /// <summary>"family+duplicates", "thread", "none": for audit details and logs.</summary>
    public override string ToString() =>
        IsNone ? "none" : string.Join('+', new[] { Family ? "family" : null, Duplicates ? "duplicates" : null, Thread ? "thread" : null }.OfType<string>());
}

/// <summary>A relationship a document can be added through. Stored values match the snapshot inclusion reasons (3, 4, 5).</summary>
public enum RelationshipKind : short
{
    Family = 3,
    Duplicate = 4,
    Thread = 5,
}

/// <summary>Which documents a step reads relationship keys from.</summary>
public enum ExpansionSeed
{
    /// <summary>The base set (hits, explicit IDs or a source snapshot's members).</summary>
    Base,

    /// <summary>The documents the duplicate and thread steps added.</summary>
    DuplicateAndThreadAdditions,
}

public readonly record struct RelationshipExpansionStep(RelationshipKind Kind, ExpansionSeed Seed);
