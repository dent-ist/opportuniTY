using System.Globalization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Jobs;
using Opportunity.Application.Rendering;
using Opportunity.Core.Jobs;
using Opportunity.Rendering.Renderers;

namespace Opportunity.Rendering.Jobs;

/// <summary>
/// Creates and plans render jobs in the render worker (E11-T02, ADR-010 §1). Every finished import job (whatever its
/// outcome: committed chunks stay, Q-34) whose documents need rasters gets one <see cref="JobType.Render"/> job, run as
/// the import's initiator; the job is created with a client idempotency key derived from the import and the import is
/// then recorded as handled, so restarts and concurrent coordinators never create a second job. Planning (Created →
/// Preparing → Running) lists the documents that still need rasters (state-based, so an import whose documents were
/// rendered meanwhile plans nothing) and splits them into explicit-id chunks bounded by documents, known pages and
/// native bytes (ADR-010 §6). A job without chunks completes at once.
/// </summary>
public sealed partial class RenderCoordinator(
    IRenderStore store,
    IJobRepository jobs,
    IRenderer renderer,
    RenderJobOptions options,
    ILogger<RenderCoordinator> logger)
{
    private RenderRendererKey Key => new(renderer.Identity.Name, renderer.Identity.Version, renderer.Identity.SettingsHash);

    /// <summary>One pass over one workspace; returns the number of jobs created or started.</summary>
    public async Task<int> RunOnceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var steps = 0;
        foreach (var import in await store.GetImportsToRenderAsync(workspaceId, 20, cancellationToken).ConfigureAwait(false))
        {
            if (await RequestAsync(workspaceId, import, cancellationToken).ConfigureAwait(false))
            {
                steps++;
            }
        }

        foreach (var job in await store.GetJobsToPlanAsync(workspaceId, 20, cancellationToken).ConfigureAwait(false))
        {
            if (await PlanAsync(workspaceId, job, cancellationToken).ConfigureAwait(false))
            {
                steps++;
            }
        }

        return steps;
    }

    private async Task<bool> RequestAsync(Guid workspaceId, ImportToRender import, CancellationToken cancellationToken)
    {
        var scope = new RenderScope(import.ImportJobId, import.ImportBatchId);
        var candidates = await store.GetCandidatesAsync(workspaceId, scope, Key, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            await store.RecordRenderRequestAsync(workspaceId, import.ImportJobId, null, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var creation = await jobs.CreateAsync(new NewJob
        {
            WorkspaceId = workspaceId,
            JobType = JobType.Render,
            InitiatedBy = import.InitiatedBy,
            Parameters = scope.ToParameters(),
            ClientIdempotencyKey = "render-import-" + import.ImportJobId.ToString("N"),
            CorrelationId = import.CorrelationId,
        }, cancellationToken).ConfigureAwait(false);
        await store.RecordRenderRequestAsync(workspaceId, import.ImportJobId, creation.Job.JobId, cancellationToken).ConfigureAwait(false);
        if (creation.Created)
        {
            LogCreated(logger, creation.Job.JobId, import.ImportJobId, candidates.Count);
        }

        return creation.Created;
    }

    private async Task<bool> PlanAsync(Guid workspaceId, RenderJobToPlan job, CancellationToken cancellationToken)
    {
        if (job.Status == JobStatus.Created
            && !(await jobs.BeginPreparingAsync(workspaceId, job.JobId, cancellationToken).ConfigureAwait(false)).Applied)
        {
            return false;
        }

        if (RenderScope.FromParameters(job.Parameters) is not { } scope)
        {
            await jobs.FailAsync(workspaceId, job.JobId, "The render job does not name the import it renders.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var candidates = await store.GetCandidatesAsync(workspaceId, scope, Key, cancellationToken).ConfigureAwait(false);
        var plans = Plan(candidates, options);
        var started = await jobs.StartAsync(new JobStartRequest(workspaceId, job.JobId, ChunkOperationKind.RenderChunk, plans), cancellationToken)
            .ConfigureAwait(false);
        if (started.Applied)
        {
            LogPlanned(logger, job.JobId, candidates.Count, plans.Count);
        }

        return started.Applied;
    }

    /// <summary>Consecutive candidates per chunk, bounded by documents, known pages and native bytes; an oversized document is a chunk of its own.</summary>
    public static IReadOnlyList<ChunkPlan> Plan(IReadOnlyList<RenderCandidate> candidates, RenderJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);
        var plans = new List<ChunkPlan>();
        var current = new List<Guid>();
        long pages = 0, bytes = 0;
        void Flush()
        {
            if (current.Count > 0)
            {
                plans.Add(new ChunkPlan(ChunkMembership.ExplicitIds(current), current.Count, bytes));
                current = [];
                pages = 0;
                bytes = 0;
            }
        }

        var maxDocuments = Math.Clamp(options.DocumentsPerChunk, 1, ChunkMembership.MaxExplicitIds);
        foreach (var candidate in candidates)
        {
            if (current.Count > 0
                && (current.Count >= maxDocuments
                    || pages + candidate.EstimatedPages > options.PagesPerChunk
                    || bytes + candidate.SourceBytes > options.SourceBytesPerChunk))
            {
                Flush();
            }

            current.Add(candidate.DocumentId);
            pages += candidate.EstimatedPages;
            bytes += candidate.SourceBytes;
        }

        Flush();
        return plans;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Render job {JobId} created for import job {ImportJobId} ({Documents} document(s))")]
    private static partial void LogCreated(ILogger logger, Guid jobId, Guid importJobId, int documents);

    [LoggerMessage(Level = LogLevel.Information, Message = "Render job {JobId} started: {Documents} document(s) in {Chunks} chunk(s)")]
    private static partial void LogPlanned(ILogger logger, Guid jobId, int documents, int chunks);
}

/// <summary>Polls every workspace for finished imports to render and render jobs to plan (restart-safe: state lives in PostgreSQL).</summary>
public sealed partial class RenderCoordinatorService(
    IServiceScopeFactory scopes, RenderJobOptions options, ILogger<RenderCoordinatorService> logger) : BackgroundService
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var chunks = scope.ServiceProvider.GetRequiredService<IJobChunkRepository>();
        var coordinator = scope.ServiceProvider.GetRequiredService<RenderCoordinator>();
        var steps = 0;
        foreach (var workspaceId in await chunks.GetWorkspacesToSweepAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                steps += await coordinator.RunOnceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One workspace's failure (e.g. it is being deleted) must not stall the others.
                LogWorkspaceFailed(logger, workspaceId.ToString("D", CultureInfo.InvariantCulture), ex);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Render coordination failed for workspace {WorkspaceId}; retrying on the next poll")]
    private static partial void LogWorkspaceFailed(ILogger logger, string workspaceId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Render coordination pass failed; retrying on the next poll")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
