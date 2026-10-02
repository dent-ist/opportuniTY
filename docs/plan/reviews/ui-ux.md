# UI/UX Review — opportuniTY Architecture Baseline

Reviewer role: senior UI/UX designer, legal-tech / eDiscovery review tools (Relativity, Everlaw, DISCO, Reveal patterns).
Scope: Angular frontend (§3, §4). Every section of the baseline was read; this review comments only on the parts that bear on UX.

## Review findings

1. **The frontend has no UX or UI architecture section (§3, §4, §18).** The only mention is "Angular — review grid, viewer, coding, admin". The baseline does not set an accessibility target (WCAG 2.2 AA), a design system or component library, a state-management approach, i18n or localisation (date formats are a real issue for international matters), browser support, or minimum viewport. Without these, the vertical slice (§20, §32) will produce screens built one at a time with nothing in common. **Recommendation:** add a "Frontend Architecture & UX Baseline" section, with an ADR beside the 14 listed in §19.

2. **The grid paging model is not defined for 100M documents (§1, §8, §22).** At this scale, offset paging (`from/size`) stops working. The grid will need `search_after` cursors, a PIT for a stable scroll, and an approximate hit count (`track_total_hits` capped). The spec does not say:
   - whether reviewers can jump to row N or to the last page;
   - whether sorting works on any column (custom JSONB metadata fields §6 may not be sortable under `flattened` mappings §8);
   - whether totals are exact or labelled "≥ 10,000".

   These choices drive the scrollbar, the page-jump control and the count labels.

3. **ADR-004 (§25, §26) directly limits what the grid and search can do, but the spec does not treat it as a UX decision.** Under Candidate B or D, sorting, filtering, aggregating and highlighting on coding fields combined with content may be slow or not possible. Examples: "sort by Responsive", facet counts by issue tag, "coded by me today". **Recommendation:** add UX acceptance cases to the §26 decision gates. The candidates should be measured against grid sort/filter/facet on coding columns, not only against search p95.

4. **The review cursor will outlive the PIT (§22).** Next/previous document navigation in the viewer needs an ordered, stable set for a reviewer session that can last hours, which is far longer than a PIT keep-alive. The spec defines snapshots for bulk jobs only, not for interactive review sets. Questions the spec leaves open:
   - What happens when the PIT expires mid-session? Silently re-query? Show "results refreshed — position may have shifted"?
   - Should a review session get a lightweight materialized snapshot?

   This is the most common reviewer workflow and it is unspecified.

5. **The §28 watermark example is written for engineers, not reviewers.** "Index current through generation 18,432 / Job generation 18,517" means nothing to a reviewer. The UI needs:
   - plain-language states: Current / Updating (≈N changes pending, ~Ts behind) / Stale (>2 min, per the §26 lag gate);
   - a per-search "as of" stamp, which requires the search API to return the generation it served;
   - a per-job "searchable at" milestone.

   Also ambiguous: whether the counts shown during indexing (§28) should be greyed, footnoted, or shown with a delta range.

6. **Read-your-own-writes on interactive coding is not specified (§7, §21).** Coding → searchable has a p95 of ≤1 s, which is not guaranteed. A reviewer who codes a document and then re-runs "Responsive = blank" will still see it, and will report it as a bug. **Recommendation:** the UI should keep a short-lived local overlay of the reviewer's own pending edits and badge them "saved · indexing". The API needs to return DocumentVersion and the outbox state for this to work.

7. **Stale hits and protected content are underspecified (§24).** §24 allows a stale hit to remain visible but says nothing about what the hit shows. Grid columns, snippets and highlights come from OpenSearch, and these can themselves be privileged (filename, subject, a snippet of a privileged email). **Recommendation:**
   - The UI should render a standard "Access restricted" row and viewer state when the authoritative check denies access.
   - The product owner must decide whether snippets and metadata columns count as protected content (see Q3).
   - Viewer prefetch of the next document must also go through the re-check.

8. **The viewer modes and term-hit model are vague (§9, §13, §33).** §9 promises "term-hit reporting" and §13 defines rendering, but the spec does not define:
   - the viewer modes: extracted text, image/page, native/PDF, and a metadata pane;
   - how hits map across modes. Image-mode highlighting needs word coordinates, and OCR is deferred (§1).
   - persistent highlight sets (reviewer-configured term colours, an Everlaw/Relativity staple);
   - per-term hit counts and next/previous-hit navigation.

   Documents with text near 10 MB (§29) cannot be loaded or highlighted in one piece. The text viewer needs a chunked or virtualised text API with server-computed hit offsets rather than OpenSearch highlight fragments.

9. **The coding panel model is missing (§5, §6, §14).** Field types are listed, but the following are not:
   - coding layouts: which fields, in what order, grouped how, per role;
   - required fields and conditional fields (e.g. Privilege Basis only when Privileged = Yes);
   - save semantics (autosave vs explicit save, save-and-next);
   - optimistic concurrency when two reviewers code the same document. DocumentVersion exists but no conflict UX is defined.
   - **Family and duplicate propagation** ("apply to family"). Every major platform has it; the baseline describes family-aware production (§14) but not family-aware coding. Propagation turns an interactive edit into a bulk edit, so which path does it take, §21 interactive or bulk?

10. **Security-affecting coding needs its own UX (§24).** Privilege and confidentiality tags change access immediately and get a stronger SLA. These fields should be visually marked, should ask for confirmation when the change removes access (including the reviewer's own access), and should show the reviewer what will happen ("this document will become hidden from Group X").

11. **Bulk-action confirmations must use frozen counts (§10, §22).** The confirmation dialog must show the *frozen* target count, taken from the PIT or materialized snapshot, not the live search count. It must also show the family/duplicate expansion delta and the snapshot strategy chosen by the §22 rule. Undo is undefined. CodingEvent history (§27) makes a "revert job" possible, so the product owner must decide whether to offer it. Partial failure (JobChunk errors, dead-letter §11) needs a "N failed — retry failed chunks / view errors" UX.

12. **Job monitoring needs two-phase progress (§7, §11, §28).** The UX must show "committed in PostgreSQL" and "searchable in OpenSearch" as two separate progress lines, because PostgreSQL is authoritative immediately while indexing lags. The spec does not say how progress reaches the browser: polling or push (SignalR/SSE). With 100 concurrent reviewers (§17) and no cache (§16), polling cost matters. The scope and visibility of job notifications are also not defined.

13. **Family, duplicate and thread display is not designed (§5, §9, §34).** The IDs exist from day one, but the following are not described:
   - grid grouping or indentation of families (parent then children by FamilySequence);
   - the "include family" / "include duplicates" toggles on search (§9 expansion);
   - a duplicate-group pivot;
   - a relationship panel in the viewer.

   Threading UI is deferred (§1, §33), but the thread ID should still appear as a groupable column.

14. **Keyboard-driven review and accessibility are absent.** Professional reviewers code thousands of documents a day using the keyboard. The baseline needs:
    - shortcuts for next/previous document, next/previous hit, setting a choice, save-and-next, mode toggle, and focus-region cycling;
    - configurable bindings, with conflict detection against screen readers and browser shortcuts;
    - WCAG 2.2 AA requirements: 2.4.11 Focus Not Obscured, 2.5.8 Target Size, 3.3.7 Redundant Entry, 2.5.7 Dragging alternatives for redaction drawing;
    - live-region announcements for async job and freshness changes.

15. **The redaction UX has gaps (§13).** The open questions:
    - Coordinate-only redactions need rendered page images. What does the user see while the render job is pending or has failed?
    - Is there text-mode or term-based redaction ("redact all hits of X")?
    - How are natives that have no image (spreadsheets) redacted?
    - The baseline mentions a type/reason; the UX also needs a reason picklist, a preview of the burned-in result, and redaction-level audit.
    - Is concurrent editing by two reviewers on the same page locked or merged?

16. **The UI has no performance targets (§17, §30).** Server p95s are defined, but the following are not:
    - time-to-first-page in the viewer;
    - document-to-document navigation latency;
    - grid scroll frame budget;
    - coding-save acknowledgement time.

    **Recommendation:** add UI targets, e.g. next document visible in ≤500 ms p95 with prefetch, and coding acknowledgement in ≤200 ms. Note that prefetch interacts with audit (§15): a prefetched document must not be logged as a "viewed" event until it is actually displayed.

## Proposed epics and tickets

### EPIC: Frontend Foundation, Design System and Accessibility
**Goal:** Set up the Angular workspace shell, a token-based design system, the keyboard-command framework and the WCAG 2.2 AA guardrails that every later screen builds on. This keeps the vertical slice from producing a different UI on every screen.
**Implements:** §3, §4, §15 (RBAC-aware navigation), §16 (Lite profile), §18 (Opportunity.Web), §20/§32 (slice shell).

#### Design tokens and core component library
- **Role:** UI
- **Description:** Define design tokens (colour, type, spacing, density, elevation, focus ring) with light, dark and high-contrast themes. Provide compact and comfortable density modes, which are critical for review grids. Build or wrap core components (button, input, select, multi-choice, date, dialog, toast, tabs, split pane, badge, status pill, progress) on Angular CDK, with Storybook documentation.
- **Acceptance criteria:**
  - Tokens are published as CSS custom properties. Every component consumes tokens only; lint fails on hard-coded colours.
  - All text and UI component colour pairs meet WCAG AA contrast (4.5:1 text, 3:1 non-text) in every theme. This is verified by an automated check.
  - Every component has a Storybook story with axe checks passing and a documented keyboard interaction.
  - Interactive targets are ≥24×24 CSS px (WCAG 2.5.8).
  - Compact density shows ≥30 grid rows at 1080p.
- **Dependencies:** none
- **Phase:** P0
- **Size:** L

#### Application shell, auth and workspace context
- **Role:** UI
- **Description:** Build the OIDC login and the top-level shell: workspace switcher, primary navigation (Review, Search, Jobs, Admin) and a user menu. WorkspaceId is held in route and state as the hard boundary (§2.3). Navigation items are hidden or disabled according to the RBAC permissions returned by the API.
- **Acceptance criteria:**
  - OIDC code+PKCE login/logout and silent token renewal work against the configured IdP (Entra optional).
  - Every API call carries the active WorkspaceId from the route. Switching workspace clears all workspace-scoped client state (cache, selections, viewer). This is verified by an e2e test.
  - A deep link to a document in a workspace the user lacks access to shows a "No access" page, not a blank screen or an error dump.
  - The shell includes a skip-to-content link and landmark regions (header/nav/main), and passes axe.
- **Dependencies:** Design tokens and core component library; API auth and permission endpoint
- **Phase:** P0
- **Size:** M

#### Keyboard command framework and shortcut configuration
- **Role:** UI
- **Description:** Build a central command registry with context scopes (grid, viewer, coding, redaction). Default bindings follow the reviewer conventions of established platforms. Add a "?" cheat-sheet overlay and per-user rebinding with conflict detection. Single-key shortcuts must be possible to turn off or remap (WCAG 2.1.4).
- **Acceptance criteria:**
  - Commands exist for next/previous document, next/previous hit, focus region cycle (grid → viewer → coding), save, save-and-next, viewer mode toggle and bulk-select toggle.
  - Single-character shortcuts can be disabled or remapped, and are never active while focus is in a text input.
  - The cheat-sheet lists the active bindings for the current context.
  - Rebinding saves to the user profile and survives a reload.
  - A reviewer can complete the code → save → next loop with no mouse use. This is verified by an e2e test.
- **Dependencies:** Application shell, auth and workspace context; user-preferences API
- **Phase:** P0
- **Size:** M

#### Accessibility and UI-performance quality gates in CI
- **Role:** UI
- **Description:** Add CI checks:
  - axe-core in component and e2e tests;
  - a keyboard-only e2e path through the vertical slice;
  - Lighthouse/performance budgets;
  - a frame-budget test for grid scroll.

  Also publish the WCAG 2.2 AA conformance checklist as a living document.
- **Acceptance criteria:**
  - The CI fails on new axe serious or critical violations.
  - The vertical-slice e2e (search → open → code → bulk tag → export) runs keyboard-only.
  - The initial JS bundle is ≤ an agreed budget (e.g. 500 KB gzip). Viewer and admin routes are lazy-loaded.
  - The grid scroll test with 10k loaded rows keeps ≥50 fps on the reference machine.
- **Dependencies:** Design tokens and core component library
- **Phase:** P0
- **Size:** M

### EPIC: Search and Review Grid at Scale
**Goal:** Let reviewers query, browse, sort and select over very large result sets with honest counts and visible freshness. The grid must stay fast at 100M documents with server-side paging, and must work regardless of how ADR-004 is resolved.
**Implements:** §8, §9, §10, §22, §23, §24, §28, §31.7, §32 (SEARCH, SEARCH NEW CODING, SEARCH/VERIFY), §33 (minimal AST).

#### Query bar with syntax validation and error positions
- **Role:** UI
- **Description:** Build a single-line or expandable query editor for the minimal opportuniTY Query Language subset (§9, §33). It provides:
  - syntax highlighting of operators, fields and ranges;
  - field-name autocomplete from FieldDefinitions;
  - inline parse errors at the exact character position returned by the parser;
  - query history.

  There is no raw DSL exposure.
- **Acceptance criteria:**
  - Invalid queries show an inline error anchored to the offending token, and the error text is announced to screen readers. Search is not executed.
  - Autocomplete suggests field names and SingleChoice/MultiChoice values, with debounce at ≤150 ms.
  - Query history keeps the last 50 queries per workspace per user.
  - Every example in §9 parses and is highlighted correctly.
- **Dependencies:** Parser validate endpoint returning AST or error offsets; FieldDefinitions API
- **Phase:** P0
- **Size:** M

#### Virtualized, server-paged review grid
- **Role:** UI
- **Description:** Build a grid with virtual rows (CDK virtual scroll or equivalent) and cursor-based paging (`search_after` inside a PIT). It supports sticky columns, keyboard row navigation, and an approximate-vs-exact total count display.
- **Acceptance criteria:**
  - Only visible rows plus a buffer are in the DOM, and memory stays flat after scrolling 50k rows.
  - Pages are fetched by cursor. Scrolling backwards re-uses cached pages and does not re-query from offset 0.
  - Approximate totals show as "≥ 10,000 (approx.)" or "~1.2M". A "count exactly" action is available where the backend supports it.
  - When the PIT expires, the grid re-establishes the cursor, keeps the current anchor document if it still exists, and shows a non-blocking "results refreshed" notice.
  - The grid has ARIA grid semantics: row and column counts, `aria-rowindex` on virtual rows, and arrow/Home/End/PageUp/PageDown navigation.
- **Dependencies:** Search API with PIT and search_after, approximate-count support; Design tokens and core component library
- **Phase:** P0
- **Size:** L

#### Column configuration and saved grid views
- **Role:** UI
- **Description:** Add a column chooser (structural fields from §23, imported metadata, coding fields), with reorder, resize, pin and sort. Views can be saved per user or shared per workspace. Columns that cannot be sorted or filtered under the active search mapping or ADR-004 outcome are marked as such rather than failing.
- **Acceptance criteria:**
  - The user can add, remove, reorder, resize and pin columns, and the layout persists per user per workspace.
  - Sort controls are disabled, with a tooltip explaining why, on fields the API flags as non-sortable.
  - Multi-column sort on up to 3 fields, with a deterministic tiebreak on controlNumber.
  - Shared views require the "manage views" permission.
- **Dependencies:** Virtualized, server-paged review grid; field capability metadata API (sortable/filterable/aggregatable per field)
- **Phase:** P1
- **Size:** M

#### Selection model ("this page", "checked", "all N results")
- **Role:** UI
- **Description:** Build a Gmail-style selection model that distinguishes explicit IDs from "all results of query Q". When "all results" is selected, the selection is represented as query + generation, never as a client-side ID list.
- **Acceptance criteria:**
  - Selecting all results in a 10M-hit search sends query + generation to the API, and no ID list over a fixed bound (e.g. 1,000) is sent.
  - The selection banner reads "N selected on this page — select all ~M results?" and states which kind of selection is active.
  - The selection clears on query change, with an undo toast.
  - The selection count is announced through a live region.
- **Dependencies:** Virtualized, server-paged review grid
- **Phase:** P0
- **Size:** S

#### Search freshness and watermark indicator (§28)
- **Role:** UI
- **Description:** Build a persistent freshness pill on search and grid views that turns projection generation data into plain language. The pill opens a detail popover showing the raw generations for administrators and support. Every result set is stamped with the generation it was served at.
- **Acceptance criteria:**
  - The pill states are **Current**, **Updating (≈N pending, ~Ts behind)** and **Delayed (>2 min)**, each with both colour and icon or text (never colour alone).
  - When the served generation is lower than the latest committed job generation that affects the workspace, counts show a footnote: "Counts may not include N recent changes".
  - A job-linked banner reads "Bulk coding committed · Search index updating… (63%)" and disappears automatically when the job's generation is covered.
  - Changes are announced politely (aria-live=polite) at most once per 30 s.
  - The detail popover shows "Index current through generation X / Job generation Y" as in §28.
- **Dependencies:** Search API returning servedGeneration; workspace projection-watermark endpoint (§31.7); Job API with job generation
- **Phase:** P0
- **Size:** M

#### Family and duplicate display and expansion in search
- **Role:** UI
- **Description:** Add "Include family" and "Include duplicates" expansion toggles (§9). Add an optional family-grouped grid mode: the parent row with indented children ordered by FamilySequence, collapsible. Add a duplicate-group indicator column with a pivot to "show all in group". The thread ID is available as a column only (analytics deferred, §33).
- **Acceptance criteria:**
  - The toggles change the query plan, and the count shows the delta ("1,240 hits + 3,102 family members").
  - In grouped mode, a family never splits across a page boundary without a "continued" marker. Children of an orphan (parent not in the result set) show a parent placeholder.
  - The duplicate icon opens a filtered grid for that DuplicateGroupId in one action.
  - The tree has `aria-level` and `aria-expanded` semantics.
- **Dependencies:** Virtualized, server-paged review grid; search planner family/duplicate expansion
- **Phase:** P1
- **Size:** M

#### Saved searches and search panel
- **Role:** UI
- **Description:** Build a saved-search tree with folders, private and shared scopes, run/edit/clone, and "use as bulk target". Saved searches record the query definition only and are re-run live. Snapshots are a separate concept.
- **Acceptance criteria:**
  - Users can save, rename, move, clone and delete saved searches, subject to permission checks.
  - Running a saved search shows the current freshness stamp.
  - The UI clearly distinguishes "Saved search (live)" from "Snapshot (frozen, N docs, at time T)".
- **Dependencies:** Saved-search API; Query bar with syntax validation and error positions
- **Phase:** P1
- **Size:** S

### EPIC: Document Viewer, Coding Panel and Redactions
**Goal:** Give reviewers the core three-pane review experience (grid/list, viewer, coding) with fast document-to-document navigation, term-hit navigation, safe coding with clear save state, and non-destructive redactions. Authoritative access checks must surface clearly throughout.
**Implements:** §5, §6, §13, §14, §21 (interactive path), §24, §32 (VIEW DOCUMENT, INTERACTIVE CODE).

#### Review workspace layout and review cursor
- **Role:** UI
- **Description:** Build a three-pane layout (document list, viewer, coding panel) with resizable, collapsible panes and pop-out viewer support. A review cursor binds next/previous to the originating result set, with a "Doc 1,204 of ~58,330" position indicator. Prefetch the next document's first page and text, but do not log a view event until the document is actually displayed.
- **Acceptance criteria:**
  - Pane sizes persist per user. Each pane is a landmark and is reachable by the focus-cycle shortcut.
  - Next/previous follows grid order, including after the PIT refreshes. If the current document has left the set, the user sees "Document no longer in results — continue from next".
  - The next document is visible in ≤500 ms p95 when prefetched (UI target to be benchmarked).
  - Prefetch does not create "viewed" audit events, verified against the audit log.
- **Dependencies:** Virtualized, server-paged review grid; Keyboard command framework and shortcut configuration; viewer/content APIs with authorization re-check
- **Phase:** P0
- **Size:** L

#### Viewer modes: extracted text, image/page, native/PDF, metadata
- **Role:** UI
- **Description:** Build a mode switcher that shows only the modes with available artifacts. Each mode handles the render-pending, failed and unavailable states. The text mode streams or virtualises large text up to about 10 MB. The image/PDF mode renders page images with zoom, rotate, fit and thumbnails. The native mode offers download, gated by permission and an authorization re-check.
- **Acceptance criteria:**
  - Modes without an artifact are disabled with a reason ("Image rendering in progress", "No native").
  - A 10 MB text document opens with the first screen in ≤1 s, and the browser does not freeze; there are no long tasks over 200 ms.
  - Page viewer: thumbnails, go-to-page, zoom 25–400%, and keyboard page navigation.
  - The metadata mode lists all fields with their types and handles empty values.
  - All content requests use short-lived URLs issued after an authoritative check (§24). Copied URLs expire.
- **Dependencies:** Render worker artifacts (§13); text chunk API; artifact access endpoint with authz re-check
- **Phase:** P0
- **Size:** L

#### Term-hit highlighting and hit navigation
- **Role:** UI
- **Description:** Highlight query hits in text mode from server-supplied hit offsets, and show a hit panel with per-term counts. Next/previous hit works across the whole document, including chunks not yet loaded. Add reviewer-defined persistent highlight sets: term lists with colours, per workspace. Image-mode highlighting is supported only where word coordinates exist (OCR deferred) and is otherwise labelled "Text mode only".
- **Acceptance criteria:**
  - The hit panel lists each query term with a count. Clicking a term cycles through its hits.
  - Next/previous hit scrolls to and focuses the hit, and announces "Hit 4 of 37 'termination'".
  - Highlights never rely on colour alone (an outline or underline is also applied), and contrast is ≥3:1 against the background.
  - Proximity (W/n) and phrase hits highlight as whole spans.
- **Dependencies:** Viewer modes: extracted text, image/page, native/PDF, metadata; term-hit offset API (§9)
- **Phase:** P1
- **Size:** M

#### Coding panel rendering from coding layouts
- **Role:** UI
- **Description:** Render a coding panel from a layout definition covering all §6 field types, with required and conditional fields, keyboard choice selection (number keys), save and save-and-next, an optimistic local state, and a "Saved · indexing" badge until the API confirms searchability. Conflicts are detected through DocumentVersion.
- **Acceptance criteria:**
  - Every field type (Text, Keyword, Integer, Decimal, Date, Boolean, SingleChoice, MultiChoice, User) renders with validation messages linked through `aria-describedby`.
  - A required field blocks save-and-next, and the error summary focuses the first invalid field.
  - Coding acknowledgement appears in ≤200 ms p95 (UI target). The indexing badge clears when the document's projected version is greater than or equal to the saved version.
  - On a version conflict, the user sees "Changed by J. Smith at 10:42" with options to review the differences or overwrite. No silent last-write-wins.
  - The reviewer's own pending edits overlay the grid values until they are searchable.
- **Dependencies:** Coding API returning DocumentVersion; coding layout API; Field definitions and coding layout editor
- **Phase:** P0
- **Size:** L

#### Security-affecting coding and access-restricted states
- **Role:** UI
- **Description:** Visually mark fields that change access (privilege, confidentiality, ethical wall). A change that reduces access asks for confirmation and shows its impact. Add a standard "Access restricted" state for grid rows, viewer and prefetch when the authoritative check denies access to a stale search hit (§24).
- **Acceptance criteria:**
  - Security fields carry a lock icon and the label "Affects access".
  - Saving a change that removes the current user's own access asks for explicit confirmation, and afterwards the user is moved on to the next document.
  - A denied document shows "You don't have access to this document", with no content, snippet or artifact request. This is verified by an e2e test that revokes access between search and open.
  - Grid rows that are denied on open are replaced with a placeholder for the rest of the session.
- **Dependencies:** Coding panel rendering from coding layouts; authz re-check endpoints; PO decision on snippet/metadata visibility (Q3)
- **Phase:** P0
- **Size:** M

#### Family and duplicate relationship panel and propagation
- **Role:** UI
- **Description:** Add a viewer side tab listing family members (tree by FamilySequence) and duplicates, with one-click open and coding status per member. Add "Apply coding to family / duplicates" actions. Propagation runs as a bulk job when it exceeds the interactive threshold.
- **Acceptance criteria:**
  - The panel shows the parent, the children in order and the duplicates, each with control number, type and key coding values.
  - Members the user cannot access appear as "Restricted item" and do not reveal metadata.
  - Propagation previews the affected count and conflicts ("3 members already coded differently") before it is applied.
  - Above the threshold (PO to confirm), propagation creates a job and links to Job monitor.
- **Dependencies:** Review workspace layout and review cursor; family/duplicate API; Bulk coding flow with frozen-target confirmation
- **Phase:** P1
- **Size:** M

#### Redaction tool
- **Role:** UI
- **Description:** Add a redaction mode on the page/image viewer for drawing rectangle redactions with a type and reason from a configurable picklist. Users can move, resize, delete and undo redactions, and see a list of all redactions on the document. There is a production-preview toggle that simulates the burned-in result. Redactions are page-coordinate annotations (§13) and the source is never modified.
- **Acceptance criteria:**
  - Drawing works with mouse, touch and keyboard. A keyboard alternative creates and adjusts a box with arrow keys (WCAG 2.5.7).
  - Each redaction stores its type, reason, author and timestamp, and these appear in the redaction list.
  - Redaction mode is disabled while page images are pending or failed, with the message "Redaction requires rendered images".
  - The preview shows black-box or labelled output that matches the production burn-in style.
  - Concurrent edits by another user show a refresh prompt, and redactions are never silently lost.
- **Dependencies:** Viewer modes: extracted text, image/page, native/PDF, metadata; redaction API (ADR §19.12)
- **Phase:** P1
- **Size:** L

### EPIC: Bulk Actions, Jobs and Exports
**Goal:** Make long-running asynchronous work safe and understandable. Bulk confirmations must use frozen counts, job progress must show the two phases (committed vs searchable), failures must be recoverable, and export/basic production must be reproducible.
**Implements:** §7, §10, §11, §14, §21, §22, §27, §28, §32 (BULK TAG, EXPORT).

#### Bulk coding flow with frozen-target confirmation
- **Role:** UI
- **Description:** Build a bulk-action dialog launched from a grid selection: choose fields and values, choose family/duplicate expansion, then review a confirmation. The confirmation shows the **frozen** target count from the snapshot API, the expansion delta, the snapshot strategy (PIT vs materialized, from the §22 rule) and the conflicts with existing coding. Submitting creates a job and links to its monitor page.
- **Acceptance criteria:**
  - The confirmation count comes from the created snapshot or PIT target, not the live search, and is labelled "Frozen at 10:42 · generation 18,432".
  - When the frozen count differs from the grid count, the dialog explains why (index lag or expansion).
  - Above a configurable threshold, or for security-affecting fields, the user must type the count or "CONFIRM".
  - Submitting returns immediately with a toast linking to the job, and the reviewer can continue reviewing.
  - The action is fully keyboard operable, and focus returns to the grid on close.
- **Dependencies:** Selection model ("this page", "checked", "all N results"); snapshot/bulk job API (§22); Job monitor list and detail with two-phase progress
- **Phase:** P0
- **Size:** M

#### Job monitor list and detail with two-phase progress
- **Role:** UI
- **Description:** Build a jobs page per workspace covering import, index, bulk coding, render and export jobs, with filters by type, status, creator and date. The detail view shows chunk progress, a separate "Committed" and "Searchable" progress line, the attempt and error counts, dead-lettered chunks, and the CorrelationId for support. Progress arrives by push (SSE/SignalR) with a polling fallback.
- **Acceptance criteria:**
  - The detail view shows two progress bars: "Committed (PostgreSQL)" and "Searchable (index)". Each has a count, a percentage and an ETA.
  - Failed or dead-lettered chunks show their error summaries, with a "Retry failed chunks" action for users with permission.
  - Updates arrive within 5 s of a state change, without a page reload.
  - The progress bars have `role=progressbar` with value text. Status changes are announced politely.
  - CorrelationId and SnapshotId can be copied in one click.
- **Dependencies:** Job/JobChunk/IndexChunkTask status API; push channel decision (Q6)
- **Phase:** P0
- **Size:** M

#### Global job tray and notifications
- **Role:** UI
- **Description:** Add a shell-level tray showing the user's running jobs and recent completions or failures, with toasts on completion. It links to the job detail and to "search the results of this job".
- **Acceptance criteria:**
  - The tray badge shows the number of running jobs and is visible from every workspace page.
  - Completion toasts persist until dismissed when the job failed.
  - Notifications are scoped to jobs the user created, or to all jobs when the user is a workspace admin.
- **Dependencies:** Job monitor list and detail with two-phase progress
- **Phase:** P1
- **Size:** S

#### Export and basic production wizard
- **Role:** UI
- **Description:** Build a step-by-step wizard:
  1. Source: a saved search or the current selection, materialized as a snapshot.
  2. Family-aware inclusion.
  3. Output: natives, images/PDF, text, load-file format.
  4. Bates prefix, start number and padding, plus endorsements (production only).
  5. Redaction burn-in toggle.
  6. Validation summary.
  7. Run.

  Past exports show their reproducible definition and manifest/checksum download.
- **Acceptance criteria:**
  - Every export or production is bound to a materialized snapshot, and the summary shows the snapshot ID and document count.
  - Validation errors (e.g. documents missing images when image output was chosen, redacted documents without images) block the run with downloadable lists.
  - The Bates range preview is shown before running.
  - Completed outputs list their manifest, checksums and download links. Every download triggers the authorization re-check.
  - "Re-run with same definition" produces identical membership.
- **Dependencies:** Export/production API; snapshot API; Job monitor list and detail with two-phase progress
- **Phase:** P1 (basic export in P0 per §32; Bates and endorsements in P1)
- **Size:** L

### EPIC: Workspace Administration
**Goal:** Give workspace and system administrators the screens to stand up a matter: create workspaces, define fields and coding layouts, import DAT/OPT loads with field mapping, manage roles, and review audit. Full batching/QC administration stays deferred (§33).
**Implements:** §1 (workspace management, audit), §5, §6, §12, §15, §34 (batching UI deferred).

#### Workspace management screens
- **Role:** UI
- **Description:** Build workspace list, create and settings screens: name, matter number, storage profile, and index placement (read-only; it is decided by Index Management, §8). Add deletion with a hold/retention check and an explicit multi-step confirmation.
- **Acceptance criteria:**
  - Creating a workspace takes the user to an empty-state checklist: Import → Fields → Layouts → Users.
  - Deletion is blocked with an explanation when a hold or retention policy applies. Otherwise the user must type the workspace name and is shown a summary of what will be removed (§15).
  - Index placement and current projection generation are displayed read-only.
- **Dependencies:** Workspace API; Application shell, auth and workspace context
- **Phase:** P1
- **Size:** M

#### Field definitions and coding layout editor
- **Role:** UI
- **Description:** Build field-definition CRUD for all §6 types, with choice lists (reorder, deactivate rather than delete once used) and a "security-affecting" flag. Add a drag-and-drop coding-layout editor (sections, order, required flag, conditional visibility, role assignment) that has a keyboard alternative and a live preview.
- **Acceptance criteria:**
  - Type changes on fields that already hold data are prevented, with an explanation.
  - Choices that are in use can be deactivated, which hides them from new coding but keeps historical values readable.
  - The layout editor works fully with the keyboard: move-up/move-down controls as an alternative to dragging (WCAG 2.5.7).
  - The preview renders exactly what the coding panel will render.
  - Mapping limitations are surfaced, e.g. "This field will not be sortable" when the API reports it.
- **Dependencies:** FieldDefinitions and layout APIs; Coding panel rendering from coding layouts
- **Phase:** P0 (minimal: fields + single layout) / P1 (conditional, per-role)
- **Size:** L

#### Import wizard with DAT/OPT field mapping
- **Role:** UI
- **Description:** Build an import wizard:
  1. Select or upload the load files and the text/native/image folders (or an object-storage prefix).
  2. Detect DAT delimiters and encoding, with a 20-row preview.
  3. Map DAT columns to existing or new fields, with type inference.
  4. Map OPT, text and native paths.
  5. Validation pass: missing files, duplicate control numbers, family integrity.
  6. Run as a job.

  Mapping templates can be saved.
- **Acceptance criteria:**
  - The DAT preview shows the parsed columns with the detected delimiter, quote character and encoding, all of which can be overridden.
  - Every mapped column shows its target field and type. Unmapped columns are explicit, with an "ignore" or "store in metadata" option.
  - The validation summary lists error counts with downloadable detail before anything is committed. Warnings can be acknowledged, while errors block the import.
  - The import job links to the Job monitor and shows a two-phase progress line (stored vs searchable).
  - Mapping templates can be saved and re-applied to a later load.
- **Dependencies:** Import API with preview/validate endpoints (§12); Job monitor list and detail with two-phase progress
- **Phase:** P0
- **Size:** L

#### Roles, permissions and user assignment
- **Role:** UI
- **Description:** Build workspace RBAC screens: roles with a permission matrix, user and group assignment, and field-level restrictions (§15). Ethical walls and document-level restrictions are display-ready but can be P2.
- **Acceptance criteria:**
  - The permission matrix is an accessible table with row and column headers, toggleable by keyboard.
  - Changes take effect immediately for protected-resource access, and the UI says so ("Applies immediately; search lists may lag briefly").
  - A user can't remove their own last admin role without a warning.
- **Dependencies:** RBAC API
- **Phase:** P1
- **Size:** M

#### Audit log viewer
- **Role:** UI
- **Description:** Build a server-paged, filterable audit list (actor, action, document, job, date range) with export to CSV as a job. A per-document history tab in the viewer shows the coding provenance from CodingEvent (§27).
- **Acceptance criteria:**
  - Filters combine. Results page by cursor and are never loaded all at once.
  - The document history shows each coding change with field, old value, new value, actor, time, and either the JobId (bulk) or "interactive".
  - Audit export runs as a job and is itself audited.
- **Dependencies:** Audit query API; CodingEvent history API
- **Phase:** P1
- **Size:** M

#### (Deferred) Review batch and QC administration
- **Role:** UI
- **Description:** Placeholder for batch creation, assignment, reviewer progress and QC sampling and re-review screens. The domain supports these now (§14, §34) but the UI is deferred under §33. Only a read-only "Coded by / coded at" column and the provenance tab ship in P1.
- **Acceptance criteria:**
  - Design specification and wireframes are approved before build.
  - The P1 grid exposes "Last coded by / at" columns from provenance.
- **Dependencies:** Review batch domain/API
- **Phase:** P2
- **Size:** L

## Open questions for the product owner

1. **Grid totals and paging:** Is an approximate count ("~1.2M", "≥10,000") acceptable for large result sets? Or must reviewers always see exact counts, even if that costs latency? Do users need "jump to page N" or "last page", which cursor paging makes expensive?
2. **Review session stability:** When a reviewer works through a result set for hours, should their next/previous order be frozen (a lightweight snapshot per review session)? Or is it acceptable for the set to re-query and shift as others code?
3. **Stale hits and privileged metadata (§24):** Do grid column values, snippets and highlight fragments from OpenSearch count as "protected content"? If yes, the grid must post-filter or re-authorize rows, which affects latency and the ADR-004 benchmark.
4. **Family/duplicate coding propagation:** Should "apply to family/duplicates" be supported in MVP? Should it be automatic for some fields (e.g. privilege)? Should it ever be enforced? Above what size does propagation become a bulk job?
5. **Bulk undo:** Should a completed bulk coding job be revertible from the UI using CodingEvent history (§27)? Who may do it? Is a confirmation typing threshold (e.g. >10k documents or any security field) wanted?
6. **Progress delivery:** May the UI use a push channel (SignalR/SSE through the API pool) for job and watermark updates? Or must v1 be polling-only? Polling at 100 reviewers with no cache (§16) has a cost.
7. **Freshness UX audience:** Should reviewers see raw generation numbers (§28)? Or only plain-language states, with generation numbers shown just to admins and support?
8. **Viewer for natives:** Until the native-rendering technology is chosen (§33), is "download native + extracted text + imported images/PDF" acceptable for MVP review? Are there any formats (e.g. spreadsheets) that need in-browser rendering on day one?
9. **Redaction scope for MVP:** Is rectangle-on-image enough? Or are text-mode redaction, term-based "redact all hits", and native spreadsheet redaction required for the first production?
10. **Accessibility and localisation commitments:** Can we formally commit to WCAG 2.2 AA (and VPAT/ACR publication)? Which locales and date/number formats must ship in MVP?
