using System.Text;
using System.Text.Json.Nodes;

namespace Opportunity.Correctness.ShadowLedger;

/// <summary>The indexed copy of one document: <c>_version</c>, <c>projectionVersion</c> and the requested source.</summary>
public sealed record IndexedDocument(Guid DocumentId, long ExternalVersion, long? ProjectionVersion, JsonObject Source);

/// <summary>Where the workspace's projection is read: a physical index or alias, and the shared-index routing.</summary>
public sealed record LedgerIndexTarget(string Index, string? Routing);

/// <summary>
/// Reads projections with <c>_mget</c>, which is real-time (it sees acknowledged writes before a refresh), so samples
/// and the reconciliation observe exactly what an index worker wrote, not what the last refresh exposed.
/// </summary>
public sealed class OpenSearchProjectionReader(HttpClient http, LedgerIndexTarget target, IProjectionLayout layout)
{
    public async Task<Dictionary<Guid, IndexedDocument>> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<Guid, IndexedDocument>(ids.Count);
        if (ids.Count == 0)
        {
            return found;
        }

        var docs = new JsonArray();
        foreach (var id in ids)
        {
            var doc = new JsonObject { ["_id"] = id.ToString("D") };
            if (target.Routing is { } routing)
            {
                doc["routing"] = routing;
            }

            docs.Add(doc);
        }

        var path = $"{target.Index}/_mget?realtime=true&_source_includes={string.Join(',', layout.SourceIncludes)}";
        using var content = new StringContent(new JsonObject { ["docs"] = docs }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri(path, UriKind.Relative), content, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && text.Contains("index_not_found_exception", StringComparison.Ordinal))
        {
            return found;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"_mget on {target.Index} failed: {(int)response.StatusCode} {text[..Math.Min(text.Length, 500)]}");
        }

        foreach (var item in JsonNode.Parse(text)?["docs"]?.AsArray() ?? [])
        {
            // A missing index answers per item with an error (before the first write placed the workspace): absent.
            if (item?["found"]?.GetValue<bool>() != true || item["_source"] is not JsonObject source)
            {
                continue;
            }

            var id = Guid.Parse(item["_id"]!.GetValue<string>());
            found[id] = new IndexedDocument(id, item["_version"]!.GetValue<long>(), source["projectionVersion"]?.GetValue<long>(), source);
        }

        return found;
    }
}
