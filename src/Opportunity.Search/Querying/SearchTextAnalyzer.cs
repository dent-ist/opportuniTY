using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Opportunity.Search.Indexing;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Querying;

/// <summary>How a text is analyzed for a field.</summary>
internal enum AnalysisMode
{
    /// <summary>The field's search analyzer: the tokens a term or phrase matches (multi-token = phrase, none = error).</summary>
    Tokens,

    /// <summary>
    /// The analyzer's token filters over the whole text, untokenized: a wildcard pattern normalized like indexed tokens
    /// (lower case, ASCII folding) with its <c>*</c>, <c>?</c> and escapes intact.
    /// </summary>
    Normalize,
}

/// <summary>
/// Turns query text into what a field's search analyzer produces (ADR-008 §2: a multi-token term is a phrase, zero
/// tokens is an error; span queries need the indexed tokens). Results are per input text, in order.
/// </summary>
internal interface ISearchTextAnalyzer
{
    ValueTask<IReadOnlyList<IReadOnlyList<string>>> AnalyzeAsync(
        int generation, string path, AnalysisMode mode, IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

/// <summary>
/// Analysis by OpenSearch itself, with the search analyzer the projection mapping of the generation assigns to the
/// path, sent as an anonymous analyzer definition (no index needed, one request per path and mode). Plain ASCII
/// words skip the round trip: every projection analyzer turns them into exactly one lower-cased token.
/// </summary>
internal sealed class OpenSearchTextAnalyzer(OpenSearchConnection connection, ProjectionMappings mappings) : ISearchTextAnalyzer
{
    /// <summary>Offset gap between the values of a multi-valued <c>_analyze</c> text (Lucene's default of 1).</summary>
    private const int OffsetGap = 1;

    private readonly ConcurrentDictionary<(int Generation, string Path, AnalysisMode Mode), JsonObject> _analyzers = new();

    public async ValueTask<IReadOnlyList<IReadOnlyList<string>>> AnalyzeAsync(
        int generation, string path, AnalysisMode mode, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(texts);
        var results = new IReadOnlyList<string>[texts.Count];
        var pending = new List<int>();
        for (var i = 0; i < texts.Count; i++)
        {
            if (IsPlain(texts[i], mode))
            {
                results[i] = [texts[i].ToLowerInvariant()];
            }
            else
            {
                pending.Add(i);
            }
        }

        if (pending.Count == 0)
        {
            return results;
        }

        var body = _analyzers.GetOrAdd((generation, path, mode), key => AnalyzerFor(mappings.Load(key.Generation), key.Path, key.Mode))
            .DeepClone().AsObject();
        body["text"] = new JsonArray([.. pending.Select(i => (JsonNode)texts[i])]);
        var response = await connection.SendAsync(HttpMethod.Post, "_analyze", body, cancellationToken).ConfigureAwait(false);

        // Values are concatenated with an offset gap: value k covers [start_k, start_k + length_k).
        var starts = new int[pending.Count];
        for (int k = 0, offset = 0; k < pending.Count; k++)
        {
            starts[k] = offset;
            offset += texts[pending[k]].Length + OffsetGap;
        }

        var buckets = pending.Select(_ => new List<string>()).ToArray();
        foreach (var token in (response.Body?["tokens"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var k = Array.BinarySearch(starts, token["start_offset"]!.GetValue<int>());
            buckets[k >= 0 ? k : ~k - 1].Add(token["token"]!.GetValue<string>());
        }

        for (var k = 0; k < pending.Count; k++)
        {
            results[pending[k]] = buckets[k];
        }

        return results;
    }

    /// <summary>
    /// Text every projection analyzer maps to itself in lower case: ASCII letters and digits within the 255-character
    /// token limit, or, when normalizing, any ASCII text.
    /// </summary>
    internal static bool IsPlain(string text, AnalysisMode mode) => mode == AnalysisMode.Normalize
        ? text.Length > 0 && text.All(char.IsAscii)
        : text.Length is > 0 and <= 255 && text.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// The anonymous <c>_analyze</c> body equivalent to the search analyzer of <paramref name="path"/> in
    /// <paramref name="mapping"/>: custom tokenizer and filter definitions are inlined, built-in names stay names. In
    /// <see cref="AnalysisMode.Normalize"/> the tokenizer is <c>keyword</c>, so only the filters apply.
    /// </summary>
    internal static JsonObject AnalyzerFor(JsonObject mapping, string path, AnalysisMode mode = AnalysisMode.Tokens)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(path);
        JsonNode? node = mapping["mappings"];
        foreach (var segment in path.Split('.'))
        {
            node = node?["properties"]?[segment] ?? node?["fields"]?[segment];
        }

        var name = (node?["search_analyzer"] ?? node?["analyzer"])?.GetValue<string>()
            ?? throw new InvalidOperationException($"Projection path '{path}' is not an analyzed text field.");
        var analysis = mapping["settings"]?["analysis"];
        var analyzer = analysis?["analyzer"]?[name] as JsonObject
            ?? throw new InvalidOperationException($"Analyzer '{name}' is not defined in the projection mapping.");
        if (analyzer["type"]?.GetValue<string>() != "custom")
        {
            throw new InvalidOperationException($"Analyzer '{name}' must be a custom analyzer to be reproduced for query analysis.");
        }

        var filters = new JsonArray();
        foreach (var filter in analyzer["filter"] as JsonArray ?? [])
        {
            var filterName = filter!.GetValue<string>();
            filters.Add(analysis?["filter"]?[filterName]?.DeepClone() ?? JsonValue.Create(filterName));
        }

        var tokenizer = analyzer["tokenizer"]!.GetValue<string>();
        return Obj(
            ("tokenizer", mode == AnalysisMode.Normalize
                ? JsonValue.Create("keyword")
                : analysis?["tokenizer"]?[tokenizer]?.DeepClone() ?? JsonValue.Create(tokenizer)),
            ("filter", filters));
    }
}
