using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Contracts.Messaging.Jobs;

namespace Opportunity.Contracts.Messaging;

/// <summary>Stable <c>messageType</c> names. Never reuse or rename one; a new meaning is a new name.</summary>
public static class MessageTypes
{
    public const string SearchOutbox = "indexing.searchOutbox";
    public const string IndexChunkTask = "indexing.indexChunkTask";
    public const string JobChunk = "jobs.jobChunk";
}

/// <summary>
/// Every message contract of the platform. When a contract gets a new major, register the new type here and the old
/// one with <see cref="MessageTypeRegistry.RegisterPrevious{TPrevious, TCurrent}"/> so consumers keep reading N-1, and
/// add golden samples for both majors (contract tests fail otherwise).
/// </summary>
public static class MessageContracts
{
    public static MessageTypeRegistry CreateRegistry() =>
        new MessageTypeRegistry()
            .Register<SearchOutboxMessage>()
            .Register<IndexChunkTaskMessage>()
            .Register<JobChunkMessage>();
}
