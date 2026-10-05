using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using Opportunity.Application.Authorization;
using Opportunity.Core.Documents;
using Opportunity.Search.Indexing;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Querying;

/// <summary>Family, duplicate and thread expansion of interactive searches (E09-T03, <see cref="RelationshipExpansionQuery"/>).</summary>
internal sealed partial class SearchService
{
    private const string KeysAggregation = "keys";

    /// <summary>
    /// Collects the relationship keys of the base hits under <paramref name="pointInTimeId"/> and returns the expanded
    /// query (outer filter included) with the count aggregations. Every page re-collects them under its own reader, so a
    /// page and its keys are always of the same point in time.
    /// </summary>
    /// <exception cref="ExpansionTooLargeException">A kind has more than <see cref="SearchServiceOptions.MaxExpansionKeys"/> keys.</exception>
    /// <exception cref="ReaderGoneException">The reader expired or its index went away.</exception>
    private async Task<ExpandedQuery> ExpandAsync(
        Placement placement, VisibilityFilter visibility, JsonObject userQuery, RelationshipExpansion expansion, string pointInTimeId,
        CancellationToken cancellationToken)
    {
        JsonObject Outer(JsonObject clause) => SearchDsl.Query(placement.WorkspaceFilterValue, visibility, clause);

        var baseQuery = Outer(userQuery);
        var family = expansion.Family ? await CollectKeysAsync(baseQuery, ProjectionFields.FamilyId, pointInTimeId, cancellationToken).ConfigureAwait(false) : [];
        var duplicates = expansion.Duplicates
            ? await CollectKeysAsync(baseQuery, ProjectionFields.DuplicateGroupId, pointInTimeId, cancellationToken).ConfigureAwait(false)
            : [];
        var thread = expansion.Thread ? await CollectKeysAsync(baseQuery, ProjectionFields.EmailThreadId, pointInTimeId, cancellationToken).ConfigureAwait(false) : [];
        HashSet<string> familyOfExpanded = [];
        if (expansion.FamilyOfExpanded && RelationshipExpansionQuery.AddedByDuplicateOrThread(userQuery, duplicates, thread) is { } added)
        {
            familyOfExpanded = await CollectKeysAsync(Outer(added), ProjectionFields.FamilyId, pointInTimeId, cancellationToken).ConfigureAwait(false);
            familyOfExpanded.ExceptWith(family);
        }

        var keys = new RelationshipExpansionQuery.Keys(family, duplicates, thread, familyOfExpanded);
        return new ExpandedQuery(
            Outer(RelationshipExpansionQuery.Combined(userQuery, keys)),
            RelationshipExpansionQuery.CountAggregations(userQuery, keys),
            family.Count + duplicates.Count + thread.Count + familyOfExpanded.Count);
    }

    /// <summary>The distinct values of <paramref name="field"/> over <paramref name="query"/>, paged with a composite aggregation.</summary>
    private async Task<HashSet<string>> CollectKeysAsync(JsonObject query, string field, string pointInTimeId, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        JsonNode? after = null;
        while (true)
        {
            var composite = Obj(
                ("size", Settings.ExpansionKeyPageSize),
                ("sources", new JsonArray(Obj(("k", Obj(("terms", Obj(("field", field)))))))));
            if (after is not null)
            {
                composite["after"] = after.DeepClone();
            }

            var body = Obj(
                ("size", 0),
                ("track_total_hits", false),
                ("query", query.DeepClone()),
                ("aggs", Obj((KeysAggregation, Obj(("composite", composite))))),
                ("timeout", string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (long)Settings.QueryTimeout.TotalMilliseconds)}ms")),
                ("pit", Obj(("id", pointInTimeId), ("keep_alive", SearchDsl.Seconds(Settings.PointInTimeKeepAlive)))));
            var response = await connection.SendAsync(
                HttpMethod.Post, "_search", body, cancellationToken, HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError)
                .ConfigureAwait(false);
            if (IsPointInTimeGone(response))
            {
                throw new ReaderGoneException();
            }

            if (response.Status != HttpStatusCode.OK || response.Body is not JsonObject json)
            {
                throw new OpenSearchRequestException(
                    $"OpenSearch expansion failed with {(int)response.Status} {OpenSearchConnection.ErrorType(response.Body)}.");
            }

            if (json["timed_out"]?.GetValue<bool>() == true || (json["_shards"]?["failed"]?.GetValue<int>() ?? 0) > 0)
            {
                // Missing keys would silently shrink the expansion: never presented as complete.
                throw new QueryRejectedException(SearchQueryErrorCodes.QueryTimeout);
            }

            var aggregation = json["aggregations"]?[KeysAggregation];
            var buckets = aggregation?["buckets"] as JsonArray ?? [];
            foreach (var bucket in buckets)
            {
                if (bucket?["key"]?["k"] is JsonValue value && value.TryGetValue<string>(out var key))
                {
                    keys.Add(key);
                }
            }

            if (keys.Count > Settings.MaxExpansionKeys)
            {
                throw new ExpansionTooLargeException(field);
            }

            after = aggregation?["after_key"];
            if (buckets.Count < Settings.ExpansionKeyPageSize || after is null)
            {
                return keys;
            }
        }
    }

    /// <summary>The expanded query (outer filter included), its count aggregations and how many keys it carries.</summary>
    private sealed record ExpandedQuery(JsonObject Query, JsonObject Aggregations, int KeyCount);

    /// <summary>The reader of a page went away while its expansion keys were collected (handled like an expired reader).</summary>
    private sealed class ReaderGoneException() : Exception("The point-in-time reader is gone.");
}

/// <summary>An interactive expansion would need more keys than allowed; answered 400 on <c>expand</c>.</summary>
internal sealed class ExpansionTooLargeException(string field) : Exception($"Too many {field} keys to expand interactively.")
{
    public string Field { get; } = field;
}
