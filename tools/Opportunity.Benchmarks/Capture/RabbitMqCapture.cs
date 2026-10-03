using System.Text.Json;

namespace Opportunity.Benchmarks.Capture;

/// <summary>Broker version, nodes and queue durability from the RabbitMQ management API (<c>/api/overview</c>, <c>/api/nodes</c>, <c>/api/queues</c>).</summary>
public static class RabbitMqCapture
{
    public static async Task<RabbitMqInfo> CaptureAsync(Uri managementEndpoint, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        using var http = new HttpJson(managementEndpoint, handler);
        JsonElement overview = await http.GetAsync("api/overview", cancellationToken).ConfigureAwait(false);
        JsonElement nodes = await http.GetAsync("api/nodes?columns=name", cancellationToken).ConfigureAwait(false);
        JsonElement queues = await http.GetAsync("api/queues?columns=name,vhost,type,durable,exclusive,auto_delete", cancellationToken).ConfigureAwait(false);
        return Parse(overview, nodes, queues);
    }

    public static RabbitMqInfo Parse(JsonElement overview, JsonElement nodes, JsonElement queues) => new()
    {
        Version = Str(overview, "rabbitmq_version") ?? "unknown",
        ErlangVersion = Str(overview, "erlang_version"),
        ClusterName = Str(overview, "cluster_name"),
        Nodes = [.. nodes.EnumerateArray().Select(n => Str(n, "name") ?? string.Empty).Order(StringComparer.Ordinal)],
        // Exclusive/auto-delete queues are per-connection by design (reply queues); they carry no durable work.
        Queues = [.. queues.EnumerateArray()
            .Where(q => !Bool(q, "exclusive") && !Bool(q, "auto_delete"))
            .Select(q => new RabbitMqQueueInfo(Str(q, "name") ?? string.Empty, Str(q, "vhost") ?? "/", Str(q, "type") ?? "classic", Bool(q, "durable")))
            .OrderBy(q => q.Vhost, StringComparer.Ordinal)
            .ThenBy(q => q.Name, StringComparer.Ordinal)],
    };

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
