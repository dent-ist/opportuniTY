namespace Opportunity.Application.Backup;

/// <param name="Record">The PostgreSQL snapshot row (with the number of anchored workspaces).</param>
/// <param name="Outcome">The repository's outcome.</param>
/// <param name="Pruned">Snapshots deleted by retention after this one.</param>
public sealed record SearchSnapshotRun(SearchSnapshotRecord Record, SearchSnapshotInfo Outcome, IReadOnlyList<string> Pruned);

/// <summary>
/// The search snapshot job (ADR-016 §3.2, design §3), run hourly by the dispatcher host and on demand by
/// <c>backup search-snapshot</c>: (1) commit the per-workspace anchors <c>G_s</c> in PostgreSQL, (2) take the OpenSearch
/// snapshot and wait for it, (3) record the outcome, then apply retention (the newest <see cref="SearchSnapshotOptions.KeepNewest"/>
/// plus one per UTC day up to <see cref="SearchSnapshotOptions.Retention"/>). Because the anchors are committed before
/// the snapshot starts, every snapshot a restore may use still has its anchors after a point-in-time restore.
/// </summary>
public sealed class SearchSnapshotService(
    ISearchSnapshotStore store, ISearchSnapshotRepository repository, SearchSnapshotOptions options, TimeProvider time)
{
    public async Task<SearchSnapshotRun> TakeAsync(CancellationToken cancellationToken = default)
    {
        await repository.EnsureRepositoryAsync(cancellationToken).ConfigureAwait(false);
        var name = SearchSnapshotNames.For(options.NamePrefix, time.GetUtcNow());
        var record = await store.BeginAsync(name, repository.RepositoryName, cancellationToken).ConfigureAwait(false);

        SearchSnapshotInfo outcome;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            outcome = await repository.CreateAsync(name, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The repository stays the authority: a snapshot that finished after all is found by name on restore.
            outcome = new SearchSnapshotInfo(name, SearchSnapshotState.Failed, null, null, [], Truncate(ex.Message));
            await store.CompleteAsync(record.SnapshotId, outcome, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await store.CompleteAsync(record.SnapshotId, outcome, cancellationToken).ConfigureAwait(false);
        var pruned = outcome.State == SearchSnapshotState.Success ? await PruneAsync(cancellationToken).ConfigureAwait(false) : [];
        return new SearchSnapshotRun(record with { State = outcome.State, StartedAt = outcome.StartTime, EndedAt = outcome.EndTime }, outcome, pruned);
    }

    /// <summary>
    /// Deletes snapshots outside retention from the repository and forgets their anchors. Only the snapshot job's own
    /// snapshots (its name prefix) are considered, and at least one usable snapshot always remains.
    /// </summary>
    public async Task<IReadOnlyList<string>> PruneAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
        var expired = SelectExpired(snapshots, options, time.GetUtcNow());
        foreach (var name in expired)
        {
            await repository.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
            if (await store.FindAsync(repository.RepositoryName, name, cancellationToken).ConfigureAwait(false) is { } record)
            {
                await store.DeleteAsync(record.SnapshotId, cancellationToken).ConfigureAwait(false);
            }
        }

        return expired;
    }

    /// <summary>The retention rule, pure: keep the newest N usable snapshots and the oldest usable one of each UTC day within retention.</summary>
    public static IReadOnlyList<string> SelectExpired(IReadOnlyList<SearchSnapshotInfo> snapshots, SearchSnapshotOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(options);
        var own = snapshots
            .Where(s => s.Name.StartsWith(options.NamePrefix + "-", StringComparison.Ordinal) && s.State != SearchSnapshotState.InProgress)
            .OrderByDescending(s => s.StartTime ?? DateTimeOffset.MinValue)
            .ToList();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in own.Where(s => s.IsUsable).Take(options.KeepNewest))
        {
            keep.Add(s.Name);
        }

        foreach (var day in own.Where(s => s.IsUsable && now - s.StartTime!.Value <= options.Retention).GroupBy(s => s.StartTime!.Value.UtcDateTime.Date))
        {
            keep.Add(day.MinBy(s => s.StartTime!.Value)!.Name);
        }

        // Failed or partial snapshots are never restored; they go once something newer succeeded.
        var newestUsable = own.FirstOrDefault(s => s.IsUsable)?.StartTime;
        return [.. own
            .Where(s => !keep.Contains(s.Name))
            .Where(s => s.IsUsable || (newestUsable is { } n && (s.StartTime ?? DateTimeOffset.MinValue) < n))
            .Select(s => s.Name)];
    }

    private static string Truncate(string text) => text.Length <= 2000 ? text : text[..2000];
}
