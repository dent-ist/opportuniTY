# UX / Practitioner Ticket Review (M1–M3)

**Purpose:** apply the [Review-Platform Familiarity Guide](review-platform-familiarity-guide.md) (Q-47) to every M1–M3 ticket in E08, E09, E10, E11, E12, E13, E15 and E16. Each ticket gets one of two verdicts:
- **OK as written**, or
- concrete **additions or changes to its acceptance criteria (AC)**, each marked **MUST** (needed for familiarity or correctness) or **SHOULD** (strong expectation that may be split out into a follow-up ticket).

**How to apply:** this file does not edit `docs/plan/*`. Treat each item as an added AC on the GitHub issue: copy it into the issue, or link this section from the PR. The eDiscovery workflow/UX reviewer (Q-47) checks these items at PR review. "Guide §n" refers to the familiarity guide.

Out of scope here: M5 tickets (E10-T06, E11-T05, E12-T09, E13-T05, E13-T06), which are reviewed when they are scheduled.

---

## E08 — Load-File Import

### E08-T01 (#75) Streaming DAT parser, delimiter profiles, encoding detection — *changes*
- **MUST** — Use vendor-neutral preset identifiers and display names: `concordance-style` "Concordance-style (DC4 / þ / ®)", `pilcrow-thorn` "Pilcrow / thorn (¶ / þ / ®)", `csv` "CSV (RFC 4180)", `custom`. No vendor product names in user-visible strings (Q-47; product question P-4).
- **MUST** — The detection/preview API returns each delimiter as a code point plus its decimal ASCII/ANSI code (`{"char":"þ","codepoint":"U+00FE","decimal":254}`). Overrides accept either a character or a decimal code. Practitioners exchange delimiter specs as "020/254/174".
- **MUST** — Add a UTF-16BE-BOM fixture to the byte-identical encoding test. The description already lists FE FF, but the AC omits it.
- **SHOULD** — Add a "First line contains field names" flag (default on). A header-less file produces generated names `Column 1…n` so mapping can still proceed.
- **SHOULD** — Invalid bytes in TXT files import with replacement characters and set `TextEncodingWarning`. They do not fail the row (eDiscovery review §12).

### E08-T02 (#76) Field mapping, typed parsing, dates, mapping templates — *changes*
- **MUST** — Rename "mapping template" to **Import profile** in UI and API docs. A profile stores delimiter profile + DAT/TXT encodings + field map + per-column parsing (date format, time column, source TZ, multi-value delimiter, Yes/No tokens) + import mode + overlay and path settings. It can be exported and imported as JSON and copied to another workspace (guide §5.1).
- **MUST** — When a profile is re-applied to a file whose headers differ, list *missing* and *new* columns explicitly, and never drop a mapping silently.
- **MUST** — Auto-map records *why* each column matched (exact or alias, and which alias) so the UI can show "matched by alias `BEGDOC`". Auto-map never targets coding or privilege fields (Q-31).
- **MUST** — Add structural targets: Begin/End Bates (received, Q-27), Begin/End Attachment, Parent ID, Family/Group ID, hashes, the E09-T02 thread/duplicate fields, and **Import name** (set automatically, see E08-T03).
- **SHOULD** — Add "Create new field" from a mapping row. For Single/Multiple Choice targets, add an option to create missing choices from the distinct values, with a preview count ("will create 12 choices").
- **SHOULD** — Allow one source column to map to two targets (e.g. `BEGDOC` → Control Number *and* Begin Bates).
- **SHOULD** — Add a Folder path target if P-8 is accepted.

### E08-T03 (#77) Import job orchestration with chunk index tasks — *changes*
- **MUST** — Each import has a user-visible **Import name** (default `<DAT file name> <yyyy-mm-dd>`, editable). The ImportBatch is a searchable, columnable, browsable field ("Import") so a load can be QC'd, overlaid or exported as a set (guide §3.1 browser, §2.4).
- **SHOULD** — The job summary counters use the report vocabulary of E08-T06 (read / imported / overlaid / skipped / errored) from M1 on, so M1 and M3 screens do not diverge.

### E08-T04 (#78) Link natives and extracted text — *changes*
- **SHOULD** — Missing-file and hash-mismatch outcomes carry stable error codes (e.g. `NATIVE_MISSING`, `TEXT_MISSING`, `HASH_MISMATCH_MD5`). The E08-T06 error detail and the UI filter on these codes.
- **SHOULD** — The path-rebase preview returns, for the first 5 rows, raw path → resolved relative path, so the wizard can show a live example (guide §5.1 step 5).
- **SHOULD** — When both an extracted-text path and an OCR-text path are mapped, add a text-precedence option (first non-empty wins, in a user-set order).

### E08-T05 (#79) OPT cross-references and page reconciliation — *changes*
- **MUST** — Support re-loading images for existing documents, as an OPT-only load against existing documents in *Replace pages* mode. Re-imaging is routine. The mode choice is defined in E08-T07, and the parser here must allow an OPT-only load without a DAT.
- **SHOULD** — OPT rejects (orphans, missing images, count mismatches) are written to an OPT error file `<opt-name>_errors.opt` in the original OPT format, plus a reason column in the CSV detail (consumed by E08-T06).
- **SHOULD (product follow-up)** — An "image-only volume" mode, where documents are created from OPT document breaks, is common for received productions. Ticket it for M5 if not in MVP.

### E08-T06 (#80) Pre-flight validation, report, re-loadable error file — *changes*
- **MUST** — The error file is named `<dat-name>_errors.dat`, includes the **original header row** + `ImportError`, and uses the same delimiters, qualifier, newline token and encoding as the source.
- **MUST** — Add an **error detail CSV** (row number, Control Number, column, error code, message) and, when an OPT was loaded, an OPT error file (see E08-T05).
- **MUST** — Pre-flight reports **will create / will update / not found / already exists** counts for the chosen mode and overlay key (guide §5.1 step 6). Each count links to a downloadable list.
- **MUST** — Classify issues as **errors (block)** or **warnings (acknowledge)**. Default classification: field-count mismatch, unparseable required field, duplicate key in file and Append collision are errors. Missing native/text/image, hash mismatch, page-count mismatch and orphan OPT row are warnings. Expose this classification in the API for E08-T08.
- **SHOULD** — Report the "stop after N errors" setting and the elapsed time per phase.

### E08-T07 (#81) Overlay and append/overlay modes — *changes*
- **MUST** — The overlay key dropdown lists only fields declared unique (Control Number by default). Pre-flight verifies that the chosen key is unique in both the file and the workspace.
- **MUST** — Image overlay: in Overlay mode an OPT replaces pages for matched documents. Prior pages and renders are retained per ADR-011/ADR-014, the document's `ImagesIncomplete` is recomputed, and redactions on replaced pages are flagged "page replaced — review redactions". They are never silently re-positioned.
- **MUST** — The pre-flight preview shows before/after values for the first 20 matched documents of each overlaid field.
- **MUST** — The "blank values overwrite" option is presented as *Leave existing values* (default) / *Clear existing values*, which is the practitioner wording.
- **SHOULD** — The Q-31 coding-field overlay switch is per import and per field, and the audit event lists the fields it enabled.

### E08-T08 (#82) Import wizard UI — *changes*
- **MUST** — Steps and order follow guide §5.1: Source & mode → File format → Field mapping → Overlay settings → Files → Validate → Run → Report. The import mode is chosen in step 1.
- **MUST** — The primary source is a **server-side staging location / object-storage prefix browser**. Browser upload is limited to a configurable size, because multi-GB volumes are not uploaded through a browser.
- **MUST** — The mapping grid has columns *Load-file column · sample values · → · Workspace field · type · status*, with Auto-map / Clear / Save as Import profile, and the "matched by alias" reason is shown.
- **MUST** — Delimiters are shown as glyph + decimal code. Preset names are vendor-neutral (E08-T01).
- **MUST** — The report page offers: import report CSV, error file, error detail CSV, OPT error file, **Open imported documents**, and **Re-import corrected error file** (pre-filled with the same profile and mode).
- **SHOULD** — Import history list (name, mode, user, start/end, counts, status) as the landing page of the Imports section.

### E08-T09 (#83) Malware scanning, type verification, quarantine — *changes (small)*
- **SHOULD** — Quarantined documents show a "Quarantined" indicator column value and a viewer state "Content quarantined — not viewable" (no artifact requests). The import report counts them.
- Otherwise OK as written.

---

## E09 — Family, Duplicate & Thread Relationships

### E09-T01 (#84) Reconstruct families — *changes*
- **MUST** — Range mode (Mode A) orders control numbers **naturally** (prefix + numeric), so `ABC10` sorts after `ABC9` when padding is inconsistent.
- **MUST** — Expose computed **Begin Attachment / End Attachment** (as Control Numbers) for every family member, even when the load file supplied only ParentID or GroupIdentifier. Practitioners verify families by these columns, and exports need them (E12-T01).
- **SHOULD** — The family report is downloadable as CSV (Control Number, problem type, related numbers) and is linked from the import report.

### E09-T02 (#85) Upstream duplicate and thread identifiers — *changes (small)*
- **MUST** — Alias maps that recognise vendor-specific header names are **data**, not UI labels. User-visible field names are generic: *Email Thread ID*, *Duplicate Group*, *All Custodians*, *Duplicate Custodians*, *All Paths*, *Conversation Index*, *Inclusive Email* (Q-47).
- Otherwise OK as written.

### E09-T03 (#86) Family/duplicate/thread expansion — *changes*
- **MUST** — The same three expansion options with the same labels ("Include: Family / Duplicates / Email thread") are used in search, Mass Edit, export, production, STR and (later) batch creation. Responses carry the base count and the expanded delta, each with an approximate flag (Q-10).
- **MUST** — Provide a sortable **Date (Family)** field: the parent's document date applied to every member, plus Family Sequence as tiebreak, so "sort by family date" keeps families contiguous in the grid.
- **MUST** — Expansion never includes members the user may not see (Q-11/Q-13), and the delta counts exclude them.

### E09-T04 (#87) Computed duplicate groups — *changes (small)*
- **SHOULD** — Add a grid column **Duplicate Status** with values *Primary / Duplicate / Unique*. Use "Primary", not "master".
- Otherwise OK as written.

### E09-T05 (#88) Family and duplicate coding propagation — *changes*
- **MUST** — Clarify that "per-field propagation configuration" means which fields the **Apply to Family / Apply to Duplicates** dialog *offers or preselects*. Nothing propagates automatically on save (Q-14, Q-09). Add an AC that saves a document with family members and asserts that no other document changes.
- **MUST** — The conflict preview lists the affected members (Control Number, relation, current value → new value) and excludes members the user cannot see, without revealing that they exist (Q-13).
- **MUST** — Propagation above the threshold runs as a Mass Edit job and applies the Q-07 skip rule. Skipped members are reported like any mass edit.

---

## E10 — Coding, Snapshots & Bulk Operations

### E10-T01 (#89) Interactive coding API — *changes (small)*
- **SHOULD** — The 412 body includes the changed field list with the other user's values, so E16-T05 can render "Review differences" without a second call.
- Otherwise OK as written.

### E10-T02 (#90) Materialized snapshots — *changes (small)*
- **SHOULD** — Snapshots carry a human-readable **name/purpose** (e.g. "Mass Edit 2026-10-03 10:42 — Responsiveness") shown as a "Frozen set" in the browser pane and job pages (guide §1.2).
- Otherwise OK as written.

### E10-T03 (#91) PIT-vs-materialized rule engine — **OK as written** (M2). The "results refreshed" signal is consumed by E16-T02/T03.

### E10-T04 (#92) Bulk coding job and worker — *changes*
- **MUST** — Replace "default last-commit-wins with provenance" in the description with the **Q-07 skip rule**. AC: a document whose target field changed after the job's start watermark is unchanged, gets a skip CodingEvent, and is counted. The result exposes a **skipped list** (Control Number, field, changed by, changed at) as CSV and as a document set the UI can open.
- **MUST** — Support the Mass Edit operations of guide §3.6: single-value *set* / *clear*; multi-choice per-choice *add* / *remove* and *replace all*. Unchanged fields are not written and not version-bumped.
- **SHOULD** — The job result distinguishes *updated*, *unchanged (already had value)*, *skipped (Q-07)* and *failed*.

### E10-T05 (#93) Coding history API and review-batch domain — *changes*
- **MUST** — Model batches with the concepts practitioners expect, so the M5 UI is familiar: **Batch Set** (source saved search, max batch size, batch name prefix, keep families together, optional keep threads together, reviewer group). **Batch** (name `<prefix>_0001`, frozen membership, status *Available / Checked out / Completed*, assignee). Check-out/check-in operations with one assignee per batch.
- **SHOULD** — Add system fields *Last Coded By* / *Last Coded On* (derived from CodingEvents) available as grid columns.

---

## E11 — Rendering, Viewer Backend & Redactions

### E11-T01 (#95) Document content API — *changes (small)*
- **SHOULD** — The metadata endpoint returns, per field, the display name, guide type label (Long Text / Short Text / Yes/No …), formatted value in the display time zone, and raw imported string. The Metadata viewer mode then needs no client-side type logic.
- **SHOULD** — Reserve the content-route shape for **production images** (per production) now, so the Production viewer mode (M3) does not need a new API pattern.
- Otherwise OK as written.

### E11-T02 (#96) Render worker pipeline — **OK as written**. (SHOULD: thumbnail size suitable for the Image-mode thumbnail strip, ~150 px wide.)

### E11-T03 (#97) Sandbox render workers — **OK as written.**

### E11-T04 (#98) Redactions API and redaction tool — *changes*
- **MUST** — Use the term **Redaction Set** in UI and API docs. The viewer has a Redaction Set selector, and productions choose one set (E12-T02).
- **MUST** — Redaction styles: *black box* and *labelled box* (the reason text, e.g. "Redacted – Privileged" or "PII", printed in the box at production). The label text is configurable per reason.
- **SHOULD** — Add a "Redact full page" action (one keystroke + confirm), which is very common for privileged attachments.
- **SHOULD** — Add a redaction list panel (page, type, reason, author, date), with click-to-jump.

---

## E12 — Export & Defensible Production

### E12-T01 (#100) Export snapshot to load-file volume — *changes*
- **MUST** — Volume naming: prefix (default `VOL`), start (1), padding (3) → `VOL001`. Subfolders `IMAGES\IMG0001`, `NATIVES\NATIVE0001`, `TEXT\TEXT0001`, `DATA` with configurable prefix/padding and max files per folder (default 1,000).
- **MUST** — Load-file paths are relative, with **backslash separators by default** (option `/`), and line endings are **CRLF**. Natives and text are named `<Control Number>.<ext>`. The OPT volume column holds the volume name.
- **MUST** — The DAT field list is ordered, editable, and supports **header renaming** per field. It is saved with delimiters, encoding (UTF-8 BOM default, UTF-16LE option) and date/time-zone format as an **Export profile**.
- **MUST** — The Q-15 exclusion report is a downloadable CSV (Control Number, reason) linked from the export result.
- **SHOULD** — Add an optional maximum volume size that rolls over to `VOL002`.

### E12-T02 (#101) Freeze production specifications — *changes*
- **MUST** — The specification includes the endorsement slot model of E12-T04, placeholder texts, Redaction Set id+version, family inclusion option, privileged handling (withhold → placeholder / redact → image), and the "wait until index current" choice (Q-10).
- **MUST** — Production statuses are user-visible and fixed: *Draft → Run → QC passed → Finalized*. Specification edits are allowed in Draft only, or after an explicit "Discard run" before finalization. The rule for reusing Bates numbers after a discard is product question P-7.
- **SHOULD** — Add "Copy settings from production…" to create a new Draft from a prior specification.

### E12-T03 (#102) Bates allocation — *changes*
- **MUST** — The start number defaults to the **next available number for the prefix** in this workspace/matter. The UI shows a Bates preview (first, last, count) before run.
- **MUST** — Define the lock point (P-7). Recommended: numbers are *reserved* at Run and *permanent* at Finalize. A discarded, never-finalized run releases its range, and this is audited. A finalized-then-voided production's range is never reissued.
- Otherwise OK as written.

### E12-T04 (#103) Designations and endorsements — *changes*
- **MUST** — Six endorsement slots: header left/centre/right and footer left/centre/right. Each slot holds Bates, Confidentiality Designation, free text or a field value. Defaults: footer right = Bates, footer left = designation.
- **MUST** — The designation choice labels are exactly the stamped legend text, so the load-file value matches the image (QC check in AC 1).
- **SHOULD** — Add an endorsement preview on a sample page in the spec UI (E12-T08).

### E12-T05 (#104) Production volume outputs and DAT/OPT — *changes*
- **MUST** — Volume and folder naming, path separator, CRLF and header renaming follow E12-T01 (shared builder). The production volume default is `<Prefix>_VOL001`.
- **MUST** — Page-level image files are named by the page's Bates number. Natives and text are named by ProdBegBates. The OPT image key is the page Bates.
- **MUST** — Produced images remain viewable in the **Production** viewer mode after finalization. They are either retained or regenerated on demand from the frozen specification (Q-08).
- **SHOULD** — Placeholder text can include field values (Bates, designation, reason).

### E12-T06 (#105) Redaction burn-in verification — **OK as written.**

### E12-T07 (#106) Production QC gate, finalization, manifest — *changes (small)*
- **MUST** — Every exception list has "Open in Documents" (opens the exception set in the document list) and CSV download.
- **SHOULD** — Add a warning (acknowledge) check: documents with Privilege Status = *Needs 2L Review* in the production set.
- **SHOULD** — Add a Bates cross-reference download (Control Number ↔ ProdBeg/ProdEnd) to the finalized output.

### E12-T08 (#107) Export and production wizard UI — *changes*
- **MUST** — Exports and Productions are separate sections (guide §2.1). Export follows guide §5.5. A production is a **page with tabs** (*Settings · Documents · QC · Output · Privilege Log*), not a one-shot wizard. Practitioners iterate on a production across days.
- **MUST** — The production list shows name, prefix and Bates range, documents/pages, status and finalized date.
- **MUST** — Each past production shows its frozen specification, snapshot ID and count, QC report, manifest and checksum downloads, Bates cross-reference, **Re-run with same specification** and **Copy settings**.
- **MUST** — The Documents tab opens Review mode with the **Production** viewer mode preselected.

---

## E13 — Privilege Review, Privilege Log & Clawback

### E13-T01 (#109) Privilege designations as system fields — *changes*
- **MUST** — Seed the **default workspace template** (guide §3.4): the privilege fields plus Responsiveness, Confidentiality Designation, Issues, Key Document, Reviewer Comments, and the *First Pass Review* and *Privilege Review* coding layouts. If template seeding belongs to E04-T03, add it there. Without it, a new workspace is unfamiliar on first open.
- **MUST** — Privilege Basis is conditional-required when Privilege Status ∈ {Withhold, Redact} in the default layouts (matches AC 1 at the UI level).

### E13-T02 (#110) Family/duplicate privilege inconsistencies — *changes (small)*
- **MUST** — The report offers "Open in Documents" for each conflict group, so conflicts are fixed in the normal review screen, plus CSV download.
- **MUST** — Conflicts involving documents the requesting user cannot see are reported only to users who may see them. A user without access sees no hint of them (Q-13).

### E13-T03 (#111) Privilege logs — *changes*
- **MUST** — Align with **Q-20**. M3 delivers the metadata-only, auto-generated document-by-document log with optional per-document description (through the Privilege Description field). Mark **categorical logs (format 3) as P2/deferred** in this ticket. Q-20 explicitly defers them, and the current description contradicts that.
- **SHOULD** — Add a configurable Priv ID prefix and sequence (`PRIV0001`) for withheld documents without a placeholder Bates.

### E13-T04 (#112) Where-produced lookup — *changes (small)*
- **SHOULD** — Surface the lookup as the **Produced in** tab of Related Items (guide §3.2), and add a grid column *Production Bates (latest)*.
- Otherwise OK as written.

---

## E15 — Frontend Foundation & Accessibility

### E15-T01 (#122) Frontend ADR, tokens, core components — *changes*
- **MUST** — ADR-018 references this guide as the binding UX source and records the Q-47 guardrails (guide §7.1): our own palette, typography and mark; an icon set with a permissive licence recorded by the licence check. No colours, icons or assets taken or traced from other review platforms.
- **MUST** — Compact density is the **default** for Documents and Review mode.
- **SHOULD** — Add core components the guide relies on: tri-state checkbox (Mass Edit add/remove/unchanged), split-pane with collapse, step-rail wizard, badge with text alternative for family/duplicate indicators.

### E15-T02 (#123) Application shell and workspace context — *changes*
- **MUST** — Replace primary navigation "Review, Search, Jobs, Admin" with the guide §2.1 sections: **Documents** (default landing), Review Batches (hidden until M5), Searches (Saved Searches · Search Terms Reports, Q-65), Productions, Imports, Exports, Jobs, Admin ▾. Search is part of Documents, with no separate Search section. Tabs are RBAC-filtered.
- **MUST** — Add an installation-level **Workspaces list** after login (recent + search) and a workspace switcher in the header.
- **MUST** — "No access" and "not found" are one indistinguishable page (Q-13).
- **MUST** — The About page carries the licence notice (Q-25) and no third-party marks.

### E15-T03 (#124) Keyboard command framework — *changes*
- **MUST** — Ship the guide §4 default map, including Save & Previous, Cancel edits, focus coding pane / jump to field n, Apply to Family, Mass Edit, select-all-results, clear selection and toggle highlighting.
- **MUST** — One preference disables all single-key shortcuts. Bindings use `KeyboardEvent.code`. The cheat sheet (`?` / `Alt+Shift+/`) is scoped to the focused region.
- **SHOULD** — Allow export/import of a user's key map as JSON, and reset to defaults.

### E15-T04 (#125) Accessibility and UI-performance gates — *changes (small)*
- **SHOULD** — Add a **shortcut conflict matrix** (browsers × OS × screen readers) as a checked artifact. A default that conflicts must change in the guide and the registry together.
- **SHOULD** — The keyboard-only e2e uses the guide default bindings.

### E15-T05 (#126) Localisation and time-zone display — **OK as written.** (SHOULD: en-US default date format `MM/DD/YYYY`, en-GB `DD/MM/YYYY`, with ISO available as a user preference. Practitioners comparing against load files often want ISO.)

---

## E16 — Review Workspace UI

### E16-T01 (#127) Query bar — *changes*
- **MUST** — The query bar sits inside the **Documents search panel** together with a **Conditions builder** (field · operator · value rows; operators *is, is not, is any of, is none of, is set, is not set, contains, between/before/after*) that compiles to the query syntax (`field:*` = is set). Most practitioner searches are field conditions. A keyword-only bar is not familiar. If this is too large for M1, split the builder into its own M3 ticket. Do not drop it.
- **MUST** — Add Include toggles (Family / Duplicates / Email thread) next to the bar. They are wired when E09-T03 lands (M3) and hidden before that.
- **SHOULD** — Help text documents the supported syntax in our own words, without naming any third-party search engine.

### E16-T02 (#128) Virtualized cursor-paged grid — *changes*
- **MUST** — Checkbox column, then **Control Number pinned first**, then a family/duplicate indicator column (text alternative available), then View columns.
- **MUST** — `Enter` / double-click / Control Number link opens Review mode with the cursor bound to this list. "Back to list" restores scroll position and selection.
- **MUST** — The results header shows the count (exact / "≈" / "≥ 10,000 (approx.)"), the freshness stamp "Current as of hh:mm", the selected count and Mass Actions. The footer shows "Rows a–b of ≈N".
- **SHOULD** — Add a page-size preference (rows fetched per cursor page: 50 / 100 / 250 / 500), pending P-2.

### E16-T03 (#129) Review layout and review cursor — *changes*
- **MUST** — Documents has **List mode** and **Review mode** (guide §3.1/§3.2). The browser pane (Saved Searches · Field Browser · Imports · Frozen sets) is in List mode. Review mode has viewer centre, coding right and Related Items below or beside, with "Doc n of ≈N" and Prev/Next in the viewer toolbar.
- **MUST** — Unsaved edits prompt **Save / Discard / Cancel** on any navigation away from the document.
- **MUST** — Opening a related item does not move the review cursor. A breadcrumb returns to the cursor document. At the end of the list there is no silent wrap.
- **SHOULD** — Add a collapsible list strip in Review mode showing neighbouring rows.

### E16-T04 (#130) Viewer modes — *changes*
- **MUST** — Mode names and fixed order: **Extracted Text · Image · Native · Production · Metadata**. Production is disabled with the reason "Not produced" until E12 (M3). Native shows rendered imported PDFs, otherwise a file card + Download native, role-gated by Q-18 (see P-3).
- **MUST** — Default mode = the user's last used mode if available, else Image → Extracted Text → Metadata. Persist it as a user preference.
- **MUST** — Extracted Text shows the Q-29 truncation banner. Metadata shows the display-TZ value with the raw imported string in a tooltip.
- **SHOULD** — Add find-in-document (`Ctrl/⌘+F` scoped to the viewer) over chunked text.

### E16-T05 (#131) Coding panel from layouts — *changes*
- **MUST** — Add a **layout selector** (layouts permitted for the role; last used remembered per user per workspace).
- **MUST** — Choice fields with ≤ 15 choices render as radio buttons / checkboxes with visible access digits (1–9). Digit keys toggle choices when the field has focus.
- **MUST** — Explicit **Save**, **Save & Next**, **Save & Previous** and **Cancel**, with no autosave on change. Save & Next at the end of the list shows "End of list". The pane is read-only for users without coding permission.
- **MUST** — Add **Apply to Family… / Apply to Duplicates…** entry points (wired in E16-T10/E09-T05, M3) and a Document History link (E10-T05, M3). Hide them until their backends exist.

### E16-T06 (#132) Selection model and bulk dialog — *changes*
- **MUST** — Name the dialog **Mass Edit** and put it under a **Mass Actions** menu with *Mass Edit*, *Export to Load File*, *Export List (CSV)* (role-gated) and later *Apply to Family*, *Add to Batch Set*.
- **MUST** — Mass Edit uses a coding layout with a per-field **Change** checkbox. Single fields offer *Set / Clear*. Multiple Choice fields have tri-state **Add / Remove / Leave unchanged** per choice plus **Replace all** (needs E10-T04 change).
- **MUST** — The confirmation text explains the **Q-07 skip rule**. The completion toast shows Updated / Skipped / Failed with **Download skipped list** and **Show skipped in list**.
- **MUST** — Selection header options: *This page*, *All ≈N results*, *None*, plus the "Select all ≈N results" banner after a page select.

### E16-T07 (#133) Freshness and two-phase progress — *changes*
- **MUST** — The detail popover with raw generation numbers is visible **only to admin/support roles** (Q-10: reviewers see plain language only). The AC currently implies everyone sees it.
- **MUST** — Two-phase progress labels: **Saved** (committed) and **Searchable**, consistent across the job tray, the Jobs page and Mass Edit/import results.
- Otherwise OK.

### E16-T08 (#134) Security-affecting coding and restricted states — *changes*
- **MUST** — Align with Q-11/Q-12/Q-13 (guide §3.5). Hits the user may not see are never rendered, because the API drops them. The placeholder applies **only** to rows already loaded before access was revoked. Its label is "No longer available", with no metadata, and it is indistinguishable for walled, restricted and deleted documents.
- **MUST** — After a save that removes the user's own access (confirmed), Review mode advances to the next document and the list row becomes "No longer available".
- Otherwise OK.

### E16-T09 (#135) Column configuration and saved grid views — *changes*
- **MUST** — Call them **Views**. The View selector sits above the grid. A workspace *Default* view exists. Shared views are managed in Admin › Views.
- **SHOULD** — Add a per-column filter row (contains / is / range) that compiles into the current search's Conditions, so it is visible and saveable.
- **SHOULD** — Add **Export List (CSV)** of the current view (role-gated per Q-18, audited, formula-neutralised).

### E16-T10 (#136) Families/duplicates display and propagation — *changes*
- **MUST** — Fix the conflict with Q-13/Q-11. Remove "Restricted members show 'Restricted item' without metadata". Restricted and walled members are **omitted**, and counts reflect visible members only (pending P-5).
- **MUST** — Call the viewer panel **Related Items**, with tabs *Family · Duplicates · Email Thread · Produced in* (Produced in via E13-T04). Columns: relation, Control Number, name/subject, date, configurable coding columns. Clicking a row opens the item without moving the review cursor.
- **MUST** — Family indicators in the grid: P (parent), └A (attachment), D (has duplicates), with text alternatives.

### E16-T11 (#137) Saved search panel — *changes*
- **MUST** — The saved-search editor has name, folder, Private/Shared, keyword, **Conditions**, Include options and **View**. Re-opening shows exactly what was saved.
- **MUST** — The ⋯ menu offers Run · Edit · Copy · Move · Delete · Search Terms Report · Mass Edit results · Export · Use in production.
- **MUST** — Label frozen snapshots **"Frozen set"** (N documents, at T, by whom) in a separate browser node. A live saved search is never called frozen.

### E16-T12 (#138) Term-hit highlighting and highlight sets — *changes (small)*
- **MUST** — Call them **Highlight Sets**, administered in Admin › Highlight Sets (term list; colour per set or per term). Reviewers toggle sets in the viewer highlight bar, and toggles persist per user. "Search hits" is a built-in set for the current search.
- **MUST** — Hit navigation uses the guide §4 bindings (F3 / Shift+F3, ⌘G / ⌘⇧G, `n` / `N`).
- Otherwise OK.

---

## Gaps: expectations no M1–M3 ticket covers

| Gap | Familiarity impact | Suggested home |
|---|---|---|
| **Search Terms Report UI** (the E07-T10 backend has no screen) | High. STRs are a weekly deliverable | New UI ticket in E16 (M3) |
| **Default workspace template** (fields, choices, layouts, guide §3.4) | High on first open | E04-T03 / E13-T01 |
| **Document folders / folder browser** | Medium–high (P-8) | Product decision first |
| **Export List (CSV)** of the grid view | High for case teams | E16-T09 or new ticket |
| **Privilege Log tab UI** (E13-T03 has no UI ticket) | High for production managers | E12-T08 scope or new ticket |
| **Highlight Sets admin screen** and **Redaction Set / reason admin** | Medium | E16-T12 / E11-T04 scope, confirm |
| Coding history UI, job monitor, fields/layout editor, users & groups | Covered by E14-T04, E06-T07, E04-T06, E05-T08 | These tickets must also follow the guide (not reviewed here) |

---

## Product questions

Practitioner expectations that conflict with, or are not settled by, existing decisions. Each has a recommended answer.

- **P-1 (Q-14) — Automatic family propagation.** Experienced users are used to administrator-configured fields that propagate coding to the family automatically on save, commonly for privilege and responsiveness. Q-14 forbids automatic inheritance. *Recommendation:* keep Q-14. Add a per-layout option that shows the "Apply to Family" fields **pre-ticked** in the Save flow, still explicit and previewed.
- **P-2 (Q-32) — Paging.** Users expect page numbers, a page-size selector and "last page". Q-32 rules out deep jumps. *Recommendation:* virtual scroll with cursor paging, a rows-per-fetch preference, and "Go to end" implemented as a reversed sort. Confirm that this is acceptable.
- **P-3 (Q-18 + Q-36) — Natives for reviewers.** With reviewer download off (Q-18) and no in-browser native rendering (Q-36), a Reviewer cannot see a spreadsheet or other non-PDF native at all in MVP. Only its extracted text is visible. *Recommendation:* allow native download for Reviewers per workspace setting, limited to configured file types (audited), until E11-T05.
- **P-4 (Q-26 vs Q-47) — Vendor-named presets and docs.** Q-26 adopted "Relativity, Nuix and Concordance/CSV presets", but Q-47 bans the Relativity name in the UI. *Recommendation:* use vendor-neutral preset names in the UI (guide §5.1). Keep vendor alias maps as internal data. Documentation may name formats and products nominatively with trademark attribution. Confirm the docs part.
- **P-5 (Q-11/Q-13) — Restricted family members.** Omitting restricted or walled members (as Q-13 requires) makes a family look complete to a reviewer who cannot see them. *Recommendation:* omit, with no placeholder. Rely on Q-14 production QC run by an authorised user to catch family incompleteness. Confirm that no "N hidden members" hint is wanted for Q-11 classes (walls must stay silent).
- **P-6 (E10-T06 at M5) — Batches.** Batch check-out is the primary way contract reviewers work. Deferring it to M5 means MVP review runs off shared saved searches, with collisions between reviewers. *Recommendation:* consider a minimal M3 "Batch Set → check out / check in" on top of E10-T05, without QC sampling.
- **P-7 (E12-T03) — Bates reuse after a discarded draft run.** Practitioners expect that a production can be re-run before delivery without burning numbers. *Recommendation:* reserve at Run, make permanent at Finalize, release (audited) on discard. Never reissue after finalization or void.
- **P-8 (baseline gap) — Folders.** Practitioners browse by the folder structure that comes from processing (a folder-path column). No ticket covers this. *Recommendation:* a single-valued *Folder Path* structural field mapped on import, shown as a tree in the browser pane (M3). Mass move is deferred.
