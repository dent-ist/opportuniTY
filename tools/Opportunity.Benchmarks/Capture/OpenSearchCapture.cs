using System.Globalization;
using System.Text.Json;

namespace Opportunity.Benchmarks.Capture;

/// <summary>
/// Cluster identity, per-node JVM settings, cluster settings and per-index shard topology/durability from the
/// OpenSearch REST API (<c>/</c>, <c>_nodes</c>, <c>_cluster/settings</c>, <c>_settings</c>, <c>_cat/indices</c>, <c>_cat/shards</c>).
/// </summary>
public static class OpenSearchCapture
{
    /// <summary>Default cluster settings worth recording (allocation, indexing buffer, caches, breakers, thread pools).</summary>
    public static IReadOnlyList<string> DefaultSettingPrefixes { get; } =
    [
        "cluster.routing.allocation.", "cluster.max_shards_per_node", "indices.memory.", "indices.queries.cache.",
        "indices.fielddata.cache.", "indices.requests.cache.", "indices.recovery.max_bytes_per_sec", "indices.breaker.",
        "thread_pool.write.", "thread_pool.search.", "search.max_buckets", "search.default_search_timeout",
        "action.auto_create_index", "node.processors",
    ];

    public static async Task<OpenSearchCluster> CaptureAsync(Uri endpoint, bool includeSystemIndices = false, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        using var http = new HttpJson(endpoint, handler);
        JsonElement info = await http.GetAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        JsonElement nodes = await http.GetAsync("_nodes/jvm,os", cancellationToken).ConfigureAwait(false);
        JsonElement cluster = await http.GetAsync("_cluster/settings?include_defaults=true&flat_settings=true", cancellationToken).ConfigureAwait(false);
        JsonElement settings = await http.GetAsync("_settings?include_defaults=true&flat_settings=true&expand_wildcards=open,hidden", cancellationToken).ConfigureAwait(false);
        JsonElement catIndices = await http.GetAsync("_cat/indices?format=json&bytes=b&h=index,docs.count,store.size&expand_wildcards=open,hidden", cancellationToken).ConfigureAwait(false);
        JsonElement catShards = await http.GetAsync("_cat/shards?format=json&h=index,shard,prirep,state,node", cancellationToken).ConfigureAwait(false);

        JsonElement version = info.GetProperty("version");
        return new OpenSearchCluster
        {
            Endpoint = http.DisplayEndpoint,
            ClusterName = info.GetProperty("cluster_name").GetString() ?? string.Empty,
            Version = version.GetProperty("number").GetString() ?? string.Empty,
            Distribution = Str(version, "distribution"),
            Nodes = ParseNodes(nodes),
            ClusterSettings = new OpenSearchClusterSettings
            {
                Persistent = Flatten(cluster, "persistent", _ => true),
                Transient = Flatten(cluster, "transient", _ => true),
                Defaults = Flatten(cluster, "defaults", key => DefaultSettingPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))),
            },
            Indices = ParseIndices(settings, catIndices, catShards, includeSystemIndices),
        };
    }

    public static IReadOnlyList<OpenSearchNode> ParseNodes(JsonElement nodes)
    {
        var result = new List<OpenSearchNode>();
        foreach (JsonProperty node in nodes.GetProperty("nodes").EnumerateObject())
        {
            JsonElement n = node.Value;
            JsonElement jvm = n.GetProperty("jvm");
            JsonElement mem = jvm.GetProperty("mem");
            JsonElement? os = n.TryGetProperty("os", out JsonElement o) ? o : null;
            result.Add(new OpenSearchNode
            {
                Name = n.GetProperty("name").GetString() ?? node.Name,
                Roles = [.. Strings(n, "roles").Order(StringComparer.Ordinal)],
                Jvm = new JvmInfo
                {
                    Version = Str(jvm, "version") ?? string.Empty,
                    VmName = Str(jvm, "vm_name"),
                    VmVendor = Str(jvm, "vm_vendor"),
                    HeapInitBytes = Long(mem, "heap_init_in_bytes"),
                    HeapMaxBytes = Long(mem, "heap_max_in_bytes") ?? 0,
                    InputArguments = Strings(jvm, "input_arguments"),
                    GcCollectors = Strings(jvm, "gc_collectors"),
                },
                AvailableProcessors = os is { } a ? (int?)Long(a, "available_processors") : null,
                AllocatedProcessors = os is { } b ? (int?)Long(b, "allocated_processors") : null,
            });
        }

        return [.. result.OrderBy(n => n.Name, StringComparer.Ordinal)];
    }

    public static IReadOnlyList<OpenSearchIndex> ParseIndices(JsonElement settings, JsonElement catIndices, JsonElement catShards, bool includeSystemIndices)
    {
        Dictionary<string, (long? Docs, long? Bytes)> stats = catIndices.EnumerateArray().ToDictionary(
            e => e.GetProperty("index").GetString() ?? string.Empty,
            e => (ParseLong(Str(e, "docs.count")), ParseLong(Str(e, "store.size"))),
            StringComparer.Ordinal);
        ILookup<string, OpenSearchShard> shards = catShards.EnumerateArray()
            .Select(e => (Index: Str(e, "index") ?? string.Empty, Shard: new OpenSearchShard(
                (int)(ParseLong(Str(e, "shard")) ?? 0), Str(e, "prirep") == "p", Str(e, "state") ?? string.Empty, Str(e, "node"))))
            .ToLookup(x => x.Index, x => x.Shard, StringComparer.Ordinal);

        var result = new List<OpenSearchIndex>();
        foreach (JsonProperty index in settings.EnumerateObject())
        {
            if (!includeSystemIndices && index.Name.StartsWith('.'))
            {
                continue;
            }

            string? Setting(string key)
            {
                foreach (string section in new[] { "settings", "defaults" })
                {
                    if (index.Value.TryGetProperty(section, out JsonElement s) && s.TryGetProperty(key, out JsonElement v))
                    {
                        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
                    }
                }

                return null;
            }

            (long? docs, long? bytes) = stats.GetValueOrDefault(index.Name);
            result.Add(new OpenSearchIndex
            {
                Name = index.Name,
                PrimaryShards = (int)(ParseLong(Setting("index.number_of_shards")) ?? 1),
                Replicas = (int)(ParseLong(Setting("index.number_of_replicas")) ?? 0),
                RefreshInterval = Setting("index.refresh_interval") ?? "1s",
                TranslogDurability = Setting("index.translog.durability") ?? "request",
                TranslogSyncInterval = Setting("index.translog.sync_interval"),
                Codec = Setting("index.codec"),
                DocsCount = docs,
                StoreBytes = bytes,
                Shards = shards.Contains(index.Name)
                    ? [.. shards[index.Name].OrderBy(s => s.Shard).ThenByDescending(s => s.Primary).ThenBy(s => s.Node, StringComparer.Ordinal)]
                    : null,
            });
        }

        return [.. result.OrderBy(i => i.Name, StringComparer.Ordinal)];
    }

    private static SortedDictionary<string, string> Flatten(JsonElement cluster, string section, Func<string, bool> include)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (cluster.TryGetProperty(section, out JsonElement s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in s.EnumerateObject().Where(p => include(p.Name)))
            {
                result[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            }
        }

        return result;
    }

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(v => v.GetString() ?? string.Empty)]
            : [];

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? result : null;
}
