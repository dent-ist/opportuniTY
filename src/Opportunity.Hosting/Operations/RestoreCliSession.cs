using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Backup;
using Opportunity.Application.Storage;

using static Opportunity.Hosting.Operations.RestoreOperationsCli;

namespace Opportunity.Hosting.Operations;

/// <summary>The commands of <see cref="RestoreOperationsCli"/>; output is tab-separated for scripts and runbooks.</summary>
internal sealed class RestoreCliSession(
    IServiceProvider services, Dictionary<string, string> options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
{
    private RestoreService Restore => services.GetRequiredService<RestoreService>();

    private ISearchSnapshotRepository Repository => services.GetRequiredService<ISearchSnapshotRepository>();

    public async Task<int> UsageAsync()
    {
        await error.WriteLineAsync(UsageText).ConfigureAwait(false);
        return ExitUsage;
    }

    public async Task<int> SnapshotAsync()
    {
        var run = await services.GetRequiredService<SearchSnapshotService>().TakeAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(string.Join('\t', "snapshot", run.Record.Name, run.Outcome.State, Time(run.Outcome.StartTime),
            Time(run.Outcome.EndTime), $"{run.Record.Workspaces} anchor(s)", $"{run.Outcome.Indices.Count} index(es)")).ConfigureAwait(false);
        foreach (var pruned in run.Pruned)
        {
            await output.WriteLineAsync($"pruned\t{pruned}").ConfigureAwait(false);
        }

        return run.Outcome.State == SearchSnapshotState.Success ? ExitSuccess : ExitRefused;
    }

    public async Task<int> SnapshotsAsync()
    {
        await Repository.EnsureRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var store = services.GetRequiredService<ISearchSnapshotStore>();
        await output.WriteLineAsync("SNAPSHOT\tSTATE\tSTART\tEND\tINDICES\tANCHORS").ConfigureAwait(false);
        foreach (var s in await Repository.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = await store.FindAsync(Repository.RepositoryName, s.Name, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(string.Join('\t', s.Name, s.State, Time(s.StartTime), Time(s.EndTime),
                s.Indices.Count.ToString(CultureInfo.InvariantCulture), record is null ? "none" : record.Workspaces.ToString(CultureInfo.InvariantCulture)))
                .ConfigureAwait(false);
        }

        return ExitSuccess;
    }

    public async Task<int> PruneAsync()
    {
        foreach (var name in await services.GetRequiredService<SearchSnapshotService>().PruneAsync(cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync($"pruned\t{name}").ConfigureAwait(false);
        }

        return ExitSuccess;
    }

    public async Task<int> PlanAsync()
    {
        if (!options.TryGetValue("pgbackrest-info", out var infoFile) || !File.Exists(infoFile)
            || !TryTime("target-time", out var target) || !TryTime("decided-at", out var decided)
            || !TryTime("object-sync-point", out var sync) || !TryTime("key-backup-at", out var keys)
            || (target is null) != options.ContainsKey("end-of-wal"))
        {
            await error.WriteLineAsync("restore plan needs --pgbackrest-info <file> and exactly one of --target-time <iso> or --end-of-wal.")
                .ConfigureAwait(false);
            return await UsageAsync().ConfigureAwait(false);
        }

        var archive = PgBackRestInfo.Parse(await File.ReadAllTextAsync(infoFile, cancellationToken).ConfigureAwait(false),
            options.GetValueOrDefault("stanza", "opportunity"));
        await Repository.EnsureRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = await Repository.ListAsync(cancellationToken).ConfigureAwait(false);
        var plan = RestorePlanner.Plan(new RestorePlanRequest(target, decided ?? DateTimeOffset.UtcNow, archive, snapshots,
            options.ContainsKey("keep-live-search"), options.GetValueOrDefault("snapshot"), sync, keys));

        await output.WriteLineAsync($"verdict\t{(plan.Accepted ? "ACCEPTED" : "REFUSED")}").ConfigureAwait(false);
        await output.WriteLineAsync($"path\t{PathName(plan.Path)}").ConfigureAwait(false);
        await output.WriteLineAsync($"target\t{(plan.TargetTime is null ? "end-of-wal" : Time(plan.TargetTime))}").ConfigureAwait(false);
        await output.WriteLineAsync($"backup\t{plan.Backup?.Label ?? "-"}").ConfigureAwait(false);
        await output.WriteLineAsync($"snapshot\t{plan.Snapshot?.Name ?? "-"}\t{Time(plan.Snapshot?.StartTime)}\t{Time(plan.Snapshot?.EndTime)}").ConfigureAwait(false);
        await output.WriteLineAsync($"loss-window\t{plan.LossWindow.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s").ConfigureAwait(false);
        if (plan.ReplayWindow is { } replay)
        {
            await output.WriteLineAsync($"replay-window\t{replay.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)} min").ConfigureAwait(false);
        }

        foreach (var problem in plan.Problems)
        {
            await output.WriteLineAsync($"problem\t{problem}").ConfigureAwait(false);
        }

        foreach (var note in plan.Notes)
        {
            await output.WriteLineAsync($"note\t{note}").ConfigureAwait(false);
        }

        if (plan.Accepted)
        {
            var restore = string.Create(CultureInfo.InvariantCulture,
                $"--path {PathName(plan.Path)}{(plan.Snapshot is { } s ? " --snapshot " + s.Name : string.Empty)}{(plan.TargetTime is { } t ? " --target-time " + Time(t) : string.Empty)}{(plan.Backup is { } b ? " --backup-label " + b.Label : string.Empty)}");
            await output.WriteLineAsync($"next\trestore reconcile --restore-id {Guid.CreateVersion7():D} {restore} --operator <name>").ConfigureAwait(false);
        }

        return plan.Accepted ? ExitSuccess : ExitRefused;
    }

    public async Task<int> RestoreSnapshotAsync()
    {
        if (!options.TryGetValue("snapshot", out var name))
        {
            return await UsageAsync().ConfigureAwait(false);
        }

        await Repository.EnsureRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var result = await Repository.RestoreAsync(name, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"deleted\t{result.Deleted.Count} index(es)").ConfigureAwait(false);
        await output.WriteLineAsync($"restored\t{result.Indices.Count} index(es) from {name}").ConfigureAwait(false);
        return ExitSuccess;
    }

    public async Task<int> ReconcileAsync()
    {
        if (!TryGuid("restore-id", out var restoreId) || !options.TryGetValue("operator", out var operatorName) || string.IsNullOrWhiteSpace(operatorName)
            || operatorName.Length > 200 || !options.TryGetValue("path", out var pathText) || ParsePath(pathText) is not { } path
            || !TryTime("target-time", out var target))
        {
            return await UsageAsync().ConfigureAwait(false);
        }

        var report = await Restore.ReconcileAsync(new RestoreRequest(restoreId, path, target, options.GetValueOrDefault("backup-label"),
            options.GetValueOrDefault("snapshot"), operatorName), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("WORKSPACE\tANCHOR\tOUTBOX_REPENDED\tTASKS_REPENDED\tWATERMARK\tREDRIVEN\tBREAK_GLASS_ENDED\tSEARCH_SESSIONS\tREINDEX_ABORTED")
            .ConfigureAwait(false);
        foreach (var w in report.Workspaces)
        {
            await output.WriteLineAsync(string.Join('\t', w.WorkspaceId.ToString("D"), N(w.AnchorGeneration), N(w.OutboxRepended), N(w.TasksRepended),
                $"{N(w.WatermarkBefore)}->{N(w.WatermarkAfter)}", N(w.Redriven), N(w.BreakGlassEnded), N(w.SearchSessionsDropped), N(w.ReindexesAborted)))
                .ConfigureAwait(false);
        }

        await output.WriteLineAsync($"sessions-revoked\t{report.SessionsRevoked}").ConfigureAwait(false);
        foreach (var ws in report.PurgedFromSearch)
        {
            await output.WriteLineAsync($"purged-from-search\t{ws:D}").ConfigureAwait(false);
        }

        foreach (var ws in report.RebuildRequired)
        {
            await output.WriteLineAsync($"reindex-after-reopen\t{ws:D}\tjobs reindex --workspace {ws:D} --operator <name>").ConfigureAwait(false);
        }

        foreach (var f in report.LostWindow)
        {
            await output.WriteLineAsync(string.Join('\t', "lost-window", f.Kind, f.WorkspaceId?.ToString("D") ?? "-", f.Subject, f.Action)).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"next\trestore record --restore-id {restoreId:D}").ConfigureAwait(false);
        return ExitSuccess;
    }

    public async Task<int> RecordAsync()
    {
        if (!TryGuid("restore-id", out var restoreId))
        {
            return await UsageAsync().ConfigureAwait(false);
        }

        var result = await Restore.RecordAsync(restoreId, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"record\t{result.RecordSha256}\t{result.RecordKey ?? "(no object store configured: database only)"}").ConfigureAwait(false);
        await output.WriteLineAsync($"chains\t{result.Chains}").ConfigureAwait(false);
        foreach (var checkpoint in result.Checkpoints)
        {
            await output.WriteLineAsync(string.Join('\t', "checkpoint", AuditChainFormat.DescribeChain(checkpoint.ChainId),
                checkpoint.Sequence.ToString(CultureInfo.InvariantCulture), checkpoint.Data.Reason, checkpoint.KeyId)).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"next\trestore verify --restore-id {restoreId:D}").ConfigureAwait(false);
        return ExitSuccess;
    }

    public async Task<int> VerifyAsync()
    {
        if (!TryGuid("restore-id", out var restoreId))
        {
            return await UsageAsync().ConfigureAwait(false);
        }

        int? sample = 10_000;
        if (options.ContainsKey("full"))
        {
            sample = null;
        }
        else if (options.TryGetValue("sample", out var text))
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                return await UsageAsync().ConfigureAwait(false);
            }

            sample = n;
        }

        IAuditCheckpointKeys? keys = null;
        if (options.TryGetValue("public-key", out var files))
        {
            var texts = new List<string>();
            foreach (var file in files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                texts.Add(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
            }

            keys = new PemCheckpointKeys(texts);
        }

        var verification = await Restore.VerifyAsync(restoreId, new RestoreVerifyOptions(
            options.ContainsKey("converge"), sample, AcknowledgeLostWindow: options.ContainsKey("acknowledge-lost-window"),
            RecoverMissingObjects: options.ContainsKey("recover-missing-objects"), Keys: keys), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("GATE\tRESULT\tPRE_OPEN\tDETAIL").ConfigureAwait(false);
        foreach (var gate in verification.Gates)
        {
            await output.WriteLineAsync(string.Join('\t', gate.Name, gate.Passed ? "PASS" : "FAIL", gate.PreOpen ? "yes" : "no", gate.Detail))
                .ConfigureAwait(false);
        }

        await output.WriteLineAsync(verification.Passed ? "all gates passed" : "gates failed: do not start any writer").ConfigureAwait(false);
        return verification.Passed ? ExitSuccess : ExitRefused;
    }

    public async Task<int> ObjectAsync()
    {
        if (!options.TryGetValue("key", out var text) || !ObjectKey.TryParse(text, out var key))
        {
            return await UsageAsync().ConfigureAwait(false);
        }

        if (services.GetService<IObjectVersionRecovery>() is not { } recovery)
        {
            await error.WriteLineAsync("The configured object store has no versions (ObjectStorage__Provider=S3 with versioning).").ConfigureAwait(false);
            return ExitFailed;
        }

        if (options.ContainsKey("list"))
        {
            await output.WriteLineAsync("VERSION\tLATEST\tDELETE_MARKER\tSIZE\tLAST_MODIFIED").ConfigureAwait(false);
            foreach (var v in await recovery.ListVersionsAsync(key, cancellationToken).ConfigureAwait(false))
            {
                await output.WriteLineAsync(string.Join('\t', v.VersionId, v.IsLatest, v.IsDeleteMarker, v.Size.ToString(CultureInfo.InvariantCulture),
                    Time(v.LastModified))).ConfigureAwait(false);
            }

            return ExitSuccess;
        }

        var outcome = await recovery.RecoverAsync(key, options.GetValueOrDefault("version"), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"{outcome}\t{key}").ConfigureAwait(false);
        return outcome == ObjectRecoveryOutcome.NoVersion ? ExitRefused : ExitSuccess;
    }

    private static RestorePath? ParsePath(string text) => text switch
    {
        "end-of-wal" => RestorePath.EndOfWal,
        "snapshot" => RestorePath.Snapshot,
        "rebuild" => RestorePath.Rebuild,
        _ => null,
    };

    private static string PathName(RestorePath path) => path switch
    {
        RestorePath.EndOfWal => "end-of-wal",
        RestorePath.Snapshot => "snapshot",
        RestorePath.KeepLive => "keep-live",
        _ => "rebuild",
    };

    private bool TryGuid(string name, out Guid value)
    {
        value = default;
        return options.TryGetValue(name, out var text) && Guid.TryParse(text, out value) && value != Guid.Empty;
    }

    private bool TryTime(string name, out DateTimeOffset? value)
    {
        value = null;
        if (!options.TryGetValue(name, out var text))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static string N(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
