using System.Globalization;
using System.Text.RegularExpressions;

namespace Opportunity.Core.ReviewBatches;

/// <summary>
/// Where a batch is in review (familiarity guide §5.3: Available → Checked out (user) → Completed). Stored as smallint;
/// values are fixed forever.
/// </summary>
public enum ReviewBatchStatus : short
{
    /// <summary>Nobody holds the batch; a reviewer may check it out.</summary>
    Available = 1,

    /// <summary>One reviewer holds the batch (checked out by themselves or assigned by a manager).</summary>
    CheckedOut = 2,

    /// <summary>Checked in as done; the last reviewer stays recorded. A manager may reassign it for re-review.</summary>
    Completed = 3,
}

/// <summary>Which pass a Batch Set serves. Stored as smallint; values are fixed forever.</summary>
public enum ReviewPass : short
{
    FirstPass = 1,

    /// <summary>Quality control of a first-pass Batch Set: its calls are compared with the first pass (conflicts).</summary>
    Qc = 2,
}

/// <summary>How a checkout ended. Stored as smallint; values are fixed forever.</summary>
public enum ReviewCheckoutEnd : short
{
    /// <summary>Checked in without completing: the batch is Available again.</summary>
    Returned = 1,

    /// <summary>Checked in as done: the batch is Completed.</summary>
    Completed = 2,

    /// <summary>A manager assigned the batch to someone else, or made it Available.</summary>
    Reassigned = 3,
}

public static partial class ReviewBatchRules
{
    public const int MaxBatchSize = 10_000;
    public const int MaxNameLength = 200;
    public const int MaxPrefixLength = 50;
    public const int MaxGroupNameLength = 256;

    /// <summary>Batch names are <c>{prefix}_0001</c>, <c>{prefix}_0002</c>, … (familiarity guide §5.3).</summary>
    public static string BatchName(string prefix, int ordinal) =>
        prefix + "_" + ordinal.ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>A prefix is letters, digits, spaces, dots, hyphens and underscores, starting with a letter or digit.</summary>
    public static bool IsValidPrefix(string? prefix) =>
        prefix is { Length: > 0 and <= MaxPrefixLength } && PrefixPattern().IsMatch(prefix);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}

/// <summary>
/// Cuts an ordered stream of keep-together groups (a document, a family, or an email thread with its families) into
/// batches of at most <see cref="MaxSize"/> documents (familiarity guide §5.3, eDiscovery review E10-T05): groups are
/// never split, so a batch exceeds the size only when one group alone is larger; a group that does not fit in the open
/// batch starts the next one. Pure and streaming: memory is one group.
/// </summary>
public sealed class ReviewBatchPacker
{
    private int _openCount;

    public ReviewBatchPacker(int maxSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSize, ReviewBatchRules.MaxBatchSize);
        MaxSize = maxSize;
    }

    public int MaxSize { get; }

    /// <summary>Ordinal (1…) of the batch the last group went to; 0 before the first group.</summary>
    public int CurrentBatch { get; private set; }

    /// <summary>Places a group of <paramref name="size"/> documents and returns its batch ordinal.</summary>
    public int Place(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        if (CurrentBatch == 0 || (_openCount > 0 && _openCount + size > MaxSize))
        {
            CurrentBatch++;
            _openCount = 0;
        }

        _openCount += size;
        return CurrentBatch;
    }
}
