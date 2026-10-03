using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

using Opportunity.Application.Search.Indexing;
using Opportunity.Search;
using Opportunity.Search.Indexing;

namespace Opportunity.UnitTests.Search;

public sealed class PlacementPolicyTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static OpenSearchOptions Options() => new() { Endpoint = new Uri("http://localhost:9200/") };

    [Fact]
    public void Small_workspaces_go_to_the_shared_pool_with_the_configured_primaries()
    {
        var options = Options();
        options.SharedPrimaryShards = 3;

        var decision = PlacementPolicy.Decide(options, new WorkspacePlacementRequest(ExpectedDocuments: 100_000, ExpectedIndexedBytes: GiB));

        decision.Should().Be(new PlacementDecision(IndexPlacementKind.Shared, 3, (long)Math.Ceiling(GiB * 1.3)));
    }

    [Theory]
    [InlineData(true, 0L, 0L)]
    [InlineData(false, 5_000_000L, 0L)]
    [InlineData(false, 0L, 40L * GiB)] // 40 GB x 1.3 calibration = 52 GB >= 50 GB
    public void Flag_document_count_or_calibrated_bytes_place_dedicated(bool flag, long docs, long bytes)
    {
        PlacementPolicy.Decide(Options(), new WorkspacePlacementRequest(flag, docs, bytes)).Kind.Should().Be(IndexPlacementKind.Dedicated);
    }

    [Fact]
    public void Just_below_every_threshold_stays_shared()
    {
        var bytes = (long)(50 * GiB / 1.3) - 1024;
        PlacementPolicy.Decide(Options(), new WorkspacePlacementRequest(false, 4_999_999, bytes)).Kind.Should().Be(IndexPlacementKind.Shared);
    }

    [Fact]
    public void Thresholds_come_from_configuration()
    {
        var options = Options();
        options.Placement.DedicatedDocuments = 1000;
        options.Placement.DedicatedBytes = 2 * GiB;
        options.Placement.SizeCalibrationFactor = 1.0;

        PlacementPolicy.Decide(options, new WorkspacePlacementRequest(ExpectedDocuments: 1000)).Kind.Should().Be(IndexPlacementKind.Dedicated);
        PlacementPolicy.Decide(options, new WorkspacePlacementRequest(ExpectedIndexedBytes: 2 * GiB)).Kind.Should().Be(IndexPlacementKind.Dedicated);
        PlacementPolicy.Decide(options, new WorkspacePlacementRequest(ExpectedDocuments: 999, ExpectedIndexedBytes: GiB))
            .Kind.Should().Be(IndexPlacementKind.Shared);
    }

    [Theory]
    [InlineData(10L, 1)]
    [InlineData(49L, 1)]
    [InlineData(50L, 3)]   // ceil(1.5 x 50 / 30) = 3
    [InlineData(100L, 5)]  // ceil(1.5 x 100 / 30) = 5
    public void Dedicated_primaries_follow_the_multi_shard_formula(long gib, int expected)
    {
        PlacementPolicy.DedicatedPrimaryShards(new IndexPlacementOptions(), gib * GiB).Should().Be(expected);
    }

    [Fact]
    public void Multi_shard_never_has_fewer_than_two_primaries()
    {
        var placement = new IndexPlacementOptions { MultiShardBytes = GiB, TargetShardBytes = 50 * GiB };
        PlacementPolicy.DedicatedPrimaryShards(placement, GiB).Should().Be(2);
    }

    [Fact]
    public void Shared_index_closes_at_the_configured_share_of_its_capacity()
    {
        var options = Options();
        options.SharedPrimaryShards = 3;
        PlacementPolicy.SharedIndexCloseAtBytes(options).Should().Be((long)(3 * 30.0 * GiB * 0.7));
    }
}

public sealed class OpenSearchOptionsTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Binds_the_connection_string_and_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OpenSearch"] = "http://opensearch:9200",
            ["OpenSearch:IndexPrefix"] = "acme",
            ["OpenSearch:SharedPrimaryShards"] = "3",
            ["OpenSearch:Placement:DedicatedDocuments"] = "1000000",
            ["OpenSearch:Placement:MaxSharedIndexes"] = "8",
        }).Build();

        var options = OpenSearchOptions.Bind(configuration);
        options.Validate();

        options.Endpoint.Should().Be(new Uri("http://opensearch:9200"));
        options.IndexPrefix.Should().Be("acme");
        options.SharedPrimaryShards.Should().Be(3);
        options.Placement.DedicatedDocuments.Should().Be(1_000_000);
        options.Placement.MaxSharedIndexes.Should().Be(8);
    }

    public static TheoryData<string, Action<OpenSearchOptions>> InvalidSettings => new()
    {
        { "IndexPrefix", o => o.IndexPrefix = "Upper" },
        { "IndexPrefix", o => o.IndexPrefix = "-dash" },
        { "DedicatedBytes", o => o.Placement.DedicatedBytes = 51 * GiB },
        { "TargetShardBytes", o => o.Placement.TargetShardBytes = 5 * GiB },
        { "CacheTtl", o => o.Placement.CacheTtl = TimeSpan.FromSeconds(6) },
        { "MaxSharedIndexes", o => o.Placement.MaxSharedIndexes = 0 },
        { "SharedIndexCloseAtFraction", o => o.Placement.SharedIndexCloseAtFraction = 1.5 },
        { "ConnectionStrings:OpenSearch", o => o.Endpoint = null },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Validation_rejects_out_of_range_settings(string setting, Action<OpenSearchOptions> configure)
    {
        var options = new OpenSearchOptions { Endpoint = new Uri("http://localhost:9200") };
        configure(options);
        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().WithMessage($"*{setting}*");
    }

    [Fact]
    public void Defaults_are_valid()
    {
        new OpenSearchOptions { Endpoint = new Uri("http://localhost:9200") }.Invoking(o => o.Validate()).Should().NotThrow();
    }
}

public sealed class IndexNamingAndMappingTests
{
    [Fact]
    public void Names_follow_adr_006()
    {
        var names = new IndexNames("opp");
        var ws = Guid.Parse("0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

        names.SharedAlias(7).Should().Be("opp-shared-007");
        names.DedicatedAlias(ws).Should().Be("opp-ws-0190a1b2c3d47e5f8a9b0c1d2e3f4a5b");
        IndexNames.Physical(names.SharedAlias(7), 2).Should().Be("opp-shared-007-g2");
        names.Template(2).Should().Be("opp-projection-g2");
        names.TemplatePatterns(2).Should().Equal("opp-shared-*-g2", "opp-ws-*-g2");
    }

    [Fact]
    public void The_embedded_generation_1_mapping_is_strict_and_holds_the_structural_fields()
    {
        var mappings = ProjectionMappings.Embedded;
        mappings.Generations.Should().Contain(1);
        var root = mappings.Load(1)["mappings"]!.AsObject();
        root["dynamic"]!.GetValue<string>().Should().Be("strict");
        root["_source"]!["excludes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("text");

        var properties = root["properties"]!.AsObject();
        properties["workspaceId"]!["type"]!.GetValue<string>().Should().Be("keyword");
        properties["securityTags"]!["type"]!.GetValue<string>().Should().Be("keyword");
        properties["text"]!["index_options"]!.GetValue<string>().Should().Be("offsets");
        properties["text"]!["store"]!.GetValue<bool>().Should().BeTrue();
        properties["metadataOverflow"]!["type"]!.GetValue<string>().Should().Be("flat_object");
        properties["fileName"]!["fields"]!["wc"]!["type"]!.GetValue<string>().Should().Be("wildcard");

        var slots = properties["metadata"]!["properties"]!.AsObject();
        var expected = new Dictionary<string, int>
        {
            ["txt"] = 100,
            ["idt"] = 50,
            ["kw"] = 300,
            ["int"] = 100,
            ["dec"] = 50,
            ["dt"] = 100,
            ["bool"] = 100,
            ["ch"] = 200,
            ["usr"] = 50,
        };
        foreach (var (kind, count) in expected)
        {
            var container = slots[kind]!.AsObject();
            container["dynamic"]!.GetValue<string>().Should().Be("strict");
            container["properties"]!.AsObject().Count.Should().Be(count, kind);
        }
    }

    [Fact]
    public void Mapping_stays_within_the_total_fields_limit()
    {
        CountFields(ProjectionMappings.Embedded.Load(1)["mappings"]!["properties"]!.AsObject()).Should().BeLessThan(2000);
    }

    private static int CountFields(JsonObject properties) => properties.Sum(p =>
        1 + (p.Value!["properties"] is JsonObject inner ? CountFields(inner) : 0)
          + (p.Value!["fields"] is JsonObject fields ? fields.Count : 0));
}
