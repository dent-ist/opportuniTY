using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;

namespace Opportunity.Application.Jobs;

public sealed class JobChunkRelayOptions
{
    /// <summary>Identifies this dispatcher instance in claim columns; unique per process.</summary>
    public string Owner { get; init; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>How long a claim protects chunks while they are published; a crashed relay's chunks are reclaimed after it.</summary>
    public TimeSpan ClaimDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay before a chunk whose publish was not confirmed is offered again.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public JobChunkDispatchLimits Limits { get; init; } = new() { Operations = JobChunkRelay.DispatchedOperations };
}

/// <summary>
/// Publishes job chunks (ADR-010 §2: <c>Pending → Dispatched</c> after a broker confirm) for one workspace per pass:
/// claim within the concurrency and backpressure limits, publish a payload-free <see cref="JobChunkMessage"/> to the
/// operation's work queue, mark the confirmed chunks Dispatched. Delivery is at-least-once; the worker's claim is the
/// inbox, so a duplicate is acked and dropped (ADR-010 §5.3).
/// </summary>
public sealed class JobChunkRelay(
    IJobChunkDispatchRepository chunks, IMessagePublisher publisher, JobChunkRelayOptions options, DispatchMetrics? metrics = null)
{
    /// <summary>Operation kinds that have a work queue today; others wait for the epic that adds their worker.</summary>
    public static IReadOnlyCollection<ChunkOperationKind> DispatchedOperations { get; } =
        [.. Enum.GetValues<ChunkOperationKind>().Where(kind => QueueFor(kind) is not null)];

    public async Task<DispatchPassResult> RelayAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var claimed = await chunks.ClaimForDispatchAsync(workspaceId, options.Owner, options.Limits, options.ClaimDuration, cancellationToken)
            .ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return DispatchPassResult.None;
        }

        var outcomes = await Task.WhenAll(claimed.Select(chunk => TryPublishAsync(chunk, cancellationToken))).ConfigureAwait(false);
        var confirmed = claimed.Where((_, i) => outcomes[i]).Select(c => c.ChunkId).ToList();
        var unconfirmed = claimed.Where((_, i) => !outcomes[i]).Select(c => c.ChunkId).ToList();
        if (confirmed.Count > 0)
        {
            await chunks.MarkDispatchedAsync(workspaceId, options.Owner, confirmed, cancellationToken).ConfigureAwait(false);
        }

        if (unconfirmed.Count > 0)
        {
            await chunks.ReleaseDispatchClaimAsync(workspaceId, options.Owner, unconfirmed, options.RetryDelay, cancellationToken)
                .ConfigureAwait(false);
        }

        return new DispatchPassResult(confirmed.Count, unconfirmed.Count, claimed.Count >= options.Limits.BatchSize);
    }

    /// <summary>The work queue of an operation kind, or null while no worker consumes it.</summary>
    public static WorkQueue? QueueFor(ChunkOperationKind operation) => operation switch
    {
        ChunkOperationKind.ImportChunk => WorkQueues.Import,
        ChunkOperationKind.BulkCodingChunk => WorkQueues.BulkCoding,
        ChunkOperationKind.RenderChunk => WorkQueues.Rendering,
        ChunkOperationKind.ExportChunk => WorkQueues.Export,
        ChunkOperationKind.ProductionChunk => WorkQueues.Production,
        // Relationship fix-ups (E09-T01) and reindex (E07-T11) choose their queue with their worker.
        _ => null,
    };

    /// <summary>The message for one chunk: identifiers only; the worker reads everything else from PostgreSQL (ADR-010 §9).</summary>
    public static OutgoingMessage<JobChunkMessage> Message(ClaimedJobChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var queue = QueueFor(chunk.Operation)
            ?? throw new ArgumentException($"No work queue consumes {chunk.Operation} yet.", nameof(chunk));
        return new OutgoingMessage<JobChunkMessage>(
            queue,
            new JobChunkMessage { ChunkId = chunk.ChunkId, Sequence = chunk.Sequence, Operation = Enum.Parse<JobChunkOperation>(chunk.Operation.ToString()) },
            new MessageCorrelation(chunk.CorrelationId ?? $"job:{chunk.JobId:N}", WorkspaceId: chunk.WorkspaceId, JobId: chunk.JobId),
            chunk.IdempotencyKey)
        {
            Attempt = chunk.AttemptCount,
        };
    }

    private async Task<bool> TryPublishAsync(ClaimedJobChunk chunk, CancellationToken cancellationToken)
    {
        var message = Message(chunk);
        try
        {
            await publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            metrics?.Published(message.Destination, confirmed: true);
            return true;
        }
        catch (MessagePublishException)
        {
            metrics?.Published(message.Destination, confirmed: false);
            return false;
        }
    }
}
