# E04 — Domain Model & Authoritative Data

**Labels:** `epic`, `role:data`, `role:devops`, `role:ediscovery`, `role:backend`, `role:legal`, `role:ui`, `P0`  
**Starts in:** M0 - Foundation & Benchmark Harness  
**Tickets:** 7

## Goal
Implement the PostgreSQL source-of-truth schema (workspaces, documents, pages, fields, coding with provenance) with partition-ready keys, plus the workspace and field administration APIs/screens.

## Baseline sections
§2.1, §2.3, §5, §6, §13, §14, §27, §34

## Scope / out of scope
**In scope**
- SQL-first migrator that also bootstraps OpenSearch templates, RabbitMQ topology and buckets
- Workspace/Document/Page schema with composite `(WorkspaceId, …)` keys, ControlNumber rules, DocumentVersion rules
- FieldDefinitions (9 types), choices, coding layouts, `IsSecurityAffecting`
- Interim DocumentCodingCurrent + CodingEvent with actor types
- Workspace management API and admin screens

**Out of scope**
- Final PG partitioning (E18-T08)
- Final coding storage (decided by E18-T03/T05)

## Contributing roles
- **Roles:** Data (PostgreSQL), DevOps / SRE, eDiscovery Practitioner, Backend, Legal / Discovery Counsel, UI/UX
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner, Legal/Discovery Counsel, Security & Compliance, UI/UX, DevOps/SRE
- **Milestones spanned:** M0 - Foundation & Benchmark Harness, M1 - First Vertical Slice, M3 - MVP Feature Complete

## Exit criteria
- [ ] Every coding mutation writes current state, CodingEvent and DocumentVersion atomically
- [ ] No tenant table lacks a leading `WorkspaceId` in its PK (lint-enforced)
- [ ] Admins can create a workspace, define fields and a coding layout through the UI

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E04-T01](#e04-t01) | Build migrator with SQL-first migrations and infrastructure bootstrap | M0 | M | E01-T01 |
| [E04-T02](#e04-t02) | Create workspace, document and page core schema | M0 | M | E04-T01, E02-T04, E02-T06 |
| [E04-T03](#e04-t03) | Implement field definitions, choices and coding layouts | M0 | M | E04-T02 |
| [E04-T04](#e04-t04) | Create interim coding current-state and CodingEvent provenance tables | M0 | M | E04-T03 |
| [E04-T05](#e04-t05) | Expose workspace management API | M1 | S | E04-T02, E05-T02 |
| [E04-T06](#e04-t06) | Build field definition and coding layout editor UI | M3 | L | E04-T03, E15-T02, E16-T05 |
| [E04-T07](#e04-t07) | Build workspace management screens | M3 | M | E04-T05, E15-T02, E20-T01 |

---

### E04-T01

**Build migrator with SQL-first migrations and infrastructure bootstrap**  
Labels: `role:data`, `role:devops`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§6, §33. Backend finding 14: EF Core migrations handle declarative partitioning, GIN indexes, fillfactor and RLS poorly. DevOps: migrations must not run at API startup with multiple replicas; `CREATE INDEX CONCURRENTLY` cannot run in a transaction.

#### Description
SQL-first migrations (DbUp or Grate) in `Opportunity.Data`, executed by a dedicated one-shot `migrator` executable/image under a PG advisory lock (never on API/worker startup). Conventions: `WorkspaceId` leads every tenant PK/index; partition-agnostic DDL; RLS policies live in migrations; expand/contract rules documented; non-transactional steps for concurrent index builds. The migrator also idempotently bootstraps OpenSearch index templates/aliases, RabbitMQ exchanges/queues/DLX/retry (quorum) and buckets via pluggable bootstrap steps owned by the respective modules.

#### Acceptance criteria
- [ ] Migrator applies all scripts idempotently to an empty DB and to a previous-version DB in CI; running it twice exits 0 as a no-op
- [ ] Two concurrent migrator runs do not corrupt state (advisory lock test)
- [ ] API and workers report not-ready if the schema version is behind
- [ ] A lint check rejects any tenant table whose PK does not lead with `WorkspaceId`
- [ ] Re-running the RabbitMQ/OpenSearch/bucket bootstrap against existing resources causes no error

#### Dependencies
- `E01-T01` — Scaffold repository layout, .NET solution and Angular workspace

#### Roles
- **Owner:** Data (PostgreSQL)
- **Contributing:** DevOps / SRE
- **Source reviews:** Backend/Architecture, DevOps/SRE
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Merges backend 'SQL-first schema migration tooling' and devops 'Migrator and infrastructure bootstrap job'.

---

### E04-T02

**Create workspace, document and page core schema**  
Labels: `role:data`, `role:ediscovery`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§5 core Document fields; eDiscovery: ControlNumber identity, BegBates/EndBates, per-page entity; security finding 3: composite FKs prevent cross-workspace references.

#### Description
Tables: Workspace (status Active/Deleting/Locked, matter number, display time zone), Document (DocumentId, WorkspaceId, ControlNumber + normalized form, FamilyId, ParentDocumentId, FamilySequence, DuplicateGroupId, EmailThreadId, MD5/SHA1/SHA256, upstream DedupeHash, file/type/date fields with raw strings, artifact object keys, `Metadata JSONB`, DocumentVersion bigint, flags `TextTruncated`/`NativeMissing`/`TextMissing`/`ImagesIncomplete`, timestamps), Page (DocumentId, ordinal, image key, object ref, width/height/DPI, rotation). Composite PK `(WorkspaceId, DocumentId)`; unique `(WorkspaceId, NormalizedControlNumber)`; composite FKs `(WorkspaceId, Id)` everywhere.

#### Acceptance criteria
- [ ] Migrations create the schema; entities map with EF Core and Dapper read models
- [ ] DocumentVersion increments on every projected-field change (repository-enforced, tested)
- [ ] Inserting 100K documents via `COPY` completes within a recorded baseline time in an integration test
- [ ] A row referencing a document of another workspace fails on FK
- [ ] Natural ordering of ControlNumber (`ABC10` > `ABC9`) is available for sorting

#### Dependencies
- `E04-T01` — Build migrator with SQL-first migrations and infrastructure bootstrap
- `E02-T04` — Write ADR-003/005/009: metadata model, interim partitioning, identity and dates
- `E02-T06` — Write ADR-011/012/014: object storage addressing, redaction and page model, retention and deletion lifecycle

#### Roles
- **Owner:** Data (PostgreSQL)
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner, Security & Compliance
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Proposed amendment — per-page model (§5).

---

### E04-T03

**Implement field definitions, choices and coding layouts**  
Labels: `role:data`, `role:backend`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§6 field types; security finding 2: field definitions need `IsSecurityAffecting`; UI finding 9: coding layouts with required/conditional fields per role; UI finding 2: per-field sortability depends on mapping.

#### Description
FieldDefinition (Text, Keyword, Integer, Decimal, Date, Boolean, SingleChoice, MultiChoice, User), Choice tables (reorder, deactivate once used), `IsSecurityAffecting` flag, capability metadata (sortable/filterable/aggregatable per ADR-007), and a validation service for JSONB metadata keyed by FieldDefinitionId. Coding layouts: sections, order, required flag, conditional visibility, role assignment; a default layout per workspace. Imported (static) metadata is kept separate from coding fields (§6).

#### Acceptance criteria
- [ ] Creating or renaming a field never rewrites document rows
- [ ] Invalid typed values are rejected with field-level errors; coercion matches ADR-003
- [ ] Type changes on fields with data are rejected; in-use choices can only be deactivated
- [ ] Layouts with conditional fields validate (e.g. Privilege Basis required only when Privileged = Yes)
- [ ] Field definitions and layouts are workspace-scoped and changes are audited (once `E14-T01` lands)

#### Dependencies
- `E04-T02` — Create workspace, document and page core schema

#### Roles
- **Owner:** Data (PostgreSQL)
- **Contributing:** Backend
- **Source reviews:** Backend/Architecture, Security & Compliance, UI/UX, eDiscovery Practitioner
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

---

### E04-T04

**Create interim coding current-state and CodingEvent provenance tables**  
Labels: `role:data`, `role:legal`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§27 conceptual model; legal finding 15: automated coding must be distinguishable now (cheap now, expensive to retrofit).

#### Description
Interim schema `DocumentCodingCurrent` (+ choice rows) and append-only `CodingEvent` (EventId, WorkspaceId, DocumentId, JobId nullable, field, prior/new value, Actor, ActorType {Human, BulkHuman, SystemRule, Model(reserved)}, IdempotencyKey, Timestamp). Repository API is storage-agnostic so the `E18-T03` spike can change tables without touching Application code.

#### Acceptance criteria
- [ ] Every coding mutation writes current state + CodingEvent + DocumentVersion increment in one transaction
- [ ] Re-applying the same chunk (same idempotency key) creates no duplicate CodingEvents
- [ ] Reports can filter CodingEvents by ActorType
- [ ] Repository interface is independent of the physical coding schema

#### Dependencies
- `E04-T03` — Implement field definitions, choices and coding layouts

#### Roles
- **Owner:** Data (PostgreSQL)
- **Contributing:** Legal / Discovery Counsel
- **Source reviews:** Backend/Architecture, Legal/Discovery Counsel, QA & Performance
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

---

### E04-T05

**Expose workspace management API**  
Labels: `role:backend`, `P0`, `size:S` · Milestone: M1 - First Vertical Slice

#### Context
§1 workspace management; §8 placement is decided by Index Management; UI needs read-only placement and projection generation.

#### Description
Create/list/get/update workspace (name, matter number, display time zone, storage profile), read-only index placement and current projection generation, membership listing. Deletion is delegated to `E20-T02`.

#### Acceptance criteria
- [ ] Workspace endpoints enforce the PDP (`E05-T02`); non-members get 404
- [ ] Index placement and projection generation are returned read-only
- [ ] All changes are audited

#### Dependencies
- `E04-T02` — Create workspace, document and page core schema
- `E05-T02` — Implement permission catalogue, workspace roles and policy decision point

#### Roles
- **Owner:** Backend
- **Contributing:** —
- **Source reviews:** Backend/Architecture, UI/UX, Legal/Discovery Counsel
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / S

---

### E04-T06

**Build field definition and coding layout editor UI**  
Labels: `role:ui`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§6; UI ticket 'Field definitions and coding layout editor'.

#### Description
Field CRUD for all §6 types with choice lists (reorder, deactivate), 'security-affecting' flag, and a drag-and-drop coding layout editor (sections, order, required, conditional visibility, role assignment) with a keyboard alternative and live preview.

#### Acceptance criteria
- [ ] Type changes on fields with data are prevented with an explanation
- [ ] In-use choices can be deactivated; historical values stay readable
- [ ] Layout editor is fully keyboard-operable with move-up/move-down (WCAG 2.5.7)
- [ ] Preview renders exactly what the coding panel renders
- [ ] Mapping limitations are surfaced ('This field will not be sortable')

#### Dependencies
- `E04-T03` — Implement field definitions, choices and coding layouts
- `E15-T02` — Build application shell, session handling and workspace context
- `E16-T05` — Build coding panel rendered from coding layouts

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
In M1 the default layout is created via API.

---

### E04-T07

**Build workspace management screens**  
Labels: `role:ui`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§1, §15; UI ticket 'Workspace management screens'.

#### Description
Workspace list/create/settings; empty-state checklist Import → Fields → Layouts → Users; read-only index placement and projection generation; deletion entry point that respects holds.

#### Acceptance criteria
- [ ] Creating a workspace lands on the empty-state checklist
- [ ] Deletion is blocked with an explanation when a preservation lock applies; otherwise the user types the workspace name and sees what will be removed (§15)
- [ ] Screens pass axe and are keyboard-operable

#### Dependencies
- `E04-T05` — Expose workspace management API
- `E15-T02` — Build application shell, session handling and workspace context
- `E20-T01` — Add workspace preservation lock (legal hold)

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX, Legal/Discovery Counsel
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M
