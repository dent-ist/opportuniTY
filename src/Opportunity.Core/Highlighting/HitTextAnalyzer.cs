using System.Globalization;
using System.Text;

namespace Opportunity.Core.Highlighting;

/// <summary>A word of the text: its UTF-16 range <c>[Start, End)</c> and its folded form (lower case, ASCII-folded).</summary>
public readonly record struct HitToken(long Start, long End, string Folded);

/// <summary>
/// The tokens a document text is searched by, reproduced in process for term-hit highlighting (E16-T12): the
/// projection's <c>opp_text_search</c> analyzer is the standard (UAX #29 word-break) tokenizer with
/// <c>max_token_length</c> 255, then <c>lowercase</c> and <c>asciifolding</c> (ADR-007; <c>opp_text</c> indexes the
/// folded and the original form at one position, so matching folded tokens is what search does). The word-break rules
/// implemented are those that matter for review text: letters and digits form words; <c>_</c> joins; an apostrophe,
/// <c>.</c>, <c>:</c> or <c>·</c> between two letters and <c>.</c>, <c>,</c> or <c>;</c> between two digits stay inside
/// the word (<c>don't</c>, <c>3.14</c>, <c>1,000</c>); combining marks stay with their letter; every Han ideograph and
/// Hiragana character is a word of its own and Katakana runs are one word. A contract test compares this tokenizer with
/// OpenSearch's <c>_analyze</c> on a mixed-script sample.
/// </summary>
public static class HitTextAnalyzer
{
    /// <summary>The standard tokenizer's <c>max_token_length</c> of the projection mapping: longer words are split.</summary>
    public const int MaxTokenLength = 255;

    private enum Kind
    {
        Other,
        Letter,
        Digit,
        Extend,
        ExtendNumLet,
        MidLetter,
        MidNum,
        MidNumLet,
        Ideographic,
        Katakana,
    }

    /// <summary>
    /// Tokenizes <paramref name="text"/>, whose first character is at <paramref name="offset"/> of the document. When
    /// <paramref name="final"/> is false, a word that touches the end of the text (it may continue in the next part) is
    /// not returned; the return value is where the caller resumes once more text arrives (the text's length when final).
    /// </summary>
    public static int Tokenize(string text, long offset, bool final, List<HitToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tokens);
        var i = 0;
        var consumed = 0;
        while (i < text.Length)
        {
            var kind = KindAt(text, i, out var width);
            if (kind is Kind.Ideographic)
            {
                var end = SkipExtend(text, i + width);
                if (!final && end >= text.Length)
                {
                    return i;
                }

                tokens.Add(new HitToken(offset + i, offset + end, Fold(text.AsSpan(i, end - i))));
                i = consumed = end;
                continue;
            }

            if (kind is not (Kind.Letter or Kind.Digit or Kind.Katakana or Kind.ExtendNumLet))
            {
                i += width;
                consumed = i;
                continue;
            }

            var start = i;
            var wordEnd = ScanWord(text, i, kind, out var hasAlphanumeric);
            if (!final && wordEnd + 1 >= text.Length)
            {
                // The word may go on in the next part (or join it across one mid-word character): resume here.
                return start;
            }

            if (hasAlphanumeric)
            {
                for (var s = start; s < wordEnd;)
                {
                    var e = SplitPoint(text, s, wordEnd);
                    tokens.Add(new HitToken(offset + s, offset + e, Fold(text.AsSpan(s, e - s))));
                    s = e;
                }
            }

            i = consumed = wordEnd;
        }

        return final ? text.Length : consumed;
    }

    /// <summary>All tokens of a complete text (query terms, phrases).</summary>
    public static IReadOnlyList<HitToken> Tokenize(string text)
    {
        var tokens = new List<HitToken>();
        Tokenize(text, 0, final: true, tokens);
        return tokens;
    }

    /// <summary>Lower case and ASCII folding, as the <c>lowercase</c> and <c>asciifolding</c> token filters.</summary>
    public static string Fold(ReadOnlySpan<char> text)
    {
        var ascii = true;
        foreach (var c in text)
        {
            if (c >= 0x80 || char.IsAsciiLetterUpper(c))
            {
                ascii = false;
                break;
            }
        }

        if (ascii)
        {
            return text.ToString();
        }

        var b = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var lower = Rune.ToLowerInvariant(rune);
            if (lower.IsAscii)
            {
                b.Append((char)lower.Value);
            }
            else if (Special(lower.Value) is { } special)
            {
                b.Append(special);
            }
            else
            {
                AppendFolded(b, lower);
            }
        }

        return b.ToString();
    }

    /// <summary>
    /// Folds a wildcard pattern like the planner's normalization (lower case and ASCII folding of everything but the
    /// <c>*</c>, <c>?</c> and <c>\</c> escapes).
    /// </summary>
    public static string FoldPattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return Fold(pattern);
    }

    private static int ScanWord(string text, int start, Kind first, out bool hasAlphanumeric)
    {
        hasAlphanumeric = first is Kind.Letter or Kind.Digit or Kind.Katakana;
        var previous = first;
        var i = SkipExtend(text, start + Width(text, start));
        while (i < text.Length)
        {
            var kind = KindAt(text, i, out var width);
            if (kind == Kind.Extend)
            {
                i += width;
                continue;
            }

            if (Joins(previous, kind))
            {
                hasAlphanumeric |= kind is Kind.Letter or Kind.Digit or Kind.Katakana;
                previous = kind;
                i = SkipExtend(text, i + width);
                continue;
            }

            if (kind is Kind.MidLetter or Kind.MidNum or Kind.MidNumLet && i + width < text.Length)
            {
                var after = SkipExtend(text, i + width);
                var next = after < text.Length ? KindAt(text, after, out _) : Kind.Other;
                var bridge = (previous == Kind.Letter && next == Kind.Letter && kind is Kind.MidLetter or Kind.MidNumLet)
                    || (previous == Kind.Digit && next == Kind.Digit && kind is Kind.MidNum or Kind.MidNumLet);
                if (bridge)
                {
                    i = after;
                    continue;
                }
            }

            break;
        }

        return i;
    }

    private static bool Joins(Kind previous, Kind next) => (previous, next) switch
    {
        (Kind.Letter or Kind.Digit, Kind.Letter or Kind.Digit) => true,
        (Kind.Katakana, Kind.Katakana) => true,
        (Kind.Letter or Kind.Digit or Kind.Katakana or Kind.ExtendNumLet, Kind.ExtendNumLet) => true,
        (Kind.ExtendNumLet, Kind.Letter or Kind.Digit or Kind.Katakana) => true,
        _ => false,
    };

    /// <summary>Where a token longer than <see cref="MaxTokenLength"/> is cut (never inside a surrogate pair).</summary>
    private static int SplitPoint(string text, int start, int end)
    {
        if (end - start <= MaxTokenLength)
        {
            return end;
        }

        var cut = start + MaxTokenLength;
        return char.IsLowSurrogate(text[cut]) ? cut - 1 : cut;
    }

    private static int SkipExtend(string text, int i)
    {
        while (i < text.Length && KindAt(text, i, out var width) == Kind.Extend)
        {
            i += width;
        }

        return i;
    }

    private static int Width(string text, int i) => char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;

    private static Kind KindAt(string text, int i, out int width)
    {
        var c = text[i];
        if (c < 0x80)
        {
            width = 1;
            return c switch
            {
                >= 'a' and <= 'z' or >= 'A' and <= 'Z' => Kind.Letter,
                >= '0' and <= '9' => Kind.Digit,
                '_' => Kind.ExtendNumLet,
                ':' => Kind.MidLetter,
                ',' or ';' => Kind.MidNum,
                '.' or '\'' => Kind.MidNumLet,
                _ => Kind.Other,
            };
        }

        int value;
        if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
        {
            width = 2;
            value = char.ConvertToUtf32(c, text[i + 1]);
        }
        else
        {
            width = 1;
            value = c;
        }

        switch (value)
        {
            case 0x00B7 or 0x0387 or 0x05F4 or 0x2027 or 0xFE13 or 0xFE55 or 0xFF1A:
                return Kind.MidLetter;
            case 0x037E or 0x0589 or 0x060C or 0x060D or 0x066C or 0x07F8 or 0x2044 or 0xFE10 or 0xFE14 or 0xFE50 or 0xFE54 or 0xFF0C
                or 0xFF1B:
                return Kind.MidNum;
            case 0x2018 or 0x2019 or 0x2024 or 0xFE52 or 0xFF07 or 0xFF0E:
                return Kind.MidNumLet;
            case 0x200D or 0x200C:
                return Kind.Extend;
            case >= 0x3040 and <= 0x309F when value is not (0x309B or 0x309C):
                // Hiragana: every character is a token of its own (the standard tokenizer's <HIRAGANA>).
                return value is >= 0x3099 and <= 0x309A ? Kind.Extend : Kind.Ideographic;
            case (>= 0x30A0 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF) or (>= 0xFF66 and <= 0xFF9F) or 0x309B or 0x309C:
                return Kind.Katakana;
            case (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x3FFFF)
                or 0x3005 or 0x3007 or (>= 0x3021 and <= 0x3029):
                return Kind.Ideographic;
        }

        return CharUnicodeInfo.GetUnicodeCategory(value) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber => Kind.Letter,
            UnicodeCategory.DecimalDigitNumber => Kind.Digit,
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.Format => Kind.Extend,
            UnicodeCategory.ConnectorPunctuation => Kind.ExtendNumLet,
            _ => Kind.Other,
        };
    }

    /// <summary>Letters the ASCII folding filter maps to something other than their decomposed base letter.</summary>
    private static string? Special(int c) => c switch
    {
        0x00DF => "ss",
        0x00E6 => "ae",
        0x00F8 => "o",
        0x0153 => "oe",
        0x00F0 => "d",
        0x0111 => "d",
        0x00FE => "th",
        0x0142 => "l",
        0x0131 => "i",
        0x0127 => "h",
        0x0138 => "q",
        0x0149 => "'n",
        0x014B => "n",
        0x0167 => "t",
        0x017F => "s",
        0x0133 => "ij",
        0x2018 or 0x2019 or 0x201B => "'",
        0xFB00 => "ff",
        0xFB01 => "fi",
        0xFB02 => "fl",
        0xFB03 => "ffi",
        0xFB04 => "ffl",
        0xFB06 => "st",
        _ => null,
    };

    /// <summary>
    /// Base letters of U+00C0–U+024F and U+1E00–U+1EFF (accented Latin; generated from the Unicode compatibility
    /// decomposition): <c>-</c> where the character is not an accented ASCII letter. The process runs with invariant
    /// globalization, so <see cref="string.Normalize()"/> is not available.
    /// </summary>
    private const string Latin1AndExtended =
        "aaaaaa-ceeeeiiii-nooooo--uuuuy--aaaaaa-ceeeeiiii-nooooo--uuuuy-yaaaaaaccccccccdd--eeeeeeeeeegggggggghh--iiiiiiiii---jjkk-llllll----nnnnnn---oooooo--rrrrrrsssssssstttt--uuuuuuuuuuuuwwyyyzzzzzzs--------------------------------oo-------------uu----------------------------aaiioouuuuuuuuuu-aaaa----ggkkoooo--j---gg--nnaa----aaaaeeeeiiiioooorrrruuuusstt--hh------aaeeooooooooyy----------------------------";

    private const string LatinAdditional =
        "aabbbbbbccddddddddddeeeeeeeeeeffgghhhhhhhhhhiiiikkkkkkllllllllmmmmmmnnnnnnnnoooooooopppprrrrrrrrssssssssssttttttttuuuuuuuuuuvvvvwwwwwwwwwwxxxxyyzzzzzzhtwy-s----aaaaaaaaaaaaaaaaaaaaaaaaeeeeeeeeeeeeeeeeiiiioooooooooooooooooooooooouuuuuuuuuuuuuuyyyyyyyy------";

    private static void AppendFolded(StringBuilder b, Rune rune)
    {
        var value = rune.Value;
        var folded = value switch
        {
            >= 0xFF01 and <= 0xFF5E => char.ToLowerInvariant((char)(value - 0xFEE0)),
            >= 0x00C0 and <= 0x024F => Latin1AndExtended[value - 0x00C0],
            >= 0x1E00 and <= 0x1EFF => LatinAdditional[value - 0x1E00],
            _ => '-',
        };
        if (folded != '-')
        {
            b.Append(folded);
        }
        else
        {
            b.Append(rune.ToString());
        }
    }
}
