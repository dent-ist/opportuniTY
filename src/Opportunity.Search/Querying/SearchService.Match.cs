using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Opportunity.Application.Authorization;
using Opportunity.Application.Search;
using Opportunity.Contracts.Search;
using Opportunity.Search.Indexing;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Querying;

/// <summary>
/// Matching within a known set of documents (search term reports, E07-T10), and searches opened on a report term's hit
/// set: the term's hits as counted, filtered for the caller, with the term's expression for relevance and highlighting.
/// </summary>
internal sealed partial class SearchService
{
    public async Task<SearchMatchOutcome> MatchAsync(
        SearchCaller caller, string query, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documentIds);
        if (documentIds.Count > ISearchService.MaxMatchDocuments)
        {
            throw new ArgumentOutOfRangeException(nameof(documentIds), documentIds.Count, $"At most {ISearchService.MaxMatchDocuments} documents per call.");
        }

        var visibility = await VisibilityAsync(caller, cancellationToken).ConfigureAwait(false);
        if (visibility.Outcome is { } denied)
        {
            return SearchMatchOutcome.Of(denied.Status == SearchStatus.NotFound ? SearchSelectionStatus.NotFound : SearchSelectionStatus.Forbidden);
        }

        var placement = await PlacementAsync(caller.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var plan = await PlanAsync(caller, query, placement?.Generation, true, cancellationToken).ConfigureAwait(false);
        if (plan.Errors is { } errors)
        {
            return new SearchMatchOutcome(SearchSelectionStatus.InvalidQuery, [], errors);
        }

        if (placement is null || documentIds.Count == 0)
        {
            return SearchMatchOutcome.Of(SearchSelectionStatus.Ok);
        }

        var clause = Obj(("bool", Obj(
            ("filter", new JsonArray(IdsFilter(documentIds))),
            ("must", new JsonArray(plan.Query!.DeepClone())))));
        var body = Obj(
            ("query", SearchDsl.Query(placement.WorkspaceFilterValue, visibility.Filter!, clause)),
            ("size", documentIds.Count),
            ("track_total_hits", false),
            ("_source", false),
            ("docvalue_fields", new JsonArray((JsonNode)ProjectionFields.WorkspaceId, (JsonNode)ProjectionFields.DocumentId)),
            ("timeout", string.Create(CultureInfo.InvariantCulture, $"{(long)Settings.QueryTimeout.TotalMilliseconds}ms")));
        var routing = placement.Read.Routing is { } r ? "?routing=" + Escape(r) : string.Empty;
        var response = await connection.SendAsync(
            HttpMethod.Post, $"{Escape(placement.Read.Index)}/_search{routing}", body, cancellationToken,
            HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError).ConfigureAwait(false);
        if (IsClauseLimit(response))
        {
            return new SearchMatchOutcome(SearchSelectionStatus.InvalidQuery, [], plan.Rejection(SearchQueryErrorCodes.WildcardTooBroad));
        }

        if (response.Status != HttpStatusCode.OK || response.Body is not JsonObject json)
        {
            throw new OpenSearchRequestException(
                $"OpenSearch search failed with {(int)response.Status} {OpenSearchConnection.ErrorType(response.Body)}.");
        }

        if (json["timed_out"]?.GetValue<bool>() == true)
        {
            // ADR-008 §5: partial results are never counted as complete.
            return new SearchMatchOutcome(SearchSelectionStatus.InvalidQuery, [], plan.Rejection(SearchQueryErrorCodes.QueryTimeout));
        }

        if ((json["_shards"]?["failed"]?.GetValue<int>() ?? 0) > 0)
        {
            throw new OpenSearchRequestException("A match request missed shards; it is retried rather than counted short.");
        }

        var hits = json["hits"]?["hits"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var dropped = new Dictionary<string, long>(StringComparer.Ordinal);
        var allowed = await SelectionPostFilterAsync(caller, placement, hits, dropped, cancellationToken).ConfigureAwait(false);
        return new SearchMatchOutcome(SearchSelectionStatus.Ok, allowed, []);
    }

    /// <summary>A <c>terms</c> filter on <c>documentId</c> (the IDs as projected, <c>Guid "D"</c>).</summary>
    private static JsonObject IdsFilter(IEnumerable<Guid> documentIds) =>
        Obj(("terms", Obj((ProjectionFields.DocumentId, new JsonArray([.. documentIds.Select(id => (JsonNode)id.ToString("D"))])))));

    /// <summary>
    /// The user clause of a search opened on a term's hit set: the hit set is the filter (exactly the documents counted),
    /// the term's expression only scores and highlights them.
    /// </summary>
    private static JsonObject TermScoped(JsonObject userClause, IReadOnlyList<Guid> hits) => Obj(("bool", Obj(
        ("filter", new JsonArray(IdsFilter(hits))),
        ("should", new JsonArray(userClause.DeepClone())))));

    private sealed record TermScope(Guid ReportId, Guid TermId, string Expression, IReadOnlyList<Guid> DocumentIds)
    {
        public string Json => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["searchTermReportId"] = ReportId.ToString("D"),
            ["termId"] = TermId.ToString("D"),
        });

        public static (Guid ReportId, Guid TermId)? Parse(string json)
        {
            var node = JsonNode.Parse(json) as JsonObject;
            return Guid.TryParse(node?["searchTermReportId"]?.GetValue<string>(), out var report)
                && Guid.TryParse(node?["termId"]?.GetValue<string>(), out var term)
                    ? (report, term)
                    : null;
        }
    }

    private async Task<(SearchOutcome? Outcome, TermScope? Scope)> ResolveTermAsync(
        SearchCaller caller, Guid reportId, Guid termId, CancellationToken cancellationToken)
    {
        if (termHits is null || await termHits.GetAsync(caller, reportId, termId, cancellationToken).ConfigureAwait(false) is not { } set)
        {
            return (SearchOutcome.NotFound, null);
        }

        return set.Status switch
        {
            SearchTermHitSetStatus.Ok => (null, new TermScope(reportId, termId, set.Expression, set.DocumentIds)),
            SearchTermHitSetStatus.NotCompleted => (SearchOutcome.InvalidRequest("searchTermReportId", "The report has not completed; open its terms once it has."), null),
            SearchTermHitSetStatus.TermError => (SearchOutcome.InvalidRequest("termId", "The term has an error and no hits."), null),
            _ => (SearchOutcome.InvalidRequest("termId",
                $"The term hits more than {SearchTermHitSet.MaxDocuments.ToString("N0", CultureInfo.InvariantCulture)} documents, too many to open as a search; search its expression instead."), null),
        };
    }
}
