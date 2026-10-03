# E06 — Messaging, Outbox & Job Infrastructure

**Labels:** `epic`, `role:backend`, `role:devops`, `role:data`, `role:search`, `role:security`, `role:ui`, `P0`  
**Starts in:** M1 - First Vertical Slice  
**Tickets:** 7

## Goal
Provide reliable asynchronous work: versioned message contracts, a RabbitMQ adapter, PostgreSQL-owned job/chunk state with leases, payload-free SearchOutbox and IndexChunkTask records, a horizontally scalable dispatcher, idempotent consumers, and operator tooling for failures.

## Baseline sections
§2.2, §2.4, §3, §7, §11, §21, §31.1

## Scope / out of scope
**In scope**
- Envelope + contracts + RabbitMQ adapter (quorum queues, confirms, retry/DLX, priority lane)
- Job/JobChunk state machine with leases
- SearchOutbox / IndexChunkTask tables
- Dispatcher (SKIP LOCKED + LISTEN/NOTIFY)
- Idempotent consumer pipeline
- Job API, DLQ inspection, PG-driven replay, runbooks; job monitor UI

**Out of scope**
- Kafka evaluation (§11, not planned)
- Index worker logic (E07)

## Contributing roles
- **Roles:** Backend, DevOps / SRE, Data (PostgreSQL), Search (OpenSearch), Security & Compliance, UI/UX
- **Source reviews:** Backend/Architecture, DevOps/SRE, Security & Compliance, UI/UX, QA & Performance
- **Milestones spanned:** M1 - First Vertical Slice, M3 - MVP Feature Complete

## Exit criteria
- [ ] Fault-injection matrix shows 100% idempotency (§26)
- [ ] Outbox insert → publish p95 < 100 ms idle on the dev profile
- [ ] Failed chunks can be replayed from PG state, and the replay is audited

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E06-T01](#e06-t01) | Implement message contracts and RabbitMQ transport adapter | M1 | L | E01-T02, E02-T02 |
| [E06-T02](#e06-t02) | Implement Job and JobChunk state machine with leases | M1 | L | E02-T02, E04-T02 |
| [E06-T03](#e06-t03) | Create SearchOutbox and IndexChunkTask tables | M1 | M | E06-T02 |
| [E06-T04](#e06-t04) | Build outbox dispatcher service | M1 | L | E06-T03, E06-T01 |
| [E06-T05](#e06-t05) | Build idempotent consumer framework | M1 | M | E06-T01, E06-T02 |
| [E06-T06](#e06-t06) | Provide job operations API, DLQ inspection, PG-driven replay and runbooks | M3 | M | E06-T04, E06-T05, E07-T08, E19-T05 |
| [E06-T07](#e06-t07) | Build job monitor UI, job tray and notifications | M3 | M | E06-T06, E15-T02, E07-T08 |

---

### E06-T01

**Implement message contracts and RabbitMQ transport adapter**  
Labels: `role:backend`, `role:devops`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§11: the envelope needs MessageId, MessageType, SchemaVersion, WorkspaceId, JobId, CorrelationId, CausationId, IdempotencyKey, CreatedAt, Attempt, Headers, Payload; RabbitMQ APIs stay outside domain/application logic. DevOps: CorrelationId/CausationId should map to W3C `traceparent`.

#### Description
`Opportunity.Contracts`: envelope + versioned message types, serializer with type registry, tolerant reader, golden-JSON contract tests, W3C trace context in headers. `IMessagePublisher`/`IMessageConsumer` in Application; RabbitMQ implementation in Messaging: quorum queues, publisher confirms, manual ack, prefetch limits, delayed retry via TTL+DLX retry queues with exponential backoff, per-queue DLQ, a separate priority queue for security-affecting index work, topology declared from code (applied by the migrator bootstrap).

#### Acceptance criteria
- [ ] Every message type has a golden-JSON contract test; removing/renaming a field fails CI
- [ ] Consumers accept SchemaVersion N and N-1; unknown major versions go to a parking state, never acked silently
- [ ] A publish is reported successful only after a broker confirm (test with broker restart)
- [ ] A failing handler retries with backoff up to max, then lands in the DLQ with original headers and error
- [ ] No RabbitMQ types leak outside `Opportunity.Messaging` (architecture test)

#### Dependencies
- `E01-T02` — Build composable worker host, API conventions and health endpoints
- `E02-T02` — Write ADR-001 and ADR-010: search consistency, version model and job/chunk/idempotency semantics

#### Roles
- **Owner:** Backend
- **Contributing:** DevOps / SRE
- **Source reviews:** Backend/Architecture, DevOps/SRE, Security & Compliance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

#### Notes
Merges backend 'Message envelope and contracts library' and 'RabbitMQ transport adapter'.

---

### E06-T02

**Implement Job and JobChunk state machine with leases**  
Labels: `role:backend`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§11 PG owns job state; backend finding 9: no statuses, transitions, cancellation, partial failure or stuck-chunk detection.

#### Description
Job (type, status, TargetSnapshotId, counts, creator, CorrelationId, job generation) and JobChunk (sequence, status, count, AttemptCount, IdempotencyKey, membership reference, LeaseOwner, LeaseExpiresAt, LastError) per ADR-010. Transitions via conditional UPDATEs; lease-reclamation sweeper; cancellation; progress counters. Minimal job status API (`GET /jobs/{id}`) returning committed and indexed chunk counts for the slice.

#### Acceptance criteria
- [ ] Illegal transitions are rejected (unit tests over the full transition matrix)
- [ ] Killing a worker mid-chunk leads to lease expiry, reclamation and exactly one successful completion
- [ ] Job progress (completed/failed/pending chunks) is O(1) via counters, not COUNT(*) over chunks
- [ ] Job status API returns separate 'committed' and 'indexed' progress

#### Dependencies
- `E02-T02` — Write ADR-001 and ADR-010: search consistency, version model and job/chunk/idempotency semantics
- `E04-T02` — Create workspace, document and page core schema

#### Roles
- **Owner:** Backend
- **Contributing:** —
- **Source reviews:** Backend/Architecture, QA & Performance, UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

---

### E06-T03

**Create SearchOutbox and IndexChunkTask tables**  
Labels: `role:data`, `role:search`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§7, §21. Backend findings 4 and 6; QA finding 1: work rows need a commit timestamp for lag measurement.

#### Description
SearchOutbox (OutboxId, WorkspaceId, DocumentId, DocumentVersion, EventType, SearchGeneration, Priority, CommittedAt, timestamps; payload-free per ADR-001) and IndexChunkTask (§21 fields + SearchGeneration, TaskKind {Import, BulkCoding, Reindex, Family}, nullable SnapshotId, chunk membership reference, CommittedAt). Retention by time partitions.

#### Acceptance criteria
- [ ] An interactive coding transaction inserts exactly one SearchOutbox row; a bulk/import chunk transaction inserts exactly one IndexChunkTask and no SearchOutbox rows (asserted in tests)
- [ ] Dispatched rows older than the retention window are removed by partition drop, not DELETE
- [ ] CommittedAt reflects the PG transaction commit time used by lag measurement

#### Dependencies
- `E06-T02` — Implement Job and JobChunk state machine with leases

#### Roles
- **Owner:** Data (PostgreSQL)
- **Contributing:** Search (OpenSearch)
- **Source reviews:** Backend/Architecture, QA & Performance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

#### Notes
Proposed amendment: §7 Payload column removed; SnapshotId nullable + TaskKind on IndexChunkTask.

---

### E06-T04

**Build outbox dispatcher service**  
Labels: `role:backend`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§3, §7, §21. Backend finding 5: the ≤ 1 s p95 target rules out naive multi-second polling.

#### Description
`Opportunity.Worker.Dispatcher` claims SearchOutbox and IndexChunkTask rows with `FOR UPDATE SKIP LOCKED` in batches, wakes on LISTEN/NOTIFY with polling fallback, publishes with confirms, then marks dispatched. Priority for security-affecting rows. Optional per-document coalescing of pending outbox rows. Horizontally scalable.

#### Acceptance criteria
- [ ] Outbox insert → message published p95 < 100 ms on the dev profile at idle
- [ ] Three dispatcher instances lose no rows; duplicates are bounded and harmless (end-to-end)
- [ ] During a broker outage rows accumulate and drain after recovery; `outbox_oldest_age_seconds` alert fires

#### Dependencies
- `E06-T03` — Create SearchOutbox and IndexChunkTask tables
- `E06-T01` — Implement message contracts and RabbitMQ transport adapter

#### Roles
- **Owner:** Backend
- **Contributing:** —
- **Source reviews:** Backend/Architecture, DevOps/SRE
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

---

### E06-T05

**Build idempotent consumer framework**  
Labels: `role:backend`, `role:security`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§2.4, §11, §26 '100% idempotent'. Security finding 7: workers must not trust envelope WorkspaceId.

#### Description
Consumer middleware: envelope validation; resolve WorkspaceId and initiating actor from the PG Job/IndexChunkTask row and reject mismatches; workspace status fence (abort if Deleting/Locked for destructive work); idempotency via inbox table or conditional JobChunk transition; OTel span; transient vs permanent error classification; ack only after commit. Includes `IFaultInjector` failpoint hooks compiled into test builds only.

#### Acceptance criteria
- [ ] Delivering every message 1–3 times with random crashes before/after commit yields state identical to single delivery
- [ ] Permanent errors skip retries and mark the chunk Failed with LastError
- [ ] A message whose envelope WorkspaceId disagrees with the PG row is rejected and audited
- [ ] Failpoints are compiled out of release builds (build check)

#### Dependencies
- `E06-T01` — Implement message contracts and RabbitMQ transport adapter
- `E06-T02` — Implement Job and JobChunk state machine with leases

#### Roles
- **Owner:** Backend
- **Contributing:** Security & Compliance
- **Source reviews:** Backend/Architecture, Security & Compliance, QA & Performance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E06-T06

**Provide job operations API, DLQ inspection, PG-driven replay and runbooks**  
Labels: `role:backend`, `role:devops`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§1 job monitoring; backend finding 11: a dead-lettered RabbitMQ message is not the source of truth, so replay = reset PG state; devops: admin CLI and runbooks per alert.

#### Description
Admin API + CLI: `/api/v1/workspaces/{ws}/jobs` list/detail/cancel/retry-failed-chunks with chunk progress, errors and index progress separate from commit progress; list failed chunks/tasks/outbox rows; replay resets status/attempts and lets the dispatcher re-publish; DLQ retained for diagnostics only. Runbooks for every alert in `E19-T05`, re-dispatch of stuck work, alias reindex trigger and deletion verification.

#### Acceptance criteria
- [ ] Replaying a failed IndexChunkTask after fixing the cause indexes current PG state and advances the watermark
- [ ] Replay is audited and restricted to `Job.Replay`; replaying the same failures twice is idempotent
- [ ] Cancel moves pending chunks to Cancelled; running chunks finish or abort at the next fence point
- [ ] Every alert links to a runbook; runbooks are exercised in a recorded game day

#### Dependencies
- `E06-T04` — Build outbox dispatcher service
- `E06-T05` — Build idempotent consumer framework
- `E07-T08` — Implement search generation watermark and freshness API
- `E19-T05` — Build consistency and pipeline metrics, dashboards and alerts

#### Roles
- **Owner:** Backend
- **Contributing:** DevOps / SRE
- **Source reviews:** Backend/Architecture, DevOps/SRE, UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Merges backend job monitoring/replay API and devops 'Operational runbooks and DLQ/redrive tooling'.

---

### E06-T07

**Build job monitor UI, job tray and notifications**  
Labels: `role:ui`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§7, §11, §28. UI finding 12: two-phase progress (committed in PG vs searchable) and push vs polling.

#### Description
Jobs page (import, index, bulk coding, render, export, production) with filters by type/status/creator/date; detail with chunk progress, separate 'Committed (PostgreSQL)' and 'Searchable (index)' bars with count/percent/ETA, attempt/error counts, dead-lettered chunks with 'Retry failed chunks', copyable CorrelationId/SnapshotId. Shell-level job tray with running count and completion/failure toasts. Updates via SSE through the API pool with polling fallback.

#### Acceptance criteria
- [ ] Detail shows two progress bars with `role=progressbar` and value text; status changes are announced politely
- [ ] Updates arrive within 5 s of a state change without reload
- [ ] Failure toasts persist until dismissed; notifications are scoped to own jobs (all jobs for workspace admins)
- [ ] Retry action is visible only with permission

#### Dependencies
- `E06-T06` — Provide job operations API, DLQ inspection, PG-driven replay and runbooks
- `E15-T02` — Build application shell, session handling and workspace context
- `E07-T08` — Implement search generation watermark and freshness API

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Q-35.
