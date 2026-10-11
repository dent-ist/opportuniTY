using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

using Opportunity.Application.Authorization;
using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;

namespace Opportunity.Application.Audit;

/// <summary>Settings of the audit export runner (section <c>AuditExports</c>).</summary>
public sealed class AuditExportOptions
{
    public const string SectionName = "AuditExports";

    /// <summary>Identifies this process in export claims and chunk leases.</summary>
    public string WorkerId { get; set; } = $"audit-export:{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>Run the background runner in this host (tests drive <see cref="AuditExportRunner"/> themselves).</summary>
    public bool BackgroundEnabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan ChunkLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Events per chunk (one CSV part); ADR-010 §6 bound for this job type.</summary>
    public int EventsPerChunk { get; set; } = ChunkBounds.For(JobType.AuditExport).MaxItems;

    /// <summary>The most events one export may hold; a larger selection is refused with a request to narrow the filters.</summary>
    public long MaxEvents { get; set; } = 1_000_000;

    public int ChunksPerPass { get; set; } = 20;

    public void Validate()
    {
        if (EventsPerChunk < 1 || MaxEvents < 1 || ChunksPerPass < 1 || PollInterval <= TimeSpan.Zero || ClaimLease <= TimeSpan.Zero
            || ChunkLease <= TimeSpan.Zero || string.IsNullOrWhiteSpace(WorkerId))
        {
            throw new InvalidOperationException($"{SectionName} settings are out of range.");
        }
    }
}

/// <summary>Wakes the runner when an export is requested, so it does not wait for its next poll.</summary>
public sealed class AuditExportSignal
{
    private readonly Channel<(Guid WorkspaceId, Guid ExportId)> _channel =
        Channel.CreateBounded<(Guid, Guid)>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<(Guid WorkspaceId, Guid ExportId)> Reader => _channel.Reader;

    public void Nudge(Guid workspaceId, Guid exportId) => _channel.Writer.TryWrite((workspaceId, exportId));
}

/// <summary>
/// Runs audit exports (E14-T04) as <see cref="JobType.AuditExport"/> jobs, in the API host like search term reports: its
/// chunks are claimed straight from PostgreSQL (<see cref="IJobChunkRepository.ClaimNextAsync"/>, with leases and
/// fencing tokens) and never go through a work queue. Each step is restartable:
/// <list type="number">
/// <item><b>Prepare</b> (under an export claim): re-authorize the initiator (<c>Audit.Read</c>, and
/// <c>Audit.ReadSearchText</c> when the export includes search text), take the initiator's document visibility and plan
/// one chunk per time window of at most <see cref="AuditExportOptions.EventsPerChunk"/> matching events (refusing more
/// than <see cref="AuditExportOptions.MaxEvents"/>).</item>
/// <item><b>Chunks</b>: re-authorize the initiator and read the window's events with the initiator's document visibility
/// as it is now (events about walled and restricted documents left out; Q-15's re-check at execution), and store them
/// as one CSV part, replaced as a whole when a chunk is retried.</item>
/// <item><b>Finish</b>: hash the header and the parts in order and record rows, bytes and SHA-256; a failed or cancelled
/// job fails the export.</item>
/// </list>
/// Events written while the export runs (its own <c>Audit.Exported</c> included) are after its time bound only when they
/// occurred after the request; the bound is fixed when the export is requested.
/// </summary>
public sealed class AuditExportRunner(
    IAuditLogStore store,
    IJobRepository jobs,
    IJobChunkRepository chunks,
    IAuthorizationService authorization,
    AuditExportOptions options)
{
    public async Task<AuditExportStatus?> ProcessAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default)
    {
        if (await store.GetAsync(workspaceId, exportId, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return null;
        }

        if (export.Status is AuditExportStatus.Completed or AuditExportStatus.Failed
            || await jobs.GetAsync(workspaceId, export.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return export.Status;
        }

        switch (job.Status)
        {
            case JobStatus.Created or JobStatus.Preparing:
                await PrepareAsync(export, job, cancellationToken).ConfigureAwait(false);
                break;
            case JobStatus.Running:
                await RunChunksAsync(export, cancellationToken).ConfigureAwait(false);
                if (await jobs.GetAsync(workspaceId, export.JobId, cancellationToken).ConfigureAwait(false) is { } after && JobStateMachine.IsFinished(after.Status))
                {
                    await FinishAsync(export, after, cancellationToken).ConfigureAwait(false);
                }

                break;
            case JobStatus.Completed or JobStatus.CompletedWithErrors or JobStatus.Failed or JobStatus.Cancelled:
                await FinishAsync(export, job, cancellationToken).ConfigureAwait(false);
                break;
        }

        return (await store.GetAsync(workspaceId, exportId, cancellationToken).ConfigureAwait(false))?.Status;
    }

    /// <summary>Processes the export until it ends or <paramref name="maxPasses"/> passes did not finish it (tests).</summary>
    public async Task<AuditExportStatus?> RunToEndAsync(Guid workspaceId, Guid exportId, int maxPasses = 1_000, CancellationToken cancellationToken = default)
    {
        AuditExportStatus? status = null;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            status = await ProcessAsync(workspaceId, exportId, cancellationToken).ConfigureAwait(false);
            if (status is null or AuditExportStatus.Completed or AuditExportStatus.Failed)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        return status;
    }

    private async Task PrepareAsync(AuditExportRecord export, JobInfo job, CancellationToken cancellationToken)
    {
        var ws = export.WorkspaceId;
        if (!await store.TryClaimAsync(ws, export.ExportId, options.WorkerId, options.ClaimLease, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (job.Status == JobStatus.Created && !(await jobs.BeginPreparingAsync(ws, job.JobId, cancellationToken).ConfigureAwait(false)).Applied)
            {
                return;
            }

            var initiator = Initiator(export);
            if (await DenialAsync(initiator, export, cancellationToken).ConfigureAwait(false) is { } denial)
            {
                await FailRunAsync(export, denial, cancellationToken).ConfigureAwait(false);
                return;
            }

            var visibility = await AuditLogService.VisibilityAsync(authorization, initiator, ws, cancellationToken).ConfigureAwait(false);
            var windows = await store.PlanAsync(Query(export, visibility), options.EventsPerChunk, options.MaxEvents, cancellationToken).ConfigureAwait(false);
            if (windows is null)
            {
                await FailRunAsync(export, string.Create(CultureInfo.InvariantCulture,
                    $"More than {options.MaxEvents:N0} events match; narrow the filters (for example the date range)."), cancellationToken).ConfigureAwait(false);
                return;
            }

            await store.MarkRunningAsync(ws, export.ExportId, cancellationToken).ConfigureAwait(false);
            var plans = windows.Select(w => new ChunkPlan(ChunkMembership.TimeRange(w.FromMicros, w.ToMicros), w.Count)).ToList();
            await jobs.StartAsync(new JobStartRequest(ws, job.JobId, ChunkOperationKind.AuditExportChunk, plans), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await store.ReleaseClaimAsync(ws, export.ExportId, options.WorkerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunChunksAsync(AuditExportRecord export, CancellationToken cancellationToken)
    {
        var ws = export.WorkspaceId;
        for (var i = 0; i < options.ChunksPerPass; i++)
        {
            var claim = await chunks.ClaimNextAsync(ws, export.JobId, options.WorkerId, options.ChunkLease, cancellationToken).ConfigureAwait(false);
            if (!claim.Claimed)
            {
                return;
            }

            var chunk = claim.Chunk!;
            ChunkCompletion completion;
            try
            {
                completion = await ExecuteChunkAsync(export, chunk, cancellationToken).ConfigureAwait(false);
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

    private async Task<ChunkCompletion> ExecuteChunkAsync(AuditExportRecord export, ClaimedChunk chunk, CancellationToken cancellationToken)
    {
        var membership = chunk.Membership;
        if (chunk.JobType != JobType.AuditExport || membership.Kind != ChunkMembershipKind.TimeRange
            || membership.RangeFrom is not { } from || membership.RangeTo is not { } to)
        {
            throw new PermanentChunkException("InvalidChunk", "The chunk does not name a time window of the export.");
        }

        // Q-15's re-check at execution, applied to the reader of the audit trail: a wall placed since planning hides its
        // documents' events from the chunks that run after it.
        var initiator = Initiator(export);
        if (await DenialAsync(initiator, export, cancellationToken).ConfigureAwait(false) is { } denial)
        {
            throw new PermanentChunkException("ExecutorNoAccess", denial);
        }

        var visibility = await AuditLogService.VisibilityAsync(authorization, initiator, export.WorkspaceId, cancellationToken).ConfigureAwait(false);

        var text = new StringBuilder();
        var rows = await store.ReadWindowAsync(Query(export, visibility), new AuditExportWindow(from, to, chunk.ItemCount), stored =>
        {
            AuditExportCsv.AppendRow(text, stored, export.IncludeSearchText);
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);

        await FenceAsync(chunk, cancellationToken).ConfigureAwait(false);
        await store.WritePartAsync(export.WorkspaceId, export.ExportId, chunk.Sequence, checked((int)rows), Encoding.UTF8.GetBytes(text.ToString()), cancellationToken)
            .ConfigureAwait(false);
        return new ChunkCompletion { ItemsApplied = rows };
    }

    private async Task FinishAsync(AuditExportRecord export, JobInfo job, CancellationToken cancellationToken)
    {
        switch (job.Status)
        {
            case JobStatus.Failed:
                await FailRunAsync(export, job.StatusReason ?? "The export's job failed.", cancellationToken).ConfigureAwait(false);
                return;
            case JobStatus.Cancelled:
                await FailRunAsync(export, "The export's job was cancelled.", cancellationToken).ConfigureAwait(false);
                return;
            case JobStatus.CompletedWithErrors:
                await FailRunAsync(export, string.Create(CultureInfo.InvariantCulture,
                    $"{job.Counters.ChunksFailed} part(s) of the export failed, so the file would be incomplete; run the export again."), cancellationToken)
                    .ConfigureAwait(false);
                return;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var header = AuditExportCsv.Header(export.IncludeSearchText);
        hash.AppendData(header);
        long bytes = header.Length;
        var rows = await store.ReadPartsAsync(export.WorkspaceId, export.ExportId, part =>
        {
            hash.AppendData(part.Span);
            bytes += part.Length;
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        await store.CompleteAsync(export.WorkspaceId, export.ExportId, rows, bytes, Convert.ToHexStringLower(hash.GetHashAndReset()), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task FailRunAsync(AuditExportRecord export, string reason, CancellationToken cancellationToken)
    {
        if (await jobs.GetAsync(export.WorkspaceId, export.JobId, cancellationToken).ConfigureAwait(false) is { } job && !JobStateMachine.IsFinished(job.Status))
        {
            await jobs.FailAsync(export.WorkspaceId, export.JobId, reason, cancellationToken).ConfigureAwait(false);
        }

        await store.FailAsync(export.WorkspaceId, export.ExportId, reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> DenialAsync(SecurityPrincipal initiator, AuditExportRecord export, CancellationToken cancellationToken)
    {
        var read = await authorization.AuthorizeAsync(initiator, export.WorkspaceId, Permission.AuditRead, cancellationToken).ConfigureAwait(false);
        if (!read.IsAllowed || read.BreakGlass)
        {
            return "The person who requested the export may no longer read the audit trail.";
        }

        return export.IncludeSearchText
               && !(await authorization.AuthorizeAsync(initiator, export.WorkspaceId, Permission.AuditReadSearchText, cancellationToken).ConfigureAwait(false)).IsAllowed
            ? "The person who requested the export may no longer read search text in the audit trail."
            : null;
    }

    private async Task FenceAsync(ClaimedChunk chunk, CancellationToken cancellationToken)
    {
        var heartbeat = await chunks.HeartbeatAsync(chunk.Lease, options.ChunkLease, cancellationToken).ConfigureAwait(false);
        if (heartbeat.Fence != ChunkFence.Proceed)
        {
            throw new ChunkFencedException(heartbeat.Fence);
        }
    }

    private static AuditQuery Query(AuditExportRecord export, AuditDocumentVisibility visibility) =>
        export.Filters.ToQuery(export.WorkspaceId) with
        {
            To = export.OccurredBefore,
            DocumentVisibility = visibility,
            IncludeRestrictedDetails = export.IncludeSearchText,
        };

    private static SecurityPrincipal Initiator(AuditExportRecord export) =>
        new() { UserId = export.CreatedBy, DisplayName = export.CreatedByDisplay, Groups = export.CreatedByGroups };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
