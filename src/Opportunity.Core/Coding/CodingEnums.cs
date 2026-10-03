namespace Opportunity.Core.Coding;

/// <summary>
/// Who made a coding change (legal finding 15): automated coding must stay distinguishable from human review.
/// Stored as smallint; values are fixed forever.
/// </summary>
public enum CodingActorType : short
{
    /// <summary>A reviewer coding interactively.</summary>
    Human = 1,

    /// <summary>A bulk coding job started by a person (ADR-010).</summary>
    BulkHuman = 2,

    /// <summary>A system rule (e.g. propagation, overlay of coding fields when an admin enabled it, Q-31).</summary>
    SystemRule = 3,

    /// <summary>Reserved for model-assisted coding; rejected until a decision allows it.</summary>
    Model = 4,
}

/// <summary>Kind of a CodingEvent. Stored as smallint.</summary>
public enum CodingEventKind : short
{
    /// <summary>A field value changed; the document's version was bumped.</summary>
    ValueChanged = 1,

    /// <summary>
    /// A bulk job left the field unchanged because someone else changed it after the job's start (Q-07, ADR-010 §8.4).
    /// </summary>
    BulkSkippedConcurrentEdit = 2,
}

/// <summary>State-based coding operations (ADR-010 §5.4): re-applying one never changes the result.</summary>
public enum CodingOperationKind
{
    /// <summary>Set the field to a canonical value; a null value clears it.</summary>
    Set = 1,

    /// <summary>MultiChoice: ensure the given choices are present.</summary>
    AddChoices = 2,

    /// <summary>MultiChoice: ensure the given choices are absent.</summary>
    RemoveChoices = 3,
}
