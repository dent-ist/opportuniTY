# E02 — Architecture Decision Records & Threat Model

**Labels:** `epic`, `role:backend`, `role:security`, `role:devops`, `role:ui`, `role:search`, `role:qa`, `role:data`, `role:ediscovery`, `role:legal`, `P0`  
**Starts in:** M0 - Foundation & Benchmark Harness  
**Tickets:** 8

## Goal
Turn the §19 priority list into written, reviewed ADRs before code accumulates. Fix the ADR-004 numbering collision, record the version/consistency model the reviews found under-specified, and add the missing security, backup/DR, observability and frontend ADRs.

## Baseline sections
§7, §10, §11, §15, §19, §21–§27, §33, §34

## Scope / out of scope
**In scope**
- ADR process, template, index and renumbering (ADR-004a PG coding storage / ADR-004b OpenSearch coding projection)
- ADR-001/010 consistency, versioning and job semantics; ADR-002 snapshots; ADR-003/005/009 metadata, partitioning, identity; ADR-006/007/008 index, mapping, query language; ADR-011/012/014 storage, redaction/page model, lifecycle; ADR-013 audit
- STRIDE threat model and ADR-015 security architecture

**Out of scope**
- Benchmark-dependent final decisions (ADR-004a/b in E18, final ADR-005 in E18)
- Backup/DR ADR (E19), frontend ADR (E15)

## Contributing roles
- **Roles:** Backend, Security & Compliance, DevOps / SRE, UI/UX, Search (OpenSearch), QA, Data (PostgreSQL), eDiscovery Practitioner, Legal / Discovery Counsel
- **Source reviews:** Backend/Architecture, Security & Compliance, DevOps/SRE, UI/UX, Legal/Discovery Counsel, eDiscovery Practitioner
- **Milestones spanned:** M0 - Foundation & Benchmark Harness

## Exit criteria
- [ ] Every §19 ADR exists with status Accepted or Proposed-with-interim-position and a named owner
- [ ] Every 'Proposed amendment' in review-findings.md has an ADR or a recorded owner decision
- [ ] Every High threat in the threat model maps to a ticket or a signed accepted-risk entry

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E02-T01](#e02-t01) | Establish ADR process, resolve numbering and record layering/API conventions | M0 | S | — |
| [E02-T02](#e02-t02) | Write ADR-001 and ADR-010: search consistency, version model and job/chunk/idempotency semantics | M0 | L | E02-T01 |
| [E02-T03](#e02-t03) | Write ADR-002: bulk snapshot semantics with numeric PIT policy | M0 | M | E02-T02 |
| [E02-T04](#e02-t04) | Write ADR-003/005/009: metadata model, interim partitioning, identity and dates | M0 | M | E02-T01 |
| [E02-T05](#e02-t05) | Write ADR-006/007/008: index strategy, mapping strategy and minimal query language | M0 | M | E02-T01 |
| [E02-T06](#e02-t06) | Write ADR-011/012/014: object storage addressing, redaction and page model, retention and deletion lifecycle | M0 | M | E02-T01 |
| [E02-T07](#e02-t07) | Write ADR-013: audit architecture and event taxonomy | M0 | S | E02-T01 |
| [E02-T08](#e02-t08) | Produce STRIDE threat model and ADR-015 security architecture | M0 | M | E02-T01 |

---

### E02-T01

**Establish ADR process, resolve numbering and record layering/API conventions**  
Labels: `role:backend`, `role:security`, `role:devops`, `role:ui`, `P0`, `size:S`, `adr` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§19 lists 14 priority ADRs; `docs/adr/` is empty. §19 item 4 is 'Coding storage' (PostgreSQL) while §25–§26 call the OpenSearch coding projection 'ADR-004', and the §27 PG spike has no number. Reviewers also found missing ADRs for security (§19), backup/DR, observability and frontend.

#### Description
Create `docs/adr/0000-template.md` and an index. Resolve the collision before any ADR file exists: proposal ADR-004a 'PostgreSQL coding storage' and ADR-004b 'OpenSearch coding projection'. Register additional ADRs: ADR-015 Security architecture & trust boundaries, ADR-016 Backup/DR & restore consistency, ADR-017 Observability & SLOs, ADR-018 Frontend architecture & UX baseline. Record the layering ADR (Core → nothing; Data/Search/Messaging → Core; Workers → Application + Infrastructure), the API conventions and the message contract versioning rules (additive-only within a major, tolerant readers, N and N-1). Propose marking §32 normative over §20.

#### Acceptance criteria
- [ ] ADR index lists all 14 §19 ADRs plus 015–018 with status and owner
- [ ] The numbering decision is recorded and referenced from review-findings.md (proposed amendment to §19/§25)
- [ ] Layering/API ADR is Accepted and referenced by architecture tests (`E01-T01`) and API conventions (`E01-T02`)

#### Dependencies
- None

#### Roles
- **Owner:** Backend
- **Contributing:** Security & Compliance, DevOps / SRE, UI/UX
- **Source reviews:** Backend/Architecture, Security & Compliance, DevOps/SRE, UI/UX
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / S

#### Notes
Proposed amendment — needs ADR/owner decision: ADR numbering §19 vs §25; §20/§32 duplication.

---

### E02-T02

**Write ADR-001 and ADR-010: search consistency, version model and job/chunk/idempotency semantics**  
Labels: `role:backend`, `role:search`, `role:qa`, `P0`, `size:L`, `adr` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§7, §11, §21, §23, §28. Backend findings 1–11: OpenSearch `version_type=external` only works on full `index` operations, not `_update`; DocumentVersion/projectionVersion/ProjectionGeneration/SearchGeneration have no defined relationship; `index.gc_deletes` (60 s) lets a delayed task resurrect a deleted doc; §7 `Payload` contradicts §21 payload-free; import chunks have no snapshot; dispatcher, coalescing, idempotency, state machine, chunk bounds and DLQ semantics are undefined. QA finding 1 requires a refresh-aware watermark.

#### Description
ADR-001: SearchOutbox (interactive) vs IndexChunkTask (bulk/import/reindex) split; payload-free interactive outbox rows (DocumentId + version); dispatcher = `FOR UPDATE SKIP LOCKED` polling + LISTEN/NOTIFY wakeup, publisher confirms before marking dispatched; explicit statement that per-document ordering is **not** guaranteed and not needed; coalescing location; the exact OpenSearch write primitive per ADR-004 candidate (full `index` with external version vs `_update` guarded by `if_seq_no/if_primary_term` or a painless `projectionVersion` compare); 'row missing in PG = delete', workspace fencing; one authoritative `DocumentVersion` bigint incremented on any projected-field change and written as the external version, plus `ProjectionGeneration` that selects the physical index and is never compared with document versions; commit timestamp on work rows; watermark advances only after OpenSearch refresh. ADR-010: Job/JobChunk statuses and transitions (cancel, pause/resume, completed-with-errors, max attempts), lease ownership (`LeaseOwner`, `LeaseExpiresAt`), `IdempotencyKey = hash(WorkspaceId, JobId, ChunkSequence, OperationKind, ProjectionGeneration)`, consumer dedupe (inbox or conditional `Pending→Running`), chunk bounds by count **and** bytes (OpenSearch `http.max_content_length` 100 MB), retry/backoff, poison policy (Failed in PG, DLQ copy for diagnostics only, replay = reset PG state), `JobChunk` membership for every job type (snapshot range or ImportBatchId/row range; nullable SnapshotId; TaskKind Import/BulkCoding/Reindex), workers resolve WorkspaceId and initiating actor from the PG row, never from the envelope.

#### Acceptance criteria
- [ ] ADR-001 states the version field(s), increment rules (incl. metadata overlay, text replacement, family re-link) and OpenSearch op for index/update/delete per candidate
- [ ] ADR-001 contains a sequence diagram for retry-after-newer-edit and for delete-then-delayed-retry, showing the '0 stale overwrites' invariant (§26)
- [ ] ADR-010 contains a state diagram for Job and JobChunk with every allowed transition, the idempotency key formula and chunk bounds (initial values to be tuned by `E18-T03`)
- [ ] Both ADRs are Accepted before the dispatcher (`E06-T04`) is merged

#### Dependencies
- `E02-T01` — Establish ADR process, resolve numbering and record layering/API conventions

#### Roles
- **Owner:** Backend
- **Contributing:** Search (OpenSearch), QA
- **Source reviews:** Backend/Architecture, QA & Performance, Security & Compliance, DevOps/SRE
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / L

#### Notes
Proposed amendments — need ADR/owner decision: external versioning vs partial updates; version-field relationships; tombstones/deleted docs; §7 payload vs §21 payload-free; import IndexChunkTask without snapshot. Q-07 (interactive vs bulk conflict rule).

---

### E02-T03

**Write ADR-002: bulk snapshot semantics with numeric PIT policy**  
Labels: `role:search`, `role:backend`, `role:ui`, `P0`, `size:M`, `adr` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§10 (superseded in part), §22, §32. Backend finding 18: 'runtime safely within PIT lifetime' needs a number; §32 requires restartable bulk targets, which by §22 forces materialization, so PIT is effectively unused in the MVP; family/duplicate expansion timing is undefined; `SearchGeneration` on a snapshot is undefined. UI finding 4: interactive review sessions outlive PIT keep-alive. Legal finding 9: reproducibility stops at membership.

#### Description
Formalize §22: runtime estimate from DocumentCount/throughput compared against `keep_alive × safety factor`; decision table mapping each job type (bulk tag, bulk code, export, production, reindex, STR, review batch, interactive review cursor) to PIT or Materialized; representation options (PG membership rows vs immutable chunked ID manifests in object storage), to be confirmed by the 1M-member benchmark in `E10-T03`; expansion (family/duplicate/thread) applied **before** materialization; meaning of SearchGeneration on PIT vs materialized targets; behaviour on PIT expiry for interactive review (re-establish cursor + notice).

#### Acceptance criteria
- [ ] Decision table covers all job types and all 16 combinations of the four §22 predicates
- [ ] Numeric PIT policy (keep-alive, safety factor, estimate formula) is stated and configurable
- [ ] Expansion timing and SearchGeneration semantics are defined
- [ ] Reproducibility scope (membership vs content-as-of) is stated with a reference to Q-08

#### Dependencies
- `E02-T02` — Write ADR-001 and ADR-010: search consistency, version model and job/chunk/idempotency semantics

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** Backend, UI/UX
- **Source reviews:** Backend/Architecture, UI/UX, QA & Performance, Legal/Discovery Counsel
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Q-08, Q-33.

---

### E02-T04

**Write ADR-003/005/009: metadata model, interim partitioning, identity and dates**  
Labels: `role:data`, `role:ediscovery`, `P0`, `size:M`, `adr` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§5, §6, §19 items 3, 5, 9. eDiscovery findings: ControlNumber uniqueness/case rules, BegBates vs ControlNumber, family modes, dedupe scope and email hash, upstream thread IDs, date formats/time zones and `documentDate` derivation. Backend findings 13–14: `WorkspaceId` must lead every PK/index from day one; partition-ready DDL.

#### Description
ADR-003: typed structural columns + `Metadata JSONB` keyed by stable FieldDefinitionId (not display name); renaming never rewrites rows; coercion table for all 9 field types; JSONB indexing policy; dates stored as UTC instant + original raw string with per-import source time zone; `documentDate` derivation (sent date for email, last-modified for e-docs, parent date for attachments = 'family date'). ADR-009: ControlNumber unique per workspace on a normalized form (trim, configurable case-insensitivity), immutable, natural sort; received-production BegBates/EndBates as typed fields; deterministic family resolution (range, pointer, group modes) and conflict handling; DuplicateGroupId scope (workspace) and which hash drove grouping; EmailThreadId sources. ADR-005 (interim, Proposed): non-partitioned but partition-ready DDL with composite PK `(WorkspaceId, DocumentId)`, benchmark plan for hash-by-workspace / dedicated / hybrid (final decision `E18-T08`).

#### Acceptance criteria
- [ ] Coercion table for all 9 §6 field types and JSONB key format are defined
- [ ] Family resolution rules for BegAttach/EndAttach, ParentID/AttachmentIDs and GroupIdentifier are defined with conflict handling
- [ ] ControlNumber normalization and immutability rules are defined (Q-27)
- [ ] Interim ADR-005 lists partition-ready DDL rules and the benchmark metrics (coding updates, bulk coding, workspace delete, vacuum/bloat, index size)

#### Dependencies
- `E02-T01` — Establish ADR process, resolve numbering and record layering/API conventions

#### Roles
- **Owner:** Data (PostgreSQL)
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner, Security & Compliance
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Q-09, Q-27, Q-28.

---

### E02-T05

**Write ADR-006/007/008: index strategy, mapping strategy and minimal query language**  
Labels: `role:search`, `role:ediscovery`, `role:ui`, `P0`, `size:M`, `adr` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§8, §9, §23, §33. Backend: placement thresholds, routing, alias naming, reindex protocol; EBNF for the MVP subset. eDiscovery: text-size caps and highlighting limits (`index.highlight.max_analyzed_offset` 1M chars). UI: grid sort/filter depends on mapping (flattened fields are not sortable). QA: the §29 mix needs proximity and wildcard queries in the benchmark.

#### Description
ADR-006: shared vs dedicated vs multi-shard thresholds in docs/bytes (configurable), routing key, alias naming, logical→physical resolution, alias-switch reindex protocol; no per-workspace filtered aliases. ADR-007: `dynamic: strict` at root, every §23 field with type, typed custom-field containers vs `flattened`, analyzers, indexed-text cap with `textTruncated` flag, per-field capability metadata (sortable/filterable/aggregatable) for the UI. ADR-008: EBNF for Boolean, grouping, phrase, `field:value`, ranges, trailing wildcard, escaping and `W/n` proximity (needed for the §29 benchmark mix); leading-wildcard policy; default operator, precedence and case rules; every §9 example marked supported or deferred; `PRE/n`, root expander and full legal syntax deferred behind AST extension points.

#### Acceptance criteria
- [ ] Placement thresholds are expressed in document count/bytes and are configurable
- [ ] Mapping ADR lists every §23 field with type and the capability metadata exposed to the UI
- [ ] Every §9 example is marked supported or deferred; precedence, default operator and case rules are documented
- [ ] Indexed text cap and highlighting limit behaviour are defined (Q-29)

#### Dependencies
- `E02-T01` — Establish ADR process, resolve numbering and record layering/API conventions

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** eDiscovery Practitioner, UI/UX
- **Source reviews:** Backend/Architecture, Security & Compliance, eDiscovery Practitioner, UI/UX, QA & Performance
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Q-02, Q-29, Q-30.

---

### E02-T06

**Write ADR-011/012/014: object storage addressing, redaction and page model, retention and deletion lifecycle**  
Labels: `role:backend`, `role:security`, `role:legal`, `P0`, `size:M`, `adr` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§13, §15, §19 items 11, 12, 14. eDiscovery: §5 has no page entity, but OPT, viewer, redaction and production need per-page identity. Security: workspace-prefixed keys with non-guessable IDs, presigned URL policy, `KeyId` per object for future crypto-shredding. Backend finding 3: deletion must fence in-flight tasks. Legal: hold blocks deletion; destruction certificate.

#### Description
ADR-011: key layout `ws/{workspaceId}/docs/{documentId}/{artifactKind}/{contentHash-or-random}`; immutability; presigned URLs only after authorization, single object, GET only, TTL ≤ 120 s default, `Content-Disposition: attachment` for natives, never logged; `KeyId` recorded per object. ADR-012: per-page model (document, ordinal, image key/page Bates, object ref, width/height/DPI, rotation); redactions as page-normalized rectangles with type/reason, author, timestamps, versioned; redaction set version referenced by productions. ADR-014: deletion sequence mark Deleting → fence workers → drain/cancel jobs → drop OpenSearch data → drop PG data → delete storage prefix → destroy keys (if per-workspace) → audit record retained outside the workspace; preservation lock blocks every step; retention profile 'retain productions, privilege logs and audit'.

#### Acceptance criteria
- [ ] ADR-011 and ADR-012 Accepted; ADR-014 Proposed with interim position
- [ ] Per-page entity is defined and referenced by import (`E08-T05`) and redaction (`E11-T04`)
- [ ] Deletion sequence and fencing rule are defined, including behaviour when a hold exists

#### Dependencies
- `E02-T01` — Establish ADR process, resolve numbering and record layering/API conventions

#### Roles
- **Owner:** Backend
- **Contributing:** Security & Compliance, Legal / Discovery Counsel
- **Source reviews:** Backend/Architecture, Security & Compliance, Legal/Discovery Counsel, eDiscovery Practitioner, UI/UX
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Proposed amendment — needs ADR/owner decision: per-page model added to §5. Q-23.

---

### E02-T07

**Write ADR-013: audit architecture and event taxonomy**  
Labels: `role:security`, `role:legal`, `role:backend`, `P0`, `size:S`, `adr` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§15, §19 item 13, §27, §33. Security finding 10 and legal finding 10: §33 defers tamper evidence, which is acceptable only if the schema reserves fields now; audit must include view events and search capture, not only edits.

#### Description
Define the closed taxonomy and envelope: EventId, WorkspaceId (nullable for system), Sequence, OccurredAt, Actor, OnBehalfOf, ClientIp, UserAgent, SessionId (hashed), Category, Action, ResourceType/Id, Outcome (allow/deny), ReasonCode, CorrelationId, CausationId, JobId, SnapshotId, Details (bounded JSON), PrevHash, EventHash. Categories: AuthN, AuthZ.Denied, Document.View/Download/Print, Search.Executed (query text, snapshot/generation, hit count), Coding.Changed (privilege flagged), Redaction.Added/Removed, Export/Production.Created/Downloaded, Role/Permission/EthicalWall.Changed, Workspace.Created/Deleted/Locked, Key/Secret ops, Job lifecycle, Integrity violations. Relationship to CodingEvent provenance (cross-linked, not duplicated); bulk operations audited per job + chunk with SnapshotId (snapshot retained as long as the audit record); prefetch vs displayed view rule (UI).

#### Acceptance criteria
- [ ] Taxonomy covers every category above and the §24 protected operations
- [ ] Policy for search query text (stored, hashed or redacted) is decided (Q-16)
- [ ] Hash fields are reserved in the schema even though chaining ships in `E14-T03`
- [ ] Prefetched documents are not 'viewed' until displayed

#### Dependencies
- `E02-T01` — Establish ADR process, resolve numbering and record layering/API conventions

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Legal / Discovery Counsel, Backend
- **Source reviews:** Security & Compliance, Legal/Discovery Counsel, Backend/Architecture, UI/UX
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / S

#### Notes
Q-16, Q-17.

---

### E02-T08

**Produce STRIDE threat model and ADR-015 security architecture**  
Labels: `role:security`, `P0`, `size:M`, `adr` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
Security finding 14: no threat model or secure SDLC; §19 has no security ADR. Security Q8 / devops Q8: the Lite trust model is undefined.

#### Description
STRIDE threat model per §3 trust boundary (browser↔API, API↔PG/OpenSearch/storage, dispatcher↔RabbitMQ↔workers, render worker↔untrusted content, operator↔admin tooling) recorded as ADR-015 with a data-flow diagram. Declare OWASP ASVS L2 as baseline (L3 controls for authorization and audit). State the Lite profile trust model (evaluation-only with documented reduced guarantees vs production-capable).

#### Acceptance criteria
- [ ] `docs/adr/` contains the threat model with a DFD and a per-boundary table (threat, mitigation, owning ticket key)
- [ ] Every High threat maps to a ticket in this plan or an accepted-risk entry signed off by the PO
- [ ] ASVS target and scope are documented; the PR template has a 'new worker type / trust boundary' checklist item

#### Dependencies
- `E02-T01` — Establish ADR process, resolve numbering and record layering/API conventions

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** —
- **Source reviews:** Security & Compliance, DevOps/SRE
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Q-01.
