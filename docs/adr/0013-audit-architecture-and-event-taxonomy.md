# ADR-013: Audit architecture and event taxonomy

| Field | Value |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-02 |
| **Owner (role)** | Security & Compliance |
| **Deciders** | Lead architect; contributing: Legal / Discovery Counsel, Backend; product owner (Q-13, Q-16, Q-17) |
| **Tracking issue** | #33 (plan key `E02-T07`) |
| **Baseline sections** | [§15](../architecture/architecture-baseline.md#15-security-audit-and-lifecycle), [§22](../architecture/architecture-baseline.md#22-snapshot-semantics), [§24](../architecture/architecture-baseline.md#24-security-consistency-rules), [§27](../architecture/architecture-baseline.md#27-postgresql-coding-model-spike), [§33](../architecture/architecture-baseline.md#33-items-safe-to-defer-past-the-1m-benchmark) |
| **Related** | ADR-005 (partitioning), ADR-010 (jobs), ADR-011 (gateway, object IDs), ADR-012 (redactions), ADR-014 (retention), ADR-015 (security); review findings A-15, §7.1–§7.6; Q-07, Q-12, Q-13, Q-15, Q-16, Q-17, Q-31 |

## Context

§15 asks for "append-oriented and scalable" audit: partitioned append-only PostgreSQL first, archival to immutable
storage later, tamper evidence "where compliance requires it". §33 defers the tamper-evidence implementation. Security
finding 10 and counsel's finding 10 accept that deferral only if the schema reserves the fields now, and require audit
of what people **saw** (views, downloads, searches, denied attempts), not only what they changed. That is what a FRCP
26(g) certification or a 37(e) declaration relies on.

Binding product decisions:

- **Q-16** — the full executed search text is stored in audit, visible only to admin and auditor roles. Audit is
  retained for the life of the matter plus a configurable period (default 7 years after close).
- **Q-17** — hash chain and signed checkpoints ship in M3 (`E14-T03`); WORM archival is post-MVP (`E14-T07`).
- **Q-13** — break-glass use is separately audited and reportable; walls must be provable (`E14-T06`).
- **Q-12** — search-result metadata, snippets and control numbers are protected content.
- **Q-15** — documents excluded from an export at execution time are audited.
- **Q-07** — bulk jobs record skipped documents.

The UI review adds that viewer prefetch must not count as "viewed" (§7.3). `E14-T01` builds the store in M1, so the
envelope must be fixed now.

## Decision

### 1. Store

1. Table `audit.audit_event` in its own schema, **range-partitioned by month** on `OccurredAt`. Partitions are created
   at least 3 months ahead by a scheduled job; a missing partition is an alert, never a dropped event.
2. Privileges: the application roles have `INSERT` and `SELECT` only. A separate `audit_sealer` role may `UPDATE` only
   the reserved columns `Sequence`, `PrevHash`, `EventHash`, `SealedAt`, and a trigger rejects any update that changes
   another column or a reserved column that is already non-null. Only the `audit_retention` role may delete, and only
   through the ADR-014 §6 purge. No application role, including Workspace Admin, can edit or delete audit (`E14-T02`).
3. Hash chains are **per workspace**; installation-level events (`WorkspaceId` null) form one **system chain**.
4. Indexes: `(WorkspaceId, OccurredAt)`, `(WorkspaceId, ResourceType, ResourceId, OccurredAt)`,
   `(ActorId, OccurredAt)`, `(CorrelationId)`. The audit table is not projected to OpenSearch in the MVP.

### 2. Write path: no audit, no action

1. Events for **state changes** (coding, privilege, redaction, role, permission, wall, hold, workspace lifecycle,
   export or production creation) are inserted **in the same PostgreSQL transaction** as the change. A change without
   its event cannot commit (fault-injection test in `E14-T01`).
2. Events for **access** (retrieval, download, print, search) are inserted and committed **before** content or a
   presigned URL is returned (ADR-011 §5.1). If the insert fails, the request is denied.
3. Denied attempts are written in their own transaction, so they persist even though the action does not happen.
4. Workers write job and chunk events in the chunk's commit transaction (ADR-010).
5. Budget: ≤ 2 ms p95 per insert at 100 concurrent reviewers (measured in `E14-T01`). Inserts never wait for the chain.

### 3. Tamper evidence (reserved now, sealed in M3)

1. The columns `Sequence`, `PrevHash`, `EventHash`, `SealedAt` exist from M1 and stay null until `E14-T03`.
2. A **sealer**, one active instance per chain (PostgreSQL advisory lock), runs every second: it takes unsealed
   events of a chain ordered by `(RecordedAt, EventId)`, assigns `Sequence` = previous + 1 and sets
   `EventHash = SHA-256(PrevHash ‖ JCS(event))`. JCS is the RFC 8785 canonical JSON of every envelope column except
   `EventHash` and `SealedAt`. Genesis `PrevHash` = 32 zero bytes. Asynchronous sealing keeps chain cost off the
   interactive path (`E14-T03`: ≤ 5 ms p95 added to coding).
3. On its first run the sealer chains all events written since M1. Events from before M3 are therefore only as
   trustworthy as the storage controls of that period; the verification report says so.
4. **Checkpoints**: `audit.checkpoint` (`WorkspaceId`, `Sequence`, `EventHash`, `EventCount`, `CreatedAt`, `KeyId`,
   `Signature`). Created every 10 minutes when the chain advanced, at matter close, before a deletion run and before an
   audit purge. Signed with an Ed25519 key from `IKeyProvider` (`E05-T09`). Checkpoints are exportable for declaration
   exhibits; `opportunity audit verify` detects modification, deletion and reordering.
5. **WORM archival** (`E14-T07`, post-MVP) ships sealed partitions and checkpoints to a separate Object Lock/immutable
   container under `sys/audit/` (ADR-011). Lite documents the reduced guarantee.

### 4. Envelope

| Field | Type | Req. | Notes |
|---|---|---|---|
| `EventId` | uuid (v7) | ✓ | Generated by the writer; idempotency key for retried inserts |
| `SchemaVersion` | smallint | ✓ | Envelope version; additive changes only within a major (ADR-019) |
| `WorkspaceId` | uuid | – | Null only for installation-level events (system chain) |
| `Sequence` | bigint | reserved | Per chain, assigned by the sealer |
| `OccurredAt` | timestamptz | ✓ | Server UTC time of the action (µs) |
| `RecordedAt` | timestamptz | ✓ | Database `clock_timestamp()` at insert |
| `Category` / `Action` | text | ✓ | Closed taxonomy (§5); unknown values rejected by a CHECK constraint |
| `ActorType` | enum | ✓ | `User` · `Service` · `System` |
| `ActorId` | text | ✓ | User ID, or service identity (`worker:production`) |
| `ActorDisplay` | text | ✓ | Display name at the time of the event (users can leave the IdP) |
| `OnBehalfOf` | uuid | – | Initiating user for worker actions (job `CreatedBy`) |
| `AccessPath` | enum | ✓ | `Normal` · `BreakGlass` · `Impersonation` (reserved, not offered in MVP) |
| `ClientIp` | inet | – | After trusted-proxy resolution; null for workers |
| `UserAgent` | text | – | Truncated to 512 chars |
| `SessionIdHash` | bytea | – | HMAC-SHA-256 of the session ID with an installation secret; never the raw ID |
| `ResourceType` / `ResourceId` | text / text | – | E.g. `Document`/DocumentId, `Production`/ProductionId; IDs only |
| `Outcome` | enum | ✓ | `Success` · `Denied` · `Failure` |
| `ReasonCode` | text | – | Required for `Denied`/`Failure`: PDP reason (`NotMember`, `MissingPermission`, `Walled`, `Restricted`, `LegalHold`, `WorkspaceDeleting`, …) |
| `CorrelationId` / `CausationId` | uuid | ✓ / – | From the request or message envelope (§11) |
| `JobId` / `ChunkSequence` | uuid / int | – | For job-driven events |
| `SnapshotId` | uuid | – | Materialized snapshot the action used (§22) |
| `SearchGeneration` | bigint | – | Index generation a search or report ran against (§28) |
| `Details` | jsonb | – | Action-specific, schema per action, ≤ 8 KB, IDs and enums only |
| `RestrictedDetails` | jsonb | – | Only for `Search.*`: query text and parsed AST (≤ 64 KB). Readable only with `Audit.ReadSearchText` |
| `PrevHash` / `EventHash` | bytea | reserved | SHA-256, sealer only |
| `SealedAt` | timestamptz | reserved | Sealer only |

Exactly one event is written per attempted action. A denied action is its own action with `Outcome = Denied`;
`AuthZ.Denied` is used only when no more specific action applies (for example a request to a workspace the user is
not a member of).

### 5. Taxonomy (closed)

M1 events ship with `E14-T01`/`E05-T04`; the rest with the owning feature, and `E14-T02` closes coverage in M3.

| Category | Actions | Resource | Required `Details` / notes |
|---|---|---|---|
| **Auth** | `SignIn`, `SignInFailed`, `SignOut`, `SessionExpired`, `SessionRevoked`, `StepUp` | User | IdP, `acr`/`amr`; system chain |
| **AuthZ** | `Denied` | any | permission, workspace |
| **Document** | `Retrieved` | Document | rendition kind, `ObjectId`s, `Purpose` (`Display` · `Prefetch` · `Export`); one event per gateway grant (ADR-011 §5.5) |
| | `Viewed` | Document | Emitted when the viewer **displays** the document as the active document; references the `Retrieved` event's ID. Prefetch alone never produces `Viewed` |
| | `NativeDownloaded`, `Printed`, `TextDownloaded` | Document | rendition; permissions distinct from view (Q-18) |
| **Search** | `Executed` | SavedSearch or none | AST hash and version, hit count with an exact/approximate flag, facet fields, PIT or snapshot ID, `SearchGeneration`; **full query text in `RestrictedDetails`** (Q-16) |
| | `ResultsPageServed` | none | DocumentIds actually returned after the Q-12 post-filter (≤ page size), count dropped by the post-filter. Proves what a user could have seen, including that walled documents were not served |
| | `CountExact`, `TermReportGenerated`, `SavedSearch.Created/Modified/Deleted` | | STR ID and snapshot (Q-30) |
| **Coding** | `Changed` | Document | `CodingEventId`s, field IDs, `SecurityAffecting`; old and new **values only for security-affecting fields** (privilege, confidentiality, wall membership) |
| | `FamilyApplied` | Document | family ID, conflict preview accepted (Q-14) |
| | `BulkSubmitted`, `BulkChunkApplied`, `BulkCompleted` | Job | `SnapshotId`, field operations, counts applied/skipped (Q-07); one event per job state and per chunk, **never per document** |
| | `OverlayEnabled` | Import | coding/privilege overlay enabled by admin (Q-31) |
| **Privilege** | `LogGenerated`, `ConflictOverride`, `ClawbackRecorded` | Production, Document | log version and hash; override reason |
| **Redaction** | `Added`, `Modified`, `Removed` | Document | `RedactionId`, `RedactionVersion`, page, reason code; never the note text (ADR-012) |
| **Export** | `Created`, `Completed`, `DocumentsExcluded`, `Downloaded` | Export | `SnapshotId`; excluded DocumentIds with reasons per chunk (Q-15); manifest hash |
| **Production** | `Created`, `SpecFrozen`, `Run`, `VerificationFailed`, `QcOverride`, `Finalized`, `Voided`, `Downloaded`, `Rerun` | Production | spec version, Bates range, manifest hash, V1–V5 results (ADR-012) |
| **Import** | `Started`, `Completed`, `MalwareDetected`, `HashMismatch` | Import | counts; quarantined `ObjectId`s |
| **Security** | `RoleAssigned`, `RoleRevoked`, `PermissionChanged`, `RestrictionChanged` (Q-11), `WallCreated`, `WallChanged`, `WallDeleted`, `WallMemberAdded`, `WallMemberRemoved` | Role, Wall, User | before/after membership (user and group IDs) |
| | `BreakGlassActivated`, `BreakGlassEnded` | Wall/Workspace | reason (required), duration (default 60 min, max 4 h — decision Q-45; read-only per ADR-015 D6.4); workspace admins and auditors notified. Every event during the window carries `AccessPath = BreakGlass` and is listed in the break-glass report (Q-13) |
| | `AcknowledgmentAccepted` | Workspace | text version hash (`E20-T03`) |
| **Workspace** | `Created`, `SettingsChanged`, `Closed`, `Reopened`, `HoldPlaced`, `HoldReleaseRequested`, `HoldReleased`, `DeletionRequested`, `DeletionApproved`, `DeletionCancelled`, `DeletionStarted`, `DeletionStepCompleted`, `DeletionHalted`, `Deleted` | Workspace | ADR-014; `Deleted` carries the certificate ID and hash |
| **Admin** | `ConfigChanged`, `UserProvisioned`, `UserDeactivated`, `KeyCreated`, `KeyRotated`, `KeyDestroyed`, `SecretRotated` | Installation | setting name, old and new value **unless secret**; system chain |
| **Audit** | `Queried`, `Exported`, `CheckpointCreated`, `Verified`, `Purged` | Workspace | query filters; who reads the audit is itself audited |
| **Job** | `Created`, `Cancelled`, `Failed`, `CompletedWithErrors`, `Replayed` | Job | job type, counts, DLQ replay reason |
| **Integrity** | `HashMismatch`, `ChainBroken`, `FenceViolation`, `EnvelopeMismatch`, `BatesConflict` | any | raised by scrub, verify CLI, workers (ADR-014 fence, envelope check in `E06-T05`) |

Every §24 protected operation (open, view, native download, image retrieval, export, production inclusion) maps to a
row above: `Document.*`, `Export.*` and `Production.Run`. Production inclusion is audited at chunk level with the
snapshot rather than per document, like bulk coding.

### 6. Bulk operations and coding provenance

1. Bulk coding, exports and productions are audited per **job** and per **chunk** with `SnapshotId`. The materialized
   snapshot membership is the record of which documents were affected, so it is retained as long as the audit record
   (ADR-014 §5 keeps it under both profiles where audit references it).
2. `CodingEvent` (§27) is the provenance of field values; audit does not duplicate it. `Coding.Changed` and
   `Coding.BulkChunkApplied` carry the `CodingEventId`s (or the chunk's event range), and `CodingEvent` carries the
   `AuditEventId`. Security-affecting values are the exception and appear in both places.

### 7. Never logged

Audit (and, by the same rule, application logs and traces) **never** contains:

1. Document content: extracted text, native or image bytes, snippets, highlights, OCR output.
2. Document metadata values (file names, subjects, custodians, email addresses, dates) and control numbers. Audit
   stores IDs; reports resolve them at read time, subject to the reader's own access.
3. Free-text fields that may hold privileged content: redaction notes, privilege descriptions (Q-20), review comments.
   Audit references the record by ID.
4. Secrets and bearer material: passwords, tokens, cookies, raw session IDs, presigned URLs, API keys, key material,
   wrapped data keys, connection strings.
5. Full request or response bodies.

The one deliberate exception is the search query text in `RestrictedDetails` (Q-16), because the search terms define
review populations a declarant must describe.

### 8. Reading audit

1. `Audit.Read` (workspace-scoped; Workspace Admin and Auditor roles) returns envelopes without `RestrictedDetails`.
2. `Audit.ReadSearchText` (Workspace Admin and Auditor by default, configurable only to narrow) also returns query
   text. Reviewers cannot read audit, including their own searches.
3. Walls apply to audit reading: a walled reader's queries exclude events whose resource is a document they are walled
   from. The installation Auditor role is not walled; it sees identifiers and envelopes only, since audit holds no
   content (§7).
4. Every audit query and export emits `Audit.Queried`/`Audit.Exported`.

## Consequences

- **Positive:** one closed vocabulary covers access, change and administration, so coverage can be tested
  mechanically. The schema is chain-ready from M1. `ResultsPageServed` plus the Q-12 post-filter gives the wall proof
  `E14-T06` needs. Search text is captured for defensibility without being exposed to reviewers.
- **Negative / costs:** `ResultsPageServed` and `Document.Retrieved` make audit the largest append stream after coding
  events (estimate: 100 active reviewers ≈ 1–2M events per day); monthly partitions and 7-year retention need storage
  planning (`E19-T07`). Events written before M3 are chained retroactively. The sealer is a single writer per chain; a
  very busy workspace may lag sealing by seconds, which is acceptable because sealing is not on the request path.
- **Follow-up work:** `E14-T01` (store, roles, partitions, writer), `E05-T04` (gateway events, prefetch purpose),
  `E16-T03` (UI `Viewed` beacon), `E07-T05` (`Search.*`, `ResultsPageServed`), `E10-T01`/`E10-T04` (coding and bulk events),
  `E12-T01`/`E12-T07` (export/production events), `E05-T06` (walls, break-glass), `E14-T02` (coverage),
  `E14-T03` (sealer, checkpoints, verify CLI), `E14-T04` (viewer), `E14-T06` (wall proof, break-glass report),
  `E14-T07` (WORM).
- **Verification:** an architecture test requires every API endpoint and worker handler to declare its audit action
  (or an explicit exemption list); an integration test calls every protected-content endpoint and fails CI when an
  event is missing; a database test proves app roles cannot UPDATE/DELETE and the sealer cannot change non-reserved
  columns; a log-scrubbing test covers §7.4; a contract test keeps the taxonomy table above and the enum in code in sync.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Store only a hash of search text, or redact it | Rejected by Q-16; a declarant must be able to state the actual terms. |
| Inline hash chaining in each writing transaction | Serializes all writes of a workspace behind one lock held until commit; asynchronous sealing gives the same evidence. |
| Separate chain table instead of reserved columns | Keeps audit rows fully immutable, but `E02-T07` requires the hash fields in the event schema and verification is simpler on one table. The column-restricted sealer role keeps the guarantee. |
| One audit event per document for bulk operations | 10M rows per bulk job; the snapshot plus chunk events carry the same information. |
| Log every page-image request | Hundreds of events per document view; one event per gateway grant is sufficient. |
| Audit in OpenSearch or the observability stack | Not authoritative, not transactional with the change, and Q-42 keeps logs out of the product cluster. |
| Count prefetch as a view | Overstates what a reviewer saw; contradicts the UI review. |

## Baseline amendments

- *Proposed* — §15: "Audit uses the closed taxonomy and envelope of ADR-013; access events are written before content
  is returned; per-workspace hash chain and signed checkpoints ship in M3 (Q-17)."
- *Proposed* — §33: narrow "Audit archival/tamper-evidence implementation" to "Audit WORM archival"; hash chain and
  checkpoints are no longer deferred (resolves A-15).

## Links

- Baseline: §15, §22, §24, §27, §33
- Review findings: [review-findings.md](../plan/review-findings.md) A-15, §7.1–§7.6, §5.5
- Reviews: [security.md](../plan/reviews/security.md) finding 10; [attorney.md](../plan/reviews/attorney.md) findings 10, 12
- Decisions: [decisions.md](../plan/decisions.md) Q-07, Q-11, Q-12, Q-13, Q-14, Q-15, Q-16, Q-17, Q-18, Q-20, Q-31, Q-42
