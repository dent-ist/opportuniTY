using System.Globalization;

using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Randomness;
using Opportunity.DataGenerator.Corpus.Text;

namespace Opportunity.Benchmarks.Workloads;

public sealed record QuerySetOptions
{
    public ulong QuerySeed { get; init; } = 1;

    public int QueryCount { get; init; } = 1_000;

    /// <summary>Documents whose text is synthesised to count text queries; the whole corpus (exact counts) when it is not larger.</summary>
    public int TextSampleDocuments { get; init; } = 5_000;

    public int MinScheduleLength { get; init; } = 10_000;

    /// <summary>Candidates generated per query kept; selection then spreads each class over selectivity bands.</summary>
    public int CandidateFactor { get; init; } = 4;

    public int MinSampleHits { get; init; } = 3;
}

/// <summary>
/// Builds the versioned query set from a corpus: terms, phrases, metadata values, planted needles and proximity pairs
/// all come from the seeded corpus, every query is counted (exactly, from ground truth, or on a text sample), tagged with
/// its taxonomy class, gate class and selectivity band, and the replay schedule is a seeded permutation. Same corpus +
/// options = byte-identical output.
/// </summary>
public static class QuerySetGenerator
{
    // Vocabulary ranks below this are English function words (Vocabulary.FunctionWords); never used as query terms.
    private const int FirstContentRank = 75;
    private const ulong SampleSalt = 0x51A3_9E7C_0D15_C0DEUL;
    private const ulong ScheduleSalt = 0x5C4E_D01E_0000_0000UL;
    private const int MaxRounds = 6;

    public static QuerySet Generate(QueryCorpus corpus, QuerySetOptions options)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.QueryCount, QueryTaxonomy.Classes.Count);
        return new Run(corpus, options).Execute();
    }

    /// <summary>
    /// Counts hand-written queries the way generated ones are counted (exact for metadata, ground truth for planted
    /// terms, the text sample otherwise; expansion applied). Field names must be ones the evaluator knows.
    /// </summary>
    public static IReadOnlyList<long> CountHits(QueryCorpus corpus, IReadOnlyList<(OqlNode Query, SearchOptions Options)> queries, QuerySetOptions options)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(options);
        return new Run(corpus, options).Count(queries);
    }

    private sealed record Candidate(string ClassId, OqlNode Query, SearchOptions Options, int Order)
    {
        public string Key { get; } = OqlPrinter.Print(Query) + "|" + Options.Expand + "|" + string.Join(",", Options.Sort.Select(s => s.Field + " " + s.Direction)) + "|" + string.Join(",", Options.Facets);

        public EvaluationMode Mode { get; set; }

        public long Hits { get; set; }

        public HashSet<string>? DuplicateGroups { get; set; }
    }

    private sealed class Run
    {
        private readonly QueryCorpus _corpus;
        private readonly QuerySetOptions _options;
        private readonly CodingFixture _fixture;
        private readonly QueryEvaluator _evaluator;
        private readonly TokenTable _tokens = new();
        private readonly List<long> _documentFrequency = [];
        private readonly List<GeneratedDocument> _sample = [];
        private readonly List<int[]> _sampleTokens = [];
        private readonly List<string> _phrases = [];
        private readonly Dictionary<string, long> _custodians = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _extensions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _confidentiality = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _languages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _projectCodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _fileNameTokens = new(StringComparer.Ordinal);
        private readonly SortedDictionary<int, long> _months = [];
        private readonly Dictionary<string, long> _needleDocs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _vocabularyRank = new(StringComparer.Ordinal);
        private readonly HashSet<string> _plantedWords = new(StringComparer.Ordinal);
        private long _pairNearDocs;
        private long _pairFarDocs;
        private List<List<string>> _termDecades = [];
        private List<List<string>> _phraseDecades = [];

        public Run(QueryCorpus corpus, QuerySetOptions options)
        {
            _corpus = corpus;
            _options = options;
            _fixture = new CodingFixture { Seed = corpus.Identity.Seed ^ 0xC0D1_F1C5_0000_0001UL };
            foreach (var needle in corpus.Context.Profile.Needles)
            {
                _plantedWords.Add(needle.Term);
            }

            foreach (var pair in corpus.Context.Profile.ProximityPairs)
            {
                _plantedWords.Add(pair.First);
                _plantedWords.Add(pair.Second);
            }

            for (int rank = 0; rank < corpus.Context.Vocabulary.Count; rank++)
            {
                _vocabularyRank.TryAdd(corpus.Context.Vocabulary[rank], rank);
            }

            _evaluator = new QueryEvaluator(_fixture, _plantedWords);
        }

        private bool Exact => _options.TextSampleDocuments >= _corpus.DocumentCount;

        private int MinTextHits => Exact ? 1 : _options.MinSampleHits;

        private long SampleCount => _sample.Count;

        public IReadOnlyList<long> Count(IReadOnlyList<(OqlNode Query, SearchOptions Options)> queries)
        {
            ScanCorpus();
            ScanSampleText();
            var candidates = queries.Select((q, i) => new Candidate("adhoc", q.Query, q.Options, i) { Mode = _evaluator.ModeOf(q.Query) }).ToList();
            if (candidates.FirstOrDefault(c => c.Mode == EvaluationMode.Text && c.Options.Expand is not null) is { } unsupported)
            {
                throw new ArgumentException($"'{unsupported.Query}': expansion is counted only for metadata and planted-term queries.", nameof(queries));
            }

            CountExact(candidates.Where(c => c.Mode != EvaluationMode.Text).ToList());
            CountText(candidates.Where(c => c.Mode == EvaluationMode.Text).ToList());
            return [.. candidates.Select(ExpectedHits)];
        }

        public QuerySet Execute()
        {
            ScanCorpus();
            ScanSampleText();
            BuildTermDecades();

            IReadOnlyDictionary<string, int> allocation = QueryTaxonomy.Allocate(_options.QueryCount);
            var candidates = new List<Candidate>();
            var rngs = QueryTaxonomy.Classes.Select((_, c) => new Rng(StableHash.Combine(_options.QuerySeed, (ulong)c + 1))).ToList();
            var seen = QueryTaxonomy.Classes.Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToList();
            int order = 0;

            // Rounds: a class short of usable candidates (e.g. phrases with too few sample hits) draws more; deterministic.
            for (int round = 0; round < MaxRounds; round++)
            {
                var fresh = new List<Candidate>();
                for (int c = 0; c < QueryTaxonomy.Classes.Count; c++)
                {
                    QueryClassDefinition definition = QueryTaxonomy.Classes[c];
                    int missing = allocation[definition.Id] - candidates.Count(x => x.ClassId == definition.Id && Usable(x));
                    if (missing <= 0)
                    {
                        continue;
                    }

                    int wanted = (missing * _options.CandidateFactor * (round + 1)) + 8;
                    int added = 0;
                    for (int attempt = 0; attempt < wanted * 3 && added < wanted; attempt++)
                    {
                        if (Build(definition.Id, rngs[c]) is not var (query, searchOptions))
                        {
                            continue;
                        }

                        var candidate = new Candidate(definition.Id, query, searchOptions, order++);
                        if (seen[c].Add(candidate.Key))
                        {
                            candidate.Mode = _evaluator.ModeOf(query);
                            fresh.Add(candidate);
                            added++;
                        }
                    }
                }

                if (fresh.Count == 0)
                {
                    break;
                }

                CountExact(fresh.Where(c => c.Mode != EvaluationMode.Text).ToList());
                CountText(fresh.Where(c => c.Mode == EvaluationMode.Text).ToList());
                candidates.AddRange(fresh);
            }

            var queries = new List<BenchmarkQuery>();
            var classes = new List<QuerySetClass>();
            IReadOnlySet<string> codingFields = _fixture.QueryNames;
            foreach (QueryClassDefinition definition in QueryTaxonomy.Classes)
            {
                int count = allocation[definition.Id];
                List<Candidate> chosen = Select(candidates.Where(c => c.ClassId == definition.Id && Usable(c)).ToList(), count);
                if (chosen.Count < count)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"Query class '{definition.Id}': only {chosen.Count} usable queries of {count} on this corpus ({_corpus.DocumentCount} documents). Use a larger corpus or fewer queries."));
                }

                foreach (Candidate c in chosen)
                {
                    GateClass gate = QueryClassifier.Classify(c.Query, c.Options, codingFields);
                    if (gate != definition.Gate)
                    {
                        throw new InvalidOperationException($"Generated '{c.Query}' for class {definition.Id} classifies as {gate.Name()}, not {definition.Gate.Name()}.");
                    }

                    queries.Add(ToQuery(c, definition, queries.Count + 1));
                }

                classes.Add(new QuerySetClass(definition.Id, definition.Gate.Name(), definition.MixBucket, definition.Share, count, definition.Description));
            }

            return new QuerySet
            {
                QuerySeed = _options.QuerySeed,
                Corpus = _corpus.Identity,
                Evaluation = new QueryEvaluation
                {
                    TextMode = Exact ? "exact" : "sampled",
                    TextSampleDocuments = SampleCount,
                    MinSampleHits = MinTextHits,
                },
                CodingFixture = _fixture,
                Classes = classes,
                Queries = queries,
                Schedule = Schedule(queries.Count),
            };
        }

        private bool Usable(Candidate c) => c.Mode == EvaluationMode.Text ? c.Hits >= MinTextHits : c.Hits >= 1;

        private BenchmarkQuery ToQuery(Candidate c, QueryClassDefinition definition, int number)
        {
            long expected = ExpectedHits(c);
            double fraction = Math.Round((double)expected / _corpus.DocumentCount, 8);
            return new BenchmarkQuery
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"q{number:D5}"),
                Class = definition.Id,
                Gate = definition.Gate.Name(),
                Bucket = definition.MixBucket,
                Oql = OqlPrinter.Print(c.Query),
                Expand = c.Options.Expand,
                Sort = c.Options.Sort,
                Facets = c.Options.Facets,
                ExpectedHits = expected,
                HitFraction = fraction,
                Selectivity = QueryTaxonomy.Band((double)expected / _corpus.DocumentCount).Name(),
                HitSource = c.Mode switch
                {
                    EvaluationMode.Metadata => "exact",
                    EvaluationMode.Planted => "ground-truth",
                    _ => Exact ? "exact" : "sampled",
                },
                SampleHits = c.Mode == EvaluationMode.Text && !Exact ? c.Hits : null,
            };
        }

        private long ExpectedHits(Candidate c) => c.Mode == EvaluationMode.Text && !Exact
            ? (long)Math.Round((double)c.Hits * _corpus.DocumentCount / SampleCount, MidpointRounding.ToEven)
            : c.Hits;

        /// <summary>Round-robin over selectivity bands so each class spans the bands the corpus offers.</summary>
        private List<Candidate> Select(List<Candidate> usable, int count)
        {
            var byBand = usable
                .GroupBy(c => QueryTaxonomy.Band((double)ExpectedHits(c) / _corpus.DocumentCount))
                .ToDictionary(g => g.Key, g => new Queue<Candidate>(g.OrderBy(c => c.Order)));
            SelectivityBand[] order = [SelectivityBand.Narrow, SelectivityBand.Medium, SelectivityBand.Broad, SelectivityBand.Needle, SelectivityBand.VeryBroad];
            var chosen = new List<Candidate>(count);
            while (chosen.Count < count && byBand.Values.Any(q => q.Count > 0))
            {
                foreach (SelectivityBand band in order)
                {
                    if (chosen.Count < count && byBand.TryGetValue(band, out Queue<Candidate>? queue) && queue.Count > 0)
                    {
                        chosen.Add(queue.Dequeue());
                    }
                }
            }

            return [.. chosen.OrderBy(c => c.Order)];
        }

        private List<int> Schedule(int count)
        {
            int epochs = Math.Max(1, (_options.MinScheduleLength + count - 1) / count);
            var schedule = new List<int>(epochs * count);
            for (int e = 0; e < epochs; e++)
            {
                var permutation = Enumerable.Range(0, count).ToList();
                new Rng(StableHash.Combine(_options.QuerySeed, ScheduleSalt + (ulong)e)).Shuffle(permutation);
                schedule.AddRange(permutation);
            }

            return schedule;
        }

        // ---- corpus scans --------------------------------------------------------------------------------------

        private void ScanCorpus()
        {
            ulong threshold = Exact ? ulong.MaxValue : (ulong)((double)_options.TextSampleDocuments / _corpus.DocumentCount * ulong.MaxValue);
            foreach (GeneratedFamily family in _corpus.Families())
            {
                foreach (GeneratedDocument doc in family.Documents)
                {
                    var facts = new DocumentFacts(doc, _fixture);
                    Increment(_custodians, doc.Custodian);
                    Increment(_extensions, facts.Field(FieldCatalog.FileExtension));
                    Increment(_confidentiality, facts.Field(FieldCatalog.Confidentiality));
                    Increment(_languages, facts.Field(FieldCatalog.Language));
                    Increment(_projectCodes, facts.Field(FieldCatalog.ProjectCode));
                    if (facts.DocumentDate is { } date)
                    {
                        _months[(date.Year * 100) + date.Month] = _months.GetValueOrDefault((date.Year * 100) + date.Month) + 1;
                    }

                    if (facts.Field(FieldCatalog.FileName) is { } fileName)
                    {
                        foreach (string token in TextTokenizer.Tokens(fileName).Distinct(StringComparer.Ordinal))
                        {
                            if (token.Length >= 4 && token.All(char.IsAsciiLetterLower))
                            {
                                Increment(_fileNameTokens, token);
                            }
                        }
                    }

                    foreach (string term in doc.Content.Needles.Select(n => n.Term).Distinct(StringComparer.Ordinal))
                    {
                        Increment(_needleDocs, term);
                    }

                    _pairNearDocs += doc.Content.ProximityHits.Any(h => h.IsNear) ? 1 : 0;
                    _pairFarDocs += doc.Content.ProximityHits.Any(h => !h.IsNear) ? 1 : 0;

                    if (Exact || StableHash.Combine(_options.QuerySeed ^ SampleSalt, (ulong)doc.DocIndex) < threshold)
                    {
                        _sample.Add(doc);
                    }
                }
            }
        }

        /// <summary>Synthesises each sampled document's text once, keeping its token ids for counting.</summary>
        private void ScanSampleText()
        {
            var synthesizer = new TextSynthesizer(_corpus.Context.Vocabulary);
            var text = new CappedText(QueryTaxonomy.IndexedTextCapChars);
            var ids = new List<int>();
            var seen = new HashSet<int>();
            foreach (GeneratedDocument doc in _sample)
            {
                text.Clear();
                synthesizer.Write(doc.Content.Text, text);
                ids.Clear();
                seen.Clear();
                TextTokenizer.Tokenize(text.Span, token => ids.Add(_tokens.GetOrAdd(token)));
                while (_documentFrequency.Count < _tokens.Count)
                {
                    _documentFrequency.Add(0);
                }

                foreach (int id in ids)
                {
                    if (seen.Add(id))
                    {
                        _documentFrequency[id]++;
                    }
                }

                _sampleTokens.Add([.. ids]);
                CollectPhrases(doc, ids);
            }
        }

        private void CollectPhrases(GeneratedDocument doc, List<int> ids)
        {
            var rng = new Rng(StableHash.Combine(_options.QuerySeed ^ 0x9A4A_5E00UL, (ulong)doc.DocIndex));
            for (int w = 0; w < 4 && ids.Count >= 8; w++)
            {
                int length = rng.NextInt(2, 3);
                int start = rng.NextInt(ids.Count - length);
                var words = new string[length];
                bool ok = true;
                for (int k = 0; k < length && ok; k++)
                {
                    words[k] = _tokens[ids[start + k]];
                    // Function words may lead or sit inside ("the draft agreement"); the last word carries the content.
                    ok = _vocabularyRank.TryGetValue(words[k], out int rank) && words[k].All(char.IsAsciiLetterLower)
                        && (rank >= FirstContentRank || k < length - 1);
                }

                if (ok)
                {
                    _phrases.Add(string.Join(' ', words));
                }
            }
        }

        /// <summary>Content words grouped by decade of sample document frequency, starting at the minimum usable count.</summary>
        private void BuildTermDecades()
        {
            var words = Enumerable.Range(0, _tokens.Count)
                .Where(id => _documentFrequency[id] >= MinTextHits
                    && _vocabularyRank.TryGetValue(_tokens[id], out int rank) && rank >= FirstContentRank
                    && _tokens[id].Length >= 4 && _tokens[id].All(char.IsAsciiLetterLower))
                .Select(id => (Word: _tokens[id], Df: _documentFrequency[id]))
                .OrderBy(w => w.Df).ThenBy(w => w.Word, StringComparer.Ordinal)
                .ToList();
            double lower = MinTextHits;
            _termDecades = [];
            while (lower <= SampleCount)
            {
                double upper = lower * 10;
                _termDecades.Add([.. words.Where(w => w.Df >= lower && w.Df < upper).Select(w => w.Word)]);
                lower = upper;
            }

            // Phrases by the decade of their rarest word: a phrase of common words is far more likely to be broad.
            var phrases = _phrases.Distinct(StringComparer.Ordinal)
                .Select(p => (Phrase: p, Df: p.Split(' ').Min(w => _documentFrequency[_tokens.Find(w)])))
                .ToList();
            _phraseDecades = [];
            for (double low = 1; low <= SampleCount; low *= 10)
            {
                double high = low * 10;
                _phraseDecades.Add([.. phrases.Where(p => p.Df >= low && p.Df < high).Select(p => p.Phrase)]);
            }
        }

        private void CountExact(List<Candidate> candidates)
        {
            if (candidates.Count == 0)
            {
                return;
            }

            foreach (Candidate c in candidates.Where(c => c.Options.Expand == "duplicates"))
            {
                c.DuplicateGroups = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (GeneratedFamily family in _corpus.Families())
            {
                DocumentFacts[] facts = [.. family.Documents.Select(d => new DocumentFacts(d, _fixture))];
                foreach (Candidate c in candidates)
                {
                    int matches = 0;
                    foreach (DocumentFacts doc in facts)
                    {
                        if (_evaluator.Matches(c.Query, doc, c.Mode))
                        {
                            matches++;
                            c.DuplicateGroups?.Add(doc.Document.DuplicateGroupId ?? doc.Document.ControlNumber);
                        }
                    }

                    if (c.DuplicateGroups is null)
                    {
                        c.Hits += c.Options.Expand == "family" && matches > 0 ? facts.Length : matches;
                    }
                }
            }

            List<Candidate> duplicates = [.. candidates.Where(c => c.DuplicateGroups is not null)];
            if (duplicates.Count == 0)
            {
                return;
            }

            foreach (GeneratedDocument doc in _corpus.Families().SelectMany(f => f.Documents))
            {
                string group = doc.DuplicateGroupId ?? doc.ControlNumber;
                foreach (Candidate c in duplicates)
                {
                    if (c.DuplicateGroups!.Contains(group))
                    {
                        c.Hits++;
                    }
                }
            }
        }

        private void CountText(List<Candidate> candidates)
        {
            if (candidates.Count == 0)
            {
                return;
            }

            var referenced = new HashSet<int>();
            foreach (Candidate c in candidates)
            {
                referenced.UnionWith(_evaluator.Bind(c.Query, _tokens));
            }

            var isReferenced = new bool[_tokens.Count];
            foreach (int id in referenced)
            {
                isReferenced[id] = true;
            }

            var positions = new Dictionary<int, List<int>>();
            for (int d = 0; d < _sample.Count; d++)
            {
                GeneratedDocument doc = _sample[d];
                positions.Clear();
                int[] tokens = _sampleTokens[d];
                for (int position = 0; position < tokens.Length; position++)
                {
                    int id = tokens[position];
                    if (isReferenced[id])
                    {
                        if (!positions.TryGetValue(id, out List<int>? list))
                        {
                            positions[id] = list = [];
                        }

                        list.Add(position);
                    }
                }

                var facts = new DocumentFacts(doc, _fixture) { Positions = positions };
                foreach (Candidate c in candidates)
                {
                    if (_evaluator.Matches(c.Query, facts, EvaluationMode.Text))
                    {
                        c.Hits++;
                    }
                }
            }
        }

        // ---- query templates -----------------------------------------------------------------------------------

        private (OqlNode Query, SearchOptions Options)? Build(string classId, Rng rng) => classId switch
        {
            "term" => Plain(TermQuery(rng)),
            "phrase" => Plain(Phrase(rng, 0)),
            "field-filter" => Plain(FieldFilterQuery(rng)),
            "date-range" => Plain(DateRangeQuery(rng)),
            "boolean" => Plain(BooleanQuery(rng)),
            "content-coding" => Plain(ContentCodingQuery(rng)),
            "expansion" => ExpansionQuery(rng),
            "grid-coding" => GridCodingQuery(rng),
            "proximity" => Plain(ProximityQuery(rng)),
            "wildcard" => Plain(WildcardQuery(rng)),
            _ => throw new ArgumentException($"Unknown class {classId}.", nameof(classId)),
        };

        private static (OqlNode, SearchOptions)? Plain(OqlNode? node) => node is null ? null : (node, SearchOptions.Default);

        private OqlNode? TermQuery(Rng rng)
        {
            var needles = Needles();
            if (needles.Count > 0 && rng.Chance(0.15))
            {
                return new OqlTerm(rng.Pick(needles));
            }

            return rng.Chance(0.5)
                ? Term(rng, 0)
                : (OqlNode?)Conjunction(rng.Chance(0.5), Term(rng, 1), Term(rng, 1));
        }

        private OqlAnd? FieldFilterQuery(Rng rng)
        {
            OqlNode? content = rng.Chance(0.4)
                ? Phrase(rng, 3)
                : rng.Chance(0.5) ? Term(rng, 1) : (OqlNode?)Conjunction(false, Term(rng, 1), Term(rng, 1));
            var filters = KeywordFilters(rng, rng.NextInt(1, 2));
            return content is null || filters.Count == 0 ? null : new OqlAnd([content, .. filters], Explicit: true);
        }

        private OqlAnd? DateRangeQuery(Rng rng)
        {
            OqlNode? content = rng.Chance(0.5) ? Term(rng, 1) : (OqlNode?)Conjunction(false, Term(rng, 1), Term(rng, 1));
            OqlNode? range = DateRange(rng);
            if (content is null || range is null)
            {
                return null;
            }

            List<OqlNode> parts = [content, range];
            if (rng.Chance(0.4))
            {
                parts.AddRange(KeywordFilters(rng, 1));
            }

            return new OqlAnd(parts, Explicit: true);
        }

        private OqlNode? BooleanQuery(Rng rng)
        {
            OqlNode? a = Term(rng, 2), b = Term(rng, 2), c = Term(rng, 2), d = Term(rng, 2);
            if (a is null || b is null || c is null || d is null || Distinct(a, b, c, d) < 4)
            {
                return null;
            }

            return rng.NextInt(4) switch
            {
                0 => new OqlAnd([new OqlOr([a, b]), new OqlOr([c, d])]),
                1 => new OqlAnd([new OqlOr([a, b, c]), new OqlNot(d)]),
                2 => new OqlAnd([a, new OqlOr([b, new OqlAnd([c, d])])]),
                _ => new OqlOr([new OqlAnd([a, b]), new OqlAnd([c, d])]),
            };
        }

        private OqlAnd? ContentCodingQuery(Rng rng)
        {
            OqlNode? content = rng.NextInt(3) switch
            {
                0 => Term(rng, 1),
                1 => Term(rng, 1) is { } x && Term(rng, 1) is { } y && Distinct(x, y) == 2 ? new OqlOr([x, y]) : null,
                _ => Phrase(rng, 3),
            };
            if (content is null)
            {
                return null;
            }

            List<OqlNode> parts = [content, CodingFilter(rng)];
            if (rng.Chance(0.35) && DateRange(rng) is { } range)
            {
                parts.Add(range);
            }

            return new OqlAnd(parts);
        }

        private (OqlNode, SearchOptions)? ExpansionQuery(Rng rng)
        {
            var needles = Needles();
            OqlNode? query;
            if (needles.Count > 0 && rng.Chance(0.5))
            {
                string first = rng.Pick(needles);
                string second = rng.Pick(needles);
                query = first == second ? new OqlTerm(first) : new OqlOr([new OqlTerm(first), new OqlTerm(second)]);
            }
            else
            {
                var filters = new List<OqlNode> { Filter("custodian", WeightedPick(rng, _custodians)) };
                filters.AddRange(KeywordFilters(rng, 1, exclude: "custodian"));
                query = filters.Count == 2 ? new OqlAnd(filters) : null;
            }

            return query is null ? null : (query, new SearchOptions { Expand = rng.Chance(0.6) ? "family" : "duplicates" });
        }

        private (OqlNode, SearchOptions)? GridCodingQuery(Rng rng)
        {
            OqlNode? query = rng.NextInt(3) switch
            {
                0 => Filter("custodian", WeightedPick(rng, _custodians)),
                1 => Term(rng, 3),
                _ => KeywordFilters(rng, 2, exclude: "custodian") is { Count: 2 } f ? new OqlAnd(f) : null,
            };
            if (query is null)
            {
                return null;
            }

            string[] facetable = [CodingFixture.ResponsivenessField, CodingFixture.PrivilegeField, CodingFixture.IssuesField];
            bool sort = rng.Chance(0.7);
            var facets = facetable.Where(_ => rng.Chance(0.5)).ToList();
            if (!sort && facets.Count == 0)
            {
                facets.Add(rng.Pick(facetable));
            }

            return (query, new SearchOptions
            {
                Sort = sort ? [new SortKey(CodingFixture.ReviewPriorityField, rng.Chance(0.5) ? "desc" : "asc")] : [],
                Facets = facets,
            });
        }

        private OqlProximity? ProximityQuery(Rng rng)
        {
            var pairs = _corpus.Context.Profile.ProximityPairs;
            var pair = pairs.Count > 0 ? pairs[0] : null;
            if (pair is not null && (_pairNearDocs > 0 || _pairFarDocs > 0) && rng.Chance(0.3))
            {
                int distance = _pairNearDocs > 0 && rng.Chance(0.5) ? pair.NearDistance + 2 : pair.FarDistance;
                return new OqlProximity(new OqlTerm(pair.First), new OqlTerm(pair.Second), distance);
            }

            int[] distances = [2, 3, 5, 10, 20];
            int n = distances[rng.NextInt(distances.Length)];
            OqlNode? a = Term(rng, 4), b = Term(rng, 4), c = Term(rng, 4);
            if (a is null || b is null || c is null || Distinct(a, b, c) < 3)
            {
                return null;
            }

            return rng.Chance(0.6) ? new OqlProximity(a, b, n) : new OqlProximity(new OqlOr([a, b]), c, n);
        }

        private OqlNode? WildcardQuery(Rng rng)
        {
            switch (rng.NextInt(4))
            {
                case 0:
                    return Prefix(rng);
                case 1:
                    return Prefix(rng) is { } prefix && Term(rng, 1) is { } term ? new OqlAnd([prefix, term]) : null;
                case 2:
                    string? extension = WeightedPick(rng, _extensions);
                    return extension is null ? null : new OqlField("filename", new OqlWildcard("*." + extension));
                default:
                    string? token = WeightedPick(rng, _fileNameTokens);
                    return token is null ? null : new OqlField("filename", new OqlWildcard("*" + token + "*"));
            }
        }

        private OqlWildcard? Prefix(Rng rng)
        {
            if (Term(rng, 2) is not OqlTerm { Text: var word } || word.Length < 5)
            {
                return null;
            }

            int literal = rng.NextInt(3, Math.Min(5, word.Length - 1));
            return new OqlWildcard(word[..literal] + "*");
        }

        // ---- building blocks -----------------------------------------------------------------------------------

        // Profiles: sample document-frequency decades each template draws from (0 = any, larger = commoner words).
        private static readonly int[][] DecadeProfiles = [[0, 1, 2, 3, 4, 5], [1, 2, 3, 4, 5], [0, 1, 2, 3], [2, 3, 4, 5], [2, 3, 4, 5]];

        private OqlTerm? Term(Rng rng, int profile)
        {
            var decades = DecadeProfiles[profile].Where(d => d < _termDecades.Count && _termDecades[d].Count > 0).ToList();
            if (decades.Count == 0)
            {
                decades = [.. Enumerable.Range(0, _termDecades.Count).Where(d => _termDecades[d].Count > 0)];
            }

            return decades.Count == 0 ? null : new OqlTerm(rng.Pick(_termDecades[rng.Pick(decades)]));
        }

        private OqlPhrase? Phrase(Rng rng, int profile)
        {
            var decades = DecadeProfiles[profile].Where(d => d < _phraseDecades.Count && _phraseDecades[d].Count > 0).ToList();
            if (decades.Count == 0)
            {
                decades = [.. Enumerable.Range(0, _phraseDecades.Count).Where(d => _phraseDecades[d].Count > 0)];
            }

            return decades.Count == 0 ? null : new OqlPhrase(rng.Pick(_phraseDecades[rng.Pick(decades)]));
        }

        private static OqlAnd? Conjunction(bool explicitAnd, OqlTerm? a, OqlTerm? b) =>
            a is null || b is null || a.Text == b.Text ? null : new OqlAnd([a, b], explicitAnd);

        private static int Distinct(params OqlNode[] nodes) => nodes.Select(OqlPrinter.Print).Distinct(StringComparer.Ordinal).Count();

        private List<string> Needles() => [.. _needleDocs.Where(n => n.Value > 0).Select(n => n.Key).Order(StringComparer.Ordinal)];

        private List<OqlNode> KeywordFilters(Rng rng, int count, string? exclude = null)
        {
            var available = new (string Field, Dictionary<string, long> Values)[]
            {
                ("custodian", _custodians), ("extension", _extensions), ("confidentiality", _confidentiality), ("language", _languages), ("projectcode", _projectCodes),
            }.Where(f => f.Field != exclude && f.Values.Count > 0).ToList();
            var filters = new List<OqlNode>();
            while (filters.Count < count && available.Count > 0)
            {
                var (field, values) = available[rng.NextInt(available.Count)];
                available.RemoveAll(f => f.Field == field);
                filters.Add(Filter(field, WeightedPick(rng, values)));
            }

            return filters;
        }

        private static OqlField Filter(string field, string? value) => new(field, new OqlTerm(value ?? throw new InvalidOperationException($"No values for {field}.")));

        private OqlField CodingFilter(Rng rng)
        {
            CodingFieldDefinition Field(string name) => _fixture.Field(name);
            switch (rng.NextInt(3))
            {
                case 0:
                    return new OqlField(CodingFixture.ResponsivenessField, new OqlTerm(rng.Pick(Field(CodingFixture.ResponsivenessField).Choices).Name));
                case 1:
                    return new OqlField(CodingFixture.PrivilegeField, new OqlTerm(rng.Pick(Field(CodingFixture.PrivilegeField).Choices).Name));
                default:
                    var issues = Field(CodingFixture.IssuesField).Choices;
                    string first = rng.Pick(issues).Name, second = rng.Pick(issues).Name;
                    return first == second
                        ? new OqlField(CodingFixture.IssuesField, new OqlTerm(first))
                        : new OqlField(CodingFixture.IssuesField, new OqlOr([new OqlTerm(first), new OqlTerm(second)]));
            }
        }

        private OqlField? DateRange(Rng rng)
        {
            if (_months.Count == 0)
            {
                return null;
            }

            var months = _months.Keys.ToList();
            int start = months[rng.NextInt(months.Count)];
            int[] spans = [1, 3, 6, 12];
            int span = spans[rng.NextInt(spans.Length)];
            var from = new DateOnly(start / 100, start % 100, 1);
            DateOnly to = from.AddMonths(span).AddDays(-1);
            return new OqlField("date", new OqlRange(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        /// <summary>Picks a value with probability proportional to its document count (deterministic order).</summary>
        private static string? WeightedPick(Rng rng, Dictionary<string, long> counts)
        {
            if (counts.Count == 0)
            {
                return null;
            }

            var ordered = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            long total = ordered.Sum(kv => kv.Value);
            long target = rng.NextInt64(total);
            foreach (var (value, count) in ordered)
            {
                if (target < count)
                {
                    return value;
                }

                target -= count;
            }

            return ordered[^1].Key;
        }

        private static void Increment(Dictionary<string, long> counts, string? key)
        {
            if (!string.IsNullOrEmpty(key))
            {
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
    }

    /// <summary>Collects synthesised text up to the indexed-text cap (ADR-007 R9); the rest is discarded.</summary>
    private sealed class CappedText(int cap) : ITextSink
    {
        private char[] _buffer = new char[1 << 16];
        private int _length;

        public ReadOnlySpan<char> Span => _buffer.AsSpan(0, _length);

        public void Clear() => _length = 0;

        public void Write(ReadOnlySpan<char> chars)
        {
            int take = Math.Min(chars.Length, cap - _length);
            if (take <= 0)
            {
                return;
            }

            if (_length + take > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Min(cap, Math.Max(_buffer.Length * 2, _length + take)));
            }

            chars[..take].CopyTo(_buffer.AsSpan(_length));
            _length += take;
        }
    }
}
