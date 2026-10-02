# ADR-005: PostgreSQL partitioning (interim position)

| Field | Value |
|---|---|
| **Status** | Proposed — interim position binding for M1–M3. Final decision is **Spike pending** in #155 (`E18-T08`) |
| **Date** | 2026-10-02 |
| **Owner (role)** | Data (PostgreSQL) |
| **Deciders** | Lead architect, product owner (Q-03 schedule); contributing: Performance, Security & Compliance |
| **Tracking issue** | #30 (plan key `E02-T04`); final: #155 (`E18-T08`) |
| **Baseline sections** | [§6](../architecture/architecture-baseline.md#6-metadata-coding-and-postgresql-partitioning), §15, §27, §29, [§33](../architecture/architecture-baseline.md#33-items-safe-to-defer-past-the-1m-benchmark) |
| **Related** | ADR-003, ADR-004a, ADR-013 (audit), ADR-014 (deletion), ADR-019; review finding A-10; backend findings 13, 14; Q-02, Q-03, Q-04, Q-43 |

## Context

§6 asks for hash-by-workspace, dedicated partitions for huge workspaces and hybrids to be benchmarked on coding
updates, bulk coding, workspace deletion, vacuum/bloat, index size and concurrent review **before** the partition
design is fixed. §33 allows the final choice to wait until after the 1M benchmark. The schema is needed now (`E04-T01`,
`E04-T02`), and backend findings 13–14 warn that `WorkspaceId`-leading keys are very costly to retrofit.

**Q-03 changes the timeline.** 10M validation (M4, where `E18-T08` sits) is postponed until a sponsor provides
hardware, so the interim schema may run in production installations for a long time, possibly past 1.0. It therefore
cannot be a throwaway shape. It must (a) be safe at the scale real installations reach first (Q-02: ≤ 1,000
workspaces, most under 1M documents), and (b) turn into any benchmark winner without a table rewrite under a
maintenance window, because Q-43 promises zero-downtime-capable migrations after 1.0.

## Decision

### 1. Interim position

1. **R1** Tenant tables (`Document`, `Page`, coding current-state, field values, family/duplicate groups, snapshot
   membership) are **not partitioned** in M1–M3. Each is one ordinary heap table shared by all workspaces, with
   isolation enforced by RLS (`E05-T03`).
2. **R2** Append-only, time-ordered tables **are** range-partitioned by time from day one. Partitioning there is about
   retention, not tenancy, and does not depend on this spike: `AuditEvent` monthly (ADR-013), `SearchOutbox` and
   `IndexChunkTask` daily (dropped after retention, ADR-001). `CodingEvent` partitioning is decided by ADR-004a. Until
   then it follows the R3 rules and is range-partitioned monthly on `CreatedAt`.
3. **R3** All tenant DDL is **partition-ready**. These rules are binding now:

| # | Rule | Why |
|---|---|---|
| P1 | Every tenant table's PK leads with `WorkspaceId`: `(WorkspaceId, DocumentId)`, `(WorkspaceId, FieldId)`, … | PostgreSQL requires the partition key in every PK and unique constraint |
| P2 | Every UNIQUE constraint and unique index includes `WorkspaceId` (e.g. `(WorkspaceId, ControlNumberNorm)`) | Same constraint; global uniqueness cannot be enforced across partitions |
| P3 | Every FK between tenant tables is composite `(WorkspaceId, X)` → `(WorkspaceId, X)` | Partition-wise joins, cross-workspace reference impossible (A-10) |
| P4 | Every secondary index on a tenant table leads with `WorkspaceId` | Pruning-friendly; identical shape after partitioning |
| P5 | Tenant row ids are application-generated UUIDv7. Per-workspace counters (FieldId, generation) live in a counter row keyed by `WorkspaceId`. No `serial`/`identity` on tenant tables | Sequences are per relation and do not survive moving rows between partitions |
| P6 | Every query on a tenant table has an equality predicate on `WorkspaceId` (RLS adds it; repositories also pass it explicitly so the planner can prune) | Partition pruning needs the literal predicate, and the RLS qual alone is not always pushed into pruning |
| P7 | No table inheritance, rules, row triggers that rely on `tableoid`, or statement-level triggers with transition tables on tenant tables | Not supported or behaves differently on partitioned tables |
| P8 | `fillfactor` and autovacuum parameters are set per table in migrations (initial: `Document` fillfactor 90, coding current-state 80; `autovacuum_vacuum_scale_factor = 0.02`, `autovacuum_analyze_scale_factor = 0.01`) | Defaults (20%) let a 100M-row table accumulate 20M dead tuples before vacuum |
| P9 | Workspace data removal goes through one port, `IWorkspaceDataPurger` (ADR-014). The interim implementation runs chunked `DELETE … WHERE WorkspaceId = $1 AND <key range>` (≤ 10,000 rows per transaction) as a job | A partition `DETACH`/`DROP` implementation can replace it without touching callers |
| P10 | No cross-workspace hot-path query. Installation-wide reports read from aggregates or iterate workspaces | Keeps every hot query single-partition |

A lint step in the migrator (`E04-T01`) enforces P1–P5 and P7. It parses the migration catalog after applying it and
fails the build on violations. P6 is covered by an integration test that runs the repository suite with
`auto_explain` and asserts a `WorkspaceId` predicate in every plan.

### 2. Preferred trajectory (no-regret shape)

The interim single table is designed to be **step zero of a LIST-by-workspace hybrid**, so the benchmark can move it
forward without a rewrite:

1. Create a parent `… PARTITION BY LIST (WorkspaceId)` with the same columns, then
   `ALTER TABLE parent ATTACH PARTITION <existing table> DEFAULT`. The existing heap becomes the default partition
   with no data copy. P1–P4 guarantee that its constraints and indexes are valid partition definitions.
2. Workspaces above the dedicated threshold (initially aligned with ADR-006: ≥ 5M documents) move one at a time into
   their own LIST partition. The move copies the rows in chunks while the workspace is `Locked` (writes paused, reads
   served), detaches and deletes from the default, then attaches the new partition. Each move is an ordinary job with
   a bounded per-workspace pause.
3. New workspaces declared "large" at creation get a dedicated partition before their first import, so no rows ever
   need to move.

If the benchmark instead selects **hash-by-workspace for the default bucket**, the default partition is itself
re-partitioned by `HASH (WorkspaceId)` using the same per-workspace copy procedure. P1–P10 hold either way.

### 3. Tripwires (act before the spike if hit)

Q-03 means M4 may be far away. The interim position is re-opened, and `E18-T08` gets priority on whatever hardware is
available, if any of the following is observed on a supported installation or in nightly benchmarks:

- one tenant table exceeds **200M rows** or **500 GB** (heap + indexes);
- purging a workspace takes **> 4 h** or produces dead-tuple bloat that autovacuum does not reclaim within 24 h;
- autovacuum on `Document` or coding current-state runs continuously for **> 6 h** while the dead-tuple ratio stays
  above 10%;
- p95 coding-write latency (relative measure per Q-03) degrades **> 50%** versus the same-machine baseline at equal
  load.

Below these values the single-table design is expected to be adequate; Q-02 (most workspaces < 1M) puts typical
installations far under them.

### 4. What the spike (#155, `E18-T08`) must measure

Candidates: (A) interim single table; (B) hash-by-workspace, 16 and 64 partitions; (C) LIST hybrid, a dedicated
partition for every workspace ≥ threshold plus a default; (D) one LIST partition per workspace (upper bound on
partition count: 1,000). All runs keep RLS enabled and record hardware and settings per §29. Gates are relative per
Q-03/Q-04.

| Metric | How | Gate input |
|---|---|---|
| Interactive coding update | p50/p95/p99 single-document coding write under the §29 concurrent-review mix | Relative to (A) |
| Bulk coding throughput | Chunked bulk coding at 1M (and 10M when hardware exists): rows/s, WAL bytes per row, chunk commit p95 | Q-04 material-advantage rule |
| Workspace deletion | Wall time, WAL, lock waits and replica lag to remove a 1M (and 5M) workspace while others are under load | Hard: must not block other workspaces' writes > 1 s |
| Vacuum and bloat | Dead tuples, autovacuum duration and frequency, heap/index bloat % after 3 bulk-coding passes | Relative to (A) |
| Index size | Total index bytes per 1M documents, per candidate | Informational, + disk-trend rule (Q-04) |
| Concurrent review | §29 query and read mix p95 with background bulk coding | Relative to (A) |
| Planning overhead | Planning time p95 for representative queries at the candidate's partition count | Must stay < 1 ms p95 |
| Partition move | Time and pause for moving one 5M workspace from default into a dedicated partition; time to attach a new list partition next to a large default | Feasibility of §2 trajectory |
| Catalog cost | Relations, memory per backend, `pg_dump`/restore time | Informational |

The ADR becomes Accepted when `E18-T08` records the results, picks a candidate and attaches a migration plan from the
interim schema. If the 10M phase is still unavailable, a decision on 1M relative results may be taken explicitly by
the lead architect and product owner, with the 10M confirmation recorded as an open follow-up.

## Consequences

- **Positive:** the schema is simple to build and operate now. RLS and `WorkspaceId`-leading keys are in place before
  any data exists. The no-regret trajectory needs no table rewrite. Time-based tables get cheap retention drops from
  day one.
- **Negative / costs:** until partitioning lands, workspace deletion is a chunked DELETE with vacuum cost, and one
  large table carries every workspace's bloat. Per-table autovacuum tuning is mandatory operational configuration.
- **Follow-up work:** `E04-T01` (lint rules P1–P5/P7), `E04-T02`, `E05-T03` (RLS), `E20-T02` (purger), `E18-T08`
  (spike and final decision), `E19-T04` (tripwire metrics as alerts).
- **Verification:** migrator lint, the P6 plan test, and tripwire metrics exported via OpenTelemetry (`E19-T04`).

## Alternatives considered

| Option | Why not chosen (now) |
|---|---|
| Hash-by-workspace from day one | Adds planning and operational overhead before any measurement. Does not make workspace deletion cheaper (still a DELETE inside a shared hash partition), which is the main benefit partitioning should buy. |
| One partition per workspace from day one | 1,000 workspaces × ~8 tenant tables = thousands of relations; planning and catalog cost unmeasured; small workspaces gain nothing. |
| Non-partitioned without partition-ready rules | Retrofitting `WorkspaceId`-leading PKs and composite FKs on a 100M-row table is a full rewrite (backend finding 13). |
| Schema-per-workspace | Per-workspace DDL and migrations; conflicts with SQL-first single migration stream and RLS model. |

## Baseline amendments

- *Proposed:* §6 — add "until ADR-005 is accepted, tenant tables are non-partitioned but partition-ready (rules
  P1–P10); time-ordered append-only tables are range-partitioned by time from day one."
- *Proposed:* §33 — note that the final partitioning decision may be taken on 1M relative results if 10M hardware is
  unavailable (Q-03), with 10M confirmation as a follow-up.

## Links

- Baseline: §6, §15, §27, §29, §33
- Review findings: [review-findings.md](../plan/review-findings.md) A-10, §3.7
- Decisions: [decisions.md](../plan/decisions.md) Q-02, Q-03, Q-04, Q-43
- Spike: #155 (`E18-T08`)
