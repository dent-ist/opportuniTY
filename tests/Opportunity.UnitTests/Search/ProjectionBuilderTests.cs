using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Search.Projection;
using Opportunity.Application.Storage;
using Opportunity.Core.Fields;
using Opportunity.Search;
using Opportunity.Search.Projection;

namespace Opportunity.UnitTests.Search;

/// <summary>
/// E07-T02: the interim Candidate A projection builder against golden documents (<c>Search/Golden/*.json</c>). Golden
/// files change only with a reviewed projection change; set <c>OPP_UPDATE_GOLDEN=1</c> to rewrite them.
/// </summary>
public sealed class ProjectionBuilderTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static ProjectionText SampleText => new("Quarterly results: the café pricing memo.", false);

    [Fact]
    public void Live_document_matches_its_golden_projection()
    {
        var result = new CandidateAProjectionBuilder().Build(ProjectionSamples.Live(), ProjectionSamples.Catalog(), SampleText);

        result.Version.Should().Be(7);
        result.Generation.Should().Be(2);
        result.DroppedFieldIds.Should().BeEmpty();
        var write = result.Writes.Should().ContainSingle().Subject;
        write.Should().BeEquivalentTo(new { Id = ProjectionSamples.DocumentId.ToString("D"), Kind = ProjectionWriteKind.Index, Version = 7L });
        AssertGolden("projection-v2-live.json", write.Body!);
    }

    [Fact]
    public void Deleted_and_missing_documents_become_version_guarded_deletes()
    {
        var builder = new CandidateAProjectionBuilder();
        var deleted = builder.Build(new ProjectionSource
        {
            WorkspaceId = ProjectionSamples.WorkspaceId,
            DocumentId = ProjectionSamples.DocumentId,
            State = ProjectionSourceState.Deleted,
            DocumentVersion = 9,
        }, ProjectionSamples.Catalog(), null);
        deleted.Writes.Should().Equal(new ProjectionWrite(ProjectionSamples.DocumentId.ToString("D"), ProjectionWriteKind.Delete, 9, null));

        var missing = builder.Build(ProjectionSource.Missing(ProjectionSamples.WorkspaceId, ProjectionSamples.DocumentId), ProjectionSamples.Catalog(), null);
        missing.Version.Should().BeNull();
        missing.Writes.Should().Equal(new ProjectionWrite(ProjectionSamples.DocumentId.ToString("D"), ProjectionWriteKind.DeleteUnconditional, null, null));
    }

    [Fact]
    public void Every_path_of_a_projection_exists_in_the_strict_mapping()
    {
        var body = new CandidateAProjectionBuilder().Build(ProjectionSamples.Live(), ProjectionSamples.Catalog(), SampleText).Writes[0].Body!;

        foreach (var path in LeafPaths(body, string.Empty))
        {
            var node = ProjectionMappingTests.Resolve(path);
            if (node is null && path.IndexOf('.', StringComparison.Ordinal) is var dot and > 0)
            {
                // Keys inside a flat_object container are free-form.
                node = ProjectionMappingTests.Resolve(path[..dot]);
                (node?["type"]?.GetValue<string>()).Should().Be("flat_object", path);
                continue;
            }

            node.Should().NotBeNull("{0} must be mapped (dynamic: strict)", path);
            node!["properties"].Should().BeNull("{0} is a leaf", path);
        }
    }

    [Fact]
    public void Values_that_do_not_fit_their_slot_are_dropped_and_reported()
    {
        var source = ProjectionSamples.Live();
        var document = source.Document!;
        document.Metadata = """{"f1004": "forty-two", "f1003": 1.5}""";
        var result = new CandidateAProjectionBuilder().Build(
            source with { Coding = new Dictionary<int, JsonNode> { [ProjectionSamples.Issues] = JsonValue.Create("x") } },
            ProjectionSamples.Catalog(), null);

        result.DroppedFieldIds.Should().Equal(ProjectionSamples.MessageCount, ProjectionSamples.Issues);
        var body = result.Writes[0].Body!;
        body["metadata"]!.ToJsonString().Should().Be("""{"dec":{"s001":1.5}}""");
        body["coding"].Should().BeNull();
        body["text"].Should().BeNull();
    }

    [Fact]
    public void Truncated_text_sets_the_flag_and_text_length_falls_back_to_the_indexed_length()
    {
        var source = ProjectionSamples.Live();
        source.Document!.TextLength = null;
        var builder = new CandidateAProjectionBuilder();

        var full = builder.Build(source, ProjectionSamples.Catalog(), new ProjectionText("abc", false)).Writes[0].Body!;
        full["textLength"]!.GetValue<long>().Should().Be(3);
        full["textTruncated"]!.GetValue<bool>().Should().BeFalse();

        var cut = builder.Build(source, ProjectionSamples.Catalog(), new ProjectionText("abc", true)).Writes[0].Body!;
        cut["textTruncated"]!.GetValue<bool>().Should().BeTrue();
        cut["textLength"].Should().BeNull("the full length is unknown without the PostgreSQL value");
    }

    [Fact]
    public void Natural_sort_keys_order_control_numbers_and_bates_like_humans_do()
    {
        var builder = new CandidateAProjectionBuilder();
        string Key(string controlNumber, string bates)
        {
            var source = ProjectionSamples.Live();
            var document = source.Document!;
            document.ControlNumber = controlNumber;
            document.ControlNumberNorm = controlNumber.ToUpperInvariant();
            document.ControlNumberSortKey = string.Empty;
            document.BegBates = bates;
            var body = builder.Build(source, ProjectionSamples.Catalog(), null).Writes[0].Body!;
            body["begBatesSort"]!.GetValue<string>().Should().Be(body["controlNumberSort"]!.GetValue<string>().Replace("ABC", "PROD", StringComparison.Ordinal));
            return body["controlNumberSort"]!.GetValue<string>();
        }

        var keys = new[] { Key("abc10", "prod10"), Key("ABC9", "PROD9"), Key("ABC0011", "prod0011") };
        keys.Order(StringComparer.Ordinal).Should().Equal(keys[1], keys[0], keys[2]);
    }

    [Fact]
    public async Task A_swapped_builder_serves_the_same_callers()
    {
        var reader = new FakeReader();
        var services = new ServiceCollection()
            .AddSingleton<IProjectionSourceReader>(reader)
            .AddSingleton<IObjectStore>(new NoObjects())
            .AddSingleton<IProjectionTextLoader>(new FixedText(SampleText))
            .AddSingleton<IProjectionBuilder, SeparateCodingBuilder>() // a Candidate B stand-in, registered first
            .AddSearchProjection()
            .BuildServiceProvider();

        var service = services.GetRequiredService<IProjectionService>();
        var documents = await service.BuildAsync(ProjectionSamples.WorkspaceId, [ProjectionSamples.DocumentId], TestContext.Current.CancellationToken);

        service.Generation.Should().Be(99);
        documents.Single().Writes.Select(w => w.Id).Should().Equal(
            ProjectionSamples.DocumentId.ToString("D"), ProjectionSamples.DocumentId.ToString("D") + "#coding");
        reader.Calls.Should().Be(1);

        // The default registration is Candidate A.
        var defaults = new ServiceCollection()
            .AddSingleton<IProjectionSourceReader>(reader)
            .AddSingleton<IObjectStore>(new NoObjects())
            .AddSearchProjection()
            .BuildServiceProvider();
        defaults.GetRequiredService<IProjectionBuilder>().Should().BeOfType<CandidateAProjectionBuilder>();
    }

    [Fact]
    public async Task The_service_loads_text_only_for_live_documents()
    {
        var reader = new FakeReader { Extra = ProjectionSource.Missing(ProjectionSamples.WorkspaceId, Guid.NewGuid()) };
        var texts = new FixedText(SampleText);
        var service = new ServiceCollection()
            .AddSingleton<IProjectionSourceReader>(reader)
            .AddSingleton<IObjectStore>(new NoObjects())
            .AddSingleton<IProjectionTextLoader>(texts)
            .AddSearchProjection()
            .BuildServiceProvider()
            .GetRequiredService<IProjectionService>();

        var documents = await service.BuildAsync(ProjectionSamples.WorkspaceId, [ProjectionSamples.DocumentId], TestContext.Current.CancellationToken);

        documents.Select(d => d.Writes[0].Kind).Should().Equal(ProjectionWriteKind.Index, ProjectionWriteKind.DeleteUnconditional);
        texts.Calls.Should().Be(1);
        documents[0].Writes[0].Body!["text"]!.GetValue<string>().Should().Be(SampleText.Text);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(50_000_001)]
    public void The_text_cap_is_validated(int cap)
    {
        new ProjectionOptions { IndexedTextCap = cap }.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*IndexedTextCap*");
    }

    private static void AssertGolden(string fileName, JsonObject actual)
    {
        var path = Path.Combine(RepositoryRoot(), "tests", "Opportunity.UnitTests", "Search", "Golden", fileName);
        var actualText = actual.ToJsonString(Indented) + "\n";
        if (Environment.GetEnvironmentVariable("OPP_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actualText);
        }

        File.Exists(path).Should().BeTrue("golden file {0} exists; actual:\n{1}", fileName, actualText);
        JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), actual).Should().BeTrue(
            "the projection must match {0}; actual:\n{1}", fileName, actualText);
    }

    private static IEnumerable<string> LeafPaths(JsonObject node, string prefix)
    {
        foreach (var (name, value) in node)
        {
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            if (value is JsonObject inner)
            {
                foreach (var child in LeafPaths(inner, path))
                {
                    yield return child;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Opportunity.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Opportunity.slnx) not found.");
    }

    private sealed class FakeReader : IProjectionSourceReader
    {
        public int Calls { get; private set; }

        public ProjectionSource? Extra { get; init; }

        public Task<ProjectionSourceBatch> ReadAsync(Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlyList<ProjectionSource> documents = Extra is null ? [ProjectionSamples.Live()] : [ProjectionSamples.Live(), Extra];
            return Task.FromResult(new ProjectionSourceBatch(workspaceId, ProjectionSamples.Catalog(), documents));
        }
    }

    private sealed class FixedText(ProjectionText text) : IProjectionTextLoader
    {
        public int Calls { get; private set; }

        public Task<ProjectionText?> LoadAsync(ProjectionSource source, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ProjectionText?>(text);
        }
    }

    private sealed class SeparateCodingBuilder : IProjectionBuilder
    {
        public int Generation => 99;

        public ProjectionDocument Build(ProjectionSource source, FieldCatalog catalog, ProjectionText? text)
        {
            var id = source.DocumentId.ToString("D");
            return new ProjectionDocument(source.WorkspaceId, source.DocumentId, source.DocumentVersion, Generation,
            [
                new ProjectionWrite(id, ProjectionWriteKind.Index, source.DocumentVersion, []),
                new ProjectionWrite(id + "#coding", ProjectionWriteKind.Index, source.DocumentVersion, []),
            ], []);
        }
    }

    private sealed class NoObjects : IObjectStore
    {
        public Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(ObjectKey key, ByteRange? range = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeletePrefixResult> DeletePrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
