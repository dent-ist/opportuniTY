using System.Text;

namespace Opportunity.Core.QueryLanguage;

internal enum TokenKind
{
    End,
    Word,
    Field,
    Phrase,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    OpenBrace,
    CloseBrace,
    And,
    Or,
    Not,
    To,
    Proximity,
}

/// <summary>
/// One lexical token. For <see cref="TokenKind.Word"/>: <see cref="Value"/> is the unescaped text and
/// <see cref="Pattern"/> the wildcard pattern (<see cref="WildcardNode.Pattern"/> escaping). For
/// <see cref="TokenKind.Field"/>: <see cref="Value"/> is the name and the span covers <c>name:</c>.
/// </summary>
internal readonly record struct Token(
    TokenKind Kind,
    SourceSpan Span,
    bool SpaceBefore,
    string Value = "",
    string Pattern = "",
    bool IsWildcard = false,
    bool HasEscape = false,
    int Distance = 0)
{
    /// <summary>An unescaped lone <c>*</c>: exists after <c>field:</c>, an open bound in a range.</summary>
    public bool IsLoneStar => Kind == TokenKind.Word && IsWildcard && Pattern == "*";
}

/// <summary>
/// Lazy lexer for the ADR-008 grammar: tokens are produced on demand so errors are reported in text order. Throws
/// <see cref="QuerySyntaxException"/> on lexical errors.
/// </summary>
internal sealed class QueryLexer(string text, QueryLimits limits)
{
    private int _position;

    public static bool IsReserved(char c) => c is '(' or ')' or '"' or ':' or '[' or ']' or '{' or '}' or '\\' or '!' or '~' or '^';

    public static bool IsFieldName(string name)
    {
        if (name.Length == 0 || !char.IsLetter(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A whole unquoted token <c>[Ww]/[0-9]+</c> is the proximity operator (R1).</summary>
    public static bool IsProximityOperator(string raw) =>
        raw.Length >= 3 && raw[0] is 'W' or 'w' && raw[1] == '/' && AllAsciiDigits(raw.AsSpan(2));

    /// <summary>Operators reserved for later (R4): <c>PRE/n</c>, <c>W/s</c>, <c>W/p</c>, case-insensitive.</summary>
    public static bool IsUnsupportedOperator(string raw) =>
        (raw.Length >= 5 && raw.StartsWith("PRE/", StringComparison.OrdinalIgnoreCase) && AllAsciiDigits(raw.AsSpan(4)))
        || raw.Equals("W/s", StringComparison.OrdinalIgnoreCase)
        || raw.Equals("W/p", StringComparison.OrdinalIgnoreCase);

    public static bool IsOperatorWord(string raw) => raw is "AND" or "OR" or "NOT" or "TO";

    public Token Next()
    {
        var start = _position;
        while (_position < text.Length && char.IsWhiteSpace(text[_position]))
        {
            _position++;
        }

        var space = _position > start || _position == 0;
        if (_position >= text.Length)
        {
            return new Token(TokenKind.End, new SourceSpan(text.Length, text.Length), space);
        }

        var c = text[_position];
        var single = new SourceSpan(_position, _position + 1);
        switch (c)
        {
            case '(':
                _position++;
                return new Token(TokenKind.OpenParen, single, space);
            case ')':
                _position++;
                return new Token(TokenKind.CloseParen, single, space);
            case '[':
                _position++;
                return new Token(TokenKind.OpenBracket, single, space);
            case ']':
                _position++;
                return new Token(TokenKind.CloseBracket, single, space);
            case '{':
                _position++;
                return new Token(TokenKind.OpenBrace, single, space);
            case '}':
                _position++;
                return new Token(TokenKind.CloseBrace, single, space);
            case '"':
                return ReadPhrase(space);
            case ':':
                throw UnexpectedColon(_position);
            case '!':
                throw new QuerySyntaxException(QueryErrorCodes.UnsupportedSyntax,
                    "Root expansion '!' is not supported yet. Escape it as \\! to search for it.", single);
            case '~':
                throw new QuerySyntaxException(QueryErrorCodes.UnsupportedSyntax,
                    "Fuzzy and slop operators '~' are not supported yet. Escape it as \\~ to search for it.", single);
            case '^':
                throw new QuerySyntaxException(QueryErrorCodes.UnsupportedSyntax,
                    "Boosting '^' is not supported. Escape it as \\^ to search for it.", single);
            case '-':
                throw new QuerySyntaxException(QueryErrorCodes.ReservedPrefix,
                    "A leading '-' is reserved. Use NOT to exclude a term, or escape it as \\-.", single);
            case '+':
                throw new QuerySyntaxException(QueryErrorCodes.ReservedPrefix,
                    "A leading '+' is reserved. Terms are required by default (AND); escape it as \\+ to search for it.", single);
            default:
                return ReadWord(space);
        }
    }

    private Token ReadPhrase(bool space)
    {
        var start = _position;
        var value = new StringBuilder();
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                _position = i + 1;
                var span = new SourceSpan(start, _position);
                if (string.IsNullOrWhiteSpace(value.ToString()))
                {
                    throw new QuerySyntaxException(QueryErrorCodes.EmptyPhrase, "A phrase cannot be empty.", span);
                }

                return new Token(TokenKind.Phrase, span, space, value.ToString());
            }

            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '*' or '?')
            {
                value.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c is '*' or '?')
            {
                throw new QuerySyntaxException(QueryErrorCodes.UnsupportedSyntax,
                    $"Wildcards inside phrases are not supported yet. Escape it as \\{c} to search for it literally.",
                    new SourceSpan(i, i + 1));
            }

            // Inside a phrase only \" and \\ (plus \* and \?) are escapes; any other backslash is literal (R4).
            value.Append(c);
            i++;
        }

        throw new QuerySyntaxException(QueryErrorCodes.UnterminatedPhrase, "The phrase has no closing quote.",
            new SourceSpan(start, text.Length), [QueryTokens.Quote]);
    }

    private Token ReadWord(bool space)
    {
        var start = _position;
        var value = new StringBuilder();
        var pattern = new StringBuilder();
        bool wildcard = false, escaped = false;
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\')
            {
                if (i + 1 >= text.Length)
                {
                    throw new QuerySyntaxException(QueryErrorCodes.DanglingEscape,
                        "'\\' at the end of the query escapes nothing. Write \\\\ to search for a backslash.",
                        new SourceSpan(i, i + 1));
                }

                var next = text[i + 1];
                value.Append(next);
                if (next is '*' or '?' or '\\')
                {
                    pattern.Append('\\');
                }

                pattern.Append(next);
                escaped = true;
                i += 2;
                continue;
            }

            if (char.IsWhiteSpace(c) || IsReserved(c))
            {
                break;
            }

            wildcard |= c is '*' or '?';
            value.Append(c);
            pattern.Append(c);
            i++;
        }

        var raw = text[start..i];
        var span = new SourceSpan(start, i);
        if (i < text.Length && text[i] == ':')
        {
            if (escaped || !IsFieldName(raw))
            {
                throw UnexpectedColon(i);
            }

            _position = i + 1;
            return new Token(TokenKind.Field, new SourceSpan(start, i + 1), space, raw);
        }

        _position = i;
        if (!escaped)
        {
            switch (raw)
            {
                case "AND":
                    return new Token(TokenKind.And, span, space, raw);
                case "OR":
                    return new Token(TokenKind.Or, span, space, raw);
                case "NOT":
                    return new Token(TokenKind.Not, span, space, raw);
                case "TO":
                    return new Token(TokenKind.To, span, space, raw);
            }

            if (IsProximityOperator(raw))
            {
                var digits = raw.AsSpan(2).TrimStart('0');
                var distance = digits.Length is > 0 and <= 9 ? int.Parse(digits, provider: System.Globalization.CultureInfo.InvariantCulture) : digits.Length == 0 ? 0 : int.MaxValue;
                if (distance < 1 || distance > limits.MaxProximityDistance)
                {
                    throw new QuerySyntaxException(QueryErrorCodes.ProximityDistanceOutOfRange,
                        $"The distance of {raw} must be between 1 and {limits.MaxProximityDistance}.", span);
                }

                return new Token(TokenKind.Proximity, span, space, raw, Distance: distance);
            }

            if (IsUnsupportedOperator(raw))
            {
                throw new QuerySyntaxException(QueryErrorCodes.UnsupportedSyntax,
                    $"'{raw}' is not supported yet; use W/n (within n words, either order). Escape the '/' to search for it literally.", span);
            }
        }

        return new Token(TokenKind.Word, span, space, value.ToString(), pattern.ToString(), wildcard, escaped);
    }

    private static QuerySyntaxException UnexpectedColon(int position) =>
        new(QueryErrorCodes.SyntaxError,
            "Unexpected ':'. Write field:value with a field name (a letter, then letters, digits, '_', '.' or '-'), or escape it as \\:.",
            new SourceSpan(position, position + 1));

    private static bool AllAsciiDigits(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class QuerySyntaxException(QueryDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    public QuerySyntaxException(string code, string message, SourceSpan span, IReadOnlyList<string>? expected = null)
        : this(new QueryDiagnostic(code, message, span, expected ?? []))
    {
    }

    public QueryDiagnostic Diagnostic { get; } = diagnostic;
}
