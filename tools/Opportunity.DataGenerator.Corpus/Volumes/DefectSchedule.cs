using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>
/// Deterministic "jittered systematic" selection: the stream of eligible items is cut into intervals of length
/// 1/rate and exactly one item is picked at a seeded random position inside each interval. After N eligible items
/// the injected count is N × rate rounded either way (±1), so configured and actual rates agree at any size, while
/// positions stay irregular. Item k is chosen purely from (seed, stream, k), never from other defect types.
/// </summary>
public sealed class DefectSchedule
{
    private readonly ulong _seed;
    private readonly ulong _stream;
    private readonly double _rate;
    private long _interval;
    private long _next;

    public DefectSchedule(ulong seed, ulong stream, double rate)
    {
        _seed = seed;
        _stream = stream;
        _rate = rate;
        _next = rate <= 0 ? long.MaxValue : Target(0);
    }

    public double Rate => _rate;

    public long Eligible { get; private set; }

    public long Selected { get; private set; }

    /// <summary>Call once per eligible item, in load order; returns true when the item is selected.</summary>
    public bool Next()
    {
        long k = Eligible++;
        if (_rate >= 1 || k == _next)
        {
            Selected++;
            if (_rate < 1)
            {
                _interval++;
                _next = Math.Max(Target(_interval), k + 1);
            }

            return true;
        }

        return false;
    }

    private long Target(long interval)
    {
        double u = new Rng(StableHash.Of(_seed, StreamTag.VolumeDefect, _stream, (ulong)interval)).NextDouble();
        return (long)Math.Floor((interval + u) / _rate);
    }
}
