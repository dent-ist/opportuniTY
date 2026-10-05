using System.Globalization;
using System.Text;
using System.Threading.Channels;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Jobs;
using Opportunity.Application.Snapshots;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchTermReports;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.Application.Search.TermReports;

/// <summary>Settings of the search term report runner (section <c>SearchTermReports</c>).</summary>
public sealed class SearchTermReportOptions
{
    public const string SectionName = "SearchTermReports";

    /// <summary>Identifies this process in report claims and chunk leases.</summary>
    public string WorkerId { get; set; } = $"str:{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>Run the background runner in this host (tests drive <see cref="SearchTermReportRunner"/> themselves).</summary>
    public bool BackgroundEnabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a runner holds a report while it prepares or finishes a run.</summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Chunk lease; renewed before each term.</summary>
    public TimeSpan ChunkLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Snapshot members per chunk (ADR-010 §6 bound for this job type).</summary>
    public int DocumentsPerChunk { get; set; } = ChunkBounds.For(JobType.SearchTermReport).MaxItems;

    /// <summary>Chunks one pass runs for one report before looking at the others.</summary>
    public int ChunksPerPass { get; set; } = 20;

    public void Validate()
    {
        if (DocumentsPerChunk is < 1 or > ISearchService.MaxMatchDocuments || ChunksPerPass < 1 || PollInterval <= TimeSpan.Zero
            || ClaimLease <= TimeSpan.Zero || ChunkLease <= TimeSpan.Zero || string.IsNullOrWhiteSpace(WorkerId))
        {
            throw new InvalidOperationException($"{SectionName} settings are out of range.");
        }
    }
}

/// <summary>Wakes the runner when a report is created or rerun, so it does not wait for its next poll.</summary>
public sealed class SearchTermReportSignal
{
    private readonly Channel<(Guid WorkspaceId, Guid ReportId)> _channel =
        Channel.CreateBounded<(Guid, Guid)>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<(Guid WorkspaceId, Guid ReportId)> Reader => _channel.Reader;

    public void Nudge(Guid workspaceId, Guid reportId) => _channel.Writer.TryWrite((workspaceId, reportId));
}

/// <summary>
/// Runs search term reports (E07-T10) as <see cref="JobType.SearchTermReport"/> jobs. The runner lives in the API host
/// because it needs the search service and the PDP (like snapshot materialization); its chunks are claimed straight from
/// PostgreSQL (<see cref="IJobChunkRepository.ClaimNextAsync"/>, leases and fencing tokens as for every job), so they
/// never go through a work queue. Each step is restartable:
/// <list type="number">
/// <item><b>Prepare</b> (under a report claim): freeze the scope into a Report snapshot as the executor through
/// <see cref="DocumentSetSnapshotService"/> (the rule always materializes a saved report, ADR-002 §4), or reuse the
/// report's snapshot on a rerun; read the refresh-aware watermark; plan one chunk per snapshot ordinal range.</item>
/// <item><b>Chunks</b>: re-authorize the members for the executor (<c>Document.View</c>; restricted and walled members
/// are excluded from every count), match every valid term against the visible members
/// (<see cref="ISearchService.MatchAsync"/>), and store the visible members with their family and the hits. A term
/// that fails to plan or run records its error once and counts nothing.</item>
/// <item><b>Finish</b> (under the claim): compute the counts in PostgreSQL, note whether the index stayed current, and
/// write <c>Search.TermReportGenerated</c> with the report.</item>
/// </list>
/// </summary>
public sealed class SearchTermReportRunner(
    ISearchTermReportStore store,
    IJobRepository jobs,
    IJobChunkRepository chunks,
    DocumentSetSnapshotService snapshotService,
    IDocumentSetSnapshotStore snapshots,
    ISearchService search,
    IAuthorizationService authorization,
    ISearchFreshnessReader freshness,
    SearchTermReportOptions options,
    TimeProvider time)
{
    /// <summary>The generic, requester-facing reason of a member the executor could not see.</summary>
    public const string AccessChanged = "AccessChanged";

    /// <summary>Advances one report as far as it can now; returns its status afterwards (null when gone).</summary>
    public async Task<SearchTermReportStatus?> ProcessAsync(Guid workspaceId, Guid reportId, CancellationToken cancellationToken = default)
    {
        if (await store.GetAsync(workspaceId, reportId, cancellationToken).ConfigureAwait(false) is not { } report)
        {
            return null;
        }

        if (report.Status is SearchTermReportStatus.Completed or SearchTermReportStatus.Failed
            || await jobs.GetAsync(workspaceId, report.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return report.Status;
        }

        switch (job.Status)
        {
            case JobStatus.Created or JobStatus.Preparing:
                await PrepareAsync(report, job, cancellationToken).ConfigureAwait(false);
                break;
            case JobStatus.Running:
                await RunChunksAsync(report, cancellationToken).ConfigureAwait(false);
                if (await jobs.GetAsync(workspaceId, report.JobId, cancellationToken).ConfigureAwait(false) is { } after && JobStateMachine.IsFinished(after.Status))
                {
                    await FinishAsync(report, after, cancellationToken).ConfigureAwait(false);
                }

                break;
            case JobStatus.Completed or JobStatus.CompletedWithErrors or JobStatus.Failed or JobStatus.Cancelled:
                await FinishAsync(report, job, cancellationToken).ConfigureAwait(false);
                break;
        }

        return (await store.GetAsync(workspaceId, reportId, cancellationToken).ConfigureAwait(false))?.Status;
    }

    /// <summary>Processes the report until its run ends or <paramref name="maxPasses"/> passes did not finish it (tests, CLI).</summary>
    public async Task<SearchTermReportStatus?> RunToEndAsync(Guid workspaceId, Guid reportId, int maxPasses = 1_000, CancellationToken cancellationToken = default)
    {
        SearchTermReportStatus? status = null;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            status = await ProcessAsync(workspaceId, reportId, cancellationToken).ConfigureAwait(false);
            if (status is null or SearchTermReportStatus.Completed or SearchTermReportStatus.Failed)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), time, cancellationToken).ConfigureAwait(false);
        }

        return status;
    }

    private async Task PrepareAsync(SearchTermReportRecord report, JobInfo job, CancellationToken cancellationToken)
    {
        var ws = report.WorkspaceId;
        if (!await store.TryClaimAsync(ws, report.ReportId, options.WorkerId, options.ClaimLease, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (job.Status == JobStatus.Created && !(await jobs.BeginPreparingAsync(ws, job.JobId, cancellationToken).ConfigureAwait(false)).Applied)
            {
                return;
            }

            var snapshotId = report.SnapshotId;
            if (snapshotId is null)
            {
                var outcome = await snapshotService.CreateAsync(Caller(report), SnapshotRequest(report), cancellationToken).ConfigureAwait(false);
                if (outcome.Snapshot is null || outcome.Status is not (SnapshotCreateStatus.Ready or SnapshotCreateStatus.Accepted))
                {
                    await FailRunAsync(report, ScopeFailure(outcome), cancellationToken).ConfigureAwait(false);
                    return;
                }

                snapshotId = outcome.Snapshot.SnapshotId;
                await store.SetSnapshotAsync(ws, report.ReportId, job.JobId, snapshotId.Value, cancellationToken).ConfigureAwait(false);
            }

            var snapshot = await snapshots.GetAsync(ws, snapshotId.Value, cancellationToken).ConfigureAwait(false);
            if (snapshot?.Status == SnapshotStatus.Materializing)
            {
                return; // frozen in the background (too large for one request); the next pass continues
            }

            if (snapshot is not { Status: SnapshotStatus.Ready })
            {
                await FailRunAsync(report, "The report's snapshot could not be frozen or has expired; create the report again.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (report.SnapshotId is null)
            {
                await store.SetSnapshotAsync(ws, report.ReportId, job.JobId, snapshot.SnapshotId, cancellationToken).ConfigureAwait(false);
            }

            var reading = await freshness.ReadAsync(ws, cancellationToken).ConfigureAwait(false);
            await store.MarkRunningAsync(ws, report.ReportId, job.JobId, reading.IndexedThroughGeneration, reading.IsCurrent, time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            var plans = DocumentSetSnapshotService.PlanChunks(snapshot, options.DocumentsPerChunk)
                .Select(m => new ChunkPlan(m, checked((int)m.KnownCount!.Value)))
                .ToList();
            await jobs.StartAsync(new JobStartRequest(ws, job.JobId, ChunkOperationKind.SearchTermReportChunk, plans), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await store.ReleaseClaimAsync(ws, report.ReportId, options.WorkerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunChunksAsync(SearchTermReportRecord report, CancellationToken cancellationToken)
    {
        var ws = report.WorkspaceId;
        var caller = Caller(report);
        var terms = (await store.GetTermsAsync(ws, report.ReportId, cancellationToken).ConfigureAwait(false)).Where(t => t.Error is null).ToList();
        for (var i = 0; i < options.ChunksPerPass; i++)
        {
            var claim = await chunks.ClaimNextAsync(ws, report.JobId, options.WorkerId, options.ChunkLease, cancellationToken).ConfigureAwait(false);
            if (!claim.Claimed)
            {
                return;
            }

            var chunk = claim.Chunk!;
            ChunkCompletion completion;
            try
            {
                completion = await ExecuteChunkAsync(report, caller, terms, chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (ChunkFencedException)
            {
                await chunks.ReleaseAsync(chunk.Lease, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await chunks.ReleaseAsync(chunk.Lease, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var error = ex is PermanentChunkException permanent
                    ? ChunkError.Permanent(permanent.Code, Truncate(permanent.Message, ChunkError.MaxMessageLength))
                    : ChunkError.Transient(ex.GetType().Name, Truncate(ex.Message, ChunkError.MaxMessageLength));
                await chunks.FailAsync(chunk.Lease, error, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            await chunks.CompleteAsync(chunk.Lease, completion, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ChunkCompletion> ExecuteChunkAsync(
        SearchTermReportRecord report, SearchCaller caller, List<SearchTermRecord> terms, ClaimedChunk chunk, CancellationToken cancellationToken)
    {
        var membership = chunk.Membership;
        if (chunk.JobType != JobType.SearchTermReport || membership.Kind != ChunkMembershipKind.SnapshotRange
            || membership.SnapshotId is not { } snapshotId || snapshotId != report.SnapshotId || membership.RangeFrom is not { } from || membership.RangeTo is not { } to)
        {
            throw new PermanentChunkException("InvalidChunk", "The chunk does not name a range of the report's snapshot.");
        }

        var ws = report.WorkspaceId;
        var members = await snapshots.ReadMembersAsync(ws, snapshotId, from, to, cancellationToken).ConfigureAwait(false);
        var ids = members.Select(m => m.DocumentId).ToList();
        IReadOnlyDictionary<Guid, AuthorizationDecision> decisions = ids.Count == 0
            ? new Dictionary<Guid, AuthorizationDecision>()
            : await authorization.AuthorizeManyAsync(caller.Principal, ws, Permission.DocumentView, ids, DenialAudit.Caller, cancellationToken).ConfigureAwait(false);
        var visible = new List<Guid>(ids.Count);
        var excluded = new List<JobItemResult>();
        foreach (var id in ids)
        {
            if (decisions.TryGetValue(id, out var decision) && decision.IsAllowed)
            {
                visible.Add(id);
            }
            else
            {
                excluded.Add(new JobItemResult(JobItemResultKind.ExcludedNoAccess, id, null, null, AccessChanged));
            }
        }

        var hits = new Dictionary<int, IReadOnlyList<Guid>>();
        foreach (var term in terms)
        {
            await FenceAsync(chunk, cancellationToken).ConfigureAwait(false);
            var matched = new List<Guid>();
            foreach (var batch in visible.Chunk(ISearchService.MaxMatchDocuments))
            {
                var outcome = await search.MatchAsync(caller, term.Expression, batch, cancellationToken).ConfigureAwait(false);
                switch (outcome.Status)
                {
                    case SearchSelectionStatus.Ok:
                        matched.AddRange(outcome.Matches);
                        break;
                    case SearchSelectionStatus.InvalidQuery:
                        var first = outcome.QueryErrors.Count > 0 ? outcome.QueryErrors[0] : null;
                        await store.SetTermErrorAsync(ws, report.ReportId, term.TermNo, first is null
                            ? new SearchTermError("INVALID_QUERY", "The term cannot be run.", null)
                            : new SearchTermError(first.Code, first.Message, first.Span.Start), cancellationToken).ConfigureAwait(false);
                        matched.Clear();
                        break;
                    default:
                        throw new PermanentChunkException("ExecutorNoAccess", "The report's executor can no longer search this workspace.");
                }

                if (outcome.Status != SearchSelectionStatus.Ok)
                {
                    break;
                }
            }

            if (matched.Count > 0)
            {
                hits[term.TermNo] = matched;
            }
        }

        await FenceAsync(chunk, cancellationToken).ConfigureAwait(false);
        await store.WriteChunkAsync(ws, report.ReportId, report.JobId, new SearchTermChunkResult(visible, hits), cancellationToken).ConfigureAwait(false);
        return new ChunkCompletion { ItemsApplied = visible.Count, ItemResults = excluded };
    }

    private async Task FenceAsync(ClaimedChunk chunk, CancellationToken cancellationToken)
    {
        var heartbeat = await chunks.HeartbeatAsync(chunk.Lease, options.ChunkLease, cancellationToken).ConfigureAwait(false);
        if (heartbeat.Fence != ChunkFence.Proceed)
        {
            throw new ChunkFencedException(heartbeat.Fence);
        }
    }

    private async Task FinishAsync(SearchTermReportRecord report, JobInfo job, CancellationToken cancellationToken)
    {
        var ws = report.WorkspaceId;
        switch (job.Status)
        {
            case JobStatus.Failed:
                await FailRunAsync(report, job.StatusReason ?? "The report's job failed.", cancellationToken).ConfigureAwait(false);
                return;
            case JobStatus.Cancelled:
                await FailRunAsync(report, "The report's job was cancelled.", cancellationToken).ConfigureAwait(false);
                return;
            case JobStatus.CompletedWithErrors when job.Counters.ChunksFailed > 0:
                await FailRunAsync(report, string.Create(CultureInfo.InvariantCulture,
                    $"{job.Counters.ChunksFailed} chunk(s) failed, so the counts would be incomplete; rerun the report."), cancellationToken).ConfigureAwait(false);
                return;
        }

        if (!await store.TryClaimAsync(ws, report.ReportId, options.WorkerId, options.ClaimLease, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // The index was current for the whole run only if it was current at the start and nothing was committed since.
            var reading = await freshness.ReadAsync(ws, cancellationToken).ConfigureAwait(false);
            var currentAtEnd = reading.IsCurrent && report.SearchGeneration is { } start && reading.LatestGeneration <= start;
            var terms = await store.GetTermsAsync(ws, report.ReportId, cancellationToken).ConfigureAwait(false);
            if (await store.CompleteAsync(ws, report.ReportId, job.JobId, currentAtEnd, completed => Generated(completed, terms), cancellationToken)
                    .ConfigureAwait(false))
            {
            }
        }
        finally
        {
            await store.ReleaseClaimAsync(ws, report.ReportId, options.WorkerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task FailRunAsync(SearchTermReportRecord report, string reason, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(report.WorkspaceId, report.JobId, cancellationToken).ConfigureAwait(false);
        if (job is { } j && !JobStateMachine.IsFinished(j.Status))
        {
            await jobs.FailAsync(report.WorkspaceId, report.JobId, reason, cancellationToken).ConfigureAwait(false);
        }

        await store.FailAsync(report.WorkspaceId, report.ReportId, report.JobId, reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>Search.TermReportGenerated</c>: report, snapshot, generation and counts; the term expressions are search text (Q-16).</summary>
    private AuditEvent Generated(SearchTermReportRecord report, IReadOnlyList<SearchTermRecord> terms)
    {
        var restricted = new StringBuilder();
        foreach (var term in terms)
        {
            var line = term.Name + "\t" + term.Expression + "\n";
            if (Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(restricted.ToString()) > AuditEventRules.MaxRestrictedDetailsBytes / 2)
            {
                break;
            }

            restricted.Append(line);
        }

        return new AuditEvent
        {
            WorkspaceId = report.WorkspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.SearchTermReport.Category,
            Action = AuditTaxonomy.SearchTermReport.Generated,
            ActorType = AuditActorType.User,
            ActorId = report.ExecutedBy.ToString(),
            ActorDisplay = report.ExecutedByDisplay,
            ResourceType = AuditTaxonomy.SearchTermReport.ResourceType,
            ResourceId = report.ReportId.ToString(),
            Outcome = AuditOutcome.Success,
            JobId = report.JobId,
            SnapshotId = report.SnapshotId,
            SearchGeneration = report.SearchGeneration,
            Details = new Dictionary<string, string?>
            {
                ["reportId"] = report.ReportId.ToString(),
                ["scope"] = report.ScopeKind.ToString(),
                ["scopeId"] = report.ScopeId?.ToString(),
                ["run"] = Invariant(report.RunCount),
                ["terms"] = Invariant(terms.Count),
                ["termErrors"] = Invariant(terms.Count(t => t.Error is not null)),
                ["documentsInScope"] = Invariant(report.DocumentsInScope ?? 0),
                ["documentsWithHits"] = Invariant(report.DocumentsWithHits ?? 0),
                ["documentsWithHitsIncludingFamily"] = Invariant(report.DocumentsWithHitsIncludingFamily ?? 0),
                ["documentsWithoutHits"] = Invariant(report.DocumentsWithoutHits ?? 0),
                ["excludedNoAccess"] = Invariant(report.ExcludedNoAccess ?? 0),
                ["indexCurrent"] = report.IndexCurrent == true ? "true" : "false",
            }.Where(d => d.Value is not null).ToDictionary(),
            RestrictedDetails = new Dictionary<string, string?> { ["terms"] = restricted.ToString() },
        };
    }

    private static SnapshotCreateRequest SnapshotRequest(SearchTermReportRecord report)
    {
        var name = Truncate("Search Terms Report: " + report.Name, SnapshotRules.MaxNameLength).Trim();
        var key = "str:" + report.JobId.ToString("N");
        return report.ScopeKind switch
        {
            SearchTermReportScopeKind.SavedSearch => new SnapshotCreateRequest(SnapshotPurpose.Report, name, Query: "savedsearch:" + report.ScopeId!.Value,
                ClientIdempotencyKey: key),
            SearchTermReportScopeKind.Snapshot => new SnapshotCreateRequest(SnapshotPurpose.Report, name, SourceSnapshotId: report.ScopeId, ClientIdempotencyKey: key),
            _ => new SnapshotCreateRequest(SnapshotPurpose.Report, name, Query: string.Empty, ClientIdempotencyKey: key),
        };
    }

    private static string ScopeFailure(SnapshotCreateOutcome outcome) => outcome.Status switch
    {
        SnapshotCreateStatus.NotFound => "The report's scope no longer exists or is no longer visible to its executor.",
        SnapshotCreateStatus.Forbidden => "The report's executor may no longer search this workspace.",
        SnapshotCreateStatus.TooLarge => "The scope is larger than a snapshot may be; narrow it.",
        SnapshotCreateStatus.InvalidQuery => "The scope's saved search can no longer be run: " + string.Join("; ", outcome.QueryErrors.Select(e => e.Message)),
        _ => "The scope could not be frozen; rerun the report.",
    };

    private static SearchCaller Caller(SearchTermReportRecord report) => new(
        new SecurityPrincipal { UserId = report.ExecutedBy, DisplayName = report.ExecutedByDisplay, Groups = report.ExecutedByGroups },
        report.WorkspaceId,
        null);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The hit set of a completed report's term for <c>POST …/searches {searchTermReportId, termId}</c>: the same visibility
/// as the report (its creator or <c>Job.ViewAll</c>); the search service then filters the hits for the caller.
/// </summary>
public sealed class SearchTermHitSource(ISearchTermReportStore store, IAuthorizationService authorization) : ISearchTermHitSource
{
    public async Task<SearchTermHitSet?> GetAsync(SearchCaller caller, Guid reportId, Guid termId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var ws = caller.WorkspaceId;
        if (await store.GetAsync(ws, reportId, cancellationToken).ConfigureAwait(false) is not { } report
            || (report.CreatedBy != caller.Principal.UserId
                && !(await authorization.AuthorizeAsync(caller.Principal, ws, Permission.JobViewAll, cancellationToken).ConfigureAwait(false)).IsAllowed)
            || (await store.GetTermsAsync(ws, reportId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(t => t.TermId == termId) is not { } term)
        {
            return null;
        }

        if (report.Status != SearchTermReportStatus.Completed)
        {
            return new SearchTermHitSet(SearchTermHitSetStatus.NotCompleted, term.Expression, []);
        }

        if (term.Error is not null)
        {
            return new SearchTermHitSet(SearchTermHitSetStatus.TermError, term.Expression, []);
        }

        var hits = await store.GetHitsAsync(ws, reportId, term.TermNo, SearchTermHitSet.MaxDocuments + 1, cancellationToken).ConfigureAwait(false);
        return hits.Count > SearchTermHitSet.MaxDocuments
            ? new SearchTermHitSet(SearchTermHitSetStatus.TooLarge, term.Expression, [])
            : new SearchTermHitSet(SearchTermHitSetStatus.Ok, term.Expression, hits);
    }
}
