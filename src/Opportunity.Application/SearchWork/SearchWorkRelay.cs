using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.Jobs;

namespace Opportunity.Application.SearchWork;

public sealed class SearchWorkRelayOptions
{
    /// <summary>Identifies this dispatcher instance in claim columns; unique per process.</summary>
    public string Owner { get; init; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public int BatchSize { get; init; } = 500;

    /// <summary>How long a claim protects rows while they are published; a crashed relay's rows are reclaimed after it.</summary>
    public TimeSpan ClaimDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay before an unconfirmed chunk task is offered again.</summary>
    public TimeSpan TaskRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <param name="Published">Rows/tasks confirmed by the broker and marked Dispatched.</param>
/// <param name="Unconfirmed">Rows/tasks whose publish failed and were released for a later attempt.</param>
public sealed record SearchWorkRelayResult(int OutboxPublished, int OutboxUnconfirmed, int TasksPublished, int TasksUnconfirmed)
{
    public int Claimed => OutboxPublished + OutboxUnconfirmed + TasksPublished + TasksUnconfirmed;
}

/// <summary>
/// One relay pass of the transactional outbox for one workspace (ADR-001 §6.1): claim a batch, publish every row with a
/// broker confirm, then mark the confirmed rows Dispatched. A crash between confirm and mark leaves the claim to expire
/// and the row is published again: delivery is at-least-once, which payload-free, version-guarded indexing makes
/// harmless. No ordering is provided or needed (ADR-001 §5.1). The dispatcher host (E06-T04) decides when and for which
/// workspaces this runs (LISTEN/NOTIFY wake-up, polling fallback, N instances).
/// </summary>
public sealed class SearchWorkRelay(
    ISearchOutboxRepository outbox, IIndexChunkTaskRepository tasks, IMessagePublisher publisher, SearchWorkRelayOptions options)
{
    public async Task<SearchWorkRelayResult> RelayOnceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var (outboxPublished, outboxFailed) = await RelayOutboxAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var (tasksPublished, tasksFailed) = await RelayTasksAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return new SearchWorkRelayResult(outboxPublished, outboxFailed, tasksPublished, tasksFailed);
    }

    private async Task<(int Published, int Unconfirmed)> RelayOutboxAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var claimed = await outbox.ClaimAsync(workspaceId, options.Owner, options.BatchSize, options.ClaimDuration, cancellationToken)
            .ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return (0, 0);
        }

        var outcomes = await Task.WhenAll(claimed.Select(row => TryPublishAsync(new OutgoingMessage<SearchOutboxMessage>(
            SearchLanes.Queue(row.Lane),
            new SearchOutboxMessage { OutboxId = row.OutboxId, DocumentId = row.DocumentId, DocumentVersion = row.DocumentVersion },
            new MessageCorrelation($"search-outbox:{row.OutboxId}", WorkspaceId: row.WorkspaceId),
            ChunkIdempotencyKey.ForOutbox(row.WorkspaceId, row.OutboxId))
        { Attempt = row.AttemptCount }, cancellationToken))).ConfigureAwait(false);

        var confirmed = claimed.Where((_, i) => outcomes[i] is null).ToList();
        var unconfirmed = claimed.Where((_, i) => outcomes[i] is not null).ToList();
        if (confirmed.Count > 0)
        {
            await outbox.MarkDispatchedAsync(workspaceId, options.Owner, confirmed, cancellationToken).ConfigureAwait(false);
        }

        if (unconfirmed.Count > 0)
        {
            var error = outcomes.First(o => o is not null)!;
            await outbox.ReleaseAsync(workspaceId, options.Owner, unconfirmed, error, cancellationToken).ConfigureAwait(false);
        }

        return (confirmed.Count, unconfirmed.Count);
    }

    private async Task<(int Published, int Unconfirmed)> RelayTasksAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var claimed = await tasks.ClaimForDispatchAsync(workspaceId, options.Owner, options.BatchSize, options.ClaimDuration, cancellationToken)
            .ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return (0, 0);
        }

        var outcomes = await Task.WhenAll(claimed.Select(task => TryPublishAsync(new OutgoingMessage<IndexChunkTaskMessage>(
            SearchLanes.Queue(task.Lane),
            new IndexChunkTaskMessage { TaskId = task.TaskId },
            new MessageCorrelation($"index-task:{task.TaskId}", WorkspaceId: task.WorkspaceId, JobId: task.JobId),
            task.IdempotencyKey)
        { Attempt = task.AttemptCount }, cancellationToken))).ConfigureAwait(false);

        var confirmed = claimed.Where((_, i) => outcomes[i] is null).Select(t => t.TaskId).ToList();
        var unconfirmed = claimed.Where((_, i) => outcomes[i] is not null).Select(t => t.TaskId).ToList();
        if (confirmed.Count > 0)
        {
            await tasks.MarkDispatchedAsync(workspaceId, options.Owner, confirmed, cancellationToken).ConfigureAwait(false);
        }

        if (unconfirmed.Count > 0)
        {
            await tasks.ReleaseClaimAsync(workspaceId, options.Owner, unconfirmed, options.TaskRetryDelay, cancellationToken)
                .ConfigureAwait(false);
        }

        return (confirmed.Count, unconfirmed.Count);
    }

    /// <summary>Null when the broker confirmed the message, otherwise the error to record.</summary>
    private async Task<string?> TryPublishAsync<TPayload>(OutgoingMessage<TPayload> message, CancellationToken cancellationToken)
        where TPayload : class
    {
        try
        {
            await publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (MessagePublishException ex)
        {
            return ex.Message;
        }
    }
}
