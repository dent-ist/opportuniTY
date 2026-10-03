# opportuniTY Architecture

## Scalable Open-Source eDiscovery Review Platform

> **Status:** Normative implementation baseline (see §35).
> **Source:** Imported from the architecture requirement artifact, converted to Markdown. Content is unchanged; tables were reformatted.

> **Design philosophy:** opportuniTY should be an open-source, horizontally scalable document review platform whose simplest deployment is easy to run while its core boundaries remain suitable for enterprise-scale matters.

## 1. Goals and Scope

Design toward **100M+ document matters**, proving scale progressively: correctness → 1M → 10M → 100M.

**Initial scope:** workspace management, DAT/OPT import with text/natives, search, saved searches, viewer, coding/bulk coding, family/duplicate relationships, redactions, export/basic production, audit and job monitoring.

**Deferred:** native processing, advanced OCR, thread analytics UI, TAR, AI, collection/legal hold, advanced QC and Kubernetes-first deployment.

## 2. Core Principles

1. PostgreSQL is authoritative transactional state; OpenSearch is a derived search projection; object storage contains artifacts; RabbitMQ transports asynchronous work.
2. Import, indexing, bulk coding, rendering, export, production, OCR and AI run asynchronously.
3. `WorkspaceId` is a first-class authorization, data, search, storage, job, audit and lifecycle boundary.
4. Assume retries, duplicate messages, worker crashes and temporary failures; handlers must be idempotent.
5. Start modular, not microservice-heavy: a .NET application plus independently scalable workers.

## 3. High-Level Architecture

```text
Angular UI -> Load Balancer -> .NET API pool
                              |-> PostgreSQL
                              |-> OpenSearch
                              |-> Object Storage
PostgreSQL -> Transactional Outbox -> Dispatcher -> RabbitMQ
                                                   |-> Import Workers
                                                   |-> Index Workers -> OpenSearch
                                                   |-> Render Workers
                                                   |-> Export Workers
                                                   |-> Production Workers
Future: OCR / extraction / analytics / AI workers
```

## 4. Technology Stack

| Layer | Technology | Notes |
|---|---|---|
| Frontend | Angular | Review grid, viewer, coding, admin |
| API | ASP.NET Core / .NET | Stateless API tier |
| Database | PostgreSQL | Source of truth |
| Search | OpenSearch | Distributed full-text/metadata search |
| Object storage | Provider-neutral abstraction | Azure Blob, S3, or compatible |
| Messaging | RabbitMQ initially | Job/command distribution |
| Cache | None initially | Add only for demonstrated need |
| Identity | OIDC/OAuth2 | Entra optional |
| Observability | OpenTelemetry | Traces, metrics, logs |
| Packaging | Docker / Compose profiles | Lite/full local deployments |
| Orchestration | Kubernetes optional | Enterprise scale later |

## 5. Core Domain and Document Model

```text
Workspace
 +-- Documents
 |    +-- Family
 |    +-- DuplicateGroup
 |    +-- EmailThread
 |    +-- Metadata / Coding / Redactions / DerivedArtifacts
 +-- FieldDefinitions / SavedSearches / DocumentSetSnapshots
 +-- ReviewBatches / Productions / Exports / Jobs / AuditEvents
 +-- Roles / Permissions
```

Core `Document` fields include DocumentId, WorkspaceId, ControlNumber, FamilyId, ParentDocumentId, FamilySequence, DuplicateGroupId, EmailThreadId, MD5/SHA256, file/type/date fields, artifact object IDs, `Metadata JSONB`, DocumentVersion and timestamps.

Families, duplicate groups and thread IDs belong in the model now even if all workflows arrive later. Deduplication policy remains configurable.

## 6. Metadata, Coding and PostgreSQL Partitioning

Do not default to pure EAV at 100M-document scale. Use typed structural columns plus `Metadata JSONB` for imported custom metadata. Field definitions specify Text, Keyword, Integer, Decimal, Date, Boolean, SingleChoice, MultiChoice and User. Keep high-write coding separate from relatively static imported metadata.

Benchmark hash-by-workspace, dedicated partitions for huge workspaces and hybrid approaches. Evaluate coding updates, bulk coding, workspace deletion, vacuum/bloat, index size and concurrent review before fixing the partition design.

## 7. PostgreSQL → OpenSearch Consistency

Never independently dual-write PostgreSQL and OpenSearch. Use a **Transactional Outbox**.

```text
PostgreSQL transaction:
  UPDATE Coding
  INSERT SearchOutbox
  COMMIT
       |
Outbox Dispatcher -> RabbitMQ -> Index Worker -> OpenSearch
```

`SearchOutbox` contains OutboxId, WorkspaceId, DocumentId, DocumentVersion, EventType, Payload and timestamps. Search updates carry monotonic versions so delayed older events cannot overwrite newer projections.

**Scope of this section:** the per-document outbox described here applies to interactive edits only. Bulk operations and import use chunk-level `IndexChunkTask` records (see §21).

**Searchability contract:** single-document coding target ≤1 second p95. For large bulk coding, PostgreSQL is authoritative immediately while UI exposes search-index progress. Bulk indexing requires batching, OpenSearch bulk operations, concurrency limits, retry/backoff, dead-letter handling, throttling and queue-depth/progress monitoring.

## 8. OpenSearch Architecture

```text
Small Workspace --> Shared index + WorkspaceId/routing
Large Workspace --> Dedicated index
Huge Workspace  --> Dedicated multi-shard index
```

Application code uses a logical search service; Index Management decides physical placement. Avoid arbitrary dynamic mappings for every imported field. Benchmark strict mappings, `flattened` where appropriate, typed custom-field containers and dedicated indexes. Shared indexes must enforce workspace scoping in the search layer while application authorization remains mandatory.

Use alias-based reindexing: build new index → reindex → validate → atomically switch logical alias. Benchmark shard size, replicas, refresh interval, bulk size, analyzers, highlighting, concurrency, lifecycle and snapshots.

## 9. opportuniTY Query Language

Do not expose raw OpenSearch DSL. Build `User Query -> Parser -> AST -> Search Planner -> OpenSearch` supporting Boolean, phrase, proximity, fields, ranges, wildcards, escaping, saved searches, family/duplicate expansion and term-hit reporting.

Examples: `contract AND termination`, `"trade secret"`, `apple W/10 iphone`, `custodian:"John Smith"`, `date:[2025-01-01 TO 2025-12-31]`, `filename:*.xlsx`.

## 10. Frozen Bulk Targets

> **Superseded in part by §22**, which defines the deterministic PIT-vs-materialized snapshot rule. §22 is normative.

Bulk operations need snapshot semantics because search results change during execution. Ordinary bulk work can use OpenSearch Point in Time plus `search_after`. Legally significant/reproducible operations use `DocumentSetSnapshot` containing SnapshotId, WorkspaceId, QueryDefinition, SearchGeneration, creator/time/purpose, DocumentCount and MaterializationStrategy. Exports/productions may materialize exact IDs when reproducibility requires it.

## 11. RabbitMQ, Jobs and Messaging

RabbitMQ transports work; PostgreSQL owns job state. `Job` stores identity/type/status, TargetSnapshotId, counts, creator/timestamps and CorrelationId. `JobChunk` stores sequence/status/count, AttemptCount, IdempotencyKey and errors. Do not send one message per document; use bounded idempotent chunks.

The earlier tiny `IMessageBus.SendAsync<T>()` is insufficient alone. Message envelopes need MessageId, MessageType, SchemaVersion, WorkspaceId, JobId, CorrelationId, CausationId, IdempotencyKey, CreatedAt, Attempt, Headers and Payload. Infrastructure defines publish/send, consume, acknowledgement, retry, backoff and dead-letter semantics. Keep RabbitMQ APIs outside domain/application logic. Evaluate Kafka separately for durable event streams.

## 12. Minimal Ingestion

"Processing deferred" does not mean "no import." MVP supports DAT, OPT, extracted TXT, natives, PDFs/images and configurable field mapping.

```text
DAT / OPT / TXT / Native -> Import Worker
                          |-> PostgreSQL
                          |-> Object Storage
                          |-> IndexChunkTask -> Index Worker -> OpenSearch
```

Import is bulk work: it creates chunk-level `IndexChunkTask` records atomically with each import chunk's PostgreSQL commit (see §21), not per-document outbox rows.

The first importer assumes upstream processing may have occurred elsewhere.

## 13. Rendering, Viewer and Redactions

```text
Native / Imported PDF / Image -> Render Worker -> Page Images / PDF / Thumbnails -> Object Storage
```

Choose native rendering technology separately because fidelity and licensing matter. Redactions are non-destructive page-coordinate annotations with type/reason, author and timestamps. Production burns redactions plus Bates/branding into derived output; source documents remain unchanged.

## 14. Production and Review Workflow

Production planning includes frozen document sets, family-aware selection, native/image/PDF output, burned redactions, Bates numbering, endorsements, load files/text, privilege-log support, validation/QC, manifests/checksums and reproducible definitions.

The model should permit review batches, assignments, reviewer status, coding provenance, QC/re-review and conflict detection. Advanced workflow UI can be phase 2.

## 15. Security, Audit and Lifecycle

Plan for workspace RBAC plus optional document/field restrictions, privilege-sensitive fields and ethical walls. Use TLS and infrastructure encryption at rest. Keep the design capable of per-workspace/customer keys without requiring them in v1.

Audit should be append-oriented and scalable: partitioned append-only PostgreSQL initially, archival to immutable/object storage later, and tamper evidence where compliance requires it.

Matter deletion may remove PostgreSQL data/partitions, OpenSearch data, storage prefixes, derived artifacts and keys, subject to retention/hold policy.

## 16. Cache and Deployment Profiles

Do **not** include Redis or Valkey in mandatory v1 infrastructure without a concrete need.

**Lite profile:** Angular, .NET API, PostgreSQL, OpenSearch, RabbitMQ, selected S3-compatible/local storage, essential workers.

**Full/scale profile:** multiple APIs, PostgreSQL, OpenSearch cluster, RabbitMQ, object storage, import/index/render/export/production worker pools and observability.

Choose bundled object-storage software after checking current license, maintenance status, S3 compatibility and developer experience.

## 17. Non-Functional Engineering Targets

These are **targets, not product guarantees**, until benchmarked on a defined environment.

| Metric | Initial target |
|---|---|
| Scale milestones | 1M → 10M → 100M docs |
| Simple search p95 | <1 sec |
| Complex search p95 | <3 sec |
| Single coding → searchable p95 | <1 sec |
| Concurrent reviewers | 100 initial target |
| Bulk coding | ≥10K docs/sec target |
| Import/index throughput | Benchmark-driven |
| API availability | 99.9% target |
| RPO | ≤5 min target |
| RTO | ≤1 hour target |

The bulk-coding target is provisional and not assumed; see §30.

Every benchmark records hardware, corpus shape, text size, field counts, query mix, concurrency, shard layout and durability settings. Measure p50/p95/p99.

## 18. Scale Testing and Repository

Build synthetic data early and test 1M/10M/100M docs, many fields, large text, family/duplicate-heavy sets, concurrent search/coding, bulk coding during search, index lag/backpressure, worker failure/retry, reindexing, deletion and production/export throughput.

```text
opportunity/
  src/
    Opportunity.Api / Core / Data / Search / Storage / Messaging
    Opportunity.Security / Jobs / Import / Rendering / Production
    Opportunity.Worker.Import / Indexing / Rendering / Export / Production
    Opportunity.Web
  tests/
    UnitTests / IntegrationTests / ScaleTests
  tools/Opportunity.DataGenerator
  deploy/docker-compose/
  deploy/kubernetes/ # later
  docs/architecture/ and docs/adr/
```

## 19. Priority ADRs

1. PostgreSQL ↔ OpenSearch consistency/outbox
2. Bulk snapshot semantics
3. Metadata/custom-field model
4. Coding storage
5. PostgreSQL partitioning
6. OpenSearch index strategy
7. Search mapping strategy
8. Query language
9. Family/dedupe/thread identity
10. Job/chunk/idempotency semantics
11. Object-storage addressing
12. Redactions
13. Audit architecture
14. Retention/deletion lifecycle

## 20. First Vertical Slice

```text
DAT / OPT / Natives / Text -> IMPORT -> INDEX -> SEARCH -> VIEW DOCUMENT
                                              -> CODE / TAG -> SEARCH CODING
                                              -> BULK TAG -> EXPORT
```

Correctness comes first. Search/coding consistency must be observable, bulk targets frozen, retries safe, family relationships preserved and scale tests automated. Do not start with AI, TAR, a broad microservice estate or every enterprise workflow.

### Design Principle to Preserve

> **Build the architecture so 100M documents are possible; build the product so the first useful workflow arrives early.**

---

## 21. Bulk Indexing Contract

### Interactive edits

Interactive edits may create document-level search-outbox records:

```text
Reviewer edit
   |
PostgreSQL transaction
   +-- update authoritative coding
   +-- increment DocumentVersion
   +-- insert document search-outbox record
   |
Index Worker
   |
OpenSearch
```

### Bulk edits

Bulk operations create **chunk-level** indexing tasks, not one outbox/message per document:

```text
BulkJob
   |
Materialized/Stable Target
   |
JobChunks
   |
Bulk Coding Worker
   |
PostgreSQL transaction
   +-- mutate authoritative coding for bounded chunk
   +-- write coding provenance/history
   +-- create IndexChunkTask
   |
Dispatcher -> RabbitMQ -> Index Worker
```

`IndexChunkTask` is itself the durable outbox record for bulk indexing. Do not create a second per-document outbox stream for the same bulk operation.

### Payload-free, self-healing IndexChunkTask

```text
IndexChunkTask
  TaskId
  WorkspaceId
  JobId
  ChunkId
  SnapshotId
  ProjectionGeneration
  Status
  AttemptCount
  CreatedAt
  StartedAt
  CompletedAt
  LastError
```

The task carries identifiers, not document projection payloads.

When executing a task, the worker:

1. Resolves the chunk's exact document IDs.
2. Reads the **current authoritative PostgreSQL state** for those documents.
3. Reads each current PostgreSQL `DocumentVersion`.
4. Builds the required search projection/update.
5. Writes to OpenSearch using external/version-aware semantics tied to the authoritative document version.
6. Marks the task complete only after the bounded indexing operation succeeds.

A retried old bulk task must never overwrite a newer interactive edit.

## 22. Snapshot Semantics

Use a deterministic rule.

```text
Short-lived
AND exact restart not required
AND runtime safely within PIT lifetime
        |
        v
OpenSearch PIT + search_after
```

Otherwise:

```text
Long-running
OR exact restart required
OR must survive alias/index changes
OR export/production/legal reproducibility required
        |
        v
Materialized DocumentSetSnapshot
```

Exports and productions use durable/materialized snapshots by default.

A materialized snapshot must preserve exact membership independent of subsequent coding/search changes. The implementation may use database membership rows, immutable chunked ID manifests in object storage, or another benchmarked representation.

## 23. Search Projection Baseline

The initial search mapping must contain structural and security-relevant fields from day one:

```text
workspaceId
documentId
controlNumber
familyId
parentDocumentId
familySequence
duplicateGroupId
emailThreadId
securityTags
fileName
fileType
mimeType
documentDate
metadata
text
projectionVersion
```

The representation of high-churn coding remains governed by ADR-004.

At high workspace counts, do not create one filtered alias per workspace by default. Shared indexes use routing plus a single search-service path that **always injects the authenticated workspace filter**. Automated tests must attempt cross-workspace access. Large or sensitive matters may receive dedicated indexes. OpenSearch document-level security can provide defense in depth.

## 24. Security Consistency Rules

Search is never the final authorization decision.

```text
Search
  |
OpenSearch returns candidate hit
  |
User opens/views/downloads/exports
  |
Authoritative authorization check
  |
PostgreSQL / security domain
  |
ALLOW or DENY
```

Every open, view, native download, image retrieval, export, production inclusion, and other protected-content operation must re-check authoritative access.

### Security-affecting coding

If coding changes privilege, confidentiality, ethical-wall membership, or another access-control attribute:

1. The authoritative PostgreSQL security state changes first.
2. The new restriction is enforced immediately by protected-resource authorization.
3. Search projection updates receive a stronger consistency/priority SLA than ordinary review coding.
4. A stale search hit may temporarily remain visible, but it must not permit retrieval of the protected content.
5. Removal of access must never depend on OpenSearch refresh completing.

This keeps OpenSearch outside the final security boundary.

## 25. ADR-004 — High-Volume Coding Search Projection

**Status: UNRESOLVED — benchmark required before implementation is fixed.**

### Candidate A — Unified document projection

Content, metadata and coding live in one search document.

Benefits: simplest combined legal queries.

Risk: high-churn coding may cause excessive reindexing/merge pressure for large text-bearing documents.

The spike must test two mitigations:

- **Coalescing:** combine multiple pending coding changes for the same document into one index update.
- **Bulk refresh tuning:** safely increase/adjust refresh interval during controlled bulk indexing, then restore normal interactive behavior.

### Candidate B — Separate coding index

```text
DocumentContentIndex
  documentId / workspaceId
  family / duplicate / security fields
  metadata / text

DocumentCodingIndex
  documentId / workspaceId
  coding / review status / privilege
  projectionVersion
```

Benefit: small high-write coding projection.

Risk: combined content+coding queries require intersection/planning across indexes and can complicate filtering, sorting, highlighting and aggregation.

### Candidate C — Hybrid projection

Keep selected high-churn fields separate while retaining lower-churn searchable metadata with content. Exact design depends on measured workload.

### Candidate D — Parent/child join in one index

Represent the content document as the parent and high-churn coding as a join child routed to the **same shard**.

Potential benefit: coding child updates avoid rewriting the large text-bearing parent.

The spike must measure:

- `has_child` / `has_parent` query latency
- routing requirements
- global-ordinal memory
- refresh/global-ordinal build cost
- concurrent-reviewer performance
- bulk coding throughput
- operational complexity
- family/security query interactions

No candidate is preferred before the benchmark.

## 26. ADR-004 Decision Gates

The spike is timeboxed and uses predetermined gates so it produces a decision rather than an open-ended research project.

Initial gates are **provisional engineering criteria** and may be adjusted once the reference hardware is frozen, but they must be frozen before the comparative run.

For the 1M-document spike:

| Criterion | Initial gate |
|---|---|
| Simple-search p95 degradation during bulk load | ≤ 25% vs idle baseline |
| Complex-search p95 degradation during bulk load | ≤ 35% vs idle baseline |
| Search-index lag at sustained target bulk load | ≤ 2 minutes |
| Interactive coding → searchable p95 | ≤ 1 second when bulk load is active |
| Correctness | 0 stale-version overwrites |
| Security authorization | 0 unauthorized protected-resource retrievals |
| Worker retry correctness | 100% idempotent in fault-injection suite |
| Bulk coding/indexing | Measure against provisional ≥10K docs/sec goal |

The winning design must satisfy correctness/security gates first. Throughput does not compensate for a correctness failure.

If multiple candidates pass, prefer the least operationally complex design unless another candidate has a material, repeatable performance advantage.

The comparative spike should be timeboxed to a defined engineering window (for example, approximately one focused sprint) once the harness and dataset generator exist.

## 27. PostgreSQL Coding Model Spike

OpenSearch is not the only bulk-coding bottleneck.

A 10M-document coding operation may create:

- 10M current-state mutations
- 10M provenance/history records
- job/chunk state
- indexing work

Benchmark at least a model conceptually separating current state from append-only history:

```text
DocumentCodingCurrent
  WorkspaceId
  DocumentId
  Field/Choice state
  DocumentVersion
  UpdatedBy
  UpdatedAt

CodingEvent
  EventId
  WorkspaceId
  DocumentId
  JobId
  Coding change
  Actor
  Timestamp
```

The exact schema may differ, but provenance must not be lost.

Measure:

- bulk INSERT/UPDATE throughput
- WAL volume
- table/index growth
- HOT-update effectiveness
- fillfactor choices
- vacuum/autovacuum behavior
- bloat
- lock contention
- history-write cost
- chunk transaction size
- rollback/retry cost

Do not finalize PostgreSQL coding storage until these results are considered alongside ADR-004.

## 28. Search Generation and Reviewer UX

Track a monotonically understandable search-projection generation/watermark.

The UI should be able to communicate:

```text
Bulk coding committed
Search index updating...

Index current through generation 18,432
Job generation: 18,517
```

Search counts shown during indexing should not silently imply that the projection is fully current.

The exact generation implementation can be per workspace/search projection rather than a single global cluster counter.

## 29. Benchmark Profiles

### Developer regression profile

A smaller topology supports correctness/regression work:

```text
1 PostgreSQL
1 OpenSearch node
1 RabbitMQ
1 object-storage service
API
essential workers
```

### Enterprise performance reference

Initial reference workload:

```text
3-node OpenSearch
PostgreSQL primary + replica
RabbitMQ
object storage
multiple worker instances

10M documents
~30 custom fields
~1 parent : 3 family-member distribution
~20% duplicates
heavy-tailed extracted text
some documents approaching ~10 MB text

Query mix:
60% simple
30% Boolean
10% proximity/wildcard

100 concurrent reviewers with realistic think time

Background:
bulk coding + indexing running concurrently
```

Store hardware, versions, JVM settings, durability settings, shard topology, corpus generator seed/configuration, and workload scripts with every result.

### Phase 1 — 1M

Use 1M documents to compare Candidates A–D and PostgreSQL coding approaches quickly.

### Phase 2 — 10M

Repeat the winning design at 10M and specifically measure:

- Lucene/OpenSearch segment-merge behavior
- disk amplification
- merge CPU/I/O
- indexing backpressure
- p95/p99 query degradation
- long-running snapshot behavior
- PostgreSQL WAL/bloat/autovacuum behavior

A design that passes 1M but fails materially at 10M is not accepted for the 100M roadmap.

## 30. Updated Performance Status

All performance numbers remain benchmark goals, not claims.

| Metric | Status |
|---|---|
| Simple search p95 <1 sec | Unproven |
| Complex search p95 <3 sec | Unproven |
| Interactive coding → searchable p95 <1 sec | Unproven |
| 100 concurrent reviewers | Reference workload |
| Bulk coding/indexing ≥10K docs/sec | Provisional ADR-004 goal; not assumed |
| 1M scale | First comparative milestone |
| 10M scale | Architecture validation milestone |
| 100M scale | Long-term architecture target |

## 31. Pre-Coding Architecture Blockers

Before the vertical slice's coding/search path is treated as stable:

1. Implement/design document-level interactive outbox vs chunk-level bulk `IndexChunkTask`.
2. Complete ADR-004 Candidate A/B/C/D spike using frozen decision gates.
3. Benchmark PostgreSQL current-state + provenance/history coding writes.
4. Implement deterministic PIT-vs-materialized snapshot semantics.
5. Include workspace/family/dedupe/thread/security fields in the initial search mapping.
6. Prove authoritative security re-checks for protected-resource access.
7. Implement search-generation/watermark observability.

## 32. First Vertical Slice

```text
DAT / OPT / Natives / Text
           |
         IMPORT
           |
         INDEX
           |
         SEARCH
           |
      VIEW DOCUMENT
           |
   INTERACTIVE CODE
           |
   SEARCH NEW CODING
           |
       BULK TAG
           |
 chunk-level index tasks
           |
    SEARCH / VERIFY
           |
         EXPORT
```

Required characteristics:

- PostgreSQL remains authoritative.
- Interactive edits use low-latency document-level search work.
- Bulk edits use payload-free chunk-level indexing tasks.
- Search updates are version-safe and retry-safe.
- Bulk target membership is deterministic and restartable.
- Protected content is re-authorized outside OpenSearch.
- Search projection freshness is visible.
- Family/dedupe/thread/security structure exists from day one.
- Scale/regression tests are automated.

## 33. Items Safe to Defer Past the 1M Benchmark

Provided their architectural boundaries are preserved:

- Full legal query language; a minimal AST/subset is enough initially
- Final native-rendering technology
- Audit archival/tamper-evidence implementation
- Final PostgreSQL partitioning decision while candidates are benchmarked
- Advanced email-thread analytics
- Full review batching/QC UI
- AI/TAR/advanced analytics
- Kubernetes-first deployment

## 34. Decision Log

This section records resolved design choices without retaining review dialogue.

| Decision | Current position |
|---|---|
| Job transport | RabbitMQ initially; no PostgreSQL queue in v1 |
| Cache | No Redis/Valkey dependency until a real need exists |
| Object storage | Provider-neutral abstraction; bundled provider is operational choice |
| Encryption keys | Abstraction supports stronger key models; per-workspace keys not mandatory for MVP |
| Families/dedupe/threading | IDs and relationships modeled from day one |
| Review batching/QC | Domain/provenance support now; full workflow UI later |
| Bulk snapshots | PIT only for short non-exact jobs; materialize restartable/long/legal jobs |
| Bulk search updates | Chunk-level payload-free `IndexChunkTask` |
| Interactive search updates | Document-level outbox allowed for low latency |
| Search security | Search is candidate discovery, never final authorization |
| Workspace search isolation | Mandatory workspace filter/routing; avoid per-workspace aliases at high counts |
| Coding search representation | Unresolved until ADR-004 Candidate A–D benchmark |
| Performance claims | None until reference benchmarks pass |

## 35. Baseline Architecture Position

This document is the implementation baseline. Earlier v1/v2/v3 files are historical design discussions and must not be used as normative implementation specifications.

The largest unresolved architecture decision is intentionally explicit:

> **How should high-churn coding state be represented in OpenSearch so bulk review updates remain scalable without making combined legal search too slow or too complex?**

ADR-004 answers that through measured comparison of unified, split, hybrid, and parent/child representations.

The surrounding foundation is fixed enough to proceed with the benchmark harness and vertical-slice scaffolding: PostgreSQL authority, transactional work creation, payload-free chunk indexing, deterministic snapshots, structural family/dedupe/thread fields, authoritative security checks, provider-neutral object storage, and horizontally scalable workers.
