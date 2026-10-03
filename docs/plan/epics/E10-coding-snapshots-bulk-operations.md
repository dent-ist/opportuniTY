# E10 — Coding, Snapshots & Bulk Operations

**Labels:** `epic`, `role:backend`, `role:search`, `role:qa`, `role:data`, `role:ui`, `role:ediscovery`, `P0`  
**Starts in:** M1 - First Vertical Slice  
**Tickets:** 6

## Goal
Deliver interactive coding with low-latency searchability, deterministic frozen bulk targets, and chunked bulk coding that writes provenance and chunk-level index tasks, plus coding history and review-batch domain support.

## Baseline sections
§7, §10, §14, §21, §22, §24, §27, §28, §31.1, §31.4, §34

## Scope / out of scope
**In scope**
- Interactive coding API (If-Match, outbox, priority lane)
- Materialized DocumentSetSnapshot service
- Deterministic PIT-vs-materialized rule engine
- Bulk coding worker
- Coding history and review-batch domain
- Deferred batching/QC workflow

**Out of scope**
- Bulk undo/revert (open question Q-34)
- TAR/AI coding (§1 deferred)

## Contributing roles
- **Roles:** Backend, Search (OpenSearch), QA, Data (PostgreSQL), UI/UX, eDiscovery Practitioner
- **Source reviews:** Backend/Architecture, UI/UX, QA & Performance, Legal/Discovery Counsel, eDiscovery Practitioner, Security & Compliance
- **Milestones spanned:** M1 - First Vertical Slice, M2 - 1M Benchmark & Architecture Blockers, M3 - MVP Feature Complete, M5 - Post-MVP / Deferred

## Exit criteria
- [ ] A retried old bulk task never overwrites a newer interactive edit in PG or OpenSearch
- [ ] Materialized membership is identical before/after coding, reindex and alias switch
- [ ] 1M-document bulk tag completes with recorded throughput and WAL

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E10-T01](#e10-t01) | Implement interactive coding API with optimistic concurrency | M1 | M | E04-T04, E07-T03, E05-T04, E06-T04 |
| [E10-T02](#e10-t02) | Build materialized DocumentSetSnapshot service | M1 | L | E02-T03, E07-T05, E06-T02 |
| [E10-T03](#e10-t03) | Implement deterministic PIT-vs-materialized rule engine and PIT lifecycle | M2 | M | E10-T02 |
| [E10-T04](#e10-t04) | Build bulk coding job and worker | M1 | L | E10-T02, E06-T02, E07-T04, E04-T04 |
| [E10-T05](#e10-t05) | Expose coding history API and review-batch domain support | M3 | M | E04-T04, E10-T02 |
| [E10-T06](#e10-t06) | Deliver review batching and QC workflow | M5 | L | E10-T05, E07-T09, E09-T03 |

---

### E10-T01

**Implement interactive coding API with optimistic concurrency**  
Labels: `role:backend`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§7, §21 interactive path; §24 security-affecting coding. UI finding 6: read-your-own-writes needs DocumentVersion and indexing state in the response.

#### Description
`PUT /documents/{id}/coding` with `If-Match: DocumentVersion`. One transaction updates current coding, writes CodingEvent, increments DocumentVersion and inserts a SearchOutbox row (priority lane if any field is security-affecting). Response returns new DocumentVersion and indexing state; `GET` returns projected version so the UI can clear 'saved · indexing'.

#### Acceptance criteria
- [ ] Stale If-Match returns 412 with the current version and last editor; no lost updates under 100 concurrent reviewers on the same docs
- [ ] Code → visible in search p95 ≤ 1 s measured on the dev profile (recorded, not assumed)
- [ ] Security-affecting changes are enforced by the gateway immediately (§24 test with index worker paused)

#### Dependencies
- `E04-T04` — Create interim coding current-state and CodingEvent provenance tables
- `E07-T03` — Build version-safe interactive index worker
- `E05-T04` — Build protected-content gateway and authoritative access service
- `E06-T04` — Build outbox dispatcher service

#### Roles
- **Owner:** Backend
- **Contributing:** —
- **Source reviews:** Backend/Architecture, UI/UX, Security & Compliance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E10-T02

**Build materialized DocumentSetSnapshot service**  
Labels: `role:search`, `role:backend`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§10, §22: exports/productions and restartable bulk use materialized snapshots; §32 requires deterministic, restartable bulk membership.

#### Description
DocumentSetSnapshot (SnapshotId, WorkspaceId, QueryDefinition, SearchGeneration at creation, creator/time/purpose, DocumentCount, MaterializationStrategy). Materialization pages the search via PIT + `search_after` into ordered membership storage per ADR-002; selection input = explicit IDs or query + generation (never large client ID lists); API returns the frozen count for bulk confirmations.

#### Acceptance criteria
- [ ] Membership is identical before and after subsequent coding, reindex and alias switch (test)
- [ ] Chunk iteration over a snapshot is restartable by sequence with no gaps or overlaps
- [ ] Creating a snapshot returns frozen count and generation for UI confirmation

#### Dependencies
- `E02-T03` — Write ADR-002: bulk snapshot semantics with numeric PIT policy
- `E07-T05` — Build logical search service with mandatory workspace filter and cursor binding
- `E06-T02` — Implement Job and JobChunk state machine with leases

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** Backend
- **Source reviews:** Backend/Architecture, QA & Performance, UI/UX, Legal/Discovery Counsel
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

---

### E10-T03

**Implement deterministic PIT-vs-materialized rule engine and PIT lifecycle**  
Labels: `role:search`, `role:qa`, `P0`, `size:M` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§22, §31.4: deterministic rule; QA: decision-table test over 16 predicate combinations; UI: PIT expiry during review sessions.

#### Description
Rule engine selecting PIT vs Materialized from job type and estimated runtime per ADR-002; PIT keep-alive management and expiry handling for interactive cursors (re-establish + notify); 1M-member representation benchmark (creation time, storage, chunk-iteration cost) finalizing ADR-002.

#### Acceptance criteria
- [ ] Exports/productions are always materialized (§22)
- [ ] Decision-table test covers all 16 predicate combinations
- [ ] PIT expiry mid-session re-establishes the cursor and signals 'results refreshed'
- [ ] 1M-member benchmark result is recorded in ADR-002

#### Dependencies
- `E10-T02` — Build materialized DocumentSetSnapshot service

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** QA
- **Source reviews:** Backend/Architecture, QA & Performance, UI/UX
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / M

---

### E10-T04

**Build bulk coding job and worker**  
Labels: `role:backend`, `role:data`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§21 bulk path, §27 provenance must not be lost, §32 BULK TAG.

#### Description
`Opportunity.Worker.BulkCoding`: bulk job over a materialized snapshot → JobChunks. Per chunk, one PG transaction applies add/remove/set coding set-based (temp table/COPY), writes CodingEvents (ActorType BulkHuman), increments versions and creates one IndexChunkTask. Conflict rule with concurrent interactive edits per Q-07 (default last-commit-wins with provenance).

#### Acceptance criteria
- [ ] A 1M-document bulk tag completes; throughput, WAL volume and chunk duration are recorded
- [ ] Chunk replay produces identical final state and no duplicate CodingEvents
- [ ] Interactive edits during the bulk job are not overwritten in PG (per agreed rule) or in OpenSearch (§21 test)
- [ ] Job audit is one summary event plus chunk-level references, not per-document rows

#### Dependencies
- `E10-T02` — Build materialized DocumentSetSnapshot service
- `E06-T02` — Implement Job and JobChunk state machine with leases
- `E07-T04` — Build chunk index worker for IndexChunkTask
- `E04-T04` — Create interim coding current-state and CodingEvent provenance tables

#### Roles
- **Owner:** Backend
- **Contributing:** Data (PostgreSQL)
- **Source reviews:** Backend/Architecture, QA & Performance, Legal/Discovery Counsel
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

#### Notes
Q-07.

---

### E10-T05

**Expose coding history API and review-batch domain support**  
Labels: `role:backend`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§14, §34: domain/provenance support now, full workflow UI later.

#### Description
Coding history API (who/when/what/job/actor type per document) and domain entities for ReviewBatch, assignments, reviewer status and conflict detection, built on CodingEvent provenance; batches created from a snapshot (keep families together, optional threads, size N).

#### Acceptance criteria
- [ ] Coding history returns field, old/new value, actor, time and JobId or 'interactive'
- [ ] Batches can be created from a snapshot with keep-families-together and assigned to users
- [ ] Conflicting first-pass vs QC calls are detectable via API

#### Dependencies
- `E04-T04` — Create interim coding current-state and CodingEvent provenance tables
- `E10-T02` — Build materialized DocumentSetSnapshot service

#### Roles
- **Owner:** Backend
- **Contributing:** —
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner, UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E10-T06

**Deliver review batching and QC workflow**  
Labels: `role:ui`, `role:ediscovery`, `P2`, `size:L` · Milestone: M5 - Post-MVP / Deferred

#### Context
§14, §33 (full batching/QC UI deferred).

#### Description
Batch creation from saved searches (size, keep families/threads together, prefix, exclude docs already in active batches), assignment, reviewer progress, QC sampling and re-review screens; 'Last coded by/at' columns.

#### Acceptance criteria
- [ ] Design spec and wireframes approved before build
- [ ] Batch membership is materialized at creation; batch status Pending/In progress/Complete is persisted
- [ ] QC sampling produces a reproducible sample with seed

#### Dependencies
- `E10-T05` — Expose coding history API and review-batch domain support
- `E07-T09` — Implement saved searches
- `E09-T03` — Expand families, duplicates and threads for search, snapshots and bulk actions

#### Roles
- **Owner:** UI/UX
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** UI/UX, eDiscovery Practitioner
- **Milestone / phase / size:** M5 - Post-MVP / Deferred / P2 / L

#### Notes
Bulk undo/revert (Q-34) is tracked as an open question.
