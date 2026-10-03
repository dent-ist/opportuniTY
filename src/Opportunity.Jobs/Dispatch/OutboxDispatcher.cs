using Microsoft.Extensions.Logging;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;

namespace Opportunity.Jobs.Dispatch;

/// <summary>
/// <c>Opportunity.Worker.Dispatcher</c>'s engine (E06-T04, ADR-001 §6, ADR-010 §2): publishes SearchOutbox rows,
/// IndexChunkTasks and job chunks with broker confirms and marks them Dispatched.
/// <list type="bullet">
/// <item><b>Wake-up:</b> <c>LISTEN</c> on the outbox channel queues the notified workspace at once (security-lane
/// wake-ups ahead of everything else); polling covers what notifications miss — SearchOutbox every second (250 ms while
/// the LISTEN session is down), IndexChunkTasks every 500 ms (they never notify), job chunks every second.</item>
/// <item><b>Fairness:</b> passes over one kind of work in one workspace are scheduled round-robin by
/// <see cref="DispatchScheduler"/>; within a pass the claim takes higher-priority lanes first.</item>
/// <item><b>Multi-replica safety:</b> every claim is <c>FOR UPDATE SKIP LOCKED</c> and owned by this instance; a crashed
/// instance's claims expire and are taken over. Delivery is at-least-once; duplicates are harmless by design.</item>
/// <item><b>Shutdown:</b> no new pass starts once stopping; running passes get
/// <see cref="OutboxDispatcherOptions.ShutdownGrace"/> to finish, then are cancelled (their claims expire).</item>
/// </list>
/// </summary>
public sealed partial class OutboxDispatcher : ISearchWorkWakeUpObserver, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(30);

    private readonly SearchWorkRelay _searchWork;
    private readonly JobChunkRelay _jobChunks;
    private readonly ISearchWorkMaintenance _maintenance;
    private readonly ISearchWorkWakeUpListener _listener;
    private readonly OutboxBacklogMonitor _backlog;
    private readonly OutboxDispatcherOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly DispatchScheduler _scheduler = new();
    private readonly Lock _errorGate = new();
    private IReadOnlyList<Guid> _workspaces = [];
    private DateTimeOffset _errorLoggedAt = DateTimeOffset.MinValue;
    private volatile bool _listening;
    private volatile bool _catchUp;

    public OutboxDispatcher(
        SearchWorkRelay searchWork,
        JobChunkRelay jobChunks,
        ISearchWorkMaintenance maintenance,
        ISearchWorkWakeUpListener listener,
        OutboxBacklogMonitor backlog,
        OutboxDispatcherOptions options,
        TimeProvider time,
        ILogger<OutboxDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _searchWork = searchWork ?? throw new ArgumentNullException(nameof(searchWork));
        _jobChunks = jobChunks ?? throw new ArgumentNullException(nameof(jobChunks));
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _backlog = backlog ?? throw new ArgumentNullException(nameof(backlog));
        _options = options;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether the LISTEN session is subscribed (otherwise SearchOutbox is polled faster).</summary>
    public bool Listening => _listening;

    public OutboxBacklogMonitor Backlog => _backlog;

    /// <summary>Runs until <paramref name="stoppingToken"/> is cancelled, then drains running passes (bounded by the grace).</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var passes = new CancellationTokenSource();
        await using var drain = stoppingToken.Register(() => passes.CancelAfter(_options.ShutdownGrace));

        var loops = new List<Task>
        {
            Guard(() => _listener.ListenAsync(this, stoppingToken), "listener"),
            Guard(() => PollAsync(stoppingToken), "poller"),
            Guard(() => SampleBacklogAsync(stoppingToken), "backlog sampler"),
        };
        loops.AddRange(Enumerable.Range(0, _options.MaxConcurrency).Select(_ => Guard(() => WorkAsync(stoppingToken, passes.Token), "worker")));
        LogStarted(_logger, _options.Owner, _options.MaxConcurrency);
        await Task.WhenAll(loops).ConfigureAwait(false);
        LogStopped(_logger, _options.Owner);
    }

    /// <summary>Runs one pass of <paramref name="item"/> now (also used by the workers).</summary>
    public Task<DispatchPassResult> RunPassAsync(DispatchItem item, CancellationToken cancellationToken) => item.Work switch
    {
        DispatchWork.Outbox => _searchWork.RelayOutboxAsync(item.WorkspaceId, cancellationToken),
        DispatchWork.IndexTasks => _searchWork.RelayTasksAsync(item.WorkspaceId, cancellationToken),
        DispatchWork.JobChunks => _jobChunks.RelayAsync(item.WorkspaceId, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(item), item.Work, null),
    };

    public void Dispose() => _scheduler.Dispose();

    void ISearchWorkWakeUpObserver.OnListening()
    {
        _listening = true;
        _catchUp = true;
    }

    void ISearchWorkWakeUpObserver.OnWakeUp(SearchWorkWakeUp wakeUp) =>
        _scheduler.Signal(new DispatchItem(wakeUp.WorkspaceId, DispatchWork.Outbox), urgent: wakeUp.Lane == MessageLane.Security);

    void ISearchWorkWakeUpObserver.OnLost(Exception exception) => _listening = false;

    private async Task WorkAsync(CancellationToken stoppingToken, CancellationToken passToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DispatchItem item;
            try
            {
                item = await _scheduler.TakeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var more = false;
            try
            {
                more = (await RunPassAsync(item, passToken).ConfigureAwait(false)).More;
            }
            catch (OperationCanceledException) when (passToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailure(item, ex);
            }
            finally
            {
                _scheduler.Complete(item, more && !stoppingToken.IsCancellationRequested);
            }
        }
    }

    private async Task PollAsync(CancellationToken stoppingToken)
    {
        var now = _time.GetUtcNow();
        var due = new Dictionary<DispatchWork, DateTimeOffset>
        {
            [DispatchWork.Outbox] = now,
            [DispatchWork.IndexTasks] = now,
            [DispatchWork.JobChunks] = now,
        };
        var workspacesDue = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(Tick, _time);
        do
        {
            now = _time.GetUtcNow();
            if (now >= workspacesDue)
            {
                try
                {
                    _workspaces = await _maintenance.GetWorkspacesAsync(stoppingToken).ConfigureAwait(false);
                    workspacesDue = now + _options.WorkspaceRefreshInterval;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailure(null, ex);
                    workspacesDue = now + _options.OutboxPollIntervalWithoutListener;
                }
            }

            if (_catchUp)
            {
                // (Re)subscribed: commits made while nobody listened were not announced.
                _catchUp = false;
                due[DispatchWork.Outbox] = now;
            }

            foreach (var work in due.Keys.ToList().Where(work => now >= due[work]))
            {
                foreach (var workspaceId in _workspaces)
                {
                    _scheduler.Signal(new DispatchItem(workspaceId, work));
                }

                due[work] = now + work switch
                {
                    DispatchWork.Outbox => _listening ? _options.OutboxPollInterval : _options.OutboxPollIntervalWithoutListener,
                    DispatchWork.IndexTasks => _options.IndexTaskPollInterval,
                    _ => _options.JobChunkPollInterval,
                };
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SampleBacklogAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.BacklogSampleInterval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _backlog.SampleAsync(_workspaces, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailure(null, ex);
            }
        }
    }

    private async Task Guard(Func<Task> loop, string name)
    {
        try
        {
            await loop().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            LogLoopFailed(_logger, name, ex);
            throw;
        }
    }

    /// <summary>A database or broker outage fails every pass; one log line per interval is enough.</summary>
    private void LogFailure(DispatchItem? item, Exception exception)
    {
        var now = _time.GetUtcNow();
        lock (_errorGate)
        {
            if (now - _errorLoggedAt < ErrorLogInterval)
            {
                return;
            }

            _errorLoggedAt = now;
        }

        LogPassFailed(_logger, item?.Work.ToString() ?? "maintenance", item?.WorkspaceId, exception);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox dispatcher {Owner} started with {Concurrency} concurrent passes")]
    private static partial void LogStarted(ILogger logger, string owner, int concurrency);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox dispatcher {Owner} stopped")]
    private static partial void LogStopped(ILogger logger, string owner);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Dispatcher pass {Work} failed for workspace {WorkspaceId}; it is retried at the next poll (further failures are logged at most every 30 s)")]
    private static partial void LogPassFailed(ILogger logger, string work, Guid? workspaceId, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Dispatcher {Loop} loop stopped unexpectedly")]
    private static partial void LogLoopFailed(ILogger logger, string loop, Exception exception);
}
