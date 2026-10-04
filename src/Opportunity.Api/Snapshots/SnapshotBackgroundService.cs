using Opportunity.Application.Snapshots;

namespace Opportunity.Api.Snapshots;

/// <summary>
/// Materializes snapshots too large for the request (202) and takes over those whose materializing process died (claim
/// expired), then applies retention (ADR-002 §9). Hosted by the API, which already composes search and the PDP; any
/// number of instances may run it: claims and retention are serialized in PostgreSQL.
/// </summary>
internal sealed partial class SnapshotBackgroundService(
    IServiceScopeFactory scopes,
    SnapshotMaterializationSignal queue,
    SnapshotOptions options,
    TimeProvider time,
    ILogger<SnapshotBackgroundService> logger) : BackgroundService
{
    private const int RetentionBatch = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.BackgroundEnabled)
        {
            return;
        }

        var nextRetention = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (queue.Reader.TryRead(out var nudge))
                {
                    await MaterializeAsync(nudge.WorkspaceId, nudge.SnapshotId, stoppingToken).ConfigureAwait(false);
                }

                await PollAsync(time.GetUtcNow() >= nextRetention, stoppingToken).ConfigureAwait(false);
                if (time.GetUtcNow() >= nextRetention)
                {
                    nextRetention = time.GetUtcNow() + options.RetentionInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // PostgreSQL or OpenSearch unavailable (e.g. a host without them configured): retry on the next pass.
                LogPassFailed(logger, ex);
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(options.PollInterval);
            try
            {
                await queue.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollAsync(bool retention, CancellationToken cancellationToken)
    {
        // The store is a singleton; resolved lazily so a host without PostgreSQL only logs the failed pass.
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentSetSnapshotStore>();
        foreach (var ws in await store.GetWorkspacesAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var id in await store.GetUnclaimedAsync(ws, 10, cancellationToken).ConfigureAwait(false))
            {
                await MaterializeAsync(ws, id, cancellationToken).ConfigureAwait(false);
            }

            if (retention)
            {
                var expired = await store.ExpireAsync(ws, options.UnreferencedLifetime, options.JobRetention, RetentionBatch, cancellationToken)
                    .ConfigureAwait(false);
                if (expired.Count > 0)
                {
                    LogExpired(logger, expired.Count, ws);
                }
            }
        }
    }

    private async Task MaterializeAsync(Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken)
    {
        // One DI scope per snapshot, like one request: the PDP caches principal state per scope only.
        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DocumentSetSnapshotService>();
        var status = await service.MaterializePendingAsync(workspaceId, snapshotId, cancellationToken).ConfigureAwait(false);
        if (status is { } s)
        {
            LogMaterialized(logger, snapshotId, workspaceId, s);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshot {SnapshotId} in workspace {WorkspaceId} materialized in the background: {Status}")]
    private static partial void LogMaterialized(ILogger logger, Guid snapshotId, Guid workspaceId, Opportunity.Core.Snapshots.SnapshotStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} snapshot(s) in workspace {WorkspaceId}")]
    private static partial void LogExpired(ILogger logger, int count, Guid workspaceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Snapshot background pass failed; retrying")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
