using AwesomeAssertions;

using HdrHistogram;

using Opportunity.Benchmarks.Bundles;

namespace Opportunity.Benchmarks.Tests;

public class LatencyTests
{
    [Fact]
    public void Histogram_round_trips_through_the_bundle_encoding()
    {
        LongHistogram histogram = Latency.NewHistogram();
        for (long us = 1; us <= 10_000; us++)
        {
            histogram.RecordValue(us * 100);
        }

        LatencySummary summary = Latency.Summarize(histogram);
        HistogramBase decoded = Latency.Decode(summary.Hdr);

        decoded.TotalCount.Should().Be(10_000);
        Latency.Summarize(decoded).Should().BeEquivalentTo(summary);
        summary.P50.Should().BeCloseTo(500_000, 500);
        summary.P95.Should().BeCloseTo(950_000, 1_000);
        summary.P99.Should().BeCloseTo(990_000, 1_000);
        summary.Max.Should().BeCloseTo(1_000_000, 1_000);
        summary.Min.Should().Be(100);
        summary.Hdr.Encoding.Should().Be(HdrPayload.V2CompressedBase64);

        // V2 compressed cookie (0x1c849314, big-endian), as the Java/Go/JS ports expect; and compact.
        byte[] payload = Convert.FromBase64String(summary.Hdr.Payload);
        payload[..4].Should().Equal(0x1c, 0x84, 0x93, 0x14);
        payload.Length.Should().BeLessThan(4_096);
    }

    [Fact]
    public void Empty_histogram_summarizes_to_zeroes()
    {
        LatencySummary summary = Latency.Summarize(Latency.NewHistogram());

        summary.Count.Should().Be(0);
        summary.P95.Should().Be(0);
        Latency.Decode(summary.Hdr).TotalCount.Should().Be(0);
    }

    [Fact]
    public void Run_ids_sort_by_time_and_satisfy_the_schema_pattern()
    {
        string id = References.RunId(new DateTime(2026, 10, 3, 9, 5, 7, DateTimeKind.Utc), "ADR004/Degradation Simple", "Candidate B", 2);

        id.Should().Be("20261003t090507z-adr004-degradation-simple-candidate-b-r2");
        id.Should().MatchRegex("^[a-z0-9][a-z0-9._-]{7,127}$");
    }
}
