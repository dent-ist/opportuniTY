using HdrHistogram;
using HdrHistogram.Utilities;

namespace Opportunity.Benchmarks.Bundles;

/// <summary>
/// HDR histograms in bundles: microsecond latencies, V2 compressed encoding (the format HdrHistogram's Java, Go and
/// JS ports read), base64. Percentiles in the summary are computed from the histogram, which stays authoritative.
/// </summary>
public static class Latency
{
    /// <summary>1 µs .. 1 h at 3 significant digits: ±0.1% for every recorded latency.</summary>
    public static LongHistogram NewHistogram() => new(1, TimeSpan.FromHours(1).Ticks / 10, 3);

    public static HdrPayload Encode(HistogramBase histogram)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        ByteBuffer buffer = ByteBuffer.Allocate(histogram.GetNeededByteBufferCapacity());
        int length = histogram.EncodeIntoCompressedByteBuffer(buffer);

        // Only the first `length` bytes are the encoding; the library's own log writer emits the whole (mostly empty) buffer.
        var bytes = new byte[length];
        buffer.Position = 0;
        for (int i = 0; i < length; i++)
        {
            bytes[i] = buffer.Get();
        }

        return new HdrPayload
        {
            LowestTrackableValue = histogram.LowestTrackableValue,
            HighestTrackableValue = histogram.HighestTrackableValue,
            SignificantDigits = histogram.NumberOfSignificantValueDigits,
            Payload = Convert.ToBase64String(bytes),
        };
    }

    public static HistogramBase Decode(HdrPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Encoding != HdrPayload.V2CompressedBase64)
        {
            throw new FormatException($"Unsupported histogram encoding '{payload.Encoding}'.");
        }

        ByteBuffer buffer = ByteBuffer.Allocate(Convert.FromBase64String(payload.Payload));
        return HistogramEncoding.DecodeFromCompressedByteBuffer(buffer, payload.HighestTrackableValue);
    }

    public static LatencySummary Summarize(HistogramBase histogram)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        long count = histogram.TotalCount;
        long At(double percentile) => count == 0 ? 0 : histogram.GetValueAtPercentile(percentile);
        return new LatencySummary
        {
            Count = count,
            Min = count == 0 ? 0 : histogram.RecordedValues().Select(v => histogram.LowestEquivalentValue(v.ValueIteratedTo)).FirstOrDefault(),
            P50 = At(50),
            P90 = At(90),
            P95 = At(95),
            P99 = At(99),
            P999 = At(99.9),
            Max = count == 0 ? 0 : histogram.GetMaxValue(),
            Mean = count == 0 ? 0 : Math.Round(histogram.GetMean(), 3),
            Hdr = Encode(histogram),
        };
    }
}
