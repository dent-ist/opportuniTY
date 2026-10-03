using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Jobs;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Jobs.Faults;
#endif

namespace Opportunity.IntegrationTests.Jobs;

/// <summary>
/// A state-based bulk-coding stand-in (ADR-010 §5.4): every chunk applies all but one of its items and reports the
/// remaining one as a Q-07 skip of a document derived from the chunk's sequence, so job counters and item results
/// fully describe the outcome. Executions are counted per chunk; a redo is harmless by construction.
/// </summary>
internal sealed class StateBasedExecutor : IJobChunkExecutor
{
    public ChunkOperationKind OperationKind => ChunkOperationKind.BulkCodingChunk;

    public ConcurrentDictionary<Guid, int> Executions { get; } = new();

    /// <summary>Optional per-chunk behaviour, run before the work completes (throw to fail the attempt).</summary>
    public Func<ChunkExecutionContext, CancellationToken, Task>? Before { get; set; }

    public static Guid SkippedDocument(int sequence) => new(sequence, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    public async Task<ChunkExecutionResult> ExecuteAsync(ChunkExecutionContext context, CancellationToken cancellationToken)
    {
        Executions.AddOrUpdate(context.Chunk.Lease.ChunkId, 1, (_, n) => n + 1);
        if (Before is { } before)
        {
            await before(context, cancellationToken);
        }

        await context.CheckFenceAsync(cancellationToken);
        return ChunkExecutionResult.Complete(new ChunkCompletion
        {
            ItemsApplied = context.Chunk.ItemCount - 1,
            ItemResults =
            [
                new JobItemResult(JobItemResultKind.SkippedConcurrentEdit, SkippedDocument(context.Chunk.Sequence), null, 1005, "ConcurrentEdit"),
            ],
        });
    }
}

/// <summary>Builds the messages the dispatcher (E06-T04) would publish for chunks, and consumers without a broker.</summary>
internal static class ChunkMessages
{
    public static readonly JobChunkConsumerOptions WorkerOptions = new() { WorkerId = "integration-worker" };

    public static JobChunkMessage Payload(JobChunkInfo chunk) =>
        new() { ChunkId = chunk.ChunkId, Sequence = chunk.Sequence, Operation = JobChunkOperation.BulkCodingChunk };

    public static OutgoingMessage<JobChunkMessage> Outgoing(JobChunkInfo chunk, Guid? workspaceId = null) => new(
        WorkQueues.BulkCoding,
        Payload(chunk),
        new MessageCorrelation($"corr-{chunk.JobId:N}", WorkspaceId: workspaceId ?? chunk.WorkspaceId, JobId: chunk.JobId),
        chunk.IdempotencyKey)
    {
        Attempt = chunk.AttemptCount,
    };

    /// <summary>A delivery as the transport hands it to the handler.</summary>
    public static ReceivedMessage Received(JobChunkInfo chunk, bool redelivered = false, Guid? workspaceId = null)
    {
        var envelope = new MessageEnvelope
        {
            MessageId = Guid.CreateVersion7(),
            MessageType = MessageTypes.JobChunk,
            SchemaVersion = new SchemaVersion(1, 0),
            WorkspaceId = workspaceId ?? chunk.WorkspaceId,
            JobId = chunk.JobId,
            CorrelationId = $"corr-{chunk.JobId:N}",
            IdempotencyKey = chunk.IdempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Attempt = chunk.AttemptCount,
            Payload = JsonSerializer.SerializeToElement(Payload(chunk), MessageJson.PayloadOptions),
        };
        return new ReceivedMessage(envelope, Payload(chunk), WorkQueues.BulkCoding, redelivered, DeliveryCount: 0, TransportRetry: 0);
    }

    public static JobChunkConsumer Consumer(
        IJobChunkRepository chunks,
        IJobChunkExecutor executor,
        IAuditEventWriter audit,
        JobLeaseOptions? lease = null
#if OPPORTUNITY_FAILPOINTS
        , IFaultInjector? faults = null
#endif
        ) => new(
            chunks, [executor], audit, lease ?? new JobLeaseOptions(), WorkerOptions, NullMessageProcessingMeter.Instance,
            TimeProvider.System, NullLogger<JobChunkConsumer>.Instance
#if OPPORTUNITY_FAILPOINTS
            , metrics: null, faults
#endif
            );
}

/// <summary>Everything a chunk run leaves in PostgreSQL that must not depend on how often messages were delivered.</summary>
internal sealed record JobOutcome(
    JobStatus Status,
    JobCounters Counters,
    IReadOnlyList<(int Sequence, JobChunkStatus Status, string? ErrorCode)> Chunks,
    IReadOnlyList<(int Sequence, JobItemResultKind Kind, Guid? DocumentId, string ReasonCode)> ItemResults)
{
    public static async Task<JobOutcome> CaptureAsync(JobDatabase db, Guid workspaceId, Guid jobId)
    {
        var job = await db.JobAsync(workspaceId, jobId);
        var chunks = await db.ChunksAsync(workspaceId, jobId);
        var results = await db.Jobs.GetItemResultsAsync(new JobItemResultQuery(workspaceId, jobId), TestContext.Current.CancellationToken);
        return new JobOutcome(
            job.Status,
            job.Counters,
            [.. chunks.Select(c => (c.Sequence, c.Status, c.ErrorCode))],
            [.. results.Select(r => (r.ChunkSequence, r.Result.Kind, r.Result.DocumentId, r.Result.ReasonCode))]);
    }
}
