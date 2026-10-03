# E18 — Architecture Spikes & Scale Validation

**Labels:** `epic`, `role:qa`, `role:performance`, `role:data`, `role:search`, `role:devops`, `P0`  
**Starts in:** M2 - 1M Benchmark & Architecture Blockers  
**Tickets:** 9

## Goal
Run the timeboxed ADR-004 Candidate A–D and PostgreSQL coding spikes against frozen gates at 1M, record the decision, then validate at 10M, finalize partitioning and prove recovery.

## Baseline sections
§6, §25, §26, §27, §29, §30, §31.2, §31.3, §33

## Scope / out of scope
**In scope**
- Fault-injection harness and idempotency matrix
- Methodology freeze
- PG coding spike
- ADR-004 comparative run and decision
- Nightly regression guard
- 10M validation
- Partitioning benchmark
- Recovery/RPO/RTO drill

**Out of scope**
- 100M validation (future)

## Contributing roles
- **Roles:** QA, Performance, Data (PostgreSQL), Search (OpenSearch), DevOps / SRE
- **Source reviews:** QA & Performance, Backend/Architecture, Security & Compliance, UI/UX, DevOps/SRE
- **Milestones spanned:** M2 - 1M Benchmark & Architecture Blockers, M3 - MVP Feature Complete, M4 - 10M Validation

## Exit criteria
- [ ] ADR-004a/004b accepted with correctness/security gates evaluated first
- [ ] 10M results published; any material regression blocks the 100M roadmap
- [ ] RPO/RTO measured against §17 with 0 ledger mismatches post-recovery

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E18-T01](#e18-t01) | Build fault-injection harness and idempotency matrix | M2 | L | E03-T02, E17-T07, E06-T05, E07-T04, E10-T04 |
| [E18-T02](#e18-t02) | Freeze reference hardware, gates and methodology | M2 | S | E17-T08, E17-T04, E17-T05 |
| [E18-T03](#e18-t03) | Run PostgreSQL coding-model spike | M2 | L | E18-T02, E04-T04, E05-T03 |
| [E18-T04](#e18-t04) | Run ADR-004 1M comparative spike for Candidates A–D | M2 | L | E18-T02, E18-T01, E05-T05, E07-T07 |
| [E18-T05](#e18-t05) | Decide ADR-004a/004b and update performance status | M2 | S | E18-T04, E18-T03 |
| [E18-T06](#e18-t06) | Run nightly scale-regression workflow with performance regression guard | M3 | M | E18-T05, E17-T05, E01-T03 |
| [E18-T07](#e18-t07) | Validate the winning design at 10M | M4 | L | E18-T05, E18-T06 |
| [E18-T08](#e18-t08) | Benchmark PostgreSQL partitioning and finalize ADR-005 | M4 | M | E18-T03 |
| [E18-T09](#e18-t09) | Drill recovery and measure RPO/RTO at scale | M4 | M | E19-T08, E17-T07, E07-T11, E20-T02 |

---

### E18-T01

**Build fault-injection harness and idempotency matrix**  
Labels: `role:qa`, `role:performance`, `P0`, `size:L`, `spike` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§26 '100% idempotent in fault-injection suite' and '0 unauthorized retrievals'; QA finding 6: no fault catalogue or harness.

#### Description
Fault types: kill -9 before/after PG commit, before/after OpenSearch bulk ack, before RabbitMQ ack, mid-chunk; duplicate delivery; redelivery after ack timeout; out-of-order delivery; OpenSearch 429/503 and partial bulk failures; PG failover; RabbitMQ node restart; network partition (Toxiproxy). Code failpoints via `IFaultInjector` plus `docker kill`/pause. Seeded matrix (fault × point × workload) with the ledger oracle and authorization assertions as pass criteria.

#### Acceptance criteria
- [ ] Matrix covers ≥ 8 fault types × ≥ 5 failpoints, ≥ 5 seeded trials per cell nightly
- [ ] 'Idempotent %' = passing/total trials must be 100%; failures reproduce from the logged seed
- [ ] No duplicate CodingEvent per idempotency key; JobChunk/IndexChunkTask reach terminal states after recovery
- [ ] Runs against each ADR-004 candidate on demand

#### Dependencies
- `E03-T02` — Build shared Testcontainers fixture library with Toxiproxy
- `E17-T07` — Build stale-version-overwrite shadow-ledger oracle
- `E06-T05` — Build idempotent consumer framework
- `E07-T04` — Build chunk index worker for IndexChunkTask
- `E10-T04` — Build bulk coding job and worker

#### Roles
- **Owner:** QA
- **Contributing:** Performance
- **Source reviews:** QA & Performance, Backend/Architecture, Security & Compliance
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / L

#### Notes
Merges QA T3.5 and backend 'Fault-injection correctness suite'.

---

### E18-T02

**Freeze reference hardware, gates and methodology**  
Labels: `role:performance`, `P0`, `size:S`, `spike` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§26: gates must be frozen before the comparative run; QA finding 12: degradation gates need a fixed offered bulk rate.

#### Description
Calibration on the reference environment: idle-baseline variance over 3 runs and max sustainable bulk rate of the weakest candidate (sets the fixed offered rate); freeze `gates.yaml`, query taxonomy, think-time parameters, durability settings and corpus profile with PO + architect sign-off.

#### Acceptance criteria
- [ ] Idle-baseline p95 coefficient of variation ≤ 5% across 3 runs (otherwise noise is investigated first)
- [ ] Signed-off methodology record is committed before the first comparative run

#### Dependencies
- `E17-T08` — Build gate evaluator and benchmark report generator
- `E17-T04` — Implement query taxonomy and k6 workload scripts
- `E17-T05` — Build fast-path loaders and corpus artifact caching

#### Roles
- **Owner:** Performance
- **Contributing:** —
- **Source reviews:** QA & Performance, Backend/Architecture
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / S

#### Notes
Q-03, Q-04, Q-05.

---

### E18-T03

**Run PostgreSQL coding-model spike**  
Labels: `role:data`, `role:performance`, `P0`, `size:L`, `spike` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§27, §31.3: benchmark current-state + append-only history before fixing coding storage.

#### Description
Variants: row-per-field vs choice arrays vs JSONB coding; fillfactor 70/90/100; chunk sizes 500/2K/10K; RLS on (overhead). Measure bulk INSERT/UPDATE throughput, WAL bytes/doc, HOT ratio, bloat (`pgstattuple`), autovacuum cycles, lock waits, history-write cost, rollback/retry cost, table/index growth, concurrent interactive coding p95 during bulk; 1M and 10M-row bulk jobs.

#### Acceptance criteria
- [ ] Results table covers every §27 metric with ≥ 3 repetitions
- [ ] Recommended chunk size (docs + bytes) is fed into ADR-010
- [ ] RLS overhead is recorded (mitigation ADR if > 10%)
- [ ] Recommendation is consistent with the ADR-004 outcome

#### Dependencies
- `E18-T02` — Freeze reference hardware, gates and methodology
- `E04-T04` — Create interim coding current-state and CodingEvent provenance tables
- `E05-T03` — Enforce PostgreSQL row-level security and composite tenant keys

#### Roles
- **Owner:** Data (PostgreSQL)
- **Contributing:** Performance
- **Source reviews:** Backend/Architecture, QA & Performance, Security & Compliance
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / L

#### Notes
Merges backend and QA PG coding spikes.

---

### E18-T04

**Run ADR-004 1M comparative spike for Candidates A–D**  
Labels: `role:search`, `role:performance`, `P0`, `size:L`, `spike` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§25, §26, §31.2. Backend findings 1 and 12: full reindex vs guarded `_update` must be separate variants; securityTags placement is an evaluation criterion. UI finding 3: grid sort/filter/facet on coding columns.

#### Description
Timeboxed (≈ one sprint) benchmark-grade projections: A (unified; coalescing and bulk-refresh-tuning variants; full reindex vs scripted guarded update), B (split coding index), C (hybrid), D (parent/child: `has_child`/`has_parent` latency, global-ordinal memory and build cost, routing). For each: idle, bulk-load, throughput-ceiling and fault-matrix scenarios at 1M; merge pressure; highlight cost on content+coding queries; security-filter placement; family queries; UX cases (sort by coding field, facet by issue tag, 'coded by me today').

#### Acceptance criteria
- [ ] Every candidate has a complete bundle (≥ 3 repetitions per scenario) and `verdict.json` for all §26 gates, or a documented disqualifying failure
- [ ] Correctness and security gates (oracle, fault matrix, attack suite) run against each candidate before throughput
- [ ] Spike code sits behind `IProjectionBuilder`; losing candidates are removed afterwards
- [ ] Unfinished candidates are marked 'not evaluated', not 'failed'

#### Dependencies
- `E18-T02` — Freeze reference hardware, gates and methodology
- `E18-T01` — Build fault-injection harness and idempotency matrix
- `E05-T05` — Build cross-workspace attack and stale-hit authorization suite as a CI gate
- `E07-T07` — Build search planner with field resolution and proximity

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** Performance
- **Source reviews:** Backend/Architecture, QA & Performance, UI/UX, Security & Compliance
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / L

#### Notes
Q-04.

---

### E18-T05

**Decide ADR-004a/004b and update performance status**  
Labels: `role:search`, `role:data`, `P0`, `size:S`, `adr`, `spike` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§26 decision rules; §30 status table; §31.2–31.3.

#### Description
Comparative report via the gate evaluator; recommendation per §26 (correctness first, then least operational complexity unless a material repeatable advantage) with an operational-complexity rubric; accept ADR-004a (PG coding storage) and ADR-004b (OpenSearch coding projection); migrate the interim projection to the winner; propose §30 updates.

#### Acceptance criteria
- [ ] Gate table filled with measured values for every candidate; bundles linked
- [ ] ADR-004a/004b status updated (decision or explicit re-run request)
- [ ] Proposed §30 status-table updates are recorded for owner approval

#### Dependencies
- `E18-T04` — Run ADR-004 1M comparative spike for Candidates A–D
- `E18-T03` — Run PostgreSQL coding-model spike

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** Data (PostgreSQL)
- **Source reviews:** Backend/Architecture, QA & Performance
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / S

---

### E18-T06

**Run nightly scale-regression workflow with performance regression guard**  
Labels: `role:devops`, `role:performance`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§18, §29; devops 'Nightly scale-regression workflow'; QA T4.6.

#### Description
Scheduled workflow on a self-hosted/large runner: developer-regression topology, cached corpus (100K nightly, 1M weekly), import → index → search → bulk tag → export, p50/p95/p99, index lag, environment metadata as JSON; comparison against a rolling baseline with automatic issue creation; 90-day trend dashboard.

#### Acceptance criteria
- [ ] Alerts on > 15% p95 regression in any bucket or any correctness counter > 0 sustained over 2 nights
- [ ] Each run stores hardware, versions, JVM, shard layout, seed and config
- [ ] Dashboard shows 90-day trends for search p95 by bucket, coding→searchable p95, index lag and bulk docs/s

#### Dependencies
- `E18-T05` — Decide ADR-004a/004b and update performance status
- `E17-T05` — Build fast-path loaders and corpus artifact caching
- `E01-T03` — Set up PR CI pipeline with real-dependency integration tests

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** Performance
- **Source reviews:** DevOps/SRE, QA & Performance, Backend/Architecture
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E18-T07

**Validate the winning design at 10M**  
Labels: `role:performance`, `role:search`, `P2`, `size:L`, `spike` · Milestone: M4 - 10M Validation

#### Context
§29 Phase 2: a design that passes 1M but fails materially at 10M is not accepted for the 100M roadmap.

#### Description
Enterprise reference workload at 10M with ≥ 4 h steady state and concurrent bulk coding: segment merges, disk amplification (on-disk / raw text), merge CPU/I/O, backpressure, p95/p99 drift (slope test), long-running materialized snapshots (1M+ members restarted mid-run), PG WAL/bloat/autovacuum; comparison with 1M for super-linear trends.

#### Acceptance criteria
- [ ] All §26 gates re-evaluated at 10M; material failure per Q-04 blocks the 100M roadmap and opens ADR revisions
- [ ] A restarted 1M-member snapshot completes with identical membership hash
- [ ] Report is published with linked bundles

#### Dependencies
- `E18-T05` — Decide ADR-004a/004b and update performance status
- `E18-T06` — Run nightly scale-regression workflow with performance regression guard

#### Roles
- **Owner:** Performance
- **Contributing:** Search (OpenSearch)
- **Source reviews:** QA & Performance, Backend/Architecture, DevOps/SRE
- **Milestone / phase / size:** M4 - 10M Validation / P2 / L

---

### E18-T08

**Benchmark PostgreSQL partitioning and finalize ADR-005**  
Labels: `role:data`, `role:performance`, `P2`, `size:M`, `adr`, `spike` · Milestone: M4 - 10M Validation

#### Context
§6, §33 (final partitioning deferred while candidates are benchmarked).

#### Description
Benchmark hash-by-workspace, dedicated partitions for huge workspaces and hybrid on coding updates, bulk coding, workspace deletion (partition drop), vacuum/bloat, index size and concurrent review, with RLS enabled; finalize ADR-005 and plan migration from interim DDL.

#### Acceptance criteria
- [ ] Results for each strategy cover all §6 metrics at 10M
- [ ] ADR-005 Accepted with a migration plan from the interim partition-ready schema

#### Dependencies
- `E18-T03` — Run PostgreSQL coding-model spike

#### Roles
- **Owner:** Data (PostgreSQL)
- **Contributing:** Performance
- **Source reviews:** Backend/Architecture, QA & Performance, Security & Compliance
- **Milestone / phase / size:** M4 - 10M Validation / P2 / M

---

### E18-T09

**Drill recovery and measure RPO/RTO at scale**  
Labels: `role:qa`, `role:devops`, `P2`, `size:M` · Milestone: M4 - 10M Validation

#### Context
§17 RPO ≤ 5 min / RTO ≤ 1 h; QA finding 13; devops: OpenSearch rebuild-from-PG cannot meet RTO at 10M+.

#### Description
On the reference profile: PG PITR + OpenSearch snapshot restore with outbox/chunk re-drive to consistency; full projection rebuild from PG via alias reindex; workspace deletion during active indexing.

#### Acceptance criteria
- [ ] Measured RTO and RPO reported against §17; ledger oracle shows 0 mismatches post-recovery
- [ ] Rebuild-from-PG projection hash equals the live projection hash
- [ ] Deletion leaves 0 residual docs in PG, OpenSearch and storage prefixes (scan)

#### Dependencies
- `E19-T08` — Implement PostgreSQL PITR, OpenSearch snapshots and artifact replication
- `E17-T07` — Build stale-version-overwrite shadow-ledger oracle
- `E07-T11` — Implement alias-based reindex and projection generation switch
- `E20-T02` — Delete workspaces defensibly with fencing and destruction certificate

#### Roles
- **Owner:** QA
- **Contributing:** DevOps / SRE
- **Source reviews:** QA & Performance, DevOps/SRE
- **Milestone / phase / size:** M4 - 10M Validation / P2 / M

#### Notes
Q-40.
