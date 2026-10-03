using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

using AwesomeAssertions;

using Opportunity.Application.Telemetry;

namespace Opportunity.UnitTests.Telemetry;

/// <summary>ADR-017 §5: the metric catalog follows OpenTelemetry naming and unit conventions and is documented.</summary>
public sealed partial class MetricCatalogTests
{
    private static readonly string[] Units = ["s", "By", "1", "{record}", "{message}", "{consumer}", "{generation}", "{document}", "{task}", "{job}", "{chunk}"];

    public static TheoryData<string> Names => new(OpportunityMetricCatalog.All.Select(m => m.Name));

    [Theory]
    [MemberData(nameof(Names))]
    public void Names_are_lowercase_dotted_namespaced_and_unit_free(string name)
    {
        NamePattern().IsMatch(name).Should().BeTrue("OpenTelemetry names are lowercase, dot-separated namespaces");
        (name.StartsWith("opportunity.", StringComparison.Ordinal) || name.StartsWith("messaging.", StringComparison.Ordinal))
            .Should().BeTrue("application metrics live under opportunity.*, or use a semantic-convention name");
        name.Should().NotEndWith("_seconds").And.NotEndWith(".seconds").And.NotEndWith("_total", "the unit and counter suffixes are added by the Prometheus translation");
    }

    [Fact]
    public void Units_are_ucum_and_names_unique()
    {
        OpportunityMetricCatalog.All.Should().OnlyContain(m => Units.Contains(m.Unit));
        OpportunityMetricCatalog.All.Select(m => m.Name).Should().OnlyHaveUniqueItems();
        OpportunityMetricCatalog.All.Should().OnlyContain(m => m.Attributes.Count > 0 || m.Kind == MetricKind.Histogram);
        OpportunityMetricCatalog.All.Where(m => m.Kind == MetricKind.Histogram).Should().OnlyContain(m => m.Buckets != null && m.Unit == "s");
    }

    [Fact]
    public void Slo_metrics_from_adr_001_are_defined()
    {
        OpportunityMetricCatalog.SecurityProjectionLag.Buckets.Should().Contain(5, "Q-10: <= 5 s p95 must be a bucket boundary");
        OpportunityMetricCatalog.SearchCommitToSearchable.Buckets.Should().Contain([1, 120]);
        OpportunityMetricCatalog.SearchIndexLag.Kind.Should().Be(MetricKind.Gauge);
    }

    [Fact]
    public void Every_catalog_metric_is_documented_in_adr_017()
    {
        var adr = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "adr", "0017-observability-and-slos.md"));

        OpportunityMetricCatalog.All.Should().OnlyContain(m => adr.Contains($"`{m.Name}`", StringComparison.Ordinal));
    }

    [Fact]
    public void Instruments_are_created_once_per_definition_and_kind_is_enforced()
    {
        using var factory = new TestMeterFactory();
        var metrics = new OpportunityMetrics(factory);

        metrics.Histogram(OpportunityMetricCatalog.JobChunkDuration)
            .Should().BeSameAs(metrics.Histogram(OpportunityMetricCatalog.JobChunkDuration));
        metrics.Histogram(OpportunityMetricCatalog.JobChunkDuration).Unit.Should().Be("s");

        var wrongKind = () => metrics.Counter(OpportunityMetricCatalog.JobChunkDuration);
        wrongKind.Should().Throw<ArgumentException>();
        var otherMeter = () => metrics.Gauge(OpportunityMetricCatalog.WorkerHeartbeatAge, () => Array.Empty<Measurement<double>>());
        otherMeter.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Workspace_attribute_values_are_bounded()
    {
        var bounded = new BoundedAttributeValues(capacity: 2);

        bounded.Map("a").Should().Be("a");
        bounded.Map("b").Should().Be("b");
        bounded.Map("c").Should().Be(BoundedAttributeValues.Overflow);
        bounded.Map("a").Should().Be("a");
        Enumerable.Range(0, 1000).Select(i => bounded.Map($"ws-{i}")).Distinct().Should().Equal(BoundedAttributeValues.Overflow);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Opportunity.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Opportunity.slnx) not found.");
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex NamePattern();

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose() => _meters.ForEach(m => m.Dispose());
    }
}
