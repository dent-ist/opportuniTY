namespace Opportunity.Core.Jobs;

/// <summary>
/// The one membership reference a chunk carries (ADR-010 §4). Identifiers only, never payload. Create through the
/// factory methods, which enforce the shape of each kind (the table has matching CHECK constraints).
/// </summary>
public sealed record ChunkMembership
{
    public const int MaxExplicitIds = 1_000;

    private ChunkMembership(ChunkMembershipKind kind)
    {
        Kind = kind;
    }

    public ChunkMembershipKind Kind { get; }

    /// <summary><see cref="ChunkMembershipKind.SnapshotRange"/>: the materialized snapshot.</summary>
    public Guid? SnapshotId { get; private init; }

    /// <summary><see cref="ChunkMembershipKind.ImportRows"/>: the import batch.</summary>
    public Guid? ImportBatchId { get; private init; }

    /// <summary>First snapshot ordinal or import row (inclusive).</summary>
    public long? RangeFrom { get; private init; }

    /// <summary>Last snapshot ordinal or import row (inclusive).</summary>
    public long? RangeTo { get; private init; }

    /// <summary><see cref="ChunkMembershipKind.DocumentKeyRange"/>: target projection generation.</summary>
    public long? ProjectionGeneration { get; private init; }

    /// <summary><see cref="ChunkMembershipKind.DocumentKeyRange"/>: first DocumentId (inclusive).</summary>
    public Guid? DocumentIdFrom { get; private init; }

    /// <summary><see cref="ChunkMembershipKind.DocumentKeyRange"/>: last DocumentId (inclusive).</summary>
    public Guid? DocumentIdTo { get; private init; }

    /// <summary><see cref="ChunkMembershipKind.ExplicitIds"/>: the documents.</summary>
    public IReadOnlyList<Guid>? DocumentIds { get; private init; }

    /// <summary>Number of members when the reference alone determines it (ranges and lists); null for key ranges.</summary>
    public long? KnownCount => Kind switch
    {
        ChunkMembershipKind.SnapshotRange or ChunkMembershipKind.ImportRows => RangeTo - RangeFrom + 1,
        ChunkMembershipKind.ExplicitIds => DocumentIds!.Count,
        _ => null,
    };

    public static ChunkMembership SnapshotRange(Guid snapshotId, long ordinalFrom, long ordinalTo)
    {
        RequireId(snapshotId, nameof(snapshotId));
        RequireRange(ordinalFrom, ordinalTo);
        return new ChunkMembership(ChunkMembershipKind.SnapshotRange)
        {
            SnapshotId = snapshotId,
            RangeFrom = ordinalFrom,
            RangeTo = ordinalTo,
        };
    }

    public static ChunkMembership ImportRows(Guid importBatchId, long rowFrom, long rowTo)
    {
        RequireId(importBatchId, nameof(importBatchId));
        RequireRange(rowFrom, rowTo);
        return new ChunkMembership(ChunkMembershipKind.ImportRows)
        {
            ImportBatchId = importBatchId,
            RangeFrom = rowFrom,
            RangeTo = rowTo,
        };
    }

    public static ChunkMembership DocumentKeyRange(long projectionGeneration, Guid documentIdFrom, Guid documentIdTo)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(projectionGeneration);
        if (documentIdFrom.CompareTo(documentIdTo) > 0)
        {
            throw new ArgumentException("The key range is empty: from > to.", nameof(documentIdTo));
        }

        return new ChunkMembership(ChunkMembershipKind.DocumentKeyRange)
        {
            ProjectionGeneration = projectionGeneration,
            DocumentIdFrom = documentIdFrom,
            DocumentIdTo = documentIdTo,
        };
    }

    public static ChunkMembership ExplicitIds(IReadOnlyCollection<Guid> documentIds)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        if (documentIds.Count is 0 or > MaxExplicitIds)
        {
            throw new ArgumentException($"An explicit chunk lists 1 to {MaxExplicitIds} documents.", nameof(documentIds));
        }

        if (documentIds.Distinct().Count() != documentIds.Count || documentIds.Contains(Guid.Empty))
        {
            throw new ArgumentException("Document ids must be distinct and non-empty.", nameof(documentIds));
        }

        return new ChunkMembership(ChunkMembershipKind.ExplicitIds) { DocumentIds = [.. documentIds] };
    }

    /// <summary>Rebuilds a stored reference (persistence only); validates like the factory methods.</summary>
    public static ChunkMembership Restore(
        ChunkMembershipKind kind, Guid? snapshotId, Guid? importBatchId, long? rangeFrom, long? rangeTo,
        long? projectionGeneration, Guid? documentIdFrom, Guid? documentIdTo, IReadOnlyCollection<Guid>? documentIds) => kind switch
        {
            ChunkMembershipKind.SnapshotRange => SnapshotRange(snapshotId!.Value, rangeFrom!.Value, rangeTo!.Value),
            ChunkMembershipKind.ImportRows => ImportRows(importBatchId!.Value, rangeFrom!.Value, rangeTo!.Value),
            ChunkMembershipKind.DocumentKeyRange => DocumentKeyRange(projectionGeneration!.Value, documentIdFrom!.Value, documentIdTo!.Value),
            ChunkMembershipKind.ExplicitIds => ExplicitIds(documentIds!),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown membership kind."),
        };

    public bool Equals(ChunkMembership? other) =>
        other is not null
        && Kind == other.Kind && SnapshotId == other.SnapshotId && ImportBatchId == other.ImportBatchId
        && RangeFrom == other.RangeFrom && RangeTo == other.RangeTo && ProjectionGeneration == other.ProjectionGeneration
        && DocumentIdFrom == other.DocumentIdFrom && DocumentIdTo == other.DocumentIdTo
        && (DocumentIds ?? []).SequenceEqual(other.DocumentIds ?? []);

    public override int GetHashCode() =>
        HashCode.Combine(Kind, SnapshotId, ImportBatchId, RangeFrom, RangeTo, ProjectionGeneration, DocumentIdFrom, DocumentIds?.Count);

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An id is required.", name);
        }
    }

    private static void RequireRange(long from, long to)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(from);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
    }
}
