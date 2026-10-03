# Benchmark query taxonomy and k6 workloads

| | |
|---|---|
| **Ticket** | E17-T04 (#142): query taxonomy and k6 workload scripts |
| **Binding** | Baseline §9, §26, §29; ADR-008 (query language), ADR-019 (API conventions); decisions Q-03, Q-04, Q-05, Q-30; [test strategy](../testing/test-strategy.md) §4–§5; [reference environments](reference-environments.md) |
| **Taxonomy version** | **1.0.0** (`QueryTaxonomy.Version`, recorded as `workload.taxonomyVersion` in every result bundle) |
| **Code** | `tools/Opportunity.Benchmarks/Workloads` (taxonomy, generator, evaluator, stub API, `ingest-k6`), `tools/Opportunity.Benchmarks/k6` (k6 scripts, `run.sh`) |
| **Tests** | `tests/Opportunity.Benchmarks.Tests`: `QueryTaxonomyTests`, `QuerySetGeneratorTests`, `K6WorkloadTests`, `BundleValidatorTests` |

The §26 gates compare **simple** and **complex** search p95 under bulk load with an idle baseline. QA findings 3, 4
and 12 noted that neither class nor the reviewer think time was defined. This document defines both. The definitions
are also executable: `QueryClassifier` classifies every generated query, and the generator refuses a query whose class
disagrees with its taxonomy bucket. Changing a class, share, rule, band boundary or session parameter bumps the
taxonomy version. Results from different taxonomy versions are never compared.

## 1. Query classes

Each benchmark query belongs to exactly one **class** (the bucket results are reported for). Each class has a
**gate class** (simple or complex, used by the §26 gates) and a **§29 mix bucket**.

| Class | Gate | §29 bucket | Share | Definition | Example (synthetic vocabulary) |
|---|---|---|---|---|---|
| `term` | simple | simple | 20% | 1–2 bare terms, implicit or explicit AND; 15% of draws are a planted needle term | `crer`, `routothu fraikai`, `subreszujo AND griopios`, `zyqvoltran` |
| `phrase` | simple | simple | 10% | One 2–3 word phrase taken from the corpus text (leading/inner function words allowed) | `"is team"`, `"vaimurim sefregukloum"` |
| `field-filter` | simple | simple | 20% | 1–2 terms or one phrase + 1–2 keyword/choice metadata filters (`custodian`, `extension`, `confidentiality`, `language`, `projectcode`) | `zoceacheax AND extension:pdf AND language:en` |
| `date-range` | simple | simple | 10% | 1–2 terms + a `date:[from TO to]` range of 1, 3, 6 or 12 months (+ at most one more metadata filter) | `payment AND date:[2020-05-01 TO 2020-05-31]` |
| `boolean` | complex | boolean | 10% | ≥ 3 Boolean clauses with nesting: `(a OR b) AND (c OR d)`, `(a OR b OR c) AND NOT d`, `a AND (b OR c AND d)`, `a AND b OR c AND d` | `(wurkplidyst OR liolslork) AND (cheafylfoum OR pleateacheaklou)` |
| `content-coding` | complex | boolean | 10% | Content (term, OR of two terms, or phrase) AND a coding-field filter; 35% also add a date range (**date range + coding filter**) | `(chylathirk OR jouso) AND responsiveness:"Not Responsive"` |
| `expansion` | complex | boolean | 5% | Needle term(s) or custodian + metadata filter with **family** (60%) or **duplicate** (40%) expansion (a search option, ADR-008 §7) | `zyqvoltran` + `expand: family` |
| `grid-coding` | complex | boolean | 5% | Custodian filter, term, or two metadata filters with **grid sort** on a coding column (`review_priority`, 70%) and/or **facets** on coding columns (`responsiveness`, `privilege`, `issues`) | `trestimkastra`, sort `review_priority asc`, facets `privilege, issues` |
| `proximity` | complex | proximity-wildcard | 5% | `a W/n b` (n ∈ {2, 3, 5, 10, 20}) or `(a OR b) W/n c` from corpus words; 30% use the planted proximity pair at the near (n = 5) or far (n = 12) distance | `kelvatron W/5 brisomund`, `ledri W/20 jeshougraist` |
| `wildcard` | complex | proximity-wildcard | 5% | Trailing text wildcard with 3–5 literal characters (ADR-008 R7), alone or ANDed with a term; leading `filename:*.ext` or infix `filename:*word*` (the `filename` field has the `leadingWildcard` capability) | `meet*`, `cris* AND weandplai`, `filename:*.docx` |

Shares give the §29 mix exactly: **60% simple, 30% Boolean, 10% proximity/wildcard**. Content + coding-filter queries
(10%) and grid sort/facet on coding columns (5%) are **mandatory** parts of the mix (E17-T04, UI finding 3: ADR-004
is judged on grid sort, filter and facet on coding columns). Every query uses the ADR-008 M1 subset. Operators are
upper case, AND is implicit between adjacent terms, `W/n` is unordered with at most n−1 intervening words, and
wildcards are never silently truncated.

Every search request uses **page size 50** (`?limit=50`), **highlighting on**, and the zone `UTC` for date-only
bounds (ADR-008 R8). Simple queries send no sort, so the server's default grid sort applies.

## 2. Simple vs complex (gate classes)

A query is **simple** when all of these hold:

1. Its content is **at most two terms**, or **exactly one phrase**, joined only by AND (explicit or implicit).
2. It has **at most two metadata filters**: equality on a keyword or choice field, or a date or number range.
3. It has no `OR`, no `NOT`, no wildcard, no `W/n`, and **no reference to a coding field**.
4. It uses no family or duplicate expansion, no facets, and no explicit sort.

**Every other query is complex.** The complex classes above deliberately cover the cases E17-T04 lists: ≥ 3 nested
Boolean clauses, `W/n`, leading or infix wildcards, date range + coding filter, family/duplicate expansion, plus
coding filters and coding-column sort/facets. Classification looks only at the query AST and the search options, never
at the hit count. A broad simple query stays simple.

`QueryClassifier.Classify` is the reference implementation (`Workloads/Oql.cs`). Its §9 examples: `contract AND
termination`, `"trade secret"`, `custodian:"John Smith"` and `contract AND date:[2025-01-01 TO 2025-12-31]` are
simple. `apple W/10 iphone` and `filename:*.xlsx` are complex.

## 3. Selectivity bands and expected hit counts

Every query carries `expectedHits`, the documents it returns after expansion on the freshly loaded corpus plus the
coding fixture. It also carries `hitFraction` (expectedHits ÷ corpus size) and a band:

| Band | Hit fraction | Typical source |
|---|---|---|
| `needle` | < 0.01% | planted needle terms (E17-T01 ground truth) |
| `narrow` | 0.01% to < 1% | rare words, phrases, filtered queries |
| `medium` | 1% to < 10% | |
| `broad` | 10% to < 50% | common words, wide date ranges, custodian expansions |
| `very-broad` | ≥ 50% | short wildcard prefixes, OR of common words |

Selection walks the bands round-robin (narrow, medium, broad, needle, very-broad) for each class, so each class spans
the bands the corpus offers. Every query has at least one expected hit.

How counts are obtained (`hitSource`):

- **`exact`**: metadata, coding and filename-wildcard queries are counted over **every** document. Text queries are
  also exact when the text evaluation covers the whole corpus (`evaluation.textMode = exact`, corpus size ≤
  `--text-sample`).
- **`ground-truth`**: queries whose default-field terms are planted needles or the planted proximity pair are counted
  from the generator's planted lists, the same data as `ground-truth.jsonl`. This covers `W/n` against the planted
  word distance and family/duplicate expansion. The tests check them against `ground-truth.jsonl` and the manifest's
  `familyExpandedDocs`.
- **`sampled`**: on large corpora, vocabulary text queries are evaluated on the synthesised text of a deterministic
  sample (default 5,000 documents, chosen by `hash(querySeed, docIndex)`). The estimate is `sampleHits × N ÷ sample`,
  and a query needs ≥ 3 sample hits to be kept. `sampleHits` is recorded so the uncertainty can be judged.

The reference evaluator applies ADR-008 §2 semantics to the text indexed by the projection: the first 10,000,000
characters (ADR-007 R9). Its tokenizer approximates `opp_text_search`: letters/digits, with `'`, `.` and `_` kept
between letters, lower-cased, and no ASCII folding because the synthetic vocabulary is ASCII. The counts are
benchmark expectations, not the search-term-report oracle. Exact product counts are proven by the ADR-008 golden files
and the known-answer corpus (`E17-T09`).

### Coding fixture

Coding filters, sorts and facets need coding values with known distributions before any reviewer has worked. The
query file carries a `codingFixture`: a seed plus field definitions. A document's coding is a pure function
`CodingFixture.ValuesFor(seed, docIndex)`, where docIndex = control-number sequence − 1.

| Field (query name) | Type | Values |
|---|---|---|
| `responsiveness` | SingleChoice | 60% of documents coded: Responsive 25%, Not Responsive 65%, Needs Further Review 10% |
| `privilege` | SingleChoice | same coded 60%: Privileged 8%, Not Privileged 92% |
| `issues` | MultiChoice | coded documents: Pricing 15%, Antitrust 10%, Employment 8%, Product Safety 6%, Export Control 4%, Board 3% (independent) |
| `review_priority` | WholeNumber | every document, uniform 1–5 (sortable: choice fields are not sortable, ADR-007 §4) |
| `bench_bulk_tag` | MultiChoice | `Batch A`…`Batch H`; written only by the bulk-coding driver, never queried |

The benchmark workspace must define these fields, and the loader must apply the fixture before the run: a bulk-coding
job per value set, or the fast-path loader (`E17-T05`). Reviewers write `responsiveness` and `issues` during the run,
so expected counts describe the state at the **start** of a run.

## 4. The query file (`opportunity-bench queries`)

```bash
dotnet run --project tools/Opportunity.Benchmarks -- queries \
  --corpus-manifest corpus-1m/corpus-manifest.json --seed 42 --count 1000 [--text-sample 5000] --out queries.json
```

The generator **re-creates the corpus from its manifest**, using the seed and the effective profile embedded in
`corpus-manifest.json`, with the generator library. It refuses a manifest from another generator version or one whose
profile does not hash to `profileHash`. Terms come from the sampled text's document frequencies, grouped by decade so
both rare and common words are drawn. Phrases are windows of real corpus text. Custodians, extensions, choice values
and months are drawn by their actual document counts. Needles and the proximity pair come from the profile.

`queries.json` (`format: opportunity-query-set`, `formatVersion: 1`) holds the taxonomy version, query seed, corpus
identity (manifest SHA-256, seed, profile hash, document count, control-number format), evaluation mode, request
defaults, coding fixture, mix, classes with counts, and the queries. Each query has `id`, `class`, `gate`, `bucket`,
`oql`, `expand`, `sort`, `facets`, `expectedHits`, `hitFraction`, `selectivity`, `hitSource` and `sampleHits`. The
file also holds the **schedule**: one seeded permutation of all queries per epoch, with at least 10,000 entries.

**Determinism.** The same corpus manifest, seed and options give a byte-identical file for any `--threads`. If a class
has too few usable candidates (for example phrases with fewer than 3 sample hits), it draws more in further seeded
rounds. Measured on a 4-core dev container: 1,000 queries from a 100K §29 corpus with a 5,000-document text sample
take about 70 s. A metadata pass over 1M documents adds about 20 s each, and the generator makes two or three. The
file is bundled with every result (`workload/queries.json`).

## 5. Workload models

### 5.1 Open-model search stream (`search-mix.js`)

`constant-arrival-rate` at `SEARCH_RATE` queries/s for `DURATION`. Iteration *i* (k6 `iterationInTest`, unique per
scenario) issues `queries[schedule[i mod |schedule|]]`, whatever VU runs it. **The same seed therefore yields the same
query sequence**, and the stub API test proves it. The offered rate does not depend on response times, so
degradation shows up as latency and dropped iterations, never as a lower request rate. This stream supplies the gated
latency samples. Its rate is the calibrated offered rate (test strategy §4).

### 5.2 Reviewer sessions (`reviewer-sessions.js`): closed model

`REVIEWERS` VUs (§29: **100 concurrent reviewers**) each loop over sessions:

1. **Search**: reviewer *v*'s *k*-th session uses schedule position (v−1)·7919 + k.
2. **Open n documents** from the first results page: n ~ uniform{5 … 25}, capped by the hits returned. A session opens
   **at most 25 documents**.
3. For each document: `GET` the document (view), then **read** for a think time *T*, then **code** it with `PUT …/coding`
   and `If-Match: <ETag>`. The coding sets `responsiveness` drawn from the fixture shares, and in 30% of cases adds one
   `issues` value. A `412` (concurrent change) is recorded as a conflict, not an error.
4. **Pause 5 s**, then the next search.

Think time *T* is **log-normal with median 20 s and p90 60 s**: ln *T* ~ N(μ, σ²) with μ = ln 20 = 2.9957 and
σ = ln(60/20) ÷ z₀.₉₀ = 1.0986 ÷ 1.2816 = **0.8572**. It is clamped to [2 s, 300 s], which affects about 0.4% of
draws. The mean is ≈ 28.9 s. Draws come from a PRNG seeded by (query seed, reviewer, session), so a reviewer's
behaviour is reproducible.

Expected load per reviewer: a session takes about 15 × 28.9 s + 5 s ≈ 440 s. For 100 reviewers that is
**≈ 0.23 searches/s, ≈ 3.4 document views/s and ≈ 3.4 coding writes/s**. That is realistic interactive background
(views, coding→searchable traffic, version conflicts with bulk jobs), but it gives too few searches for p95/p99 on its
own. That is why the gated percentiles come mainly from the open stream. Reviewer searches carry `source=session` and
are included in their class histograms.

`THINK_SCALE` multiplies every think time and pause. Use 1 for real runs. Stub and CI runs use 0.001.

### 5.3 Bulk-coding background load (`bulk-coding.js`)

An open model at a **fixed offered rate**: `BULK_DOCS_PER_SEC` documents/s, submitted as jobs of `BULK_BATCH`
documents (k6 `rate = BULK_DOCS_PER_SEC` per `BULK_BATCH` seconds). Job *i* targets
`controlnumber:[<batch i first> TO <batch i last>]`, cycling through the corpus, and adds one `bench_bulk_tag` value. Each job has a unique `Idempotency-Key` (`<RUN_ID>-bulk-<i>`, ADR-019 §2.6)
and expects `202 Accepted` with a `Location`. The submit latency is reported as class `bulk-submit`. Committed and
reflected bulk throughput (`throughput.bulkDocsPerSecond`) and index lag come from the watermark probe (`E17-T06`), not
from k6.

### 5.4 Mixed scenario for the §26 gates (`mixed.js`)

| Phase (k6 `phase` tag) | Window | Load | Bundle scenario role |
|---|---|---|---|
| `idle` | `IDLE_DURATION` | open search stream + reviewers | `idle-baseline` |
| `bulk` | `BULK_DURATION`, starting after the idle window | the **same** search stream (schedule position 0 again) + reviewers + bulk coding at `BULK_DOCS_PER_SEC` | `bulk-load` |

Both windows replay the same query sequence, so the comparison is between like and like. Reviewers run through both
windows, and each of their requests is tagged with the phase it falls in. The warm-up seconds (`ingest-k6 --warmup`)
at the start of each phase are excluded from the histograms.

## 6. API contract used (ADR-019) and stub mode

The M1 API does not exist yet. The scripts use the ADR-019 conventions, and every path is an overridable template
(`API_BASE`, `WORKSPACE_ID`, `PATH_*`). Request bodies that E07/E10 have not fixed yet are defined only in
`k6/lib/api.js`.

| Operation | Default | Request | Expected |
|---|---|---|---|
| Search | `POST /api/v1/workspaces/{workspaceId}/searches?limit=50` (`PATH_SEARCH`) | `{ query, highlight, zone, sort, facets, expand }` | 200, `{ items: [{ documentId }], nextCursor, total: { value, relation } }` |
| Document view | `GET …/documents/{documentId}` (`PATH_DOCUMENT`) | none | 200 with `ETag` |
| Coding | `PUT …/documents/{documentId}/coding` (`PATH_CODING`) | `If-Match`, `{ changes: [{ field, op, value }] }` | 200/204; 412 = conflict |
| Bulk coding job | `POST …/bulk-coding-jobs` (`PATH_BULK_JOBS`) | `Idempotency-Key`, `{ target: { query }, changes }` | 202 + `Location: …/jobs/{jobId}` |

Other settings: `BASE_URL`, and `BENCH_TOKEN` or `BENCH_TOKENS` (bearer tokens; a comma-separated list gives each VU
its own reviewer identity).

**Stub mode.** `opportunity-bench stub-api --queries queries.json [--port 8080]` serves a fake of this contract. It
accepts only queries of the loaded query set with their exact options, page size and highlighting. It enforces
`If-Match` and ETag versions on coding, and `Idempotency-Key` (with reuse detection) on bulk jobs. It answers
violations with RFC 9457 problems, returns `total = expectedHits`, and delays simple searches by 2 ms and complex ones
by 6 ms, so the histograms differ. With `STRICT=1` the scripts turn any failed check into a non-zero k6 exit. The CI
tests run every script against the stub for a few seconds.

## 7. Running and ingesting

k6 is pinned in `versions.env` (`K6=2.3.0`, `K6_DIGEST` = the `grafana/k6` multi-arch index digest). GitHub release
downloads are not reachable from every runner, so the pinned **image** is the source. `k6/run.sh` uses `$K6_BIN`, a
`k6` of the pinned version on `PATH`, or the image with host networking. The tests extract the binary from the image
(`K6_BINARY` overrides, `OPPORTUNITY_TEST_IMAGE_K6` replaces the image).

```bash
# 1. corpus, query set
dotnet run --project tools/Opportunity.DataGenerator -c Release -- generate --seed 42 --documents 1000000 --out corpus-1m
dotnet run --project tools/Opportunity.Benchmarks -- queries --corpus-manifest corpus-1m/corpus-manifest.json --seed 42 --out queries.json
# 2. environment manifest (reference-environments.md §8)
deploy/benchmarks/bench.sh capture --profile dev
# 3. run (k6 samples + summary + load-generator CPU at 1 s)
QUERIES=queries.json BASE_URL=http://127.0.0.1:8080 SEARCH_RATE=50 REVIEWERS=100 BULK_DOCS_PER_SEC=2000 \
  IDLE_DURATION=15m BULK_DURATION=15m tools/Opportunity.Benchmarks/k6/run.sh mixed.js artifacts/bench/k6-run1
# 4. result bundle
dotnet run --project tools/Opportunity.Benchmarks -- ingest-k6 --raw artifacts/bench/k6-run1/k6-raw.json.gz \
  --summary artifacts/bench/k6-run1/k6-summary.json --loadgen-cpu artifacts/bench/k6-run1/loadgen-cpu.jsonl \
  --queries queries.json --environment artifacts/bench/environment-developer-regression-<utc>.json \
  --corpus-manifest corpus-1m/corpus-manifest.json --tier T2 --suite nightly/regression --repetition 1/1 \
  --warmup 60 --offered-qps 50 --offered-bulk-docs 2000 --reviewers 100
# stub mode instead of a real API
dotnet run --project tools/Opportunity.Benchmarks -- stub-api --queries queries.json --port 8080
```

`ingest-k6` writes one run directory through `BundleWriter`, so the bundle is schema-checked and rule-checked
(reference-environments §5.1). Its contents:

- One **scenario per phase**, with time window, warm-up and drain.
- **Query classes**: `simple` and `complex` (gated), one per taxonomy class, and `document-view`, `coding-write` and
  `bulk-submit`. Each has request and error counts and latency in µs (count, min, p50, p90, p95, p99, p99.9, max,
  mean) with the **raw HDR histogram**. Failed requests and warm-up samples are excluded from latency.
- Throughput (queries/s, coding ops/s) and the offered bulk rate.
- **Validity**: dropped iterations ÷ (iterations + dropped) in the phase must be ≤ 0.5%, and load-generator CPU must
  be < 70% (`gates.yaml` `policy.validity`). A phase without CPU samples is **invalid** ("not sampled").
- Bundled: corpus manifest, gates file, `workload/queries.json`, every k6 script (`workloadSha256`), the k6 summary,
  and the CPU series.

Oracles are `not-run` unless `--oracles` supplies them (`E17-T07`).

## 8. Points for confirmation

1. **Simple/complex boundaries.** Two choices here go beyond the ticket text. (a) Any facet, or any sort other than
   the default, makes a query complex. (b) A coding-column sort or facet over an otherwise simple query counts as
   complex. To be confirmed by the PO and architect with the frozen `gates.yaml` (Q-04).
2. **Gated samples.** The gated classes pool open-stream and reviewer searches (`source` tag). The alternative is
   gating on the open stream only.
3. **Coding fixture.** The shares are invented (60% coded, 25% responsive, 8% privileged). Real review statistics
   would re-weight them, as for Q-06.
4. **Sampled counts.** On the 1M/10M corpora, text-query counts are sample estimates. The bands they fall in may be off
   for queries near a band boundary.
5. **Request bodies.** The search, coding and bulk-job bodies in `k6/lib/api.js` are assumptions until E07 and E10 fix
   them. Paths follow ADR-019 and can be overridden.
6. **k6 licence.** k6 is AGPL-3.0. It is used unmodified as an external test tool and is never shipped or linked.
   Confirm that this fits Q-38/Q-42.
