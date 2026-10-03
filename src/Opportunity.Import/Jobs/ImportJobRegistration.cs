using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Import;
using Opportunity.Application.Jobs;

namespace Opportunity.Import.Jobs;

/// <summary>
/// Prepares import jobs in the import worker: polls every workspace (ADR-005 P10) for batches whose job waits in
/// Created or Preparing and whose claim is free or expired, so a worker that died mid-preparation is taken over.
/// Chunks are then published by the job dispatcher and consumed by <see cref="ImportChunkExecutor"/>.
/// </summary>
public sealed partial class ImportPreparationService(
    IServiceScopeFactory scopes, ImportJobOptions options, ILogger<ImportPreparationService> logger) : BackgroundService
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var chunks = scope.ServiceProvider.GetRequiredService<IJobChunkRepository>();
        var batches = scope.ServiceProvider.GetRequiredService<IImportBatchStore>();
        var preparer = scope.ServiceProvider.GetRequiredService<ImportJobPreparer>();
        var prepared = 0;
        foreach (var workspaceId in await chunks.GetWorkspacesToSweepAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var batchId in await batches.GetPreparableAsync(workspaceId, 10, cancellationToken).ConfigureAwait(false))
            {
                if (await preparer.PrepareAsync(workspaceId, batchId, cancellationToken).ConfigureAwait(false) != ImportPreparationOutcome.Skipped)
                {
                    prepared++;
                }
            }
        }

        return prepared;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PreparationPollInterval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // The claim expires and the next poll (here or on another worker) resumes the preparation.
                LogPassFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import preparation pass failed; retrying on the next poll")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}

public static class ImportJobRegistration
{
    /// <summary>
    /// The import job services: <see cref="ImportJobPreparer"/>, the <see cref="ImportChunkExecutor"/> for the chunk
    /// consumer and, with <paramref name="runPreparation"/>, the polling <see cref="ImportPreparationService"/>. The host
    /// registers the stores (<see cref="IImportBatchStore"/>, jobs, fields, workspaces) and the object store.
    /// </summary>
    public static IServiceCollection AddImportJobs(this IServiceCollection services, ImportJobOptions? options = null, bool runPreparation = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new ImportJobOptions());
        services.TryAddScoped<ImportJobPreparer>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobChunkExecutor, ImportChunkExecutor>());
        if (runPreparation)
        {
            services.AddHostedService<ImportPreparationService>();
        }

        return services;
    }
}
