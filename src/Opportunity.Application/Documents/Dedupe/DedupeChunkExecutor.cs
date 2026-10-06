using Opportunity.Application.Jobs;
using Opportunity.Core.Documents;
using Opportunity.Core.Jobs;

namespace Opportunity.Application.Documents.Dedupe;

/// <summary>
/// The <see cref="ChunkOperationKind.RelationshipChunk"/> executor of a dedupe run (E09-T04), run by the idempotent chunk
/// consumer of the import worker. The policy comes from the job's parameters (frozen at submission), never from the
/// message. The whole run is one PostgreSQL transaction (<see cref="IDedupeStore.RunAsync"/>) that ends with fence F3,
/// so a replayed or redelivered chunk either finds it committed or applies it from scratch; the result is the same.
/// </summary>
public sealed class DedupeChunkExecutor(IDedupeStore store) : IJobChunkExecutor
{
    public ChunkOperationKind OperationKind => ChunkOperationKind.RelationshipChunk;

    public async Task<ChunkExecutionResult> ExecuteAsync(ChunkExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var chunk = context.Chunk;
        if (chunk.JobType != JobType.RelationshipFixup || chunk.Membership != DedupeService.WholeWorkspace)
        {
            throw new PermanentChunkException("NotADedupeChunk", "A dedupe run is one RelationshipFixup chunk over the whole workspace.");
        }

        DedupePolicy policy;
        try
        {
            policy = DedupePolicy.Parse(chunk.Parameters);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            throw new PermanentChunkException("InvalidParameters", ex.Message, ex);
        }

        // Fence F2 before the PostgreSQL work; F3 runs inside the run's transaction.
        await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
        var result = await store.RunAsync(chunk, policy, cancellationToken).ConfigureAwait(false);
        return ChunkExecutionResult.Committed(result.Commit);
    }
}
