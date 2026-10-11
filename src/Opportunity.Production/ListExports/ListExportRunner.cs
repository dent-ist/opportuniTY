using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Exports;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Application.ListExports;
using Opportunity.Application.Snapshots;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.Production.Exports;

namespace Opportunity.Production.ListExports;

/// <summary>Settings of the list export runner (section <c>ListExports</c>).</summary>
public sealed class ListExportOptions
{
    public const string SectionName = "ListExports";

    /// <summary>Identifies this process in export claims and chunk leases.</summary>
    public string WorkerId { get; set; } = $"list-export:{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>Run the background runner in this host (tests drive <see cref="ListExportRunner"/> themselves).</summary>
    public bool BackgroundEnabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan ChunkLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Documents per chunk (one CSV part); ADR-010 §6 bound for this job type.</summary>
    public int DocumentsPerChunk { get; set; } = ChunkBounds.For(JobType.ListExport).MaxItems;

    public int ChunksPerPass { get; set; } = 20;

    public void Validate()
    {
        if (DocumentsPerChunk is < 1 or > 5_000 || ChunksPerPass < 1 || PollInterval <= TimeSpan.Zero || ClaimLease <= TimeSpan.Zero
            || ChunkLease <= TimeSpan.Zero || string.IsNullOrWhiteSpace(WorkerId))
        {
            throw new InvalidOperationException($"{SectionName} settings are out of range.");
        }
    }
}

/// <summary>Wakes the runner when an export is requested, so it does not wait for its next poll.</summary>
public sealed class ListExportSignal
{
    private readonly Channel<(Guid WorkspaceId, Guid ExportId)> _channel =
        Channel.CreateBounded<(Guid, Guid)>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<(Guid WorkspaceId, Guid ExportId)> Reader => _channel.Reader;

    public void Nudge(Guid workspaceId, Guid exportId) => _channel.Writer.TryWrite((workspaceId, exportId));
}

/// <summary>
/// Runs document list exports (#181) as <see cref="JobType.ListExport"/> jobs in the API host, like audit exports and
/// search term reports: chunks are claimed straight from PostgreSQL (<see cref="IJobChunkRepository.ClaimNextAsync"/>,
/// leases and fencing tokens) and never go through a work queue. Each step is restartable:
/// <list type="number">
/// <item><b>Prepare</b> (under an export claim): wait until the frozen selection is Ready, re-check that the initiator
/// still holds <c>Document.ExportList</c>, point the job at the snapshot and plan one chunk per snapshot ordinal range.</item>
/// <item><b>Chunks</b>: rebuild the initiator from PostgreSQL, re-check <c>Document.ExportList</c> (a lost permission
/// fails the export), re-authorize every member against current security state (Q-15: restriction classes, walls,
/// deletion) — denied members are left out, counted and audited with the precise reason
/// (<c>Export.DocumentsExcluded</c>) — and store the remaining members' rows as one CSV part, with fields the
/// initiator may no longer see left empty.</item>
/// <item><b>Finish</b>: hash the header and the parts in order and record rows, exclusions, bytes and SHA-256; a failed
/// or cancelled job fails the export.</item>
/// </list>
/// </summary>
public sealed class ListExportRunner(
    IListExportStore store,
    IDocumentSetSnapshotStore snapshots,
    IExportStore documents,
    IJobRepository jobs,
    IJobChunkRepository chunks,
    IAuthorizationService authorization,
    IFieldAccessFilter fieldAccess,
    IAuditEventWriter audit,
    ListExportOptions options,
    TimeProvider time)
{
    /// <summary>Requester-facing reason of a Q-15 exclusion; the precise one is in audit only (ADR-015 D9.4).</summary>
    public const string AccessChanged = "AccessChanged";

    /// <summary>Actor of runner-side audit events, on behalf of the initiator.</summary>
    public const string RunnerActor = "service:list-export";

    private const int ExcludedPerAuditEvent = 100;

    public async Task<ListExportStatus?> ProcessAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default)
    {
        if (await store.GetAsync(workspaceId, exportId, cancellationToken).ConfigureAwait(false) is not { } export)
        {
            return null;
        }

        if (export.Status is ListExportStatus.Completed or ListExportStatus.Failed
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
    public async Task<ListExportStatus?> RunToEndAsync(Guid workspaceId, Guid exportId, int maxPasses = 1_000, CancellationToken cancellationToken = default)
    {
        ListExportStatus? status = null;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            status = await ProcessAsync(workspaceId, exportId, cancellationToken).ConfigureAwait(false);
            if (status is null or ListExportStatus.Completed or ListExportStatus.Failed)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), time, cancellationToken).ConfigureAwait(false);
        }

        return status;
    }

    private async Task PrepareAsync(ListExportRecord export, JobInfo job, CancellationToken cancellationToken)
    {
        var ws = export.WorkspaceId;
        if (!await store.TryClaimAsync(ws, export.ExportId, options.WorkerId, options.ClaimLease, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var snapshot = await snapshots.GetAsync(ws, export.SnapshotId, cancellationToken).ConfigureAwait(false);
            if (snapshot?.Status == SnapshotStatus.Materializing)
            {
                return; // frozen in the background (too large for the request); the next pass continues
            }

            if (job.Status == JobStatus.Created && !(await jobs.BeginPreparingAsync(ws, job.JobId, cancellationToken).ConfigureAwait(false)).Applied)
            {
                return;
            }

            if (snapshot is not { Status: SnapshotStatus.Ready })
            {
                await FailRunAsync(export, snapshot?.StatusReason is { Length: > 0 } reason
                    ? "The selection could not be frozen: " + reason
                    : "The selection could not be frozen or has expired; export the list again.", cancellationToken).ConfigureAwait(false);
                return;
            }

            var initiator = await InitiatorAsync(export, cancellationToken).ConfigureAwait(false);
            if (await DenialAsync(initiator, ws, cancellationToken).ConfigureAwait(false) is { } denial)
            {
                await FailRunAsync(export, denial, cancellationToken).ConfigureAwait(false);
                return;
            }

            await store.MarkRunningAsync(ws, export.ExportId, cancellationToken).ConfigureAwait(false);
            var plans = DocumentSetSnapshotService.PlanChunks(snapshot, options.DocumentsPerChunk)
                .Select(m => new ChunkPlan(m, checked((int)m.KnownCount!.Value)))
                .ToList();
            await jobs.StartAsync(new JobStartRequest(ws, job.JobId, ChunkOperationKind.ListExportChunk, plans), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await store.ReleaseClaimAsync(ws, export.ExportId, options.WorkerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RunChunksAsync(ListExportRecord export, CancellationToken cancellationToken)
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

    private async Task<ChunkCompletion> ExecuteChunkAsync(ListExportRecord export, ClaimedChunk chunk, CancellationToken cancellationToken)
    {
        var membership = chunk.Membership;
        var ws = export.WorkspaceId;
        if (chunk.JobType != JobType.ListExport || membership.Kind != ChunkMembershipKind.SnapshotRange
            || membership.SnapshotId != export.SnapshotId || membership.RangeFrom is not { } from || membership.RangeTo is not { } to)
        {
            throw new PermanentChunkException("InvalidChunk", "The chunk does not name a range of the export's frozen selection.");
        }

        // ADR-015 D9.4: the initiator as PostgreSQL knows them now, who must still hold the job's permission.
        var initiator = await InitiatorAsync(export, cancellationToken).ConfigureAwait(false);
        if (await DenialAsync(initiator, ws, cancellationToken).ConfigureAwait(false) is { } denial)
        {
            throw new PermanentChunkException("ExecutorNoAccess", denial);
        }

        var members = await snapshots.ReadMembersAsync(ws, export.SnapshotId, from, to, cancellationToken).ConfigureAwait(false);
        if (members.Count != membership.KnownCount)
        {
            throw new PermanentChunkException("SnapshotUnavailable", "The frozen selection is no longer readable for this range (expired or not Ready).");
        }

        // Q-15: every member re-authorized against current PostgreSQL security state.
        var ids = members.Select(m => m.DocumentId).ToList();
        var decisions = ids.Count == 0
            ? new Dictionary<Guid, AuthorizationDecision>()
            : await authorization.AuthorizeManyAsync(initiator, ws, Permission.DocumentExportList, ids, DenialAudit.Caller, cancellationToken).ConfigureAwait(false);
        var permitted = ids.Where(id => decisions.TryGetValue(id, out var d) && d.IsAllowed).ToList();
        var source = await documents.ReadDocumentsAsync(ws, permitted, cancellationToken).ConfigureAwait(false);
        var restricted = await fieldAccess.RestrictedFieldIdsAsync(ws, initiator, source.Catalog, cancellationToken).ConfigureAwait(false);

        var columns = export.Columns.Select(c => new ExportColumn(ExportColumnKind.Field, c.FieldId, c.Header)).ToList();
        var values = new ExportValues(source.Catalog, ';', restricted);
        var text = new StringBuilder();
        var rows = 0;
        var excluded = new List<(Guid DocumentId, string Reason)>();
        foreach (var member in members)
        {
            if (!decisions.TryGetValue(member.DocumentId, out var decision) || !decision.IsAllowed)
            {
                excluded.Add((member.DocumentId, decision.Reason ?? AuthorizationReasons.DocumentNotFound));
            }
            else if (source.Documents.TryGetValue(member.DocumentId, out var document))
            {
                ListExportCsv.AppendRow(text, values.Row(columns, document, null, null).Values);
                rows++;
            }
            else
            {
                excluded.Add((member.DocumentId, AuthorizationReasons.DocumentNotFound));
            }
        }

        await FenceAsync(chunk, cancellationToken).ConfigureAwait(false);
        await store.WritePartAsync(ws, export.ExportId, chunk.Sequence, rows, excluded.Count, Encoding.UTF8.GetBytes(text.ToString()), cancellationToken)
            .ConfigureAwait(false);
        foreach (var part in excluded.Chunk(ExcludedPerAuditEvent))
        {
            await audit.WriteAsync(ExclusionEvent(export, chunk, part), cancellationToken).ConfigureAwait(false);
        }

        return new ChunkCompletion
        {
            ItemsApplied = rows,
            ItemResults = [.. excluded.Select(e => new JobItemResult(JobItemResultKind.ExcludedNoAccess, e.DocumentId, null, null, AccessChanged))],
        };
    }

    private async Task FinishAsync(ListExportRecord export, JobInfo job, CancellationToken cancellationToken)
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
                    $"{job.Counters.ChunksFailed} part(s) of the export failed, so the file would be incomplete; export the list again."), cancellationToken)
                    .ConfigureAwait(false);
                return;
        }

        if (export.Status == ListExportStatus.Queued)
        {
            // An empty selection plans no chunk: the job completes as it starts.
            await store.MarkRunningAsync(export.WorkspaceId, export.ExportId, cancellationToken).ConfigureAwait(false);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var header = ListExportCsv.Header(export.Columns);
        hash.AppendData(header);
        long bytes = header.Length;
        var (rows, excluded) = await store.ReadPartsAsync(export.WorkspaceId, export.ExportId, part =>
        {
            hash.AppendData(part.Span);
            bytes += part.Length;
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        await store.CompleteAsync(export.WorkspaceId, export.ExportId, rows, excluded, bytes, Convert.ToHexStringLower(hash.GetHashAndReset()), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task FailRunAsync(ListExportRecord export, string reason, CancellationToken cancellationToken)
    {
        if (await jobs.GetAsync(export.WorkspaceId, export.JobId, cancellationToken).ConfigureAwait(false) is { } job && !JobStateMachine.IsFinished(job.Status))
        {
            await jobs.FailAsync(export.WorkspaceId, export.JobId, reason, cancellationToken).ConfigureAwait(false);
        }

        await store.FailAsync(export.WorkspaceId, export.ExportId, reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> DenialAsync(SecurityPrincipal initiator, Guid workspaceId, CancellationToken cancellationToken)
    {
        var decision = await authorization.AuthorizeAsync(initiator, workspaceId, Permission.DocumentExportList, cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed && !decision.BreakGlass ? null : "The person who requested the export may no longer export the document list.";
    }

    private async Task<SecurityPrincipal> InitiatorAsync(ListExportRecord export, CancellationToken cancellationToken)
    {
        var current = await documents.ReadInitiatorAsync(export.CreatedBy, cancellationToken).ConfigureAwait(false);
        return new SecurityPrincipal
        {
            UserId = export.CreatedBy,
            DisplayName = current?.DisplayName ?? export.CreatedByDisplay,
            Groups = current?.Groups ?? export.CreatedByGroups,
        };
    }

    private async Task FenceAsync(ClaimedChunk chunk, CancellationToken cancellationToken)
    {
        var heartbeat = await chunks.HeartbeatAsync(chunk.Lease, options.ChunkLease, cancellationToken).ConfigureAwait(false);
        if (heartbeat.Fence != ChunkFence.Proceed)
        {
            throw new ChunkFencedException(heartbeat.Fence);
        }
    }

    private AuditEvent ExclusionEvent(ListExportRecord export, ClaimedChunk chunk, (Guid DocumentId, string Reason)[] excluded) => new()
    {
        WorkspaceId = export.WorkspaceId,
        OccurredAt = time.GetUtcNow(),
        Category = AuditTaxonomy.Export.Category,
        Action = AuditTaxonomy.Export.DocumentsExcluded,
        ActorType = AuditActorType.Service,
        ActorId = RunnerActor,
        ActorDisplay = "List export",
        OnBehalfOf = export.CreatedBy,
        ResourceType = AuditTaxonomy.Export.ListResourceType,
        ResourceId = export.ExportId.ToString(),
        Outcome = AuditOutcome.Success,
        JobId = export.JobId,
        ChunkSequence = chunk.Sequence,
        SnapshotId = export.SnapshotId,
        Details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ExportId"] = export.ExportId.ToString(),
            ["Count"] = excluded.Length.ToString(CultureInfo.InvariantCulture),
            ["Documents"] = string.Join(',', excluded.Select(e => e.DocumentId.ToString("N") + ":" + e.Reason)),
        },
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
