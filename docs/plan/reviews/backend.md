# Backend / Architecture Review — opportuniTY Architecture Baseline

Reviewer role: Lead Backend Developer / Software Architect (.NET, PostgreSQL, OpenSearch, RabbitMQ)
Input: `docs/architecture/architecture-baseline.md` (§1–§35, normative per §35)

---

## Review findings

1. **External versioning is incompatible with partial updates (§21 step 5, §25 Candidate A/D, §7).** OpenSearch `version_type=external`/`external_gte` applies only to full `index` operations, not to `_update` (partial/scripted updates reject external versions). Two consequences follow. (a) Under Candidate A, every coding change must rebuild and re-send the whole document, ~10 MB of text included, from PostgreSQL/object storage, which raises the merge-pressure risk that §25 already flags. (b) Any design that uses `_update` needs a different guard, such as `if_seq_no`/`if_primary_term` or a painless script that compares `projectionVersion`. The ADR-004 spike must measure full-reindex vs scripted-guarded-update as separate variants, and ADR-001 must name the exact OpenSearch write primitive per candidate.

2. **Three version concepts have no defined relationship (§5 DocumentVersion, §7 "monotonic versions", §21 ProjectionGeneration, §23 projectionVersion, §10 SearchGeneration).** The spec does not say whether a metadata re-import, text replacement or family re-link increments `DocumentVersion`. It also does not say whether `projectionVersion == DocumentVersion`, or what happens to version-safety when a mapping change (ProjectionGeneration) forces a reindex. Under Candidate B/C each index needs its own monotonic counter, for example `ContentVersion` and `CodingVersion`, or a coding-only bump would race content writes. Proposed fix: one authoritative `DocumentVersion` bigint, incremented on *any* projected-field change, written as the external version, plus a separate `ProjectionGeneration` that selects the target physical index and is never compared with document versions.

3. **Deletes and version tombstones (§21, §15 deletion).** OpenSearch only keeps external-version tombstones for deleted docs for `index.gc_deletes` (default 60 s). A delayed retried IndexChunkTask can therefore resurrect a document deleted from PostgreSQL. The self-healing worker must treat "row missing in PG" as an explicit delete and never as a skip, and workspace deletion must fence in-flight tasks (a status flag checked by the worker) before it drops indexes or partitions.

4. **IndexChunkTask for import is under-specified (§12 vs §21).** The §21 schema (`JobId, ChunkId, SnapshotId`) assumes a bulk-coding chunk resolved from a snapshot. Import chunks have no snapshot: the documents are *created* by the chunk. The spec needs to say how step 1 ("resolve the chunk's exact document IDs") works for import. Proposal: `JobChunk` owns a membership representation for every job type (a materialized snapshot range for bulk work, `ImportBatchId`/row range for import), and `SnapshotId` becomes nullable. Reindex/alias-switch (§8) also needs a task kind, and the spec doesn't say whether that is IndexChunkTask with a new ProjectionGeneration.

5. **Outbox dispatcher semantics are undefined (§3, §7, §21).** The spec doesn't settle: polling vs logical replication/LISTEN-NOTIFY; how multiple dispatcher instances coordinate (`FOR UPDATE SKIP LOCKED` vs leader election); RabbitMQ publisher confirms before marking dispatched; cleanup/retention of dispatched rows (partition by day and drop?); and whether SearchOutbox and IndexChunkTask share one dispatcher with priority. **Ordering:** a per-document guarantee is neither provided nor needed if version-safety holds, and the spec should state that explicitly so nobody builds single-active-consumer queues "for ordering". The ≤1 s p95 interactive target (§17) rules out naive multi-second polling, so it needs a NOTIFY-wakeup plus a polling fallback.

6. **Coalescing has no defined place (§25 mitigation, §7).** Coalescing pending coding changes per document could happen in the dispatcher (collapse SearchOutbox rows by DocumentId), in the index worker (dedupe within a consumed batch), or not at all if workers always read current PG state. Because §21 workers read *current* state anyway, interactive outbox messages could also be payload-free (DocumentId + version). That removes the `Payload` column from §7 and makes coalescing trivial. The baseline should resolve the conflict between §7 (`Payload`) and §21 (payload-free).

7. **SearchGeneration/watermark computation is unspecified (§28, §10).** A naive per-workspace sequence breaks because sequence values are allocated before commit and can commit out of order. "Index current through N" is then only correct as "all generations ≤ N are applied", which needs a low-watermark over in-flight work (min un-applied generation − 1). The spec also leaves open whether interactive outbox rows and chunk tasks share one generation sequence (they must for one UI watermark), and how the watermark behaves across a reindex/alias switch. §10 puts `SearchGeneration` on `DocumentSetSnapshot` without defining what a snapshot's generation means for a PIT-based target.

8. **Idempotency key derivation and consumer dedupe are unspecified (§11, §2.4).** `MessageId`, `IdempotencyKey` and `JobChunk.IdempotencyKey` coexist with no rule. Proposal: `IdempotencyKey = hash(WorkspaceId, JobId, ChunkSequence, OperationKind, ProjectionGeneration)`, deterministic and stable across re-dispatch. Consumers dedupe via an inbox table or a conditional state transition on `JobChunk.Status` (`Pending→Running` with attempt fencing). Bulk coding chunks must also be idempotent *semantically*: "add tag X" re-applied must not double-write `CodingEvent` provenance (§27).

9. **The job/chunk state machine is not defined (§11).** The spec has no statuses, no allowed transitions, and no rules for cancellation, pause/resume, partial failure ("completed with errors"), maximum attempts, or stuck-chunk detection (lease/heartbeat timeout). A worker crash after the PG commit but before the ack is the canonical case, and the baseline should require lease-based ownership (`LeaseOwner`, `LeaseExpiresAt`) so another worker can reclaim the chunk.

10. **Chunk sizing has no bound (§11, §21, §27).** "Bounded" chunks are mandated but not quantified. Chunks must be bounded by document count *and* bytes: OpenSearch `http.max_content_length` defaults to 100 MB, and §29 includes ~10 MB-text documents, so a 1,000-document chunk can blow the bulk limit. PG transaction size (WAL, lock duration) is a separate bound. Chunk size should be configurable per job type, with initial values set by the §27 spike, and the index worker should split OpenSearch bulk sub-requests by bytes independently of the job chunk size.

11. **Dead-letter handling and replay are missing (§7, §11).** The spec names DLQ, but "PostgreSQL owns job state" means a dead-lettered RabbitMQ message is not the source of truth. Replay should be "reset JobChunk/IndexChunkTask status in PG and re-dispatch", not "shovel the DLQ back". The spec needs a poison-message policy (max attempts, then `Failed` in PG, then a DLQ copy for diagnostics only), an operator replay API, and alerts on DLQ depth.

12. **Security-affecting coding lacks a mechanism (§24.3, §23 `securityTags`).** "Stronger consistency/priority SLA" has no implementation: a separate priority queue, RabbitMQ `x-max-priority`, or a dedicated worker pool. `securityTags` lives in the content projection (§23), yet privilege/confidentiality are *coding* fields whose placement ADR-004 decides, and under Candidate B the security filter would live in a different index than content. This should become an explicit ADR-004 evaluation criterion.

13. **Multi-tenant enforcement in the data layer is unspecified (§2.3, §23).** §23 covers search scoping, but PostgreSQL scoping is left open. EF Core global query filters are bypassed by Dapper, raw SQL and Npgsql `COPY` (which bulk paths will need). Options are PostgreSQL Row-Level Security via `SET LOCAL app.workspace_id`, a mandatory `IWorkspaceScope` on every repository with analyzer enforcement, or both. Workspace-partitioned tables (§6) also make `WorkspaceId` the leading column of every PK/index, which needs stating as a rule now because it is very costly to retrofit.

14. **Schema migration tooling and the partitioning decision interact (§6, §33).** Partitioning is deferred, but the schema is needed now. EF Core migrations handle declarative partitioning, JSONB GIN indexes, fillfactor and RLS poorly. Recommendation: SQL-first migrations (DbUp or Grate) owned by `Opportunity.Data`, EF Core/Dapper for access only, with partition-agnostic DDL shaped so hash-by-workspace can be introduced later (composite PK `(WorkspaceId, DocumentId)` from day one).

15. **.NET version, API style and contract versioning are absent (§4, §11).** Proposals: pin .NET 10 (current LTS); REST + OpenAPI 3.1 with URL-segment major versioning; RFC 9457 ProblemDetails; cursor pagination; long-running operations as `202 Accepted` + `/jobs/{id}`. For messages, `SchemaVersion` needs rules: additive-only within a major version, tolerant readers, consumers that handle N and N-1 during rolling deploys, and a contract test suite. Otherwise a rolling worker deployment can strand in-flight RabbitMQ messages.

16. **§18 layout does not match the other sections.** It has no Bulk Coding worker (§21), no Contracts/Messages project (envelope types shared by API, dispatcher and workers), no Outbox Dispatcher host (§3), no Application/Domain split inside `Core`, and no `Opportunity.Benchmarks` harness (§26/§29), though ScaleTests partially covers it. `Opportunity.Jobs` vs `Worker.*` responsibilities are unclear. The root `opportunity/` also differs from the repo name `opportuniTY`. A short layering ADR should fix project references, e.g. Core → nothing; Data/Search/Messaging → Core; Workers → Application + Infrastructure; enforced with NetArchTest.

17. **ADR numbering collides (§19 vs §25).** §19 item 4 is "Coding storage" (PostgreSQL), while "ADR-004" in §25–26 is the *OpenSearch* coding projection. The PG coding spike (§27) has no ADR number. Renumbering before any ADR files are written (docs/adr is empty) avoids permanent confusion.

18. **PIT-vs-materialized rule thresholds and §20/§32 duplication (§22, §10).** "Runtime safely within PIT lifetime" needs a numeric policy: estimated runtime from DocumentCount/throughput, compared against `keep_alive` × safety factor. §32 requires bulk targets to be "restartable", which by §22 means bulk tagging in the slice must use a materialized snapshot, so PIT is effectively unused in the MVP. §20 and §32 duplicate the vertical slice with different steps; §32 should be marked normative. Family/duplicate expansion timing (at snapshot materialization vs at execution) is also undefined, and it matters for legal reproducibility.

---

## Proposed epics and tickets

Conventions: ticket titles are unique and referenced by title in Dependencies. Phase follows the request: P0 = foundation / vertical slice / §31 blockers; P1 = MVP scope §1; P2 = deferred per §33.

---

### EPIC: Platform Foundation and Solution Scaffolding

Stand up the .NET solution, local Lite profile, CI, observability and cross-cutting infrastructure so every other epic has a stable, testable home. Fixes project layering and conventions before code accumulates.
**Implements:** §2.5, §4, §16, §18, §29 (developer profile), §15 (authN), §24 (authorization hook).

#### Solution scaffolding and project layering
- **Role:** Platform
- **Description:** Create `Opportunity.sln` per §18, extended with the missing projects: `Opportunity.Contracts` (message envelopes/DTOs), `Opportunity.Application` (use cases), `Opportunity.Worker.Dispatcher`, `Opportunity.Worker.BulkCoding`, `tests/ArchitectureTests`, `tools/Opportunity.Benchmarks`. Pin .NET 10 LTS via `global.json`, central package management, nullable + warnings-as-errors, `.editorconfig`, analyzers. API conventions: OpenAPI 3.1, `/api/v1` URL versioning, RFC 9457 ProblemDetails, cursor pagination, long-running operations return `202` + `Location: .../jobs/{jobId}`, and an `Idempotency-Key` header on job-creating POSTs.
- **Acceptance criteria:**
  - `dotnet build` and `dotnet test` succeed from a clean clone.
  - NetArchTest rules fail the build if `Core` references infrastructure (`Npgsql`, `OpenSearch.Client`, `RabbitMQ.Client`, AWS/Azure SDKs) or if `Application` references `RabbitMQ.Client`.
  - Every worker is a generic-host executable with health endpoints (`/health/live`, `/health/ready`).
  - The layering is documented in a short ADR (see "ADR: numbering, template and solution layering and API conventions").
  - OpenAPI spec is generated in the build and diffed in CI; re-POSTing a job creation with the same Idempotency-Key returns the original job.
- **Dependencies:** none
- **Phase:** P0
- **Size:** M

#### Docker Compose Lite profile
- **Role:** Platform
- **Description:** `deploy/docker-compose` with profiles `lite` (PostgreSQL, single-node OpenSearch, RabbitMQ, S3-compatible store, API, essential workers) and `observability` (OTel collector, Grafana stack). Select the bundled S3-compatible store after the license/maintenance check (§16); record it in the object-storage ADR.
- **Acceptance criteria:**
  - `docker compose --profile lite up` reaches healthy for all services in <3 min on a dev laptop.
  - Seed script creates a demo workspace and imports a sample DAT/OPT set end to end.
  - No Redis/Valkey service is present (§16, §34).
- **Dependencies:** Solution scaffolding and project layering; Object storage abstraction and addressing
- **Phase:** P0
- **Size:** M

#### CI pipeline with containerized integration tests
- **Role:** Platform
- **Description:** GitHub Actions: build, unit tests, integration tests using Testcontainers (PostgreSQL, OpenSearch, RabbitMQ, MinIO-compatible), architecture tests, OpenAPI diff, message-contract tests. Scale tests run on a nightly/manual workflow.
- **Acceptance criteria:**
  - PR pipeline completes in <15 min; integration tests run against real containers (no in-memory fakes for PG/OpenSearch).
  - Test results and coverage are published as artifacts; a failing architecture test blocks merge.
  - Nightly job runs the 100K-document regression scale test and stores results.
- **Dependencies:** Solution scaffolding and project layering
- **Phase:** P0
- **Size:** M

#### OpenTelemetry baseline
- **Role:** Platform
- **Description:** Traces, metrics and logs via OTel across API, dispatcher and workers. Propagate W3C trace context in message headers alongside CorrelationId/CausationId (§11). Standard metrics: queue depth, outbox lag, IndexChunkTask backlog, search-generation lag, job throughput, OpenSearch bulk latency.
- **Acceptance criteria:**
  - One trace spans API request → outbox insert → dispatch → index worker → OpenSearch bulk call.
  - Metrics `outbox_pending_count`, `outbox_oldest_age_seconds`, `index_chunk_tasks_pending`, `search_generation_lag` are exported per workspace (bounded cardinality: top-N plus "other").
  - Logs are structured JSON with WorkspaceId, JobId and CorrelationId.
- **Dependencies:** Solution scaffolding and project layering; Message envelope and contracts library
- **Phase:** P0
- **Size:** M

#### SQL-first schema migration tooling
- **Role:** Data
- **Description:** Adopt SQL-first migrations (DbUp/Grate) in `Opportunity.Data`, run by a dedicated migrator executable (not on API startup). Conventions: `WorkspaceId` leads every tenant PK/index; partition-agnostic DDL; RLS policies in migrations.
- **Acceptance criteria:**
  - Migrator applies all scripts idempotently to an empty and to a previous-version database in CI.
  - Concurrent API/worker startup never runs migrations; services fail readiness if the schema version is behind.
  - A lint check rejects a tenant table without a leading `WorkspaceId` in its PK.
- **Dependencies:** Solution scaffolding and project layering
- **Phase:** P0
- **Size:** S

#### Object storage abstraction and addressing
- **Role:** Platform
- **Description:** `IObjectStore` (put/get/stream/range/head/delete-prefix/presign) with S3 and Azure Blob providers. Deterministic addressing `ws/{workspaceId}/docs/{documentId}/{artifactKind}/{contentHash}` so workspace deletion is a prefix delete (§15) and retries are idempotent. Supports multipart upload and checksum verification.
- **Acceptance criteria:**
  - Both providers pass the same contract test suite (Azurite and an S3-compatible container).
  - Re-uploading identical content to the same key is a no-op verified by checksum.
  - Signed URLs are never handed out without an authoritative authorization check (enforced by API design; see "Authoritative protected-resource authorization service").
- **Dependencies:** Solution scaffolding and project layering
- **Phase:** P0
- **Size:** M

#### OIDC authentication and workspace RBAC
- **Role:** Backend
- **Description:** OIDC/OAuth2 JWT bearer auth (Entra optional, Keycloak in Lite). Workspace membership and roles in PG; policy-based authorization with `WorkspaceId` resolved from the route and validated against membership on every request.
- **Acceptance criteria:**
  - Every workspace-scoped endpoint returns 403 for a non-member; an automated test enumerates all routes from the OpenAPI document and asserts this.
  - Roles: Admin, Reviewer, ReadOnly (minimum); permissions are checked server-side.
  - Lite profile ships a dev identity provider configuration.
- **Dependencies:** Solution scaffolding and project layering; Workspace and document core schema
- **Phase:** P0
- **Size:** M

---

### EPIC: Priority Architecture Decision Records

Turn the §19 priority list into written, reviewed ADRs so implementation choices are explicit and traceable. ADRs that depend on benchmarks start as "Proposed" with interim positions and are finalized after the spikes.
**Implements:** §19, §25–§27, §34, §35. Also resolves the ADR numbering collision (finding 17).

#### ADR: numbering, template and solution layering and API conventions
- **Role:** Platform
- **Description:** Create `docs/adr/0000-template.md` and an index. Resolve the §19/§25 numbering collision: proposal ADR-004a "PostgreSQL coding storage", ADR-004b "OpenSearch coding projection", or renumber. Record layering, .NET version, API style and message contract versioning rules (findings 15, 16).
- **Acceptance criteria:**
  - ADR index lists all 14 priority ADRs with status and owner.
  - Layering/API ADR accepted and referenced by architecture tests and API conventions.
- **Dependencies:** none
- **Phase:** P0
- **Size:** S

#### ADR-001 and ADR-010: Search consistency/outbox and job/chunk/idempotency semantics
- **Role:** Search
- **Description:** Define the SearchOutbox (interactive) vs IndexChunkTask (bulk/import) split. Decide whether interactive outbox rows are payload-free (finding 6), and pin the dispatcher mechanism (NOTIFY + SKIP LOCKED polling), the no-ordering guarantee, the coalescing location, the OpenSearch write primitive per projection candidate (finding 1), delete/tombstone handling (finding 3) and the version model (finding 2). ADR-010 part: Job/JobChunk state machine with leases, idempotency key derivation, consumer dedupe, chunk sizing (count + bytes), retry/backoff, poison policy, DLQ-as-diagnostics with PG-driven replay, message SchemaVersion compatibility (findings 8–11, 15).
- **Acceptance criteria:**
  - ADR states the exact version field(s), increment rules and OpenSearch write op for index/update/delete.
  - ADR includes a sequence diagram for retry-after-newer-edit and proves the "0 stale overwrites" invariant (§26).
  - State diagram for Job and JobChunk with every allowed transition; cancellation and partial-failure semantics defined; idempotency key formula published.
  - Both accepted before "Outbox dispatcher service" is merged.
- **Dependencies:** ADR: numbering, template and solution layering and API conventions
- **Phase:** P0
- **Size:** M

#### ADR-002: Bulk snapshot semantics
- **Role:** Search
- **Description:** Formalize the §22 rule with numeric thresholds (PIT keep-alive, safety factor, runtime estimate), the materialized representation (PG membership rows vs object-storage manifests), family/duplicate expansion timing, and the meaning of SearchGeneration on a snapshot.
- **Acceptance criteria:**
  - Decision table maps each job type (bulk tag, bulk code, export, production, reindex) to PIT or Materialized.
  - Representation chosen with a 1M-member benchmark result (creation time, storage, chunk iteration cost).
- **Dependencies:** PostgreSQL coding model spike
- **Phase:** P0
- **Size:** S

#### ADR-003, ADR-005 and ADR-009: Metadata model, interim partitioning and family/dedupe/thread identity
- **Role:** Data
- **Description:** Typed structural columns + `Metadata JSONB` keyed by stable FieldDefinitionId (not display name). Field-type validation and JSONB indexing policy. Identity rules for FamilyId, DuplicateGroupId (hash algorithm, scope: workspace) and EmailThreadId; behaviour on re-import and overlay. ADR-005 is recorded as an interim position: non-partitioned but partition-ready DDL, with the hash-by-workspace / dedicated / hybrid benchmark plan (final decision deferred, §33).
- **Acceptance criteria:**
  - ADR defines JSONB key format, renaming behaviour, and the type coercion table for all 9 field types (§6).
  - ADR defines deterministic family resolution from DAT BegAttach/EndAttach/ParentId fields and the conflict handling.
  - Interim ADR-005 lists the "partition-ready" DDL rules and benchmark metrics (coding updates, bulk coding, workspace delete, vacuum/bloat, index size).
- **Dependencies:** ADR: numbering, template and solution layering and API conventions
- **Phase:** P0
- **Size:** M

#### ADR-004a/004b: Coding storage and coding search projection
- **Role:** Search
- **Description:** Record the outcome of the ADR-004 spike (Candidates A–D) and the PG coding spike, applying the §26 gates. Includes the security-field placement (finding 12). Until accepted, the vertical slice uses an interim Candidate A behind the logical search service.
- **Acceptance criteria:**
  - Gate table is filled with measured values for every candidate; correctness/security gates are evaluated first.
  - Decision follows the §26 tie-break (least operational complexity unless a material repeatable advantage).
  - Benchmark artifacts (hardware, versions, seed, scripts) are linked per §29.
- **Dependencies:** ADR-004 projection spike comparative run; PostgreSQL coding model spike
- **Phase:** P0
- **Size:** M

#### ADR-006, ADR-007 and ADR-008: Index strategy, mapping strategy and minimal query language
- **Role:** Search
- **Description:** Shared vs dedicated vs multi-shard placement thresholds, routing key, alias naming, logical-to-physical resolution, reindex/alias-switch protocol; strict mappings with typed custom-field containers vs `flattened`; analyzers. ADR-008: EBNF for the MVP subset (§33) — Boolean, phrase, field:value, ranges, trailing wildcard, escaping — with proximity and full legal syntax marked deferred behind AST extension points.
- **Acceptance criteria:**
  - Placement thresholds are expressed in document count/bytes and are configurable.
  - Mapping ADR explicitly lists §23 fields with types; `dynamic: strict` at root.
  - Every §9 example is marked supported or deferred; precedence, default operator and case rules are documented.
- **Dependencies:** ADR: numbering, template and solution layering and API conventions
- **Phase:** P0
- **Size:** M

#### ADR-011 to ADR-014: Object storage addressing, redactions, audit and retention lifecycle
- **Role:** Platform
- **Description:** Grouped ADRs: object key layout and immutability; redaction coordinate model (page-normalized coords, rotation, versioning); audit event schema, partitioning and later archival (§15, deferred tamper-evidence per §33); workspace deletion order and fencing (finding 3), with retention/hold hooks.
- **Acceptance criteria:**
  - Four ADRs at status Accepted (011, 012) or Proposed with interim position (013, 014).
  - Deletion ADR defines the sequence: mark deleting → fence workers → drain/cancel jobs → drop OpenSearch data → drop PG data → delete storage prefix → audit record.
- **Dependencies:** ADR: numbering, template and solution layering and API conventions
- **Phase:** P0 (011, 013 interim), P1 (012, 014)
- **Size:** M

---

### EPIC: Domain Model and Authoritative Data

Implement the PostgreSQL source-of-truth schema for workspaces, documents, fields, coding, relationships and audit, with enforced tenant isolation and authoritative authorization.
**Implements:** §2.1, §2.3, §5, §6, §15, §24, §27 (interim coding model).

#### Workspace and document core schema
- **Role:** Data
- **Description:** Tables: Workspace, Document (all §5 fields: ControlNumber, FamilyId, ParentDocumentId, FamilySequence, DuplicateGroupId, EmailThreadId, MD5/SHA256, file/type/date fields, artifact object keys, `Metadata JSONB`, DocumentVersion bigint, timestamps). Composite PK `(WorkspaceId, DocumentId)`; unique `(WorkspaceId, ControlNumber)`.
- **Acceptance criteria:**
  - Migrations create the schema; domain entities map with EF Core and Dapper read models.
  - DocumentVersion increments on every projected-field change (enforced in the repository, covered by tests).
  - Insert of 100K documents via `COPY` completes in the integration test within a recorded baseline time.
- **Dependencies:** SQL-first schema migration tooling; ADR-003, ADR-005 and ADR-009: Metadata model, interim partitioning and family/dedupe/thread identity
- **Phase:** P0
- **Size:** M

#### Field definitions and typed metadata
- **Role:** Data
- **Description:** FieldDefinition (Text, Keyword, Integer, Decimal, Date, Boolean, SingleChoice, MultiChoice, User), Choice tables, and a validation service for JSONB metadata keyed by FieldDefinitionId. Separate imported (static) metadata from coding fields (§6).
- **Acceptance criteria:**
  - Creating/renaming a field never rewrites document rows.
  - Invalid typed values are rejected with field-level errors; coercion table matches ADR-003.
  - Field definitions are workspace-scoped and audited.
- **Dependencies:** Workspace and document core schema
- **Phase:** P0
- **Size:** M

#### Interim coding current-state and provenance tables
- **Role:** Data
- **Description:** Implement the §27 conceptual model as the interim schema: `DocumentCodingCurrent` (+ choice rows) and append-only `CodingEvent` (JobId nullable for interactive). The schema remains swappable until the PG coding spike concludes.
- **Acceptance criteria:**
  - Every coding mutation writes current state + CodingEvent + DocumentVersion increment in one transaction.
  - Re-applying the same chunk (same idempotency key) creates no duplicate CodingEvents.
  - Repository API is storage-agnostic so the spike result can change tables without touching Application code.
- **Dependencies:** Field definitions and typed metadata
- **Phase:** P0
- **Size:** M

#### Workspace isolation enforcement in the data layer
- **Role:** Data
- **Description:** Enforce WorkspaceId at the data layer: PostgreSQL RLS on tenant tables driven by `SET LOCAL app.workspace_id` from a scoped `IWorkspaceContext`, plus EF Core global filters. Dapper/COPY paths must go through a connection factory that sets the GUC. A dedicated superuser-free app role.
- **Acceptance criteria:**
  - Test suite attempts cross-workspace reads/writes via EF Core, Dapper and raw SQL; all return zero rows or fail.
  - A connection without `app.workspace_id` set sees no tenant rows.
  - RLS overhead measured on the 1M dataset and recorded.
- **Dependencies:** Workspace and document core schema
- **Phase:** P0
- **Size:** M

#### Authoritative protected-resource authorization service
- **Role:** Backend
- **Description:** `IDocumentAccessService` that checks workspace role, document/field restrictions and security-affecting coding (privilege/confidentiality/ethical wall) against PG. Called by every open/view/native/image/export/production path (§24). OpenSearch is never consulted.
- **Acceptance criteria:**
  - After a security-affecting coding change commits, the next view/download is denied before any index update (test with the index worker paused).
  - A route inventory test asserts that every protected-content endpoint invokes the service.
  - 0 unauthorized retrievals in the fault-injection suite (§26 gate).
- **Dependencies:** OIDC authentication and workspace RBAC; Interim coding current-state and provenance tables
- **Phase:** P0
- **Size:** M

#### Partitioned append-only audit log
- **Role:** Data
- **Description:** AuditEvent table range-partitioned by time (monthly), append-only (no UPDATE/DELETE grants for the app role). Records views, downloads, coding, bulk jobs, exports, admin changes. Archival/tamper-evidence deferred (§33).
- **Acceptance criteria:**
  - App role cannot UPDATE/DELETE audit rows (verified test).
  - Bulk jobs write one summary audit event plus chunk-level references, not per-document rows; per-document provenance stays in CodingEvent.
  - Partition creation is automated ahead of time.
- **Dependencies:** SQL-first schema migration tooling
- **Phase:** P1
- **Size:** M

#### Workspace deletion lifecycle
- **Role:** Backend
- **Description:** Implement the ADR-014 deletion sequence as a job with fencing: workers check workspace status before every chunk and abort if the workspace is `Deleting`.
- **Acceptance criteria:**
  - Deleting a workspace with in-flight bulk and index tasks leaves no OpenSearch docs, PG rows or storage objects, and no resurrected docs after 5 min.
  - The deletion audit record survives the workspace deletion.
- **Dependencies:** ADR-011 to ADR-014: Object storage addressing, redactions, audit and retention lifecycle; Job and JobChunk state machine
- **Phase:** P1
- **Size:** M

---

### EPIC: Messaging, Outbox and Job Infrastructure

Provide reliable asynchronous work creation and execution: the transactional outbox, dispatcher, RabbitMQ adapter, job/chunk state in PG, idempotent consumers, and dead-letter/replay tooling.
**Implements:** §2.2, §2.4, §3, §7, §11, §21, §31.1.

#### Message envelope and contracts library
- **Role:** Backend
- **Description:** `Opportunity.Contracts`: envelope (MessageId, MessageType, SchemaVersion, WorkspaceId, JobId, CorrelationId, CausationId, IdempotencyKey, CreatedAt, Attempt, Headers, Payload) and versioned message types. Serializer with a type registry; tolerant reader; contract snapshot tests.
- **Acceptance criteria:**
  - Every message type has a golden-JSON contract test; removing or renaming a field fails CI.
  - Consumers accept SchemaVersion N and N-1; unknown major versions are rejected to a parking state, not acked silently.
- **Dependencies:** Solution scaffolding and project layering
- **Phase:** P0
- **Size:** S

#### RabbitMQ transport adapter
- **Role:** Platform
- **Description:** `IMessagePublisher`/`IMessageConsumer` abstractions in Application; RabbitMQ implementation in Messaging. Quorum queues, publisher confirms, manual ack, prefetch limits, delayed retry via TTL+DLX retry queues with exponential backoff, and per-queue DLQ. Topology declared from code. No RabbitMQ types leak outside Messaging.
- **Acceptance criteria:**
  - A publish is only reported successful after a broker confirm (test with a broker restart).
  - A failing handler is retried with backoff up to the configured max, then lands in the DLQ with the original headers and error.
  - A priority lane exists for security-affecting index work (separate queue).
- **Dependencies:** Message envelope and contracts library
- **Phase:** P0
- **Size:** M

#### Job and JobChunk state machine
- **Role:** Backend
- **Description:** Job (type, status, TargetSnapshotId, counts, creator, CorrelationId) and JobChunk (sequence, status, count, AttemptCount, IdempotencyKey, LeaseOwner, LeaseExpiresAt, errors) per ADR-010. Transitions via conditional UPDATEs; lease reclamation sweeper; cancellation; progress aggregation.
- **Acceptance criteria:**
  - Illegal transitions are rejected (unit tests over the full transition matrix).
  - Killing a worker mid-chunk leads to lease expiry, reclamation and exactly one successful completion recorded.
  - Job progress (completed/failed/pending chunk counts) is queryable in O(1) via counters, not COUNT(*) over chunks.
- **Dependencies:** ADR-001 and ADR-010: Search consistency/outbox and job/chunk/idempotency semantics; Workspace and document core schema
- **Phase:** P0
- **Size:** L

#### SearchOutbox and IndexChunkTask tables
- **Role:** Data
- **Description:** SearchOutbox (OutboxId, WorkspaceId, DocumentId, DocumentVersion, EventType, SearchGeneration, Priority, timestamps; payload-free per ADR-001) and IndexChunkTask (§21 fields + SearchGeneration, TaskKind {Import, BulkCoding, Reindex}, nullable SnapshotId, chunk membership reference). Retention via time partitions.
- **Acceptance criteria:**
  - An interactive coding transaction inserts exactly one SearchOutbox row; a bulk/import chunk transaction inserts exactly one IndexChunkTask and no SearchOutbox rows (asserted in tests).
  - Dispatched rows older than the retention window are removed by partition drop, without a DELETE storm.
- **Dependencies:** ADR-001 and ADR-010: Search consistency/outbox and job/chunk/idempotency semantics; Job and JobChunk state machine
- **Phase:** P0
- **Size:** M

#### Outbox dispatcher service
- **Role:** Backend
- **Description:** `Opportunity.Worker.Dispatcher`: claims SearchOutbox and IndexChunkTask rows with `FOR UPDATE SKIP LOCKED` in batches. A LISTEN/NOTIFY wakeup handles low latency, with polling as the fallback. Publishes with confirms, then marks dispatched. Optional per-document coalescing of pending outbox rows. Horizontally scalable (N instances).
- **Acceptance criteria:**
  - Outbox insert → message published p95 < 100 ms in the dev profile with idle load.
  - Running 3 dispatcher instances produces no lost rows; duplicates are allowed but bounded and harmless (verified end-to-end).
  - Broker outage: rows accumulate and drain after recovery; `outbox_oldest_age_seconds` alerts fire.
- **Dependencies:** SearchOutbox and IndexChunkTask tables; RabbitMQ transport adapter
- **Phase:** P0
- **Size:** L

#### Idempotent consumer framework
- **Role:** Backend
- **Description:** Consumer pipeline middleware: envelope validation, workspace status fence, idempotency check (inbox table or JobChunk conditional transition), OTel span, retry classification (transient vs permanent), ack after commit.
- **Acceptance criteria:**
  - The fault-injection suite delivers every message 1–3 times, crashes randomly before/after commit, and final state is identical to single delivery (100% idempotent, §26 gate).
  - Permanent errors skip retries and mark the chunk Failed with LastError.
- **Dependencies:** RabbitMQ transport adapter; Job and JobChunk state machine
- **Phase:** P0
- **Size:** M

#### Job monitoring, dead-letter inspection and PG-driven replay API
- **Role:** Backend
- **Description:** Admin API: list failed chunks/tasks/outbox rows from PG with errors; replay = reset status/attempts in PG and let the dispatcher re-publish. The DLQ is retained for diagnostics only and purged after inspection. Also covers `/api/v1/workspaces/{ws}/jobs` list/detail/cancel, showing chunk progress, errors, and search-index progress separately from PG commit progress (§1 job monitoring).
- **Acceptance criteria:**
  - Replaying a failed IndexChunkTask after fixing the cause indexes the current PG state and advances the watermark.
  - Replay is audited and permission-restricted to Admin.
  - DLQ depth > 0 raises an alert metric.
  - Cancel moves pending chunks to Cancelled; running chunks finish or abort at the next fence point.
- **Dependencies:** Outbox dispatcher service; Idempotent consumer framework; Search generation watermark
- **Phase:** P1
- **Size:** M

---

### EPIC: Search Projection and Query Service

Build the OpenSearch projection, index management, version-safe index workers, the logical search service with mandatory workspace filtering, the minimal query language, and search-freshness observability.
**Implements:** §8, §9, §23, §24, §28, §31.5, §31.7, §33 (minimal AST).

#### Index management and logical index resolution
- **Role:** Search
- **Description:** `IIndexManager`: maps (WorkspaceId, ProjectionGeneration) to a physical index/alias and routing. The initial strategy is a shared index with `routing=workspaceId`, with dedicated-index placement supported. Creates indexes from versioned mapping templates.
- **Acceptance criteria:**
  - Application code never references physical index names (architecture test).
  - A workspace can be configured shared or dedicated; both pass the same search test suite.
- **Dependencies:** ADR-006, ADR-007 and ADR-008: Index strategy, mapping strategy and minimal query language
- **Phase:** P0
- **Size:** M

#### Baseline search projection mapping
- **Role:** Search
- **Description:** Strict mapping with every §23 field (workspaceId, documentId, controlNumber, familyId, parentDocumentId, familySequence, duplicateGroupId, emailThreadId, securityTags, fileName, fileType, mimeType, documentDate, metadata, text, projectionVersion), typed custom-field container, and interim coding fields (Candidate A) behind a projection builder interface.
- **Acceptance criteria:**
  - Mapping test asserts every §23 field is present with the agreed type.
  - Unknown fields are rejected (`dynamic: strict`); custom fields go to typed containers only.
  - The projection builder is swappable per ADR-004 outcome without changing search service callers.
- **Dependencies:** Index management and logical index resolution; Field definitions and typed metadata
- **Phase:** P0
- **Size:** M

#### Version-safe interactive index worker
- **Role:** Search
- **Description:** Consumes SearchOutbox messages and reads current PG state + DocumentVersion. Writes with the ADR-001 primitive (external version on full index, or a guarded update). Treats a missing PG row as a delete. Handles version-conflict responses as success (a newer write already applied).
- **Acceptance criteria:**
  - Test: deliver v5 then a delayed v3 → index holds v5; 0 stale overwrites over 10K randomized reorderings.
  - Single coding → searchable p95 < 1 s on the dev profile with refresh interval 1 s (measured, not assumed).
  - Security-priority messages are consumed from the priority lane ahead of ordinary coding.
- **Dependencies:** Outbox dispatcher service; Baseline search projection mapping; Idempotent consumer framework
- **Phase:** P0
- **Size:** M

#### Chunk index worker for IndexChunkTask
- **Role:** Search
- **Description:** Executes §21 steps 1–6: resolve membership (snapshot range or import batch), read current PG state + versions in pages, build projections, send byte-bounded OpenSearch `_bulk` sub-requests with version semantics, handle 429 backpressure with backoff, and mark complete only after all items succeed or are version-conflict no-ops.
- **Acceptance criteria:**
  - Bulk request size never exceeds the configured byte limit (test with 10 MB text documents).
  - A retried old bulk task never overwrites a newer interactive edit (§21; fault-injection test).
  - Partial bulk failures retry only failed items; the task completes idempotently.
- **Dependencies:** SearchOutbox and IndexChunkTask tables; Baseline search projection mapping; Idempotent consumer framework
- **Phase:** P0
- **Size:** L

#### Search generation watermark
- **Role:** Search
- **Description:** Per-workspace generation: a commit-ordered counter (a per-workspace counter row updated in the same transaction, or a sequence plus low-watermark tracking). Each SearchOutbox row and IndexChunkTask carries a generation; the watermark is "highest N such that all work ≤ N is applied". Exposed via API and search-response metadata so the UI can render "Bulk coding committed / Search index updating / Index current through generation N; job generation M" (§28).
- **Acceptance criteria:**
  - The watermark never advances past an unapplied generation, including under concurrent out-of-order commits (property-based test).
  - API returns `indexedThroughGeneration`, plus `jobGeneration` per job; the UI can show "Index current through N; job generation M".
  - Search responses include the watermark and `isProjectionCurrent`; during a bulk job it stays false until the watermark ≥ job generation (end-to-end test).
- **Dependencies:** SearchOutbox and IndexChunkTask tables; Chunk index worker for IndexChunkTask
- **Phase:** P0
- **Size:** M

#### Logical search service with mandatory workspace filter
- **Role:** Search
- **Description:** A single `ISearchService` path that takes the authenticated workspace context, always injects the `workspaceId` filter and routing, applies search-side security filters (defence in depth), and returns candidate hits only (§24).
- **Acceptance criteria:**
  - Automated tests attempt cross-workspace access via crafted queries, field names, saved searches and routing manipulation; 0 leaks (§23).
  - No code path outside the search service constructs OpenSearch queries (architecture test).
  - Simple search p95 is measured and recorded on the 1M dataset.
- **Dependencies:** Index management and logical index resolution; OIDC authentication and workspace RBAC
- **Phase:** P0
- **Size:** M

#### Minimal query parser and AST
- **Role:** Search
- **Description:** Parser for the ADR-008 subset: AND/OR/NOT, grouping, phrase, field:value, ranges, trailing wildcard, escaping. Produces a typed AST with source positions for error messages. Proximity (`W/n`) is parsed into an AST node but may be deferred in the planner.
- **Acceptance criteria:**
  - All supported §9 examples parse to the expected AST (golden tests); malformed queries return a positioned error.
  - Fuzz test: no parser crash or unbounded runtime on 100K random inputs.
  - Raw OpenSearch DSL is never accepted from clients.
- **Dependencies:** ADR-006, ADR-007 and ADR-008: Index strategy, mapping strategy and minimal query language
- **Phase:** P0
- **Size:** M

#### Search planner and field resolution
- **Role:** Search
- **Description:** AST → OpenSearch query translation. Resolves field names against workspace FieldDefinitions (types decide term/range/match), the leading-wildcard policy, query-cost limits and timeouts. Includes saved searches: query text + AST version persisted in PG, re-parsed at execution, usable as snapshot input.
- **Acceptance criteria:**
  - Each AST node type has a translation test against a real OpenSearch container.
  - Unknown fields produce a user-facing error, never a silent match-none.
  - Wildcard expansion limits are enforced with a clear error.
  - A saved search referencing a deleted field returns a clear validation error.
- **Dependencies:** Minimal query parser and AST; Logical search service with mandatory workspace filter; Baseline search projection mapping
- **Phase:** P0
- **Size:** M

#### Family and duplicate expansion
- **Role:** Search
- **Description:** Expand search result sets to full families and/or duplicate groups (§9), for both interactive search and snapshot materialization.
- **Acceptance criteria:**
  - Expansion over a 1M-document family-heavy dataset (1:3) completes within a recorded budget.
  - Expanded membership in a materialized snapshot is stable after later family edits.
- **Dependencies:** Search planner and field resolution; Snapshot service with deterministic PIT vs materialized rule
- **Phase:** P1
- **Size:** M

#### Alias-based reindex
- **Role:** Search
- **Description:** Reindex job: create a new ProjectionGeneration index, fill it via IndexChunkTask(kind=Reindex) from PG, catch up interactive changes (dual-target dispatch during the window), validate counts/checksums, then atomically switch the alias (§8).
- **Acceptance criteria:**
  - Coding during the reindex is visible in the new index after the switch (no lost updates).
  - Validation failure aborts without switching; the old alias remains serving.
- **Dependencies:** Chunk index worker for IndexChunkTask; Index management and logical index resolution
- **Phase:** P1
- **Size:** L

#### Proximity, term-hit reporting and full legal query language
- **Role:** Search
- **Description:** Implement `W/n` (span queries), term-hit reports, and the remaining legal syntax beyond the MVP subset.
- **Acceptance criteria:**
  - `apple W/10 iphone` returns correct results on the golden corpus.
  - Term-hit report counts match the planner for each AST term.
- **Dependencies:** Search planner and field resolution
- **Phase:** P2
- **Size:** L

---

### EPIC: Minimal Ingestion (DAT/OPT Import)

Import load files with text, natives and images into PG and object storage, with families and duplicates resolved, in bounded idempotent chunks that atomically create IndexChunkTasks.
**Implements:** §5, §12, §20/§32 (IMPORT → INDEX), §21 (import chunk tasks).

#### DAT/OPT load-file parsers and field mapping
- **Role:** Backend
- **Description:** Streaming parsers for Concordance DAT (configurable delimiters, encodings, multi-line values) and Opticon OPT (page/image mapping). Never loads a whole file into memory. Field mapping API maps columns to structural columns or FieldDefinitions with a coercion preview on the first N rows; mapping profiles saved per workspace.
- **Acceptance criteria:**
  - Parses a 1M-row DAT with constant memory (<200 MB working set).
  - Malformed rows are reported with line numbers; the import continues or aborts per policy.
  - Encoding detection/override supports UTF-8, UTF-16 and Windows-1252.
  - Mapping preview shows coerced values and per-column error counts; unmapped columns are explicitly ignored or auto-created as Text fields.
- **Dependencies:** Solution scaffolding and project layering
- **Phase:** P0
- **Size:** M

#### Import job orchestration with chunk-level index tasks
- **Role:** Backend
- **Description:** Import Job split into JobChunks by row range. Each chunk transaction: insert/overlay Documents and metadata, set DocumentVersion, record the import batch membership, and insert one IndexChunkTask (kind=Import) in the same commit (§12, §21). Overlay imports update by ControlNumber, bump DocumentVersion and never overwrite coding (P1 extension).
- **Acceptance criteria:**
  - Each committed import chunk has exactly one IndexChunkTask; no SearchOutbox rows are created by import.
  - Re-running a chunk after a crash produces no duplicate documents (idempotency by ControlNumber + ImportBatchId).
  - 1M-document import completes end-to-end (PG + index) and throughput is recorded.
  - An overlay running concurrently with interactive coding loses neither change.
- **Dependencies:** DAT/OPT load-file parsers and field mapping; Job and JobChunk state machine; SearchOutbox and IndexChunkTask tables
- **Phase:** P0
- **Size:** L

#### Native, text and image file ingestion
- **Role:** Backend
- **Description:** Stream natives, extracted TXT and images/PDFs referenced by the load file into object storage using content-hash addressing; compute MD5/SHA256; store artifact keys on Document. Extracted text is stored in object storage (and optionally size-capped in PG) per the ADR-011 decision.
- **Acceptance criteria:**
  - Missing referenced files are reported per document without failing the whole chunk.
  - Hashes match independently computed values; retries re-upload nothing.
  - Text >10 MB is handled (streamed), with the indexed-text truncation policy applied and recorded.
- **Dependencies:** Object storage abstraction and addressing; Import job orchestration with chunk-level index tasks
- **Phase:** P0
- **Size:** M

#### Family, duplicate and thread identity assignment
- **Role:** Data
- **Description:** Resolve FamilyId, ParentDocumentId and FamilySequence from BegAttach/EndAttach or ParentId columns per ADR-009, including families spanning chunk boundaries (post-pass job). Assign DuplicateGroupId by the configured workspace-scoped hash policy and carry the imported EmailThreadId; dedup is informational in MVP (§5).
- **Acceptance criteria:**
  - Families spanning chunk boundaries resolve correctly (test with chunk size 10 on a family of 25).
  - Orphan attachments are flagged, not silently dropped.
  - Family changes bump DocumentVersion and produce an IndexChunkTask.
  - On the synthetic 20%-duplicate corpus, duplicate groups match generator ground truth and stay stable on re-import.
- **Dependencies:** Import job orchestration with chunk-level index tasks; ADR-003, ADR-005 and ADR-009: Metadata model, interim partitioning and family/dedupe/thread identity
- **Phase:** P0
- **Size:** M

---

### EPIC: Coding, Snapshots and Bulk Operations

Deliver interactive coding with low-latency searchability, deterministic frozen bulk targets, and chunked bulk coding that writes provenance and chunk-level index tasks, plus reviewer-visible freshness.
**Implements:** §7, §10, §14 (provenance), §21, §22, §24 (security-affecting coding), §27, §28, §31.1, §31.4.

#### Interactive coding API
- **Role:** Backend
- **Description:** `PUT /documents/{id}/coding` with optimistic concurrency (`If-Match: DocumentVersion`). One transaction updates current coding, writes the CodingEvent, increments DocumentVersion, and inserts a SearchOutbox row (priority lane if the field is security-affecting).
- **Acceptance criteria:**
  - A stale If-Match returns 412; there are no lost updates under 100 concurrent reviewers on the same documents.
  - Code → visible in search p95 ≤ 1 s measured on the dev profile (§17 target, recorded not assumed).
  - Security-affecting changes are enforced by the authorization service immediately (§24 test).
- **Dependencies:** Interim coding current-state and provenance tables; Version-safe interactive index worker; Authoritative protected-resource authorization service
- **Phase:** P0
- **Size:** M

#### Snapshot service with deterministic PIT vs materialized rule
- **Role:** Search
- **Description:** `DocumentSetSnapshot` (SnapshotId, WorkspaceId, QueryDefinition, SearchGeneration at creation, creator/time/purpose, DocumentCount, MaterializationStrategy). Materialization pages the search via PIT + `search_after` into ordered membership storage per ADR-002, optionally with family/duplicate expansion. A rule engine selects PIT vs Materialized deterministically from job type and estimated runtime.
- **Acceptance criteria:**
  - Export/production snapshots are always materialized (§22).
  - Membership is identical before and after subsequent coding/reindex/alias switch (test).
  - Chunk iteration over a materialized snapshot is restartable by sequence with no gaps or overlaps.
- **Dependencies:** ADR-002: Bulk snapshot semantics; Logical search service with mandatory workspace filter
- **Phase:** P0
- **Size:** L

#### Bulk coding job and worker
- **Role:** Backend
- **Description:** `Opportunity.Worker.BulkCoding`: bulk job over a snapshot → JobChunks. Per chunk, one PG transaction applies coding to the bounded set (set-based SQL / COPY to a temp table), writes CodingEvents, increments versions, and creates one IndexChunkTask. Supports add/remove/set semantics, all idempotent.
- **Acceptance criteria:**
  - A bulk tag of 1M documents completes; throughput, WAL volume and chunk duration are recorded.
  - Chunk replay produces identical final state and no duplicate CodingEvents.
  - Interactive edits made during the bulk job are not overwritten in PG or in OpenSearch (§21 test).
- **Dependencies:** Snapshot service with deterministic PIT vs materialized rule; Job and JobChunk state machine; Chunk index worker for IndexChunkTask
- **Phase:** P0
- **Size:** L

#### Coding provenance and review batch domain support
- **Role:** Backend
- **Description:** Domain support for ReviewBatch, assignments, reviewer status and conflict detection, built on CodingEvent provenance. Full batching/QC workflow and UI are P2 (§33) and are not part of this ticket.
- **Acceptance criteria:**
  - Coding history API returns who/when/what/job for any document.
  - Batches can be created from a snapshot and assigned to users.
- **Dependencies:** Interim coding current-state and provenance tables; Snapshot service with deterministic PIT vs materialized rule
- **Phase:** P1
- **Size:** M

---

### EPIC: Rendering, Viewer, Redactions and Export

Make documents viewable through authorized endpoints, render images/PDFs and thumbnails, store non-destructive redactions, and export/produce frozen document sets with load files.
**Implements:** §13, §14, §24, §20/§32 (VIEW, EXPORT).

#### Document viewer API with authoritative re-check
- **Role:** Backend
- **Description:** Endpoints for metadata, extracted text, page images, PDF and native download. Each call invokes the authorization service, then streams from object storage (or issues a short-lived presigned URL only after authorization).
- **Acceptance criteria:**
  - A stale search hit for a newly privileged document cannot be opened (test with the index worker paused).
  - Range requests are supported for large natives; every access is audited.
- **Dependencies:** Authoritative protected-resource authorization service; Native, text and image file ingestion
- **Phase:** P0
- **Size:** M

#### Render worker pipeline for imported PDFs and images
- **Role:** Backend
- **Description:** `Opportunity.Worker.Rendering`: generate page images and thumbnails from imported PDFs/TIFFs behind an `IRenderer` abstraction. Chunked jobs; outputs addressed deterministically. The native rendering engine is not chosen here (§13, §33).
- **Acceptance criteria:**
  - A 500-page PDF renders to page images + thumbnails, idempotently on retry.
  - The licence of every rendering dependency is recorded and is OSI-compatible.
- **Dependencies:** Job and JobChunk state machine; Object storage abstraction and addressing
- **Phase:** P1
- **Size:** M

#### Native rendering technology selection
- **Role:** Platform
- **Description:** Evaluate native-to-PDF engines (e.g. LibreOffice headless, Gotenberg, commercial) for fidelity, licensing and scalability; implement the chosen `IRenderer`.
- **Acceptance criteria:**
  - Fidelity scorecard on a 200-document format test set; decision recorded as an ADR.
- **Dependencies:** Render worker pipeline for imported PDFs and images
- **Phase:** P2
- **Size:** L

#### Non-destructive redaction annotations
- **Role:** Backend
- **Description:** Redaction storage per ADR-012: page-normalized rectangles with type/reason, author, timestamps, versioned; CRUD API with authorization; source files never modified.
- **Acceptance criteria:**
  - Redactions survive re-rendering at different resolutions (normalized coordinates test).
  - Every change is audited; deleted redactions remain in history.
- **Dependencies:** Document viewer API with authoritative re-check; ADR-011 to ADR-014: Object storage addressing, redactions, audit and retention lifecycle
- **Phase:** P1
- **Size:** M

#### Basic export job
- **Role:** Backend
- **Description:** `Opportunity.Worker.Export`: export from a materialized snapshot. Chunked: re-authorizes each document, writes natives/text/images, DAT/OPT load files, and a manifest with SHA256 checksums to object storage.
- **Acceptance criteria:**
  - Export of a 100K-document snapshot is byte-reproducible (same manifest checksums) when re-run.
  - Documents that became restricted after snapshot creation are excluded and reported (§24).
  - Restart after a worker crash resumes from the last completed chunk.
- **Dependencies:** Snapshot service with deterministic PIT vs materialized rule; Document viewer API with authoritative re-check; Job and JobChunk state machine
- **Phase:** P0
- **Size:** L

#### Basic production with Bates numbering and burned redactions
- **Role:** Backend
- **Description:** `Opportunity.Worker.Production`: production definition (snapshot, family policy, Bates prefix/start, endorsements), deterministic Bates assignment in sequence, burn redactions + Bates into derived images/PDF, load files, privilege-log stub, manifest/QC report.
- **Acceptance criteria:**
  - Bates numbers are gap-free and stable across a restarted production.
  - Burned output contains no recoverable redacted text (text-layer check).
  - The production definition is stored and is reproducible.
- **Dependencies:** Basic export job; Non-destructive redaction annotations; Render worker pipeline for imported PDFs and images
- **Phase:** P1
- **Size:** L

---

### EPIC: Scale Benchmarks and Architecture Spikes

Build the synthetic data generator and benchmark harness, then run the timeboxed ADR-004 and PostgreSQL coding spikes against frozen gates to unblock the vertical slice's coding/search path.
**Implements:** §6, §17, §18, §25–§27, §29, §30, §31.2, §31.3.

#### Synthetic corpus generator
- **Role:** Platform
- **Description:** `tools/Opportunity.DataGenerator`: seeded generation of DAT/OPT/text/natives matching §29 (~30 custom fields, 1:3 families, ~20% duplicates, heavy-tailed text up to ~10 MB), with direct-to-PG/OpenSearch fast paths for 1M/10M runs and ground-truth files.
- **Acceptance criteria:**
  - The same seed produces byte-identical output.
  - Generates 1M documents in < 1 h on reference hardware; distribution statistics are reported and match the configuration.
- **Dependencies:** Solution scaffolding and project layering
- **Phase:** P0
- **Size:** M

#### Benchmark harness and reference environment
- **Role:** Platform
- **Description:** Workload driver (e.g. NBomber/k6) for the §29 query mix (60/30/10) with 100 concurrent reviewers and think time, plus a background bulk coding+indexing load. Records hardware, versions, JVM, durability, shard topology, seed and scripts with p50/p95/p99. Freeze the reference hardware and the §26 gates before the comparative run.
- **Acceptance criteria:**
  - One command runs a profile and emits a results bundle (JSON + environment manifest).
  - Gate values are committed and frozen (signed off) before the first comparative run.
  - Index lag and stale-overwrite counters are captured automatically.
- **Dependencies:** Synthetic corpus generator; OpenTelemetry baseline
- **Phase:** P0
- **Size:** L

#### Fault-injection correctness suite
- **Role:** Platform
- **Description:** Automated chaos tests: kill workers mid-chunk, duplicate/reorder/delay messages, restart the broker and OpenSearch, network partitions (Toxiproxy). Assert 0 stale overwrites, 0 unauthorized retrievals and 100% idempotency (§26).
- **Acceptance criteria:**
  - Suite runs nightly and on demand against each ADR-004 candidate.
  - Any invariant violation fails the run with a reproducible seed.
- **Dependencies:** Idempotent consumer framework; Chunk index worker for IndexChunkTask; Authoritative protected-resource authorization service
- **Phase:** P0
- **Size:** L

#### ADR-004 projection spike comparative run
- **Role:** Search
- **Description:** Timeboxed (one sprint) implementation of Candidates A (with coalescing and refresh-tuning variants, full reindex vs guarded update), B, C and D (parent/child: has_child latency, global ordinals, routing). Measured at 1M against the frozen gates, including security-filter placement and family queries.
- **Acceptance criteria:**
  - Every candidate has gate results or a documented disqualifying failure.
  - Correctness/security gates are evaluated before throughput; results feed ADR-004a/004b.
  - The spike code is isolated behind the projection builder interface and the losing candidates are removed afterwards.
- **Dependencies:** Benchmark harness and reference environment; Fault-injection correctness suite; Baseline search projection mapping
- **Phase:** P0
- **Size:** L

#### PostgreSQL coding model spike
- **Role:** Data
- **Description:** Benchmark current-state + append-only history variants (row-per-field vs choice arrays vs JSONB coding; fillfactor; HOT; chunk transaction sizes) at 1M/10M-document bulk coding. Measure WAL, bloat, autovacuum, lock contention and retry cost (§27). The comparative partitioning benchmark that finalizes ADR-005 is a P2 follow-up (§33).
- **Acceptance criteria:**
  - Results table for each variant covers every §27 metric.
  - A recommended chunk size (docs + bytes) is fed into ADR-010.
  - Recommendation is consistent with the ADR-004 outcome.
- **Dependencies:** Benchmark harness and reference environment; Interim coding current-state and provenance tables
- **Phase:** P0
- **Size:** L

#### 10M architecture validation run
- **Role:** Search
- **Description:** Repeat the winning design at 10M (§29 Phase 2): segment merges, disk amplification, backpressure, p95/p99 degradation, long-running snapshots, PG WAL/bloat/autovacuum.
- **Acceptance criteria:**
  - Published report; any material regression vs 1M blocks the 100M roadmap (§29) and opens ADR revisions.
- **Dependencies:** ADR-004a/004b: Coding storage and coding search projection; Bulk coding job and worker
- **Phase:** P1
- **Size:** L

---

### Vertical-slice critical path (P0 summary)

Scaffolding → migrations/schema → contracts + RabbitMQ → job state machine → outbox tables + dispatcher → index workers (interactive + chunk) → mapping + search service + parser/planner → import (DAT/OPT + chunk tasks) → viewer with re-auth → interactive coding → snapshot service → bulk coding → watermark/freshness → export. In parallel: data generator → harness → fault suite → ADR-004 and PG coding spikes, which gate the transition from "interim Candidate A" to the final design (§31.2–31.3).

Ticket count: 7 platform + 7 ADR + 7 domain + 7 messaging + 11 search + 4 import + 4 coding + 6 rendering/export + 6 benchmarks = **59 tickets** in 9 epics, slightly above the 40–55 target. Saved searches, overlay import, the freshness API, job monitoring, REST conventions and several ADRs are folded into their host tickets; ADRs are grouped so each §19 item has an owner without separate tracking noise.

---

## Open questions for the product owner

1. **Coding during bulk jobs:** If a reviewer interactively changes a field on a document while a bulk job targeting the same field is in flight, which value should win in PostgreSQL: last-commit-wins, or should bulk skip documents edited after the snapshot was taken? This is a product rule, not only a technical one, and it affects provenance reporting.
2. **Reproducibility scope of exports/productions:** Must a re-run of an export reproduce the *content as of snapshot time* (requires versioned metadata/coding history reads) or only the *membership* (current content of the frozen document IDs)? §22 guarantees membership only.
3. **Dedup policy for MVP:** Is DuplicateGroupId informational only in MVP, or must review/export support suppression of duplicates (global vs custodian-level), and which hash defines a duplicate for email vs loose files?
4. **Staleness tolerance in the UI:** Is it acceptable that search counts during bulk indexing are visibly stale with a freshness banner (§28), or do some workflows (e.g. privilege review) require blocking until the index is current?
5. **Security-affecting fields:** Which fields are security-affecting in MVP (privilege, confidentiality, ethical walls), and are document-level restrictions in scope for MVP or only workspace RBAC (§15 says "optional")?
6. **Deployment target for the first users:** Is MVP Lite-profile/single-tenant (self-hosted per firm) or a multi-tenant hosted service? That drives shared-index placement, RLS rigor and the per-workspace key roadmap.
7. **Expected workspace distribution:** Roughly how many workspaces per installation, and what size distribution? This sets the shared vs dedicated index thresholds (§8) and whether hash partitioning (§6) is worth the complexity.
8. **Text size policy:** Should extracted text beyond a cap (e.g. 10 MB) be truncated for indexing with a flag, or must every character be searchable? This affects bulk request sizing and ADR-004 costs.
9. **Production in MVP depth:** Does MVP need full production (Bates, burned redactions, endorsements, privilege log) or is "export + basic production" (Bates + burned redactions only) sufficient for the first release?
10. **Benchmark hardware budget:** Who provides the frozen enterprise reference environment (3-node OpenSearch, PG primary+replica), and is there a budget/timebox for running 10M validation before the 1.0 release?
