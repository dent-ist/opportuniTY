namespace Opportunity.Core.Documents;

/// <summary>Outcome of family resolution (ADR-009 R10). Stored as smallint.</summary>
public enum FamilyStatus : short
{
    Unresolved = 0,
    Resolved = 1,
    Conflict = 2,
    ParentMissing = 3,
    Gap = 4,
    InvalidRange = 5,
}

/// <summary>Which input produced <see cref="Document.DocumentDate"/> (ADR-009 R25). Stored as smallint.</summary>
public enum DocumentDateSource : short
{
    DateSent = 1,
    DateReceived = 2,
    DateLastModified = 3,
    DateCreated = 4,
    Upstream = 5,
}

/// <summary>Which input produced <see cref="Document.EmailThreadId"/> (ADR-009 R18, R19). Stored as smallint.</summary>
public enum EmailThreadSource : short
{
    Upstream = 1,
    ConversationIndex = 2,
}
