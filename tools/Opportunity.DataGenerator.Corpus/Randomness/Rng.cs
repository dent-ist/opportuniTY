namespace Opportunity.DataGenerator.Corpus.Randomness;

/// <summary>
/// xoshiro256** generator seeded from a 64-bit key. Fully specified integer arithmetic, so a key yields the
/// same sequence on every platform. Not thread-safe; create one per entity stream.
/// </summary>
public sealed class Rng
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public Rng(ulong key)
    {
        ulong x = key;
        _s0 = Next(ref x);
        _s1 = Next(ref x);
        _s2 = Next(ref x);
        _s3 = Next(ref x);

        static ulong Next(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    public static Rng For(ulong seed, StreamTag tag, ulong a = 0, ulong b = 0, ulong c = 0) =>
        new(StableHash.Of(seed, tag, a, b, c));

    public ulong NextUInt64()
    {
        ulong result = RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform in (0, 1], safe for logarithms.</summary>
    public double NextDoubleNonZero() => ((NextUInt64() >> 11) + 1) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [0, maxExclusive) without modulo bias (Lemire).</summary>
    public int NextInt(int maxExclusive) => (int)NextInt64(maxExclusive);

    public long NextInt64(long maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        ulong range = (ulong)maxExclusive;
        UInt128 m = (UInt128)NextUInt64() * range;
        ulong low = (ulong)m;
        if (low < range)
        {
            ulong threshold = (0 - range) % range;
            while (low < threshold)
            {
                m = (UInt128)NextUInt64() * range;
                low = (ulong)m;
            }
        }

        return (long)(ulong)(m >> 64);
    }

    /// <summary>Uniform integer in [min, max] inclusive.</summary>
    public int NextInt(int min, int max) => min + NextInt(max - min + 1);

    public bool Chance(double probability) => probability > 0 && NextDouble() < probability;

    /// <summary>Geometric count on {0, 1, 2, ...} with the given mean.</summary>
    public int Geometric(double mean)
    {
        if (mean <= 0)
        {
            return 0;
        }

        double continueProbability = mean / (1 + mean);
        double value = Math.Floor(Math.Log(NextDoubleNonZero()) / Math.Log(continueProbability));
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    /// <summary>Standard normal via Box-Muller (one value per call; no cached state).</summary>
    public double Normal()
    {
        double u1 = NextDoubleNonZero();
        double u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public double LogNormal(double mu, double sigma) => Math.Exp(mu + (sigma * Normal()));

    /// <summary>Exponential with the given mean.</summary>
    public double Exponential(double mean) => -mean * Math.Log(NextDoubleNonZero());

    public void NextBytes(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(NextUInt64() >> 56);
        }
    }

    public T Pick<T>(IReadOnlyList<T> items) => items[NextInt(items.Count)];

    public void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));
}
