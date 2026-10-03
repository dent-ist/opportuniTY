# E07 — Search Projection, Query Language & Search Features

**Labels:** `epic`, `role:search`, `role:ediscovery`, `role:performance`, `role:security`, `role:backend`, `P0`  
**Starts in:** M1 - First Vertical Slice  
**Tickets:** 12

## Goal
Build the OpenSearch projection with version-safe interactive and chunk index workers, the single workspace-enforcing search service, the minimal query language (with proximity for the §29 mix), the search-generation watermark, and the MVP search features (saved searches, search term reports, reindex).

## Baseline sections
§7, §8, §9, §21, §23, §24, §28, §31.5, §31.7, §33

## Scope / out of scope
**In scope**
- Index management and logical placement
- §23 strict mapping with typed custom-field containers
- Interactive + chunk index workers
- Search service with non-removable workspace filter, cursor binding, hit post-filtering
- Parser/AST, planner, proximity
- Commit-ordered, refresh-aware watermark
- Saved searches, STR, alias-based reindex

**Out of scope**
- Final coding representation (ADR-004b, E18)
- Full dtSearch-style syntax (E07-T12, M5)

## Contributing roles
- **Roles:** Search (OpenSearch), eDiscovery Practitioner, Performance, Security & Compliance, Backend
- **Source reviews:** Backend/Architecture, Security & Compliance, eDiscovery Practitioner, QA & Performance, UI/UX
- **Milestones spanned:** M1 - First Vertical Slice, M2 - 1M Benchmark & Architecture Blockers, M3 - MVP Feature Complete, M5 - Post-MVP / Deferred

## Exit criteria
- [ ] 0 stale-version overwrites under randomized reordering
- [ ] Single coding → searchable p95 measured (≤1 s target) on the dev profile
- [ ] Watermark never advances past unapplied work (property test)
- [ ] No code outside Opportunity.Search builds OpenSearch queries (architecture test)

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E07-T01](#e07-t01) | Implement index management and logical index resolution | M1 | M | E02-T05, E04-T01 |
| [E07-T02](#e07-t02) | Create baseline search projection mapping and projection builder | M1 | M | E07-T01, E04-T03 |
| [E07-T03](#e07-t03) | Build version-safe interactive index worker | M1 | M | E06-T04, E06-T05, E07-T02 |
| [E07-T04](#e07-t04) | Build chunk index worker for IndexChunkTask | M1 | L | E06-T03, E06-T05, E07-T02 |
| [E07-T05](#e07-t05) | Build logical search service with mandatory workspace filter and cursor binding | M1 | L | E07-T01, E05-T02 |
| [E07-T06](#e07-t06) | Implement minimal query parser and AST | M1 | M | E02-T05 |
| [E07-T07](#e07-t07) | Build search planner with field resolution and proximity | M1 | L | E07-T06, E07-T05, E07-T02 |
| [E07-T08](#e07-t08) | Implement search generation watermark and freshness API | M2 | M | E06-T03, E07-T03, E07-T04 |
| [E07-T09](#e07-t09) | Implement saved searches | M3 | M | E07-T07, E14-T01 |
| [E07-T10](#e07-t10) | Generate search term reports with unique hits | M3 | M | E07-T07, E10-T02, E09-T03, E07-T08, E17-T09 |
| [E07-T11](#e07-t11) | Implement alias-based reindex and projection generation switch | M3 | L | E07-T04, E07-T01, E07-T08 |
| [E07-T12](#e07-t12) | Extend query language to full legal syntax | M5 | L | E07-T07 |

---

### E07-T01

**Implement index management and logical index resolution**  
Labels: `role:search`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§8, §23: application code uses a logical search service; Index Management decides placement; no per-workspace filtered aliases at high counts.

#### Description
`IIndexManager` maps (WorkspaceId, ProjectionGeneration) to physical index/alias and routing. Default: shared index with `routing=workspaceId`; dedicated placement supported. Indexes created from versioned mapping templates (bootstrapped by the migrator).

#### Acceptance criteria
- [ ] Application code never references physical index names (architecture test)
- [ ] A workspace can be configured shared or dedicated; both pass the same search test suite
- [ ] Placement thresholds come from configuration per ADR-006

#### Dependencies
- `E02-T05` — Write ADR-006/007/008: index strategy, mapping strategy and minimal query language
- `E04-T01` — Build migrator with SQL-first migrations and infrastructure bootstrap

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** —
- **Source reviews:** Backend/Architecture, Security & Compliance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E07-T02

**Create baseline search projection mapping and projection builder**  
Labels: `role:search`, `role:ediscovery`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§23 required fields from day one; §25 coding representation unresolved; eDiscovery: `textTruncated`, AllCustodians multi-value; UI: per-field capability metadata.

#### Description
Strict mapping with every §23 field (workspaceId, documentId, controlNumber, familyId, parentDocumentId, familySequence, duplicateGroupId, emailThreadId, securityTags, fileName, fileType, mimeType, documentDate, metadata, text, projectionVersion) plus `textTruncated`, typed custom-field containers, interim Candidate A coding fields behind an `IProjectionBuilder` swappable per ADR-004b. Indexed text capped per ADR-007.

#### Acceptance criteria
- [ ] Mapping test asserts every §23 field is present with the agreed type
- [ ] Unknown fields are rejected (`dynamic: strict`); custom fields go to typed containers only
- [ ] Projection builder can be swapped without changing search-service callers
- [ ] Field capability metadata (sortable/filterable/aggregatable) is exposed via API

#### Dependencies
- `E07-T01` — Implement index management and logical index resolution
- `E04-T03` — Implement field definitions, choices and coding layouts

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner, UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E07-T03

**Build version-safe interactive index worker**  
Labels: `role:search`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§7, §21 'a retried old task must never overwrite a newer edit'; backend findings 1 and 3.

#### Description
Consumes SearchOutbox messages, reads current PG state + DocumentVersion, writes with the ADR-001 primitive (external version on full index, or guarded update), treats a missing PG row as a delete, and treats version-conflict responses as success (counted metric). Consumes the security priority lane ahead of ordinary coding.

#### Acceptance criteria
- [ ] Deliver v5 then delayed v3 → index holds v5; 0 stale overwrites over 10K randomized reorderings
- [ ] Delete in PG followed by a delayed retried task does not resurrect the document beyond `gc_deletes` (test with shortened gc_deletes)
- [ ] Single coding → searchable p95 < 1 s measured on the dev profile with refresh 1 s
- [ ] Version-conflict rejections are exported as a metric

#### Dependencies
- `E06-T04` — Build outbox dispatcher service
- `E06-T05` — Build idempotent consumer framework
- `E07-T02` — Create baseline search projection mapping and projection builder

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** —
- **Source reviews:** Backend/Architecture, QA & Performance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E07-T04

**Build chunk index worker for IndexChunkTask**  
Labels: `role:search`, `role:performance`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§21 steps 1–6; backend finding 10 (byte bounds; ~10 MB texts); eDiscovery: 100 MB+ texts exist.

#### Description
Resolve membership (snapshot range, import batch or reindex range), read current PG state + versions in pages, build projections, send byte-bounded OpenSearch `_bulk` sub-requests with version semantics, back off on 429, retry only failed items, mark complete only when all items succeed or are version-conflict no-ops. Missing PG rows become deletes.

#### Acceptance criteria
- [ ] Bulk request size never exceeds the configured byte limit (test with 10 MB text docs)
- [ ] A retried old bulk task never overwrites a newer interactive edit (fault-injection test)
- [ ] Partial bulk failures retry only failed items; the task completes idempotently
- [ ] Import chunks are indexed without a SnapshotId

#### Dependencies
- `E06-T03` — Create SearchOutbox and IndexChunkTask tables
- `E06-T05` — Build idempotent consumer framework
- `E07-T02` — Create baseline search projection mapping and projection builder

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** Performance
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner, QA & Performance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

---

### E07-T05

**Build logical search service with mandatory workspace filter and cursor binding**  
Labels: `role:search`, `role:security`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§23, §24, §34. Security findings 4–5: filter must be structurally non-removable; PIT IDs are bearer tokens; snippets/highlights of hits can leak privileged metadata. UI finding 2: cursor paging, approximate totals.

#### Description
Single `ISearchService` path: injects `workspaceId` as a filter-context clause plus routing that user ASTs cannot OR away; applies search-side security filters (defence in depth); cursor paging with PIT + `search_after` and capped `track_total_hits` (approximate totals flagged); PIT IDs and cursors bound server-side to (user, workspace); highlights/snippets and grid fields post-filtered against authoritative PG security state for the returned page; responses return only grid fields (no full text); `servedGeneration` field in responses (populated by `E07-T08`).

#### Acceptance criteria
- [ ] Architecture test: OpenSearch client types are referenced only from `Opportunity.Search` infrastructure
- [ ] Property test: 10K random ASTs (OR, NOT, nested, `workspaceId:` injection) all produce DSL whose top-level `bool.filter` holds the authenticated workspace term
- [ ] A PIT/cursor created by user A in WS-1 cannot be used by user B or in WS-2 (→ 404)
- [ ] Highlights/snippets for documents the user cannot view are suppressed
- [ ] Aggregations, highlighting, PIT and `search_after` continuations all carry the workspace filter

#### Dependencies
- `E07-T01` — Implement index management and logical index resolution
- `E05-T02` — Implement permission catalogue, workspace roles and policy decision point

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** Security & Compliance
- **Source reviews:** Backend/Architecture, Security & Compliance, UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

#### Notes
Merges backend 'Logical search service' and SEC-06. Q-12.

---

### E07-T06

**Implement minimal query parser and AST**  
Labels: `role:search`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§9, §33 'a minimal AST/subset is enough initially'; UI needs positioned errors for the query bar.

#### Description
Parser for the ADR-008 subset (AND/OR/NOT, grouping, phrase, `field:value`, ranges, trailing wildcard, escaping, `W/n`) producing a typed AST with source positions; a validate endpoint returning AST or error offsets.

#### Acceptance criteria
- [ ] All supported §9 examples parse to the expected AST (golden tests); malformed queries return a positioned error
- [ ] Fuzz test: no crash or unbounded runtime on 100K random inputs
- [ ] Raw OpenSearch DSL is never accepted from clients

#### Dependencies
- `E02-T05` — Write ADR-006/007/008: index strategy, mapping strategy and minimal query language

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** —
- **Source reviews:** Backend/Architecture, UI/UX, Security & Compliance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E07-T07

**Build search planner with field resolution and proximity**  
Labels: `role:search`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§9; QA finding 3: the §29 mix needs proximity/wildcard and coding-filter queries; security: query-complexity limits against wildcard/proximity DoS.

#### Description
AST → OpenSearch translation: field resolution against FieldDefinitions (type decides term/range/match), `W/n` via span queries, leading-wildcard policy, query-cost limits (max clauses, wildcard expansion, proximity span) and timeouts, coding-field filters against the interim projection.

#### Acceptance criteria
- [ ] Each AST node type has a translation test against a real OpenSearch container
- [ ] Unknown fields produce a user-facing error, never a silent match-none
- [ ] `apple W/10 iphone` returns correct results on the golden corpus
- [ ] Complexity limits return 400 with a clear message

#### Dependencies
- `E07-T06` — Implement minimal query parser and AST
- `E07-T05` — Build logical search service with mandatory workspace filter and cursor binding
- `E07-T02` — Create baseline search projection mapping and projection builder

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** —
- **Source reviews:** Backend/Architecture, Security & Compliance, QA & Performance, eDiscovery Practitioner
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

---

### E07-T08

**Implement search generation watermark and freshness API**  
Labels: `role:search`, `role:performance`, `role:backend`, `P0`, `size:M` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§28, §31.7. Backend finding 7: sequences commit out of order, so 'current through N' needs a low watermark over in-flight work; QA finding 1: the watermark must advance only after OpenSearch refresh, otherwise the lag gate passes when it should fail.

#### Description
Per-workspace commit-ordered generation (counter row updated in the same transaction, or sequence + low-watermark tracking) carried by every SearchOutbox row and IndexChunkTask; watermark = highest N such that all work ≤ N is applied **and refreshed**; behaviour across reindex/alias switch defined. API exposes `indexedThroughGeneration`, `jobGeneration` per job and `isProjectionCurrent`/`servedGeneration` on search responses; OTel gauges `search_generation_lag` and `search.index_lag_seconds` (now − CommittedAt of oldest unreflected work).

#### Acceptance criteria
- [ ] Watermark never advances past an unapplied or unrefreshed generation under concurrent out-of-order commits (property test)
- [ ] During a bulk job `isProjectionCurrent` stays false until watermark ≥ job generation (end-to-end test)
- [ ] Lag is sampled per workspace at 1 s resolution and exported

#### Dependencies
- `E06-T03` — Create SearchOutbox and IndexChunkTask tables
- `E07-T03` — Build version-safe interactive index worker
- `E07-T04` — Build chunk index worker for IndexChunkTask

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** Performance, Backend
- **Source reviews:** Backend/Architecture, QA & Performance, DevOps/SRE, UI/UX
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / M

#### Notes
Proposed amendment: refresh-aware, commit-ordered watermark definition for §28.

---

### E07-T09

**Implement saved searches**  
Labels: `role:backend`, `role:ediscovery`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§1 saved searches; §9. eDiscovery: nesting with cycle detection; results honour the runner's permissions.

#### Description
Persist query text + parsed AST version, expansion options, column set and sort, owner and sharing scope; re-parse at execution; nested saved-search criteria with cycle detection; usable as snapshot/bulk input; audit create/modify/run.

#### Acceptance criteria
- [ ] Results honour the executing user's current permissions (§24), not the creator's
- [ ] A saved search referencing a deleted field returns a clear validation error
- [ ] Cycles in nested saved searches are rejected
- [ ] Create/modify/run are audited

#### Dependencies
- `E07-T07` — Build search planner with field resolution and proximity
- `E14-T01` — Build append-only partitioned audit store and writer

#### Roles
- **Owner:** Backend
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner, UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E07-T10

**Generate search term reports with unique hits**  
Labels: `role:ediscovery`, `role:search`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§9 'term-hit reporting'; eDiscovery: STRs are exchanged in meet-and-confer, so they must be reproducible; legal: hit reports must note index staleness.

#### Description
Input: named terms (paste or CSV `Name,Expression`), scope = saved search / snapshot / workspace. Per term: docs with hits; with family; unique hits (only this term); unique hits with family. Totals: docs with ≥ 1 hit, with family, docs with no hits in scope. Executes against a materialized snapshot; records SnapshotId, SearchGeneration, executor/time.

#### Acceptance criteria
- [ ] Re-running on the same snapshot gives identical numbers
- [ ] Invalid term syntax returns a per-term error without failing the report
- [ ] Export to CSV and XLSX; terms open their hit set as a search
- [ ] The report states when the index was not current at execution time
- [ ] Counts equal the generator ground truth on the known-answer corpus (`E17-T09`)

#### Dependencies
- `E07-T07` — Build search planner with field resolution and proximity
- `E10-T02` — Build materialized DocumentSetSnapshot service
- `E09-T03` — Expand families, duplicates and threads for search, snapshots and bulk actions
- `E07-T08` — Implement search generation watermark and freshness API
- `E17-T09` — Generate known-answer STR and production-QC fixtures

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** Search (OpenSearch)
- **Source reviews:** eDiscovery Practitioner, Legal/Discovery Counsel, Backend/Architecture
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E07-T11

**Implement alias-based reindex and projection generation switch**  
Labels: `role:search`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§8 alias-based reindexing; devops: mapping changes and DR rebuild use it; QA: rebuild-from-PG must converge.

#### Description
Reindex job: create a new ProjectionGeneration index, fill via IndexChunkTask(kind=Reindex) from PG, catch up interactive changes (dual-target dispatch during the window), validate counts/checksums, atomically switch the alias; watermark semantics across the switch per ADR-001.

#### Acceptance criteria
- [ ] Coding during the reindex is visible in the new index after the switch (no lost updates)
- [ ] Validation failure aborts without switching; the old alias keeps serving
- [ ] Rebuild-from-PG projection hash equals the live projection hash on a 1M corpus

#### Dependencies
- `E07-T04` — Build chunk index worker for IndexChunkTask
- `E07-T01` — Implement index management and logical index resolution
- `E07-T08` — Implement search generation watermark and freshness API

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** —
- **Source reviews:** Backend/Architecture, DevOps/SRE, QA & Performance
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

---

### E07-T12

**Extend query language to full legal syntax**  
Labels: `role:search`, `role:ediscovery`, `P2`, `size:L` · Milestone: M5 - Post-MVP / Deferred

#### Context
§9, §33 (full legal QL deferred); eDiscovery Q9: dtSearch/Relativity familiarity (`PRE/n`, `!` root expander).

#### Description
Add `PRE/n`, root expander, nested proximity, and remaining legal syntax beyond the MVP subset behind the AST extension points.

#### Acceptance criteria
- [ ] Every §9 example and the agreed dtSearch-style operators are supported with golden tests
- [ ] Term-hit report counts match the planner for each AST term

#### Dependencies
- `E07-T07` — Build search planner with field resolution and proximity

#### Roles
- **Owner:** Search (OpenSearch)
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner
- **Milestone / phase / size:** M5 - Post-MVP / Deferred / P2 / L

#### Notes
Q-30.
