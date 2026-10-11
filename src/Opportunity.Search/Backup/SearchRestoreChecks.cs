using System.Net;
using System.Text.Json.Nodes;

using Opportunity.Application.Backup;
using Opportunity.Application.Search.Reindex;
using Opportunity.Search.Indexing;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Backup;

/// <summary>
/// Search gates of the restore tool (design §7.1–§7.3). The comparison is the <c>ReindexValidator</c> access pattern
/// against the live read location instead of a rebuild target: one PostgreSQL page of <c>document_projection_state</c>
/// (tombstones included) per range scan of the index's <c>documentId</c>s with their external versions. With writers
/// stopped (before reopening) a single pass is exact; after reopening run it once the watermark caught up.
/// </summary>
internal sealed class SearchRestoreChecks(IIndexManager indexes, IReindexStore store, OpenSearchConnection connection) : ISearchRestoreChecks
{
    private const int PageSize = 1_000;
    private const int MaxExamples = 20;

    public async Task<SearchPlacementCheck> CheckPlacementAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        Placement placement;
        try
        {
            placement = await indexes.ResolveAsync(workspaceId, IndexPurpose.Read, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceNotPlacedException)
        {
            return new SearchPlacementCheck(workspaceId, Placed: false, IndexExists: false, AliasExists: false, InTransition: false);
        }

        var alias = await ExistsAsync(placement.Read.Index, cancellationToken).ConfigureAwait(false);
        var physical = true;
        foreach (var target in placement.WriteTargets)
        {
            physical &= await ExistsAsync(target.Index, cancellationToken).ConfigureAwait(false);
        }

        return new SearchPlacementCheck(workspaceId, Placed: true, IndexExists: physical, AliasExists: alias,
            InTransition: placement.State is Application.Search.Indexing.IndexPlacementState.Building or Application.Search.Indexing.IndexPlacementState.Moving);
    }

    public async Task<SearchVersionComparison> CompareAsync(
        Guid workspaceId, int? sample, IReadOnlyCollection<Guid> alsoCheck, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alsoCheck);
        var run = new Run(workspaceId, sample is null);
        IndexTarget? target;
        try
        {
            target = (await indexes.ResolveAsync(workspaceId, IndexPurpose.Read, cancellationToken).ConfigureAwait(false)).Read;
        }
        catch (WorkspaceNotPlacedException)
        {
            target = null;
        }

        if (target is not null && !await RefreshAsync(target, cancellationToken).ConfigureAwait(false))
        {
            target = null;
        }

        var (documents, _) = await store.CountDocumentsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var pages = Math.Max(1, (documents + PageSize - 1) / PageSize);
        var stride = sample is { } n ? Math.Max(1, pages / Math.Max(1, n / PageSize)) : 1;

        Guid? after = null;
        for (long page = 0; ; page++)
        {
            var batch = await store.ReadVersionsAsync(workspaceId, after, PageSize, cancellationToken).ConfigureAwait(false);
            var last = batch.Documents.Count < PageSize;
            if (page % stride == 0 || (last && run.Full))
            {
                var indexed = target is null
                    ? []
                    : await ScanAsync(workspaceId, target, after, last ? null : batch.Documents[^1].DocumentId, cancellationToken).ConfigureAwait(false);
                foreach (var document in batch.Documents)
                {
                    run.Classify(document, indexed.Remove(document.DocumentId, out var version) ? version : null);
                }

                // Indexed without any PostgreSQL row in this key range.
                foreach (var orphan in indexed.Keys)
                {
                    run.Ahead++;
                    run.Example(orphan);
                }
            }

            if (last)
            {
                break;
            }

            after = batch.Documents[^1].DocumentId;
        }

        foreach (var ids in alsoCheck.Distinct().Order().Chunk(PageSize))
        {
            var states = await store.ReadVersionsAsync(workspaceId, ids, cancellationToken).ConfigureAwait(false);
            var indexed = target is null ? [] : await GetVersionsAsync(target, ids, cancellationToken).ConfigureAwait(false);
            var byId = states.Documents.ToDictionary(d => d.DocumentId);
            foreach (var id in ids)
            {
                if (byId.TryGetValue(id, out var state))
                {
                    run.Classify(state, indexed.TryGetValue(id, out var v) ? v : null);
                }
                else if (indexed.ContainsKey(id))
                {
                    run.Ahead++;
                    run.Example(id);
                }
            }
        }

        return new SearchVersionComparison(workspaceId, run.Full, run.Compared, run.Ahead, run.Stale, run.Missing, run.Examples);
    }

    private async Task<bool> ExistsAsync(string index, CancellationToken cancellationToken)
    {
        var response = await connection.SendAsync(HttpMethod.Head, Escape(index), null, cancellationToken, HttpStatusCode.NotFound).ConfigureAwait(false);
        return response.Status != HttpStatusCode.NotFound;
    }

    private async Task<bool> RefreshAsync(IndexTarget target, CancellationToken cancellationToken)
    {
        var response = await connection.SendAsync(HttpMethod.Post, $"{Escape(target.Index)}/_refresh", null, cancellationToken, HttpStatusCode.NotFound)
            .ConfigureAwait(false);
        return response.Status != HttpStatusCode.NotFound;
    }

    private async Task<Dictionary<Guid, long>> ScanAsync(Guid workspaceId, IndexTarget target, Guid? lower, Guid? upper, CancellationToken cancellationToken)
    {
        var range = new JsonObject();
        if (lower is { } l)
        {
            range["gt"] = l.ToString("D");
        }

        if (upper is { } u)
        {
            range["lte"] = u.ToString("D");
        }

        var filters = new JsonArray(Obj(("term", Obj(("workspaceId", workspaceId.ToString("D"))))));
        if (range.Count > 0)
        {
            filters.Add(Obj(("range", Obj(("documentId", range)))));
        }

        var found = new Dictionary<Guid, long>();
        JsonArray? searchAfter = null;
        while (true)
        {
            var body = Obj(
                ("size", PageSize),
                ("_source", false),
                ("version", true),
                ("track_total_hits", false),
                ("sort", new JsonArray(Obj(("documentId", "asc")))),
                ("query", Obj(("bool", Obj(("filter", filters.DeepClone()))))));
            if (searchAfter is not null)
            {
                body["search_after"] = searchAfter.DeepClone();
            }

            var response = await connection.SendAsync(HttpMethod.Post, $"{Escape(target.Index)}/_search{Routing(target)}", body, cancellationToken)
                .ConfigureAwait(false);
            var hits = response.Body?["hits"]?["hits"] as JsonArray ?? [];
            foreach (var hit in hits)
            {
                found[Guid.Parse(hit!["_id"]!.GetValue<string>())] = hit["_version"]!.GetValue<long>();
            }

            if (hits.Count < PageSize)
            {
                return found;
            }

            searchAfter = hits[^1]!["sort"]!.AsArray();
        }
    }

    private async Task<Dictionary<Guid, long>> GetVersionsAsync(IndexTarget target, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var body = Obj(("ids", new JsonArray([.. ids.Select(id => JsonValue.Create(id.ToString("D")))])));
        var response = await connection.SendAsync(HttpMethod.Post, $"{Escape(target.Index)}/_mget?_source=false{Routing(target, "&")}", body,
            cancellationToken).ConfigureAwait(false);
        var found = new Dictionary<Guid, long>();
        foreach (var doc in response.Body?["docs"] as JsonArray ?? [])
        {
            if (doc?["found"]?.GetValue<bool>() == true)
            {
                found[Guid.Parse(doc["_id"]!.GetValue<string>())] = doc["_version"]!.GetValue<long>();
            }
        }

        return found;
    }

    private static string Routing(IndexTarget target, string separator = "?") =>
        target.Routing is { } routing ? $"{separator}routing={Uri.EscapeDataString(routing)}" : string.Empty;

    private sealed class Run(Guid workspaceId, bool full)
    {
        public Guid WorkspaceId { get; } = workspaceId;

        public bool Full { get; } = full;

        public long Compared { get; set; }

        public long Ahead { get; set; }

        public long Stale { get; set; }

        public long Missing { get; set; }

        public List<Guid> Examples { get; } = [];

        /// <summary>Design §1: ahead is corruption; behind (stale, missing, an unapplied delete) is lag.</summary>
        public void Classify(DocumentVersionState document, long? indexed)
        {
            Compared++;
            switch (indexed)
            {
                case null when !document.Deleted:
                    Missing++;
                    Example(document.DocumentId);
                    break;
                case null:
                    break;
                case { } v when v > document.Version || (document.Deleted && v >= document.Version):
                    Ahead++;
                    Example(document.DocumentId);
                    break;
                case { } v when v < document.Version:
                    Stale++;
                    Example(document.DocumentId);
                    break;
            }
        }

        public void Example(Guid id)
        {
            if (Examples.Count < MaxExamples)
            {
                Examples.Add(id);
            }
        }
    }
}
