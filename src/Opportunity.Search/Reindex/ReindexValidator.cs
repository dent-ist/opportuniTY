using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Search;
using Opportunity.Application.Search.Reindex;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Reindex;

/// <summary>
/// ADR-006 R13 step 5 for the rebuild target of a placement, while writes continue: the target is compared with
/// PostgreSQL page by page (one snapshot of <c>document_projection_state</c>, one range scan of the target's
/// <c>documentId</c>s and external versions), so missing, stale and unexpected documents are all found. A document that
/// disagrees may simply be in flight; it is checked again once the applied watermark covers everything committed before
/// the re-read and the target was refreshed — still disagreeing there means lost work. For documents whose versions
/// agree, the projection rebuilt from PostgreSQL must equal the target's stored source (the projection hash). Up to
/// <see cref="ReindexOptions.FullCheckMaxDocuments"/> every document is checked, above that
/// <see cref="ReindexOptions.SampledPages"/> evenly spread pages plus an exact count comparison.
/// </summary>
internal sealed partial class ReindexValidator(
    IReindexStore store,
    OpenSearchConnection connection,
    IProjectionService projections,
    ISearchFreshnessReader freshness,
    ReindexOptions options,
    TimeProvider time,
    ILogger<ReindexValidator> logger)
{
    private const int MaxExamples = 20;
    private const int HashBatch = 100;

    public async Task<ReindexValidation> ValidateAsync(Placement placement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var target = placement.RebuildTarget ?? throw new InvalidOperationException("The placement is not rebuilding.");
        var run = new Run(placement.WorkspaceId, target, target.Generation == projections.Generation);

        await RefreshAsync(target, cancellationToken).ConfigureAwait(false);
        var (pgDocuments, _) = await store.CountDocumentsAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var full = pgDocuments <= options.FullCheckMaxDocuments;
        var pages = Math.Max(1, (pgDocuments + options.ValidationPageSize - 1) / options.ValidationPageSize);
        var stride = full ? 1 : Math.Max(1, pages / options.SampledPages);

        Guid? after = null;
        for (long page = 0; ; page++)
        {
            var batch = await store.ReadVersionsAsync(run.WorkspaceId, after, options.ValidationPageSize, cancellationToken).ConfigureAwait(false);
            var last = batch.Documents.Count < options.ValidationPageSize;
            if (batch.Documents.Count > 0 && (page % stride == 0 || (last && full)))
            {
                // The final page has no upper bound, so documents the target holds beyond the last key are seen too.
                await CompareRangeAsync(run, batch.Documents, after, last ? null : batch.Documents[^1].DocumentId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (batch.Documents.Count == 0 && full)
            {
                await CompareRangeAsync(run, [], after, null, cancellationToken).ConfigureAwait(false);
            }

            if (run.Suspects.Count > options.MaxRechecks)
            {
                break;
            }

            if (last)
            {
                break;
            }

            after = batch.Documents[^1].DocumentId;
        }

        await HashAsync(run, flush: true, cancellationToken).ConfigureAwait(false);

        string? failure = null;
        if (run.Suspects.Count > options.MaxRechecks)
        {
            failure = string.Create(CultureInfo.InvariantCulture,
                $"More than {options.MaxRechecks} documents disagree with the database; the new index is incomplete.");
        }
        else if (run.Suspects.Count > 0 && !await RecheckAsync(run, cancellationToken).ConfigureAwait(false))
        {
            failure = string.Create(CultureInfo.InvariantCulture,
                $"Search work did not catch up within {options.RecheckTimeout.TotalSeconds:F0} s, so {run.Suspects.Count} disagreeing documents could not be re-checked.");
        }

        var (pgCount, indexCount) = await CountAsync(run, full, cancellationToken).ConfigureAwait(false);
        if (failure is null)
        {
            if (run.Stale + run.Missing + run.Unexpected > 0)
            {
                failure = string.Create(CultureInfo.InvariantCulture,
                    $"The new index disagrees with the database: {run.Stale} stale, {run.Missing} missing, {run.Unexpected} unexpected document(s).");
            }
            else if (run.HashMismatches > 0)
            {
                failure = string.Create(CultureInfo.InvariantCulture,
                    $"{run.HashMismatches} document(s) in the new index differ from their projection rebuilt from the database.");
            }
            else if (!full && pgCount != indexCount)
            {
                failure = string.Create(CultureInfo.InvariantCulture,
                    $"The new index holds {indexCount} documents, the database {pgCount} live documents.");
            }
        }

        var result = new ReindexValidation
        {
            PostgresDocuments = pgCount,
            IndexDocuments = indexCount,
            FullCheck = full,
            DocumentsCompared = run.Compared,
            Rechecked = run.Rechecked,
            StaleDocuments = run.Stale,
            MissingDocuments = run.Missing,
            UnexpectedDocuments = run.Unexpected,
            HashesCompared = run.Hashed,
            HashMismatches = run.HashMismatches,
            Examples = [.. run.Examples],
            Failure = failure,
        };
        LogValidated(logger, run.WorkspaceId, failure is null, run.Compared, run.Hashed, failure);
        return result;
    }

    /// <summary>Compares one page of PostgreSQL states with the target's documents in the key range (lower, upper].</summary>
    private async Task CompareRangeAsync(
        Run run, IReadOnlyList<DocumentVersionState> documents, Guid? lower, Guid? upper, CancellationToken cancellationToken)
    {
        var indexed = await ScanAsync(run, lower, upper, cancellationToken).ConfigureAwait(false);
        foreach (var document in documents)
        {
            run.Compared++;
            var found = indexed.Remove(document.DocumentId, out var version);
            if (document.Deleted ? found : !found || version < document.Version)
            {
                run.Suspects.Add(document.DocumentId);
            }
            else if (found && version == document.Version && run.Hashable)
            {
                run.ToHash.Add((document.DocumentId, version));
            }
        }

        // Indexed but not in this PostgreSQL page: created after the snapshot, or a document that should not be there.
        run.Suspects.UnionWith(indexed.Keys);
        await HashAsync(run, flush: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disagreeing documents again, once everything committed before the re-read is applied (applied watermark) and the
    /// target refreshed: then the target must hold at least the version PostgreSQL holds, and nothing deleted.
    /// </summary>
    private async Task<bool> RecheckAsync(Run run, CancellationToken cancellationToken)
    {
        foreach (var ids in run.Suspects.Order().Chunk(options.ValidationPageSize))
        {
            var states = await store.ReadVersionsAsync(run.WorkspaceId, ids, cancellationToken).ConfigureAwait(false);
            if (!await WaitAppliedAsync(run.WorkspaceId, states.Generation, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await RefreshAsync(run.Target, cancellationToken).ConfigureAwait(false);
            var indexed = await GetVersionsAsync(run.Target, ids, cancellationToken).ConfigureAwait(false);
            var byId = states.Documents.ToDictionary(d => d.DocumentId);
            foreach (var id in ids)
            {
                run.Rechecked++;
                var live = byId.TryGetValue(id, out var state) && !state.Deleted;
                var found = indexed.TryGetValue(id, out var version);
                if (!live && found)
                {
                    run.Unexpected++;
                    run.Example(id);
                }
                else if (live && !found)
                {
                    run.Missing++;
                    run.Example(id);
                }
                else if (live && version < state.Version)
                {
                    run.Stale++;
                    run.Example(id);
                }
            }
        }

        run.Suspects.Clear();
        return true;
    }

    /// <summary>The projection rebuilt from PostgreSQL equals the target's stored source (documents whose versions agree).</summary>
    private async Task HashAsync(Run run, bool flush, CancellationToken cancellationToken)
    {
        while (run.ToHash.Count >= HashBatch || (flush && run.ToHash.Count > 0))
        {
            var batch = run.ToHash.Take(HashBatch).ToList();
            run.ToHash.RemoveRange(0, batch.Count);
            var built = await projections.BuildAsync(run.WorkspaceId, [.. batch.Select(b => b.DocumentId)], cancellationToken).ConfigureAwait(false);
            var sources = await GetSourcesAsync(run.Target, [.. batch.Select(b => b.DocumentId)], cancellationToken).ConfigureAwait(false);
            var expected = batch.ToDictionary(b => b.DocumentId, b => b.Version);
            foreach (var document in built)
            {
                // Changed since the comparison: the version check covers it.
                if (document.Version != expected[document.DocumentId] || !sources.TryGetValue(document.DocumentId, out var stored)
                    || stored.Version != document.Version)
                {
                    continue;
                }

                var body = document.Writes.FirstOrDefault(w => w.Kind == ProjectionWriteKind.Index && w.Id == document.DocumentId.ToString("D"))?.Body;
                if (body is null)
                {
                    continue;
                }

                run.Hashed++;
                if (!JsonNode.DeepEquals(JsonNode.Parse(body.ToJsonString()), stored.Source))
                {
                    run.HashMismatches++;
                    run.Example(document.DocumentId);
                }
            }
        }
    }

    private async Task<(long Postgres, long Index)> CountAsync(Run run, bool full, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (documents, generation) = await store.CountDocumentsAsync(run.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (!await WaitAppliedAsync(run.WorkspaceId, generation, cancellationToken).ConfigureAwait(false))
            {
                return (documents, await CountIndexAsync(run, cancellationToken).ConfigureAwait(false));
            }

            await RefreshAsync(run.Target, cancellationToken).ConfigureAwait(false);
            var indexed = await CountIndexAsync(run, cancellationToken).ConfigureAwait(false);

            // Work committed after the count can move either number; a few attempts settle on a quiet moment.
            if (indexed == documents || full || attempt == 3)
            {
                return (documents, indexed);
            }
        }
    }

    private async Task<bool> WaitAppliedAsync(Guid workspaceId, long generation, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        while (true)
        {
            var reading = await freshness.ReadAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            if (reading.AppliedGeneration >= generation)
            {
                return true;
            }

            if (time.GetElapsedTime(started) > options.RecheckTimeout)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<Guid, long>> ScanAsync(Run run, Guid? lower, Guid? upper, CancellationToken cancellationToken)
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

        var filters = new JsonArray(Obj(("term", Obj(("workspaceId", run.WorkspaceId.ToString("D"))))));
        if (range.Count > 0)
        {
            filters.Add(Obj(("range", Obj(("documentId", range)))));
        }

        var found = new Dictionary<Guid, long>();
        JsonArray? searchAfter = null;
        while (true)
        {
            var body = Obj(
                ("size", options.ValidationPageSize),
                ("_source", false),
                ("version", true),
                ("track_total_hits", false),
                ("sort", new JsonArray(Obj(("documentId", "asc")))),
                ("query", Obj(("bool", Obj(("filter", filters.DeepClone()))))));
            if (searchAfter is not null)
            {
                body["search_after"] = searchAfter.DeepClone();
            }

            var response = await connection.SendAsync(HttpMethod.Post, $"{Escape(run.Target.Index)}/_search{Routing(run.Target)}", body, cancellationToken)
                .ConfigureAwait(false);
            var hits = response.Body?["hits"]?["hits"] as JsonArray ?? [];
            foreach (var hit in hits)
            {
                found[Guid.Parse(hit!["_id"]!.GetValue<string>())] = hit["_version"]!.GetValue<long>();
            }

            if (hits.Count < options.ValidationPageSize)
            {
                return found;
            }

            searchAfter = hits[^1]!["sort"]!.AsArray();
        }
    }

    private async Task<Dictionary<Guid, long>> GetVersionsAsync(IndexTarget target, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        (await MultiGetAsync(target, ids, source: false, cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.Key, d => d.Value.Version);

    private Task<Dictionary<Guid, (long Version, JsonNode? Source)>> GetSourcesAsync(
        IndexTarget target, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        MultiGetAsync(target, ids, source: true, cancellationToken);

    private async Task<Dictionary<Guid, (long Version, JsonNode? Source)>> MultiGetAsync(
        IndexTarget target, IReadOnlyList<Guid> ids, bool source, CancellationToken cancellationToken)
    {
        var body = Obj(("ids", new JsonArray([.. ids.Select(id => JsonValue.Create(id.ToString("D")))])));
        var response = await connection.SendAsync(HttpMethod.Post,
            $"{Escape(target.Index)}/_mget?_source={(source ? "true" : "false")}{Routing(target, "&")}", body, cancellationToken)
            .ConfigureAwait(false);
        var found = new Dictionary<Guid, (long, JsonNode?)>();
        foreach (var doc in response.Body?["docs"] as JsonArray ?? [])
        {
            if (doc?["found"]?.GetValue<bool>() == true)
            {
                found[Guid.Parse(doc["_id"]!.GetValue<string>())] = (doc["_version"]!.GetValue<long>(), doc["_source"]?.DeepClone());
            }
        }

        return found;
    }

    private async Task<long> CountIndexAsync(Run run, CancellationToken cancellationToken)
    {
        var response = await connection.SendAsync(HttpMethod.Post, $"{Escape(run.Target.Index)}/_count{Routing(run.Target)}",
            Obj(("query", Obj(("term", Obj(("workspaceId", run.WorkspaceId.ToString("D"))))))), cancellationToken).ConfigureAwait(false);
        return response.Body?["count"]?.GetValue<long>() ?? 0;
    }

    private async Task RefreshAsync(IndexTarget target, CancellationToken cancellationToken)
    {
        var response = await connection.SendAsync(HttpMethod.Post, $"{Escape(target.Index)}/_refresh", null, cancellationToken, HttpStatusCode.NotFound)
            .ConfigureAwait(false);
        if (response.Status == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("The rebuild target index does not exist.");
        }
    }

    private static string Routing(IndexTarget target, string separator = "?") =>
        target.Routing is { } routing ? $"{separator}routing={Uri.EscapeDataString(routing)}" : string.Empty;

    private sealed class Run(Guid workspaceId, IndexTarget target, bool hashable)
    {
        public Guid WorkspaceId { get; } = workspaceId;

        public IndexTarget Target { get; } = target;

        /// <summary>The target's generation is the one this build projects, so rebuilt projections are comparable.</summary>
        public bool Hashable { get; } = hashable;

        public HashSet<Guid> Suspects { get; } = [];

        public List<(Guid DocumentId, long Version)> ToHash { get; } = [];

        public List<Guid> Examples { get; } = [];

        public long Compared { get; set; }

        public long Rechecked { get; set; }

        public long Stale { get; set; }

        public long Missing { get; set; }

        public long Unexpected { get; set; }

        public long Hashed { get; set; }

        public long HashMismatches { get; set; }

        public void Example(Guid id)
        {
            if (Examples.Count < MaxExamples)
            {
                Examples.Add(id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Reindex validation of workspace {WorkspaceId}: passed {Passed}, {Compared} documents compared, {Hashed} hashes compared; {Failure}")]
    private static partial void LogValidated(ILogger logger, Guid workspaceId, bool passed, long compared, long hashed, string? failure);
}
