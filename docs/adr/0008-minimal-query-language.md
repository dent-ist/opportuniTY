# ADR-008: Query language — minimal subset for M1

| Field | Value |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-10-02 |
| **Owner (role)** | Search (OpenSearch) |
| **Deciders** | Lead architect; contributing: eDiscovery Practitioner, UI/UX, Security & Compliance |
| **Tracking issue** | #31 (plan key `E02-T05`) |
| **Baseline sections** | [§9](../architecture/architecture-baseline.md#9-opportunity-query-language), [§33](../architecture/architecture-baseline.md#33-items-safe-to-defer-past-the-1m-benchmark), §22, §23, §28, §29 |
| **Related** | ADR-006 (single search path), ADR-007 (fields, analyzers, capabilities), ADR-009 (dates), ADR-004b; Q-12, Q-28, Q-30, Q-32; review findings §5.3, §9.12, §11.3 |

## Context

§9 forbids exposing raw OpenSearch DSL and requires `User Query → Parser → AST → Search Planner → OpenSearch`, with
Boolean, phrase, proximity, fields, ranges, wildcards, escaping, saved searches, family/duplicate expansion and
term-hit reporting. §33 allows a minimal AST subset at first, and Q-30 puts **`W/n` in M1** (`PRE/n` and `!` later,
`E07-T12`). The §29 benchmark mix is 10% proximity/wildcard, so the subset must cover both. Security requires
query-complexity limits (wildcard and proximity DoS) and a workspace clause the query cannot remove. The UI needs
positioned errors for the query bar. Search term reports are exchanged with opposing counsel, so semantics must be
precise and reproducible. Silent truncation of a wildcard expansion is unacceptable.

## Decision

### 1. Grammar (EBNF, M1 subset)

```ebnf
query       = ws , [ or_expr ] , ws ;                       (* blank query = match all (browse) *)
or_expr     = and_expr , { ws1 , "OR" , ws1 , and_expr } ;
and_expr    = prox_expr , { ( ws1 , "AND" , ws1 | ws1 ) , prox_expr } ;  (* juxtaposition = implicit AND *)
prox_expr   = unary , [ ws1 , prox_op , ws1 , unary ] ;    (* non-associative in M1 *)
prox_op     = ( "W" | "w" ) , "/" , digit , { digit } ;
unary       = "NOT" , ws1 , unary | primary ;
primary     = group | field_expr | phrase | term ;
group       = "(" , ws , or_expr , ws , ")" ;
field_expr  = field_name , ":" , ( group | phrase | range | exists | term ) ;
exists      = "*" ;
range       = ( "[" | "{" ) , ws , bound , ws1 , "TO" , ws1 , bound , ws , ( "]" | "}" ) ;
bound       = "*" | phrase | term ;
phrase      = '"' , { phrase_char | "\" , any_char } , '"' ;
term        = term_start , { term_char | "\" , any_char } ;   (* may contain * and ? (wildcards) *)
field_name  = letter , { letter | digit | "_" | "." | "-" } ;
term_start  = any_char - ( reserved | ws_char | "-" | "+" ) ;
term_char   = any_char - ( reserved | ws_char ) ;
reserved    = "(" | ")" | '"' | ":" | "[" | "]" | "{" | "}" | "\" | "!" | "~" | "^" ;
```

Disambiguation rules:

1. **R1** `AND`, `OR`, `NOT` and `TO` are operators **only in UPPER CASE**. Lower-case `and`/`or`/`not` are search
   terms, and the parser returns a positioned **warning** ("did you mean AND?"). `W/n` is case-insensitive (`w/5`),
   because `w/5` is never a meaningful term. Lexer rule: a whole unquoted token matching `[Ww]/[0-9]+` is the
   proximity operator (escape it, `W\/5`, to search it literally). After `field:`, a lone `*` is `exists`; `*` followed
   by other characters (`*.xlsx`) is a wildcard term.
2. **R2** Precedence, highest first: grouping → `field:` → `NOT` → `W/n` → `AND` (explicit or implicit) → `OR`.
   `AND` and `OR` are left-associative. `W/n` is non-associative: `a W/3 b W/5 c` is an error in M1.
3. **R3** The default operator between adjacent terms is **AND**. (dtSearch treats adjacent words as a phrase; users
   quote phrases. The UI echoes the normalized interpretation, e.g. `contract AND termination`.)
4. **R4** Escaping: `\` escapes any following character in a term (`AT\&T`, `10\:00`, `\(draft\)`). Inside a phrase
   only `\"` and `\\` are meaningful. A leading `-` or `+` is reserved (error suggesting `NOT`); inside a term,
   `-` is literal (`e-mail`). `PRE/n`, `!`, `~`, `^`, `W/s` and `W/p` give `UNSUPPORTED_SYNTAX` with a span.
5. **R5** A blank query means "all documents in scope" (grid browsing). A purely negative query (`NOT privileged`) is
   allowed: it means all documents except the matches.

### 2. Semantics

| Construct | Meaning | OpenSearch translation (planner) |
|---|---|---|
| `term` (default field) | Analyzed with `opp_text_search` on `text`. If analysis gives several tokens (`e-mail`, `john.smith`), it is a phrase of those tokens. Zero tokens → error | `match` / `match_phrase` |
| `"phrase"` | Exact token sequence, slop 0, case- and accent-insensitive | `match_phrase` |
| `a W/n b` | `a` and `b` within **n** words of each other, **either order**: at most n−1 intervening words. `1 ≤ n ≤ 1000` | `span_near { slop: n−1, in_order: false }` |
| `field:value` | Resolved by field capability (ADR-007): full-text → match; keyword → `term` on normalized value; Choice → name→ChoiceId; User → email/name→UserId; Boolean → `true/false/yes/no/y/n`; number → exact | per type |
| `field:(…)` | Applies the field to every leaf in the group | per leaf |
| `field:*` | Field has a value | `exists` |
| `field:[a TO b]` / `{a TO b}` | Inclusive / exclusive bounds, mixable (`[a TO b}`), `*` = open | `range` |
| Wildcards `*`, `?` | `*` = zero or more characters, `?` = exactly one, within a single term | `wildcard`, `case_insensitive: true` |

6. **R6** **Proximity operands** are a term, a wildcard term, a phrase, or a parenthesized `OR` of those
   (`(apple OR pear) W/5 iphone` → `span_or`). Both operands must target the same field (default `text`, or a common
   `field:(a W/3 b)`). `AND`, `NOT`, ranges and nested proximity inside an operand are M1 errors (`E07-T12`). A
   multi-token term inside proximity becomes an ordered `span_near` with slop 0.
7. **R7** **Wildcards.**
   - On `text` and other full-text fields: at least **3** literal characters before the first wildcard
     (`Search:MinWildcardPrefix`); leading or early wildcards are rejected (`LEADING_WILDCARD`).
   - On keyword fields: trailing wildcards with ≥ 1 literal prefix character.
   - Leading or infix wildcards are allowed only on fields with `leadingWildcard` capability (`filename`, via
     `fileName.wc`).
   - Wildcards inside phrases are M1 errors (deferred).
   - Outside proximity: `rewrite: constant_score`, which never truncates.
   - Inside proximity: `span_multi` with `rewrite: constant_score_boolean`. If the expansion exceeds
     `indices.query.bool.max_clause_count` (configured to 4,096 = `Search:MaxWildcardExpansion`), OpenSearch errors.
     The planner turns that error into `400 WILDCARD_TOO_BROAD` with the term's span. **Expansions are never silently
     truncated**, so `top_terms_N` rewrites are forbidden.
8. **R8** **Dates** (ADR-009 R27). Literals are `YYYY-MM-DD`, `YYYY-MM`, `YYYY` (whole day, month or year) or ISO 8601
   with an offset. On DateTime fields, date-only bounds use the executing user's effective display zone: the lower
   bound is 00:00, and an inclusive upper bound runs to the end of that day. On Date-precision fields, bounds compare
   calendar dates with no zone. `date:2025-03-01` means that whole day. The zone used is part of the bound query and is
   stored with saved searches, snapshots and search term reports.
9. **R9** Numbers use invariant notation (`.` decimal separator, no thousands separators). Keyword ranges are allowed
   only on fields with `rangeable` capability (`controlnumber`, `begbates`, `endbates`, compared by natural sort key).
10. **R10** **Case rules:** terms, phrases and keyword values are case- and accent-insensitive (ADR-007 analyzers and
    normalizer). Field names and choice names are case-insensitive. Operators are case-sensitive (R1).

### 3. Field resolution

11. **R11** Fields are addressed by `queryName` (ADR-007 R8). The default is generated from the display name
    (lower-case, non-alphanumerics → `_`, collisions suffixed `_2`) and is editable by admins. Structural fields use the
    names in ADR-007 §3 (`date` = `documentDate`). An unknown field is `400 UNKNOWN_FIELD` with suggestions, never a
    silent match-none. `workspaceId`, `securityTags`, `documentId` and `projectionVersion` are not addressable.
12. **R12** The default field for bare terms is `text` only in M1. Workspace-configurable default field sets come later.

### 4. Pipeline and AST

```text
text ──Parse──► AST (syntax only) ──Bind(workspace fields, user zone)──► BoundAST ──Plan──► DSL
                 │ positioned errors          │ type/capability errors        │ wrapped by ISearchService (ADR-006 R7)
```

13. **R13** AST node kinds (JSON canonical form, `astVersion: 1`); every node carries `span {start, end}` in UTF-16
    code units of the original text:
    `Or{children}`, `And{children}`, `Not{child}`, `Proximity{left, right, distance, ordered=false}`,
    `Field{name, child}`, `Term{text}`, `Phrase{text}`, `Wildcard{pattern}`, `Range{lower?, upper?}` (bound =
    `{value, inclusive}`), `Exists`, `MatchAll`.
14. **R14** Extension points, reserved now so later features are additive: `Proximity.ordered` (`PRE/n`),
    `Term.rootExpand` (`!`), nested proximity (relaxing the validator), `Fuzzy` and `Regex` kinds. The planner rejects
    node kinds or flags it does not know (`UNSUPPORTED_SYNTAX`). An AST JSON schema change bumps `astVersion`.
15. **R15** Parse is pure and workspace-independent, and backs the `validate` endpoint the query bar uses. Bind
    resolves fields, choices, users and the zone against the workspace. Plan emits only the user subtree; the workspace
    and security filters are added by `ISearchService` outside it (ADR-006 R7). Raw DSL is never accepted from clients.
16. **R16** Saved searches persist the original text, `astVersion`, bound field ids and zone. They are re-parsed and
    re-bound at execution with the **executing** user's permissions (`E07-T09`). When a field's `queryName` changes,
    stored text is regenerated from the AST by the canonical printer.

### 5. Limits (configurable; violations → `400` with code and span)

| Limit | Default |
|---|---|
| Query length | 10,000 characters |
| AST nodes | 1,024 |
| Nesting depth | 32 |
| Proximity distance `n` | 1–1,000 |
| Wildcard expansion (per term) | 4,096 terms |
| Minimum literal prefix before wildcard (full-text fields) | 3 |
| Server-side timeout | 30 s, then `QUERY_TIMEOUT` (never partial results presented as complete) |

### 6. Term-hit reporting

17. **R17** The planner returns, with every plan, the list of **hit units**: each `Term`, `Phrase`, `Wildcard` leaf
    and each `Proximity` node as a whole (operands of a proximity node are not separate units, since alone they
    over-count). Each unit has an id, a source span and a display string. Hit units drive (a) the highlight query for
    snippets, (b) per-unit counts in search term reports (`E07-T10`, exact counts with `track_total_hits: true`,
    executed against a materialized snapshot per Q-30), and (c) the viewer hit list. M1 delivers the hit-unit
    extraction; the report itself is M3.

### 7. §9 coverage

| §9 item / example | M1 | Notes |
|---|---|---|
| `contract AND termination` | Supported | |
| `"trade secret"` | Supported | |
| `apple W/10 iphone` | Supported | Unordered, ≤ 9 intervening words (Q-30) |
| `custodian:"John Smith"` | Supported | `custodian` is a system Keyword field; exact, case-insensitive |
| `date:[2025-01-01 TO 2025-12-31]` | Supported | `date` = `documentDate`; whole days in the user's zone (R8) |
| `filename:*.xlsx` | Supported | Leading wildcard via `fileName.wc` |
| Boolean, grouping, phrase, fields, ranges, escaping | Supported | |
| Wildcards | Supported with R7 policy | Leading wildcards on full text: deferred |
| Saved searches | M3 (`E07-T09`) | AST/text persistence rules fixed here (R16) |
| Family/duplicate/thread expansion | M3 (`E09-T03`) | Search **options**, not syntax |
| Term-hit reporting | Hit units M1; reports M3 (`E07-T10`) | |
| `PRE/n`, `!`, nested proximity, `NOT W/n`, wildcards in phrases, fuzzy, regex, boosting, sentence/paragraph proximity | Deferred (`E07-T12`) | Behind R14 extension points |

### 8. Golden-file contract tests

18. **R18** One golden case per file in the search test project (`QueryLanguage/Golden/*.yaml`): `query`, `zone`,
    field fixture → expected canonical `ast` JSON, expected canonical `dsl` JSON (workspace id as placeholder),
    optional expected `hits` on the known-answer corpus (`E17-T09`), or expected error `code` + `span`. The suite
    covers every row of §2, every rule R1–R10, every limit and every §9 example.
19. **R19** Golden files change only via an explicit update run and are reviewed by the Search owner. A DSL change for
    unchanged AST is a planner behaviour change and must be called out in the PR, because it can change search term
    report counts. Fuzzing (`E07-T06`, 100K inputs) asserts no crash and bounded parse time. A property test asserts
    the workspace filter survives every generated AST (`E07-T05`).

### 9. Saved-search references (amendment, E07-T09 #71)

A saved search is used as a criterion with the reserved field name `savedsearch` (case-insensitive) and the saved
search's ID as the value, in GUID D or N form, optionally quoted: `savedsearch:0199a8a0-0000-7000-8000-000000000001
AND NOT custodian:smith`. The grammar is unchanged (it is an ordinary `field_expr`); no workspace field may use the
query name `savedsearch` (such a field would be shadowed; reserving the name in field creation is a follow-up).

- **Expansion** happens after parsing and before binding: each reference is replaced by the referenced search's query,
  **re-parsed now** (never a stored AST or result set), recursively. The inlined nodes take the span of the reference,
  so binding errors inside it (e.g. a deleted field: `UNKNOWN_FIELD`) point at the reference and their message names
  the saved search. The AST returned by validation and audited with the run is the text as written.
- **Placement:** only at Boolean level (`AND`, `OR`, `NOT`, grouping); inside `W/n`, another field, or with a range,
  wildcard or `*` value it is `SAVED_SEARCH_INVALID_REFERENCE`.
- **Visibility:** references written by the caller must name a saved search the caller can see (own, shared with them
  or one of their groups, any as Workspace Admin), else `SAVED_SEARCH_NOT_FOUND` (same answer as a missing one).
  References stored inside a saved search are part of that search's criteria and resolve within the workspace when it
  runs. Either way the results are filtered for the runner (ADR-015 D5.8: candidate sets, never grants).
- **Cycles and depth:** a reference back to a search already on the expansion path, or to the search being saved, is
  `SAVED_SEARCH_CYCLE` (checked on save and again at every run); more than 8 levels is `SAVED_SEARCH_TOO_DEEP`; a
  nested search that no longer parses is `SAVED_SEARCH_INVALID`. All are positioned 400 errors like R15.

## Consequences

- **Positive:** a small, precisely specified language that covers the §29 mix and every §9 example. Reproducible and
  explainable counts (no silent truncation, recorded zone). Positioned errors for the UI. Later operators are additive.
- **Negative / costs:** upper-case-only Boolean operators and the 3-character wildcard prefix will surprise some
  dtSearch users (mitigated by warnings and the echoed interpretation). No stemming or root expansion until `!` ships.
  `constant_score` wildcards may be slow on very broad prefixes; the timeout bounds them.
- **Follow-up work:** `E07-T06` (parser), `E07-T07` (binder/planner), `E07-T09`, `E07-T10`, `E07-T12`, `E16` query bar.
- **Verification:** golden suite (R18–R19), fuzzing, workspace-filter property test, `apple W/10 iphone` correctness on
  the golden corpus (`E07-T07`), and benchmark query mix built from this grammar (`E17-T04`).

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Expose OpenSearch `query_string` | Forbidden by §9; leaks DSL features (regex, fuzzy, field globbing) and cannot guarantee the workspace clause or limits. |
| Implicit OR (Lucene default) | Inflates hit counts; legal users expect narrowing. |
| Case-insensitive `and`/`or`/`not` operators | Makes searching for those words impossible without quoting; warnings give the same help without ambiguity. |
| `top_terms_N` wildcard rewrite in proximity | Silently drops expansions: indefensible in search term reports. |
| Full dtSearch syntax in M1 | §33 allows a subset; Q-30 schedules `PRE/n` and `!` after MVP. |

## Baseline amendments

- *Proposed:* §9 — add the M1 subset definition (this ADR §1–§2), the `W/n` semantics (unordered, ≤ n−1 intervening
  words) and the "no silent wildcard truncation" rule.

## Links

- Baseline: §9, §22, §23, §28, §29, §33
- Review findings: [review-findings.md](../plan/review-findings.md) §5.3, §9.12, §11.3
- Decisions: [decisions.md](../plan/decisions.md) Q-12, Q-28, Q-30, Q-32
- Related ADRs: [ADR-006](0006-opensearch-index-strategy.md), [ADR-007](0007-search-mapping-strategy.md), [ADR-009](0009-family-dedupe-thread-identity-and-dates.md)
