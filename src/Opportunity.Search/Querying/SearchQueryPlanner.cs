using System.Globalization;
using System.Text.Json.Nodes;

using Opportunity.Core.Fields;
using Opportunity.Core.QueryLanguage;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Querying;

/// <summary>
/// The search planner (ADR-008 §2–§5, E07-T07): binds the AST against the workspace's field catalogue and plans the
/// user clause. Every field resolves by query name to a projection path with the catalogue's capabilities; the
/// field type decides the query (full text → <c>match</c>/<c>match_phrase</c>, keyword → <c>term</c>, numbers and
/// dates → <c>term</c>/<c>range</c>, choices by name → ChoiceId); <c>W/n</c> becomes <c>span_near</c> over the
/// analyzed tokens; wildcards follow R7 (literal prefix, leading only with the <c>LeadingWildcard</c> capability,
/// never truncated); cost limits apply after expansion. Every problem is a positioned diagnostic, never a silent
/// match-none. The result only ever lands in the service's <c>bool.must</c> (ADR-006 R7).
/// </summary>
internal sealed class SearchQueryPlanner(ISearchFieldCatalogSource catalogs, ISearchTextAnalyzer analyzer) : ISearchQueryTranslator
{
    public async ValueTask<SearchTranslation> TranslateAsync(
        QueryNode ast, SearchTranslationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(context);
        var catalog = await catalogs.GetAsync(context.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var plan = new Plan(SearchFieldResolver.Create(catalog, context.ProjectionGeneration, context.FieldOptions), context);

        plan.Collect(ast, null);
        foreach (var ((path, mode), texts) in plan.Pending)
        {
            var list = texts.ToList();
            var analyzed = await analyzer.AnalyzeAsync(context.ProjectionGeneration, path, mode, list, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < list.Count; i++)
            {
                plan.Analyzed[(path, mode, list[i])] = analyzed[i];
            }
        }

        var query = plan.Emit(ast, null);
        if (plan.Clauses > context.Limits.MaxClauses)
        {
            plan.Errors.Add(new QueryDiagnostic(SearchQueryErrorCodes.TooManyClauses,
                $"The query expands to {plan.Clauses} clauses; at most {context.Limits.MaxClauses} are allowed. Narrow it or split it.",
                ast.Span));
        }

        if (plan.Errors.Count > 0 || query is null)
        {
            return SearchTranslation.Failed([.. plan.Errors.OrderBy(e => e.Span.Start).ThenBy(e => e.Span.End)]);
        }

        return SearchTranslation.Ok(query, plan.Classify(ast)) with { BoundedExpansions = plan.Bounded };
    }

    private sealed class Plan(SearchFieldResolver fields, SearchTranslationContext context)
    {
        private readonly TimeZoneInfo _zone = context.Zone ?? TimeZoneInfo.Utc;
        private int _proximityWildcards;

        public List<QueryDiagnostic> Errors { get; } = [];

        public Dictionary<(string Path, AnalysisMode Mode), HashSet<string>> Pending { get; } = [];

        public Dictionary<(string Path, AnalysisMode Mode, string Text), IReadOnlyList<string>> Analyzed { get; } = [];

        public List<SourceSpan> Bounded { get; } = [];

        public int Clauses { get; private set; }

        private QueryLimits Limits => context.Limits;

        /// <summary>First pass: the texts that need the field's analyzer, so analysis is one request per path.</summary>
        public void Collect(QueryNode node, IReadOnlyList<SearchTarget>? targets)
        {
            switch (node)
            {
                case AndNode and:
                    and.Children.ToList().ForEach(c => Collect(c, targets));
                    break;
                case OrNode or:
                    or.Children.ToList().ForEach(c => Collect(c, targets));
                    break;
                case NotNode not:
                    Collect(not.Child, targets);
                    break;
                case FieldNode field:
                    if (fields.Resolve(field.Name, out var resolved) == FieldResolution.Resolved)
                    {
                        Collect(field.Child, resolved);
                    }

                    break;
                case ProximityNode p:
                    Collect(p.Left, targets);
                    Collect(p.Right, targets);
                    break;
                case TermNode or PhraseNode or WildcardNode:
                    foreach (var target in targets ?? [SearchFieldResolver.Text])
                    {
                        if (target.Kind == SearchValueKind.FullText)
                        {
                            var (path, mode, text) = AnalysisKey(node, target);
                            if (!Pending.TryGetValue((path, mode), out var set))
                            {
                                Pending[(path, mode)] = set = new HashSet<string>(StringComparer.Ordinal);
                            }

                            set.Add(text);
                        }
                    }

                    break;
            }
        }

        public JsonObject? Emit(QueryNode node, IReadOnlyList<SearchTarget>? targets)
        {
            switch (node)
            {
                case MatchAllNode:
                    return Obj(("match_all", new JsonObject()));
                case AndNode and:
                    return Bool("must", and.Children, targets);
                case OrNode or:
                    var should = Bool("should", or.Children, targets);
                    if (should is not null)
                    {
                        ((JsonObject)should["bool"]!)["minimum_should_match"] = 1;
                    }

                    return should;
                case NotNode not:
                    return Bool("must_not", [not.Child], targets);
                case FieldNode field:
                    return Field(field);
                case ProximityNode p:
                    return Each(targets ?? [SearchFieldResolver.Text], t => Proximity(p, t));
                case TermNode or PhraseNode or WildcardNode or RangeNode or ExistsNode:
                    return Each(targets ?? [SearchFieldResolver.Text], t => Leaf(node, t));
                default:
                    Errors.Add(new QueryDiagnostic(QueryErrorCodes.UnsupportedSyntax, $"{node.Kind} queries are not supported yet.", node.Span));
                    return null;
            }
        }

        /// <summary>The §29 gate class (docs/benchmarks/query-taxonomy.md, QueryClassifier): simple or complex.</summary>
        public string Classify(QueryNode ast)
        {
            if (ast is MatchAllNode)
            {
                return SearchTranslation.Simple;
            }

            int terms = 0, phrases = 0, filters = 0;
            foreach (var node in Conjuncts(ast))
            {
                switch (node)
                {
                    case TermNode:
                        terms++;
                        break;
                    case PhraseNode:
                        phrases++;
                        break;
                    case FieldNode { Child: TermNode or PhraseNode or RangeNode } field
                        when fields.Resolve(field.Name, out var targets) == FieldResolution.Resolved && !targets.Any(t => t.IsCoding):
                        filters++;
                        break;
                    default:
                        return SearchTranslation.Complex;
                }
            }

            var content = (phrases == 0 && terms <= 2) || (phrases == 1 && terms == 0);
            return content && filters <= 2 ? SearchTranslation.Simple : SearchTranslation.Complex;
        }

        private static IEnumerable<QueryNode> Conjuncts(QueryNode node) => node is AndNode and ? and.Children.SelectMany(Conjuncts) : [node];

        private JsonObject? Bool(string occur, IEnumerable<QueryNode> children, IReadOnlyList<SearchTarget>? targets)
        {
            var clauses = new JsonArray();
            var failed = false;
            foreach (var child in children)
            {
                if (Emit(child, targets) is { } clause)
                {
                    clauses.Add(clause);
                }
                else
                {
                    failed = true;
                }
            }

            return failed ? null : Obj(("bool", Obj((occur, clauses))));
        }

        private JsonObject? Field(FieldNode field)
        {
            var nameSpan = new SourceSpan(field.Span.Start, field.Span.Start + field.Name.Length);
            switch (fields.Resolve(field.Name, out var targets))
            {
                case FieldResolution.Unknown:
                    var suggestions = fields.Suggest(field.Name);
                    Errors.Add(new QueryDiagnostic(SearchQueryErrorCodes.UnknownField,
                        suggestions.Count > 0
                            ? $"Unknown field '{field.Name}'. Did you mean {string.Join(" or ", suggestions.Select(s => s + ":"))}?"
                            : $"Unknown field '{field.Name}'.",
                        nameSpan,
                        suggestions));
                    return null;
                case FieldResolution.NotSearchable:
                    Errors.Add(new QueryDiagnostic(SearchQueryErrorCodes.UnsupportedForField,
                        $"Field '{field.Name}' is not searchable.", nameSpan));
                    return null;
                default:
                    return Emit(field.Child, targets);
            }
        }

        /// <summary>One clause per target; several targets (custodian expansion) are OR-ed.</summary>
        private static JsonObject? Each(IReadOnlyList<SearchTarget> targets, Func<SearchTarget, JsonObject?> clause)
        {
            if (targets.Count == 1)
            {
                return clause(targets[0]);
            }

            var any = new JsonArray();
            foreach (var target in targets)
            {
                if (clause(target) is not { } c)
                {
                    return null;
                }

                any.Add(c);
            }

            return Obj(("bool", Obj(("should", any), ("minimum_should_match", 1))));
        }

        private JsonObject? Leaf(QueryNode node, SearchTarget target)
        {
            switch (node)
            {
                case ExistsNode:
                    return Require(target, FieldCapabilities.Exists, "Exists checks (field:*)", node.Span)
                        ? Count(Obj(("exists", Obj(("field", target.Path)))))
                        : null;
                case TermNode term:
                    return Value(target, term.Text, node, phrase: false);
                case PhraseNode phrase:
                    return Value(target, phrase.Text, node, phrase: true);
                case WildcardNode wildcard:
                    return Wildcard(wildcard, target);
                case RangeNode range:
                    return Range(range, target);
                default:
                    Errors.Add(new QueryDiagnostic(QueryErrorCodes.UnsupportedSyntax, $"{node.Kind} queries are not supported yet.", node.Span));
                    return null;
            }
        }

        private JsonObject? Value(SearchTarget target, string text, QueryNode node, bool phrase)
        {
            if (!target.Has(FieldCapabilities.Filterable) && !target.Has(FieldCapabilities.FullText))
            {
                Errors.Add(Unsupported(target, "Searching", node.Span));
                return null;
            }

            switch (target.Kind)
            {
                case SearchValueKind.FullText:
                    var tokens = Tokens(node, target);
                    if (tokens.Count == 0)
                    {
                        Errors.Add(NoTokens(node));
                        return null;
                    }

                    return Count(phrase || tokens.Count > 1
                        ? Obj(("match_phrase", Obj((target.Path, Obj(("query", text))))))
                        : Obj(("match", Obj((target.Path, Obj(("query", text)))))));
                case SearchValueKind.Keyword:
                    return Count(Term(target.Path, text, caseInsensitive: true));
                case SearchValueKind.Choice:
                    return Choices(target, text, node.Span, overflow: false);
                case SearchValueKind.User:
                    if (!Guid.TryParse(text, out var user))
                    {
                        Errors.Add(Invalid(target, text, "a user id (UUID); user names are not resolved yet", node.Span));
                        return null;
                    }

                    return Count(Term(target.Path, user.ToString("D")));
                case SearchValueKind.Overflow:
                    return Overflow(target, text, node.Span);
                case SearchValueKind.Date:
                    if (!DateLiteral.TryParse(text, target.CalendarDate ? TimeZoneInfo.Utc : _zone, target.CalendarDate, out var date))
                    {
                        Errors.Add(Invalid(target, text, "a date (YYYY, YYYY-MM, YYYY-MM-DD or ISO 8601)", node.Span));
                        return null;
                    }

                    return Count(date.IsInstant
                        ? Term(target.Path, DateLiteral.Format(date.Start))
                        : Obj(("range", Obj((target.Path, Obj(("gte", DateLiteral.Format(date.Start)), ("lt", DateLiteral.Format(date.End))))))));
                default:
                    return Scalar(target, text, node.Span) is { } value ? Count(Term(target.Path, value)) : null;
            }
        }

        /// <summary>Integer, decimal and boolean values in invariant notation (R9).</summary>
        private JsonNode? Scalar(SearchTarget target, string text, SourceSpan span)
        {
            switch (target.Kind)
            {
                case SearchValueKind.Integer when SearchValues.TryInteger(text, out var integer):
                    return integer;
                case SearchValueKind.Decimal when SearchValues.TryDecimal(text, out var number):
                    return number;
                case SearchValueKind.Boolean when SearchValues.TryBoolean(text, out var flag):
                    return flag;
                case SearchValueKind.Integer:
                    Errors.Add(Invalid(target, text, "a whole number", span));
                    return null;
                case SearchValueKind.Decimal:
                    Errors.Add(Invalid(target, text, "a number (use . as the decimal separator)", span));
                    return null;
                case SearchValueKind.Boolean:
                    Errors.Add(Invalid(target, text, "true or false (yes/no, y/n)", span));
                    return null;
                default:
                    Errors.Add(Unsupported(target, "This value", span));
                    return null;
            }
        }

        /// <summary>Choice names → ChoiceIds (R10: case-insensitive); an unknown name is an error, never a match-none.</summary>
        private JsonObject? Choices(SearchTarget target, string text, SourceSpan span, bool overflow)
        {
            var choices = fields.Catalog.ChoicesOf(target.Definition!.FieldId);
            var ids = choices.Where(c => string.Equals(c.Name.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(c => (JsonNode)c.ChoiceId.ToString(CultureInfo.InvariantCulture))
                .ToList();
            if (ids.Count == 0)
            {
                Errors.Add(new QueryDiagnostic(SearchQueryErrorCodes.InvalidFieldValue,
                    $"Field '{target.Name}' has no choice named '{text}'.", span,
                    [.. choices.Where(c => c.IsActive).Take(10).Select(c => c.Name)]));
                return null;
            }

            Clauses += ids.Count;
            return ids.Count == 1
                ? Term(target.Path, ids[0]!.GetValue<string>(), caseInsensitive: false, overflow)
                : Obj(("terms", Obj((target.Path, new JsonArray([.. ids])))));
        }

        /// <summary>Overflow keys hold every value as its canonical string (ADR-007 R6): exact match only.</summary>
        private JsonObject? Overflow(SearchTarget target, string text, SourceSpan span)
        {
            var definition = target.Definition!;
            switch (definition.Type)
            {
                case FieldType.SingleChoice or FieldType.MultiChoice:
                    return Choices(target, text, span, overflow: true);
                case FieldType.Integer when SearchValues.TryInteger(text, out var integer):
                    return Count(Term(target.Path, integer.ToString(CultureInfo.InvariantCulture)));
                case FieldType.Boolean when SearchValues.TryBoolean(text, out var flag):
                    return Count(Term(target.Path, flag ? "true" : "false"));
                case FieldType.Integer or FieldType.Boolean or FieldType.Decimal or FieldType.Date:
                    Errors.Add(Unsupported(target, "Searching this overflow value", span));
                    return null;
                default:
                    return Count(Term(target.Path, text));
            }
        }

        private JsonObject? Wildcard(WildcardNode wildcard, SearchTarget target)
        {
            if (target.Kind is not (SearchValueKind.FullText or SearchValueKind.Keyword or SearchValueKind.Overflow))
            {
                Errors.Add(Unsupported(target, "Wildcards", wildcard.Span));
                return null;
            }

            if (!Require(target, FieldCapabilities.Wildcard, "Wildcards", wildcard.Span))
            {
                return null;
            }

            // Fields with the LeadingWildcard capability match the whole value (fileName.wc): `*.xlsx`, `report*`.
            if (target.Has(FieldCapabilities.LeadingWildcard) && target.WholeValuePath is { } whole)
            {
                return Count(WildcardQuery(whole, wildcard.Pattern));
            }

            var minimum = target.Kind == SearchValueKind.FullText ? Limits.MinWildcardPrefix : 1;
            if (wildcard.LiteralPrefixLength < minimum)
            {
                Errors.Add(LeadingWildcard(target, minimum, wildcard.Span));
                return null;
            }

            var pattern = target.Kind == SearchValueKind.FullText ? Normalized(wildcard, target) : wildcard.Pattern;
            return Count(WildcardQuery(target.Path, pattern, caseInsensitive: target.Kind != SearchValueKind.Overflow));
        }

        private JsonObject? Range(RangeNode range, SearchTarget target)
        {
            if (!Require(target, FieldCapabilities.Rangeable, "Ranges", range.Span))
            {
                return null;
            }

            if (range.Lower is null && range.Upper is null)
            {
                return Count(Obj(("exists", Obj(("field", target.Path)))));
            }

            var bounds = new JsonObject();
            var path = target.Path;
            var ok = true;
            switch (target.Kind)
            {
                case SearchValueKind.Keyword:
                    path = target.SortKeyPath ?? target.Path;
                    if (range.Lower is { } kl)
                    {
                        bounds[kl.Inclusive ? "gte" : "gt"] = target.SortKeyPath is null ? kl.Value.ToLowerInvariant() : SearchValues.SortKey(kl.Value);
                    }

                    if (range.Upper is { } ku)
                    {
                        bounds[ku.Inclusive ? "lte" : "lt"] = target.SortKeyPath is null ? ku.Value.ToLowerInvariant() : SearchValues.SortKey(ku.Value);
                    }

                    break;
                case SearchValueKind.Date:
                    var zone = target.CalendarDate ? TimeZoneInfo.Utc : _zone;
                    if (range.Lower is { } dl)
                    {
                        if (DateLiteral.TryParse(dl.Value, zone, target.CalendarDate, out var lower))
                        {
                            // An exclusive whole-unit bound starts after the unit: {2025-01 TO …} is from February.
                            if (dl.Inclusive)
                            {
                                bounds["gte"] = DateLiteral.Format(lower.Start);
                            }
                            else if (lower.IsInstant)
                            {
                                bounds["gt"] = DateLiteral.Format(lower.Start);
                            }
                            else
                            {
                                bounds["gte"] = DateLiteral.Format(lower.End);
                            }
                        }
                        else
                        {
                            Errors.Add(Invalid(target, dl.Value, "a date (YYYY, YYYY-MM, YYYY-MM-DD or ISO 8601)", range.Span));
                            ok = false;
                        }
                    }

                    if (range.Upper is { } du)
                    {
                        if (DateLiteral.TryParse(du.Value, zone, target.CalendarDate, out var upper))
                        {
                            // An inclusive whole-unit bound runs to the end of the unit (R8).
                            if (!du.Inclusive)
                            {
                                bounds["lt"] = DateLiteral.Format(upper.Start);
                            }
                            else if (upper.IsInstant)
                            {
                                bounds["lte"] = DateLiteral.Format(upper.Start);
                            }
                            else
                            {
                                bounds["lt"] = DateLiteral.Format(upper.End);
                            }
                        }
                        else
                        {
                            Errors.Add(Invalid(target, du.Value, "a date (YYYY, YYYY-MM, YYYY-MM-DD or ISO 8601)", range.Span));
                            ok = false;
                        }
                    }

                    break;
                case SearchValueKind.Integer or SearchValueKind.Decimal:
                    if (range.Lower is { } nl && Scalar(target, nl.Value, range.Span) is { } lo)
                    {
                        bounds[nl.Inclusive ? "gte" : "gt"] = lo;
                    }
                    else if (range.Lower is not null)
                    {
                        ok = false;
                    }

                    if (range.Upper is { } nu && Scalar(target, nu.Value, range.Span) is { } hi)
                    {
                        bounds[nu.Inclusive ? "lte" : "lt"] = hi;
                    }
                    else if (range.Upper is not null)
                    {
                        ok = false;
                    }

                    break;
                default:
                    Errors.Add(Unsupported(target, "Ranges", range.Span));
                    return null;
            }

            return ok ? Count(Obj(("range", Obj((path, bounds))))) : null;
        }

        /// <summary><c>a W/n b</c>: both within n words, either order, at most n−1 words between (ADR-008 §2, R6).</summary>
        private JsonObject? Proximity(ProximityNode p, SearchTarget target)
        {
            if (target.Kind != SearchValueKind.FullText || !target.Has(FieldCapabilities.FullText))
            {
                Errors.Add(new QueryDiagnostic(SearchQueryErrorCodes.UnsupportedForField,
                    $"W/n needs a full-text field; '{target.Name}' is not one.", p.Span));
                return null;
            }

            var left = Span(p.Left, target);
            var right = Span(p.Right, target);
            return left is null || right is null
                ? null
                : Obj(("span_near", Obj(("clauses", new JsonArray(left, right)), ("slop", p.Distance - 1), ("in_order", p.Ordered))));
        }

        private JsonObject? Span(QueryNode operand, SearchTarget target)
        {
            switch (operand)
            {
                case TermNode or PhraseNode:
                    var tokens = Tokens(operand, target);
                    if (tokens.Count == 0)
                    {
                        Errors.Add(NoTokens(operand));
                        return null;
                    }

                    Clauses += tokens.Count;
                    var terms = tokens.Select(t => (JsonNode)Obj(("span_term", Obj((target.Path, t))))).ToArray();
                    return terms.Length == 1
                        ? (JsonObject)terms[0]
                        : Obj(("span_near", Obj(("clauses", new JsonArray(terms)), ("slop", 0), ("in_order", true))));
                case WildcardNode wildcard:
                    if (wildcard.LiteralPrefixLength < Limits.MinWildcardPrefix)
                    {
                        Errors.Add(LeadingWildcard(target, Limits.MinWildcardPrefix, wildcard.Span, inProximity: true));
                        return null;
                    }

                    if (++_proximityWildcards > Limits.MaxProximityWildcards)
                    {
                        Errors.Add(new QueryDiagnostic(QueryErrorCodes.TooManyWildcards,
                            $"At most {Limits.MaxProximityWildcards} wildcard terms may be used inside W/n.", wildcard.Span));
                        return null;
                    }

                    // constant_score_boolean: OpenSearch fails the search instead of dropping expansions (R7).
                    Bounded.Add(wildcard.Span);
                    Clauses++;
                    var multi = Obj((target.Path, Obj(("value", Normalized(wildcard, target)), ("rewrite", "constant_score_boolean"))));
                    return Obj(("span_multi", Obj(("match", Obj(("wildcard", multi))))));
                case OrNode or:
                    var clauses = new JsonArray();
                    foreach (var child in or.Children)
                    {
                        if (Span(child, target) is not { } span)
                        {
                            return null;
                        }

                        clauses.Add(span);
                    }

                    return Obj(("span_or", Obj(("clauses", clauses))));
                default:
                    Errors.Add(new QueryDiagnostic(QueryErrorCodes.InvalidProximityOperand,
                        "A W/n operand must be a term, phrase, wildcard or an OR of those.", operand.Span));
                    return null;
            }
        }

        private IReadOnlyList<string> Tokens(QueryNode node, SearchTarget target) =>
            Analyzed.TryGetValue(AnalysisKey(node, target), out var tokens) ? tokens : [];

        private string Normalized(WildcardNode wildcard, SearchTarget target) =>
            Analyzed.TryGetValue(AnalysisKey(wildcard, target), out var normalized) && normalized.Count == 1
                ? normalized[0]
                : wildcard.Pattern.ToLowerInvariant();

        private static (string Path, AnalysisMode Mode, string Text) AnalysisKey(QueryNode node, SearchTarget target) => node switch
        {
            TermNode t => (target.Path, AnalysisMode.Tokens, t.Text),
            PhraseNode p => (target.Path, AnalysisMode.Tokens, p.Text),
            WildcardNode w => (target.Path, AnalysisMode.Normalize, w.Pattern),
            _ => throw new ArgumentOutOfRangeException(nameof(node)),
        };

        private bool Require(SearchTarget target, FieldCapabilities capability, string what, SourceSpan span)
        {
            if (target.Has(capability))
            {
                return true;
            }

            Errors.Add(Unsupported(target, what, span));
            return false;
        }

        private JsonObject Count(JsonObject clause)
        {
            Clauses++;
            return clause;
        }

        private static JsonObject Term(string path, JsonNode value, bool caseInsensitive = false, bool overflow = false)
        {
            var term = Obj(("value", value));
            if (caseInsensitive && !overflow)
            {
                term["case_insensitive"] = true;
            }

            return Obj(("term", Obj((path, term))));
        }

        private static JsonObject WildcardQuery(string path, string pattern, bool caseInsensitive = true)
        {
            var body = Obj(("value", pattern));
            if (caseInsensitive)
            {
                body["case_insensitive"] = true;
            }

            body["rewrite"] = "constant_score";
            return Obj(("wildcard", Obj((path, body))));
        }

        private static QueryDiagnostic Unsupported(SearchTarget target, string what, SourceSpan span) =>
            new(SearchQueryErrorCodes.UnsupportedForField, $"{what} are not supported for field '{target.Name}'.", span);

        private static QueryDiagnostic Invalid(SearchTarget target, string text, string expected, SourceSpan span) =>
            new(SearchQueryErrorCodes.InvalidFieldValue, $"'{text}' is not valid for field '{target.Name}': expected {expected}.", span);

        private static QueryDiagnostic NoTokens(QueryNode node) =>
            new(SearchQueryErrorCodes.NoSearchableTerms, "This has no searchable words (only punctuation or symbols); quote or remove it.", node.Span);

        private static QueryDiagnostic LeadingWildcard(SearchTarget target, int minimum, SourceSpan span, bool inProximity = false) =>
            new(SearchQueryErrorCodes.LeadingWildcard,
                inProximity
                    ? $"A wildcard inside W/n needs at least {minimum} literal characters before the first * or ?."
                    : $"A wildcard on '{target.Name}' needs at least {minimum} literal character(s) before the first * or ?.",
                span);
    }
}
