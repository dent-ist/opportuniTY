using System.Globalization;
using System.Text.Json.Nodes;

using Opportunity.Application.Authorization;
using Opportunity.Contracts.Search;
using Opportunity.Core.QueryLanguage;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Querying;

/// <summary>A resolved sort key: the API name, the projection path and the direction.</summary>
internal sealed record SortKey(string Field, string Path, bool Descending)
{
    public const string ScorePath = "_score";

    public bool IsScore => Path == ScorePath;

    /// <summary>
    /// The OpenSearch sort clauses of this key. "Date (Family)" sorts by the family date, then the family and its family
    /// sequence (parent first), so families stay contiguous (ADR-009 R25, E09-T03).
    /// </summary>
    public IReadOnlyList<(string Path, bool Descending)> Clauses => Field == SearchSortFields.FamilyDate
        ? [(ProjectionFields.FamilyDate, Descending), (ProjectionFields.FamilyId, Descending), (ProjectionFields.FamilySequence, false)]
        : [(Path, Descending)];

    /// <summary>"Date (Family)" ascending: the default order of an expanded search.</summary>
    public static SortKey Family { get; } = new(SearchSortFields.FamilyDate, ProjectionFields.FamilyDate, Descending: false);

    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        [SearchSortFields.Relevance] = ScorePath,
        ["controlNumber"] = ProjectionFields.ControlNumberSort,
        ["fileName"] = ProjectionFields.FileNameKeyword,
    };

    /// <summary>Resolves an API sort key; null when the field is not sortable.</summary>
    public static SortKey? Resolve(string? field, SearchSortDirection direction)
    {
        var name = SearchSortFields.All.FirstOrDefault(f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : new SortKey(name, Paths.GetValueOrDefault(name, name), direction == SearchSortDirection.Desc);
    }

    /// <summary>Relevance, best first.</summary>
    public static SortKey Default { get; } = new(SearchSortFields.Relevance, ScorePath, Descending: true);

    /// <summary>Control Number ascending (natural order, ADR-009 R5).</summary>
    public static SortKey ControlNumber { get; } = new("controlNumber", ProjectionFields.ControlNumberSort, Descending: false);

    /// <summary>
    /// The sort of a request that gives none: relevance when the query has a keyword (an unfielded term, phrase,
    /// wildcard or proximity outside a NOT), else Control Number ascending. Without a keyword every hit scores alike
    /// (match-all, filters), so relevance order would be arbitrary.
    /// </summary>
    public static SortKey DefaultFor(QueryNode? ast) => ast is not null && HasKeyword(ast) ? Default : ControlNumber;

    private static bool HasKeyword(QueryNode node) => node switch
    {
        TermNode or PhraseNode or WildcardNode or ProximityNode => true,
        AndNode and => and.Children.Any(HasKeyword),
        OrNode or => or.Children.Any(HasKeyword),
        _ => false,
    };

    /// <summary>Stored with the resolved path, so pages sort exactly as the first page did even if the catalogue changes.</summary>
    public static string ToJson(IReadOnlyList<SortKey> keys) =>
        new JsonArray([.. keys.Select(k => (JsonNode)Obj(("field", k.Field), ("direction", k.Descending ? "desc" : "asc"), ("path", k.Path)))])
            .ToJsonString();

    public static IReadOnlyList<SortKey> FromJson(string json) =>
        [.. JsonNode.Parse(json)!.AsArray().Select(n =>
        {
            var field = n!["field"]!.GetValue<string>();
            var descending = n["direction"]!.GetValue<string>() == "desc";
            return n["path"]?.GetValue<string>() is { Length: > 0 } path
                ? new SortKey(field, path, descending)
                : Resolve(field, descending ? SearchSortDirection.Desc : SearchSortDirection.Asc)
                    ?? throw new InvalidOperationException("A stored sort key is no longer sortable.");
        })];
}

/// <summary>Everything one search request needs besides the query (built by <see cref="SearchService"/>).</summary>
internal sealed record SearchBodySpec
{
    public required JsonObject Query { get; init; }

    public required string PointInTimeId { get; init; }

    public required TimeSpan KeepAlive { get; init; }

    public required IReadOnlyList<SortKey> Sort { get; init; }

    /// <summary>Projection paths fetched besides the grid fields (<c>SearchRequest.Fields</c>).</summary>
    public IReadOnlyList<string> SourceFields { get; init; } = [];

    /// <summary>Walk the sort backwards (previous and last pages, Q-49).</summary>
    public bool Reverse { get; init; }

    public JsonArray? SearchAfter { get; init; }

    public int From { get; init; }

    public required int Size { get; init; }

    /// <summary>Null: exact (Q-32 "count exactly").</summary>
    public int? TrackTotalHitsUpTo { get; init; }

    public bool Highlight { get; init; }

    public int SnippetFragmentSize { get; init; } = 150;

    public int SnippetsPerHit { get; init; } = 3;

    public IReadOnlyList<string> Facets { get; init; } = [];

    /// <summary>Further aggregations by name (the expanded counts of E09-T03).</summary>
    public JsonObject? Aggregations { get; init; }

    public int FacetBuckets { get; init; } = 25;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Builds every OpenSearch search body (ADR-006 R7, ADR-015 D8.1). The outer <c>bool.filter</c> is assembled here from
/// server state only and always leads with the authenticated workspace term; the user clause is placed only in
/// <c>bool.must</c>, so no AST can OR or NOT it away. The same query object is used for hits, totals, aggregations,
/// highlighting and every <c>search_after</c> continuation.
/// </summary>
internal static class SearchDsl
{
    /// <summary>Private-use characters that delimit highlighted ranges; stripped before snippets leave the service.</summary>
    public const char HighlightStart = '';
    public const char HighlightEnd = '';

    public const string FacetPrefix = "facet_";

    public static JsonObject Query(string workspaceFilterValue, VisibilityFilter visibility, JsonObject userQuery)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceFilterValue);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(userQuery);

        var filter = new JsonArray(Obj(("term", Obj((ProjectionFields.WorkspaceId, Obj(("value", workspaceFilterValue)))))));
        var excluded = visibility.DeniedClasses.Select(SecurityTags.Class)
            .Concat(visibility.WallIds.Select(SecurityTags.Wall))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(t => (JsonNode)t)
            .ToArray();
        if (excluded.Length > 0)
        {
            filter.Add(Obj(("bool", Obj(("must_not", new JsonArray(Obj(("terms", Obj((ProjectionFields.SecurityTags, new JsonArray(excluded)))))))))));
        }

        return Obj(("bool", Obj(("filter", filter), ("must", new JsonArray(userQuery.DeepClone())))));
    }

    public static JsonObject Body(SearchBodySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var body = Obj(
            ("query", spec.Query.DeepClone()),
            ("size", spec.Size),
            ("sort", Sort(spec.Sort, spec.Reverse)),
            ("track_total_hits", spec.TrackTotalHitsUpTo is { } cap ? cap : true),
            ("_source", Obj(("includes", new JsonArray([.. ProjectionFields.GridSource.Concat(spec.SourceFields).Distinct(StringComparer.Ordinal)
                .Select(f => (JsonNode)f)])))),
            ("timeout", Milliseconds(spec.Timeout)),
            ("pit", Obj(("id", spec.PointInTimeId), ("keep_alive", Seconds(spec.KeepAlive)))));
        if (spec.From > 0)
        {
            body["from"] = spec.From;
        }

        if (spec.SearchAfter is { } after)
        {
            body["search_after"] = after.DeepClone();
        }

        if (spec.Highlight && spec.SnippetsPerHit > 0)
        {
            body["highlight"] = Obj(
                ("type", "unified"),
                ("pre_tags", new JsonArray(HighlightStart.ToString())),
                ("post_tags", new JsonArray(HighlightEnd.ToString())),
                ("fields", Obj((ProjectionFields.Text, Obj(
                    ("fragment_size", spec.SnippetFragmentSize),
                    ("number_of_fragments", spec.SnippetsPerHit),
                    ("no_match_size", 0))))));
        }

        if (spec.Facets.Count > 0 || spec.Aggregations is { Count: > 0 })
        {
            var aggs = new JsonObject();
            foreach (var facet in spec.Facets)
            {
                aggs[FacetPrefix + facet] = Obj(("terms", Obj(("field", facet), ("size", spec.FacetBuckets))));
            }

            foreach (var (name, aggregation) in spec.Aggregations ?? [])
            {
                aggs[name] = aggregation!.DeepClone();
            }

            body["aggs"] = aggs;
        }

        return body;
    }

    /// <summary>
    /// The sort with the deterministic tie-breakers: Control Number ascending (unless the sort already uses it, E16-T09),
    /// then <c>documentId</c> (unique, so equal Control Numbers never reorder). Reversed exactly (including missing values)
    /// when asked.
    /// </summary>
    public static JsonArray Sort(IReadOnlyList<SortKey> keys, bool reverse)
    {
        var sort = new JsonArray();
        var tieBreakers = keys.Any(k => k.Path == ProjectionFields.ControlNumberSort)
            ? [new SortKey(ProjectionFields.DocumentId, ProjectionFields.DocumentId, Descending: false)]
            : new[] { SortKey.ControlNumber, new SortKey(ProjectionFields.DocumentId, ProjectionFields.DocumentId, Descending: false) };
        foreach (var key in keys.Concat(tieBreakers))
        {
            foreach (var (path, keyDescending) in key.Clauses)
            {
                var descending = keyDescending ^ reverse;
                var order = Obj(("order", descending ? "desc" : "asc"));
                if (!key.IsScore)
                {
                    order["missing"] = reverse ? "_first" : "_last";
                }

                sort.Add(Obj((path, order)));
            }
        }

        return sort;
    }

    public static string Seconds(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (long)value.TotalSeconds)}s");

    private static string Milliseconds(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (long)value.TotalMilliseconds)}ms");
}
