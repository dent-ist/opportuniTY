using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.Benchmarks.Workloads;

public sealed record StubApiOptions
{
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>0 = any free port.</summary>
    public int Port { get; init; }

    public string ApiBase { get; init; } = "/api/v1";

    public string WorkspaceId { get; init; } = "bench";

    /// <summary>Artificial service time per gate class, so simple and complex histograms differ in stub runs.</summary>
    public TimeSpan SimpleDelay { get; init; } = TimeSpan.FromMilliseconds(2);

    public TimeSpan ComplexDelay { get; init; } = TimeSpan.FromMilliseconds(6);

    public TimeSpan OtherDelay { get; init; } = TimeSpan.FromMilliseconds(1);
}

/// <summary>One accepted search, in arrival order.</summary>
public sealed record StubSearch(long Sequence, string QueryId, string Class);

/// <summary>What the fake API saw: per-operation counts, contract violations (with reasons) and the search log.</summary>
public sealed class StubStats
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _violations = new();
    private readonly ConcurrentQueue<StubSearch> _searches = new();

    public IReadOnlyDictionary<string, long> Counts => _counts;

    public IReadOnlyCollection<string> Violations => _violations;

    public IReadOnlyCollection<StubSearch> Searches => _searches;

    public long Count(string operation) => _counts.GetValueOrDefault(operation);

    internal void Increment(string operation) => _counts.AddOrUpdate(operation, 1, (_, n) => n + 1);

    internal void Violation(string message)
    {
        Increment("violations");
        if (_violations.Count < 200)
        {
            _violations.Enqueue(message);
        }
    }

    internal void Searched(StubSearch search) => _searches.Enqueue(search);
}

/// <summary>
/// A fake of the M1 API surface the k6 workloads use (ADR-019 conventions), for validating script logic without the
/// product: it accepts only queries of the loaded query set with their exact options, enforces If-Match on coding and
/// Idempotency-Key on bulk jobs, returns RFC 9457 problems for contract violations and records what it saw.
/// Responses are synthetic (document ids, totals = expected hits); latencies are the configured delays.
/// </summary>
public sealed partial class StubApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Dictionary<string, BenchmarkQuery> _queries;
    private readonly QuerySet _set;
    private readonly StubApiOptions _options;
    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Body, string JobId)> _jobs = new(StringComparer.Ordinal);
    private long _jobSequence;

    private StubApi(WebApplication app, QuerySet set, StubApiOptions options)
    {
        _app = app;
        _set = set;
        _options = options;
        _queries = set.Queries.GroupBy(q => q.Oql + "|" + q.Expand + "|" + Options(q.Sort, q.Facets), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public StubStats Stats { get; } = new();

    public Uri BaseAddress { get; private set; } = new("http://127.0.0.1/");

    public static async Task<StubApi> StartAsync(QuerySet set, StubApiOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        options ??= new StubApiOptions();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(string.Create(CultureInfo.InvariantCulture, $"http://{options.Host}:{options.Port}"));
        WebApplication app = builder.Build();
        var stub = new StubApi(app, set, options);
        stub.Map();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        stub.BaseAddress = new Uri(address.TrimEnd('/') + "/");
        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static string Options(IReadOnlyList<SortKey> sort, IReadOnlyList<string> facets) =>
        string.Join(",", sort.Select(s => s.Field + " " + s.Direction)) + "|" + string.Join(",", facets);

    private void Map()
    {
        string workspace = _options.ApiBase + "/workspaces/{workspaceId}";
        _app.MapPost(workspace + "/searches", SearchAsync);
        _app.MapGet(workspace + "/documents/{documentId}", ViewAsync);
        _app.MapPut(workspace + "/documents/{documentId}/coding", CodeAsync);
        _app.MapPost(workspace + "/bulk-coding-jobs", BulkAsync);
        _app.MapGet(workspace + "/jobs/{jobId}", (string workspaceId, string jobId) => WrongWorkspace(workspaceId) ?? Results.Ok(new { jobId, status = "completed" }));
        _app.MapGet("/__stub/stats", () => Results.Json(new { counts = Stats.Counts, violations = Stats.Violations, searches = Stats.Searches.Count }));
        _app.MapFallback((HttpContext context) => Problem(context.Request.Method + " " + context.Request.Path + " is not an endpoint of the benchmark API contract", StatusCodes.Status404NotFound, "not-found"));
    }

    private async Task<IResult> SearchAsync(HttpContext context, string workspaceId)
    {
        Stats.Increment("search");
        if (WrongWorkspace(workspaceId) is { } wrong)
        {
            return wrong;
        }

        if (!int.TryParse(context.Request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit) || limit != _set.Request.PageSize)
        {
            return Problem($"limit must be the taxonomy page size {_set.Request.PageSize}", 400);
        }

        JsonNode? body = await ReadJsonAsync(context).ConfigureAwait(false);
        string? oql, expand;
        List<SortKey> sort;
        List<string> facets;
        try
        {
            oql = body?["query"]?.GetValue<string>();
            expand = body?["expand"]?.GetValue<string>();
            sort = body?["sort"]?.AsArray().Select(s => new SortKey(s!["field"]!.GetValue<string>(), s["direction"]!.GetValue<string>())).ToList() ?? [];
            facets = body?["facets"]?.AsArray().Select(f => f!.GetValue<string>()).ToList() ?? [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or FormatException)
        {
            return Problem("malformed search body: " + ex.Message, 400);
        }

        if (oql is null || !_queries.TryGetValue(oql + "|" + expand + "|" + Options(sort, facets), out BenchmarkQuery? query))
        {
            return Problem($"query '{oql}' with these options is not in query set {_set.QuerySeed}", 400);
        }

        if (body?["highlight"]?.GetValue<bool>() != _set.Request.Highlight)
        {
            return Problem("highlight must match the taxonomy (on)", 400);
        }

        string? declared = context.Request.Headers["X-Bench-Query"];
        if (declared is not null && declared != query.Id)
        {
            return Problem($"X-Bench-Query {declared} does not match the query text ({query.Id})", 400);
        }

        long sequence = long.TryParse(context.Request.Headers["X-Bench-Seq"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : -1;
        Stats.Searched(new StubSearch(sequence, query.Id, query.Class));
        Stats.Increment("search-ok");
        await Task.Delay(query.Gate == "simple" ? _options.SimpleDelay : _options.ComplexDelay, context.RequestAborted).ConfigureAwait(false);

        long total = query.ExpectedHits;
        int page = (int)Math.Min(limit, total);
        long documents = _set.Corpus.DocumentCount;
        var items = Enumerable.Range(0, page).Select(i =>
        {
            ulong number = ulong.Parse(query.Id.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture);
            long n = (long)(StableHash.Combine(number, (ulong)i) % (ulong)documents) + 1;
            return new { documentId = "doc-" + n.ToString(CultureInfo.InvariantCulture) };
        });
        return Results.Ok(new { items, nextCursor = total > page ? "stub-cursor" : null, total = new { value = total, relation = "eq" } });
    }

    private async Task<IResult> ViewAsync(HttpContext context, string workspaceId, string documentId)
    {
        Stats.Increment("document-view");
        if (WrongWorkspace(workspaceId) is { } wrong)
        {
            return wrong;
        }

        await Task.Delay(_options.OtherDelay, context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.ETag = ETag(_versions.GetOrAdd(documentId, 1));
        return Results.Ok(new { documentId });
    }

    private async Task<IResult> CodeAsync(HttpContext context, string workspaceId, string documentId)
    {
        Stats.Increment("coding-write");
        if (WrongWorkspace(workspaceId) is { } wrong)
        {
            return wrong;
        }

        string? ifMatch = context.Request.Headers.IfMatch;
        if (string.IsNullOrEmpty(ifMatch) || ifMatch == "*")
        {
            return Problem("coding requires If-Match with the document ETag (ADR-019 §2.7)", 428);
        }

        JsonNode? body = await ReadJsonAsync(context).ConfigureAwait(false);
        if (body?["changes"] is not JsonArray changes || changes.Count == 0)
        {
            return Problem("coding body must carry a non-empty changes array", 400);
        }

        foreach (JsonNode? change in changes)
        {
            string? field = change?["field"]?.GetValue<string>();
            string? value = change?["value"]?.GetValue<string>();
            CodingFieldDefinition? definition = _set.CodingFixture.Fields.FirstOrDefault(f => f.QueryName == field);
            if (definition is null || !definition.Choices.Any(c => c.Name == value))
            {
                return Problem($"unknown coding field/value {field}={value}", 400);
            }
        }

        await Task.Delay(_options.OtherDelay, context.RequestAborted).ConfigureAwait(false);
        int current = _versions.GetOrAdd(documentId, 1);
        if (ifMatch != ETag(current) || !_versions.TryUpdate(documentId, current + 1, current))
        {
            Stats.Increment("coding-conflict");
            return Problem("the document changed since it was read", StatusCodes.Status412PreconditionFailed, "version-conflict", violation: false);
        }

        Stats.Increment("coding-ok");
        context.Response.Headers.ETag = ETag(current + 1);
        return Results.Ok(new { documentId, documentVersion = current + 1 });
    }

    private async Task<IResult> BulkAsync(HttpContext context, string workspaceId)
    {
        Stats.Increment("bulk-submit");
        if (WrongWorkspace(workspaceId) is { } wrong)
        {
            return wrong;
        }

        string? key = context.Request.Headers["Idempotency-Key"];
        if (string.IsNullOrEmpty(key) || key.Length > 128)
        {
            return Problem("bulk jobs require an Idempotency-Key of at most 128 characters (ADR-019 §2.6)", 400);
        }

        string text = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        JsonNode? body = JsonNode.Parse(text);
        string? target = body?["target"]?["query"]?.GetValue<string>();
        if (target is null || !ControlNumberRange().IsMatch(target))
        {
            return Problem($"bulk target must be a controlnumber range, got '{target}'", 400);
        }

        if (body?["changes"] is not JsonArray { Count: > 0 } changes || changes.Any(c => c?["field"]?.GetValue<string>() != CodingFixture.BulkTagField))
        {
            return Problem($"background bulk jobs may only write {CodingFixture.BulkTagField}", 400);
        }

        (string Body, string JobId) job = _jobs.GetOrAdd(key, _ => (text, "job-" + Interlocked.Increment(ref _jobSequence).ToString(CultureInfo.InvariantCulture)));
        if (job.Body != text)
        {
            return Problem("Idempotency-Key reused with a different body", 422, "idempotency-key-reuse");
        }

        Stats.Increment("bulk-accepted");
        await Task.Delay(_options.OtherDelay, context.RequestAborted).ConfigureAwait(false);
        string location = $"{_options.ApiBase}/workspaces/{workspaceId}/jobs/{job.JobId}";
        return Results.Accepted(location, new { jobId = job.JobId, status = "queued" });
    }

    private IResult? WrongWorkspace(string workspaceId) =>
        workspaceId == _options.WorkspaceId ? null : Problem($"workspace {workspaceId} not found", 404, "not-found");

    private static string ETag(int version) => string.Create(CultureInfo.InvariantCulture, $"\"v{version}\"");

    private IResult Problem(string detail, int status, string code = "validation", bool violation = true)
    {
        if (violation)
        {
            Stats.Violation(string.Create(CultureInfo.InvariantCulture, $"{status} {code}: {detail}"));
        }

        return Results.Problem(detail: detail, statusCode: status, type: "urn:opportunity:problem:" + code, extensions: new Dictionary<string, object?> { ["code"] = code });
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpContext context)
    {
        try
        {
            return await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^controlnumber:\[[A-Za-z0-9_-]+ TO [A-Za-z0-9_-]+\]$", RegexOptions.CultureInvariant)]
    private static partial Regex ControlNumberRange();
}
