# opportuniTY Test Strategy and CI Tiers

> **Status:** Draft for sign-off (plan key `E03-T01`, issue #35). Becomes normative once the architect and product owner approve it (see [Sign-off](#9-sign-off)).
> **Owner:** QA Lead.
> **Inputs:** [architecture baseline](../architecture/architecture-baseline.md) §2.4, §9, §11, §17, §18, §21–§24, §26, §29, §31, §32; [PO decisions](../plan/decisions.md) Q-03, Q-04, Q-05, Q-07, Q-10, Q-12, Q-15; epics E03, E05, E17, E18; [QA & Performance review](../plan/reviews/qa-perf.md) findings 1–14.

## 1. Principles

1. **Evidence, not assertion.** Every §26 gate and §31 blocker has a named suite and a mechanical oracle (§5). A gate without an oracle counts as unproven, not passed.
2. **Real stores, no store mocks.** PostgreSQL, OpenSearch, RabbitMQ and the S3-compatible store are tested as real containers. Mocks are allowed only for *ports we own* inside unit tests (e.g. `IClock`, `IFaultInjector`), never to stand in for a store's behavior.
3. **Correctness and security before throughput.** In line with §26, a suite that reports both always evaluates correctness and security first. Throughput never offsets a correctness failure.
4. **Deterministic and replayable.** Every randomized test (FsCheck, generator, fault matrix, k6 workload) logs its seed and can be replayed from it (`FSCHECK_SEED`, `--seed`).
5. **Relative measures on developer hardware (Q-03).** No frozen reference hardware exists. Performance gates are judged on the same machine against that machine's own idle baseline, and on comparisons between candidates. Absolute latencies are recorded but informational. Every run records hardware, versions and settings per §17/§29.
6. **Synthetic data only** (§7).

## 2. Test layers

Location paths are relative to the repo root and match the scaffolded projects (`tests/Opportunity.*Tests`, `tools/Opportunity.*`). `tests/Opportunity.Testing` is the shared fixture library (Testcontainers + Toxiproxy, `E03-T02`) used by every container-backed layer.

| # | Layer | Purpose | Owner role | Tooling | Location | Runtime budget | CI trigger |
|---|---|---|---|---|---|---|---|
| L1 | **Unit** | Domain rules, query parser/AST, §22 snapshot decision logic, Bates allocator, envelope (de)serialization, Angular components | Backend / UI (author of the code) | xUnit, FluentAssertions; Jasmine/Jest for Angular | `tests/Opportunity.UnitTests`, `src/Opportunity.Web/**/*.spec.ts` | ≤ 3 min | Every PR and push |
| L2 | **Architecture** | Enforce boundaries: RabbitMQ APIs stay out of domain/application code (§11); no raw OpenSearch DSL reaches the API surface (§9); every search goes through the single workspace-filtering search service (§23); protected-content endpoints call the authorization re-check (§24) | Lead architect (rules), QA (CI) | NetArchTest / ArchUnitNET on xUnit | `tests/Opportunity.ArchitectureTests` | ≤ 1 min | Every PR |
| L3 | **Property / model-based** | Correctness invariants in §6, as pure properties and as an FsCheck state machine (in-memory reference model vs Testcontainers SUT) | QA | FsCheck + xUnit | Pure: `tests/Opportunity.UnitTests/Properties`; model-based: `tests/Opportunity.IntegrationTests/Invariants` | PR ≤ 4 min (≥ 100 cases/property); nightly ≤ 45 min (≥ 10,000 cases/property) | PR + nightly |
| L4 | **Integration** | Real PG/OpenSearch/RabbitMQ/S3 behavior: migrations, RLS, outbox/dispatcher, `IndexChunkTask` workers, mapping (§23), import → index → search on a 100-doc corpus | Backend (author), QA (fixtures) | xUnit + Testcontainers (images pinned by digest), per-test `WorkspaceId`, template-DB reset | `tests/Opportunity.IntegrationTests` | ≤ 8 min (parallel collections) | Every PR |
| L5 | **Contract** | (a) Message envelope (§11: MessageId, MessageType, SchemaVersion, WorkspaceId, JobId, CorrelationId, CausationId, IdempotencyKey, CreatedAt, Attempt, Headers, Payload) validated against a versioned JSON Schema; old-version payloads still consumable. (b) Query language → OpenSearch DSL golden files (§9): each query in the corpus has a checked-in expected DSL; any diff fails until the golden file is deliberately updated in the same PR | Backend (messaging, search) | xUnit, JSON Schema validator, golden-file approval (Verify) | `tests/Opportunity.UnitTests/Contracts/{Messaging,QueryLanguage}`; golden files beside them | ≤ 1 min | Every PR; golden-file changes require search-owner review |
| L6 | **Component fault injection** | Idempotency and version safety under crashes, duplicates, redelivery, reordering, OpenSearch 429/503 and partial bulk failure, PG failover, RabbitMQ restart, network partition (`E18-T01`) | QA (contributing: Performance) | `IFaultInjector` failpoints (test builds only), Toxiproxy, `docker kill`/`pause`, shadow-ledger oracle (`E17-T07`) | `tests/Opportunity.IntegrationTests/Faults` | PR smoke ≤ 5 min (1 trial × each fault type on the vertical-slice path); nightly full matrix ≤ 75 min | PR (smoke) + nightly (full ≥ 8 faults × ≥ 5 failpoints × ≥ 5 seeds) + on-demand per ADR-004 candidate |
| L7 | **Security** | Cross-workspace attack suite (§23) and stale-hit authorization suite (§24, Q-11–Q-13, Q-12, Q-15) across REST/IDOR, search, counts, aggregations, saved searches, snapshots, PIT cursors, object keys/presigned URLs, job IDs, exports, message injection, audit queries (`E05-T05`) | Security (contributing: QA) | xUnit + Testcontainers, randomized FsCheck variant, ≥ 3 workspaces in shared-index and dedicated-index placements | `tests/Opportunity.IntegrationTests/Security` | ≤ 4 min PR; randomized ≥ 1,000 queries nightly | Every PR (merge-blocking) + nightly |
| L8 | **E2E (UI)** | §32 vertical slice through the browser: search → view → interactive code → search new coding → bulk tag → verify → export; freshness banner (§28, Q-10) and stale-hit denial (§24); keyboard-only variant | QA (contributing: UI/UX) | Playwright (headless), Angular, developer Compose profile, 1K seeded corpus imported via API | `src/Opportunity.Web/e2e` | ≤ 20 min | Nightly + PRs touching `src/Opportunity.Web` or the API |
| L9 | **Scale / benchmark** | Throughput, latency percentiles, index lag, degradation under bulk load, §26 gate verdicts, regression guard | Performance (contributing: DevOps, QA for oracles) | `tools/Opportunity.DataGenerator`, k6 (open + closed models), coding→searchable canary probe, gate evaluator, result bundles (`manifest.json`, HDR histograms, `verdict.json`) | `tests/Opportunity.ScaleTests` (scenario definitions, assertions), `tools/Opportunity.Benchmarks` (harness, k6 scripts, `gates.yaml`, evaluator) | Per tier (§3) | Tiers T1–T4 (§3) |

Rules that apply to all layers:

- Test names state the behavior and the baseline section or decision they prove, e.g. `RetriedBulkChunk_NeverOverwritesNewerInteractiveEdit_S21`.
- A PR that adds a protected-content endpoint must add it to the L7 endpoint inventory. L2 fails if an endpoint lacks the re-check attribute or the inventory entry.
- New message types need an L5 schema. New query-language operators need L5 golden files.

## 3. CI tiers

| Tier | Trigger | Corpus | Environment | Wall-clock budget | Contents | Blocks |
|---|---|---|---|---|---|---|
| **T1 — PR** | Every PR and push to a PR branch | ≤ 10K docs (generator seed fixed per release; 100-doc fixtures for L4) | Hosted CI runner, Testcontainers | **≤ 15 min** end to end (jobs run in parallel) | L1, L2, L3 (≥ 100 cases), L4, L5, L6 smoke, L7, L8 only when the UI/API paths changed; 10K import → index → search → bulk tag → export smoke with ledger reconciliation | Merge |
| **T2 — Nightly** | Scheduled nightly on `main` | 100K docs nightly; 1M on the weekly slot of the same job (cached corpus restore, `E17-T05`) | Largest available self-hosted runner or developer machine, developer-regression topology (§29) | **≤ 3 h** | L3 (≥ 10,000 cases), L6 full matrix, L7 randomized, L8, L9 regression run: import → index → search → bulk tag → export, p50/p95/p99 per query bucket, coding→searchable probe, index lag, ledger oracle | Opens an issue automatically; release is blocked while any correctness counter is > 0 |
| **T3 — On-demand 1M comparative spike** | Manual dispatch by Performance owner (ADR-004 `E18-T04`, PG coding spike `E18-T03`) | 1M docs, §29 profile (Q-06) | **Developer hardware (Q-03)**, every hardware/version/setting captured in `manifest.json`; relaxed durability allowed only if recorded (Q-05) | Timeboxed per candidate; no CI budget, ≥ 3 repetitions per scenario | Per candidate: L6 full matrix + L7 + ledger oracle **first**, then the gated load scenarios (§5) at the calibrated offered rate (Q-04) | ADR-004 decision |
| **T4 — 10M validation (M4)** | — | 10M docs | **Deferred until a sponsor provides hardware (Q-03).** Harness stays scripted (`bench up/down`) so it can move to rented or sponsored hardware without changes | n/a | `E18-T07`, `E18-T08`, `E18-T09`; §29 Phase 2 measures; Q-04 "material failure at 10M" | 100M roadmap |

Tier rules:

- **PR budget is enforced.** If T1 goes over 15 min, the slowest suites are split or sharded. Tests are not moved to nightly to save time unless the owner and QA agree, and the move is recorded in this document.
- **Nightly regression guard** (`E18-T06`): an alert fires on > 15% p95 regression in any query bucket against the rolling baseline *on the same runner*, or on any correctness counter > 0 for 2 consecutive nights. Runs on a different machine start a new baseline series. They are never compared directly with the previous series.
- **Weekly 1M slot** reuses the nightly job definition. If a 1M cache restore pushes it over 3 h, it moves to T3-style manual dispatch, and the regression guard compares only weekly runs with each other.

## 4. Performance gate method (applies to T2/T3)

- Gates live in `tools/Opportunity.Benchmarks/gates.yaml`. They are frozen and signed off by PO + architect before the comparative run, and may afterwards only be tightened (Q-04, `E18-T02`). The report states the gates file hash.
- **Calibration:** idle-baseline p95 coefficient of variation ≤ 5% over 3 runs on the spike machine, otherwise noise is investigated first. The fixed offered bulk rate = calibrated maximum sustainable rate of the weakest candidate (Q-04). ≥ 10K docs/s is measured separately as a ceiling.
- **Simple/complex** follow the versioned query taxonomy (`E17-T04`). Content+coding-filter queries and grid sort/facet on coding columns are mandatory in the mix.
- **Q-12 page post-filter cost** (re-checking each results page against PostgreSQL) is always enabled in gated runs and is reported separately per page.
- **Material advantage** = ≥ 20% p95 improvement on gated classes, or ≥ 1.5× bulk throughput, consistent across all repetitions (Q-04).
- Runs with > 0.5% dropped k6 iterations, or load-generator CPU ≥ 70%, are invalid.

## 5. Gate and blocker traceability

### 5.1 §26 ADR-004 decision gates

| §26 gate | Threshold | Suite | Oracle (how the verdict is computed) | Tier |
|---|---|---|---|---|
| Simple-search p95 degradation during bulk load | ≤ 25% vs idle baseline | L9 `ScaleTests/Adr004/DegradationSimple` (k6 open model, fixed offered bulk rate) | p95(simple bucket, bulk-load window) ÷ p95(simple, idle baseline, same machine, same run set) − 1 ≤ 0.25, in **every** repetition; computed from raw HDR histograms by the gate evaluator (`E17-T08`) | T3 (trend in T2) |
| Complex-search p95 degradation during bulk load | ≤ 35% vs idle baseline | L9 `ScaleTests/Adr004/DegradationComplex` | Same as above on the complex class | T3 (trend in T2) |
| Search-index lag at sustained bulk load | ≤ 2 min | L9 `ScaleTests/Adr004/IndexLag` | `search.index_lag_seconds` = now − commit_ts of the oldest outbox/`IndexChunkTask` not yet reflected in the watermark, sampled at 1 s; **max** over the steady-state window ≤ 120 s (warm-up/drain excluded per documented rule). Absolute value on dev hardware; see §9 open point | T3 (trend in T2) |
| Interactive coding → searchable p95 | ≤ 1 s with bulk load active | L9 canary probe (`E17-T06`) | t0 = HTTP 200 from public coding API; t1 = marker first visible through public search API (same workspace-filtered path, ≤ 50 ms poll); p95(t1 − t0) under bulk load. Probe calibrated by a Toxiproxy 500 ms injection (p50 shifts 500 ± 50 ms) | T3 (trend in T2) |
| Correctness: 0 stale-version overwrites | 0 | L6 fault matrix + L9 load runs, with the shadow-ledger oracle (`E17-T07`); L3 version-monotonicity property | Continuous: sampled `os.projectionVersion ≤ pg.DocumentVersion` and never decreasing per doc. After quiescence: full scan, `os.projectionVersion == pg.DocumentVersion` and coding values equal for 100% of touched docs. `staleOverwrites = versionRegressions = missingDocs = valueMismatches = 0`. The oracle's own sensitivity is proved by detecting a deliberate unversioned write in 10/10 trials | T1 (smoke), T2, T3 |
| Security: 0 unauthorized protected-resource retrievals | 0 | L7 attack + stale-hit suite, also asserted inside every L6 fault trial and L9 run | Count of protected-content responses (view, native, image, snippet/highlight/metadata on a results page per Q-12, export inclusion per Q-15) returned to a principal without authoritative PG access = 0. Stale-hit scenario runs with index worker stopped / refresh disabled | T1, T2, T3 |
| Worker retry correctness: 100% idempotent | 100% | L6 fault matrix (`E18-T01`) + L3 idempotency property | Idempotent % = trials whose final PG + OpenSearch state matches the ledger oracle and the no-fault reference ÷ total trials = 100%; exactly one `CodingEvent` per logical change per idempotency key; every `JobChunk`/`IndexChunkTask` reaches a terminal state. One mismatch fails the gate; failures replay from the logged seed | T1 (smoke), T2 (full), T3 (per candidate) |
| Bulk coding/indexing throughput | Measure vs provisional ≥ 10K docs/s | L9 `ScaleTests/Adr004/BulkCeiling` | Docs committed in PG **and** reflected in the watermark per second, measured at the ceiling (separate from degradation runs, Q-04). Reported, not pass/fail; feeds "material advantage" | T3 |

### 5.2 §31 pre-coding architecture blockers

| §31 blocker | Suite | Oracle | Tier |
|---|---|---|---|
| 1. Document-level interactive outbox vs chunk-level `IndexChunkTask` | L4 `IntegrationTests/Indexing`; L2 rule "bulk paths never write `SearchOutbox`"; L6 | Interactive edit → exactly one `SearchOutbox` row in the same transaction; bulk job of N docs → ⌈N/chunk⌉ `IndexChunkTask` rows and 0 `SearchOutbox` rows; tasks carry no projection payload (schema check) | T1 |
| 2. ADR-004 Candidate A–D spike with frozen gates | L9 T3 run + L6 + L7 per candidate | `verdict.json` per candidate for every §26 gate (§5.1), gates file hash matches the signed-off hash; unfinished candidates reported "not evaluated" | T3 |
| 3. PostgreSQL current-state + provenance coding writes | L9 `ScaleTests/PgCoding` (`E18-T03`); L4 provenance tests | Every §27 metric reported with ≥ 3 repetitions; `count(CodingEvent)` = logical changes (no lost provenance) after each run, including Q-07 skips recorded as history | T3 (trend in T2) |
| 4. Deterministic PIT-vs-materialized snapshot semantics | L3 §22 decision table (all 16 predicate combinations) + snapshot-stability property; L4 PIT lifecycle | Decision equals the §22 table for every combination; materialized snapshot membership hash identical before/after concurrent coding, reindex and alias switch, and after restart | T1 (100 cases), T2 (10,000) |
| 5. Workspace/family/dedupe/thread/security fields in initial mapping | L4 `IntegrationTests/Search/Mapping` + L5 mapping snapshot | Live index mapping contains every §23 field with the expected type; mapping hash checked in; dynamic mapping is strict | T1 |
| 6. Authoritative security re-checks for protected-resource access | L7 (`E05-T05`); L2 endpoint inventory; L8 stale-hit scenario | 0 unauthorized retrievals; after privilege coding commit with OpenSearch refresh paused, view/native/image/export return 403 **in the same request**; results pages omit the hit (Q-12); security-projection lag p95 ≤ 5 s reported (Q-10) | T1, T2 |
| 7. Search-generation/watermark observability | L3 watermark property; L4 freshness API; L8 freshness banner | Watermark never advances past an unreflected task; generation is monotonic; banner shown while watermark < job generation and cleared after catch-up | T1, T2 |

## 6. Correctness invariants (L3)

Each invariant is a named FsCheck property; the model-based variant drives random command sequences (interactive edit, bulk job, retry, crash, reindex, alias switch, privilege change) against a reference in-memory model.

| Invariant | Statement | Baseline / decision |
|---|---|---|
| `ChunkIdempotency` | Applying any chunk N ≥ 1 times, in any interleaving with other chunks, yields the same PG and OpenSearch state as applying it once; exactly one `CodingEvent` per logical change per idempotency key | §2.4, §11, §21 |
| `VersionMonotonicity` | For any interleaving of interactive edits and retried bulk tasks, final `os.projectionVersion` = max committed `pg.DocumentVersion`, and the observed OpenSearch version per doc never decreases | §7, §21 |
| `SnapshotMembershipStability` | A materialized `DocumentSetSnapshot` returns the identical ID set (order-stable hash) before and after arbitrary concurrent coding, reindex, alias switch and worker restart | §22 |
| `SnapshotDecisionTable` | PIT is chosen iff short-lived ∧ no exact restart ∧ within PIT lifetime ∧ no legal reproducibility; exports/productions always materialize | §22 |
| `WatermarkSafety` | The search generation never advances past an unreflected outbox/chunk task | §28 |
| `WorkspaceIsolation` | For random multi-workspace corpora and queries, no hit, count, aggregation bucket, cursor or snapshot crosses workspaces | §3 principle 3, §23 |
| `BulkSkipsLaterEdits` (Q-07) | For every field a bulk job writes, a doc whose value for that field was changed by anyone else after the job's start watermark is left unchanged; the skip appears in coding history and in the job's skipped count/list; skipped + applied + excluded = target count | Q-07, Q-14 |
| `BatesContinuity` | A production's Bates numbers are unique per matter+prefix, gap-free and monotonic in production sort order, keep family adjacency, are never reused, and are identical after a production-worker crash/restart; a re-run matches the stored manifest checksums | §14, Q-08, `E12-T03` |
| `ExportRecheck` (Q-15) | An export never contains a doc the requester cannot access at chunk execution time; every exclusion is reported and audited | §24, Q-15 |

Case counts: ≥ 100 per property on PR, ≥ 10,000 nightly. Mutation check: a test-build flag that issues an unversioned OpenSearch write must be caught by `VersionMonotonicity` within the nightly budget. If it is not caught, the property is defective and the nightly run fails.

## 7. Test data policy

- **Synthetic only.** All corpora come from `tools/Opportunity.DataGenerator` (`E17-T01`/`T02`) with a recorded `--seed`, profile file and generator version (`corpus-manifest.json`). Outputs are byte-identical for the same seed + profile on any machine and thread count.
- **Never real client data**, in any environment, fixture, bug reproduction or benchmark. That includes "anonymized" or "redacted" real data. Customer-reported bugs are reproduced by writing a generator profile or a hand-written minimal fixture that has the same *shape*.
- Hand-written fixtures (e.g. defective DAT files, Unicode edge cases) live beside their tests. They are invented content: no real names, email addresses or matter details.
- Ground truth (planted needle terms, proximity pairs, defect lists, STR and production-QC known answers, `E17-T09`) is generated with the corpus. Recall and count assertions use it rather than hard-coded numbers.
- Large corpora are cached as versioned artifacts keyed by `(generatorVersion, profileHash, seed, schemaVersion, mappingHash)` (`E17-T05`). A schema or mapping change automatically invalidates the cache.
- Per Q-01, the Lite profile used by L8 is evaluation-only. Test docs must not suggest loading real data into it.

## 8. Flake policy

- **No quarantining to get green.** A failing or intermittent test is never skipped, `[Trait]`-excluded, retried-until-pass or moved to a later tier just to unblock a merge. CI retries are disabled for L1–L7. L8 may capture a retry *only as a diagnostic artifact*; the first failure still fails the run.
- **Root cause required.** Every flake gets an issue labelled `flaky` that includes the seed, logs and traces. The fix PR states the root cause: product race, test isolation, timing assumption, or environment. "Increased timeout" alone is not a root cause.
- **Product bug until proven otherwise.** In a system built on retries, duplicates and eventual consistency, an intermittent failure is assumed to be a real concurrency bug until analysis shows it is a test defect.
- **Owner and SLA.** The owning role of the layer (§2) triages within 1 working day. If it is unresolved after 5 working days, it is escalated to the QA Lead and architect at the next planning meeting. The only temporary relief allowed is reverting the change that introduced the flake.
- **Targets:** L8 flake rate < 1% over 50 consecutive nightly runs. Fixture parallelism is verified by running L4 four times in parallel with zero failures (`E03-T02`).
- Timing assertions use oracles and watermarks (wait-until-condition with a bounded deadline), never fixed sleeps.

## 9. Sign-off

This strategy needs approval from the **lead architect** and the **product owner** before `E03-T02` onward treat it as normative (E03-T01 acceptance criterion). Changes after approval go through PR review with the same approvers. Gate thresholds may only be tightened (Q-04).

Points for explicit confirmation at sign-off:

1. **Absolute-threshold gates on developer hardware.** Under Q-03, the index-lag (≤ 2 min) and coding→searchable (≤ 1 s) gates are absolute numbers measured on non-reference hardware. Proposal: they are evaluated and reported. A miss is disqualifying only if the candidate also loses the relative comparison against other candidates on the same machine. Otherwise it is flagged for re-measurement on sponsored hardware (M4). Record the outcome in `gates.yaml`. **Confirmed by the product owner as decision Q-44 (2026-10-02).**
2. **Weekly 1M slot** runs on the same runner as nightly (100K), within the 3 h budget, if cache restore allows it (§3).
3. **T4 (10M)** stays deferred until hardware is sponsored (Q-03). Any claim about 10M+ behavior is labelled "unproven" (§30) until then.

| Role | Name | Decision | Date |
|---|---|---|---|
| Lead architect | | ☐ Approved ☐ Changes requested | |
| Product owner | plogramer | Item 1 approved (Q-44); full sign-off pending | 2026-10-02 |
| QA Lead (author) | | Submitted | 2026-10-02 |

## 10. Container fixture library (`tests/Opportunity.Testing`, `E03-T02`)

- **Fixtures.** `PostgresFixture`, `OpenSearchFixture`, `RabbitMqFixture`, `ObjectStoreFixture`, used as xUnit v3 collection fixtures (collection definitions live in the test assembly, e.g. `tests/Opportunity.IntegrationTests/Containers/ContainerCollections.cs`). Each fixture starts its dependency plus a Toxiproxy container on a private Docker network, once per collection, and removes them when the collection ends (Ryuk cleans up after crashed runs).
- **Per-test isolation.** PostgreSQL: `CreateDatabaseAsync()` clones `opportunity_template` (override `SeedTemplateAsync` to migrate the template once). OpenSearch: `CreateIndexScope()` reserves a unique index prefix and deletes `prefix*` on dispose. RabbitMQ: `CreateVirtualHostAsync()` creates a vhost via the management API. Object store: `CreateBucketAsync()`. All are `IAsyncDisposable`; dispose drops the resource.
- **Fault injection.** `CreateFaultProxyAsync()` returns a dedicated Toxiproxy proxy in front of the dependency (pool of 16 per fixture), so faults never leak into parallel tests. It supports `AddLatencyAsync`, `AddTimeoutAsync`, `AddResetPeerAsync` (each returns a disposable toxic) and `CutAsync`/`RestoreAsync`.
- **Images.** Read at runtime from `versions.env` as `repo:TAG@DIGEST` (`POSTGRES`, `OPENSEARCH`, `RABBITMQ`, `SEAWEEDFS`, `TOXIPROXY` plus `*_DIGEST`). `OPPORTUNITY_TEST_IMAGE_<KEY>` overrides one reference for registry mirrors or air-gapped runners. The S3-compatible test store is SeaweedFS (Apache-2.0, per Q-38). The bundled production store is still chosen by `E19`'s ADR.
- **Settings.** OpenSearch runs single-node with the security plugin disabled, a 512 MB heap and wildcard deletes allowed. PostgreSQL runs with `fsync`/`synchronous_commit`/`full_page_writes` off. These are test-only settings.
- **Runtime.** The four collections start in parallel. On a 4-vCPU / 16 GB runner with images already pulled, the fixture suite takes about 50 s of wall clock, dominated by OpenSearch start-up (about 40 s). A cold image pull adds about 1–2 min (about 4 GB, mostly OpenSearch).
