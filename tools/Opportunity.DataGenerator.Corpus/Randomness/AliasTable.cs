namespace Opportunity.DataGenerator.Corpus.Randomness;

/// <summary>O(1) sampling from a fixed discrete distribution (Vose alias method).</summary>
public sealed class AliasTable
{
    private readonly double[] _probability;
    private readonly int[] _alias;

    public AliasTable(IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        int n = weights.Count;
        if (n == 0)
        {
            throw new ArgumentException("At least one weight is required.", nameof(weights));
        }

        double total = 0;
        foreach (double w in weights)
        {
            if (w < 0 || double.IsNaN(w))
            {
                throw new ArgumentException("Weights must be non-negative.", nameof(weights));
            }

            total += w;
        }

        if (total <= 0)
        {
            throw new ArgumentException("Weights must not all be zero.", nameof(weights));
        }

        _probability = new double[n];
        _alias = new int[n];
        double[] scaled = new double[n];
        var small = new Stack<int>();
        var large = new Stack<int>();
        for (int i = n - 1; i >= 0; i--)
        {
            scaled[i] = weights[i] * n / total;
            (scaled[i] < 1.0 ? small : large).Push(i);
        }

        while (small.Count > 0 && large.Count > 0)
        {
            int s = small.Pop();
            int l = large.Pop();
            _probability[s] = scaled[s];
            _alias[s] = l;
            scaled[l] = scaled[l] + scaled[s] - 1.0;
            (scaled[l] < 1.0 ? small : large).Push(l);
        }

        while (large.Count > 0)
        {
            _probability[large.Pop()] = 1.0;
        }

        while (small.Count > 0)
        {
            _probability[small.Pop()] = 1.0;
        }
    }

    public int Count => _probability.Length;

    public int Sample(Rng rng)
    {
        int column = rng.NextInt(_probability.Length);
        return rng.NextDouble() < _probability[column] ? column : _alias[column];
    }

    /// <summary>Weights proportional to 1 / (rank + 1 + offset)^exponent for ranks 0..count-1.</summary>
    public static double[] ZipfWeights(int count, double exponent, double offset = 0)
    {
        double[] weights = new double[count];
        for (int i = 0; i < count; i++)
        {
            weights[i] = 1.0 / Math.Pow(i + 1 + offset, exponent);
        }

        return weights;
    }
}

/// <summary>A Zipf-like (truncated power-law) distribution over the integers [min, max].</summary>
public sealed class PowerLawRange
{
    private readonly AliasTable _table;

    public PowerLawRange(int min, int max, double exponent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(min);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(min, max);
        Min = min;
        Max = max;
        double[] weights = new double[max - min + 1];
        double weightSum = 0;
        double weightedSum = 0;
        for (int k = min; k <= max; k++)
        {
            double w = 1.0 / Math.Pow(k, exponent);
            weights[k - min] = w;
            weightSum += w;
            weightedSum += w * k;
        }

        Mean = weightedSum / weightSum;
        _table = new AliasTable(weights);
    }

    public int Min { get; }

    public int Max { get; }

    public double Mean { get; }

    public int Sample(Rng rng) => Min + _table.Sample(rng);
}
