namespace Opportunity.Core.Highlighting;

/// <summary>A highlighted span of the text: UTF-16 offsets <c>[Start, End)</c> from the start of what was scanned.</summary>
public readonly record struct TermHit(int Unit, long Start, long End);

/// <summary>
/// Finds the hits of a set of <see cref="HitUnit"/>s in a text that arrives in parts (E16-T12): the 256 KiB chunks of
/// a document's extracted text, so a text of any length is scanned with memory bounded by one part and the proximity
/// window. A phrase is one span from its first to its last word; a <c>W/n</c> hit is one span from the first word of
/// one operand to the last word of the other, at most n − 1 words apart, in either order. Overlapping spans of the
/// same unit are merged, so the reviewer steps through distinct places.
/// </summary>
public sealed class TermHitScanner
{
    /// <summary>Occurrences of each proximity operand remembered (the most recent ones).</summary>
    private const int RecentOccurrences = 4;

    private readonly IReadOnlyList<HitUnit> _units;
    private readonly UnitState[] _states;
    private readonly HitToken[] _window;
    private readonly List<HitToken> _tokens = [];
    private readonly List<TermHit> _hits = [];
    private readonly Dictionary<string, List<Entry>> _byWord = new(StringComparer.Ordinal);
    private readonly List<Entry> _wildcards = [];
    private readonly List<int> _touched = [];
    private string _pending = string.Empty;
    private long _pendingOffset;
    private long _position;

    public TermHitScanner(IReadOnlyList<HitUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        _units = units;
        _states = [.. units.Select(_ => new UnitState())];
        var longest = units.Count == 0 ? 1 : units.Max(u => u.MaxPatternLength);
        _window = new HitToken[Math.Max(1, longest)];
        for (var u = 0; u < units.Count; u++)
        {
            Index(u, Side.Left, units[u].Left);
            Index(u, Side.Right, units[u].Right ?? []);
        }
    }

    /// <summary>UTF-16 characters received so far.</summary>
    public long Length { get; private set; }

    /// <summary>
    /// Scans the next part of the text and returns the hits completed by it (in order of their end). Call with
    /// <paramref name="final"/> true for the last part (or <see cref="Complete"/>) to flush the last word and spans.
    /// </summary>
    public IReadOnlyList<TermHit> Append(string text, bool final = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        _hits.Clear();
        _tokens.Clear();
        var buffer = _pending.Length == 0 ? text : _pending + text;
        var offset = _pendingOffset;
        Length += text.Length;
        var consumed = HitTextAnalyzer.Tokenize(buffer, offset, final, _tokens);
        _pending = buffer[consumed..];
        _pendingOffset = offset + consumed;
        foreach (var token in _tokens)
        {
            Accept(token);
        }

        if (final)
        {
            Flush();
        }

        return [.. _hits];
    }

    /// <summary>Ends the text: the last word and every open span.</summary>
    public IReadOnlyList<TermHit> Complete() => Append(string.Empty, final: true);

    private void Index(int unit, Side side, IReadOnlyList<HitPattern> patterns)
    {
        foreach (var pattern in patterns)
        {
            var entry = new Entry(unit, side, pattern);
            if (pattern.Wildcard is not null)
            {
                _wildcards.Add(entry);
                continue;
            }

            // Words are found by their last word (the token that completes the occurrence).
            var key = pattern.Words[^1];
            if (!_byWord.TryGetValue(key, out var list))
            {
                _byWord[key] = list = [];
            }

            list.Add(entry);
        }
    }

    private void Accept(HitToken token)
    {
        var position = _position++;
        _window[position % _window.Length] = token;
        _touched.Clear();
        if (_byWord.TryGetValue(token.Folded, out var entries))
        {
            foreach (var entry in entries)
            {
                Consider(entry, token, position);
            }
        }

        foreach (var entry in _wildcards)
        {
            Consider(entry, token, position);
        }

        foreach (var u in _touched)
        {
            var state = _states[u];
            var (left, right) = (state.MatchLeft, state.MatchRight);
            state.MatchLeft = state.MatchRight = null;
            if (_units[u].Kind != HitUnitKind.Proximity)
            {
                Emit(u, state, left!.Value.Start, left.Value.End);
                continue;
            }

            var leftBefore = state.Left.ToArray();
            var rightBefore = state.Right.ToArray();
            if (left is { } l)
            {
                Pair(u, state, l, rightBefore, _units[u].Distance);
                Remember(state.Left, l);
            }

            if (right is { } r)
            {
                Pair(u, state, r, leftBefore, _units[u].Distance);
                Remember(state.Right, r);
            }
        }
    }

    /// <summary>Records the first occurrence per unit and operand that this token completes.</summary>
    private void Consider(Entry entry, HitToken token, long position)
    {
        var state = _states[entry.Unit];
        if ((entry.Side == Side.Left ? state.MatchLeft : state.MatchRight) is not null
            || Match(entry.Pattern, token, position) is not { } occurrence)
        {
            return;
        }

        if (state.MatchLeft is null && state.MatchRight is null)
        {
            _touched.Add(entry.Unit);
        }

        if (entry.Side == Side.Left)
        {
            state.MatchLeft = occurrence;
        }
        else
        {
            state.MatchRight = occurrence;
        }
    }

    /// <summary>The latest occurrence of the other operand that ends before this one starts, if close enough.</summary>
    private void Pair(int unit, UnitState state, Occurrence occurrence, Occurrence[] others, int distance)
    {
        for (var i = others.Length - 1; i >= 0; i--)
        {
            var other = others[i];
            if (other.LastWord >= occurrence.FirstWord)
            {
                continue;
            }

            if (occurrence.FirstWord - other.LastWord - 1 <= distance - 1)
            {
                Emit(unit, state, other.Start, occurrence.End);
            }

            return;
        }
    }

    private static void Remember(Queue<Occurrence> recent, Occurrence occurrence)
    {
        recent.Enqueue(occurrence);
        if (recent.Count > RecentOccurrences)
        {
            recent.Dequeue();
        }
    }

    /// <summary>An occurrence of the pattern that ends with the token at <paramref name="position"/>.</summary>
    private Occurrence? Match(HitPattern pattern, HitToken token, long position)
    {
        if (pattern.Wildcard is not null || pattern.Words.Count == 1)
        {
            return pattern.MatchesToken(token.Folded) ? new Occurrence(position, position, token.Start, token.End) : null;
        }

        var words = pattern.Words;
        var first = position - words.Count + 1;
        if (first < 0 || first < _position - _window.Length)
        {
            return null;
        }

        for (var k = 0; k < words.Count; k++)
        {
            if (_window[(first + k) % _window.Length].Folded != words[k])
            {
                return null;
            }
        }

        return new Occurrence(first, position, _window[first % _window.Length].Start, token.End);
    }

    private void Emit(int unit, UnitState state, long start, long end)
    {
        if (state.Open is { } open && start <= open.End)
        {
            state.Open = (Math.Min(open.Start, start), Math.Max(open.End, end));
            return;
        }

        if (state.Open is { } done)
        {
            _hits.Add(new TermHit(unit, done.Start, done.End));
        }

        state.Open = (start, end);
    }

    /// <summary>Closes spans that no later match can extend (called at the end; spans also close as the next one opens).</summary>
    private void Flush()
    {
        for (var u = 0; u < _states.Length; u++)
        {
            if (_states[u].Open is { } open)
            {
                _hits.Add(new TermHit(u, open.Start, open.End));
                _states[u].Open = null;
            }
        }

        _hits.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Unit.CompareTo(b.Unit));
    }

    private enum Side
    {
        Left,
        Right,
    }

    private readonly record struct Entry(int Unit, Side Side, HitPattern Pattern);

    private readonly record struct Occurrence(long FirstWord, long LastWord, long Start, long End);

    private sealed class UnitState
    {
        public Queue<Occurrence> Left { get; } = new();

        public Queue<Occurrence> Right { get; } = new();

        public (long Start, long End)? Open { get; set; }

        public Occurrence? MatchLeft { get; set; }

        public Occurrence? MatchRight { get; set; }
    }
}
