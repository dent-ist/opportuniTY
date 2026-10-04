using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Search.Indexing;
using Opportunity.Search;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;
using Opportunity.Search.Writing;

namespace Opportunity.UnitTests.Search;

/// <summary>E07-T04: byte-bounded <c>_bulk</c> sub-requests and ADR-001 §3 item classification of the shared writer.</summary>
public sealed class ProjectionIndexWriterTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Requests_stay_within_the_byte_and_action_limits_and_a_larger_document_travels_alone()
    {
        const long limit = 10L * 1024 * 1024;
        var handler = new FakeBulk();
        var writer = Writer(handler, new ProjectionWriterOptions { MaxRequestBytes = limit, MaxRequestActions = 5 });
        var tenMegabytes = new string('x', 10 * 1024 * 1024);
        var documents = Enumerable.Range(0, 20).Select(i => Index(i % 4 == 0 ? tenMegabytes : new string('y', 3 * 1024 * 1024 + i))).ToList();

        var report = await writer.WriteAsync(Workspace, documents, Ct);

        report.Documents.Should().HaveCount(20).And.OnlyContain(d => d.Status == ProjectionWriteStatus.Applied);
        handler.Requests.Should().OnlyContain(r => r.Bytes <= limit || r.Actions == 1);
        handler.Requests.Should().OnlyContain(r => r.Actions <= 5);
        handler.Requests.Count(r => r.Bytes > limit).Should().Be(5, "each 10 MB document is sent alone");
        handler.Requests.Sum(r => r.Actions).Should().Be(20);
        report.RequestBytes.Should().Equal(handler.Requests.Select(r => r.Bytes));
    }

    [Fact]
    public async Task Writes_are_external_versioned_and_go_to_every_write_target()
    {
        var handler = new FakeBulk();
        var writer = Writer(handler, new ProjectionWriterOptions(), targets: [new IndexTarget("idx-a", "r1"), new IndexTarget("idx-b", null)]);
        var document = Index("text", version: 7);

        await writer.WriteAsync(Workspace, [document, Delete(9), new ProjectionDocument(Workspace, Guid.NewGuid(), null, 2,
            [new ProjectionWrite(Guid.NewGuid().ToString("D"), ProjectionWriteKind.DeleteUnconditional, null, null)], [])], Ct);

        var lines = handler.Bodies.Single().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        var metadata = lines.Where(l => l.ContainsKey("index") || l.ContainsKey("delete")).ToList();
        metadata.Should().HaveCount(6, "three writes to two targets");
        metadata[0]["index"]!["version"]!.GetValue<long>().Should().Be(7);
        metadata[0]["index"]!["version_type"]!.GetValue<string>().Should().Be("external");
        metadata[0]["index"]!["routing"]!.GetValue<string>().Should().Be("r1");
        metadata[1]["index"]!["_index"]!.GetValue<string>().Should().Be("idx-b");
        metadata[1]["index"]!.AsObject().ContainsKey("routing").Should().BeFalse();
        metadata[2]["delete"]!["version"]!.GetValue<long>().Should().Be(9);
        metadata[4]["delete"]!.AsObject().ContainsKey("version").Should().BeFalse("a missing row is deleted unconditionally");
    }

    [Fact]
    public async Task Items_are_classified_per_document()
    {
        var handler = new FakeBulk
        {
            Items = [Item(201), Item(409, "version_conflict_engine_exception"), Item(429, "es_rejected_execution_exception"),
                Item(400, "mapper_parsing_exception"), Item(404, null, "delete"), Item(503, "unavailable_shards_exception")],
        };
        var writer = Writer(handler, new ProjectionWriterOptions());

        var report = await writer.WriteAsync(Workspace, [Index("a"), Index("b"), Index("c"), Index("d"), Delete(3), Index("f")], Ct);

        report.Documents.Select(d => d.Status).Should().Equal(
            ProjectionWriteStatus.Applied, ProjectionWriteStatus.StaleNoOp, ProjectionWriteStatus.Transient,
            ProjectionWriteStatus.Permanent, ProjectionWriteStatus.Applied, ProjectionWriteStatus.Transient);
        report.Throttled.Should().BeTrue();
        report.Documents[3].Error.Should().StartWith("mapper_parsing_exception");
    }

    [Fact]
    public async Task A_throttled_request_fails_all_its_items_transiently_and_an_oversized_projection_is_never_sent()
    {
        var handler = new FakeBulk { Status = HttpStatusCode.TooManyRequests };
        var writer = Writer(handler, new ProjectionWriterOptions { MaxRequestBytes = 1024 * 1024, MaxActionBytes = 2 * 1024 * 1024 });

        var report = await writer.WriteAsync(Workspace, [Index("small"), Index(new string('z', 3 * 1024 * 1024))], Ct);

        report.Throttled.Should().BeTrue();
        report.Documents[0].Status.Should().Be(ProjectionWriteStatus.Transient);
        report.Documents[1].Status.Should().Be(ProjectionWriteStatus.Permanent);
        report.Documents[1].Error.Should().StartWith("projection_too_large");
        handler.Requests.Should().ContainSingle().Which.Actions.Should().Be(1);
    }

    private static ProjectionIndexWriter Writer(FakeBulk handler, ProjectionWriterOptions options, IReadOnlyList<IndexTarget>? targets = null)
    {
        var connection = new OpenSearchConnection(new OpenSearchOptions { Endpoint = new Uri("http://opensearch.invalid:9200") }, handler);
        return new ProjectionIndexWriter(new FixedPlacement(targets ?? [new IndexTarget("idx", Workspace.ToString("D"))]), connection, options);
    }

    private static ProjectionDocument Index(string text, long version = 1)
    {
        var id = Guid.NewGuid();
        var body = new JsonObject { ["workspaceId"] = Workspace.ToString("D"), ["documentId"] = id.ToString("D"), ["text"] = text };
        return new ProjectionDocument(Workspace, id, version, 2, [new ProjectionWrite(id.ToString("D"), ProjectionWriteKind.Index, version, body)], []);
    }

    private static ProjectionDocument Delete(long version)
    {
        var id = Guid.NewGuid();
        return new ProjectionDocument(Workspace, id, version, 2, [new ProjectionWrite(id.ToString("D"), ProjectionWriteKind.Delete, version, null)], []);
    }

    private static JsonObject Item(int status, string? error = null, string op = "index")
    {
        var item = new JsonObject { ["status"] = status };
        if (error is not null)
        {
            item["error"] = new JsonObject { ["type"] = error, ["reason"] = "test" };
        }

        return new JsonObject { [op] = item };
    }

    private sealed class FixedPlacement(IReadOnlyList<IndexTarget> targets) : IIndexManager
    {
        public Task<Placement> ResolveAsync(Guid workspaceId, IndexPurpose purpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Placement(workspaceId, IndexPlacementKind.Shared, 2, IndexPlacementState.Active, targets[0], targets));

        public Task<Placement> PlaceAsync(Guid workspaceId, WorkspacePlacementRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Placement> BeginRebuildAsync(Guid workspaceId, IndexRebuildRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Placement> CompleteRebuildAsync(Guid workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Placement> AbortRebuildAsync(Guid workspaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Answers <c>_bulk</c> with <see cref="Items"/> (or 201 for every action) and records each request.</summary>
    private sealed class FakeBulk : HttpMessageHandler
    {
        public List<(long Bytes, int Actions)> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public IReadOnlyList<JsonObject>? Items { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var actions = body.Split('\n').Count(l => l.StartsWith("{\"index\":", StringComparison.Ordinal) || l.StartsWith("{\"delete\":", StringComparison.Ordinal));
            Requests.Add((request.Content.Headers.ContentLength!.Value, actions));
            Bodies.Add(body);
            var items = new JsonArray([.. (Items ?? Enumerable.Range(0, actions).Select(_ => Item(201))).Select(i => (JsonNode)i.DeepClone())]);
            var response = Status == HttpStatusCode.OK
                ? new JsonObject { ["errors"] = false, ["items"] = items }
                : new JsonObject { ["error"] = new JsonObject { ["type"] = "circuit_breaking_exception" } };
            return new HttpResponseMessage(Status) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
