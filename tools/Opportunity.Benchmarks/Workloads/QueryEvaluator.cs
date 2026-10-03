using System.Globalization;

using Opportunity.DataGenerator.Corpus.Model;

namespace Opportunity.Benchmarks.Workloads;

/// <summary>How a query's hit count is obtained.</summary>
internal enum EvaluationMode
{
    /// <summary>Metadata and coding only: counted exactly over every document.</summary>
    Metadata,

    /// <summary>Default-field leaves are planted needle/proximity words: counted exactly from the generator's ground truth.</summary>
    Planted,

    /// <summary>Vocabulary text: counted on the synthesised text of the evaluation sample (exact when it is the whole corpus).</summary>
    Text,
}

/// <summary>
/// Reference evaluator for the generated queries: ADR-008 §2 semantics over <see cref="DocumentFacts"/>. Field names
/// are the ADR-007 §3 / ADR-008 R11 query names the benchmark workspace exposes.
/// </summary>
internal sealed class QueryEvaluator(CodingFixture fixture, IReadOnlySet<string> plantedWords)
{
    private readonly Dictionary<OqlNode, int[]> _sequences = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<OqlNode, int[]> _expansions = new(ReferenceEqualityComparer.Instance);

    public static readonly IReadOnlyList<string> MetadataFields = ["custodian", "extension", "confidentiality", "language", "projectcode", "date", "filename", "controlnumber"];

    public EvaluationMode ModeOf(OqlNode node)
    {
        var mode = EvaluationMode.Metadata;
        Visit(node, inField: false, leaf =>
        {
            EvaluationMode leafMode = leaf is OqlTerm term && plantedWords.Contains(term.Text) ? EvaluationMode.Planted : EvaluationMode.Text;
            mode = (EvaluationMode)Math.Max((int)mode, (int)leafMode);
        });
        return mode;
    }

    /// <summary>Resolves the text leaves of <paramref name="node"/> against the sample's token table; returns the token ids it needs.</summary>
    public IEnumerable<int> Bind(OqlNode node, TokenTable tokens)
    {
        var ids = new List<int>();
        Visit(node, inField: false, leaf =>
        {
            switch (leaf)
            {
                case OqlTerm or OqlPhrase:
                    string text = leaf is OqlTerm t ? t.Text : ((OqlPhrase)leaf).Text;
                    int[] sequence = [.. TextTokenizer.Tokens(text).Select(tok => tokens.Find(tok))];
                    _sequences[leaf] = sequence;
                    ids.AddRange(sequence.Where(id => id >= 0));
                    break;
                case OqlWildcard w:
                    string pattern = w.Pattern.ToLowerInvariant();
                    int[] expansion = [.. Enumerable.Range(0, tokens.Count).Where(id => Glob.IsMatch(pattern, tokens[id]))];
                    _expansions[leaf] = expansion;
                    ids.AddRange(expansion);
                    break;
                default:
                    break;
            }
        });
        return ids;
    }

    public bool Matches(OqlNode node, DocumentFacts doc, EvaluationMode mode) => node switch
    {
        OqlAnd and => and.Children.All(c => Matches(c, doc, mode)),
        OqlOr or => or.Children.Any(c => Matches(c, doc, mode)),
        OqlNot not => !Matches(not.Child, doc, mode),
        OqlField field => FieldMatches(field.Name, field.Child, doc),
        OqlProximity p => mode == EvaluationMode.Text ? TextProximity(p, doc) : PlantedProximity(p, doc),
        _ => mode == EvaluationMode.Text ? TextLeaf(node, doc) : PlantedLeaf(node, doc),
    };

    private static void Visit(OqlNode node, bool inField, Action<OqlNode> defaultFieldLeaf)
    {
        switch (node)
        {
            case OqlAnd and:
                and.Children.ToList().ForEach(c => Visit(c, inField, defaultFieldLeaf));
                break;
            case OqlOr or:
                or.Children.ToList().ForEach(c => Visit(c, inField, defaultFieldLeaf));
                break;
            case OqlNot not:
                Visit(not.Child, inField, defaultFieldLeaf);
                break;
            case OqlProximity p:
                Visit(p.Left, inField, defaultFieldLeaf);
                Visit(p.Right, inField, defaultFieldLeaf);
                break;
            case OqlField f:
                Visit(f.Child, inField: true, defaultFieldLeaf);
                break;
            default:
                if (!inField)
                {
                    defaultFieldLeaf(node);
                }

                break;
        }
    }

    private static bool PlantedLeaf(OqlNode node, DocumentFacts doc)
    {
        string term = node is OqlTerm t ? t.Text : throw new InvalidOperationException($"'{node}' is not a planted term.");
        GeneratedDocument d = doc.Document;
        return d.Content.Needles.Any(n => n.Term == term)
            || d.Content.ProximityHits.Any(h => h.First == term || h.Second == term);
    }

    private static bool PlantedProximity(OqlProximity p, DocumentFacts doc)
    {
        if (p is not { Left: OqlTerm left, Right: OqlTerm right })
        {
            throw new InvalidOperationException($"'{p}' is not a planted proximity pair.");
        }

        return doc.Document.Content.ProximityHits.Any(h =>
            ((h.First == left.Text && h.Second == right.Text) || (h.First == right.Text && h.Second == left.Text)) && h.Distance <= p.Distance);
    }

    private bool TextLeaf(OqlNode node, DocumentFacts doc)
    {
        IReadOnlyDictionary<int, List<int>> positions = doc.Positions ?? throw new InvalidOperationException("Text was not synthesised for this document.");
        if (node is OqlWildcard)
        {
            return _expansions[node].Any(positions.ContainsKey);
        }

        return StartPositions(node, positions).Any();
    }

    private bool TextProximity(OqlProximity p, DocumentFacts doc)
    {
        IReadOnlyDictionary<int, List<int>> positions = doc.Positions ?? throw new InvalidOperationException("Text was not synthesised for this document.");
        var left = OperandPositions(p.Left, positions).Order().ToList();
        if (left.Count == 0)
        {
            return false;
        }

        foreach (int b in OperandPositions(p.Right, positions))
        {
            int i = left.BinarySearch(b - p.Distance);
            i = i < 0 ? ~i : i;
            for (; i < left.Count && left[i] <= b + p.Distance; i++)
            {
                if (left[i] != b)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private IEnumerable<int> OperandPositions(OqlNode operand, IReadOnlyDictionary<int, List<int>> positions) => operand switch
    {
        OqlOr or => or.Children.SelectMany(c => OperandPositions(c, positions)),
        OqlWildcard => _expansions[operand].SelectMany(id => positions.TryGetValue(id, out List<int>? list) ? list : []),
        _ => StartPositions(operand, positions),
    };

    /// <summary>Positions where the term's (or phrase's) token sequence starts.</summary>
    private IEnumerable<int> StartPositions(OqlNode node, IReadOnlyDictionary<int, List<int>> positions)
    {
        int[] sequence = _sequences[node];
        if (sequence.Length == 0 || sequence.Any(id => id < 0) || !positions.TryGetValue(sequence[0], out List<int>? first))
        {
            yield break;
        }

        foreach (int start in first)
        {
            bool all = true;
            for (int k = 1; k < sequence.Length && all; k++)
            {
                all = positions.TryGetValue(sequence[k], out List<int>? next) && next.BinarySearch(start + k) >= 0;
            }

            if (all)
            {
                yield return start;
            }
        }
    }

    private bool FieldMatches(string name, OqlNode value, DocumentFacts doc)
    {
        switch (value)
        {
            case OqlOr or:
                return or.Children.Any(c => FieldMatches(name, c, doc));
            case OqlAnd and:
                return and.Children.All(c => FieldMatches(name, c, doc));
            case OqlNot not:
                return !FieldMatches(name, not.Child, doc);
            default:
                break;
        }

        GeneratedDocument d = doc.Document;
        return name switch
        {
            "custodian" => KeywordEquals(value, d.Custodian),
            "extension" => KeywordEquals(value, doc.Field(FieldCatalog.FileExtension)),
            "confidentiality" => KeywordEquals(value, doc.Field(FieldCatalog.Confidentiality)),
            "language" => KeywordEquals(value, doc.Field(FieldCatalog.Language)),
            "projectcode" => KeywordEquals(value, doc.Field(FieldCatalog.ProjectCode)),
            "filename" => value is OqlWildcard w
                ? doc.Field(FieldCatalog.FileName) is { } fileName && Glob.IsMatch(w.Pattern.ToLowerInvariant(), fileName.ToLowerInvariant())
                : throw new InvalidOperationException("filename supports wildcard filters only in the benchmark evaluator."),
            "date" => DateMatches(value, doc.DocumentDate),
            "controlnumber" => value is OqlRange r
                ? (r.Lower is null || string.CompareOrdinal(d.ControlNumber, r.Lower) >= 0) && (r.Upper is null || string.CompareOrdinal(d.ControlNumber, r.Upper) <= 0)
                : KeywordEquals(value, d.ControlNumber),
            CodingFixture.ResponsivenessField => KeywordEquals(value, doc.Coding.Responsiveness),
            CodingFixture.PrivilegeField => KeywordEquals(value, doc.Coding.Privilege),
            CodingFixture.IssuesField => doc.Coding.Issues.Any(i => KeywordEquals(value, i)),
            CodingFixture.ReviewPriorityField => NumberMatches(value, doc.Coding.ReviewPriority),
            _ => throw new InvalidOperationException($"Field '{name}' is not known to the benchmark evaluator (coding fields: {string.Join(", ", fixture.QueryNames)})."),
        };
    }

    private static bool KeywordEquals(OqlNode value, string? actual) => actual is not null && value switch
    {
        OqlTerm t => string.Equals(t.Text, actual, StringComparison.OrdinalIgnoreCase),
        OqlPhrase p => string.Equals(p.Text, actual, StringComparison.OrdinalIgnoreCase),
        _ => throw new InvalidOperationException($"Unsupported keyword filter value '{value}'."),
    };

    private static bool DateMatches(OqlNode value, DateOnly? date)
    {
        if (date is not { } d)
        {
            return false;
        }

        return value switch
        {
            OqlRange r => (r.Lower is null || d >= ParseDate(r.Lower)) && (r.Upper is null || d <= ParseDate(r.Upper)),
            OqlTerm t => d == ParseDate(t.Text),
            _ => throw new InvalidOperationException($"Unsupported date filter '{value}'."),
        };
    }

    private static bool NumberMatches(OqlNode value, int actual) => value switch
    {
        OqlRange r => (r.Lower is null || actual >= int.Parse(r.Lower, CultureInfo.InvariantCulture)) && (r.Upper is null || actual <= int.Parse(r.Upper, CultureInfo.InvariantCulture)),
        OqlTerm t => actual == int.Parse(t.Text, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"Unsupported number filter '{value}'."),
    };

    private static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Whole-value glob with <c>*</c> and <c>?</c> (ADR-008 R7 wildcards).</summary>
internal static class Glob
{
    public static bool IsMatch(string pattern, string value)
    {
        int p = 0, v = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v]))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
