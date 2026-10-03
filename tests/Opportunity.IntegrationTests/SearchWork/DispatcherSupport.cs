using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Data;
using Opportunity.Data.SearchWork;
using Opportunity.Jobs.Dispatch;

namespace Opportunity.IntegrationTests.SearchWork;

/// <summary>A confirmed publish as the dispatcher handed it to the transport.</summary>
internal sealed record Published(WorkQueue Queue, object Payload, Guid? WorkspaceId, long ConfirmedAt)
{
    public long? OutboxId => (Payload as SearchOutboxMessage)?.OutboxId;
}

/// <summary>
/// Wraps the real publisher: records every confirmed publish in confirm order and lets a test delay or "crash" around the
/// broker call (<see cref="Before"/> runs before the publish, <see cref="After"/> after the confirm).
/// </summary>
internal sealed class RecordingPublisher(IMessagePublisher inner) : IMessagePublisher
{
    public ConcurrentQueue<Published> Confirmed { get; } = new();

    public Func<CancellationToken, Task>? Before { get; set; }

    public Func<CancellationToken, Task>? After { get; set; }

    public async Task<MessageEnvelope> PublishAsync<TPayload>(OutgoingMessage<TPayload> message, CancellationToken cancellationToken = default)
        where TPayload : class
    {
        if (Before is { } before)
        {
            await before(cancellationToken);
        }

        var envelope = await inner.PublishAsync(message, cancellationToken);
        Confirmed.Enqueue(new Published(message.Destination, message.Payload, message.Correlation.WorkspaceId, Stopwatch.GetTimestamp()));
        if (After is { } after)
        {
            await after(cancellationToken);
        }

        return envelope;
    }

    public IReadOnlyList<long> OutboxIds(Guid workspaceId) =>
        [.. Confirmed.Where(p => p.WorkspaceId == workspaceId && p.OutboxId is not null).Select(p => p.OutboxId!.Value)];
}

/// <summary>
/// One dispatcher instance composed exactly as the dispatcher worker module does (<c>AddOutboxDispatcher</c> over the
/// PostgreSQL stores), connected as the application login, running until disposed.
/// </summary>
internal sealed class RunningDispatcher : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    private RunningDispatcher(ServiceProvider provider)
    {
        _provider = provider;
        Dispatcher = provider.GetRequiredService<OutboxDispatcher>();
        _run = Task.Run(() => Dispatcher.RunAsync(_stop.Token));
    }

    public OutboxDispatcher Dispatcher { get; }

    public bool Stopped => _run.IsCompleted;

    /// <summary>Test timings: short claims, fast polling, frequent backlog samples.</summary>
    public static OutboxDispatcherOptions FastOptions(string owner) => new()
    {
        Owner = owner,
        ClaimDuration = TimeSpan.FromSeconds(30),
        BacklogSampleInterval = TimeSpan.FromMilliseconds(200),
        WorkspaceRefreshInterval = TimeSpan.FromMilliseconds(500),
        RetryDelay = TimeSpan.FromMilliseconds(200),
    };

    public static RunningDispatcher Start(
        SearchWorkDatabase db, IMessagePublisher publisher, OutboxDispatcherOptions options, OpportunityMetrics? metrics = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(db.Core.AppDataSource);
        services.AddSingleton(publisher);
        if (metrics is not null)
        {
            services.AddSingleton(metrics);
        }

        services.AddPostgresSearchWorkStore();
        services.AddPostgresJobChunkStore();
        services.AddOutboxDispatcher(options);
        return new RunningDispatcher(services.BuildServiceProvider());
    }

    /// <summary>Stops like a host shutdown: no new pass, running passes get the shutdown grace.</summary>
    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        await _run.WaitAsync(TimeSpan.FromSeconds(60));
    }

    public async ValueTask DisposeAsync()
    {
        if (!_run.IsCompleted)
        {
            await StopAsync();
        }

        await _provider.DisposeAsync();
        _stop.Dispose();
    }
}

internal static class DispatcherTestData
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One interactive transaction writing outbox rows for <paramref name="count"/> new document ids.</summary>
    public static async Task<long> AddOutboxRowsAsync(this SearchWorkDatabase db, Guid workspaceId, int count, SearchChangeMask mask)
    {
        long generation = 0;
        await db.Core.InWorkspaceAsync(workspaceId, async tx =>
            generation = await SearchWorkSql.AddOutboxRowsAsync(
                tx, [.. Enumerable.Range(0, count).Select(_ => (Guid.CreateVersion7(), 1L))], mask, Ct));
        return generation;
    }

    /// <summary>Rows of the workspace's outbox by status.</summary>
    public static async Task<Dictionary<SearchOutboxStatus, long>> OutboxStatusesAsync(this SearchWorkDatabase db, Guid workspaceId)
    {
        var counts = new Dictionary<SearchOutboxStatus, long>();
        foreach (SearchOutboxStatus status in Enum.GetValues<SearchOutboxStatus>())
        {
            counts[status] = await db.Core.ScalarAsync<long>(
                "SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND status = @s",
                ("ws", workspaceId), ("s", (short)status));
        }

        return counts;
    }

    public static Task<List<long>> OutboxIdsAsync(this SearchWorkDatabase db, Guid workspaceId) =>
        db.Core.ScalarAsync<long[]>(
            "SELECT coalesce(array_agg(outbox_id ORDER BY outbox_id), '{}') FROM opportunity.search_outbox WHERE workspace_id = @ws",
            ("ws", workspaceId)).ContinueWith(t => t.Result.ToList(), TaskScheduler.Default);

    /// <summary>A Running bulk-coding job of <paramref name="chunks"/> chunks (nothing claimed).</summary>
    public static async Task<Guid> RunningJobAsync(this SearchWorkDatabase db, Guid workspaceId, int chunks)
    {
        var snapshot = Guid.CreateVersion7();
        var created = await db.Jobs.CreateAsync(new NewJob
        {
            WorkspaceId = workspaceId,
            JobType = JobType.BulkCoding,
            InitiatedBy = SearchWorkDatabase.Reviewer,
            TargetSnapshotId = snapshot,
        }, Ct);
        await db.Jobs.BeginPreparingAsync(workspaceId, created.Job.JobId, Ct);
        var plans = ChunkPlanner.SplitByCount(1, chunks * 100L, 100)
            .Select(r => new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, r.From, r.To), (int)r.Count))
            .ToList();
        await db.Jobs.StartAsync(new JobStartRequest(workspaceId, created.Job.JobId, ChunkOperationKind.BulkCodingChunk, plans), Ct);
        return created.Job.JobId;
    }

    /// <summary>Index chunk tasks for chunks of <paramref name="jobId"/> (as committed chunks write them).</summary>
    public static async Task<List<Guid>> AddIndexTasksAsync(
        this SearchWorkDatabase db, Guid workspaceId, Guid jobId, int count, SearchChangeMask mask = SearchChangeMask.Coding)
    {
        var chunks = await db.Jobs.GetChunksAsync(workspaceId, jobId, cancellationToken: Ct);
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var chunk = chunks[i % chunks.Count];
            await db.Core.InWorkspaceAsync(workspaceId, async tx =>
            {
                var (taskId, _) = await SearchWorkSql.AddIndexChunkTaskAsync(tx, new NewIndexChunkTask
                {
                    JobId = jobId,
                    ChunkId = chunk.ChunkId,
                    Kind = IndexTaskKind.BulkCoding,
                    Membership = chunk.Membership,
                    ChangeMask = mask,
                    IdempotencyKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                }, Ct);
                ids.Add(taskId);
            });
        }

        return ids;
    }

    public static async Task EventuallyAsync(Func<Task<bool>> condition, TimeSpan? timeout = null, string? because = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met in time{(because is null ? "" : ": " + because)}.");
            }

            await Task.Delay(50, Ct);
        }
    }
}
