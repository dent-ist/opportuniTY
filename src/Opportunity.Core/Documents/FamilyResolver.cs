using System.Globalization;

namespace Opportunity.Core.Documents;

/// <summary>What the family report lists for a document (ADR-009 R10). Stored as smallint; values are fixed forever.</summary>
public enum FamilyIssueKind : short
{
    /// <summary>The parent named by ParentID or BegAttach is not in the workspace (an orphan attachment).</summary>
    ParentMissing = 1,

    /// <summary>An attachment range spans control numbers that are not in the workspace.</summary>
    RangeGap = 2,

    /// <summary>The document is claimed by two families (overlapping ranges); it joined the lowest parent.</summary>
    ClaimedByTwoFamilies = 3,

    /// <summary>BegAttach/EndAttach with different prefixes, or a reversed range; the range is ignored.</summary>
    InvalidRange = 4,

    /// <summary>The parent pointers form a cycle; the lowest control number became the parent.</summary>
    Cycle = 5,

    /// <summary>AttachmentIDs disagrees with the resolved family (cross-validation only, ADR-009 Mode B).</summary>
    AttachmentListMismatch = 6,
}

/// <summary>The family inputs of one document, as imported (ADR-009 §2). Control-number values are normalized.</summary>
public sealed record FamilySource
{
    public required Guid DocumentId { get; init; }

    /// <summary>Display spelling, used in report messages.</summary>
    public required string ControlNumber { get; init; }

    public required string ControlNumberNorm { get; init; }

    public string? BegAttachNorm { get; init; }

    public string? EndAttachNorm { get; init; }

    /// <summary>Control number of the immediate parent (Mode B).</summary>
    public string? ParentIdNorm { get; init; }

    /// <summary>Group / family identifier shared by the members (Mode C), compared ordinally.</summary>
    public string? GroupIdentifier { get; init; }

    /// <summary>Control numbers a parent lists as its attachments; cross-validated only.</summary>
    public IReadOnlyList<string> AttachmentIdsNorm { get; init; } = [];

    /// <summary>Received EndBates: a member covers the numbers up to it when checking a range for gaps.</summary>
    public string? EndBates { get; init; }

    public DateTimeOffset? DocumentDate { get; init; }

    /// <summary>A mapped upstream FamilyDate (ADR-009 R25), used instead of the parent's DocumentDate.</summary>
    public DateTimeOffset? UpstreamFamilyDate { get; init; }
}

/// <summary>The resolved family columns of one document (ADR-009 R7, R8, R10, R25).</summary>
public sealed record FamilyAssignment(Guid FamilyId, Guid? ParentDocumentId, int FamilySequence, FamilyStatus Status, DateTimeOffset? FamilyDate);

/// <summary>One report line. <paramref name="Related"/> lists the other control numbers involved (claims, cycle, list).</summary>
public sealed record FamilyIssue(Guid DocumentId, FamilyIssueKind Kind, string Message, IReadOnlyList<string> Related, long? MissingCount);

public sealed record FamilyResolution(IReadOnlyDictionary<Guid, FamilyAssignment> Assignments, IReadOnlyList<FamilyIssue> Issues);

/// <summary>
/// Family reconstruction (ADR-009 §2, E09-T01) as a pure function of a closed set of documents: the same documents give
/// the same families whatever the load order or chunking. Each document takes its family source by precedence
/// pointer &gt; group &gt; range (R9):
/// <list type="bullet">
/// <item>Mode B: <c>ParentID</c> names the immediate parent; unknown parents leave the document standalone (ParentMissing).</item>
/// <item>Mode C: documents sharing a <c>GroupIdentifier</c> form a family whose parent is the only member without a
/// ParentID, else the lowest control number.</item>
/// <item>Mode A: documents within [BegAttach, EndAttach] with BegAttach's non-numeric prefix belong to the document
/// <c>ControlNumber = BegAttach</c>. Nested ranges nest; partly overlapping ranges are a conflict resolved to the
/// lowest parent. A range without its parent forms a provisional family under its lowest member (ParentMissing).</item>
/// </list>
/// Pointer cycles are broken at the lowest control number (Conflict). Nested attachments flatten into the top-level
/// family: <c>FamilyId</c> is the top-level parent's DocumentId, <c>FamilySequence</c> 0 for it and 1..n over the others
/// in natural control-number order, and <c>ParentDocumentId</c> is the immediate parent from ParentID, otherwise the
/// top-level parent. <c>FamilyDate</c> is the top-level parent's upstream FamilyDate, else its DocumentDate.
/// </summary>
public static class FamilyResolver
{
    private enum Mode
    {
        None,
        Range,
        Group,
        Pointer,
    }

    private enum ClaimKind
    {
        None,
        Pointer,
        Other,
    }

    public static FamilyResolution Resolve(IEnumerable<FamilySource> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var nodes = documents
            .Select(d => new Node(d, ControlNumber.SortKey(d.ControlNumberNorm)))
            .OrderBy(n => n.Key, StringComparer.Ordinal)
            .ThenBy(n => n.Source.DocumentId)
            .ToList();
        for (var i = 0; i < nodes.Count; i++)
        {
            nodes[i].Index = i;
        }

        var byNorm = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            byNorm.TryAdd(node.Source.ControlNumberNorm, node);
        }

        var issues = new List<FamilyIssue>();
        ClaimPointers(nodes, byNorm, issues);
        ClaimGroups(nodes);
        ClaimRanges(nodes, byNorm, issues);
        BreakCycles(nodes, issues);

        var tops = nodes.Select(Top).ToList();
        var assignments = new Dictionary<Guid, FamilyAssignment>(nodes.Count);
        foreach (var family in nodes.GroupBy(n => tops[n.Index]))
        {
            var top = family.Key;
            var date = top.Source.UpstreamFamilyDate ?? top.Source.DocumentDate
                ?? family.OrderBy(n => n.Index).Select(n => n.Source.UpstreamFamilyDate).FirstOrDefault(d => d is not null);
            var sequence = 0;
            foreach (var member in family.OrderBy(n => n == top ? -1 : n.Index))
            {
                Guid? parent = member == top ? null
                    : member.ClaimKind == ClaimKind.Pointer ? member.Claim!.Source.DocumentId
                    : top.Source.DocumentId;
                assignments[member.Source.DocumentId] = new FamilyAssignment(
                    top.Source.DocumentId, parent, sequence++, member.Status, date);
            }
        }

        CrossValidateAttachmentLists(nodes, byNorm, tops, issues);
        var order = nodes.ToDictionary(n => n.Source.DocumentId, n => n.Index);
        return new FamilyResolution(
            assignments,
            [.. issues.DistinctBy(i => (i.DocumentId, i.Kind, i.Message)).OrderBy(i => order[i.DocumentId]).ThenBy(i => i.Kind)]);
    }

    /// <summary>Everything before the trailing digit run (<c>ABC-0012</c> → <c>ABC-</c>); the whole value when it has none.</summary>
    public static string Prefix(string norm)
    {
        ArgumentNullException.ThrowIfNull(norm);
        var end = norm.Length;
        while (end > 0 && char.IsAsciiDigit(norm[end - 1]))
        {
            end--;
        }

        return norm[..end];
    }

    /// <summary>The trailing number (<c>ABC-0012</c> → 12); false when there is none or it has more than 18 digits.</summary>
    public static bool TryNumericTail(string norm, out long value)
    {
        ArgumentNullException.ThrowIfNull(norm);
        var digits = norm.Length - Prefix(norm).Length;
        value = 0;
        return digits is > 0 and <= 18
            && long.TryParse(norm.AsSpan(norm.Length - digits), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static void ClaimPointers(List<Node> nodes, Dictionary<string, Node> byNorm, List<FamilyIssue> issues)
    {
        foreach (var node in nodes.Where(n => n.Mode == Mode.Pointer))
        {
            var parentNorm = node.Source.ParentIdNorm!;
            if (byNorm.TryGetValue(parentNorm, out var parent) && parent != node)
            {
                node.SetClaim(parent, ClaimKind.Pointer);
            }
            else
            {
                node.Flag(FamilyStatus.ParentMissing);
                issues.Add(new FamilyIssue(node.Source.DocumentId, FamilyIssueKind.ParentMissing,
                    $"Parent {parentNorm} (ParentID) is not in the workspace; the document stands alone until it arrives.", [parentNorm], null));
            }
        }
    }

    private static void ClaimGroups(List<Node> nodes)
    {
        foreach (var group in nodes.Where(n => n.Source.GroupIdentifier is not null).GroupBy(n => n.Source.GroupIdentifier!, StringComparer.Ordinal))
        {
            var members = group.OrderBy(n => n.Index).ToList();
            var withoutParent = members.Where(n => !n.HasPointer).ToList();
            var root = withoutParent.Count == 1 ? withoutParent[0] : members[0];
            foreach (var member in members.Where(m => m.Mode == Mode.Group && m != root))
            {
                member.SetClaim(root, ClaimKind.Other);
            }
        }
    }

    private static void ClaimRanges(List<Node> nodes, Dictionary<string, Node> byNorm, List<FamilyIssue> issues)
    {
        var ranges = new Dictionary<(string Beg, string End), Range>();
        foreach (var node in nodes.Where(n => n.Mode == Mode.Range))
        {
            var beg = node.Source.BegAttachNorm ?? node.Source.EndAttachNorm!;
            var end = node.Source.EndAttachNorm ?? beg;
            var begKey = ControlNumber.SortKey(beg);
            var endKey = ControlNumber.SortKey(end);
            if (Prefix(beg) != Prefix(end) || string.CompareOrdinal(begKey, endKey) > 0)
            {
                node.Flag(FamilyStatus.InvalidRange);
                issues.Add(new FamilyIssue(node.Source.DocumentId, FamilyIssueKind.InvalidRange,
                    Prefix(beg) != Prefix(end)
                        ? $"BegAttach {beg} and EndAttach {end} have different prefixes; the range is ignored."
                        : $"BegAttach {beg} is after EndAttach {end}; the range is ignored.",
                    [beg, end], null));
                continue;
            }

            if (!ranges.TryGetValue((beg, end), out var range))
            {
                range = new Range(beg, end, begKey, endKey, byNorm.GetValueOrDefault(beg));
                ranges[(beg, end)] = range;
            }

            range.Declarers.Add(node);
        }

        var keys = nodes.Select(n => n.Key).ToList();
        var ordered = ranges.Values.OrderBy(r => r.BegKey, StringComparer.Ordinal).ThenBy(r => r.EndKey, StringComparer.Ordinal).ToList();
        foreach (var range in ordered)
        {
            var prefix = Prefix(range.Beg);
            var lower = LowerBound(keys, range.BegKey);
            for (var i = lower; i < nodes.Count && string.CompareOrdinal(keys[i], range.EndKey) <= 0; i++)
            {
                if (Prefix(nodes[i].Source.ControlNumberNorm) == prefix)
                {
                    range.Members.Add(nodes[i]);
                }
            }

            if (range.Parent is { } parent)
            {
                parent.OwnRanges.Add(range);
            }

            var claimants = range.Members.Concat(range.Declarers).Where(n => n.Mode == Mode.Range && n != range.Parent).Distinct().OrderBy(n => n.Index).ToList();
            range.Effective = range.Parent ?? claimants.FirstOrDefault();
            foreach (var claimant in claimants.Where(c => c != range.Effective))
            {
                claimant.Candidates.Add(range);
            }

            if (range.Parent is null && range.Effective is { } provisional)
            {
                provisional.ProvisionalRoot = range;
            }
        }

        foreach (var node in nodes.Where(n => n.Mode == Mode.Range))
        {
            if (node.ProvisionalRoot is { } own && node.Candidates.Count == 0)
            {
                OrphanOfRange(node, own, issues);
            }

            if (node.Candidates.Count == 0)
            {
                continue;
            }

            // Outer ranges first: candidates that contain one another nest; anything else is an overlap.
            var candidates = node.Candidates.OrderBy(r => r.BegKey, StringComparer.Ordinal).ThenByDescending(r => r.EndKey, StringComparer.Ordinal).ToList();
            var nested = candidates.Zip(candidates.Skip(1)).All(p => p.First.Contains(p.Second))
                && node.OwnRanges.All(own => candidates.All(c => c.Contains(own)))
                && node.ProvisionalRoot is null;
            Range chosen;
            if (nested)
            {
                chosen = candidates[^1];
            }
            else
            {
                chosen = candidates.OrderBy(r => r.Effective!.Index).ThenBy(r => r.BegKey, StringComparer.Ordinal).First();
                node.Flag(FamilyStatus.Conflict);
                var claims = candidates.Select(c => c.Effective!.Source.ControlNumber)
                    .Concat(node.OwnRanges.Count > 0 || node.ProvisionalRoot is not null ? [node.Source.ControlNumber] : [])
                    .Distinct(StringComparer.Ordinal).ToList();
                issues.Add(new FamilyIssue(node.Source.DocumentId, FamilyIssueKind.ClaimedByTwoFamilies,
                    $"Claimed by the families of {string.Join(", ", claims)} (overlapping attachment ranges); joined {chosen.Effective!.Source.ControlNumber}.",
                    claims, null));
            }

            node.SetClaim(chosen.Effective!, ClaimKind.Other);
            node.ChosenRange = chosen;
            if (chosen.Parent is null)
            {
                OrphanOfRange(node, chosen, issues);
            }
        }

        foreach (var range in ordered.Where(r => r.Parent is not null))
        {
            var missing = MissingCount(range);
            if (missing is not > 0)
            {
                continue;
            }

            range.Parent!.Flag(FamilyStatus.Gap);
            foreach (var member in nodes.Where(n => n.ChosenRange == range))
            {
                member.Flag(FamilyStatus.Gap);
            }

            issues.Add(new FamilyIssue(range.Parent.Source.DocumentId, FamilyIssueKind.RangeGap,
                $"Attachment range {range.Beg}–{range.End} spans {missing} control number(s) that are not in the workspace.",
                [range.Beg, range.End], missing));
        }
    }

    private static void OrphanOfRange(Node node, Range range, List<FamilyIssue> issues)
    {
        node.Flag(FamilyStatus.ParentMissing);
        issues.Add(new FamilyIssue(node.Source.DocumentId, FamilyIssueKind.ParentMissing,
            $"Parent {range.Beg} (BegAttach) is not in the workspace; the range forms a provisional family until it arrives.", [range.Beg], null));
    }

    /// <summary>Numbers of [Beg, End] that no member covers (a member covers its own number up to its EndBates).</summary>
    private static long? MissingCount(Range range)
    {
        if (!TryNumericTail(range.Beg, out var first) || !TryNumericTail(range.End, out var last) || last - first > 10_000_000)
        {
            return null;
        }

        var prefix = Prefix(range.Beg);
        var spans = new List<(long From, long To)>();
        foreach (var member in range.Members)
        {
            if (!TryNumericTail(member.Source.ControlNumberNorm, out var from))
            {
                continue;
            }

            var to = from;
            if (member.Source.EndBates is { } endBates
                && ControlNumber.TryNormalize(endBates, caseSensitive: false, null, out var endNorm, out _)
                && string.Equals(Prefix(endNorm), prefix, StringComparison.OrdinalIgnoreCase)
                && TryNumericTail(endNorm, out var endNumber) && endNumber > from)
            {
                to = endNumber;
            }

            spans.Add((Math.Max(from, first), Math.Min(to, last)));
        }

        long covered = 0;
        long next = first;
        foreach (var (from, to) in spans.Where(s => s.From <= s.To).OrderBy(s => s.From))
        {
            var start = Math.Max(from, next);
            if (to >= start)
            {
                covered += to - start + 1;
                next = to + 1;
            }
        }

        return last - first + 1 - covered;
    }

    private static void BreakCycles(List<Node> nodes, List<FamilyIssue> issues)
    {
        var state = new byte[nodes.Count];
        foreach (var start in nodes)
        {
            var path = new List<Node>();
            var current = start;
            while (current is not null && state[current.Index] == 0)
            {
                state[current.Index] = 1;
                path.Add(current);
                current = current.Claim;
            }

            if (current is not null && state[current.Index] == 1)
            {
                var cycle = path.Skip(path.IndexOf(current)).ToList();
                var root = cycle.MinBy(n => n.Index)!;
                var members = cycle.OrderBy(n => n.Index).Select(n => n.Source.ControlNumber).ToList();
                root.SetClaim(null, ClaimKind.None);
                foreach (var member in cycle)
                {
                    member.Flag(FamilyStatus.Conflict);
                    issues.Add(new FamilyIssue(member.Source.DocumentId, FamilyIssueKind.Cycle,
                        $"Parent references form a cycle ({string.Join(" → ", members)}); {root.Source.ControlNumber} became the parent.", members, null));
                }
            }

            foreach (var node in path)
            {
                state[node.Index] = 2;
            }
        }
    }

    private static Node Top(Node node)
    {
        var current = node;
        for (var guard = 0; current.Claim is { } parent; guard++)
        {
            if (guard > 1_000_000)
            {
                throw new InvalidOperationException("Family claims still contain a cycle.");
            }

            current = parent;
        }

        return current;
    }

    private static void CrossValidateAttachmentLists(List<Node> nodes, Dictionary<string, Node> byNorm, List<Node> tops, List<FamilyIssue> issues)
    {
        foreach (var parent in nodes.Where(n => n.Source.AttachmentIdsNorm.Count > 0))
        {
            var listed = parent.Source.AttachmentIdsNorm.Distinct(StringComparer.Ordinal).ToList();
            foreach (var id in listed)
            {
                if (!byNorm.TryGetValue(id, out var child))
                {
                    issues.Add(new FamilyIssue(parent.Source.DocumentId, FamilyIssueKind.AttachmentListMismatch,
                        $"AttachmentIDs lists {id}, which is not in the workspace.", [id], null));
                }
                else if (tops[child.Index] != tops[parent.Index])
                {
                    issues.Add(new FamilyIssue(parent.Source.DocumentId, FamilyIssueKind.AttachmentListMismatch,
                        $"AttachmentIDs lists {child.Source.ControlNumber}, which belongs to another family.", [child.Source.ControlNumber], null));
                }
            }

            var set = listed.ToHashSet(StringComparer.Ordinal);
            foreach (var child in nodes.Where(n => n.ClaimKind == ClaimKind.Pointer && n.Claim == parent && !set.Contains(n.Source.ControlNumberNorm)))
            {
                issues.Add(new FamilyIssue(child.Source.DocumentId, FamilyIssueKind.AttachmentListMismatch,
                    $"ParentID names {parent.Source.ControlNumber}, whose AttachmentIDs does not list this document.", [parent.Source.ControlNumber], null));
            }
        }
    }

    private static int LowerBound(List<string> keys, string key)
    {
        int lo = 0, hi = keys.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(keys[mid], key) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private sealed class Node(FamilySource source, string key)
    {
        public FamilySource Source { get; } = source;

        public string Key { get; } = key;

        public int Index { get; set; }

        /// <summary>A ParentID naming the document itself means "no parent".</summary>
        public bool HasPointer { get; } = source.ParentIdNorm is not null && source.ParentIdNorm != source.ControlNumberNorm;

        public Mode Mode => HasPointer ? Mode.Pointer
            : Source.GroupIdentifier is not null ? Mode.Group
            : Source.BegAttachNorm is not null || Source.EndAttachNorm is not null ? Mode.Range
            : Mode.None;

        public Node? Claim { get; private set; }

        public ClaimKind ClaimKind { get; private set; }

        public List<Range> Candidates { get; } = [];

        public List<Range> OwnRanges { get; } = [];

        public Range? ProvisionalRoot { get; set; }

        public Range? ChosenRange { get; set; }

        public FamilyStatus Status { get; private set; } = FamilyStatus.Resolved;

        public void SetClaim(Node? parent, ClaimKind kind)
        {
            Claim = parent;
            ClaimKind = parent is null ? ClaimKind.None : kind;
        }

        /// <summary>Keeps the most severe status: Conflict, InvalidRange, ParentMissing, Gap, Resolved.</summary>
        public void Flag(FamilyStatus status)
        {
            if (Rank(status) > Rank(Status))
            {
                Status = status;
            }
        }

        private static int Rank(FamilyStatus status) => status switch
        {
            FamilyStatus.Conflict => 4,
            FamilyStatus.InvalidRange => 3,
            FamilyStatus.ParentMissing => 2,
            FamilyStatus.Gap => 1,
            _ => 0,
        };
    }

    private sealed class Range(string beg, string end, string begKey, string endKey, Node? parent)
    {
        public string Beg { get; } = beg;

        public string End { get; } = end;

        public string BegKey { get; } = begKey;

        public string EndKey { get; } = endKey;

        public Node? Parent { get; } = parent;

        /// <summary>The parent, or for a range whose parent is missing its lowest claimant (the provisional parent).</summary>
        public Node? Effective { get; set; }

        public List<Node> Members { get; } = [];

        public List<Node> Declarers { get; } = [];

        public bool Contains(Range other) =>
            string.CompareOrdinal(BegKey, other.BegKey) <= 0 && string.CompareOrdinal(EndKey, other.EndKey) >= 0;
    }
}
