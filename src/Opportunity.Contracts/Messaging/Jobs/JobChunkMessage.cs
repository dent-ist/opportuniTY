namespace Opportunity.Contracts.Messaging.Jobs;

/// <summary>
/// One <c>JobChunk</c> to execute (ADR-010). The worker claims the chunk row by <see cref="ChunkId"/> and takes every
/// scope input (workspace, snapshot, range, initiating actor) from PostgreSQL; the envelope <c>jobId</c> names the job.
/// </summary>
[MessageContract(MessageTypes.JobChunk, 1, 0)]
public sealed record JobChunkMessage
{
    public required Guid ChunkId { get; init; }

    public required int Sequence { get; init; }

    public required JobChunkOperation Operation { get; init; }
}

/// <summary>The <c>OperationKind</c> of ADR-010 §5.1.</summary>
public enum JobChunkOperation
{
    ImportChunk,
    BulkCodingChunk,
    RelationshipChunk,
    IndexChunk,
    ReindexChunk,
    ExportChunk,
    ProductionChunk,
    RenderChunk,
}
