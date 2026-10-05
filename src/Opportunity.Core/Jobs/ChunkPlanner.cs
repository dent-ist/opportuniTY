namespace Opportunity.Core.Jobs;

/// <summary>A chunk bound by item count and, where the work carries bytes, by referenced bytes (ADR-010 §6).</summary>
public sealed record ChunkBounds
{
    public ChunkBounds(int maxItems, long? maxBytes = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);
        if (maxBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "A byte bound must be positive.");
        }

        MaxItems = maxItems;
        MaxBytes = maxBytes;
    }

    public int MaxItems { get; }

    public long? MaxBytes { get; }

    private const long MiB = 1024L * 1024;

    /// <summary>Initial values of ADR-010 §6; tuned by E18-T03/E18-T04 without a new ADR.</summary>
    public static ChunkBounds For(JobType jobType, bool writesSecurityAffectingField = false) => jobType switch
    {
        JobType.BulkCoding => new(writesSecurityAffectingField ? 500 : 1_000),
        JobType.Import => new(500, 512 * MiB),
        JobType.RelationshipFixup => new(1_000),
        JobType.Reindex => new(2_000),
        JobType.Export => new(250, 2_048 * MiB),
        JobType.Production => new(100),
        JobType.Render => new(100),
        JobType.SearchTermReport => new(1_000),
        _ => throw new ArgumentOutOfRangeException(nameof(jobType), jobType, "Unknown job type."),
    };
}

/// <summary>An inclusive range of item positions (snapshot ordinals or import rows) planned as one chunk.</summary>
public readonly record struct ChunkRange(long From, long To, long Bytes)
{
    public long Count => To - From + 1;
}

/// <summary>Plans every chunk of a job up front, in <c>Preparing</c> (ADR-010 §1).</summary>
public static class ChunkPlanner
{
    /// <summary>Splits positions <paramref name="first"/>…<paramref name="last"/> into dense ranges by count only.</summary>
    public static IReadOnlyList<ChunkRange> SplitByCount(long first, long last, int maxItems)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(first);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);
        if (last < first)
        {
            return [];
        }

        var ranges = new List<ChunkRange>((int)Math.Min(int.MaxValue, ((last - first) / maxItems) + 1));
        for (var from = first; from <= last; from += maxItems)
        {
            ranges.Add(new ChunkRange(from, Math.Min(last, from + maxItems - 1), 0));
        }

        return ranges;
    }

    /// <summary>
    /// Splits consecutive items starting at position <paramref name="first"/> so that each chunk respects both bounds.
    /// An item larger than the byte bound on its own becomes a chunk of one (it cannot be split further).
    /// </summary>
    /// <param name="itemBytes">Referenced bytes per item, in position order.</param>
    public static IReadOnlyList<ChunkRange> Split(long first, IEnumerable<long> itemBytes, ChunkBounds bounds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(first);
        ArgumentNullException.ThrowIfNull(itemBytes);
        ArgumentNullException.ThrowIfNull(bounds);

        var maxBytes = bounds.MaxBytes ?? long.MaxValue;
        var ranges = new List<ChunkRange>();
        var position = first;
        long start = first, count = 0, bytes = 0;
        foreach (var size in itemBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(size, nameof(itemBytes));
            if (count > 0 && (count == bounds.MaxItems || bytes + size > maxBytes))
            {
                ranges.Add(new ChunkRange(start, position - 1, bytes));
                start = position;
                count = 0;
                bytes = 0;
            }

            count++;
            bytes += size;
            position++;
        }

        if (count > 0)
        {
            ranges.Add(new ChunkRange(start, position - 1, bytes));
        }

        return ranges;
    }
}
