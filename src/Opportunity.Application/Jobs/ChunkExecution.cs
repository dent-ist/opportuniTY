using Opportunity.Application.Messaging;
using Opportunity.Core.Jobs;

namespace Opportunity.Application.Jobs;

/// <summary>
/// Runs the work of one claimed chunk for one <see cref="ChunkOperationKind"/>. The idempotent consumer (E06-T05) has
/// already validated the envelope against the PostgreSQL row, claimed the chunk (the inbox) and resolved workspace,
/// actor and parameters from PG; it records the outcome and acks only after that commit.
/// <list type="bullet">
/// <item>Return <see cref="ChunkExecutionResult.Complete"/> and the consumer commits the chunk (fence F3). External
/// side effects must then be state-based or keyed by <see cref="ClaimedChunk.IdempotencyKey"/>, so a redo is a no-op.</item>
/// <item>Data-layer executors whose PG writes must commit atomically with the chunk commit them in their own
/// transaction and return <see cref="ChunkExecutionResult.Committed"/>.</item>
/// <item>Throw <see cref="PermanentChunkException"/> for errors retrying cannot fix (the chunk fails at once) and
/// <see cref="TransientChunkException"/> (or any transient exception) for retryable ones.</item>
/// <item>Call <see cref="ChunkExecutionContext.CheckFenceAsync"/> before each external side-effect batch (fence F2).</item>
/// </list>
/// </summary>
public interface IJobChunkExecutor
{
    ChunkOperationKind OperationKind { get; }

    Task<ChunkExecutionResult> ExecuteAsync(ChunkExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>What an executor gets: the claimed chunk (authoritative, from PG) and the message that carried it.</summary>
public sealed class ChunkExecutionContext(
    ClaimedChunk chunk, ReceivedMessage message, Func<CancellationToken, Task<ChunkFence>> checkFence)
{
    private readonly Func<CancellationToken, Task<ChunkFence>> _checkFence = checkFence ?? throw new ArgumentNullException(nameof(checkFence));

    public ClaimedChunk Chunk { get; } = chunk ?? throw new ArgumentNullException(nameof(chunk));

    /// <summary>The delivery; its envelope is a hint only and already matched against <see cref="Chunk"/>.</summary>
    public ReceivedMessage Message { get; } = message ?? throw new ArgumentNullException(nameof(message));

    public ChunkLease Lease => Chunk.Lease;

    public Guid WorkspaceId => Chunk.Lease.WorkspaceId;

    /// <summary>The actor every action of the chunk runs as, from the job row (ADR-010 §9).</summary>
    public Guid InitiatedBy => Chunk.InitiatedBy;

    /// <summary>
    /// Fence F2: extends the lease and throws <see cref="ChunkFencedException"/> when the lease was lost, the job is no
    /// longer Running or the workspace no longer Active (e.g. Deleting). Call before each external side-effect batch.
    /// </summary>
    public async Task CheckFenceAsync(CancellationToken cancellationToken)
    {
        var fence = await _checkFence(cancellationToken).ConfigureAwait(false);
        if (fence != ChunkFence.Proceed)
        {
            throw new ChunkFencedException(fence);
        }
    }
}

/// <summary>How an executor finished a chunk.</summary>
public sealed record ChunkExecutionResult
{
    private ChunkExecutionResult(ChunkCompletion? completion, ChunkCommitResult? commit)
    {
        Completion = completion;
        CommitResult = commit;
    }

    /// <summary>The work is done; the consumer commits the chunk with this completion.</summary>
    public ChunkCompletion? Completion { get; }

    /// <summary>The executor already ran fence F3 in its own transaction; this is that outcome.</summary>
    public ChunkCommitResult? CommitResult { get; }

    public static ChunkExecutionResult Complete(ChunkCompletion completion) =>
        new(completion ?? throw new ArgumentNullException(nameof(completion)), null);

    public static ChunkExecutionResult Committed(ChunkCommitResult result) =>
        new(null, result ?? throw new ArgumentNullException(nameof(result)));
}

/// <summary>An error retrying cannot fix (validation, mapping, invalid parameters): the chunk fails at once.</summary>
public sealed class PermanentChunkException : Exception
{
    public PermanentChunkException()
        : this("PermanentError", "The chunk failed permanently.")
    {
    }

    public PermanentChunkException(string message)
        : this("PermanentError", message)
    {
    }

    public PermanentChunkException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "PermanentError";
    }

    public PermanentChunkException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Stored as the chunk's <c>ErrorCode</c>.</summary>
    public string Code { get; }
}

/// <summary>A retryable error: the chunk waits with backoff and is dispatched again while attempts remain.</summary>
public sealed class TransientChunkException : Exception
{
    public TransientChunkException()
        : this("TransientError", "The chunk failed transiently.")
    {
    }

    public TransientChunkException(string message)
        : this("TransientError", message)
    {
    }

    public TransientChunkException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "TransientError";
    }

    public TransientChunkException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

/// <summary>A fence stopped the chunk (<see cref="ChunkExecutionContext.CheckFenceAsync"/>); nothing more may be written.</summary>
public sealed class ChunkFencedException : Exception
{
    public ChunkFencedException()
        : this(ChunkFence.LeaseLost)
    {
    }

    public ChunkFencedException(string message)
        : base(message)
    {
        Fence = ChunkFence.LeaseLost;
    }

    public ChunkFencedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Fence = ChunkFence.LeaseLost;
    }

    public ChunkFencedException(ChunkFence fence)
        : base($"Chunk stopped at a fence: {fence}.")
    {
        Fence = fence;
    }

    public ChunkFence Fence { get; }
}
