namespace Opportunity.Benchmarks.Workloads;

public delegate void TokenAction(ReadOnlySpan<char> token);

/// <summary>
/// Approximates <c>opp_text_search</c> (ADR-007 §5: standard tokenizer, lowercase) closely enough to estimate hit
/// counts: tokens are runs of letters/digits; <c>'</c>, <c>.</c> and <c>_</c> between two letters/digits stay inside the
/// token (UAX #29 MidLetter/MidNumLet/ExtendNumLet), every other character splits; tokens longer than 255 characters
/// are split. ASCII folding is not applied (the synthetic vocabulary is ASCII). The authoritative counts are the
/// golden known-answer tests (E17-T09), not this tokenizer.
/// </summary>
public static class TextTokenizer
{
    public const int MaxTokenLength = 255;

    public static void Tokenize(ReadOnlySpan<char> text, TokenAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Span<char> buffer = stackalloc char[MaxTokenLength];
        int length = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool word = IsWordChar(c)
                || (length > 0 && c is '\'' or '.' or '_' && i + 1 < text.Length && IsWordChar(text[i + 1]));
            if (word)
            {
                if (length == MaxTokenLength)
                {
                    action(buffer[..length]);
                    length = 0;
                }

                buffer[length++] = char.IsAsciiLetterUpper(c) ? (char)(c | 0x20) : c < 128 ? c : char.ToLowerInvariant(c);
            }
            else if (length > 0)
            {
                action(buffer[..length]);
                length = 0;
            }
        }

        if (length > 0)
        {
            action(buffer[..length]);
        }
    }

    private static bool IsWordChar(char c) => c < 128 ? char.IsAsciiLetterOrDigit(c) : char.IsLetterOrDigit(c);

    public static IReadOnlyList<string> Tokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<string>();
        Tokenize(text, t => tokens.Add(t.ToString()));
        return tokens;
    }
}

/// <summary>Interns tokens to dense ids without allocating a string per occurrence.</summary>
internal sealed class TokenTable
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly List<string> _words = [];
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public TokenTable()
    {
        _lookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public int Count => _words.Count;

    public string this[int id] => _words[id];

    public int GetOrAdd(ReadOnlySpan<char> token)
    {
        if (_lookup.TryGetValue(token, out int id))
        {
            return id;
        }

        string word = token.ToString();
        id = _words.Count;
        _ids.Add(word, id);
        _words.Add(word);
        return id;
    }

    public int Find(ReadOnlySpan<char> token) => _lookup.TryGetValue(token, out int id) ? id : -1;
}
