using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Api.Conventions;
using Opportunity.Application.Jobs;
using Opportunity.Contracts.Api;
using Opportunity.Core.Jobs;
using Opportunity.Data.Jobs;

namespace Opportunity.Api.Jobs;

/// <summary>Minimal job status API for the first slice (E06-T02); operations and SSE come with E06-T06.</summary>
public sealed class JobEndpoints : IApiEndpointModule
{
    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Workspace.MapGet("/jobs/{jobId}", GetJobAsync)
            .WithName("GetJob")
            .WithTags("Jobs")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Job status with separate committed and indexed progress.");
    }

    internal static async Task<Results<Ok<JobResource>, ProblemHttpResult>> GetJobAsync(
        string workspaceId, string jobId, IJobRepository jobs, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(workspaceId, out var ws) || !Guid.TryParse(jobId, out var id)
            || await jobs.GetAsync(ws, id, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return Problems.NotFound("No such job.");
        }

        return TypedResults.Ok(ToResource(job));
    }

    internal static JobResource ToResource(JobInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var c = job.Counters;
        // ADR-010 §10: current once the job's PG phase is over and every index task it created has been applied. The
        // watermark ≥ JobGeneration check joins once the search watermark exists (E06-T03/E07).
        var indexState = JobStateMachine.IsFinished(job.Status) && c.IndexTasksApplied >= c.IndexTasksTotal
            ? JobIndexState.Current
            : JobIndexState.Indexing;
        return new JobResource(
            job.JobId,
            job.WorkspaceId,
            Enum.Parse<JobResourceType>(job.JobType.ToString()),
            Enum.Parse<JobResourceStatus>(job.Status.ToString()),
            job.StatusReason,
            job.InitiatedBy,
            job.TargetSnapshotId,
            job.CreatedAt,
            job.StartedAt,
            job.FinishedAt,
            new JobCommittedProgress(
                c.ChunksTotal, c.ChunksCommitted, c.ChunksFailed, c.ChunksCancelled, c.ChunksPending,
                c.ItemsApplied, c.ItemsUnchanged, c.ItemsSkippedConcurrentEdit, c.ItemsExcludedNoAccess, c.ItemsFailed),
            new JobIndexedProgress(indexState, c.IndexTasksTotal, c.IndexTasksApplied));
    }
}

public static class JobEndpointRegistration
{
    public static IServiceCollection AddJobEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IJobRepository, JobRepository>();
        services.AddSingleton<IApiEndpointModule, JobEndpoints>();
        return services;
    }
}
