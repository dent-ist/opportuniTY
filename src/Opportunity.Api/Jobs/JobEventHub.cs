using System.Threading.Channels;

using Microsoft.Extensions.Options;

using Opportunity.Application.Jobs;
using Opportunity.Contracts.Api;

namespace Opportunity.Api.Jobs;

/// <summary>Settings of the job-events stream (section <c>Jobs:Events</c>).</summary>
public sealed class JobEventOptions
{
    public const string SectionName = "Jobs:Events";

    /// <summary>How often one shared poller per workspace reads changed jobs (indexed on UpdatedAt).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A comment line keeps proxies from closing an idle stream; access is re-checked at the same rate.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A stream ends after this long and the browser's EventSource reconnects (with <c>Last-Event-ID</c>), so no
    /// connection outlives a session or a deployment by much.
    /// </summary>
    public TimeSpan MaxStreamDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Changes are read again for this long: a transaction stamps <c>updated_at</c> at its start but becomes visible at
    /// commit, so a change may appear "in the past". Repeats are filtered per job.
    /// </summary>
    public TimeSpan Overlap { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Fans job changes out to the job-events streams of this API instance (E06-T06, Q-35). One poller per workspace with
/// listeners reads the jobs changed since its last read from PostgreSQL, which owns job state; each listener receives the
/// jobs it may see (its own, or all with <c>Job.ViewAll</c>). Pollers stop when their last listener leaves.
/// </summary>
public sealed partial class JobEventHub(
    IJobOperationsStore store, IOptions<JobEventOptions> options, TimeProvider time, ILogger<JobEventHub> logger) : IDisposable
{
    private const int PageSize = 500;
    private const int ListenerCapacity = 256;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Feed> _feeds = [];
    private readonly JobEventOptions _options = options.Value;

    public JobEventListener Listen(Guid workspaceId, Guid userId, bool viewAll)
    {
        var listener = new JobEventListener(this, workspaceId, userId, viewAll,
            Channel.CreateBounded<JobEvent>(new BoundedChannelOptions(ListenerCapacity) { FullMode = BoundedChannelFullMode.DropOldest }));
        lock (_gate)
        {
            if (!_feeds.TryGetValue(workspaceId, out var feed))
            {
                feed = new Feed(workspaceId);
                _feeds[workspaceId] = feed;
                feed.Loop = Task.Run(async () =>
                {
                    using (feed.Stop)
                    {
                        await PollAsync(feed, feed.Stop.Token).ConfigureAwait(false);
                    }
                });
            }

            feed.Listeners.Add(listener);
        }

        return listener;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var feed in _feeds.Values)
            {
                feed.Stop.Cancel();
            }

            _feeds.Clear();
        }
    }

    internal void Remove(JobEventListener listener)
    {
        lock (_gate)
        {
            if (_feeds.TryGetValue(listener.WorkspaceId, out var feed) && feed.Listeners.Remove(listener) && feed.Listeners.Count == 0)
            {
                _feeds.Remove(listener.WorkspaceId);
                feed.Stop.Cancel();
            }
        }
    }

    private async Task PollAsync(Feed feed, CancellationToken cancellationToken)
    {
        var highWater = time.GetUtcNow() - _options.Overlap;
        var sent = new Dictionary<Guid, DateTimeOffset>();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                (DateTimeOffset, Guid)? after = null;
                var since = highWater - _options.Overlap;
                while (true)
                {
                    var page = await store.ListAsync(
                        new JobListQuery(feed.WorkspaceId) { UpdatedSince = since, After = after, Limit = PageSize }, cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var overview in page.Items)
                    {
                        var job = overview.Job;
                        if (job.UpdatedAt > highWater)
                        {
                            highWater = job.UpdatedAt;
                        }

                        if (sent.TryGetValue(job.JobId, out var last) && last >= job.UpdatedAt)
                        {
                            continue;
                        }

                        sent[job.JobId] = job.UpdatedAt;
                        Publish(feed, job.InitiatedBy, JobMapping.ToEvent(overview, page.AppliedWatermark));
                    }

                    if (page.Items.Count < PageSize)
                    {
                        break;
                    }

                    after = (page.Items[^1].Job.UpdatedAt, page.Items[^1].Job.JobId);
                }

                var forget = highWater - (2 * _options.Overlap);
                foreach (var stale in sent.Where(e => e.Value < forget).Select(e => e.Key).ToList())
                {
                    sent.Remove(stale);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed poll (database restart) must not end the feed; the next poll retries.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPollFailed(logger, feed.WorkspaceId, ex);
            }

            try
            {
                await Task.Delay(_options.PollInterval, time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Publish(Feed feed, Guid initiatedBy, JobEvent jobEvent)
    {
        JobEventListener[] listeners;
        lock (_gate)
        {
            listeners = [.. feed.Listeners];
        }

        foreach (var listener in listeners)
        {
            if (listener.ViewAll || listener.UserId == initiatedBy)
            {
                listener.Writer.TryWrite(jobEvent);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job events: reading changed jobs of workspace {WorkspaceId} failed; retrying")]
    private static partial void LogPollFailed(ILogger logger, Guid workspaceId, Exception exception);

    private sealed class Feed(Guid workspaceId)
    {
        public Guid WorkspaceId { get; } = workspaceId;

        public HashSet<JobEventListener> Listeners { get; } = [];

        public CancellationTokenSource Stop { get; } = new();

        public Task? Loop { get; set; }
    }
}

/// <summary>One stream's subscription: the events of the jobs this user may see. Dispose to leave.</summary>
public sealed class JobEventListener : IDisposable
{
    private readonly JobEventHub _hub;
    private readonly Channel<JobEvent> _channel;
    private volatile bool _viewAll;

    internal JobEventListener(JobEventHub hub, Guid workspaceId, Guid userId, bool viewAll, Channel<JobEvent> channel)
    {
        _hub = hub;
        _channel = channel;
        WorkspaceId = workspaceId;
        UserId = userId;
        _viewAll = viewAll;
    }

    public Guid WorkspaceId { get; }

    public Guid UserId { get; }

    /// <summary>Updated when the stream re-checks access (a revoked <c>Job.ViewAll</c> narrows the stream).</summary>
    public bool ViewAll
    {
        get => _viewAll;
        set => _viewAll = value;
    }

    public ChannelReader<JobEvent> Reader => _channel.Reader;

    internal ChannelWriter<JobEvent> Writer => _channel.Writer;

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _hub.Remove(this);
    }
}
