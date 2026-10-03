namespace Opportunity.Contracts.Messaging.Indexing;

/// <summary>
/// One interactive <c>SearchOutbox</c> row to project (ADR-001 §1 R3). Payload-free: the index worker rebuilds the
/// document from current PostgreSQL state; <see cref="DocumentVersion"/> is a hint, not the version written.
/// Published to the security (L0) or interactive (L1) lane.
/// </summary>
[MessageContract(MessageTypes.SearchOutbox, 1, 0)]
public sealed record SearchOutboxMessage
{
    public required long OutboxId { get; init; }

    public required Guid DocumentId { get; init; }

    public required long DocumentVersion { get; init; }
}

/// <summary>
/// One committed <c>IndexChunkTask</c> (ADR-001 §1, ADR-010 §4). The worker loads the task row, its membership
/// reference and scope from PostgreSQL. Published to the security-bulk (L2) or bulk (L3) lane.
/// </summary>
[MessageContract(MessageTypes.IndexChunkTask, 1, 0)]
public sealed record IndexChunkTaskMessage
{
    public required Guid TaskId { get; init; }
}
