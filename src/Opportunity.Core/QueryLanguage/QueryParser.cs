namespace Opportunity.Core.QueryLanguage;

/// <summary>
/// Hand-written recursive-descent parser for the ADR-008 M1 grammar. Pure and workspace-independent (R15): it never
/// throws for any input string, reports the first error with an exact span and the expected tokens, and bounds its own
/// recursion by <see cref="QueryLimits.MaxDepth"/>.
/// </summary>
/// <remarks>
/// Precedence (R2), highest first: grouping, <c>field:</c>, <c>NOT</c>, <c>W/n</c> (non-associative), <c>AND</c>
/// (explicit or implicit, R3), <c>OR</c>. Whitespace is required between two words, as the grammar's <c>ws1</c>;
/// next to a parenthesis or quote it is optional (<c>NOT(a)</c>, <c>a"b c"</c>).
/// </remarks>
public static class QueryParser
{
    private static readonly string[] OperandStart =
        [QueryTokens.Term, QueryTokens.Phrase, QueryTokens.Field, QueryTokens.OpenParen, QueryTokens.Not];

    private static readonly string[] FieldValueStart =
        [QueryTokens.Term, QueryTokens.Phrase, QueryTokens.OpenParen, QueryTokens.OpenRange, QueryTokens.OpenExclusiveRange, QueryTokens.Star];

    private static readonly string[] AfterOperand =
        [QueryTokens.And, QueryTokens.Or, QueryTokens.Proximity, QueryTokens.Term, QueryTokens.Phrase, QueryTokens.Field, QueryTokens.OpenParen, QueryTokens.Not];

    public static QueryParseResult Parse(string text, QueryLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        limits ??= QueryLimits.Default;

        if (text.Length > limits.MaxLength)
        {
            return Failed(new QueryDiagnostic(QueryErrorCodes.QueryTooLong,
                $"The query is {text.Length} characters long; the limit is {limits.MaxLength}.",
                new SourceSpan(limits.MaxLength, text.Length)));
        }

        var state = new State(text, limits);
        try
        {
            var ast = state.ParseQuery();
            QueryComplexity.Check(ast, limits);
            return new QueryParseResult(ast, [], state.Warnings);
        }
        catch (QuerySyntaxException e)
        {
            return new QueryParseResult(null, [e.Diagnostic], state.Warnings);
        }
    }

    private static QueryParseResult Failed(QueryDiagnostic error) => new(null, [error], []);

    private sealed class State(string text, QueryLimits limits)
    {
        private readonly QueryLexer _lexer = new(text, limits);
        private Token? _peeked;
        private int _nesting;
        private int _openParens;
        private string? _enclosingField;

        public List<QueryDiagnostic> Warnings { get; } = [];

        public QueryNode ParseQuery()
        {
            if (Peek().Kind == TokenKind.End)
            {
                return new MatchAllNode(new SourceSpan(0, text.Length));
            }

            var root = ParseOr();
            var next = Peek();
            if (next.Kind != TokenKind.End)
            {
                throw next.Kind == TokenKind.CloseParen
                    ? new QuerySyntaxException(QueryErrorCodes.UnbalancedParenthesis, "This ')' has no matching '('.", next.Span)
                    : Unexpected(next, [.. AfterOperand, QueryTokens.EndOfInput]);
            }

            return root;
        }

        private QueryNode ParseOr()
        {
            var first = ParseAnd();
            if (Peek().Kind != TokenKind.Or)
            {
                return first;
            }

            List<QueryNode> children = [first];
            while (Peek().Kind == TokenKind.Or)
            {
                var op = Next();
                RequireOperand(op);
                children.Add(ParseAnd());
            }

            return new OrNode(children, SourceSpan.Cover(first.Span, children[^1].Span));
        }

        private QueryNode ParseAnd()
        {
            var first = ParseProximity();
            List<QueryNode>? children = null;
            while (true)
            {
                var next = Peek();
                if (next.Kind == TokenKind.And)
                {
                    Next();
                    RequireOperand(next);
                }
                else if (!StartsOperand(next.Kind))
                {
                    break;
                }

                children ??= [first];
                children.Add(ParseProximity());
            }

            return children is null ? first : new AndNode(children, SourceSpan.Cover(first.Span, children[^1].Span));
        }

        private QueryNode ParseProximity()
        {
            var left = ParseUnary();
            if (Peek().Kind != TokenKind.Proximity)
            {
                return left;
            }

            var op = Next();
            RequireOperand(op);
            var right = ParseUnary();
            if (Peek() is { Kind: TokenKind.Proximity } chained)
            {
                throw new QuerySyntaxException(QueryErrorCodes.UnsupportedSyntax,
                    $"Chained proximity ('{op.Value} … {chained.Value}') is not supported yet; group with AND instead.", chained.Span);
            }

            CheckProximityOperand(left);
            CheckProximityOperand(right);
            return new ProximityNode(left, right, op.Distance, SourceSpan.Cover(left.Span, right.Span));
        }

        private QueryNode ParseUnary()
        {
            if (Peek().Kind != TokenKind.Not)
            {
                return ParsePrimary();
            }

            var op = Next();
            RequireOperand(op);
            Enter(op);
            var child = ParseUnary();
            _nesting--;
            return new NotNode(child, SourceSpan.Cover(op.Span, child.Span));
        }

        private QueryNode ParsePrimary()
        {
            var token = Peek();
            switch (token.Kind)
            {
                case TokenKind.OpenParen:
                    return ParseGroup();
                case TokenKind.Phrase:
                    Next();
                    return new PhraseNode(token.Value, token.Span);
                case TokenKind.Word:
                    Next();
                    return WordNode(token);
                case TokenKind.Field:
                    return ParseField();
                default:
                    throw ExpectedOperand(token);
            }
        }

        private QueryNode ParseGroup()
        {
            var open = Next();
            Enter(open);
            _openParens++;
            RequireOperand(open);
            var inner = ParseOr();
            var close = Peek();
            if (close.Kind != TokenKind.CloseParen)
            {
                throw close.Kind == TokenKind.End
                    ? new QuerySyntaxException(QueryErrorCodes.UnbalancedParenthesis, "This '(' is never closed.", open.Span, [QueryTokens.CloseParen])
                    : Unexpected(close, [.. AfterOperand, QueryTokens.CloseParen]);
            }

            Next();
            _openParens--;
            _nesting--;
            return inner with { Span = SourceSpan.Cover(open.Span, close.Span) };
        }

        private FieldNode ParseField()
        {
            var field = Next();
            if (_enclosingField is not null)
            {
                throw new QuerySyntaxException(QueryErrorCodes.NestedField,
                    $"'{field.Value}:' cannot be used inside the value of '{_enclosingField}:'.", field.Span);
            }

            var value = Peek();
            if (value.SpaceBefore)
            {
                throw new QuerySyntaxException(QueryErrorCodes.SyntaxError,
                    $"Put the value directly after '{field.Value}:', without a space.",
                    new SourceSpan(field.Span.End, value.Span.Start), FieldValueStart);
            }

            QueryNode child;
            switch (value.Kind)
            {
                case TokenKind.OpenParen:
                    _enclosingField = field.Value;
                    child = ParseGroup();
                    _enclosingField = null;
                    break;
                case TokenKind.Phrase:
                    Next();
                    child = new PhraseNode(value.Value, value.Span);
                    break;
                case TokenKind.OpenBracket or TokenKind.OpenBrace:
                    child = ParseRange();
                    break;
                case TokenKind.Word when value.IsLoneStar:
                    Next();
                    child = new ExistsNode(value.Span);
                    break;
                case TokenKind.Word:
                    Next();
                    child = WordNode(value);
                    break;
                case TokenKind.Field:
                    throw new QuerySyntaxException(QueryErrorCodes.NestedField,
                        $"'{value.Value}:' cannot be the value of '{field.Value}:'. Escape the ':' as \\: to search for it.", value.Span);
                default:
                    throw value.Kind == TokenKind.End
                        ? new QuerySyntaxException(QueryErrorCodes.SyntaxError, $"'{field.Value}:' needs a value.", value.Span, FieldValueStart)
                        : Unexpected(value, FieldValueStart);
            }

            return new FieldNode(field.Value, child, SourceSpan.Cover(field.Span, child.Span));
        }

        private RangeNode ParseRange()
        {
            var open = Next();
            var lower = ParseBound(open.Kind == TokenKind.OpenBracket);
            var to = Peek();
            if (to.Kind != TokenKind.To)
            {
                var message = to.Kind == TokenKind.Word && to.Value.Equals("to", StringComparison.OrdinalIgnoreCase) && !to.HasEscape
                    ? "Write TO in upper case: [a TO b]."
                    : "A range is written [from TO to] (inclusive) or {from TO to} (exclusive).";
                throw new QuerySyntaxException(QueryErrorCodes.InvalidRange, message, to.Span, [QueryTokens.To]);
            }

            Next();
            var upper = ParseBound(inclusive: true);
            var close = Next();
            bool inclusive;
            switch (close.Kind)
            {
                case TokenKind.CloseBracket:
                    inclusive = true;
                    break;
                case TokenKind.CloseBrace:
                    inclusive = false;
                    break;
                case TokenKind.End:
                    throw new QuerySyntaxException(QueryErrorCodes.InvalidRange, "The range is never closed.",
                        new SourceSpan(open.Span.Start, text.Length), [QueryTokens.CloseInclusive, QueryTokens.CloseExclusive]);
                default:
                    throw new QuerySyntaxException(QueryErrorCodes.InvalidRange,
                        "Close the range with ']' (inclusive) or '}' (exclusive).", close.Span, [QueryTokens.CloseInclusive, QueryTokens.CloseExclusive]);
            }

            return new RangeNode(lower, upper is null ? null : upper with { Inclusive = inclusive }, SourceSpan.Cover(open.Span, close.Span));
        }

        private RangeBound? ParseBound(bool inclusive)
        {
            string[] expected = [QueryTokens.Term, QueryTokens.Phrase, QueryTokens.Star];
            var token = Next();
            switch (token.Kind)
            {
                case TokenKind.Word when token.IsLoneStar:
                    return null;
                case TokenKind.Word when token.IsWildcard:
                    throw new QuerySyntaxException(QueryErrorCodes.InvalidRange,
                        "Wildcards are not allowed in range bounds; use * alone for an open bound, or escape it as \\*.", token.Span, expected);
                case TokenKind.Word:
                case TokenKind.Phrase:
                    return new RangeBound(token.Value, inclusive);
                case TokenKind.End:
                    throw new QuerySyntaxException(QueryErrorCodes.InvalidRange, "The range is incomplete.", token.Span, expected);
                default:
                    throw new QuerySyntaxException(QueryErrorCodes.InvalidRange,
                        "A range bound is a term, a quoted phrase or * (open).", token.Span, expected);
            }
        }

        private QueryNode WordNode(Token token)
        {
            if (token.IsWildcard)
            {
                return new WildcardNode(token.Pattern, token.Span);
            }

            var upper = token.Value.ToUpperInvariant();
            if (!token.HasEscape && upper is "AND" or "OR" or "NOT")
            {
                Warnings.Add(new QueryDiagnostic(QueryWarningCodes.LowercaseOperator,
                    $"'{token.Value}' is searched as a word. Did you mean {upper}? Operators must be upper case.", token.Span));
            }

            return new TermNode(token.Value, token.Span);
        }

        /// <summary>R6: a term, wildcard, phrase, or a parenthesized OR of those; the field goes around the whole proximity.</summary>
        private static void CheckProximityOperand(QueryNode operand)
        {
            switch (operand)
            {
                case TermNode or PhraseNode or WildcardNode:
                    return;
                case OrNode or:
                    foreach (var child in or.Children)
                    {
                        if (child is not (TermNode or PhraseNode or WildcardNode))
                        {
                            CheckProximityOperand(child);
                            throw new QuerySyntaxException(QueryErrorCodes.UnsupportedSyntax,
                                "Inside a W/n operand, OR may only combine terms, wildcards and phrases.", child.Span);
                        }
                    }

                    return;
                case FieldNode field:
                    throw new QuerySyntaxException(QueryErrorCodes.InvalidProximityOperand,
                        $"Both sides of W/n search the same field: write {field.Name}:(a W/n b) instead.", operand.Span);
                case AndNode:
                    throw new QuerySyntaxException(QueryErrorCodes.UnsupportedSyntax,
                        "AND inside a W/n operand is not supported yet.", operand.Span);
                case NotNode:
                    throw new QuerySyntaxException(QueryErrorCodes.UnsupportedSyntax,
                        "NOT inside a W/n operand is not supported yet.", operand.Span);
                case ProximityNode:
                    throw new QuerySyntaxException(QueryErrorCodes.UnsupportedSyntax,
                        "Nested proximity is not supported yet.", operand.Span);
                default:
                    throw new QuerySyntaxException(QueryErrorCodes.UnsupportedSyntax,
                        "This is not allowed as a W/n operand.", operand.Span);
            }
        }

        private void Enter(Token token)
        {
            if (++_nesting > limits.MaxDepth)
            {
                throw new QuerySyntaxException(QueryErrorCodes.QueryTooDeep,
                    $"The query nests more than {limits.MaxDepth} levels deep.", token.Span);
            }
        }

        private void RequireOperand(Token op)
        {
            var next = Peek();
            if (!StartsOperand(next.Kind))
            {
                throw ExpectedOperand(next, op);
            }
        }

        private QuerySyntaxException ExpectedOperand(Token token, Token? after = null)
        {
            var what = after is { } op ? Describe(op) : null;
            return token.Kind switch
            {
                TokenKind.End => new QuerySyntaxException(QueryErrorCodes.SyntaxError,
                    what is null ? "The query ends too early." : $"The query ends after {what}; a term is missing.", token.Span, OperandStart),
                TokenKind.CloseParen when _openParens == 0 =>
                    new QuerySyntaxException(QueryErrorCodes.UnbalancedParenthesis, "This ')' has no matching '('.", token.Span),
                TokenKind.CloseParen => new QuerySyntaxException(QueryErrorCodes.SyntaxError,
                    what is null ? "Empty parentheses." : $"A term is missing after {what}.", token.Span, OperandStart),
                TokenKind.And or TokenKind.Or or TokenKind.To or TokenKind.Proximity => new QuerySyntaxException(QueryErrorCodes.SyntaxError,
                    $"{Describe(token)} needs a term on both sides. To search for the word, quote it: \"{token.Value}\".", token.Span, OperandStart),
                TokenKind.OpenBracket or TokenKind.OpenBrace => new QuerySyntaxException(QueryErrorCodes.InvalidRange,
                    $"Unexpected {Describe(token)}: a range needs a field, e.g. date:[2025-01-01 TO 2025-12-31]. Escape it as \\{text[token.Span.Start]} to search for it.",
                    token.Span, OperandStart),
                _ => Unexpected(token, OperandStart),
            };
        }

        private static QuerySyntaxException Unexpected(Token token, string[] expected) =>
            new(QueryErrorCodes.SyntaxError, $"Unexpected {Describe(token)}.", token.Span, expected);

        private static string Describe(Token token) => token.Kind switch
        {
            TokenKind.End => "end of query",
            TokenKind.Word or TokenKind.Phrase => $"term '{token.Value}'",
            TokenKind.Field => $"field '{token.Value}:'",
            TokenKind.OpenParen => "'('",
            TokenKind.CloseParen => "')'",
            TokenKind.OpenBracket => "'['",
            TokenKind.CloseBracket => "']'",
            TokenKind.OpenBrace => "'{'",
            TokenKind.CloseBrace => "'}'",
            _ => token.Value,
        };

        private static bool StartsOperand(TokenKind kind) =>
            kind is TokenKind.Word or TokenKind.Phrase or TokenKind.Field or TokenKind.OpenParen or TokenKind.Not;

        private Token Peek() => _peeked ??= _lexer.Next();

        private Token Next()
        {
            var token = Peek();
            _peeked = null;
            return token;
        }
    }
}

/// <summary>Post-parse limits of ADR-008 §5 on the finished AST; the error span is the first node over the limit.</summary>
internal static class QueryComplexity
{
    public static void Check(QueryNode root, QueryLimits limits)
    {
        int nodes = 0, terms = 0, wildcards = 0;
        Visit(root, 1);

        void Visit(QueryNode node, int depth)
        {
            if (depth > limits.MaxDepth)
            {
                throw new QuerySyntaxException(QueryErrorCodes.QueryTooDeep,
                    $"The query nests more than {limits.MaxDepth} levels deep.", node.Span);
            }

            if (++nodes > limits.MaxNodes)
            {
                throw new QuerySyntaxException(QueryErrorCodes.TooManyNodes,
                    $"The query has more than {limits.MaxNodes} clauses and operators.", node.Span);
            }

            if (node is TermNode or PhraseNode or WildcardNode or RangeNode or ExistsNode && ++terms > limits.MaxTerms)
            {
                throw new QuerySyntaxException(QueryErrorCodes.TooManyTerms,
                    $"The query has more than {limits.MaxTerms} search terms.", node.Span);
            }

            if (node is WildcardNode && ++wildcards > limits.MaxWildcardTerms)
            {
                throw new QuerySyntaxException(QueryErrorCodes.TooManyWildcards,
                    $"The query has more than {limits.MaxWildcardTerms} wildcard terms.", node.Span);
            }

            switch (node)
            {
                case OrNode or:
                    foreach (var child in or.Children)
                    {
                        Visit(child, depth + 1);
                    }

                    break;
                case AndNode and:
                    foreach (var child in and.Children)
                    {
                        Visit(child, depth + 1);
                    }

                    break;
                case NotNode not:
                    Visit(not.Child, depth + 1);
                    break;
                case ProximityNode proximity:
                    Visit(proximity.Left, depth + 1);
                    Visit(proximity.Right, depth + 1);
                    break;
                case FieldNode field:
                    Visit(field.Child, depth + 1);
                    break;
            }
        }
    }
}
