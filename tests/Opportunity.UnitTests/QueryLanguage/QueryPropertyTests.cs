using System.Diagnostics;
using System.Text;

using AwesomeAssertions;

using Opportunity.Core.QueryLanguage;

namespace Opportunity.UnitTests.QueryLanguage;

/// <summary>
/// Property and fuzz tests (ADR-008 R19): <c>Parse(Print(ast)) == ast</c> for generated ASTs, and no crash or unbounded
/// parse time on 100K random inputs.
/// </summary>
public class QueryPropertyTests
{
    private const int FuzzInputs = 100_000;

    /// <summary>Generated trees may hold more wildcards than the default limit; limits have their own tests.</summary>
    private static readonly QueryLimits Unlimited = new() { MaxTerms = 100_000, MaxWildcardTerms = 100_000, MaxNodes = 100_000 };

    [Fact]
    public void Printed_ast_parses_back_to_the_same_ast()
    {
        var random = new Random(20261003);
        for (var i = 0; i < 20_000; i++)
        {
            var ast = new AstGenerator(random).Query();
            var text = QueryPrinter.Print(ast);

            var result = QueryParser.Parse(text, Unlimited);

            result.Errors.Should().BeEmpty($"'{text}' was printed from a valid AST");
            QueryAstComparer.IgnoringSpans.Equals(result.Ast, ast).Should().BeTrue($"'{text}' must round-trip, got '{result.Ast}'");
            QueryPrinter.Print(result.Ast!).Should().Be(text, "printing is canonical");
        }
    }

    [Fact]
    public void Random_input_never_crashes_and_parses_in_bounded_time()
    {
        var random = new Random(68);
        var slowest = TimeSpan.Zero;
        var slowestText = string.Empty;
        var total = Stopwatch.StartNew();
        int parsed = 0;
        for (var i = 0; i < FuzzInputs; i++)
        {
            var text = RandomInput(random, random.Next(0, 80));
            var watch = Stopwatch.StartNew();
            var result = QueryParser.Parse(text);
            watch.Stop();
            if (i > 1_000 && watch.Elapsed > slowest)
            {
                slowest = watch.Elapsed;
                slowestText = text;
            }

            AssertWellFormed(text, result);
            if (result.Success)
            {
                parsed++;
            }
        }

        total.Stop();
        parsed.Should().BeGreaterThan(FuzzInputs / 20, "the fuzzer must also reach the success paths");
        // A pathological input is slow every time; a GC pause or a busy machine is not. Re-time the slowest input.
        var retimed = Enumerable.Range(0, 3).Min(_ =>
        {
            var watch = Stopwatch.StartNew();
            QueryParser.Parse(slowestText);
            return watch.Elapsed;
        });
        retimed.Should().BeLessThan(TimeSpan.FromSeconds(1), $"parsing is linear (first measured {slowest} for {slowestText.Length} characters)");
        total.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Maximum_length_random_input_parses_in_bounded_time()
    {
        var random = new Random(9);
        for (var i = 0; i < 200; i++)
        {
            var text = RandomInput(random, QueryLimits.Default.MaxLength);
            var watch = Stopwatch.StartNew();
            var result = QueryParser.Parse(text);
            watch.Stop();

            AssertWellFormed(text, result);
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        }
    }

    private static void AssertWellFormed(string text, QueryParseResult result)
    {
        if (result.Success)
        {
            result.Errors.Should().BeEmpty();
            var printed = QueryPrinter.Print(result.Ast!);
            var reparsed = QueryParser.Parse(printed);
            reparsed.Success.Should().BeTrue($"'{text}' printed as '{printed}': {string.Join("; ", reparsed.Errors.Select(e => e.Message))}");
            QueryAstComparer.IgnoringSpans.Equals(reparsed.Ast, result.Ast).Should().BeTrue($"'{text}' printed as '{printed}'");
            AssertSpans(text, result.Ast!);
        }
        else
        {
            var error = result.Errors.Should().ContainSingle().Subject;
            error.Span.Start.Should().BeInRange(0, text.Length, text);
            error.Span.End.Should().BeInRange(error.Span.Start, text.Length, text);
            error.Code.Should().NotBeNullOrEmpty();
        }

        foreach (var warning in result.Warnings)
        {
            warning.Span.End.Should().BeInRange(warning.Span.Start, text.Length);
        }
    }

    private static void AssertSpans(string text, QueryNode node)
    {
        (node.Span.Start >= 0 && node.Span.Start <= node.Span.End && node.Span.End <= text.Length).Should().BeTrue($"{node.Span} in '{text}'");
        IEnumerable<QueryNode> children = node switch
        {
            OrNode or => or.Children,
            AndNode and => and.Children,
            NotNode not => [not.Child],
            ProximityNode p => [p.Left, p.Right],
            FieldNode f => [f.Child],
            _ => [],
        };
        foreach (var child in children)
        {
            (child.Span.Start >= node.Span.Start && child.Span.End <= node.Span.End).Should().BeTrue($"child {child.Span} inside {node.Span} in '{text}'");
            AssertSpans(text, child);
        }
    }

    private static readonly string[] Fragments =
    [
        " ", " ", " ", "  ", "(", ")", "\"", "\\", ":", "[", "]", "{", "}", "*", "?", "-", "+", "!", "~", "^", "/",
        "a", "b", "c", "W", "w", "0", "5", "ü", "😀", "\uD800", "\t",
        " AND ", " OR ", "NOT ", " TO ", " W/3 ", " w/10 ", " W/0 ", " PRE/2 ", "W/s", "and", "or",
        "custodian:", "date:", "filename:", "contract", "abc*", "\"trade secret\"", "[2020 TO 2021]", "{a TO *}", "x:*",
    ];

    private static string RandomInput(Random random, int length)
    {
        var b = new StringBuilder(length + 16);
        while (b.Length < length)
        {
            b.Append(random.Next(4) == 0 ? (char)random.Next(0x20, 0x7F) : Fragments[random.Next(Fragments.Length)]);
        }

        return b.Length > length ? b.ToString(0, length) : b.ToString();
    }

    /// <summary>Generates ASTs the M1 grammar can express (R6 operand rules, no nested fields, within the limits).</summary>
    private sealed class AstGenerator(Random random)
    {
        private const string TermChars = "abcxyzAZ09-_.'/&@#$%,;=<>|üß😀 ()\"\\:[]{}!~^*?+\t";
        private static SourceSpan None => default;
        private static readonly string[] OperatorLookalikes = ["AND", "OR", "NOT", "TO", "W/5", "w/12", "PRE/3", "W/s", "and", "-x", "+y"];

        public QueryNode Query() => random.Next(50) == 0 ? new MatchAllNode(None) : Node(depth: 0, inField: false);

        private QueryNode Node(int depth, bool inField)
        {
            var choice = depth >= 3 ? random.Next(6) : random.Next(11);
            return choice switch
            {
                0 or 1 => Term(),
                2 => Phrase(),
                3 => Wildcard(),
                4 when !inField => Field(depth),
                4 or 5 => ProximityOperandLeaf(),
                6 => new OrNode(Children(depth, inField), None),
                7 => new AndNode(Children(depth, inField), None),
                8 => new NotNode(Node(depth + 1, inField), None),
                _ => Proximity(),
            };
        }

        private List<QueryNode> Children(int depth, bool inField) =>
            [.. Enumerable.Range(0, random.Next(2, 5)).Select(_ => Node(depth + 1, inField))];

        private ProximityNode Proximity() =>
            new(ProximityOperand(), ProximityOperand(), random.Next(1, 1001), None);

        private QueryNode ProximityOperand() =>
            random.Next(4) == 0
                ? new OrNode([.. Enumerable.Range(0, random.Next(2, 4)).Select(_ => ProximityOperandLeaf())], None)
                : ProximityOperandLeaf();

        private QueryNode ProximityOperandLeaf() => random.Next(3) switch
        {
            0 => Term(),
            1 => Phrase(),
            _ => Wildcard(),
        };

        private FieldNode Field(int depth)
        {
            QueryNode child = random.Next(7) switch
            {
                0 => Term(),
                1 => Phrase(),
                2 => Wildcard(),
                3 => new ExistsNode(None),
                4 => new RangeNode(Bound(), Bound(), None),
                _ => random.Next(2) == 0 ? Proximity() : new OrNode(Children(depth + 1, inField: true), None),
            };
            return new FieldNode(FieldName(), child, None);
        }

        private string FieldName()
        {
            const string first = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZé";
            const string rest = "abcdefghijklmnopqrstuvwxyz0123456789_.-";
            var b = new StringBuilder().Append(first[random.Next(first.Length)]);
            for (var i = random.Next(0, 10); i > 0; i--)
            {
                b.Append(rest[random.Next(rest.Length)]);
            }

            return b.ToString();
        }

        private RangeBound? Bound()
        {
            if (random.Next(4) == 0)
            {
                return null;
            }

            string value;
            do
            {
                value = Text(1, 12);
            }
            while (string.IsNullOrWhiteSpace(value));

            return new RangeBound(value, random.Next(2) == 0);
        }

        private TermNode Term()
        {
            // Mostly plain words, sometimes operator look-alikes and characters that need escaping.
            return random.Next(10) switch
            {
                0 => new TermNode(OperatorLookalikes[random.Next(OperatorLookalikes.Length)], None),
                < 4 => new TermNode(Text(1, 10), None),
                _ => new TermNode(Word(), None),
            };
        }

        private PhraseNode Phrase()
        {
            string text;
            do
            {
                text = random.Next(2) == 0 ? Word() + " " + Word() : Text(1, 15);
            }
            while (string.IsNullOrWhiteSpace(text));

            return new PhraseNode(text, None);
        }

        private WildcardNode Wildcard()
        {
            var b = new StringBuilder();
            var hasWildcard = false;
            for (var i = random.Next(1, 8); i > 0 || !hasWildcard; i--)
            {
                var r = random.Next(10);
                if (r < 3)
                {
                    b.Append(r == 0 ? '?' : '*');
                    hasWildcard = true;
                }
                else
                {
                    var c = TermChars[random.Next(TermChars.Length)];
                    if (c is '*' or '?' or '\\')
                    {
                        b.Append('\\');
                    }

                    b.Append(c);
                }
            }

            return new WildcardNode(b.ToString(), None);
        }

        private string Word()
        {
            var b = new StringBuilder();
            for (var i = random.Next(1, 9); i > 0; i--)
            {
                b.Append((char)('a' + random.Next(26)));
            }

            return b.ToString();
        }

        private string Text(int min, int max)
        {
            var b = new StringBuilder();
            for (var i = random.Next(min, max + 1); i > 0; i--)
            {
                var c = TermChars[random.Next(TermChars.Length)];
                if (char.IsHighSurrogate(c))
                {
                    continue;
                }

                b.Append(c);
            }

            return b.Length == 0 ? "x" : b.ToString();
        }
    }
}
