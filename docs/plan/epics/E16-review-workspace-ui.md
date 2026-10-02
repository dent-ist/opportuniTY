# E16 — Review Workspace UI

**Labels:** `epic`, `role:ui`, `role:security`, `role:search`, `P0`  
**Starts in:** M1 - First Vertical Slice  
**Tickets:** 12

## Goal
Give reviewers the core review experience at 100M-document scale: query bar, virtualized cursor-paged grid, review cursor, viewer modes, coding panel, frozen-count bulk actions, honest freshness, access-restricted states, family display and term-hit highlighting.

## Baseline sections
§5, §6, §9, §10, §13, §21, §22, §24, §28, §32

## Scope / out of scope
**In scope**
- Query bar
- Grid
- Review layout + cursor
- Viewer modes
- Coding panel
- Selection + bulk dialog
- Freshness indicator
- Security-affecting/restricted states
- Columns/views
- Family/duplicate display + propagation
- Saved-search panel
- Hit highlighting + persistent highlight sets

**Out of scope**
- Batching/QC UI (E10-T06)
- Thread analytics UI (§33)

## Contributing roles
- **Roles:** UI/UX, Security & Compliance, Search (OpenSearch)
- **Source reviews:** UI/UX, eDiscovery Practitioner, Security & Compliance, QA & Performance
- **Milestones spanned:** M1 - First Vertical Slice, M2 - 1M Benchmark & Architecture Blockers, M3 - MVP Feature Complete

## Exit criteria
- [ ] A reviewer completes search → open → code → next → bulk tag → export in the UI
- [ ] Counts are labelled approximate or stale whenever the projection is not current
- [ ] Denied documents never render content, snippets or artifact requests

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E16-T01](#e16-t01) | Build query bar with syntax validation and error positions | M1 | M | E07-T06, E15-T01, E04-T03 |
| [E16-T02](#e16-t02) | Build virtualized, cursor-paged review grid | M1 | L | E07-T05, E15-T01 |
| [E16-T03](#e16-t03) | Build review workspace layout and review cursor | M1 | L | E16-T02, E15-T03 |
| [E16-T04](#e16-t04) | Build viewer modes for text, metadata, native and page images | M1 | L | E11-T01, E16-T03 |
| [E16-T05](#e16-t05) | Build coding panel rendered from coding layouts | M1 | L | E10-T01, E04-T03, E16-T03 |
| [E16-T06](#e16-t06) | Build selection model and bulk action dialog with frozen-target confirmation | M1 | L | E16-T02, E10-T04, E12-T01 |
| [E16-T07](#e16-t07) | Show search freshness and two-phase progress in plain language | M2 | M | E07-T08, E16-T02 |
| [E16-T08](#e16-t08) | Handle security-affecting coding and access-restricted states | M2 | M | E16-T05, E05-T04 |
| [E16-T09](#e16-t09) | Add column configuration and saved grid views | M3 | M | E16-T02, E04-T03 |
| [E16-T10](#e16-t10) | Display families and duplicates with relationship panel and propagation | M3 | L | E09-T03, E09-T05, E16-T03 |
| [E16-T11](#e16-t11) | Build saved search panel | M3 | S | E07-T09, E16-T01 |
| [E16-T12](#e16-t12) | Implement term-hit highlighting and persistent highlight sets | M3 | L | E16-T04, E07-T07 |

---

### E16-T01

**Build query bar with syntax validation and error positions**  
Labels: `role:ui`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§9, §33 minimal QL; no raw DSL exposure.

#### Description
Single-line/expandable editor with highlighting of operators, fields and ranges; field-name autocomplete from FieldDefinitions and choice values; inline parse errors at the exact position returned by the validate endpoint; per-user query history.

#### Acceptance criteria
- [ ] Invalid queries show an inline error anchored to the offending token, announced to screen readers; search is not executed
- [ ] Autocomplete suggests fields and SingleChoice/MultiChoice values with ≤ 150 ms debounce
- [ ] History keeps the last 50 queries per workspace per user
- [ ] Every supported §9 example parses and highlights correctly

#### Dependencies
- `E07-T06` — Implement minimal query parser and AST
- `E15-T01` — Write frontend architecture ADR and build design tokens and core components
- `E04-T03` — Implement field definitions, choices and coding layouts

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E16-T02

**Build virtualized, cursor-paged review grid**  
Labels: `role:ui`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§1, §8, §22. UI finding 2: offset paging breaks at 100M; totals may be approximate.

#### Description
Virtual rows, cursor paging (`search_after` inside a PIT), sticky columns, keyboard row navigation, approximate-vs-exact totals, PIT-expiry recovery.

#### Acceptance criteria
- [ ] Only visible rows + buffer are in the DOM; memory is flat after scrolling 50k rows
- [ ] Scrolling back re-uses cached pages and never re-queries from offset 0
- [ ] Approximate totals display as '≥ 10,000 (approx.)' or '~1.2M' with a 'count exactly' action where supported
- [ ] On PIT expiry the grid re-establishes the cursor, keeps the anchor document if present and shows 'results refreshed'
- [ ] ARIA grid semantics with row/column counts and `aria-rowindex`; arrow/Home/End/PageUp/PageDown navigation

#### Dependencies
- `E07-T05` — Build logical search service with mandatory workspace filter and cursor binding
- `E15-T01` — Write frontend architecture ADR and build design tokens and core components

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

#### Notes
Q-32.

---

### E16-T03

**Build review workspace layout and review cursor**  
Labels: `role:ui`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
UI finding 4: next/previous needs a stable ordered set across long sessions; finding 16: prefetch must not create view audit events.

#### Description
Three-pane layout (list, viewer, coding) with resizable/collapsible panes and pop-out viewer; review cursor bound to the originating result set with 'Doc 1,204 of ~58,330'; prefetch of next document's first page/text through the gateway, flagged as prefetch.

#### Acceptance criteria
- [ ] Pane sizes persist per user; each pane is a landmark reachable by focus-cycle shortcut
- [ ] Next/previous follows grid order including after PIT refresh; a document that left the set shows 'continue from next'
- [ ] Next document visible ≤ 500 ms p95 when prefetched (measured)
- [ ] Prefetch creates no 'viewed' audit events (verified against the audit log)

#### Dependencies
- `E16-T02` — Build virtualized, cursor-paged review grid
- `E15-T03` — Build keyboard command framework and user preferences

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

#### Notes
Q-33.

---

### E16-T04

**Build viewer modes for text, metadata, native and page images**  
Labels: `role:ui`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§13; UI finding 8: modes and render-pending states undefined; 10 MB texts.

#### Description
Mode switcher showing only modes with artifacts: extracted text (virtualised, chunked API), metadata, native download (permission-gated), and page images/PDF (zoom 25–400%, rotate, fit, thumbnails, go-to-page) once renders exist; render-pending/failed/unavailable states; short-lived URLs only after authorization.

#### Acceptance criteria
- [ ] Modes without an artifact are disabled with a reason ('Image rendering in progress', 'No native')
- [ ] A 10 MB text document shows its first screen in ≤ 1 s with no long tasks > 200 ms
- [ ] Page viewer supports thumbnails, go-to-page, zoom and keyboard page navigation
- [ ] Copied content URLs expire

#### Dependencies
- `E11-T01` — Build document content API for metadata, chunked text, page images and natives
- `E16-T03` — Build review workspace layout and review cursor

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

#### Notes
Q-36.

---

### E16-T05

**Build coding panel rendered from coding layouts**  
Labels: `role:ui`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§5, §6, §21 interactive path. UI findings 6 and 9: read-your-own-writes, save semantics, conflict UX.

#### Description
Panel rendered from the layout: all 9 field types, required/conditional fields, number-key choice selection, save and save-and-next, optimistic local state, 'Saved · indexing' badge until projected version ≥ saved version, local overlay of own pending edits on grid values, DocumentVersion conflict handling.

#### Acceptance criteria
- [ ] Every field type renders with validation messages linked via `aria-describedby`; required fields block save-and-next and focus the first invalid field
- [ ] Coding acknowledgement ≤ 200 ms p95 (UI target, measured)
- [ ] Version conflict shows 'Changed by J. Smith at 10:42' with review-differences/overwrite options; no silent last-write-wins
- [ ] Own pending edits overlay grid values until searchable

#### Dependencies
- `E10-T01` — Implement interactive coding API with optimistic concurrency
- `E04-T03` — Implement field definitions, choices and coding layouts
- `E16-T03` — Build review workspace layout and review cursor

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

---

### E16-T06

**Build selection model and bulk action dialog with frozen-target confirmation**  
Labels: `role:ui`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§10, §22, §32 BULK TAG/EXPORT. UI finding 11: confirmations must show the frozen count, not the live count.

#### Description
Gmail-style selection ('this page', 'checked', 'all ~M results' represented as query + generation, never as a large ID list); bulk dialog for coding/tagging and basic export: fields/values or export options, expansion choice, confirmation with frozen count from the snapshot API, expansion delta, snapshot strategy and conflicts; typed confirmation above a threshold or for security-affecting fields; submit creates a job and returns immediately.

#### Acceptance criteria
- [ ] Selecting all results in a 10M-hit search sends query + generation; no ID list above a fixed bound (e.g. 1,000) is sent
- [ ] Confirmation shows 'Frozen at 10:42 · generation 18,432' and explains differences from the grid count
- [ ] Above the threshold or for security fields the user must type the count or 'CONFIRM'
- [ ] The dialog is fully keyboard-operable; selection count is announced via live region; focus returns to the grid

#### Dependencies
- `E16-T02` — Build virtualized, cursor-paged review grid
- `E10-T04` — Build bulk coding job and worker
- `E12-T01` — Export a materialized snapshot to a load-file volume

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX, eDiscovery Practitioner
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

#### Notes
Merges UI 'Selection model', 'Bulk coding flow' and the basic-export entry point. Q-34.

---

### E16-T07

**Show search freshness and two-phase progress in plain language**  
Labels: `role:ui`, `P0`, `size:M` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§28; UI finding 5: 'Index current through generation 18,432 / Job generation 18,517' means nothing to reviewers.

#### Description
Freshness pill on search/grid: Current / Updating (≈N pending, ~Ts behind) / Delayed (> 2 min); every result set stamped with its served generation; footnote 'Counts may not include N recent changes'; job-linked banner 'Bulk coding committed · Search index updating… (63%)'; admin popover with raw generations as in §28. Adds Playwright scenarios for banner show/clear (extends `E03-T03`).

#### Acceptance criteria
- [ ] States use colour plus icon/text (never colour alone); announced politely at most once per 30 s
- [ ] Banner shows while watermark < job generation and clears after catch-up (Playwright)
- [ ] Detail popover shows 'Index current through generation X / Job generation Y'

#### Dependencies
- `E07-T08` — Implement search generation watermark and freshness API
- `E16-T02` — Build virtualized, cursor-paged review grid

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX, QA & Performance
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / M

#### Notes
Q-10.

---

### E16-T08

**Handle security-affecting coding and access-restricted states**  
Labels: `role:ui`, `role:security`, `P0`, `size:M` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§24; UI findings 7 and 10.

#### Description
Mark security-affecting fields ('Affects access' + lock icon); confirm changes that reduce access incl. one's own and move on afterwards; standard 'You don't have access to this document' state for viewer, grid rows and prefetch with no content, snippet or artifact request; denied rows become placeholders for the session. Adds the Playwright stale-hit scenario (revoke between search and open → denied).

#### Acceptance criteria
- [ ] Saving a change that removes the user's own access requires confirmation
- [ ] A denied document makes no artifact requests (network assertion in e2e)
- [ ] Stale-hit Playwright scenario passes against the developer profile

#### Dependencies
- `E16-T05` — Build coding panel rendered from coding layouts
- `E05-T04` — Build protected-content gateway and authoritative access service

#### Roles
- **Owner:** UI/UX
- **Contributing:** Security & Compliance
- **Source reviews:** UI/UX, Security & Compliance, QA & Performance
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / M

#### Notes
Q-12.

---

### E16-T09

**Add column configuration and saved grid views**  
Labels: `role:ui`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
UI finding 3: ADR-004 may make some coding columns non-sortable; field capability metadata.

#### Description
Column chooser (structural §23 fields, metadata, coding fields), reorder/resize/pin/sort, saved views per user or shared per workspace; non-sortable/filterable columns marked with reason.

#### Acceptance criteria
- [ ] Layout persists per user per workspace
- [ ] Sort controls are disabled with a tooltip on fields flagged non-sortable
- [ ] Multi-column sort on up to 3 fields with deterministic controlNumber tiebreak
- [ ] Shared views require 'manage views' permission

#### Dependencies
- `E16-T02` — Build virtualized, cursor-paged review grid
- `E04-T03` — Implement field definitions, choices and coding layouts

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E16-T10

**Display families and duplicates with relationship panel and propagation**  
Labels: `role:ui`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§5, §9; UI finding 13; eDiscovery: family sort keeps families contiguous.

#### Description
'Include family/duplicates' toggles with delta counts ('1,240 hits + 3,102 family members'); family-grouped grid mode (parent + indented children by FamilySequence, collapsible); duplicate indicator with one-click pivot; thread ID column; viewer relationship tab listing family and duplicates with coding status; 'apply to family/duplicates' with conflict preview via `E09-T05`.

#### Acceptance criteria
- [ ] Families never split across a page boundary without a 'continued' marker; orphans show a parent placeholder
- [ ] Restricted members show 'Restricted item' without metadata
- [ ] Tree has `aria-level`/`aria-expanded` semantics
- [ ] Propagation previews count and conflicts; above the threshold it creates a job

#### Dependencies
- `E09-T03` — Expand families, duplicates and threads for search, snapshots and bulk actions
- `E09-T05` — Implement family and duplicate coding propagation
- `E16-T03` — Build review workspace layout and review cursor

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX, eDiscovery Practitioner
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
Merges UI 'Family and duplicate display and expansion' and 'relationship panel and propagation'.

---

### E16-T11

**Build saved search panel**  
Labels: `role:ui`, `P1`, `size:S` · Milestone: M3 - MVP Feature Complete

#### Context
§1 saved searches; UI ticket 'Saved searches and search panel'.

#### Description
Saved-search tree with folders, private/shared scopes, run/edit/clone/delete, 'use as bulk target'; clear distinction between 'Saved search (live)' and 'Snapshot (frozen, N docs, at time T)'.

#### Acceptance criteria
- [ ] Save/rename/move/clone/delete respect permissions
- [ ] Running a saved search shows the current freshness stamp
- [ ] Live vs frozen is visually distinct

#### Dependencies
- `E07-T09` — Implement saved searches
- `E16-T01` — Build query bar with syntax validation and error positions

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / S

---

### E16-T12

**Implement term-hit highlighting and persistent highlight sets**  
Labels: `role:ui`, `role:search`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§9 term-hit reporting, §13 viewer; UI finding 8 and eDiscovery: persistent highlight sets are a baseline reviewer expectation; OpenSearch highlighting fails above `max_analyzed_offset`.

#### Description
Server-computed hit offsets over chunked text (not OpenSearch highlight fragments); hit panel with per-term counts; next/previous hit across unloaded chunks; workspace highlight sets (term lists + colours, phrases and wildcards) toggled by reviewers; banner for `TextTruncated` docs; image-mode highlighting labelled 'Text mode only' (coordinates require OCR, deferred).

#### Acceptance criteria
- [ ] Next/previous hit scrolls, focuses and announces 'Hit 4 of 37 "termination"'
- [ ] Highlights use outline/underline in addition to colour with ≥ 3:1 contrast
- [ ] Proximity and phrase hits highlight as whole spans
- [ ] Docs with > 1M characters highlight without timing out

#### Dependencies
- `E16-T04` — Build viewer modes for text, metadata, native and page images
- `E07-T07` — Build search planner with field resolution and proximity

#### Roles
- **Owner:** UI/UX
- **Contributing:** Search (OpenSearch)
- **Source reviews:** UI/UX, eDiscovery Practitioner
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
Merges UI 'Term-hit highlighting' and eDiscovery 'Persistent highlight term sets'.
