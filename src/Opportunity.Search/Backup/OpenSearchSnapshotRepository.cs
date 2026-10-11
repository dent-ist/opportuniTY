using System.Net;
using System.Text.Json.Nodes;

using Opportunity.Application.Backup;
using Opportunity.Search.Indexing;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Backup;

/// <summary>
/// The OpenSearch snapshot repository (ADR-016 §3.1, design §4.2 R4) over the snapshot REST API. Snapshots cover only the
/// installation's <c>&lt;prefix&gt;-*</c> indices, never the global state: index templates come back from the migrator
/// bootstrap and the security configuration from the deployment. Long calls are polled, never held open.
/// </summary>
internal sealed class OpenSearchSnapshotRepository(
    OpenSearchConnection connection, IndexNames names, SearchSnapshotOptions options, TimeProvider time) : ISearchSnapshotRepository
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public string RepositoryName => options.Repository;

    private string Pattern => $"{names.Prefix}-*";

    public async Task EnsureRepositoryAsync(CancellationToken cancellationToken = default)
    {
        var existing = await connection.SendAsync(HttpMethod.Get, $"_snapshot/{Escape(RepositoryName)}", null, cancellationToken, HttpStatusCode.NotFound)
            .ConfigureAwait(false);
        if (existing.Status != HttpStatusCode.NotFound)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.RepositoryType))
        {
            throw new InvalidOperationException(
                $"The snapshot repository '{RepositoryName}' does not exist and {SearchSnapshotOptions.SectionName}:RepositoryType is not set to register it.");
        }

        var settings = new JsonObject();
        foreach (var (key, value) in options.RepositorySettings)
        {
            settings[key] = value;
        }

        await connection.SendAsync(HttpMethod.Put, $"_snapshot/{Escape(RepositoryName)}",
            Obj(("type", options.RepositoryType), ("settings", settings)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchSnapshotInfo> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        Validate(name);
        await connection.SendAsync(HttpMethod.Put, $"_snapshot/{Escape(RepositoryName)}/{Escape(name)}",
            Obj(("indices", Pattern), ("ignore_unavailable", true), ("include_global_state", false), ("partial", false)), cancellationToken)
            .ConfigureAwait(false);
        while (true)
        {
            var info = await GetAsync(name, cancellationToken).ConfigureAwait(false);
            if (info is not null && info.State != SearchSnapshotState.InProgress)
            {
                return info;
            }

            await Task.Delay(PollInterval, time, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SearchSnapshotInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await connection.SendAsync(HttpMethod.Get, $"_snapshot/{Escape(RepositoryName)}/_all", null, cancellationToken)
            .ConfigureAwait(false);
        return [.. (response.Body?["snapshots"] as JsonArray ?? []).Select(Parse).OrderBy(s => s.StartTime ?? DateTimeOffset.MinValue)];
    }

    public async Task<SearchSnapshotInfo?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        Validate(name);
        var response = await connection.SendAsync(HttpMethod.Get, $"_snapshot/{Escape(RepositoryName)}/{Escape(name)}", null, cancellationToken,
            HttpStatusCode.NotFound).ConfigureAwait(false);
        return response.Status == HttpStatusCode.NotFound
            ? null
            : (response.Body?["snapshots"] as JsonArray ?? []).Select(Parse).FirstOrDefault();
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        Validate(name);
        await connection.SendAsync(HttpMethod.Delete, $"_snapshot/{Escape(RepositoryName)}/{Escape(name)}", null, cancellationToken,
            HttpStatusCode.NotFound).ConfigureAwait(false);
    }

    public async Task<SearchSnapshotRestoreResult> RestoreAsync(string name, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Snapshot {name} is not in repository {RepositoryName}.");
        if (!snapshot.IsUsable)
        {
            throw new InvalidOperationException($"Snapshot {name} is {snapshot.State}; only a successful snapshot can be restored.");
        }

        // R4: nothing of the current cluster may survive next to the restored indices (it may be ahead of PostgreSQL).
        var existing = await ListIndicesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var batch in existing.Chunk(50))
        {
            await connection.SendAsync(HttpMethod.Delete, string.Join(',', batch.Select(Escape)) + "?ignore_unavailable=true", null, cancellationToken)
                .ConfigureAwait(false);
        }

        var own = snapshot.Indices.Where(i => i.StartsWith(names.Prefix + "-", StringComparison.Ordinal)).ToList();
        if (own.Count > 0)
        {
            await connection.SendAsync(HttpMethod.Post, $"_snapshot/{Escape(RepositoryName)}/{Escape(name)}/_restore",
                Obj(("indices", Pattern), ("include_aliases", true), ("include_global_state", false), ("partial", false)), cancellationToken)
                .ConfigureAwait(false);
            await WaitReadableAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SearchSnapshotRestoreResult(own, existing);
    }

    private async Task WaitReadableAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var health = await connection.SendAsync(HttpMethod.Get,
                $"_cluster/health/{Escape(Pattern)}?wait_for_status=yellow&timeout=10s&expand_wildcards=all", null, cancellationToken,
                HttpStatusCode.RequestTimeout).ConfigureAwait(false);
            var status = health.Body?["status"]?.GetValue<string>();
            var initializing = health.Body?["initializing_shards"]?.GetValue<int>() ?? 0;
            if (status is "yellow" or "green" && initializing == 0)
            {
                return;
            }

            await Task.Delay(PollInterval, time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<string>> ListIndicesAsync(CancellationToken cancellationToken)
    {
        var response = await connection.SendAsync(HttpMethod.Get, $"_cat/indices/{Escape(Pattern)}?format=json&h=index&expand_wildcards=all", null,
            cancellationToken, HttpStatusCode.NotFound).ConfigureAwait(false);
        return [.. (response.Body as JsonArray ?? []).Select(i => i?["index"]?.GetValue<string>()).OfType<string>().Order(StringComparer.Ordinal)];
    }

    private static SearchSnapshotInfo Parse(JsonNode? node)
    {
        var state = node?["state"]?.GetValue<string>() switch
        {
            "SUCCESS" => SearchSnapshotState.Success,
            "PARTIAL" => SearchSnapshotState.Partial,
            "IN_PROGRESS" or "STARTED" => SearchSnapshotState.InProgress,
            _ => SearchSnapshotState.Failed,
        };
        DateTimeOffset? Millis(string field) =>
            node?[field] is JsonValue v && v.TryGetValue<long>(out var ms) && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        var failures = node?["failures"] as JsonArray;
        return new SearchSnapshotInfo(
            node?["snapshot"]?.GetValue<string>() ?? string.Empty,
            state,
            Millis("start_time_in_millis"),
            state == SearchSnapshotState.InProgress ? null : Millis("end_time_in_millis"),
            [.. (node?["indices"] as JsonArray ?? []).Select(i => i?.GetValue<string>()).OfType<string>().Order(StringComparer.Ordinal)],
            failures is { Count: > 0 } ? node?["reason"]?.GetValue<string>() ?? $"{failures.Count} shard failure(s)" : null);
    }

    private static void Validate(string name)
    {
        if (!SearchSnapshotNames.IsValid(name))
        {
            throw new ArgumentException("Snapshot names are lowercase [a-z0-9._-].", nameof(name));
        }
    }
}
