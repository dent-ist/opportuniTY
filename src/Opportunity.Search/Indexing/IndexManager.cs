using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Search.Indexing;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Indexing;

/// <summary>
/// ADR-006 index management over the PostgreSQL placement record (<see cref="IIndexPlacementStore"/>) and the
/// OpenSearch REST API. Placements are cached per process for at most <see cref="IndexPlacementOptions.CacheTtl"/>.
/// </summary>
internal sealed partial class IndexManager(
    IIndexPlacementStore store,
    OpenSearchConnection connection,
    IndexTemplates templates,
    OpenSearchOptions options,
    TimeProvider timeProvider,
    ILogger<IndexManager> logger) : IIndexManager, IWorkspaceSearchPlacement
{
    private const string AlreadyExists = "resource_already_exists_exception";

    private readonly ConcurrentDictionary<Guid, (WorkspaceIndexPlacement Placement, DateTimeOffset Expires)> _cache = new();
    private readonly ConcurrentDictionary<int, bool> _verifiedTemplates = new();

    private IndexNames Names => templates.Names;

    private ProjectionMappings Mappings => templates.Mappings;

    public async Task<Placement> ResolveAsync(Guid workspaceId, IndexPurpose purpose, CancellationToken cancellationToken = default)
    {
        var record = await GetRecordAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (record is not null)
        {
            return ToPlacement(record);
        }

        return purpose == IndexPurpose.Write
            ? await PlaceAsync(workspaceId, WorkspacePlacementRequest.Default, cancellationToken).ConfigureAwait(false)
            : throw new WorkspaceNotPlacedException(workspaceId);
    }

    public async Task<Placement> PlaceAsync(Guid workspaceId, WorkspacePlacementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireWorkspace(workspaceId);
        if (await store.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return ToPlacement(Remember(existing));
        }

        var decision = PlacementPolicy.Decide(options, request);
        var generation = Mappings.CurrentGeneration;
        SharedIndexPool? pool = null;
        if (decision.Kind == IndexPlacementKind.Shared)
        {
            pool = await ReservePoolAsync(generation, decision.EstimatedBytes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var alias = Names.DedicatedAlias(workspaceId);
            await EnsureIndexAsync(IndexNames.Physical(alias, generation), generation, decision.PrimaryShards, alias,
                routingRequired: false, bulkLoad: false, cancellationToken).ConfigureAwait(false);
        }

        var (stored, created) = await store.InsertAsync(
            new WorkspaceIndexPlacement
            {
                WorkspaceId = workspaceId,
                Kind = decision.Kind,
                SharedPool = pool?.PoolNumber,
                Generation = generation,
                PrimaryShards = pool?.PrimaryShards ?? decision.PrimaryShards,
                DedicatedRequested = request.DedicatedIndex,
                EstimatedDocuments = Math.Max(0, request.ExpectedDocuments),
                EstimatedBytes = decision.EstimatedBytes,
            },
            cancellationToken).ConfigureAwait(false);

        if (created)
        {
            LogPlaced(logger, workspaceId, decision.Kind, generation, pool?.PoolNumber);
        }
        else if (pool is not null)
        {
            // Lost a race with a concurrent placement: give the reservation back.
            await ReleasePoolAsync(pool.PoolNumber, decision.EstimatedBytes, cancellationToken).ConfigureAwait(false);
        }

        return ToPlacement(Remember(stored));
    }

    public async Task<Placement> BeginRebuildAsync(Guid workspaceId, IndexRebuildRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await store.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkspaceNotPlacedException(workspaceId);
        if (current.State != IndexPlacementState.Active)
        {
            throw new IndexPlacementConflictException($"Workspace {workspaceId} is {current.State}; only an active placement can be rebuilt.");
        }

        var kind = request.Kind ?? current.Kind;
        var generation = request.Generation ?? Mappings.CurrentGeneration;
        if (current.Kind == IndexPlacementKind.Dedicated && kind == IndexPlacementKind.Shared)
        {
            throw new InvalidOperationException("Dedicated placements are never demoted to shared (ADR-006 R4).");
        }

        if (!Mappings.Generations.Contains(generation))
        {
            throw new InvalidOperationException($"Projection generation {generation} is not known to this build.");
        }

        if (kind == current.Kind && generation == current.Generation)
        {
            throw new InvalidOperationException(
                "A rebuild needs a new projection generation or a move to a dedicated index (same-generation targets share the physical name).");
        }

        SharedIndexPool? pool = null;
        int shards;
        if (kind == IndexPlacementKind.Shared)
        {
            pool = await ReservePoolAsync(generation, current.EstimatedBytes, cancellationToken).ConfigureAwait(false);
            shards = pool.PrimaryShards;
        }
        else
        {
            shards = request.PrimaryShards ?? PlacementPolicy.DedicatedPrimaryShards(options.Placement, current.EstimatedBytes);
            await EnsureIndexAsync(IndexNames.Physical(Names.DedicatedAlias(workspaceId), generation), generation, shards, alias: null,
                routingRequired: false, bulkLoad: true, cancellationToken).ConfigureAwait(false);
        }

        var stored = await store.UpdateAsync(
            current with
            {
                State = kind == current.Kind ? IndexPlacementState.Building : IndexPlacementState.Moving,
                PendingKind = kind,
                PendingSharedPool = pool?.PoolNumber,
                PendingGeneration = generation,
                PendingPrimaryShards = shards,
            },
            cancellationToken).ConfigureAwait(false);
        Forget(workspaceId);
        if (stored is null)
        {
            await DropTargetAsync(workspaceId, kind, pool?.PoolNumber, generation, current.EstimatedBytes, cancellationToken).ConfigureAwait(false);
            throw new IndexPlacementConflictException($"The placement of workspace {workspaceId} changed concurrently.");
        }

        LogRebuildStarted(logger, workspaceId, stored.State, kind, generation);
        return ToPlacement(stored);
    }

    public async Task<Placement> CompleteRebuildAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var current = await store.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkspaceNotPlacedException(workspaceId);
        if (current.State == IndexPlacementState.Active)
        {
            return ToPlacement(Remember(current));
        }

        var (kind, pool, generation, shards) = Pending(current);
        var targetAlias = Names.Alias(workspaceId, kind, pool);
        var targetIndex = IndexNames.Physical(targetAlias, generation);

        if (kind == IndexPlacementKind.Dedicated)
        {
            await connection.SendAsync(HttpMethod.Put, $"{Escape(targetIndex)}/_settings",
                Obj(("index", Obj(("refresh_interval", options.RefreshInterval), ("number_of_replicas", options.Replicas)))),
                cancellationToken).ConfigureAwait(false);
            await connection.SendAsync(HttpMethod.Get,
                $"_cluster/health/{Escape(targetIndex)}?wait_for_status=yellow&timeout=60s", null, cancellationToken).ConfigureAwait(false);
            await connection.SendAsync(HttpMethod.Post, $"{Escape(targetIndex)}/_refresh", null, cancellationToken).ConfigureAwait(false);
            await SwitchAliasAsync(targetAlias, targetIndex, cancellationToken).ConfigureAwait(false);
            if (current.Kind == IndexPlacementKind.Dedicated)
            {
                // The replaced generation stays readable for rollback; the reindex job deletes it after 24 h (R13 step 7).
                var oldIndex = IndexNames.Physical(Names.DedicatedAlias(workspaceId), current.Generation);
                await connection.SendAsync(HttpMethod.Put, $"{Escape(oldIndex)}/_settings",
                    Obj(("index", Obj(("blocks", Obj(("write", true)))))), cancellationToken, HttpStatusCode.NotFound).ConfigureAwait(false);
            }
        }
        else
        {
            await connection.SendAsync(HttpMethod.Post, $"{Escape(targetIndex)}/_refresh", null, cancellationToken).ConfigureAwait(false);
        }

        var stored = await store.UpdateAsync(
            ClearPending(current) with { Kind = kind, SharedPool = pool, Generation = generation, PrimaryShards = shards },
            cancellationToken).ConfigureAwait(false);
        Forget(workspaceId);
        if (stored is null)
        {
            throw new IndexPlacementConflictException($"The placement of workspace {workspaceId} changed concurrently.");
        }

        if (current.Kind == IndexPlacementKind.Shared)
        {
            var leftPool = current.SharedPool!.Value;
            await ReleasePoolAsync(leftPool, current.EstimatedBytes, cancellationToken).ConfigureAwait(false);
            await DeleteWorkspaceDocumentsAsync(
                IndexNames.Physical(Names.SharedAlias(leftPool), current.Generation), workspaceId, cancellationToken).ConfigureAwait(false);
        }

        LogRebuildCompleted(logger, workspaceId, kind, generation);
        return ToPlacement(Remember(stored));
    }

    public async Task<Placement> AbortRebuildAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var current = await store.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkspaceNotPlacedException(workspaceId);
        if (current.State == IndexPlacementState.Active)
        {
            return ToPlacement(Remember(current));
        }

        var (kind, pool, generation, _) = Pending(current);
        var stored = await store.UpdateAsync(ClearPending(current), cancellationToken).ConfigureAwait(false);
        Forget(workspaceId);
        if (stored is null)
        {
            throw new IndexPlacementConflictException($"The placement of workspace {workspaceId} changed concurrently.");
        }

        await DropTargetAsync(workspaceId, kind, pool, generation, current.EstimatedBytes, cancellationToken).ConfigureAwait(false);
        LogRebuildAborted(logger, workspaceId);
        return ToPlacement(Remember(stored));
    }

    async Task<WorkspaceSearchPlacementInfo> IWorkspaceSearchPlacement.PlaceAsync(
        Guid workspaceId, WorkspacePlacementRequest request, CancellationToken cancellationToken) =>
        ToInfo(await PlaceAsync(workspaceId, request, cancellationToken).ConfigureAwait(false));

    async Task<WorkspaceSearchPlacementInfo?> IWorkspaceSearchPlacement.GetAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await GetRecordAsync(workspaceId, cancellationToken).ConfigureAwait(false) is { } record ? ToInfo(ToPlacement(record)) : null;

    private Placement ToPlacement(WorkspaceIndexPlacement p)
    {
        var current = Target(p.WorkspaceId, p.Kind, p.SharedPool, p.Generation);
        var read = current with { Index = Names.Alias(p.WorkspaceId, p.Kind, p.SharedPool) };
        IndexTarget[] writes = p.State is IndexPlacementState.Building or IndexPlacementState.Moving
            ? [current, Target(p.WorkspaceId, p.PendingKind!.Value, p.PendingSharedPool, p.PendingGeneration!.Value)]
            : [current];
        return new Placement(p.WorkspaceId, p.Kind, p.Generation, p.State, read, writes);
    }

    private IndexTarget Target(Guid workspaceId, IndexPlacementKind kind, int? pool, int generation) => new(
        IndexNames.Physical(Names.Alias(workspaceId, kind, pool), generation),
        kind == IndexPlacementKind.Shared ? workspaceId.ToString("D") : null);

    private static WorkspaceSearchPlacementInfo ToInfo(Placement p) => new(p.WorkspaceId, p.Kind, p.Generation, p.State);

    private static (IndexPlacementKind Kind, int? Pool, int Generation, int Shards) Pending(WorkspaceIndexPlacement p) =>
        (p.PendingKind!.Value, p.PendingSharedPool, p.PendingGeneration!.Value, p.PendingPrimaryShards!.Value);

    private static WorkspaceIndexPlacement ClearPending(WorkspaceIndexPlacement p) => p with
    {
        State = IndexPlacementState.Active,
        PendingKind = null,
        PendingSharedPool = null,
        PendingGeneration = null,
        PendingPrimaryShards = null,
    };

    private async Task<WorkspaceIndexPlacement?> GetRecordAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(workspaceId, out var entry) && entry.Expires > timeProvider.GetUtcNow())
        {
            return entry.Placement;
        }

        var record = await store.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return record is null ? null : Remember(record);
    }

    private WorkspaceIndexPlacement Remember(WorkspaceIndexPlacement placement)
    {
        if (options.Placement.CacheTtl > TimeSpan.Zero)
        {
            _cache[placement.WorkspaceId] = (placement, timeProvider.GetUtcNow() + options.Placement.CacheTtl);
        }

        return placement;
    }

    private void Forget(Guid workspaceId) => _cache.TryRemove(workspaceId, out _);

    /// <summary>R6: the open shared index of <paramref name="generation"/> with the most free capacity, or a new one.</summary>
    private async Task<SharedIndexPool> ReservePoolAsync(int generation, long bytes, CancellationToken cancellationToken)
    {
        var pools = await store.ListSharedPoolsAsync(cancellationToken).ConfigureAwait(false);
        var pool = pools
            .Where(p => !p.Closed && p.Generation == generation)
            .OrderBy(p => p.AssignedBytes)
            .ThenBy(p => p.PoolNumber)
            .FirstOrDefault();
        if (pool is null)
        {
            if (pools.Count >= options.Placement.MaxSharedIndexes)
            {
                throw new InvalidOperationException(
                    $"All {pools.Count} shared indexes are closed or on another generation and MaxSharedIndexes is reached.");
            }

            pool = await store.CreateSharedPoolAsync(generation, options.SharedPrimaryShards, cancellationToken).ConfigureAwait(false);
            LogPoolCreated(logger, pool.PoolNumber, generation, pool.PrimaryShards);
        }

        var alias = Names.SharedAlias(pool.PoolNumber);
        await EnsureIndexAsync(IndexNames.Physical(alias, pool.Generation), pool.Generation, pool.PrimaryShards, alias,
            routingRequired: true, bulkLoad: false, cancellationToken).ConfigureAwait(false);
        await store.AdjustSharedPoolAsync(pool.PoolNumber, 1, bytes, PlacementPolicy.SharedIndexCloseAtBytes(options), cancellationToken)
            .ConfigureAwait(false);
        return pool;
    }

    private Task ReleasePoolAsync(int pool, long bytes, CancellationToken cancellationToken) =>
        store.AdjustSharedPoolAsync(pool, -1, -bytes, PlacementPolicy.SharedIndexCloseAtBytes(options), cancellationToken);

    private async Task DropTargetAsync(
        Guid workspaceId, IndexPlacementKind kind, int? pool, int generation, long bytes, CancellationToken cancellationToken)
    {
        if (kind == IndexPlacementKind.Dedicated)
        {
            var index = IndexNames.Physical(Names.DedicatedAlias(workspaceId), generation);
            await connection.SendAsync(HttpMethod.Delete, Escape(index), null, cancellationToken, HttpStatusCode.NotFound).ConfigureAwait(false);
            return;
        }

        await ReleasePoolAsync(pool!.Value, bytes, cancellationToken).ConfigureAwait(false);
        await DeleteWorkspaceDocumentsAsync(IndexNames.Physical(Names.SharedAlias(pool.Value), generation), workspaceId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Creates the index from its generation's template; an existing index only gets its alias ensured.</summary>
    private async Task EnsureIndexAsync(
        string index, int generation, int primaryShards, string? alias, bool routingRequired, bool bulkLoad, CancellationToken cancellationToken)
    {
        await EnsureTemplateAsync(generation, cancellationToken).ConfigureAwait(false);

        var settings = Obj(("number_of_shards", primaryShards));
        if (bulkLoad)
        {
            // R13 step 1: no refresh and no replicas while the backfill runs; restored on completion.
            settings["refresh_interval"] = "-1";
            settings["number_of_replicas"] = 0;
        }

        var body = Obj(("settings", Obj(("index", settings))));
        if (routingRequired)
        {
            body["mappings"] = Obj(("_routing", Obj(("required", true))));
        }

        if (alias is not null)
        {
            body["aliases"] = Obj((alias, new JsonObject()));
        }

        var response = await connection.SendAsync(HttpMethod.Put, Escape(index), body, cancellationToken, HttpStatusCode.BadRequest)
            .ConfigureAwait(false);
        if (response.Status == HttpStatusCode.BadRequest)
        {
            if (OpenSearchConnection.ErrorType(response.Body) != AlreadyExists)
            {
                throw new OpenSearchRequestException(
                    $"Creating index {index} failed: {OpenSearchConnection.ErrorType(response.Body)} {response.Body?["error"]?["reason"]}");
            }

            if (alias is not null)
            {
                await connection.SendAsync(HttpMethod.Put, $"{Escape(index)}/_alias/{Escape(alias)}", null, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        LogIndexCreated(logger, index, primaryShards);
    }

    private async Task EnsureTemplateAsync(int generation, CancellationToken cancellationToken)
    {
        if (_verifiedTemplates.ContainsKey(generation))
        {
            return;
        }

        if (!await templates.IsInstalledAsync(generation, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The index template for projection generation {generation} is missing or differs from this build; run the migrator.");
        }

        _verifiedTemplates[generation] = true;
    }

    /// <summary>R13 step 6: one atomic <c>_aliases</c> request moves the alias to <paramref name="index"/>; retry-safe.</summary>
    private async Task SwitchAliasAsync(string alias, string index, CancellationToken cancellationToken)
    {
        var holders = await connection.SendAsync(HttpMethod.Get, $"_alias/{Escape(alias)}", null, cancellationToken, HttpStatusCode.NotFound)
            .ConfigureAwait(false);
        var current = holders.Status == HttpStatusCode.NotFound || holders.Body is not JsonObject map
            ? []
            : map.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

        var actions = new JsonArray();
        foreach (var other in current.Where(i => i != index))
        {
            actions.Add(Obj(("remove", Obj(("index", other), ("alias", alias)))));
        }

        if (!current.Contains(index))
        {
            actions.Add(Obj(("add", Obj(("index", index), ("alias", alias)))));
        }

        if (actions.Count > 0)
        {
            await connection.SendAsync(HttpMethod.Post, "_aliases", Obj(("actions", actions)), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Removes one workspace from a shared index (term + routing), asynchronously on the cluster (R13 step 7).</summary>
    private async Task DeleteWorkspaceDocumentsAsync(string index, Guid workspaceId, CancellationToken cancellationToken)
    {
        var routing = workspaceId.ToString("D");
        await connection.SendAsync(HttpMethod.Post,
            $"{Escape(index)}/_delete_by_query?routing={routing}&conflicts=proceed&refresh=true&wait_for_completion=false",
            Obj(("query", Obj(("term", Obj(("workspaceId", routing)))))),
            cancellationToken, HttpStatusCode.NotFound).ConfigureAwait(false);
    }

    private static void RequireWorkspace(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A placement needs a workspace id.", nameof(workspaceId));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Placed workspace {WorkspaceId} {Kind} at generation {Generation} (pool {Pool})")]
    private static partial void LogPlaced(ILogger logger, Guid workspaceId, IndexPlacementKind kind, int generation, int? pool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created shared index pool {Pool} at generation {Generation} with {Shards} primaries")]
    private static partial void LogPoolCreated(ILogger logger, int pool, int generation, int shards);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created index {Index} with {Shards} primaries")]
    private static partial void LogIndexCreated(ILogger logger, string index, int shards);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} is {State} towards {Kind} generation {Generation}")]
    private static partial void LogRebuildStarted(ILogger logger, Guid workspaceId, IndexPlacementState state, IndexPlacementKind kind, int generation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} switched to {Kind} generation {Generation}")]
    private static partial void LogRebuildCompleted(ILogger logger, Guid workspaceId, IndexPlacementKind kind, int generation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rebuild of workspace {WorkspaceId} aborted; the current placement keeps serving")]
    private static partial void LogRebuildAborted(ILogger logger, Guid workspaceId);
}
