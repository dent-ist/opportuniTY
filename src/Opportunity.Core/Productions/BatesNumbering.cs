using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Opportunity.Core.Productions;

/// <summary>Whether Bates numbers count pages or documents (E12-T02).</summary>
public enum BatesNumberingLevel
{
    /// <summary>One number per produced page; a placeholder or native slip sheet consumes one.</summary>
    Page,

    /// <summary>One number per document; page images are labelled with a page suffix (<c>ABC0000001.0001</c>).</summary>
    Document,
}

/// <summary>How a document is produced. Stored as smallint; values are fixed forever.</summary>
public enum ProductionOutputKind : short
{
    /// <summary>Page images (and text), one Bates number per page at page level.</summary>
    Image = 1,

    /// <summary>The native file with a slip sheet; one Bates number.</summary>
    Native = 2,

    /// <summary>A placeholder page instead of the document (e.g. a file type never produced); one Bates number.</summary>
    Placeholder = 3,
}

/// <summary>
/// The Bates label format of a production: <c>prefix + zero-padded number + suffix</c>, e.g. <c>ABC0000001</c>.
/// Uniqueness is per workspace (matter) and <see cref="PrefixKey"/> (the prefix compared without case), across all
/// productions (E12-T03).
/// </summary>
public sealed partial record BatesFormat
{
    public const int MinPadding = 1;
    public const int MaxPadding = 12;
    public const int MaxPrefixLength = 30;
    public const int MaxSuffixLength = 30;
    public const int PageSuffixPadding = 4;
    public const string PageSuffixSeparator = ".";

    public BatesFormat(string prefix, int padding, string suffix, BatesNumberingLevel level)
    {
        if (Validate(prefix, padding, suffix) is { } error)
        {
            throw new ArgumentException(error);
        }

        Prefix = prefix;
        Padding = padding;
        Suffix = suffix;
        Level = level;
    }

    public string Prefix { get; }

    public int Padding { get; }

    public string Suffix { get; }

    public BatesNumberingLevel Level { get; }

    /// <summary>The largest number the padding can hold (a production never overflows its padding).</summary>
    public long MaxNumber => MaxFor(Padding);

    /// <summary>The key Bates uniqueness is enforced on (per workspace).</summary>
    public string PrefixKey => KeyOf(Prefix);

    public static string KeyOf(string prefix) => (prefix ?? throw new ArgumentNullException(nameof(prefix))).ToUpperInvariant();

    public static long MaxFor(int padding) => padding is < MinPadding or > MaxPadding
        ? throw new ArgumentOutOfRangeException(nameof(padding))
        : (long)Math.Pow(10, padding) - 1;

    /// <summary>Null when valid; otherwise the reason. Prefix: 1–30 of letters, digits, <c>_ - .</c>; suffix: 0–30 of the same.</summary>
    public static string? Validate(string? prefix, int padding, string? suffix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length > MaxPrefixLength || !LabelPart().IsMatch(prefix))
        {
            return $"A Bates prefix has 1 to {MaxPrefixLength} letters, digits, '_', '-' or '.', starting with a letter or digit.";
        }

        if (suffix is null || suffix.Length > MaxSuffixLength || (suffix.Length > 0 && !SuffixPart().IsMatch(suffix)))
        {
            return $"A Bates suffix has at most {MaxSuffixLength} letters, digits, '_', '-' or '.'.";
        }

        return padding is < MinPadding or > MaxPadding ? $"Bates padding is {MinPadding} to {MaxPadding} digits." : null;
    }

    public string Format(long number) =>
        number < 1 || number > MaxNumber
            ? throw new ArgumentOutOfRangeException(nameof(number), number, $"A Bates number is 1 to {MaxNumber}.")
            : string.Concat(Prefix, number.ToString("D" + Padding.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), Suffix);

    /// <summary>The label of page <paramref name="page"/> (1-based) of a document numbered at document level.</summary>
    public string PageLabel(long number, int page)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        var label = Format(number);
        return string.Concat(label.AsSpan(0, label.Length - Suffix.Length), PageSuffixSeparator,
            page.ToString("D" + PageSuffixPadding.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), Suffix);
    }

    /// <summary>
    /// Parses a label of this format (prefix and suffix compared without case; the digits may be shorter or longer than
    /// the padding, so <c>ABC123</c> finds <c>ABC0000123</c>).
    /// </summary>
    public bool TryParse(string? label, out long number)
    {
        number = 0;
        if (string.IsNullOrEmpty(label) || label.Length <= Prefix.Length + Suffix.Length
            || !label.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !label.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = label.AsSpan(Prefix.Length, label.Length - Prefix.Length - Suffix.Length);
        return digits.Length <= 18 && digits.IndexOfAnyExceptInRange('0', '9') < 0
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 1;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPart();

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SuffixPart();
}

/// <summary>One production member in production order, as the planner reads it.</summary>
/// <param name="Sequence">1-based position in production order (dense).</param>
/// <param name="FamilyKey">The family (the document itself when standalone); a family's members are adjacent.</param>
/// <param name="PageCount">Pages of the document's active page set (0 when none are stored yet).</param>
public readonly record struct BatesMember(long Sequence, Guid DocumentId, Guid FamilyKey, ProductionOutputKind Output, int PageCount);

/// <summary>What the planner decided for one member: its numbers consumed, its offset from the start number and its chunk.</summary>
public readonly record struct BatesPlannedMember(long Sequence, int Units, long FirstOffset, int ChunkSequence);

/// <summary>
/// Plans a production's Bates numbering in one streaming pass over its members in production order (E12-T03): how
/// many numbers each member consumes (<see cref="UnitsFor"/>), its offset from the start number (the running sum, so
/// numbers are gap-free and monotonic in production order) and its allocation chunk. Chunks close only between
/// families, after <see cref="DocumentsPerChunk"/> documents or <see cref="UnitsPerChunk"/> numbers (ADR-010 §6: 100
/// documents, ≤ 2,000 pages), so every family is numbered by one chunk and its attachment range is local to it. The
/// plan depends only on the members, never on timing or chunk execution order: re-planning yields the same plan.
/// </summary>
public sealed class BatesPlanner
{
    private long _nextOffset;
    private long _expectedSequence = 1;
    private Guid? _family;
    private int _chunkDocuments;
    private long _chunkUnits;

    public BatesPlanner(BatesNumberingLevel level, int documentsPerChunk = 100, int unitsPerChunk = 2_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(documentsPerChunk, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(unitsPerChunk, 1);
        Level = level;
        DocumentsPerChunk = documentsPerChunk;
        UnitsPerChunk = unitsPerChunk;
    }

    public BatesNumberingLevel Level { get; }

    public int DocumentsPerChunk { get; }

    public int UnitsPerChunk { get; }

    /// <summary>Numbers consumed so far (the production's span once every member was added).</summary>
    public long TotalUnits => _nextOffset;

    public long Documents => _expectedSequence - 1;

    public int Chunks { get; private set; }

    /// <summary>
    /// Numbers a member consumes: one at document level; at page level its page count for images (at least one, so a
    /// document without stored pages still holds a number for QC to flag) and exactly one for a native slip sheet or a
    /// placeholder.
    /// </summary>
    public static int UnitsFor(BatesNumberingLevel level, ProductionOutputKind output, int pageCount) =>
        level == BatesNumberingLevel.Document || output != ProductionOutputKind.Image ? 1 : Math.Max(1, pageCount);

    public BatesPlannedMember Add(BatesMember member)
    {
        if (member.Sequence != _expectedSequence)
        {
            throw new InvalidOperationException($"Members are planned in dense production order: expected {_expectedSequence}, got {member.Sequence}.");
        }

        var units = UnitsFor(Level, member.Output, member.PageCount);
        var newFamily = _family != member.FamilyKey;
        if (Chunks == 0 || (newFamily && (_chunkDocuments >= DocumentsPerChunk || _chunkUnits >= UnitsPerChunk)))
        {
            Chunks++;
            _chunkDocuments = 0;
            _chunkUnits = 0;
        }

        var planned = new BatesPlannedMember(member.Sequence, units, _nextOffset, Chunks);
        _family = member.FamilyKey;
        _chunkDocuments++;
        _chunkUnits += units;
        _nextOffset += units;
        _expectedSequence++;
        return planned;
    }
}

/// <summary>One planned member of an allocation chunk.</summary>
public readonly record struct BatesSliceMember(long Sequence, Guid DocumentId, Guid FamilyKey, int Units, long FirstOffset);

/// <summary>The Bates numbers of one produced document (E12-T03: written back per document as ProdBeg/End/Attach).</summary>
public sealed record BatesAssignment(
    long Sequence,
    Guid DocumentId,
    long BegNumber,
    long EndNumber,
    string ProdBegBates,
    string ProdEndBates,
    string ProdBegAttach,
    string ProdEndAttach);

/// <summary>
/// Assigns Bates numbers to one allocation chunk (E12-T03): a pure function of the format, the start number and the
/// chunk's planned members, so a chunk re-executed after a crash or a redelivery assigns exactly the same numbers.
/// <c>ProdBegAttach</c>/<c>ProdEndAttach</c> span the document's family within the production (a standalone document's
/// own range); families never straddle chunks (<see cref="BatesPlanner"/>).
/// </summary>
public static class BatesAllocator
{
    public static IReadOnlyList<BatesAssignment> Assign(BatesFormat format, long startNumber, IReadOnlyList<BatesSliceMember> slice)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentOutOfRangeException.ThrowIfLessThan(startNumber, 1);
        var numbers = new (long Beg, long End)[slice.Count];
        for (var i = 0; i < slice.Count; i++)
        {
            var member = slice[i];
            if (member.Units < 1 || member.FirstOffset < 0)
            {
                throw new ArgumentException("Every member consumes at least one number from a non-negative offset.", nameof(slice));
            }

            if (i > 0 && (member.Sequence != slice[i - 1].Sequence + 1 || member.FirstOffset != slice[i - 1].FirstOffset + slice[i - 1].Units))
            {
                throw new ArgumentException("A chunk's members are consecutive in production order with contiguous offsets.", nameof(slice));
            }

            var beg = checked(startNumber + member.FirstOffset);
            var end = checked(beg + member.Units - 1);
            if (end > format.MaxNumber)
            {
                throw new BatesOverflowException(format, end);
            }

            numbers[i] = (beg, end);
        }

        var result = new BatesAssignment[slice.Count];
        var runStart = 0;
        for (var i = 0; i <= slice.Count; i++)
        {
            if (i < slice.Count && (i == runStart || slice[i].FamilyKey == slice[runStart].FamilyKey))
            {
                continue;
            }

            if (i > runStart)
            {
                var begAttach = format.Format(numbers[runStart].Beg);
                var endAttach = format.Format(numbers[i - 1].End);
                for (var j = runStart; j < i; j++)
                {
                    result[j] = new BatesAssignment(slice[j].Sequence, slice[j].DocumentId, numbers[j].Beg, numbers[j].End,
                        format.Format(numbers[j].Beg), format.Format(numbers[j].End), begAttach, endAttach);
                }
            }

            runStart = i;
        }

        return result;
    }
}

/// <summary>The production would need numbers beyond what the padding can hold.</summary>
public sealed class BatesOverflowException : Exception
{
    public BatesOverflowException()
    {
    }

    public BatesOverflowException(string message)
        : base(message)
    {
    }

    public BatesOverflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BatesOverflowException(BatesFormat format, long number)
        : base(string.Create(CultureInfo.InvariantCulture,
            $"Bates number {number} does not fit {format?.Padding} digits; choose a lower start number or a larger padding."))
    {
    }
}

/// <summary>
/// The verification hash of a production's Bates assignment (Q-08 manifest): SHA-256 over every member in production
/// order of <c>sequence ‖ document id ‖ document version ‖ output ‖ units ‖ beg ‖ end ‖ UTF-8 labels</c>, each label
/// length-prefixed. Recomputable from the stored rows alone.
/// </summary>
public sealed class BatesAssignmentHasher : IDisposable
{
    public const string Prefix = "opportunity.bates.v1";

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _buffer = new byte[8 + 16 + 8 + 2 + 4 + 8 + 8];

    public BatesAssignmentHasher()
    {
        _hash.AppendData(Encoding.UTF8.GetBytes(Prefix));
    }

    public long Count { get; private set; }

    public void Append(long sequence, Guid documentId, long documentVersion, ProductionOutputKind output, int units, BatesAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var span = _buffer.AsSpan();
        BinaryPrimitives.WriteInt64BigEndian(span, sequence);
        if (!documentId.TryWriteBytes(span[8..], bigEndian: true, out _))
        {
            throw new InvalidOperationException("A document ID did not fit its slot.");
        }

        BinaryPrimitives.WriteInt64BigEndian(span[24..], documentVersion);
        BinaryPrimitives.WriteInt16BigEndian(span[32..], (short)output);
        BinaryPrimitives.WriteInt32BigEndian(span[34..], units);
        BinaryPrimitives.WriteInt64BigEndian(span[38..], assignment.BegNumber);
        BinaryPrimitives.WriteInt64BigEndian(span[46..], assignment.EndNumber);
        _hash.AppendData(_buffer);
        Span<byte> length = stackalloc byte[4];
        foreach (var label in new[] { assignment.ProdBegBates, assignment.ProdEndBates, assignment.ProdBegAttach, assignment.ProdEndAttach })
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            _hash.AppendData(length);
            _hash.AppendData(bytes);
        }

        Count++;
    }

    public byte[] Finish()
    {
        Span<byte> count = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(count, Count);
        _hash.AppendData(count);
        return _hash.GetHashAndReset();
    }

    public void Dispose() => _hash.Dispose();
}
