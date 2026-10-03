using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Identity;

namespace Opportunity.Security.Authentication;

/// <summary>
/// Deletes sessions that ended more than a day ago, hourly. Ended sessions already hold no tokens. The store is resolved
/// per run so the host starts without a database connection.
/// </summary>
internal sealed partial class SessionCleanupService(IServiceProvider services, TimeProvider time, ILogger<SessionCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var deleted = await services.GetRequiredService<ISessionStore>().DeleteEndedAsync(time.GetUtcNow() - Retention, stoppingToken).ConfigureAwait(false);
                LogDeleted(logger, deleted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception.GetType().Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session cleanup deleted {Count} ended session(s).")]
    private static partial void LogDeleted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session cleanup failed ({ErrorType}); retrying next hour.")]
    private static partial void LogFailed(ILogger logger, string errorType);
}
