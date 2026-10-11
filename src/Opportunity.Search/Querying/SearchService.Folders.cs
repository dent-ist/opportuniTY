using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using Opportunity.Application.Documents;
using Opportunity.Application.Search;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Querying;

/// <summary>
/// Folder counts of the Folders tree (#179): one size-0 aggregation over the read alias under the outer filter of every
/// search (workspace term, denied restriction classes and walls), so a document the caller may not see is never counted.
/// </summary>
internal sealed partial class SearchService : IDocumentFolderCounts
{
    public async Task<DocumentFolderCountOutcome> CountAsync(
        SearchCaller caller, IReadOnlyCollection<Guid> folderIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(folderIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(folderIds.Count, DocumentFolderService.MaxPageSize, nameof(folderIds));
        var visibility = await VisibilityAsync(caller, cancellationToken).ConfigureAwait(false);
        if (visibility.Outcome is { } denied)
        {
            return DocumentFolderCountOutcome.Of(
                denied.Status == SearchStatus.NotFound ? DocumentFolderCountStatus.NotFound : DocumentFolderCountStatus.Forbidden);
        }

        var placement = await PlacementAsync(caller.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var ids = folderIds.Distinct().Select(id => id.ToString("D")).ToList();
        if (placement is null || ids.Count == 0)
        {
            return new DocumentFolderCountOutcome(DocumentFolderCountStatus.Ok, new Dictionary<Guid, DocumentFolderCount>());
        }

        var watermark = await freshnessReader.ReadAsync(caller.WorkspaceId, cancellationToken).ConfigureAwait(false);
        JsonArray Include() => new([.. ids.Select(id => (JsonNode)id)]);
        var clause = Obj(("terms", Obj((ProjectionFieldPaths.FolderIds, Include()))));
        var body = Obj(
            ("query", SearchDsl.Query(placement.WorkspaceFilterValue, visibility.Filter!, clause)),
            ("size", 0),
            ("track_total_hits", false),
            ("aggs", Obj(
                ("direct", Obj(("terms", Obj(("field", ProjectionFieldPaths.FolderId), ("include", Include()), ("size", ids.Count))))),
                ("total", Obj(("terms", Obj(("field", ProjectionFieldPaths.FolderIds), ("include", Include()), ("size", ids.Count))))))),
            ("timeout", string.Create(CultureInfo.InvariantCulture, $"{(long)Settings.QueryTimeout.TotalMilliseconds}ms")));
        var routing = placement.Read.Routing is { } r ? "?routing=" + Escape(r) : string.Empty;
        var response = await connection.SendAsync(
            HttpMethod.Post, $"{Escape(placement.Read.Index)}/_search{routing}", body, cancellationToken).ConfigureAwait(false);
        if (response.Status != HttpStatusCode.OK || response.Body is not JsonObject json
            || json["timed_out"]?.GetValue<bool>() == true || (json["_shards"]?["failed"]?.GetValue<int>() ?? 0) > 0)
        {
            // Counts are never shown short: a partial answer is a failure, like any search (ADR-008 §5).
            throw new OpenSearchRequestException("A folder count missed shards or timed out.");
        }

        var direct = Buckets(json["aggregations"]?["direct"]);
        var total = Buckets(json["aggregations"]?["total"]);
        var counts = new Dictionary<Guid, DocumentFolderCount>();
        foreach (var id in folderIds.Distinct())
        {
            var key = id.ToString("D");
            counts[id] = new DocumentFolderCount(direct.GetValueOrDefault(key), total.GetValueOrDefault(key));
        }

        return new DocumentFolderCountOutcome(DocumentFolderCountStatus.Ok, counts, !watermark.IsCurrent);
    }

    private static Dictionary<string, long> Buckets(JsonNode? aggregation) =>
        (aggregation?["buckets"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .ToDictionary(b => b["key"]!.GetValue<string>(), b => b["doc_count"]!.GetValue<long>(), StringComparer.Ordinal);
}
