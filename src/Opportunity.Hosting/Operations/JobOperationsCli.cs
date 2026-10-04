using System.Globalization;

using Npgsql;

using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Data.Jobs;
using Opportunity.Data.SearchWork;
using Opportunity.Hosting.Health;

namespace Opportunity.Hosting.Operations;

/// <summary>
/// The operations CLI of the worker image (E06-T06; runbooks in docs/operations): <c>jobs list|show|failures|replay|
/// replay-outbox|backlog|redispatch</c>, run instead of the worker host when the first argument is <c>jobs</c>, e.g.
/// <c>docker compose run --rm --no-deps worker jobs failures --workspace &lt;id&gt; --job &lt;id&gt;</c>. It connects
/// with the runtime login (<c>ConnectionStrings__App</c>, RLS applies) and uses the same PostgreSQL operations as the
/// API: replay resets rows to Pending for the dispatcher (never re-publishes dead-lettered messages) and is audited as
/// the operator named by <c>--operator</c>.
/// </summary>
public static class JobOperationsCli
{
    public const string Verb = "jobs";

    public const int ExitSuccess = 0;
    public const int ExitNotFound = 1;
    public const int ExitUsage = 2;

    private const string Usage =
        """
        Usage: jobs <command> --workspace <id> [options]   (connection: ConnectionStrings__App or --connection-string)
          list          [--status running,failed,...] [--type import,...] [--limit 50]   jobs, newest first
          show          --job <id>                       one job: status, counters, chunk breakdown, last error
          failures      [--job <id>] [--limit 100]       failed chunks and index tasks of a job; without --job the
                                                         failed SearchOutbox rows (interactive edits)
          replay        --job <id> --operator <name>     reset the job's failed chunks and index tasks to Pending
          replay-outbox --operator <name>                reset the workspace's failed SearchOutbox rows to Pending
          backlog                                        un-applied and failed search work, oldest commit times
          redispatch                                     return lost work to Pending now (what the sweepers do every 30 s)
        """;

    /// <summary>Runs the CLI when <paramref name="args"/> starts with <see cref="Verb"/>; null otherwise (start the host).</summary>
    public static async Task<int?> TryRunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args is not [Verb, ..])
        {
            return null;
        }

        return await RunAsync(args[1..], Console.Out, Console.Error, null, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="args">The command and its options (without the leading <see cref="Verb"/>).</param>
    /// <param name="connectionString">Overrides <c>--connection-string</c> and the environment (tests).</param>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, string? connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args is not [var command, .. var rest] || !TryParseOptions(rest, out var options))
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitUsage;
        }

        connectionString ??= options.GetValueOrDefault("connection-string")
            ?? Environment.GetEnvironmentVariable($"ConnectionStrings__{PostgresReadiness.ConnectionStringName}");
        if (string.IsNullOrWhiteSpace(connectionString) || !TryGuid(options, "workspace", out var workspaceId))
        {
            await error.WriteLineAsync("A workspace id (--workspace) and a connection string are required.\n" + Usage).ConfigureAwait(false);
            return ExitUsage;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new JobOperationsStore(dataSource);
        var cli = new Session(store, dataSource, workspaceId, options, output, error, cancellationToken);
        return command switch
        {
            "list" => await cli.ListAsync().ConfigureAwait(false),
            "show" => await cli.ShowAsync().ConfigureAwait(false),
            "failures" => await cli.FailuresAsync().ConfigureAwait(false),
            "replay" => await cli.ReplayAsync().ConfigureAwait(false),
            "replay-outbox" => await cli.ReplayOutboxAsync().ConfigureAwait(false),
            "backlog" => await cli.BacklogAsync().ConfigureAwait(false),
            "redispatch" => await cli.RedispatchAsync().ConfigureAwait(false),
            _ => await cli.UsageAsync().ConfigureAwait(false),
        };
    }

    private static bool TryParseOptions(string[] args, out Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                return false;
            }

            options[args[i][2..]] = args[i + 1];
        }

        return true;
    }

    private static bool TryGuid(Dictionary<string, string> options, string name, out Guid value)
    {
        value = default;
        return options.TryGetValue(name, out var text) && Guid.TryParse(text, out value);
    }

    private static string Time(DateTimeOffset? value) =>
        value is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "-";

    private sealed class Session(
        JobOperationsStore store, NpgsqlDataSource dataSource, Guid workspaceId, Dictionary<string, string> options, TextWriter output,
        TextWriter error, CancellationToken cancellationToken)
    {
        public async Task<int> UsageAsync()
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitUsage;
        }

        public async Task<int> ListAsync()
        {
            if (!TryEnums<JobStatus>("status", out var statuses) || !TryEnums<JobType>("type", out var types) || !TryLimit(50, out var limit))
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var page = await store.ListAsync(new JobListQuery(workspaceId) { Statuses = statuses, Types = types, Limit = limit }, cancellationToken)
                .ConfigureAwait(false);
            await output.WriteLineAsync("JOB_ID\tTYPE\tSTATUS\tCOMMITTED\tSEARCHABLE\tERRORS\tCREATED\tUPDATED\tCREATED_BY").ConfigureAwait(false);
            foreach (var o in page.Items)
            {
                var j = o.Job;
                var (done, total, state) = JobSearchability.Evaluate(j, page.AppliedWatermark);
                await output.WriteLineAsync(string.Join('\t',
                    j.JobId, j.JobType, j.Status, $"{j.Counters.ChunksCommitted}/{j.Counters.ChunksTotal}", $"{done}/{total} {state}",
                    j.Counters.ChunksFailed + j.Counters.ItemsFailed + o.FailedIndexTasks, Time(j.CreatedAt), Time(j.UpdatedAt),
                    o.InitiatorDisplayName ?? j.InitiatedBy.ToString())).ConfigureAwait(false);
            }

            return ExitSuccess;
        }

        public async Task<int> ShowAsync()
        {
            if (!TryGuid(options, "job", out var jobId))
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            if (await store.GetDetailAsync(workspaceId, jobId, cancellationToken).ConfigureAwait(false) is not { } detail)
            {
                await error.WriteLineAsync($"No job {jobId} in workspace {workspaceId}.").ConfigureAwait(false);
                return ExitNotFound;
            }

            var j = detail.Overview.Job;
            var c = j.Counters;
            var (done, total, state) = JobSearchability.Evaluate(j, detail.AppliedWatermark);
            await output.WriteLineAsync(
                $"""
                job          {j.JobId} ({j.JobType}{(detail.Overview.Name is { } n ? ", " + n : string.Empty)})
                status       {j.Status}{(j.StatusReason is { } r ? " — " + r : string.Empty)}
                created      {Time(j.CreatedAt)} by {detail.Overview.InitiatorDisplayName ?? j.InitiatedBy.ToString()}
                updated      {Time(j.UpdatedAt)}   finished {Time(j.FinishedAt)}
                correlation  {j.CorrelationId ?? "-"}   snapshot {j.TargetSnapshotId?.ToString() ?? "-"}
                committed    {c.ChunksCommitted}/{c.ChunksTotal} chunks ({c.ChunksFailed} failed, {c.ChunksCancelled} cancelled), {c.ItemsApplied} items applied, {c.ItemsFailed} failed
                searchable   {done}/{total} index tasks, {state} (job generation {j.JobGeneration?.ToString(CultureInfo.InvariantCulture) ?? "-"}, applied watermark {detail.AppliedWatermark}); {detail.Overview.FailedIndexTasks} failed
                chunks       {string.Join(", ", detail.ChunkCounts.OrderBy(e => e.Key).Select(e => $"{e.Key} {e.Value}"))}{(detail.ExhaustedChunks > 0 ? $", Failed (attempts exhausted) {detail.ExhaustedChunks}" : string.Empty)}
                attempts     {detail.Attempts}
                last error   {detail.LastError ?? "-"}
                """).ConfigureAwait(false);
            return ExitSuccess;
        }

        public async Task<int> FailuresAsync()
        {
            if (!TryLimit(100, out var limit))
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            IReadOnlyList<JobFailureRecord> failures;
            if (options.ContainsKey("job"))
            {
                if (!TryGuid(options, "job", out var jobId))
                {
                    return await UsageAsync().ConfigureAwait(false);
                }

                failures = await store.ListFailuresAsync(workspaceId, jobId, null, limit, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                failures = await store.ListOutboxFailuresAsync(workspaceId, null, limit, cancellationToken).ConfigureAwait(false);
            }

            await output.WriteLineAsync("KIND\tID\tATTEMPTS\tFAILED_AT\tERROR").ConfigureAwait(false);
            foreach (var f in failures)
            {
                await output.WriteLineAsync(string.Join('\t', f.Source, f.Id, f.Attempts, Time(f.FailedAt), f.Error ?? "-")).ConfigureAwait(false);
            }

            return ExitSuccess;
        }

        public async Task<int> ReplayAsync()
        {
            if (!TryGuid(options, "job", out var jobId) || Operator() is not { } actor)
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var outcome = await store.ReplayFailedAsync(workspaceId, jobId, actor, cancellationToken).ConfigureAwait(false);
            if (outcome.Outcome == JobTransitionOutcome.NotFound)
            {
                await error.WriteLineAsync($"No job {jobId} in workspace {workspaceId}.").ConfigureAwait(false);
                return ExitNotFound;
            }

            await output.WriteLineAsync(
                $"Replayed {outcome.ChunksReplayed} chunk(s) and {outcome.IndexTasksReplayed} index task(s); job status {outcome.Status}.")
                .ConfigureAwait(false);
            return ExitSuccess;
        }

        public async Task<int> ReplayOutboxAsync()
        {
            if (Operator() is not { } actor)
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            var rows = await store.ReplayFailedOutboxAsync(workspaceId, actor, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Replayed {rows} SearchOutbox row(s).").ConfigureAwait(false);
            return ExitSuccess;
        }

        public async Task<int> BacklogAsync()
        {
            var backlog = await new SearchWorkMaintenance(dataSource).GetBacklogAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"""
                search outbox    {backlog.OutboxUnapplied} un-applied (oldest commit {Time(backlog.OldestOutboxCommittedAt)}), {backlog.OutboxFailed} failed
                index tasks      {backlog.TasksUnapplied} un-applied (oldest commit {Time(backlog.OldestTaskCommittedAt)}), {backlog.TasksFailed} failed
                """).ConfigureAwait(false);
            return ExitSuccess;
        }

        public async Task<int> RedispatchAsync()
        {
            // The dispatcher's own thresholds (ADR-001 §6.3, ADR-010 §3.3): Dispatched for 60 s, lease expired 10 s ago.
            var search = await new SearchWorkMaintenance(dataSource)
                .RecoverAsync(workspaceId, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var chunks = await new JobChunkRepository(dataSource)
                .RecoverExpiredLeasesAsync(workspaceId, TimeSpan.FromSeconds(10), 1_000, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Returned to Pending: {search.OutboxRedispatched} outbox row(s), {search.TasksLeaseExpired + search.TasksRedispatched} index task(s), " +
                $"{chunks.ReturnedToPending} job chunk(s); {chunks.Failed} chunk(s) failed (attempts exhausted), {chunks.Cancelled} cancelled.")
                .ConfigureAwait(false);
            return ExitSuccess;
        }

        private OperationsActor? Operator() =>
            options.TryGetValue("operator", out var name) && !string.IsNullOrWhiteSpace(name) && name.Length <= 200 ? OperationsActor.Cli(name) : null;

        private bool TryLimit(int fallback, out int limit)
        {
            limit = fallback;
            return !options.TryGetValue("limit", out var text)
                || (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit is >= 1 and <= JobListQuery.MaxLimit);
        }

        private bool TryEnums<T>(string name, out List<T> values)
            where T : struct, Enum
        {
            values = [];
            if (!options.TryGetValue(name, out var text))
            {
                return true;
            }

            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<T>(part, ignoreCase: true, out var value) || !Enum.IsDefined(value))
                {
                    return false;
                }

                values.Add(value);
            }

            return true;
        }
    }
}
