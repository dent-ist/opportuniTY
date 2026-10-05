# ADR-002: Bulk snapshot semantics — numeric PIT policy and materialized DocumentSetSnapshot

| Field | Value |
|---|---|
| **Status** | **Accepted** for §1–§5 and §7–§9 (rule, numeric policy, decision tables, materialization procedure, generation semantics, interactive cursors, reproducibility scope); implemented by `E10-T03` (#91), see §10. **Proposed — interim benchmark recorded, pending reference hardware** for §6 (membership representation): interim choice *PG member pages* confirmed at CI scale (§6.1); the decision is finalized by the run on reference hardware (Q-70). |
| **Date** | 2026-10-02 |
| **Owner (role)** | Search (OpenSearch) |
| **Deciders** | Lead architect; contributing: Backend, UI/UX; product owner (Q-08, Q-30, Q-33) |
| **Tracking issue** | #29 (plan key `E02-T03`) |
| **Baseline sections** | §10 (superseded in part), §14, §22, §28, §32 |
| **Related** | ADR-001 (version model, watermark), ADR-010 (chunk membership, Q-07), ADR-011, ADR-012, ADR-014; findings A-14, §4.1–§4.5; Q-07, Q-08, Q-10, Q-12, Q-14, Q-15, Q-30, Q-32, Q-33 |

## Context

§22 states a deterministic rule — PIT + `search_after` when a job is short-lived, needs no exact restart and runs
"safely within PIT lifetime"; otherwise a materialized `DocumentSetSnapshot` — but gives no numbers (backend
finding 18). §32 requires restartable bulk targets, so under §22 bulk tagging is materialized and PIT is barely used
in the MVP. Family/duplicate expansion timing, the meaning of `SearchGeneration` on a snapshot (§10), interactive
review cursors that outlive a PIT (UI finding 4, Q-33) and the scope of reproducibility (legal finding 9, A-14, Q-08)
are undefined. ADR-010 additionally needs a per-member baseline version to enforce Q-07.

An OpenSearch PIT pins segments: a long-lived PIT blocks merge reclamation and costs disk and heap, and every node has
a hard cap (`search.max_open_pit_context`, default 300). A PIT is also lost on node restart and when its index is
deleted after an alias switch.

## Decision

### 1. Scope

The §22 rule applies to every **job** (server-side operation over a document set). Interactive grid and review
cursors are not jobs; they follow §8. Snapshot materialization itself pages search results under a PIT with its own
budget (§5).

### 2. Predicates and numeric PIT policy

A job uses PIT **iff all four predicates are false**:

| Predicate | True when |
|---|---|
| **L** long-running | `T_est × SafetyFactor > Pit.JobMaxAge` |
| **R** exact restart required | the job mutates authoritative state, or must resume after a crash without redoing finished work |
| **A** must survive alias/index changes | the job may outlive `Pit.JobMaxAge`, or its membership is referenced after the job ends |
| **X** legal reproducibility required | the membership is evidence (export, production, privilege log, saved/exported search-term report) |

Estimate: `T_est = T_setup + N_est / R_type`, where `N_est = hitCount × ExpansionFactor`. `hitCount` comes from one
count request (`track_total_hits = true`). `ExpansionFactor` is `1.0` without expansion, otherwise the expanded/hit
ratio measured on a random sample of 1,000 hits (default `4.0` before a sample exists). `R_type` is the 10th-percentile
throughput of the last 20 completed runs of that job type in the installation once ≥ 5 runs exist, otherwise the
default below.

| Setting (`Snapshots:*`, validated on start) | Default |
|---|---|
| `Pit.JobMaxAge` | 600 s |
| `Pit.SafetyFactor` | 3 → **short-lived ⇔ `T_est` ≤ 200 s** |
| `Pit.JobKeepAlive` (renewed on every page) | 2 min |
| `Pit.MaxOpenPerWorkspace` (jobs) / installation cap | 10 / 50% of `search.max_open_pit_context` |
| `T_setup` | 5 s |
| `R_type` default — ID paging | 20,000 docs/s |
| `R_type` default — search-term report preview | 0.5 terms/s (`N_est` = term count) |
| `Materialization.PitMaxAge` / keep-alive | 60 min / 2 min |
| `Materialization.Slices` | `min(8, ceil(N_est / 1,000,000))` (sliced PIT) |
| `Materialization.MaxEstimate` | 45 min (75% of `PitMaxAge`) → larger targets are rejected with "narrow the selection" |
| Unconfirmed snapshot lifetime | 24 h |

A PIT job whose PIT expires or is lost restarts once from the beginning with a new PIT (legal because R is false), then
fails with "selection changed too fast — run as a saved report".

### 3. Decision table — all 16 predicate combinations

| # | L | R | A | X | Strategy | Governing predicate(s) |
|---|---|---|---|---|---|---|
| 1 | F | F | F | F | **PIT + `search_after`** | none |
| 2 | F | F | F | T | Materialized | X |
| 3 | F | F | T | F | Materialized | A |
| 4 | F | F | T | T | Materialized | A, X |
| 5 | F | T | F | F | Materialized | R |
| 6 | F | T | F | T | Materialized | R, X |
| 7 | F | T | T | F | Materialized | R, A |
| 8 | F | T | T | T | Materialized | R, A, X |
| 9 | T | F | F | F | Materialized | L |
| 10 | T | F | F | T | Materialized | L, X |
| 11 | T | F | T | F | Materialized | L, A |
| 12 | T | F | T | T | Materialized | L, A, X |
| 13 | T | T | F | F | Materialized | L, R |
| 14 | T | T | F | T | Materialized | L, R, X |
| 15 | T | T | T | F | Materialized | L, R, A |
| 16 | T | T | T | T | Materialized | L, R, A, X |

`E10-T03` implements this as a pure function `Choose(L, R, A, X)` with a table-driven test over all 16 rows
(`Opportunity.Core.Snapshots.SnapshotStrategyRules`, `tests/Opportunity.UnitTests/Snapshots/SnapshotStrategyRulesTests.cs`).

### 4. Job types

| Job type | L | R | A | X | Strategy |
|---|---|---|---|---|---|
| Bulk tag / bulk code | computed | **T** (mutates; §32 restartable; Q-07 baselines) | F | F | Materialized |
| Apply-to-family > 1,000 documents (Q-14) | computed | T | F | F | Materialized |
| Export | computed | T | T | T | Materialized (§22) |
| Production | computed | T | T | T | Materialized + frozen production state (§9) |
| Privilege log | — | — | — | T | reuses the production's snapshot |
| Saved / exported search-term report (Q-30) | computed | F | T | T | Materialized scope; per-term hit IDs paged under per-term PITs and intersected with membership in PG; each term records its generation |
| Search-term report *preview* (on screen, unsaved) | computed | F | F | F | PIT if `T_est` ≤ 200 s, else Materialized (row 9) |
| Review batch (post-MVP, Q-33) | — | T | T | F | Materialized |
| Reindex | — | — | — | — | **no search snapshot**: PG `DocumentKeyRange` chunks (ADR-010 §4); a reindex must never select from the index it replaces |
| Interactive grid / review cursor | — | — | — | — | PIT under §8; never materialized in the MVP (Q-33) |

Consequence, stated plainly: in the MVP every job except the search-term report preview is materialized; PIT is used
for interactive cursors, for materialization paging and for previews.

### 5. Materialization procedure

1. **Select.** Read the visible watermark `G` (ADR-001 §7.3), open a (sliced) PIT on the serving `ProjectionGeneration`,
   page hits with `search_after` (sort `documentId`, `_source: false`, doc-value `documentId` only) into an
   unlogged staging table. Hits pass the same mandatory workspace filter as every search (§23).
2. **Freeze** in one PostgreSQL `REPEATABLE READ` transaction:
   1. **expand** families (`FamilyId`), duplicates (`DuplicateGroupId`) and threads (`EmailThreadId`) as requested,
      from authoritative PG relationships — expansion happens **before** membership is fixed, never at execution;
   2. de-duplicate and **security-filter** for the initiator against PG state (Q-11/Q-12/Q-13), counting exclusions;
   3. **order** by `(FamilySortKey, FamilySequence, DocumentId)` so families are contiguous, and assign dense ordinals
      `1…N`;
   4. record per member `DocumentId`, `BaselineVersion` (current `DocumentVersion`, the Q-07 start point used by
      ADR-010 §8) and `InclusionReason` (`Hit`, `Family`, `Duplicate`, `Thread`);
   5. write membership (§6) plus SHA-256 per page and a root hash, and set the header (`DocumentCount`, counts per
      inclusion reason, `ExcludedNoAccess`, `SearchGeneration = G`, `ProjectionGeneration`, `QueryDefinition` with AST
      version, `MaterializationStrategy`, creator/time/purpose) to `Ready`.
3. The header is the only thing a job can reference, and it becomes visible only on commit: membership is published
   atomically or not at all. Losing the PIT discards the staging table and restarts once with a new PIT and a new `G`;
   a second failure fails the job in `Preparing` (ADR-010).
4. Snapshot creation precedes the bulk confirmation: the UI shows the **frozen** count, the expansion delta and the
   chosen strategy; an unconfirmed snapshot expires after 24 h.
5. Membership is immutable once `Ready`: later coding, reindex, alias switch or deletion of documents does not change
   it (deleted members are reported by the consuming job).

### 6. Membership representation (*Proposed — pending spike*)

| Option | Shape | Strengths | Weaknesses |
|---|---|---|---|
| (a) row per member | `SnapshotMember(WorkspaceId, SnapshotId, Ordinal, DocumentId, BaselineVersion, InclusionReason)` | simplest; directly joinable | ~100 B/member with PK index (~100 MB per 1M); 100M-member snapshots are 10 GB of WAL and heap |
| **(b) PG member pages** — *interim choice* | `SnapshotMemberPage(WorkspaceId, SnapshotId, PageNo, FirstOrdinal, Count, DocumentIds[], BaselineVersions[], InclusionReasons[], Sha256)`, 1,000 members per page | ~1,000× fewer rows; ~25–30 B/member; a chunk reads 1–2 rows; joinable via `unnest … WITH ORDINALITY`; per-page hashes are natural; immutable rows never bloat | arrays must be unnested for set-based SQL; page size fixed per snapshot |
| (c) immutable chunked manifests in object storage | `ws/{ws}/snapshots/{id}/pages/{n}.bin` (zstd) + `manifest.json` with per-page SHA-256; PG holds the header only | cheapest for PG; natural evidence artifact | not joinable: each chunk downloads and loads a page into a temp table; depends on object-store latency |
| (d) compressed bitmap per snapshot | roaring bitmap over integer document numbers | very compact | needs dense integer IDs (ADR-009); loses ordering and baseline versions |

**Interim decision: (b).** In addition, every snapshot with **X = true** (export, production, saved report) also writes
an (c)-style manifest as immutable evidence; it is never used for iteration.

`E10-T03` measures (a), (b) and (c) at 1M members (10M on the nightly profile where hardware allows): freeze time,
heap + index + TOAST bytes, WAL bytes, p95 time to resolve one 1,000-member chunk, cost of the set-based bulk-coding
`UPDATE` joined to membership, delete cost and autovacuum impact. Decision rule: keep (b); switch to (a) only if it is
within 20% of (b) on every metric (simplicity wins); use (c) for snapshots > 5M members if (b)'s freeze time
extrapolates beyond 10 min at 10M or exceeds 50 B/member. Fallback if the benchmark is not run: (b).

#### 6.1 Benchmark result — *interim, pending reference hardware* (2026-10-05, E10-T03)

No reference hardware exists yet (Q-70), so the benchmark was built as an opt-in test and run on the CI-class
development container. These numbers are **interim**; they are re-recorded on the frozen reference hardware (#149) at
1M and 10M members before §6 is accepted.

**Method.** `tests/Opportunity.IntegrationTests/Snapshots/SnapshotRepresentationBenchmark.cs`, run with
`OPPORTUNITY_SNAPSHOT_BENCHMARK=1 OPPORTUNITY_SNAPSHOT_BENCHMARK_MEMBERS=<n>` (optional
`OPPORTUNITY_SNAPSHOT_BENCHMARK_OUT=<file>` for the Markdown table, `OPPORTUNITY_SNAPSHOT_BENCHMARK_PGOPTIONS` for session
settings). One migrated PostgreSQL 17 database (Testcontainers, container defaults: `shared_buffers` 128 MB,
`work_mem` 4 MB), one workspace with `n` live documents (each its own family) and a `bench_coding` table standing in
for coding state. For each representation, over the same members in the §5 order:

- **(b) PG member pages** — the production store (`DocumentSetSnapshotStore`): candidates staged, then the timed
  `FreezeAsync` (stage join, liveness and baselines, authorization pass with an allow-all authorizer in batches of
  5,000, ordering, pages with SHA-256, root hash, header); chunks read through `ReadMembersAsync` as the RLS-bound app
  login; delete through retention (`ExpireAsync`).
- **(a) row per member** — a benchmark-only table `(workspace_id, snapshot_id, ordinal, document_id, baseline_version,
  inclusion_reason)` with PK `(workspace_id, snapshot_id, ordinal)`, filled by one `REPEATABLE READ` `INSERT … SELECT`
  with the same ordering plus the per-page hashes; chunks by ordinal range.
- **(c) object-store manifests** — 1,000-member binary pages (34 B/member), zlib (fastest), SHA-256 per page and a
  manifest, written to a local temporary directory (no object-store network latency); a chunk is read, decoded and
  loaded into a temp table by binary `COPY` before the `UPDATE` joins it.

Chunk timings are 200 (resolve) and 50 (bulk `UPDATE`) random, unaligned 1,000-member ranges. "VACUUM" is the manual
`VACUUM` of the membership table right after the delete (autovacuum proxy). Hardware: 4 vCPU Intel Xeon @ 2.1 GHz,
16 GB RAM, shared with other parallel test runs (timings are noisy; relative order is what matters).

| Members | Representation | Freeze | Stored | Bytes/member | WAL | Chunk p50 / p95 | Bulk `UPDATE` p95 | Delete | VACUUM |
|---|---|---|---|---|---|---|---|---|---|
| 100,000 | (a) row per member | 0.37 s | 15.3 MiB | 160.7 | 22.1 MiB | 0.8 / 1.4 ms | 11.3 ms | 32 ms | 13 ms |
| 100,000 | **(b) PG member pages** | 1.30 s | 1.7 MiB | 17.9 | 1.7 MiB | 3.1 / 4.5 ms | 11.9 ms | 21 ms | 4 ms |
| 100,000 | (c) manifests | 0.18 s | 1.8 MiB | 18.9 | n/a | 7.5 / 14.0 ms | 22.6 ms | 321 ms | n/a |
| 1,000,000 | (a) row per member | 5.41 s | 152.8 MiB | 160.3 | 220.8 MiB | 1.7 / 3.1 ms | 33.7 ms | 494 ms | 384 ms |
| 1,000,000 | **(b) PG member pages** | 44.37 s | 16.4 MiB | 17.2 | 19.0 MiB | 3.6 / 8.4 ms | 35.2 ms | 1,124 ms | 24 ms |
| 1,000,000 | (c) manifests | 2.37 s | 18.1 MiB | 19.0 | n/a | 5.8 / 19.0 ms | 65.3 ms | 388 ms | n/a |

The 1M row is a single run on the same development container (it fits the shared disk only just); it is recorded
because the ticket asks for 1M, not as a reference-hardware result.

**Reading against the decision rule (interim).**

1. (a) is not within 20% of (b): it stores ~9× the bytes and writes ~11× the WAL (160 vs 17–18 B/member), and its
   delete + vacuum cost grows with it. (a) is faster to freeze and to resolve a chunk, but resolving a chunk from (b)
   stays in single-digit milliseconds at p95 and the bulk `UPDATE` joined to membership costs the same. **Keep (b).**
2. (b) stores 17–18 B/member, well under the 50 B/member threshold for (c).
3. (b)'s freeze grows faster than linearly on this container (1.3 s at 100k, 44 s at 1M; a linear extrapolation from
   1M gives 7.4 min at 10M, under the 10 min threshold, but the growth suggests more). The timed (b) freeze includes the
   staging join, the authorization pass and the audit insert that (a) and (c) skip, and at 1M its sorts and joins
   exceed the container's 4 MB `work_mem`. **Open item for the reference run:** profile the 1M freeze (`EXPLAIN
   ANALYZE` per statement, `work_mem` sized per the deployment guide) and run 10M; if (b) still extrapolates beyond
   10 min at 10M, apply (c) for snapshots above 5M members as §6 already provides. The freeze is several statements in one
   transaction and took 44 s in total at 1M here; the API data source's per-statement command timeout (Npgsql default
   30 s) must be checked against the largest accepted selection on reference hardware (the benchmark disables it).
4. (c) freezes fastest and stores as little as (b), but every chunk pays a read-decode-`COPY` round trip (p95 2–4× of
   (b), and object-store latency is not even included) and it is not joinable. It stays the evidence manifest for X =
   true snapshots and the candidate for very large snapshots only.

### 7. Meaning of `SearchGeneration`

1. `Snapshot.SearchGeneration = G`, the visible watermark read immediately **before** the PIT was opened. Meaning:
   the selection reflects every committed change with generation ≤ `G`; it may also reflect some later changes
   (lower bound, not exact). It is never compared with `DocumentVersion` (ADR-001 §2).
2. **PIT targets** (previews, interactive cursors): same definition, per PIT; a re-established PIT returns a new `G`.
3. **Materialized targets:** `G` and `ProjectionGeneration` are frozen provenance ("selected from an index current
   through `G`"); membership no longer depends on any index, so alias switches and later generations do not affect it.
4. If `isProjectionCurrent` was false at selection, the header records `SelectedWhileIndexing = true` and the UI says
   so. Privilege-review and production flows offer Q-10 "wait until the index is current": the PIT opens only once
   the watermark ≥ the workspace generation counter read when the user asked.
5. Reviewers see "selection as of <time>"; raw `G` is admin/support-only (Q-10).

### 8. Interactive grid and review cursors (Q-32, Q-33)

| Setting | Default |
|---|---|
| Keep-alive, renewed by each page request | 5 min |
| Maximum PIT age | 30 min |
| Open interactive PITs per user | 3 (oldest closed first) |
| Binding | cursor bound to (user, workspace), rejected otherwise (ADR-019 §2.8) |

Sorts always end with the `documentId` tie-breaker. When a PIT expires, reaches its maximum age or its index was
deleted, the API opens a new PIT with the same query and sort, resumes `search_after` from the last returned sort
values and returns `cursorReestablished: true` with the new `searchGeneration`; the UI shows "results refreshed"; if
the anchor document left the set it shows "Document no longer in results — continue from next". Totals above 10,000
are approximate (Q-32). An old index is retained after an alias switch for at least the maximum PIT age (ADR-001
§7.5).

### 9. Reproducibility scope (Q-08, A-14)

| Operation | Membership | Content |
|---|---|---|
| Bulk coding | exact (snapshot) | not applicable; applies to current state under Q-07 |
| Export | exact | as at execution: re-authorized per chunk (Q-15), recorded in the manifest with checksums; a re-run uses current state and reports differences |
| Production | exact | **exact as of finalization** (Q-08) |
| Saved search-term report | exact scope | the stored result is the record, with per-term generations |

Production finalization freezes, in one `REPEATABLE READ` transaction: the materialized snapshot (page and root
hashes), Bates assignments, the frozen specification (`E12-T02`), and per document a `ProductionDocumentState`:
`DocumentVersionAtFreeze`, `RedactionSetVersion` (append-only redaction versions, ADR-012), a **frozen copy of every
field value the specification uses** (load-file fields, endorsements, confidentiality designation, privilege
treatment, plus the highest `CodingEvent` id read) and the content hashes/object keys of natives, text and images
(content-addressed, ADR-011). Renderer and tool versions are recorded as image digests. The frozen state is written as
immutable chunked JSONL in object storage with hashes in PG. A re-run reads only frozen inputs, never current coding
or metadata, and must match the manifest checksums page for page. ADR-014 must retain every object referenced by a
finalized production for the life of the matter. A frozen copy is chosen over as-of reconstruction from history because
imported metadata and overlays are not event-sourced, and the copy's size is bounded by documents × fields used.

**Retention:** unconfirmed snapshots 24 h; job snapshots 90 days after the job ends unless referenced by an export,
production or saved report, which keeps them for the life of the matter (ADR-014).

### 10. Implementation (E10-T03, 2026-10-05)

**Rule engine.** `Opportunity.Core.Snapshots.SnapshotStrategyRules` is pure and deterministic:
`Choose(L, R, A, X)` is the §3 table; `FixedPredicates(SetOperationKind)` is the §4 job-type table (R, A, X per
operation); `EstimateRuntime` computes `T_est = T_setup + Count × ExpansionFactor / R_type`; `Decide(operation, estimate,
policy)` adds L when `T_est × SafetyFactor > JobMaxAge` **or when no estimate is given** (an unsized job never gets a
reader it might outlive). `PitPolicy` holds the §2 defaults and is configured as `Snapshots:Pit` (validated on start).
Interactive cursors always get a reader (§8). Exports and productions set R, A and X, so they are materialized for every
size and every policy (tested). `R_type` uses the policy defaults; the "10th percentile of the last 20 runs" refinement
waits for job throughput history (it changes only L, which never changes the strategy of any operation built so far).

**Callers.** `DocumentSetSnapshotService` serves only operations the rule always materializes
(`AlwaysMaterialized(OperationFor(purpose))`) and records the decision in its `Search.Executed` audit details
(`strategyRule`, e.g. `Materialized (R, A, X; T_est 12.5 s)`); a saved search frozen as `savedsearch:<id>` goes the same
way. Bulk coding and export accept a snapshot only through `AcceptsSnapshot(operation, purpose)`. The interactive search
service asks the rule for `InteractiveCursor` (always a reader). The search-term report preview — the only job that may
run under a reader — is not built yet (#72); it must call `Decide` with its term count.

**Interactive reader lifecycle (§8).** Settings `OpenSearch:Search:PointInTimeKeepAlive` (5 min, renewed by every page),
`PointInTimeMaxAge` (30 min) and `MaxOpenPointInTimesPerUser` (3). A page whose reader expired or was lost, aged out,
or was detached by the per-user cap opens a new reader with the same query and sort, resumes `search_after` from the
cursor's stored sort values, stores the new reader's open time and watermark (`search_session.pit_opened_at`,
`served_generation`; V0030) and answers `resultsRefreshed: true` with the new `freshness`; `Search.ResultsPageServed`
records `readerReestablished` = `expired` | `maxAge` | `detached`. Verified against real OpenSearch with a 3 s
keep-alive (`PointInTimeLifecycleTests`) and in the grid and Review-mode UI tests.

Deviations from §8 as written (lead confirmation requested):

- The response flag is the existing `resultsRefreshed` (E07-T05 contract the UI already uses), not a new
  `cursorReestablished`; the new generation is reported in `freshness`, not as a separate `searchGeneration`.
- The per-user cap counts readers per user **per workspace** (search sessions are workspace-owned under RLS); it is
  enforced when a new search opens a reader, so re-establishing a detached search's reader may briefly exceed it.
- The installation-level cap (50% of `search.max_open_pit_context`) and `Pit.MaxOpenPerWorkspace` for jobs are not
  enforced yet: no job pages under a reader in the MVP.

## Consequences

- **Positive:** the rule is a pure, testable function with concrete thresholds; every mutating job has restartable,
  immutable membership with Q-07 baselines; expansion is deterministic; generation semantics are honest (lower bound);
  productions are reproducible without depending on stored output.
- **Negative / costs:** materialization adds a selection phase before every bulk job (≈ 50 s per 1M at default rates
  before parallel slices); the freeze transaction holds an old xmin during large freezes (measured in `E10-T03`);
  production freeze stores a copy of the used field values.
- **Follow-up work:** `E10-T02` (#90) snapshot service; `E10-T03` (#91) rule engine, PIT lifecycle and the §6
  benchmark (result recorded here with a dated note); `E09-T03` (#86) expansion; `E07-T05` (#67) cursor binding and
  re-establishment; `E07-T10` (#72) search-term reports; `E12-T02` (#101) frozen production specification;
  `E16-T02`/`E16-T03` "results refreshed" UX.
- **Verification:** 16-row decision-table test; membership identical before and after coding, reindex and alias switch;
  chunk iteration restartable by ordinal with no gaps or overlaps; PIT-expiry test re-establishing a cursor; production
  re-run byte-identical to the manifest.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| PIT for bulk coding below a size threshold | Bulk coding mutates state (R true) and needs Q-07 baselines; restartability cannot depend on size |
| Expansion at chunk execution | Membership would depend on relationship edits made during the job; not reproducible |
| Materialize directly from PG queries | The selection language is the search language; PG cannot evaluate full-text queries |
| `SearchGeneration` as an exact point | Visibility of work above the watermark is not tracked per document; claiming exactness would be false |
| Light materialized "review snapshot" per reviewer session | Rejected for the MVP by Q-33 (live PIT + notice); review batches cover it post-MVP |
| As-of reconstruction of coding/metadata from history for productions | Metadata overlays are not versioned per field; a frozen copy is simpler and exact |

## Baseline amendments

All *Proposed* until lead-architect and product-owner sign-off; the baseline file is not edited by this ADR.

| ID | Amendment | Finding |
|---|---|---|
| B-1 | §22: predicates L/R/A/X with the numeric policy of §2 and the 16-row table of §3; "safely within PIT lifetime" ⇔ `T_est × 3 ≤ 600 s` | §4.1, §4.2 |
| B-2 | §10/§22: expansion before materialization; members carry `BaselineVersion` | §4.3 |
| B-3 | §10: `SearchGeneration` on a snapshot = visible watermark before PIT open (lower bound) | A-09, §4.5 |
| B-4 | §22: interactive cursors use PIT with re-establishment and notice (Q-33) | §4.4 |
| B-5 | §14/§22: production reproducibility = membership + frozen production state (Q-08) | A-14 |

## Links

- Baseline: [§10, §14, §22, §28, §32](../architecture/architecture-baseline.md)
- Review findings: [review-findings.md](../plan/review-findings.md) A-14, §4.1–§4.5;
  [backend review](../plan/reviews/backend.md) finding 18; [UI/UX review](../plan/reviews/ui-ux.md) findings 2, 4, 11
- Decisions: [decisions.md](../plan/decisions.md) Q-07, Q-08, Q-10, Q-12, Q-14, Q-15, Q-30, Q-32, Q-33
- Companion ADRs: [ADR-001](0001-search-consistency-outbox-and-version-model.md),
  [ADR-010](0010-job-chunk-idempotency-semantics.md)
