namespace Opportunity.DataGenerator.Corpus.Model;

public enum DocumentKind
{
    Email,
    EDocument,
}

/// <summary>
/// How a document relates to earlier documents with the same MD5 in load order. Every non-<see cref="None"/>
/// value is an exact-hash duplicate; the value says where the earlier copy lives.
/// </summary>
public enum DuplicateType
{
    /// <summary>First occurrence of its content (the primary), or unique content.</summary>
    None,

    /// <summary>Earlier copy held by the same custodian (e.g. the same message filed in two folders).</summary>
    ExactMd5,

    /// <summary>Earlier copies are held only by other custodians.</summary>
    CrossCustodian,

    /// <summary>The same attachment appears earlier in the same family.</summary>
    WithinFamily,
}

public sealed record NeedleHit(string Term, int Occurrences);

public sealed record ProximityHit(string First, string Second, int Distance, bool IsNear);

/// <summary>
/// Content shared by every copy of a document: identical bytes, hashes, text and metadata. Copies differ only in
/// their <see cref="GeneratedDocument"/> placement (control number, custodian, file path).
/// </summary>
public sealed class DocumentContent
{
    public required UInt128 ContentKey { get; init; }

    public required DocumentKind Kind { get; init; }

    public required string FileType { get; init; }

    public required string Md5 { get; init; }

    public required string Sha256 { get; init; }

    public required TextSpec Text { get; init; }

    /// <summary>Metadata by <see cref="FieldCatalog"/> ordinal; FilePath is per copy and left null here.</summary>
    public required object?[] Fields { get; init; }

    public string? NearDuplicateClusterId { get; init; }

    public IReadOnlyList<NeedleHit> Needles { get; init; } = [];

    public IReadOnlyList<ProximityHit> ProximityHits { get; init; } = [];
}

/// <summary>One document in load order. Instances are complete (numbered) when yielded by the generator.</summary>
public sealed class GeneratedDocument
{
    public required DocumentContent Content { get; init; }

    public long DocIndex { get; internal set; }

    public string ControlNumber { get; internal set; } = "";

    /// <summary>Control number of the family parent.</summary>
    public string FamilyId { get; internal set; } = "";

    public string? ParentControlNumber { get; internal set; }

    /// <summary>0 for the parent, then 1..n in family (pre-order) order.</summary>
    public required int FamilySequence { get; init; }

    public required int AttachmentDepth { get; init; }

    /// <summary>Family-local index of the parent node, -1 for the family parent.</summary>
    public required int ParentSequence { get; init; }

    public string BegAttach { get; internal set; } = "";

    public string EndAttach { get; internal set; } = "";

    public required string Custodian { get; init; }

    public IReadOnlyList<string> AllCustodians { get; internal set; } = [];

    public IReadOnlyList<string> DuplicateCustodians { get; internal set; } = [];

    public string? DuplicateGroupId { get; internal set; }

    public string? FamilyDuplicateGroupId { get; internal set; }

    public DuplicateType DuplicateType { get; internal set; }

    public bool IsDuplicatePrimary { get; internal set; } = true;

    public string? EmailThreadId { get; init; }

    /// <summary>Metadata by <see cref="FieldCatalog"/> ordinal (content fields plus this copy's FilePath).</summary>
    public required object?[] Fields { get; init; }

    public DocumentKind Kind => Content.Kind;

    public string Md5 => Content.Md5;

    public long TextBytes => Content.Text.TotalBytes;

    public bool IsFamilyParent => FamilySequence == 0;
}

/// <summary>A family (parent plus descendants, pre-order) as emitted for one custodian copy.</summary>
public sealed class GeneratedFamily
{
    public required IReadOnlyList<GeneratedDocument> Documents { get; init; }

    public required long UnitIndex { get; init; }

    /// <summary>Index of the family within its unit (thread message number for email threads).</summary>
    public required int FamilyIndex { get; init; }

    /// <summary>0 for the original, 1..k for whole-family duplicate copies.</summary>
    public required int CopyIndex { get; init; }

    public required string Custodian { get; init; }

    public string? EmailThreadId { get; init; }

    public bool IsFiller { get; init; }

    public GeneratedDocument Parent => Documents[0];
}

public sealed record ThreadSummary(string ThreadId, int MessageCount);

/// <summary>A contiguous block of families in load order, produced by one parallel work item.</summary>
public sealed class GeneratedChunk
{
    public required long Index { get; init; }

    public required long FirstDocIndex { get; init; }

    public required IReadOnlyList<GeneratedFamily> Families { get; init; }

    public required IReadOnlyList<ThreadSummary> Threads { get; init; }

    public required int UnitCount { get; init; }
}
