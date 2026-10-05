#if OPPORTUNITY_FAILPOINTS
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Opportunity.IntegrationTests.Faults;

/// <summary>
/// HTTP-level OpenSearch faults for the workers' connection (E18-T01): whole <c>_bulk</c> requests rejected with 429 or
/// 503 before they reach the cluster, and partial bulk failures — a seeded subset of the actions is withheld from the
/// cluster (never applied) and answered with 429/503 item errors spliced into the real response in request order.
/// </summary>
internal sealed class OpenSearchFaults
{
    private readonly Lock _gate = new();
    private int _rejectRemaining;
    private HttpStatusCode _rejectStatus;
    private Random? _partial;

    public int Rejected { get; private set; }

    public int Withheld { get; private set; }

    public void RejectNext(HttpStatusCode status, int requests)
    {
        lock (_gate)
        {
            _rejectStatus = status;
            _rejectRemaining = requests;
        }
    }

    public void PartiallyFailNext(Random random)
    {
        lock (_gate)
        {
            _partial = random;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _rejectRemaining = 0;
            _partial = null;
        }
    }

    /// <summary>What to do with the next <c>_bulk</c> request: reject it (status), withhold some actions (random) or pass.</summary>
    public (HttpStatusCode? Reject, Random? Partial) Next()
    {
        lock (_gate)
        {
            if (_rejectRemaining > 0)
            {
                _rejectRemaining--;
                Rejected++;
                return (_rejectStatus, null);
            }

            var partial = _partial;
            _partial = null;
            return (null, partial);
        }
    }

    public void CountWithheld(int count)
    {
        lock (_gate)
        {
            Withheld += count;
        }
    }
}

internal sealed class OpenSearchFaultHandler(OpenSearchFaults faults) : DelegatingHandler(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1) })
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/_bulk", StringComparison.Ordinal) != true || request.Content is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var (reject, partial) = faults.Next();
        if (reject is { } status)
        {
            var type = status == HttpStatusCode.TooManyRequests ? "es_rejected_execution_exception" : "unavailable_shards_exception";
            return Json(status, new JsonObject { ["error"] = new JsonObject { ["type"] = type, ["reason"] = "injected by the fault matrix" }, ["status"] = (int)status });
        }

        if (partial is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        var actions = Parse(body);
        if (actions.Count < 2)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // Withhold at least one action and keep at least one.
        var withheld = new HashSet<int>(Enumerable.Range(0, actions.Count).Where(_ => partial.Next(3) == 0));
        if (withheld.Count == 0)
        {
            withheld.Add(partial.Next(actions.Count));
        }

        if (withheld.Count == actions.Count)
        {
            withheld.Remove(partial.Next(actions.Count));
        }

        var forwarded = string.Concat(actions.Where((_, i) => !withheld.Contains(i)).Select(a => a.Lines));
        using var inner = new HttpRequestMessage(request.Method, request.RequestUri) { Content = new StringContent(forwarded, Encoding.UTF8, "application/x-ndjson") };
        foreach (var header in request.Headers)
        {
            inner.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await base.SendAsync(inner, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || JsonNode.Parse(text) is not JsonObject result || result["items"] is not JsonArray items)
        {
            return Json(response.StatusCode, JsonNode.Parse(text) ?? new JsonObject());
        }

        faults.CountWithheld(withheld.Count);
        var merged = new JsonArray();
        var next = 0;
        for (var i = 0; i < actions.Count; i++)
        {
            if (withheld.Contains(i))
            {
                var itemStatus = partial.Next(2) == 0 ? 429 : 503;
                merged.Add(new JsonObject
                {
                    [actions[i].Kind] = new JsonObject
                    {
                        ["status"] = itemStatus,
                        ["error"] = new JsonObject
                        {
                            ["type"] = itemStatus == 429 ? "es_rejected_execution_exception" : "unavailable_shards_exception",
                            ["reason"] = "withheld by the fault matrix",
                        },
                    },
                });
            }
            else
            {
                merged.Add(items[next++]!.DeepClone());
            }
        }

        result["items"] = merged;
        result["errors"] = true;
        return Json(response.StatusCode, result);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    /// <summary>NDJSON actions: an <c>index</c> metadata line with its source line, or a <c>delete</c> line alone.</summary>
    private static List<(string Kind, string Lines)> Parse(string body)
    {
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var actions = new List<(string, string)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var kind = JsonNode.Parse(lines[i])!.AsObject().First().Key;
            if (kind == "delete")
            {
                actions.Add((kind, lines[i] + "\n"));
            }
            else
            {
                actions.Add((kind, lines[i] + "\n" + lines[i + 1] + "\n"));
                i++;
            }
        }

        return actions;
    }
}
#endif
