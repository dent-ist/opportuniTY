namespace Opportunity.Application.Search.Reindex;

/// <summary>
/// DocumentId key arithmetic for reindex ranges. PostgreSQL orders <c>uuid</c> by its 16 bytes in RFC 4122 (big-endian)
/// order, which is also the order of <see cref="Guid.CompareTo(Guid)"/> and of the canonical "D" string, so ranges planned
/// in PostgreSQL, checked in .NET and compared on the indexed <c>documentId</c> keyword agree.
/// </summary>
public static class DocumentKeyRanges
{
    /// <summary>The next id in that order; <see cref="Guid.AllBitsSet"/> has none.</summary>
    public static Guid Successor(Guid id)
    {
        if (id == Guid.AllBitsSet)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "The largest id has no successor.");
        }

        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        for (var i = 15; i >= 0; i--)
        {
            if (++bytes[i] != 0)
            {
                break;
            }
        }

        return new Guid(bytes, bigEndian: true);
    }
}
