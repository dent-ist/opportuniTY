using System.Text.Json.Nodes;

using Opportunity.Contracts.Search;
using Opportunity.Core.Documents;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Querying;

/// <summary>
/// The OpenSearch form of <see cref="RelationshipExpansion"/> (E09-T03) for interactive searches. The relationship keys
/// of the base hits (family, duplicate group, email thread) are collected under the page's point-in-time reader and the
/// user clause becomes "base OR family OR duplicate OR thread OR family-of-expanded", each a named clause so every hit
/// says why it is there (<c>matched_queries</c>). Expanded clauses score 0, so relevance order keeps the hits first.
/// Everything stays inside the outer workspace and security filter and the page post-filter (Q-12, Q-52): an expanded
/// member is authorized exactly like a hit, and keys are read only from hits the caller may see.
/// Snapshots expand in PostgreSQL instead (<c>Opportunity.Data.Relationships.RelationshipExpansionSql</c>); both follow
/// the steps and the first-reason-wins labelling of <see cref="RelationshipExpansion"/>.
/// </summary>
internal static class RelationshipExpansionQuery
{
    public const string BaseName = "x.base";
    public const string FamilyName = "x.family";
    public const string DuplicateName = "x.duplicate";
    public const string ThreadName = "x.thread";
    public const string FamilyOfExpandedName = "x.familyOfExpanded";

    /// <summary>Aggregation names of the counts.</summary>
    public const string BaseCount = "expanded_base";
    public const string FamilyCount = "expanded_family";
    public const string FamilyOfExpandedCount = "expanded_family_of_expanded";
    public const string DuplicateCount = "expanded_duplicate";
    public const string ThreadCount = "expanded_thread";

    /// <summary>Terms per <c>terms</c> query, below OpenSearch's <c>index.max_terms_count</c> (65,536).</summary>
    public const int MaxTermsPerClause = 65_000;

    /// <summary>The keys of each step; an empty set means the step adds nothing (its clause is left out).</summary>
    public sealed record Keys(
        IReadOnlyCollection<string> Family,
        IReadOnlyCollection<string> Duplicates,
        IReadOnlyCollection<string> Thread,
        IReadOnlyCollection<string> FamilyOfExpanded)
    {
        public static Keys Empty { get; } = new([], [], [], []);
    }

    /// <summary>A filter matching documents whose <paramref name="field"/> is one of <paramref name="keys"/> (chunked).</summary>
    public static JsonObject Terms(string field, IReadOnlyCollection<string> keys)
    {
        var ordered = keys.Order(StringComparer.Ordinal).ToList();
        if (ordered.Count <= MaxTermsPerClause)
        {
            return Obj(("terms", Obj((field, new JsonArray([.. ordered.Select(k => (JsonNode)k)])))));
        }

        var chunks = new JsonArray();
        foreach (var chunk in ordered.Chunk(MaxTermsPerClause))
        {
            chunks.Add(Obj(("terms", Obj((field, new JsonArray([.. chunk.Select(k => (JsonNode)k)]))))));
        }

        return Obj(("bool", Obj(("should", chunks), ("minimum_should_match", 1))));
    }

    /// <summary>The user clause of an expanded search: the base query or any non-empty expansion clause.</summary>
    public static JsonObject Combined(JsonObject userQuery, Keys keys)
    {
        ArgumentNullException.ThrowIfNull(userQuery);
        ArgumentNullException.ThrowIfNull(keys);
        var should = new JsonArray(Obj(("bool", Obj(("must", new JsonArray(userQuery.DeepClone())), ("_name", BaseName)))));
        foreach (var (name, filter) in Filters(keys))
        {
            should.Add(Obj(("constant_score", Obj(("filter", filter), ("boost", 0), ("_name", name)))));
        }

        return Obj(("bool", Obj(("should", should), ("minimum_should_match", 1))));
    }

    /// <summary>
    /// Filter aggregations counting each kind's additions under the first-reason-wins rule (family, duplicate, thread,
    /// then family of the expanded documents), plus the exact base count.
    /// </summary>
    public static JsonObject CountAggregations(JsonObject userQuery, Keys keys)
    {
        ArgumentNullException.ThrowIfNull(userQuery);
        ArgumentNullException.ThrowIfNull(keys);
        var filters = Filters(keys).ToDictionary(f => f.Name, f => f.Filter, StringComparer.Ordinal);
        var aggs = new JsonObject { [BaseCount] = Obj(("filter", userQuery.DeepClone())) };

        void Add(string aggregation, string clause, params string[] earlier)
        {
            if (!filters.TryGetValue(clause, out var filter))
            {
                return;
            }

            var mustNot = new JsonArray(userQuery.DeepClone());
            foreach (var name in earlier)
            {
                if (filters.TryGetValue(name, out var previous))
                {
                    mustNot.Add(previous.DeepClone());
                }
            }

            aggs[aggregation] = Obj(("filter", Obj(("bool", Obj(("filter", new JsonArray(filter.DeepClone())), ("must_not", mustNot))))));
        }

        Add(FamilyCount, FamilyName);
        Add(DuplicateCount, DuplicateName, FamilyName);
        Add(ThreadCount, ThreadName, FamilyName, DuplicateName);
        Add(FamilyOfExpandedCount, FamilyOfExpandedName, FamilyName, DuplicateName, ThreadName);
        return aggs;
    }

    /// <summary>The base count and the additions by kind, from the response's aggregations.</summary>
    public static (long Base, SearchExpandedCounts Counts) ReadCounts(JsonObject? aggregations)
    {
        long Count(string name) => aggregations?[name]?["doc_count"] is JsonValue value
            ? value.TryGetValue<long>(out var count) ? count : value.TryGetValue<int>(out var small) ? small : 0
            : 0;
        var baseHits = Count(BaseCount);
        var family = Count(FamilyCount) + Count(FamilyOfExpandedCount);
        var duplicates = Count(DuplicateCount);
        var thread = Count(ThreadCount);
        return (baseHits, new SearchExpandedCounts(family, duplicates, thread, baseHits + family + duplicates + thread));
    }

    /// <summary>Why a hit of an expanded search is there: null when it matches the query, else the first reason (family wins).</summary>
    public static SearchExpandedBy? ExpandedBy(JsonNode? matchedQueries)
    {
        var names = matchedQueries switch
        {
            JsonArray array => array.Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal),
            JsonObject scores => scores.Select(p => p.Key).ToHashSet(StringComparer.Ordinal),
            _ => [],
        };

        return names.Contains(BaseName) ? null
            : names.Contains(FamilyName) ? SearchExpandedBy.Family
            : names.Contains(DuplicateName) ? SearchExpandedBy.Duplicate
            : names.Contains(ThreadName) ? SearchExpandedBy.Thread
            : names.Contains(FamilyOfExpandedName) ? SearchExpandedBy.Family
            : null;
    }

    /// <summary>The seeds of step 4: documents the duplicate or thread clause adds (not hits).</summary>
    public static JsonObject? AddedByDuplicateOrThread(JsonObject userQuery, IReadOnlyCollection<string> duplicates, IReadOnlyCollection<string> thread)
    {
        var should = new JsonArray();
        if (duplicates.Count > 0)
        {
            should.Add(Terms(ProjectionFields.DuplicateGroupId, duplicates));
        }

        if (thread.Count > 0)
        {
            should.Add(Terms(ProjectionFields.EmailThreadId, thread));
        }

        return should.Count == 0
            ? null
            : Obj(("bool", Obj(("should", should), ("minimum_should_match", 1), ("must_not", new JsonArray(userQuery.DeepClone())))));
    }

    private static IEnumerable<(string Name, JsonObject Filter)> Filters(Keys keys)
    {
        if (keys.Family.Count > 0)
        {
            yield return (FamilyName, Terms(ProjectionFields.FamilyId, keys.Family));
        }

        if (keys.Duplicates.Count > 0)
        {
            yield return (DuplicateName, Terms(ProjectionFields.DuplicateGroupId, keys.Duplicates));
        }

        if (keys.Thread.Count > 0)
        {
            yield return (ThreadName, Terms(ProjectionFields.EmailThreadId, keys.Thread));
        }

        if (keys.FamilyOfExpanded.Count > 0)
        {
            yield return (FamilyOfExpandedName, Terms(ProjectionFields.FamilyId, keys.FamilyOfExpanded));
        }
    }
}
