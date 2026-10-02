# QA & Performance Review — opportuniTY Architecture Baseline

Reviewer role: QA Lead & Performance Engineer
Baseline reviewed: `docs/architecture/architecture-baseline.md` (§1–§35; focus §17, §18, §26, §27, §29, §30)

---

## Review findings

1. **"Index lag" (§26 gate: ≤2 min) has no defined measurement point or method.** §28 introduces a search generation/watermark but does not say how lag is computed. Proposal: lag = `now() − commit_ts(oldest IndexChunkTask/outbox row not yet reflected in the watermark)`, sampled every 1 s per workspace. Report max and p95 over the sustained-load window, not just the final drain time. This needs (a) a commit timestamp on `IndexChunkTask`/`SearchOutbox` rows taken from the PG transaction, (b) a per-workspace "projection current through generation N" watermark that only advances after an OpenSearch *refresh*, not just after the bulk ack, and (c) an OTel gauge `search.index_lag_seconds`. Without (b), a worker can ack a bulk write that is still invisible to search, and the gate passes when it should fail.

2. **"Coding → searchable p95 ≤1 s" (§7, §17, §26) needs a black-box end-to-end probe, not internal timers.** Internal spans (outbox insert → OS ack) leave out dispatcher polling, RabbitMQ queueing and the refresh interval. Proposal: a *canary prober* writes a unique marker value through the public coding API (t0 = HTTP 200 after commit). It then polls the public search API with a query that matches only that marker until the doc appears (t1); latency = t1 − t0. The polling interval (≤50 ms) and the probe rate (for example 2/s spread over the 100 reviewers' workspaces) must be fixed in the methodology. Probes run during idle, during bulk load and during the fault windows. Polling must go through the same API path as real queries, so it also exercises the workspace filter (§23).

3. **"Simple" vs "complex" query is undefined (§17, §26, §29).** §29 gives a 60/30/10 mix of simple/Boolean/proximity-wildcard, but §17/§26 gate only "simple" and "complex". Proposal: define a versioned **query taxonomy** in the workload repo.
   - **Simple:** ≤2 terms or one phrase, plus ≤2 metadata filters. No wildcard, no proximity, no coding filter, sort by relevance or one field, page size 50, highlighting on.
   - **Complex:** any of: ≥3 Boolean clauses with nesting; a proximity operator (W/n); a leading or infix wildcard; a date range combined with a coding filter; family/duplicate expansion.
   - The §29 Boolean and proximity/wildcard buckets both map to "complex" for gating, and per-bucket percentiles are also reported.
   - **Coding-filter queries are mandatory in the mix.** ADR-004 Candidates B and D are penalised exactly on content+coding queries (§25). A mix without them biases the decision toward B or D.

4. **No think-time or session model for the "100 concurrent reviewers with realistic think time" requirement (§29).** Proposal: a closed-model workload of 100 virtual users, each running a scripted review session. One session = search, open up to 25 docs from the result list in order (view + metadata), code each doc, and occasionally run a new search or page forward. Think time is log-normal: median 20 s per doc with p90 at 60 s, and 5 s between searches.
   - Report both an **open-model** arrival-rate result (queries/s at fixed RPS) and the closed-model result. Closed-model alone hides latency through coordinated omission.
   - The load tool must record *intended* start time (k6 `constant-arrival-rate`, or Gatling open injection).
   - Freeze the parameters before the comparative run, as §26 requires for the gates.

5. **The "0 stale-version overwrites" gate (§21, §26) has no oracle.** Proposal: a **shadow-ledger oracle**. The harness records every committed `(docId, DocumentVersion, coding value)` from PG (logical replication slot or `CodingEvent` table). After quiescence it bulk-scans OpenSearch (`projectionVersion` + coding fields) and asserts `os.projectionVersion == pg.DocumentVersion`. It also asserts that the coding values equal the PG current state for 100% of touched docs.
   - The oracle also runs a **continuous variant** during load: sample N docs/s and assert `os.version ≤ pg.version` and never decreasing over time. Detecting a regression needs a per-doc time series, not just the final state.
   - Every write must use external versioning so that OpenSearch `version_conflict` rejections become a counted metric (expected > 0 under contention; counted as safe rejections).

6. **The "100% idempotent in fault-injection suite" gate (§26) is not defined: no fault catalogue and no harness.** Proposal: a deterministic fault-injection harness with three parts.
   - **Fault types:** worker crash at named points (kill −9 before/after PG commit, before/after OS bulk ack, before RabbitMQ ack); duplicate message delivery; message redelivery after an ack timeout; out-of-order delivery; OS 429/503 and partial bulk failures; PG failover; RabbitMQ node restart; network partition (Toxiproxy).
   - **Injection mechanism:** code-level failpoints (an `IFaultInjector` interface, compiled in for test builds, toggled via env or an admin endpoint) plus Toxiproxy and `docker kill` for infrastructure faults.
   - **Metric:** count of runs whose final state matches the oracle (finding 5) divided by total runs, across a seeded matrix. Any single mismatch fails the gate.

7. **Benchmark result storage and reproducibility are under-specified (§17, §29).** §29 lists *what* to record but not the format or location. Proposal:
   - One immutable result bundle per run, holding a `manifest.json` with a published JSON Schema: git SHAs, image digests, hardware/cloud SKU, kernel, JVM flags, PG conf, OS cluster settings, shard topology, durability settings (`synchronous_commit`, translog durability, replicas), generator seed and config hash, workload script hash, and query-taxonomy version.
   - The bundle also holds raw HDR histograms (not just percentiles), time-series exports (Prometheus snapshot or OTel metrics as Parquet), and gate verdicts.
   - Bundles are stored in object storage keyed by `runId`, with an index in a `benchmarks/` git repo or branch.
   - **Reproducibility rule:** ≥3 repetitions per configuration, cold vs warm cache stated, results reported with variance. A "material, repeatable advantage" (§26) must be defined numerically, for example a non-overlapping 95% CI or a ≥10% p95 difference in all 3 runs.

8. **CI cost of 1M/10M runs is unaddressed (§18, §29).** A 10M corpus with heavy-tailed text up to ~10 MB/doc could be several TB of raw text before replicas. Re-generating and re-indexing it on every run is prohibitive. Proposal:
   - Tier the runs: PR CI uses 10K docs; nightly runs use 100K–1M on the dev-regression profile (§29).
   - 10M runs happen only on demand or weekly, on ephemeral reference infrastructure.
   - Generated corpora are cached as versioned artifacts (DAT/TXT tarballs plus PG dumps and OS snapshots keyed by seed+config hash) to skip re-ingest when only query-path code changed.
   - The text-size distribution should be capped for nightly runs and full-fidelity only for reference runs, with the cap recorded in the manifest.
   - A cloud budget per run is needed (see open questions).

9. **The test pyramid is only implied (§18 lists Unit/Integration/ScaleTests).** Contract, property, E2E, security and fault layers are missing. Proposed layers:
   - **Unit:** domain, query parser/AST.
   - **Property-based:** FsCheck. Covers the invariants below.
   - **Integration:** Testcontainers PG/OS/RabbitMQ/MinIO-equivalent, real containers, no mocks for the stores.
   - **Contract:** message envelope schema (§11), OQL→DSL golden files (§9).
   - **Component fault suite.**
   - **E2E UI:** Playwright.
   - **Scale/benchmark.**
   - Each layer needs an owner, a runtime budget and a CI trigger. The §23 cross-workspace access tests belong in integration and must run on every PR.

10. **Correctness invariants worth property-based or model-based testing** (§2.4, §11, §21, §22, §14):
    - **Idempotency:** applying any chunk N times, in any interleaving with other chunks, yields the same PG and OS state as applying it once. Exactly one `CodingEvent` is written per logical change per idempotency key.
    - **Version monotonicity:** for any interleaving of interactive edits and retried bulk tasks, the final OS version equals the max PG version, and the observed OS version for a doc never decreases.
    - **Snapshot membership:** a materialized `DocumentSetSnapshot` returns the identical ID set (order-stable hash) before and after arbitrary concurrent coding, reindex and alias switch. A PIT is only chosen when the §22 predicates hold. A decision-table test covers all 2^4 predicate combinations.
    - **Bates continuity:** a production's Bates numbers are gap-free, unique and monotonic in production sort order, preserve family adjacency, and are deterministic across a crash and restart of production workers.
    - **Workspace isolation:** for random multi-workspace corpora and random queries, no hit, count or aggregation ever crosses workspaces.
    - **Watermark:** the watermark generation never advances past an unreflected task.
    - These are best expressed as a **model-based state-machine test**: an FsCheck `Machine` running against Testcontainers, with a reference in-memory model.

11. **The family/duplicate distribution in §29 is underspecified for the generator.** "~1:3" is ambiguous: it could mean a mean of 3 children or a family size of 4. The rest is also unstated:
    - The shape of the family-size distribution (geometric vs Zipf; max family size, for example 500-attachment emails).
    - Whether the 20% duplicates are exact MD5 duplicates, cross-custodian, or within-family.
    - The text-size distribution parameters ("heavy-tailed", "~10 MB").
    - Field cardinality for the ~30 custom fields.
    - Vocabulary and term-frequency model: needed so proximity and wildcard queries have realistic selectivity (Zipfian vocabulary, plus planted needle terms with known hit counts for oracle-checked recall).

    These must be parameters with defaults recorded in the manifest. Otherwise ADR-004 results will not transfer to real matters.

12. **Bulk-load definition for degradation gates (§26) is missing.** "Sustained target bulk load" needs a fixed offered rate (for example a bulk coding job applying N docs/s via M chunks of size K). If the system cannot absorb 10K docs/s, the gate otherwise becomes untestable. Proposal: run gates at a **fixed offered bulk rate**, chosen as the max sustainable rate of the weakest candidate or a PO-set floor. Run a separate **throughput-ceiling** test to measure the ≥10K docs/s goal. The idle baseline and the loaded run must use identical query streams (same seed).

13. **The RPO/RTO/availability targets (§17) have no test plan.** At minimum, P2 needs a restore drill: PG PITR plus OS snapshot restore, with the outbox and chunk tasks re-driving the projection to consistency. Measure the RTO and verify the RPO with the ledger oracle (finding 5). Also verify that a projection rebuild from PG alone (alias-based reindex, §8) converges.

14. **E2E UI testing is not mentioned (§20, §28, §32).** Freshness UX ("Index current through generation …", §28) and security re-checks (§24: stale hit visible but open/download denied) are user-observable and must be E2E-tested.
    - Proposal: Playwright suites for the §32 vertical slice, run on the Lite Compose profile with a 1K seeded corpus.
    - Include a "stale hit after privilege coding" scenario asserting HTTP 403 on view/native/image, and that the bulk-indexing progress banner shows until the watermark catches up.
    - Use a Playwright-driven UI latency trace for grid/viewer as a smoke perf check only. Load testing stays at the API level.

---

## Proposed epics and tickets

### EPIC: E1 — Test Strategy, CI Test Pyramid & Correctness Infrastructure
Establish the layered test strategy, CI tiers and shared Testcontainers fixtures so that every vertical-slice increment (§32) ships with automated correctness evidence. This epic also defines the oracles (ledger, isolation, watermark) that the ADR-004 gates depend on.
Baseline: §2, §18, §20, §23, §24, §26, §31, §32.

#### T1.1 Test strategy document and CI tier definition
- **Role:** QA
- **Description:** Write `docs/testing/test-strategy.md` defining the pyramid: unit, property/model-based, integration (Testcontainers), contract, fault, E2E, scale. For each layer, state owner, runtime budget and CI trigger (PR / nightly / weekly / on-demand). Map each §26 gate and §31 blocker to the layer that proves it.
- **Acceptance criteria:**
  - The document lists every §26 gate with its measuring test suite and oracle.
  - CI tiers are defined with wall-clock budgets: PR ≤15 min, nightly ≤3 h, reference runs on demand.
  - Reviewed and merged with sign-off from architecture and PO.
- **Dependencies:** None
- **Phase:** P0
- **Size:** S

#### T1.2 Shared Testcontainers fixture library
- **Role:** QA
- **Description:** Create `tests/Opportunity.Testing` with xUnit collection fixtures for PostgreSQL, OpenSearch (single node), RabbitMQ and S3-compatible storage, all with pinned image digests. Each test gets an isolated workspace (WorkspaceId per test) and fast reset (template DB / index deletion). Include Toxiproxy containers in front of each dependency.
- **Acceptance criteria:**
  - A sample integration test exercises import → index → search on a 100-doc corpus in under 60 s on a CI runner.
  - Fixtures reuse containers across a test collection, and parallel test classes do not interfere (verified by running the suite with `-parallel` 4 times without flakes).
  - Toxiproxy can inject latency, reset and timeout on PG, OS and RabbitMQ links from test code.
- **Dependencies:** T1.1
- **Phase:** P0
- **Size:** M

#### T1.3 Cross-workspace isolation and authorization re-check suite
- **Role:** QA
- **Description:** Add an automated suite that seeds ≥3 workspaces in a shared index (routing) and attempts cross-workspace access through every search, count, aggregation, saved-search, snapshot, view, native, image and export endpoint. Include a randomized property variant. Include the §24 scenario: privilege coding removes access, the stale search hit remains, and protected retrieval is denied before OS refresh (refresh disabled during the test).
- **Acceptance criteria:**
  - 0 cross-workspace hits, counts or aggregation buckets across ≥1,000 generated queries.
  - Protected retrieval returns 403 within the same request after privilege coding commit, with OS refresh paused.
  - Runs on every PR. Any failure blocks merge.
- **Dependencies:** T1.2
- **Phase:** P0
- **Size:** M

#### T1.4 Property- and model-based correctness invariants
- **Role:** QA
- **Description:** Use FsCheck to write property and state-machine tests for:
  - chunk idempotency (N× apply, any interleaving);
  - version monotonicity (interactive edits vs retried bulk tasks);
  - snapshot membership stability across coding, reindex and alias switch;
  - the §22 PIT-vs-materialized decision table;
  - watermark safety;
  - (when production exists) Bates continuity and family adjacency.

  Each test runs against Testcontainers with a reference in-memory model.
- **Acceptance criteria:**
  - Each invariant has a named property. Shrunk counterexamples are logged with their seed and can be replayed via `FSCHECK_SEED`.
  - PR runs use ≥100 cases per property. Nightly runs use ≥10,000 cases.
  - The §22 decision table covers all 16 predicate combinations.
  - A seeded known bug (an unversioned OS write in a test build) is detected by the version property within the nightly budget (mutation check).
- **Dependencies:** T1.2, T3.4 (oracle)
- **Phase:** P0 (idempotency/version/snapshot), P1 (Bates)
- **Size:** L

#### T1.5 E2E UI suite for the vertical slice (Playwright)
- **Role:** QA
- **Description:** Write Playwright tests on the Lite Compose profile with a 1K seeded corpus. They cover the §32 path (import → search → view → interactive code → search new coding → bulk tag → verify → export) plus the freshness banner (§28) and stale-hit denial (§24).
- **Acceptance criteria:**
  - The suite passes headless in CI (nightly, and on PRs that touch `Opportunity.Web` or the API) in ≤20 min.
  - Asserts the UI shows "Search index updating" while watermark < job generation, and that the banner clears after catch-up.
  - Asserts the viewer shows access denied for a doc whose privilege was coded after the search.
  - Trace, video and HAR artifacts are uploaded on failure. Flake rate is <1% over 50 consecutive nightly runs.
- **Dependencies:** T1.2, vertical-slice UI availability
- **Phase:** P1
- **Size:** M

### EPIC: E2 — Synthetic Data Generator (`tools/Opportunity.DataGenerator`)
Provide a seedable, deterministic corpus generator that produces DAT/OPT/TXT/native load sets (and optionally direct PG/OS bulk loads). Its distributions are configurable to match §29, so 1M/10M benchmarks are reproducible and representative.
Baseline: §5, §12, §18, §23, §29.

#### T2.1 Generator core: deterministic seeded corpus model
- **Role:** Performance
- **Description:** Build a .NET CLI that, given `--seed` and a versioned YAML/JSON profile, deterministically generates documents with structural fields (§23): control numbers, families (parent + attachments), duplicate groups, email threads, custodians, dates, file types and ~30 custom fields. Field types follow §6 and use configurable cardinality. Generation is streaming and parallel-safe: each doc is derived from `hash(seed, docIndex)` so output is identical regardless of thread count.
- **Acceptance criteria:**
  - The same seed and profile produce byte-identical output (SHA-256 of the manifest) across 2 machines and different `--threads` values.
  - Generates 1M metadata records in ≤10 min on an 8-core runner.
  - The profile hash and generator version are written to `corpus-manifest.json`.
- **Dependencies:** None
- **Phase:** P0
- **Size:** M

#### T2.2 Distribution models: families, duplicates, text size, vocabulary
- **Role:** Performance
- **Description:** Implement parameterised distributions:
  - family size (mean ≈3 members per parent per §29; geometric or Zipf with configurable max, for example 500);
  - duplicate rate (default 20%) with types for exact-MD5, cross-custodian and within-family;
  - heavy-tailed extracted-text size (log-normal body plus Pareto tail, max ~10 MB);
  - a Zipfian vocabulary with planted "needle" terms and phrases at known frequencies;
  - proximity pairs at controlled distances, for oracle-checked hit counts.
- **Acceptance criteria:**
  - A generated 1M corpus report (histograms) matches configured parameters within ±2% (family mean, duplicate %, text-size p50/p95/p99/max).
  - Needle queries return exactly the planted hit counts when run through the search API (recall oracle).
  - Defaults reproduce the §29 enterprise profile, and the profile file is documented.
- **Dependencies:** T2.1
- **Phase:** P0
- **Size:** M

#### T2.3 Output writers: DAT/OPT/TXT/natives + fast-path bulk loaders
- **Role:** Performance
- **Description:** Implement writers for Concordance DAT, Opticon OPT, extracted TXT and placeholder natives/images (small PDFs/TIFFs). Add an optional "fast-path" loader that writes directly to PG (COPY) and OS (bulk) for benchmark setup, so 10M runs do not need full import. The import path is benchmarked separately.
- **Acceptance criteria:**
  - Generated DAT/OPT import successfully through the MVP importer (§12) for a 10K corpus with 0 errors.
  - The fast-path loads 1M docs into PG and OS on the dev-regression profile, and the resulting state is equivalent to the import path: same doc count and same per-doc projection hash for a 1% sample.
  - Writers stream output (bounded memory ≤2 GB at 10M docs).
- **Dependencies:** T2.1, T2.2, importer DAT/OPT schema
- **Phase:** P0
- **Size:** M

#### T2.4 Corpus artifact caching and versioning
- **Role:** Performance
- **Description:** Publish generated corpora and pre-loaded PG dumps and OS snapshots as versioned artifacts, keyed by `(generatorVersion, profileHash, seed, schemaVersion)`. A cache hit skips generation and import for query-path benchmarks.
- **Acceptance criteria:**
  - A nightly 1M run restores from cache in ≤20% of the full generate+ingest time.
  - A cache miss is detected automatically on a schema or mapping hash change.
  - The retention policy and storage cost are documented.
- **Dependencies:** T2.3, T3.1
- **Phase:** P1
- **Size:** S

### EPIC: E3 — Benchmark Harness, Workload Model & Measurement Oracles
Build the reproducible benchmark harness: environment provisioning, workload scripts, the end-to-end latency and lag probes, the stale-version oracle, fault injection, and immutable result bundles. With these in place, the §26 gates are measurable and their verdicts can be computed mechanically.
Baseline: §7, §17, §21, §26, §28, §29.

#### T3.1 Reference environment definitions and result-bundle schema
- **Role:** Performance
- **Description:** Codify the dev-regression and enterprise-reference topologies (§29) as Compose and IaC (Terraform or equivalent), with pinned image digests and settings. Define `manifest.json` JSON Schema to capture:
  - hardware/SKU, kernel, versions, JVM flags, PG config;
  - OS cluster settings, shard topology, durability settings;
  - corpus manifest, workload script hash, query taxonomy version, gate thresholds;
  - run repetition index.

  Results are stored in object storage per `runId`, with a git-indexed summary.
- **Acceptance criteria:**
  - `bench up --profile reference` provisions the environment, and `bench down` destroys it. The provisioned config is captured automatically into the manifest (no manual entry).
  - A run without a complete manifest is rejected by the result uploader.
  - The bundle contains raw HDR histograms, a metrics time-series export and `verdict.json`.
- **Dependencies:** PO decision on reference hardware (OQ1)
- **Phase:** P0
- **Size:** L

#### T3.2 Query taxonomy and workload scripts (k6)
- **Role:** Performance
- **Description:** Implement a versioned query taxonomy: simple vs complex per finding 3, with the 60/30/10 bucket mix and a mandatory share of content+coding-filter queries. Query templates draw parameters from the corpus manifest, so selectivity is known. Write k6 scripts covering:
  - (a) an open-model constant-arrival query stream;
  - (b) a closed-model 100-reviewer session with the log-normal think time from finding 4;
  - (c) bulk-coding job drivers at a fixed offered rate.
- **Acceptance criteria:**
  - Each query is tagged with its bucket. Results report p50/p95/p99 per bucket and for the gated simple/complex classes.
  - Scripts are seedable: the same seed yields an identical query sequence.
  - k6 records dropped iterations. A run with >0.5% dropped iterations (load-generator saturation) is marked invalid.
  - The load generator runs on separate hosts from the SUT, with CPU <70% verified in the manifest.
- **Dependencies:** T2.2, T3.1
- **Phase:** P0
- **Size:** M

#### T3.3 End-to-end coding→searchable probe and index-lag measurement
- **Role:** Performance
- **Description:** Implement the canary prober from finding 2 (public coding API → poll public search API for a unique marker), and the index-lag metric from finding 1:
  - commit timestamps on `SearchOutbox`/`IndexChunkTask`;
  - a refresh-aware watermark;
  - an OTel gauge `search.index_lag_seconds`.

  Validate the probe against injected known delays.
- **Acceptance criteria:**
  - With a Toxiproxy-injected 500 ms delay on the index worker → OS link, probe p50 increases by 500 ms ±50 ms (calibration test).
  - Lag is reported as a time series (1 s resolution) with max and p95 over the steady-state window. Warm-up and drain are excluded per documented rules.
  - Probe traffic is ≤1% of total query load and is excluded from the query-latency histograms.
- **Dependencies:** §28 watermark implementation, T3.1
- **Phase:** P0
- **Size:** M

#### T3.4 Stale-version-overwrite oracle (shadow ledger)
- **Role:** QA
- **Description:** Build the ledger oracle from finding 5. It captures committed `(docId, version, coding)` from PG (logical decoding or `CodingEvent`), samples OS continuously during runs, and does a full reconciliation scan after quiescence. It also counts OS version-conflict rejections.
- **Acceptance criteria:**
  - Reports `staleOverwrites`, `versionRegressions`, `missingDocs` and `valueMismatches` in `verdict.json`.
  - Detects a deliberately injected unversioned write (test build flag) in 100% of 10 trials.
  - The full reconciliation of 1M docs completes in ≤15 min (PIT + `search_after` scan vs PG COPY).
- **Dependencies:** T1.2, T3.1
- **Phase:** P0
- **Size:** M

#### T3.5 Fault-injection harness and idempotency matrix
- **Role:** QA
- **Description:** Implement an `IFaultInjector` failpoint interface (test builds only) at named points:
  - before/after PG commit;
  - before/after OS bulk ack;
  - before RabbitMQ ack;
  - mid-chunk.

  Add an infrastructure fault driver: `docker kill`/pause, Toxiproxy, PG failover, RabbitMQ restart, OS 429/partial bulk failure via a proxy. Define a seeded matrix of (fault × point × workload) and use the T3.4 oracle as the pass criterion.
- **Acceptance criteria:**
  - The matrix covers ≥8 fault types × ≥5 failpoints. Each cell runs ≥5 seeded trials in nightly runs.
  - Gate metric "idempotent %" = passing trials / total trials, and must be 100%. Failures are reproducible from the logged seed.
  - The matrix also asserts no duplicate `CodingEvent` per idempotency key, and that `JobChunk` and `IndexChunkTask` reach terminal states (no orphaned tasks after the recovery window).
  - Failpoints are compiled out of release builds (verified by a build check).
- **Dependencies:** T1.2, T3.4, §11 job/chunk implementation
- **Phase:** P0
- **Size:** L

#### T3.6 Gate evaluator and benchmark report generator
- **Role:** Performance
- **Description:** Write a tool that reads result bundles (≥3 repetitions) and computes §26 gate verdicts:
  - degradation % vs the idle baseline with the same query seed;
  - lag;
  - probe p95;
  - correctness counters.

  It also computes confidence intervals and produces a Markdown/HTML report with charts, with correctness and security gates evaluated first per §26.
- **Acceptance criteria:**
  - Gate thresholds are read from a frozen, versioned `gates.yaml`. The report states the gates file hash.
  - "Material, repeatable advantage" is computed per the agreed numeric rule (OQ5).
  - The report is reproducible: re-running on the same bundles yields an identical verdict.
- **Dependencies:** T3.1–T3.4
- **Phase:** P0
- **Size:** M

### EPIC: E4 — ADR-004 & PostgreSQL Coding Benchmarks, 10M Validation
Execute the timeboxed comparative 1M spike of Candidates A–D together with the PostgreSQL coding-model spike (§27). Publish the decision evidence, then validate the winning design at 10M (§29 Phase 2) and add regression guards.
Baseline: §6, §25, §26, §27, §29, §30, §31.

#### T4.1 Freeze reference hardware, gates and methodology (pre-run sign-off)
- **Role:** Performance
- **Description:** Run a calibration on the reference environment. This covers the idle baseline variance across 3 runs and the max sustainable bulk rate for the weakest candidate, which sets the fixed offered rate per finding 12. Then freeze `gates.yaml`, the query taxonomy, think-time parameters and the corpus profile.
- **Acceptance criteria:**
  - Idle-baseline p95 coefficient of variation is ≤5% across 3 runs. If it is higher, environment noise is investigated before proceeding.
  - The signed-off methodology record (PO + architect) is committed before the first comparative run.
- **Dependencies:** T3.1–T3.6, T2.2
- **Phase:** P0
- **Size:** S

#### T4.2 PostgreSQL coding-model benchmark (§27)
- **Role:** Performance
- **Description:** Benchmark the `DocumentCodingCurrent` + `CodingEvent` variants:
  - fillfactor 70/90/100;
  - chunk sizes 500/2K/10K;
  - partitioning hash-by-workspace vs dedicated (§6).

  Measure insert/update throughput, WAL bytes per doc, HOT ratio (`pg_stat_user_tables`), bloat (`pgstattuple`), autovacuum cycles, lock waits, rollback/retry cost and table/index growth.
- **Acceptance criteria:**
  - Results are reported for 1M and 10M-row bulk coding jobs with all §27 metrics, ≥3 repetitions.
  - Concurrent interactive coding p95 is measured while the bulk job runs.
  - A recommendation note is linked into the ADR-004 decision.
- **Dependencies:** T2.3, T3.1, T3.6
- **Phase:** P0
- **Size:** L

#### T4.3 ADR-004 1M comparative run, Candidates A–D
- **Role:** Performance
- **Description:** Implement benchmark-grade projections for A (unified, with coalescing and bulk refresh-tuning variants), B (split), C (hybrid) and D (parent/child). For each, run idle, bulk-load, throughput-ceiling and fault-matrix scenarios at 1M. Capture §25-specific metrics: `has_child` latency, global-ordinal memory and build time, merge pressure, and highlight cost on content+coding queries.
- **Acceptance criteria:**
  - Every candidate has a complete result bundle (≥3 repetitions per scenario) and a `verdict.json` for all §26 gates.
  - The correctness and security gates (T3.4, T3.5, T1.3) run against each candidate.
  - The run fits the agreed timebox. Any candidate not completed is marked "not evaluated", not "failed".
- **Dependencies:** T4.1, T4.2, T3.5
- **Phase:** P0
- **Size:** L

#### T4.4 ADR-004 decision report
- **Role:** Performance
- **Description:** Produce the comparative report (T3.6) with a recommendation per the §26 rules: correctness first, then least operational complexity unless there is a material repeatable advantage. Record an operational-complexity rubric score for each candidate.
- **Acceptance criteria:**
  - The report is published under `docs/adr/` evidence with links to the immutable bundles.
  - The ADR-004 status is updated (decision or explicit re-run request).
  - §30 status table updates are proposed with the measured values.
- **Dependencies:** T4.3
- **Phase:** P0
- **Size:** S

#### T4.5 10M validation of the winning design (§29 Phase 2)
- **Role:** Performance
- **Description:** Run the enterprise reference workload at 10M with the winning design. Run duration is ≥4 h of steady state, with bulk coding running concurrently. Measure:
  - segment-merge behavior, disk amplification, merge CPU/I/O;
  - indexing backpressure and p95/p99 degradation over time;
  - long-running materialized snapshot behavior;
  - PG WAL, bloat and autovacuum.

  Compare against the 1M results to detect super-linear trends.
- **Acceptance criteria:**
  - All §26 gates are re-evaluated at 10M. Any material failure (as defined in OQ6) blocks acceptance for the 100M roadmap per §29.
  - p95 drift over the steady-state window is reported (slope test). Disk amplification is reported as on-disk size / raw text size.
  - A snapshot of 1M+ members, restarted mid-run, completes with identical membership hash.
- **Dependencies:** T4.4, T2.4
- **Phase:** P1
- **Size:** L

#### T4.6 Continuous performance regression guard
- **Role:** Performance
- **Description:** Run a nightly 1M dev-regression profile benchmark (cached corpus) on fixed hardware. It compares against a rolling baseline and opens an issue automatically on regression. Results are kept in the time-series result index for trend dashboards.
- **Acceptance criteria:**
  - Alerts fire on a >15% p95 regression in any bucket, or on any correctness-counter >0, sustained over 2 consecutive nights.
  - Dashboard shows a 90-day trend for search p95 by bucket, coding→searchable p95, index lag and bulk docs/s.
- **Dependencies:** T2.4, T3.6, T4.4
- **Phase:** P1
- **Size:** M

#### T4.7 Recovery and RPO/RTO drill
- **Role:** QA
- **Description:** On the reference profile at 1M, run three recovery scenarios:
  - PG PITR restore plus OS snapshot restore, measuring outbox and chunk-task re-drive to consistency;
  - a full projection rebuild from PG via alias-based reindex (§8);
  - a workspace deletion (§15) during active indexing.
- **Acceptance criteria:**
  - Measured RTO and RPO are reported against the §17 targets. The ledger oracle shows 0 mismatches post-recovery.
  - The rebuild-from-PG projection hash equals the live projection hash.
  - Deletion leaves 0 residual docs in PG, OS and storage prefixes for that workspace, verified by a scan.
- **Dependencies:** T3.4, T4.4
- **Phase:** P2
- **Size:** M

---

## Open questions for the product owner

1. **Reference hardware and budget:** which cloud/region and instance SKUs (or on-prem spec) define the "enterprise performance reference" (§29)? What is the approved budget per 10M run and per month? Estimated need: ~3 OS data nodes with NVMe, a PG primary + replica, 2–4 load-generator hosts, multi-TB storage, and runs of 8–24 h including load.
2. **Gate freezing authority:** who signs off the frozen gates, the query taxonomy and the think-time model before the ADR-004 comparative run (§26)? Can gates be relaxed after the calibration run (T4.1), or only tightened?
3. **Bulk-load definition:** for the "sustained target bulk load" degradation gates, should the fixed offered rate be the provisional 10K docs/s (§17) even if no candidate sustains it? Or should it be the calibrated max of the weakest candidate, with 10K docs/s measured separately as a ceiling?
4. **Corpus realism:** do we have, or can we obtain, anonymised statistics from real matters (family-size distribution, duplicate types, text-size percentiles, custom-field cardinality) to set the generator defaults? Or is the §29 profile the sole authority?
5. **"Material, repeatable advantage" (§26):** what numeric threshold justifies choosing a more operationally complex candidate? Proposed: ≥20% p95 improvement on gated query classes, or ≥1.5× bulk throughput, consistent across all repetitions.
6. **"Fails materially at 10M" (§29):** what constitutes material failure? Examples: any §26 gate missed; p95 growth more than X% from 1M to 10M; or a super-linear disk or merge cost trend.
7. **Durability settings for benchmarks:** must reference runs use production-grade durability (PG `synchronous_commit=on` with a sync replica, OS translog `request` durability, replicas ≥1)? Results under relaxed settings are not comparable to the §17 RPO target.
8. **E2E and browser scope:** which browsers and accessibility standards (for example WCAG 2.1 AA) must the Playwright suite cover for MVP? Is UI-level latency (grid render, viewer first page) a gated metric, or diagnostic only?
