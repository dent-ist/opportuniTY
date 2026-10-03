# E09 — Family, Duplicate & Thread Relationships

**Labels:** `epic`, `role:ediscovery`, `role:data`, `role:search`, `role:backend`, `P0`  
**Starts in:** M1 - First Vertical Slice  
**Tickets:** 5

## Goal
Normalize upstream relationship metadata into the §5 structural fields so family-aware search, review and production are correct from day one; honour upstream dedupe/thread decisions; add expansion and propagation workflows.

## Baseline sections
§1, §5, §9, §14, §19 (ADR-009), §22, §23, §32, §34

## Scope / out of scope
**In scope**
- Family reconstruction (BegAttach/EndAttach ranges, ParentID pointers, GroupIdentifier)
- Upstream duplicate/thread identifiers and custodian rollups
- Family/duplicate/thread expansion in search, snapshots, bulk
- Computed family-level duplicate groups
- Family/duplicate coding propagation

**Out of scope**
- Email threading engine and thread analytics UI (§33)

## Contributing roles
- **Roles:** eDiscovery Practitioner, Data (PostgreSQL), Search (OpenSearch), Backend
- **Source reviews:** eDiscovery Practitioner, Backend/Architecture, UI/UX, QA & Performance
- **Milestones spanned:** M1 - First Vertical Slice, M3 - MVP Feature Complete

## Exit criteria
- [ ] Families spanning chunk and volume boundaries resolve correctly and orphans are reported
- [ ] Expansion is applied before snapshot materialization so membership is deterministic
- [ ] Duplicate groups match generator ground truth on the 20%-duplicate corpus

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E09-T01](#e09-t01) | Reconstruct families from BegAttach/EndAttach, ParentID and GroupIdentifier | M1 | M | E08-T03, E02-T04 |
| [E09-T02](#e09-t02) | Import upstream duplicate and email-thread identifiers | M1 | S | E08-T02 |
| [E09-T03](#e09-t03) | Expand families, duplicates and threads for search, snapshots and bulk actions | M3 | M | E07-T07, E10-T02, E09-T01 |
| [E09-T04](#e09-t04) | Compute duplicate groups with family-level, scoped dedupe policy | M3 | M | E09-T01, E09-T02 |
| [E09-T05](#e09-t05) | Implement family and duplicate coding propagation | M3 | M | E09-T03, E10-T04 |

---

### E09-T01

**Reconstruct families from BegAttach/EndAttach, ParentID and GroupIdentifier**  
Labels: `role:ediscovery`, `role:data`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§5, ADR-009, §32 'family relationships preserved'. eDiscovery: three upstream representations; range families depend on consistent padding.

#### Description
Mode A (range): docs within [BegAttach, EndAttach] of a parent, parent = ControlNumber = BegAttach. Mode B (pointer): `ParentID` on children, `AttachmentIDs` cross-validated. Mode C (group): shared `GroupIdentifier`/`FamilyID`, parent = lowest ControlNumber or blank ParentID. FamilySequence 0 for parent, 1..n by control-number order; nested attachments flattened to the top-level family with ParentDocumentId = immediate parent. Post-pass job resolves families spanning chunk boundaries; family changes bump DocumentVersion and create an IndexChunkTask(kind=Family).

#### Acceptance criteria
- [ ] A family of 25 spanning chunk size 10 resolves correctly
- [ ] Report lists orphan attachments, ranges spanning missing control numbers and docs claimed by two families
- [ ] Family changes are reindexed through chunk tasks
- [ ] Results match generator ground truth on the 1:3 family corpus

#### Dependencies
- `E08-T03` — Orchestrate import jobs with chunk-level index tasks
- `E02-T04` — Write ADR-003/005/009: metadata model, interim partitioning, identity and dates

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** Data (PostgreSQL)
- **Source reviews:** eDiscovery Practitioner, Backend/Architecture, QA & Performance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E09-T02

**Import upstream duplicate and email-thread identifiers**  
Labels: `role:ediscovery`, `P0`, `size:S` · Milestone: M1 - First Vertical Slice

#### Context
§5, §33 (threading UI deferred). eDiscovery: honour upstream dedupe (`AllCustodians`, `DuplicateCustodians`, `AllPaths`) and thread IDs (Relativity Email Thread Group, Nuix thread ID, `ConversationIndex`, `InclusiveEmail`).

#### Description
Map upstream DuplicateGroupId/DedupeHash/EmailHash and EmailThreadId; store `ConversationIndex` (hex), `ConversationTopic`, `InclusiveEmail`, `ThreadSortOrder`, `AllCustodians`, `DuplicateCustodians`, `AllPaths` as typed (multi-value) fields; record which hash drove grouping.

#### Acceptance criteria
- [ ] EmailThreadId and DuplicateGroupId populate from mapped upstream fields
- [ ] Multi-value custodian/path fields are searchable; `custodian:` can optionally include AllCustodians
- [ ] Thread ID is available as a groupable column

#### Dependencies
- `E08-T02` — Implement field mapping, typed parsing, dates/time zones and mapping templates

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** —
- **Source reviews:** eDiscovery Practitioner, Backend/Architecture
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / S

---

### E09-T03

**Expand families, duplicates and threads for search, snapshots and bulk actions**  
Labels: `role:search`, `role:ediscovery`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§9 family/duplicate expansion; eDiscovery: expansion applied before snapshot materialization.

#### Description
'Include family', 'include duplicates', 'include thread' expansion for interactive search, bulk targets and snapshot materialization; base vs expanded counts; family sort ('family date' then FamilySequence) keeps families contiguous.

#### Acceptance criteria
- [ ] Search responses return base hits and expanded counts separately
- [ ] Expansion is applied before materialization; expanded membership is stable after later family edits
- [ ] Expansion over a 1M family-heavy (1:3) corpus completes within a recorded budget

#### Dependencies
- `E07-T07` — Build search planner with field resolution and proximity
- `E10-T02` — Build materialized DocumentSetSnapshot service
- `E09-T01` — Reconstruct families from BegAttach/EndAttach, ParentID and GroupIdentifier

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner, UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E09-T04

**Compute duplicate groups with family-level, scoped dedupe policy**  
Labels: `role:ediscovery`, `role:data`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§5 'deduplication policy remains configurable'; eDiscovery: dedupe on top-level parent hash only (never individual attachments); MD5 of two copies of the same email differs.

#### Description
Optional computation of DuplicateGroupId from a selectable hash (file SHA-256, upstream MD5/SHA1, upstream DedupeHash/email hash); family-level comparison (attachments inherit parent group); scope Global or Custodial; primary-document rule (earliest date, then lowest control number); informational only in MVP (no suppression).

#### Acceptance criteria
- [ ] The hash field used is recorded on each group
- [ ] Duplicate groups match generator ground truth on the 20%-duplicate corpus and are stable on re-import
- [ ] Primary flag is surfaced in the grid

#### Dependencies
- `E09-T01` — Reconstruct families from BegAttach/EndAttach, ParentID and GroupIdentifier
- `E09-T02` — Import upstream duplicate and email-thread identifiers

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** Data (PostgreSQL)
- **Source reviews:** eDiscovery Practitioner, Backend/Architecture
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Q-09.

---

### E09-T05

**Implement family and duplicate coding propagation**  
Labels: `role:backend`, `role:ediscovery`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
UI finding 9: 'apply to family' turns an interactive edit into a bulk edit; security: privilege family propagation; legal: inconsistent privilege calls across duplicates is a waiver argument.

#### Description
'Apply to family / duplicates' with per-field propagation configuration; conflict preview ('3 members already coded differently'); below a configurable threshold uses the interactive path, above it creates a bulk job over a materialized snapshot; provenance recorded with ActorType SystemRule/BulkHuman.

#### Acceptance criteria
- [ ] Preview shows affected count and conflicts before apply
- [ ] Above the threshold (default 1,000, Q-14) propagation creates a job and links to the job monitor
- [ ] Propagated changes have CodingEvents referencing the originating edit

#### Dependencies
- `E09-T03` — Expand families, duplicates and threads for search, snapshots and bulk actions
- `E10-T04` — Build bulk coding job and worker

#### Roles
- **Owner:** Backend
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** UI/UX, eDiscovery Practitioner, Security & Compliance, Legal/Discovery Counsel
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Q-14.
