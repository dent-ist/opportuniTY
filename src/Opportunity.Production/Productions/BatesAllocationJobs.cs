using System.Globalization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif
using Opportunity.Application.Jobs;
using Opportunity.Application.Productions;
using Opportunity.Core.Jobs;
using Opportunity.Core.Productions;
using Opportunity.Core.Security;

namespace Opportunity.Production.Productions;

/// <summary>Production worker settings (ADR-010 §6 initial values).</summary>
public sealed class ProductionJobOptions
{
    /// <summary>Documents per allocation chunk (ADR-010 §6: 100 for productions); chunks close only between families.</summary>
    public int DocumentsPerChunk { get; init; } = ChunkBounds.For(JobType.Production).MaxItems;

    /// <summary>Numbers (pages at page level) per allocation chunk (ADR-010 §6: ≤ 2,000 pages).</summary>
    public int NumbersPerChunk { get; init; } = 2_000;

    public TimeSpan ClaimLease { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public string WorkerId { get; init; } = string.Create(
        CultureInfo.InvariantCulture, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");
}

public enum BatesAllocationStep
{
    None,

    /// <summary>The production order and range were planned and reserved, and the job started.</summary>
    Started,

    /// <summary>Every chunk committed; the integrity check ran and the allocation is Allocated (or Failed when it did not pass).</summary>
    Completed,

    /// <summary>The allocation failed (overlap, overflow, failed chunks, cancelled job) and its range was released.</summary>
    Ended,
}

/// <summary>
/// Plans and completes Bates allocations in the production worker (E12-T03). Planning runs the store's planning
/// transaction (production order, units, offsets, chunks; overlap check and reservation under the prefix lock) and starts
/// the job with one <c>SnapshotRange</c> chunk per planned range of production sequence numbers. Once every chunk
/// committed, completion runs the integrity check, hashes the assignment for the manifest and marks the allocation
/// Allocated with <c>Production.BatesAllocated</c>; a failed check (or an overlap found while planning) is audited as
/// <c>Integrity.BatesConflict</c> and releases the range. Both steps are restartable under a claim.
/// </summary>
public sealed partial class BatesAllocationCoordinator(
    IProductionStore productions, IJobRepository jobs, ProductionJobOptions options, ILogger<BatesAllocationCoordinator> logger)
{
    /// <summary>Actor of worker-side production audit events, on behalf of the initiator.</summary>
    public const string WorkerActor = "service:production";

    public async Task<BatesAllocationStep> ProcessAsync(ActiveBatesAllocation active, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(active);
        var production = active.Production;
        var jobId = production.BatesJobId!.Value;
        switch (active.JobStatus)
        {
            case JobStatus.Created or JobStatus.Preparing:
                return await PlanAsync(production, jobId, cancellationToken).ConfigureAwait(false);
            case JobStatus.Completed:
                return await CompleteAsync(production, jobId, cancellationToken).ConfigureAwait(false);
            case JobStatus.CompletedWithErrors:
                return await EndAsync(production, jobId, string.Create(CultureInfo.InvariantCulture,
                    $"{active.ChunksFailed} allocation chunk(s) failed; allocate again."), null, cancellationToken).ConfigureAwait(false);
            case JobStatus.Failed:
                var job = await jobs.GetAsync(production.WorkspaceId, jobId, cancellationToken).ConfigureAwait(false);
                return await EndAsync(production, jobId, job?.StatusReason ?? "The allocation job failed.", null, cancellationToken).ConfigureAwait(false);
            case JobStatus.Cancelled:
                return await EndAsync(production, jobId, "The allocation job was cancelled.", null, cancellationToken).ConfigureAwait(false);
            default:
                return BatesAllocationStep.None;
        }
    }

    private async Task<BatesAllocationStep> PlanAsync(ProductionRecord production, Guid jobId, CancellationToken cancellationToken)
    {
        var ws = production.WorkspaceId;
        if (!await productions.TryClaimAsync(ws, production.ProductionId, options.WorkerId, options.ClaimLease, cancellationToken).ConfigureAwait(false)
            || await jobs.GetAsync(ws, jobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return BatesAllocationStep.None;
        }

        if (job.Status == JobStatus.Created && !(await jobs.BeginPreparingAsync(ws, jobId, cancellationToken).ConfigureAwait(false)).Applied)
        {
            return BatesAllocationStep.None;
        }

        var specification = ProductionSpecificationRules.Deserialize(production.SpecificationJson);
        var format = ProductionSpecificationRules.FormatOf(specification);
        var plan = await productions.PlanAllocationAsync(ws, production.ProductionId, jobId, new BatesPlanRequest(
            format.Level, ProductionSpecificationRules.OutputFor(specification), options.DocumentsPerChunk, options.NumbersPerChunk, format.MaxNumber),
            cancellationToken).ConfigureAwait(false);
        if (!plan.Planned)
        {
            var reason = plan.Conflicts.Count > 0
                ? "The Bates range overlaps " + string.Join("; ", plan.Conflicts.Select(c => string.Create(CultureInfo.InvariantCulture,
                    $"production '{c.ProductionName}' ({format.Format(c.FirstNumber)}–{format.Format(c.LastNumber)})"))) + "; choose another start number."
                : plan.Problem!;
            var conflict = plan.Conflicts.Count > 0
                ? WorkerEvent(production, AuditTaxonomy.Integrity.BatesConflict, new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["ProductionId"] = production.ProductionId.ToString(),
                    ["Prefix"] = production.BatesPrefix,
                    ["Conflicts"] = string.Join(',', plan.Conflicts.Select(c => string.Create(CultureInfo.InvariantCulture,
                        $"{c.ProductionId:N}:{c.FirstNumber}-{c.LastNumber}"))),
                }) with
                { Category = AuditTaxonomy.Integrity.Category, Outcome = AuditOutcome.Failure, ReasonCode = "BatesOverlap" }
                : null;
            return await EndAsync(production, jobId, reason, conflict, cancellationToken).ConfigureAwait(false);
        }

        var chunks = plan.Chunks
            .Select(c => new ChunkPlan(ChunkMembership.SnapshotRange(production.SnapshotId, c.SequenceFrom, c.SequenceTo), checked((int)(c.SequenceTo - c.SequenceFrom + 1))))
            .ToList();
        var started = await jobs.StartAsync(new JobStartRequest(ws, jobId, ChunkOperationKind.ProductionChunk, chunks), cancellationToken).ConfigureAwait(false);
        await productions.ReleaseClaimAsync(ws, production.ProductionId, options.WorkerId, cancellationToken).ConfigureAwait(false);
        LogPlanned(logger, production.ProductionId, plan.Documents, plan.Numbers, chunks.Count);
        return started.Applied ? BatesAllocationStep.Started : BatesAllocationStep.None;
    }

    private async Task<BatesAllocationStep> CompleteAsync(ProductionRecord production, Guid jobId, CancellationToken cancellationToken)
    {
        var ws = production.WorkspaceId;
        if (!await productions.TryClaimAsync(ws, production.ProductionId, options.WorkerId, options.ClaimLease, cancellationToken).ConfigureAwait(false))
        {
            return BatesAllocationStep.None;
        }

        var integrity = await productions.CheckIntegrityAsync(ws, production.ProductionId, cancellationToken).ConfigureAwait(false);
        var hash = integrity.Passed ? await AssignmentHashAsync(productions, ws, production.ProductionId, cancellationToken).ConfigureAwait(false) : null;
        var format = ProductionSpecificationRules.FormatOf(ProductionSpecificationRules.Deserialize(production.SpecificationJson));
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ProductionId"] = production.ProductionId.ToString(),
            ["First"] = production.BatesFirst is { } f ? format.Format(f) : null,
            ["Last"] = production.BatesLast is { } l ? format.Format(l) : null,
            ["Documents"] = Invariant(integrity.Documents),
            ["Numbers"] = Invariant(integrity.Numbers),
            ["Placeholders"] = Invariant(integrity.Placeholders),
            ["NativeSlipSheets"] = Invariant(integrity.NativeSlipSheets),
        };
        AuditEvent audit;
        if (integrity.Passed)
        {
            details["AssignmentsSha256"] = Convert.ToHexStringLower(hash!);
            audit = WorkerEvent(production, AuditTaxonomy.Production.BatesAllocated, details);
        }
        else
        {
            details["Problems"] = string.Join(" ", integrity.Problems);
            audit = WorkerEvent(production, AuditTaxonomy.Integrity.BatesConflict, details) with
            {
                Category = AuditTaxonomy.Integrity.Category,
                Outcome = AuditOutcome.Failure,
                ReasonCode = "IntegrityCheckFailed",
            };
            LogIntegrityFailed(logger, production.ProductionId, string.Join(" ", integrity.Problems));
        }

        if (!await productions.CompleteAllocationAsync(ws, production.ProductionId, jobId, integrity, hash, [audit], cancellationToken).ConfigureAwait(false))
        {
            return BatesAllocationStep.None;
        }

        LogCompleted(logger, production.ProductionId, integrity.Passed);
        return BatesAllocationStep.Completed;
    }

    private async Task<BatesAllocationStep> EndAsync(ProductionRecord production, Guid jobId, string reason, AuditEvent? audit, CancellationToken cancellationToken)
    {
        var ws = production.WorkspaceId;
        if (await jobs.GetAsync(ws, jobId, cancellationToken).ConfigureAwait(false) is { Status: JobStatus.Created or JobStatus.Preparing or JobStatus.Running })
        {
            await jobs.FailAsync(ws, jobId, reason, cancellationToken).ConfigureAwait(false);
        }

        return await productions.FailAllocationAsync(ws, production.ProductionId, jobId, reason, audit is null ? [] : [audit], cancellationToken).ConfigureAwait(false)
            ? BatesAllocationStep.Ended
            : BatesAllocationStep.None;
    }

    /// <summary>The manifest hash of the stored assignment, streamed in production order.</summary>
    public static async Task<byte[]> AssignmentHashAsync(IProductionStore productions, Guid workspaceId, Guid productionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productions);
        using var hasher = new BatesAssignmentHasher();
        long after = 0;
        while (true)
        {
            var page = await productions.ReadDocumentsAsync(workspaceId, productionId, after, 5_000, cancellationToken).ConfigureAwait(false);
            foreach (var row in page)
            {
                hasher.Append(row.Sequence, row.DocumentId, row.DocumentVersion, row.Output, row.Units, new BatesAssignment(
                    row.Sequence, row.DocumentId, row.BegNumber ?? 0, row.EndNumber ?? 0, row.ProdBegBates ?? string.Empty, row.ProdEndBates ?? string.Empty,
                    row.ProdBegAttach ?? string.Empty, row.ProdEndAttach ?? string.Empty));
            }

            if (page.Count < 5_000)
            {
                return hasher.Finish();
            }

            after = page[^1].Sequence;
        }
    }

    internal static AuditEvent WorkerEvent(ProductionRecord production, string action, IReadOnlyDictionary<string, string?> details) => new()
    {
        WorkspaceId = production.WorkspaceId,
        OccurredAt = DateTimeOffset.UtcNow,
        Category = AuditTaxonomy.Production.Category,
        Action = action,
        ActorType = AuditActorType.Service,
        ActorId = WorkerActor,
        ActorDisplay = "Production worker",
        OnBehalfOf = production.CreatedBy,
        ResourceType = AuditTaxonomy.Production.ResourceType,
        ResourceId = production.ProductionId.ToString(),
        Outcome = AuditOutcome.Success,
        JobId = production.BatesJobId,
        SnapshotId = production.SnapshotId,
        Details = details,
    };

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Production {ProductionId}: {Documents} document(s), {Numbers} Bates number(s) planned in {Chunks} chunk(s)")]
    private static partial void LogPlanned(ILogger logger, Guid productionId, long documents, long numbers, int chunks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Production {ProductionId}: Bates allocation completed (integrity passed: {Passed})")]
    private static partial void LogCompleted(ILogger logger, Guid productionId, bool passed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Production {ProductionId}: Bates integrity check failed: {Problems}")]
    private static partial void LogIntegrityFailed(ILogger logger, Guid productionId, string problems);
}

/// <summary>
/// The <see cref="ChunkOperationKind.ProductionChunk"/> executor (E12-T03): assigns the Bates numbers of one planned
/// range of production sequence numbers with the pure <see cref="BatesAllocator"/> and writes them back per document
/// (ProdBegBates, ProdEndBates, ProdBegAttach, ProdEndAttach) together with the chunk commit (fence F3). Inputs are
/// the stored plan only, so a chunk re-run after a crash or a redelivery writes exactly the same numbers. The initiator
/// must still hold <c>Production.Create</c>; otherwise the job is cancelled (ADR-015 D9.4).
/// </summary>
public sealed class BatesChunkExecutor(
    IProductionStore productions,
    IJobRepository jobs,
    IAuthorizationService authorization
#if OPPORTUNITY_FAILPOINTS
    , IFaultInjector? faults = null
#endif
    ) : IJobChunkExecutor
{
    public ChunkOperationKind OperationKind => ChunkOperationKind.ProductionChunk;

    public async Task<ChunkExecutionResult> ExecuteAsync(ChunkExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var chunk = context.Chunk;
        var membership = chunk.Membership;
        var ws = context.WorkspaceId;
        if (membership.Kind != ChunkMembershipKind.SnapshotRange || membership.RangeFrom is not { } from || membership.RangeTo is not { } to)
        {
            throw new PermanentChunkException("NotABatesChunk", "A Bates allocation chunk references a range of production sequence numbers.");
        }

        var production = await productions.GetByBatesJobAsync(ws, chunk.Lease.JobId, cancellationToken).ConfigureAwait(false)
            ?? throw new PermanentChunkException("ProductionMissing", "The chunk's job allocates no production.");
        if (production.SnapshotId != membership.SnapshotId)
        {
            throw new PermanentChunkException("ProductionSnapshotMismatch", "The chunk's snapshot is not the production's.");
        }

        var initiator = await productions.ReadInitiatorAsync(production.CreatedBy, cancellationToken).ConfigureAwait(false);
        var principal = new SecurityPrincipal
        {
            UserId = production.CreatedBy,
            DisplayName = initiator?.DisplayName ?? production.CreatedByDisplay,
            Groups = initiator?.Groups ?? production.CreatedByGroups,
        };
        if (!(await authorization.AuthorizeAsync(principal, ws, Permission.ProductionCreate, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            await jobs.CancelAsync(ws, chunk.Lease.JobId, production.CreatedBy, "The initiator no longer holds Production.Create.", cancellationToken)
                .ConfigureAwait(false);
            throw new ChunkFencedException(ChunkFence.JobCancelling);
        }

        var rows = await productions.ReadSliceAsync(ws, production.ProductionId, from, to, cancellationToken).ConfigureAwait(false);
        if (rows.Count != membership.KnownCount)
        {
            throw new PermanentChunkException("BatesPlanMissing", "The production's plan does not cover this chunk (it was released or replanned).");
        }

        IReadOnlyList<BatesAssignment> assignments;
        try
        {
            var format = ProductionSpecificationRules.FormatOf(ProductionSpecificationRules.Deserialize(production.SpecificationJson));
            assignments = BatesAllocator.Assign(format, production.BatesStart,
                [.. rows.Select(r => new BatesSliceMember(r.Sequence, r.DocumentId, r.FamilyKey, r.Units, r.FirstOffset))]);
        }
        catch (BatesOverflowException ex)
        {
            throw new PermanentChunkException("BatesOverflow", ex.Message, ex);
        }

#if OPPORTUNITY_FAILPOINTS
        if (faults is not null)
        {
            await faults.HitAsync(Failpoints.BatesBeforeWrite, new FailpointContext(context.Message, chunk.Lease), cancellationToken).ConfigureAwait(false);
        }
#endif

        // Fence F2 before the PostgreSQL batch; F3 runs inside the store's transaction.
        await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
        var commit = await productions.ApplyChunkAsync(chunk, production.ProductionId, assignments, new ChunkCompletion { ItemsApplied = assignments.Count },
            cancellationToken).ConfigureAwait(false);
        return ChunkExecutionResult.Committed(commit);
    }
}

/// <summary>Polls every workspace for allocations to plan or complete (restart-safe: claims expire).</summary>
public sealed partial class BatesAllocationCoordinatorService(
    IServiceScopeFactory scopes, ProductionJobOptions options, ILogger<BatesAllocationCoordinatorService> logger) : BackgroundService
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var chunks = scope.ServiceProvider.GetRequiredService<IJobChunkRepository>();
        var productions = scope.ServiceProvider.GetRequiredService<IProductionStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<BatesAllocationCoordinator>();
        var steps = 0;
        foreach (var workspaceId in await chunks.GetWorkspacesToSweepAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var active in await productions.GetActiveAllocationsAsync(workspaceId, 20, cancellationToken).ConfigureAwait(false))
            {
                if (await coordinator.ProcessAsync(active, cancellationToken).ConfigureAwait(false) != BatesAllocationStep.None)
                {
                    steps++;
                }
            }
        }

        return steps;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogPassFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bates allocation pass failed; retrying on the next poll")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
