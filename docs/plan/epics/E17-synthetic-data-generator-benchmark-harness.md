# E17 — Synthetic Data Generator & Benchmark Harness

**Labels:** `epic`, `role:performance`, `role:ediscovery`, `role:devops`, `role:data`, `role:qa`, `P0`  
**Starts in:** M0 - Foundation & Benchmark Harness  
**Tickets:** 9

## Goal
Provide a seedable, deterministic generator of realistic vendor-style volumes, plus the reproducible harness (environments, workload model, probes, oracles, result bundles, gate evaluator) that makes the §26 gates mechanically measurable.

## Baseline sections
§5, §12, §17, §18, §26, §28, §29, §30

## Scope / out of scope
**In scope**
- Generator core + distributions (families, duplicates, threads, text, vocabulary, needles)
- Volume writers with defect injection
- Reference environments + result-bundle schema
- Query taxonomy + k6 workloads
- Fast-path loaders + corpus caching
- Coding→searchable probe and index-lag
- Shadow-ledger oracle
- Gate evaluator
- STR/production fixtures

**Out of scope**
- Running the comparative spikes (E18)

## Contributing roles
- **Roles:** Performance, eDiscovery Practitioner, DevOps / SRE, Data (PostgreSQL), QA
- **Source reviews:** QA & Performance, eDiscovery Practitioner, Backend/Architecture, DevOps/SRE
- **Milestones spanned:** M0 - Foundation & Benchmark Harness, M2 - 1M Benchmark & Architecture Blockers, M3 - MVP Feature Complete

## Exit criteria
- [ ] Same seed + profile gives byte-identical output on any machine/thread count
- [ ] A run without a complete manifest is rejected
- [ ] Gate verdicts are reproducible from stored bundles

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E17-T01](#e17-t01) | Build deterministic seeded corpus generator with realistic distributions | M0 | L | E01-T01 |
| [E17-T02](#e17-t02) | Write load-file volumes with defect injection | M0 | M | E17-T01 |
| [E17-T03](#e17-t03) | Define reference environments and result-bundle schema | M0 | L | E01-T01, E19-T03 |
| [E17-T04](#e17-t04) | Implement query taxonomy and k6 workload scripts | M0 | M | E17-T01, E17-T03 |
| [E17-T05](#e17-t05) | Build fast-path loaders and corpus artifact caching | M2 | M | E17-T01, E04-T02, E07-T02, E17-T03 |
| [E17-T06](#e17-t06) | Build end-to-end coding→searchable probe and index-lag measurement | M2 | M | E07-T08, E10-T01, E17-T03 |
| [E17-T07](#e17-t07) | Build stale-version-overwrite shadow-ledger oracle | M2 | M | E03-T02, E17-T03, E04-T04 |
| [E17-T08](#e17-t08) | Build gate evaluator and benchmark report generator | M2 | M | E17-T03, E17-T06, E17-T07 |
| [E17-T09](#e17-t09) | Generate known-answer STR and production-QC fixtures | M3 | M | E17-T01, E17-T02 |

---

### E17-T01

**Build deterministic seeded corpus generator with realistic distributions**  
Labels: `role:performance`, `role:ediscovery`, `P0`, `size:L` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§18 `tools/Opportunity.DataGenerator`, §29 reference corpus. QA finding 11: '~1:3' is ambiguous and distributions are unspecified; eDiscovery: realism (email/e-doc mix, nested attachments, cross-custodian duplicates, threads, Unicode, delimiter characters in values).

#### Description
.NET CLI: `--seed` + versioned YAML/JSON profile → documents derived from `hash(seed, docIndex)` (identical regardless of thread count). Parameterised distributions: ~60% email / 40% e-docs; family size mean ≈ 3 members per parent (geometric or Zipf, long tail incl. > 200-member zip families), nested attachments depth ≤ 3; 20% duplicates as whole-family cross-custodian duplicates (types: exact-MD5, cross-custodian, within-family) with AllCustodians/DuplicateCustodians; threads of 2–50 messages with ConversationIndex prefixes and InclusiveEmail; ~30 custom fields incl. multi-value To/CC/BCC (1–500), multi-zone dates, CJK/accented/RTL names, values containing delimiters and `®`/newlines; heavy-tailed text (median ~5 KB, p99 ~1 MB, some ≥ 10 MB, a few > 100 MB); Zipfian vocabulary with planted needle terms and proximity pairs at known distances; custodians 50–5,000 with Zipf distribution. Ground-truth files and `corpus-manifest.json` (profile hash, generator version).

#### Acceptance criteria
- [ ] Same seed + profile produce byte-identical output across 2 machines and different `--threads`
- [ ] 1M metadata records generate in ≤ 10 min on an 8-core runner
- [ ] Generated 1M distribution report matches configured parameters within ±2% (family mean, duplicate %, text p50/p95/p99/max)
- [ ] Defaults reproduce the §29 enterprise profile and the profile file is documented

#### Dependencies
- `E01-T01` — Scaffold repository layout, .NET solution and Angular workspace

#### Roles
- **Owner:** Performance
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** QA & Performance, eDiscovery Practitioner, Backend/Architecture
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / L

#### Notes
Merges QA T2.1/T2.2, eDiscovery 'Realistic metadata, families, duplicates and threads', backend 'Synthetic corpus generator'. Q-06.

---

### E17-T02

**Write load-file volumes with defect injection**  
Labels: `role:performance`, `role:ediscovery`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
eDiscovery: the importer must be exercised with realistic volume structures and defects, not only DB rows.

#### Description
Writers for `VOL001/DATA|NATIVES|TEXT|IMAGES` (configurable files per folder), DAT in any delimiter preset and encoding (UTF-8, UTF-8-BOM, UTF-16LE, 1252), OPT, extracted TXT, placeholder natives/images (small PDFs/TIFFs, single-page G4/JPG and multi-page TIFF, page counts 1–2,000). Configurable defect injection: wrong field counts, unparseable dates, missing natives/text/images, OPT page-count mismatch, duplicate control numbers, orphan attachments. Streaming output.

#### Acceptance criteria
- [ ] Defect rates match configuration and are listed in the ground-truth file
- [ ] Writers stream with bounded memory (≤ 2 GB at 10M docs)
- [ ] Output is deterministic by seed

#### Dependencies
- `E17-T01` — Build deterministic seeded corpus generator with realistic distributions

#### Roles
- **Owner:** Performance
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** eDiscovery Practitioner, QA & Performance
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

---

### E17-T03

**Define reference environments and result-bundle schema**  
Labels: `role:performance`, `role:devops`, `P0`, `size:L` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§17, §29: every result must record hardware, versions, JVM, durability, shard topology, seed and scripts. QA finding 7: no format, location or repetition rule.

#### Description
Codify the developer-regression and enterprise-reference topologies (§29) as Compose + IaC with pinned digests. `manifest.json` JSON Schema: git SHAs, image digests, SKU/kernel, JVM flags, PG conf, OpenSearch settings, shard topology, durability (`synchronous_commit`, translog, replicas), corpus manifest, workload hash, query-taxonomy version, gates hash, repetition index. Bundles (raw HDR histograms, metrics time series, `verdict.json`) stored in object storage per `runId` with a git-indexed summary. Rule: ≥ 3 repetitions, cold vs warm stated.

#### Acceptance criteria
- [ ] `bench up --profile reference` provisions and `bench down` destroys; provisioned config is captured automatically
- [ ] The uploader rejects a run without a complete manifest
- [ ] One command runs a profile and emits a results bundle

#### Dependencies
- `E01-T01` — Scaffold repository layout, .NET solution and Angular workspace
- `E19-T03` — Create Docker Compose developer profile

#### Roles
- **Owner:** Performance
- **Contributing:** DevOps / SRE
- **Source reviews:** QA & Performance, Backend/Architecture, DevOps/SRE
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / L

#### Notes
Q-03, Q-05.

---

### E17-T04

**Implement query taxonomy and k6 workload scripts**  
Labels: `role:performance`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§29 60/30/10 mix and 100 reviewers with think time; QA findings 3, 4, 12; UI finding 3: ADR-004 must be judged on grid sort/filter/facet on coding columns.

#### Description
Versioned taxonomy: Simple (≤ 2 terms or one phrase + ≤ 2 metadata filters, no wildcard/proximity/coding filter, page 50, highlighting on) vs Complex (≥ 3 nested Boolean clauses, W/n, leading/infix wildcard, date range + coding filter, family/duplicate expansion); mandatory share of content+coding-filter queries and grid sort/facet on coding columns. k6 scripts: open-model constant-arrival stream; closed-model 100-reviewer sessions (search, open ≤ 25 docs, code each, log-normal think time median 20 s / p90 60 s, 5 s between searches); bulk-coding driver at a fixed offered rate.

#### Acceptance criteria
- [ ] Each query is tagged with its bucket; results report p50/p95/p99 per bucket and for simple/complex
- [ ] Same seed yields an identical query sequence
- [ ] Runs with > 0.5% dropped iterations are marked invalid; load generator CPU < 70% recorded

#### Dependencies
- `E17-T01` — Build deterministic seeded corpus generator with realistic distributions
- `E17-T03` — Define reference environments and result-bundle schema

#### Roles
- **Owner:** Performance
- **Contributing:** —
- **Source reviews:** QA & Performance, UI/UX
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

---

### E17-T05

**Build fast-path loaders and corpus artifact caching**  
Labels: `role:performance`, `role:data`, `P0`, `size:M` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
QA finding 8: 10M corpora are several TB; re-generating and re-indexing per run is prohibitive.

#### Description
Fast-path loader writing directly to PG (COPY) and OpenSearch (bulk) for benchmark setup; publish corpora + PG dumps + OpenSearch snapshots keyed by `(generatorVersion, profileHash, seed, schemaVersion, mappingHash)`; cache hit skips generation and ingest for query-path benchmarks; text cap for nightly runs recorded in the manifest.

#### Acceptance criteria
- [ ] Fast path loads 1M docs on the dev-regression profile with state equivalent to the import path (same count, same projection hash on a 1% sample)
- [ ] Nightly 1M run restores from cache in ≤ 20% of full generate+ingest time
- [ ] Schema or mapping hash change triggers a cache miss automatically; retention and cost are documented

#### Dependencies
- `E17-T01` — Build deterministic seeded corpus generator with realistic distributions
- `E04-T02` — Create workspace, document and page core schema
- `E07-T02` — Create baseline search projection mapping and projection builder
- `E17-T03` — Define reference environments and result-bundle schema

#### Roles
- **Owner:** Performance
- **Contributing:** Data (PostgreSQL)
- **Source reviews:** QA & Performance, Backend/Architecture
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / M

---

### E17-T06

**Build end-to-end coding→searchable probe and index-lag measurement**  
Labels: `role:performance`, `P0`, `size:M` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§7, §17, §26. QA finding 2: internal timers omit dispatcher polling, queueing and refresh; finding 1: lag needs a defined measurement point.

#### Description
Canary prober writes a unique marker via the public coding API (t0 = HTTP 200) and polls the public search API (≤ 50 ms interval, same path as real queries, incl. workspace filter) until the doc appears (t1); runs idle, under bulk load and during fault windows. Index lag series from `search.index_lag_seconds` at 1 s resolution with max and p95 over steady state (warm-up/drain excluded per documented rules).

#### Acceptance criteria
- [ ] Calibration: a Toxiproxy-injected 500 ms delay on index worker → OpenSearch raises probe p50 by 500 ms ± 50 ms
- [ ] Probe traffic ≤ 1% of load and excluded from query histograms
- [ ] Lag max and p95 are written to the result bundle

#### Dependencies
- `E07-T08` — Implement search generation watermark and freshness API
- `E10-T01` — Implement interactive coding API with optimistic concurrency
- `E17-T03` — Define reference environments and result-bundle schema

#### Roles
- **Owner:** Performance
- **Contributing:** —
- **Source reviews:** QA & Performance, DevOps/SRE
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / M

---

### E17-T07

**Build stale-version-overwrite shadow-ledger oracle**  
Labels: `role:qa`, `P0`, `size:M` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§21, §26 '0 stale-version overwrites' has no oracle (QA finding 5).

#### Description
Capture committed `(docId, DocumentVersion, coding)` from PG (logical decoding or CodingEvent); continuous sampling during runs asserting `os.version ≤ pg.version` and never decreasing; full reconciliation after quiescence (`os.projectionVersion == pg.DocumentVersion` and coding values equal for 100% of touched docs); count version-conflict rejections.

#### Acceptance criteria
- [ ] `verdict.json` reports staleOverwrites, versionRegressions, missingDocs, valueMismatches
- [ ] A deliberately injected unversioned write (test flag) is detected in 10/10 trials
- [ ] Full reconciliation of 1M docs completes in ≤ 15 min

#### Dependencies
- `E03-T02` — Build shared Testcontainers fixture library with Toxiproxy
- `E17-T03` — Define reference environments and result-bundle schema
- `E04-T04` — Create interim coding current-state and CodingEvent provenance tables

#### Roles
- **Owner:** QA
- **Contributing:** —
- **Source reviews:** QA & Performance
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / M

---

### E17-T08

**Build gate evaluator and benchmark report generator**  
Labels: `role:performance`, `P0`, `size:M` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§26: correctness/security gates first; 'material, repeatable advantage' needs a numeric rule.

#### Description
Tool reading ≥ 3-repetition bundles: degradation % vs idle baseline with identical query seed, lag, probe p95, correctness counters, confidence intervals; Markdown/HTML report with charts; thresholds from a frozen versioned `gates.yaml`.

#### Acceptance criteria
- [ ] Report states the gates file hash and evaluates correctness/security before throughput
- [ ] 'Material, repeatable advantage' is computed per the agreed rule (Q-04)
- [ ] Re-running on the same bundles yields an identical verdict

#### Dependencies
- `E17-T03` — Define reference environments and result-bundle schema
- `E17-T06` — Build end-to-end coding→searchable probe and index-lag measurement
- `E17-T07` — Build stale-version-overwrite shadow-ledger oracle

#### Roles
- **Owner:** Performance
- **Contributing:** —
- **Source reviews:** QA & Performance, Backend/Architecture
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / M

---

### E17-T09

**Generate known-answer STR and production-QC fixtures**  
Labels: `role:ediscovery`, `role:qa`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
eDiscovery: STR counts, unique hits, proximity and family-expanded counts need known answers; production QC needs seeded coding states.

#### Description
Seed text with controllable term frequencies, proximity pairs at known distances (`W/5` true/false), stemming variants, hyphenated/apostrophe tokens and attachment-only terms; ground-truth file with expected docs-with-hits, unique hits and with-family counts. Seed coding: privileged-withhold family members, redact-coded docs with/without redactions, spreadsheet natives, render-failure docs; expected QC results file.

#### Acceptance criteria
- [ ] Ground-truth files are generated deterministically by seed
- [ ] Expected blocking/warning QC counts are emitted for automated production QC tests

#### Dependencies
- `E17-T01` — Build deterministic seeded corpus generator with realistic distributions
- `E17-T02` — Write load-file volumes with defect injection

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** QA
- **Source reviews:** eDiscovery Practitioner
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M
