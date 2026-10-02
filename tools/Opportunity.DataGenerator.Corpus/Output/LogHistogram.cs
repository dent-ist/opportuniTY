namespace Opportunity.DataGenerator.Corpus.Output;

/// <summary>
/// Fixed-memory quantile sketch for positive integers: ~1% wide buckets built by integer-exact growth, so the
/// reported quantiles are identical on every platform. Quantiles are bucket upper bounds (≤ 1% high).
/// </summary>
public sealed class LogHistogram
{
    private static readonly long[] Bounds = BuildBounds();
    private readonly long[] _counts = new long[Bounds.Length + 1];

    public long Count { get; private set; }

    public long Sum { get; private set; }

    public long Max { get; private set; }

    public void Add(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        int index = Array.BinarySearch(Bounds, value);
        _counts[index >= 0 ? index : ~index]++;
        Count++;
        Sum += value;
        Max = Math.Max(Max, value);
    }

    public long Quantile(double q)
    {
        if (Count == 0)
        {
            return 0;
        }

        long rank = Math.Max(1, (long)Math.Ceiling(q * Count));
        long seen = 0;
        for (int i = 0; i < _counts.Length; i++)
        {
            seen += _counts[i];
            if (seen >= rank)
            {
                return i < Bounds.Length ? Math.Min(Bounds[i], Max) : Max;
            }
        }

        return Max;
    }

    public double Mean => Count == 0 ? 0 : (double)Sum / Count;

    private static long[] BuildBounds()
    {
        var bounds = new List<long>();
        long t = 0;
        while (t < (1L << 42))
        {
            bounds.Add(t);
            t = Math.Max(t + 1, t + (t / 100));
        }

        return [.. bounds];
    }
}