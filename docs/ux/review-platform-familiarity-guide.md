# Review-Platform Familiarity Guide

> **Binding companion:** [ai-ui-guidelines.md](ai-ui-guidelines.md) (product owner) — *copy the workflow, not the
> pixels*. Where this guide and the AI UI guidelines differ, the AI UI guidelines win.

**Status:** Binding for all UI tickets and for import, export, review and production work (decision **Q-47**).
**Owner:** eDiscovery workflow / UX specialist. Changes need the UI lead and, where a Q-decision is touched, the product owner.
**Companion:** [ticket-review.md](ticket-review.md) holds per-ticket acceptance-criteria changes that apply this guide.
**Frontend architecture:** ADR-018 (frontend architecture, `E15-T01`) owns the technical side: components, state, paging and push. This guide owns *what* the user sees and does. If the two conflict, raise it with the UI lead. Do not resolve it silently in code.

## 0. How to use this guide

- **Goal.** An experienced review-platform user (typically a Relativity administrator, reviewer or litigation-support analyst) can sit down at opportuniTY and import, search, review, mass-code, export and produce **without training**. The screens they need are where they expect them, use the words they expect, and behave the way they expect.
- **Limit.** We copy *conventions*, not *products* (§7). We use generic industry terms that are shared across review platforms. We use our own visual design, icons, wording and help text. The Relativity name never appears in the product UI.
- **Precedence.** Product decisions (`docs/plan/decisions.md`) override this guide. Where practitioner habit conflicts with a decision, the decision wins and the UI must *explain* the difference at the point of use (examples: Q-07 skipped documents, Q-14 explicit apply-to-family, Q-10 "≈" counts). Open conflicts are listed in [ticket-review.md § Product questions](ticket-review.md#product-questions).
- **Review gate.** Q-47 requires the eDiscovery workflow/UX specialist to review import, export, review and UI PRs before merge. PR descriptions for those areas must link the section of this guide they implement.
- **Keywords.** **MUST** means familiarity or correctness depends on it. **SHOULD** means a strong expectation that can be deferred with a ticket.

---

## 1. Terminology glossary

Rule: **if the industry word is generic, keep it.** Reviewers type these words into search boxes and help requests, and they use them in meet-and-confers. Renaming them only makes the product harder to learn. We avoid only (a) vendor product and feature *brand* names and (b) vendor-internal identifiers.

### 1.1 Same word (generic industry terms we keep)

| Expected term | opportuniTY UI term | Meaning in opportuniTY / notes |
|---|---|---|
| Workspace | **Workspace** | One matter's isolated container (documents, fields, searches, productions). Hard security boundary (baseline §2.3). A workspace has a *Matter name/number* attribute; the UI never says "tenant". |
| Documents (tab) | **Documents** | Home of search + document list + review. Default landing page of a workspace. |
| Document list / item list | **Document list** | The results grid. |
| View | **View** | A saved column set + sort (+ optional conditions) applied to the document list (`E16-T09`). |
| Saved Search | **Saved Search** | Named live query + conditions + view + include-family options (`E07-T09`, `E16-T11`). Runs live each time. |
| Keyword search | **Keyword search** | Text box using the opportuniTY query syntax (ADR-008). Help text names the operators (AND/OR/NOT, `"phrase"`, `W/n`, `*`, `field:value`), never a third-party engine's name. |
| Conditions / criteria | **Conditions** | Field–operator–value rows (e.g. *Responsiveness · is · Responsive*; *Custodian · is any of · …*; *Privilege Status · is not set*). Compiled to the query syntax (`field:*` is "is set"). |
| Search Terms Report (STR) | **Search Terms Report** | Per-term hits, hits with family, unique hits (`E07-T10`). |
| Field | **Field** | Admin-defined metadata or coding field. |
| Field types | **Long Text**, **Short Text**, **Whole Number**, **Decimal**, **Date**, **Yes/No**, **Single Choice**, **Multiple Choice**, **User** | Display names for ADR-003 types Text, Keyword, Integer, Decimal, Date, Boolean, SingleChoice, MultiChoice, User. API and code keep the ADR-003 names. Users think in "long text vs short text" and "yes/no". |
| Choice | **Choice** | A value of a Single/Multiple Choice field. Choices are ordered and can be deactivated (not deleted) once used. |
| Coding layout / layout | **Coding Layout** | Which fields appear in the coding pane, in what sections and order, which are required or conditional, and for which roles. |
| Coding / to code | **Coding**, **code** | We do not call it "tagging" in labels. "Tag" may appear in help text as a synonym. |
| Mass Edit | **Mass Edit** | Coding many documents at once. The API and code call it *bulk coding job* (E10-T04). The UI says Mass Edit. |
| Mass operations | **Mass Actions** (menu) | Menu over a selection: Mass Edit, Export to Load File, Export List (CSV), Apply to Family, later Add to Batch. |
| Related items | **Related Items** | Pane listing Family, Duplicates and Email Thread members of the current document. |
| Family, parent, attachment | **Family**, **Parent**, **Attachment** | Family ID, Family Sequence (parent = 0). |
| Duplicates | **Duplicates**, **Duplicate Group**, **Primary** | Q-09: labelled, never suppressed. We say "Primary", not "master". |
| Email thread | **Email Thread** | Thread ID column and Related Items tab. No thread visualisation in MVP (baseline §33). |
| Persistent highlighting | **Highlight Sets** (persistent highlighting) | Admin-defined term lists with colours that reviewers toggle in the viewer (`E16-T12`). |
| Batch, batch set | **Batch**, **Batch Set** | Assignable review units built from a saved search. Domain in M3 (`E10-T05`), UI in M5 (`E10-T06`). |
| Check out / check in (batch) | **Check out**, **Check in** | M5. |
| Control Number | **Control Number** | Immutable unique document identifier per workspace (Q-27). Always the first, pinned grid column. |
| Bates / Bates number | **Bates** | Production numbering. Fields: *Production Begin Bates*, *Production End Bates*, *Production Begin Attachment*, *Production End Attachment*. Load-file headers default to `ProdBegBates` etc. and are renameable per export. |
| BegBates/EndBates (received) | **Begin Bates / End Bates** | Typed fields for received productions (Q-27). |
| BegAttach / EndAttach | **Begin Attachment / End Attachment** | Displayed for every family member, and computed if not imported. |
| Production / production set | **Production** | A frozen specification + snapshot + Bates + output (Q-08). |
| Placeholder / slip sheet | **Placeholder** (slip sheet) | One-page image that stands in for a withheld or natively produced document and consumes one Bates number. |
| Endorsement / stamping / branding | **Endorsements** | Header/footer stamps (Bates, designation, text). We avoid the vendor-flavoured "branding". |
| Confidentiality designation | **Confidentiality Designation** | Protective-order legend (None / CONFIDENTIAL / HIGHLY CONFIDENTIAL – AEO). Security-affecting (Q-11). |
| Redaction | **Redaction**, **Redaction Set** | Rectangles on page images (Q-22). A production selects one Redaction Set. We say "Redaction Set", not "markup set". |
| Privilege log | **Privilege Log** | Q-20. |
| Load file, DAT, OPT | **Load file**, **DAT**, **OPT** | Industry file formats. Keep these exact names. |
| Import, append, overlay, append/overlay | **Import**: **Append**, **Overlay**, **Append/Overlay** | Same mode names and semantics as practitioners expect (§5.1). |
| Overlay identifier | **Overlay key** | The unique field used to match rows to existing documents (default Control Number). |
| Field mapping, settings file | **Field mapping**, **Import profile** | A saved, re-usable mapping + parser settings (§5.1). |
| Export, volume | **Export**, **Volume** | Load-file volume (`VOL001`, …). |
| Viewer modes: extracted text, image, native, production | **Extracted Text**, **Image**, **Native**, **Production** (+ **Metadata**) | §3.3. |
| Users, groups | **Users & Groups** | Group membership may come from the IdP (Q-13). |
| Workspace admin, system admin | **Workspace Admin**, **Installation Admin** | "Installation" because opportuniTY is self-hosted, one org per installation (Q-01). |
| Audit / history | **Audit**, **Document History** | Document History = coding history per document (`E10-T05`, `E14-T04`). |
| Responsive / Not Responsive / Needs Further Review | Same default choices | Seeded in the default workspace template (§3.4). |
| Hot / key document | **Key Document** (Yes/No) | Default template field. |

### 1.2 Where we deliberately differ, and why

| Expected term or behaviour | opportuniTY | Why |
|---|---|---|
| Item-level security / secured documents | **Document restrictions** (privilege, confidentiality class) and **Ethical walls** | Q-11/Q-13 model restriction classes and walls, not per-item permission lists. Restricted documents are invisible, not greyed out. |
| Group-permission matrices | **Roles** assigned to users/groups per workspace | Permission catalogue (`E05-T02`). Role names: Reviewer, Senior Reviewer, Production Manager, Workspace Admin, Auditor, Break-glass. |
| "Build / update the search index" admin step | **No manual index build.** A freshness indicator shows "Current" / "Updating" / "Delayed", and counts are prefixed "≈" while updating (Q-10). | The index updates continuously. Reviewers see "Current as of 10:42". Only admins see generation numbers. |
| Mass edit silently overwrites everything in scope | Mass Edit **skips** documents whose target field someone else changed after the job started, and reports them (Q-07) | Protects concurrent reviewer work. The result dialog shows "Skipped N (changed by others after start)" with a downloadable list. |
| Automatic family/duplicate propagation on save | **Apply to Family / Apply to Duplicates** is an explicit action with a conflict preview (Q-14, Q-09) | Defensibility: no invisible coding. See product question P-1. |
| A saved search is the only "frozen" thing | **Snapshot** ("frozen set": N documents at time T) is a visible object for mass edits, exports and productions | Baseline §10/§22. Labelled "Frozen set" in the UI with the snapshot ID in details. Never call a live saved search "frozen". |
| Last page / jump to page 5,000 | **No deep page jumps.** Reverse the sort to see the end. Counts above 10,000 are approximate with "Count exactly" (Q-32) | Cursor paging at 100M scale. See product question P-2. |
| Undo mass edit | None in MVP (Q-34). Typed confirmation above 10,000 documents or for security-affecting fields | Same as practitioners' current reality (mass edits are not undoable). The typed confirmation is new and must be explained in the dialog. |
| "Artifact ID" or other internal numeric IDs | **Not shown to users.** Control Number identifies documents; internal IDs appear only in admin diagnostics | Avoids vendor-internal vocabulary and identifier leakage. |
| Vendor-named import presets ("<vendor> default") | Vendor-neutral preset names (§5.1) | Q-47: no vendor names in the UI. See product question P-4. |
| Field Tree | **Field Browser** (left pane) | Same idea (browse documents by choice/keyword values). We use a generic name. |
| Dashboard landing | **Documents** is the landing page. Workspace Home dashboards come later | Reviewers go straight to work. |

### 1.3 Words we never use in the UI

Vendor product, module or feature brand names (including "Relativity", "RelativityOne", "Relativity Desktop Client"/"RDC", "Review Center", "aiR", "Integration Points" and other vendors' marks such as "Nuix" or "Concordance"). Exception: a file-*format* description in help text where it is the only accurate name, for example "Concordance-style DAT". The format is generic, but the trademark rule in §7 still applies. We also never use "dtSearch", "Artifact ID", "RDO/Dynamic Object", "Markup Set" or "Core Reviewer". Interop documentation outside the UI follows §7.2.

---

## 2. Information architecture & navigation

### 2.1 Levels

```
Installation (self-hosted)
├── Workspaces list            ← after login; recent + search; "Create workspace" (admins)
├── Installation Admin         ← users, IdP groups, roles catalogue, storage, jobs (installation admins only)
└── Workspace "ACME v. Widget" ← everything below is scoped to one workspace
    ├── Documents        (default landing)
    ├── Review Batches   (M5; hidden until enabled)
    ├── Searches         Saved Searches · Search Terms Reports (Q-65)
    ├── Productions
    ├── Imports
    ├── Exports
    ├── Jobs
    └── Admin ▾  Fields · Choices · Coding Layouts · Views · Highlight Sets · Redaction Sets ·
                 Users & Groups · Roles & Security · Ethical Walls · Workspace Settings · Audit
```

### 2.2 Workspace header (every workspace page)

> **Updated by Q-64 (product owner, 2026-10-04):** the workspace sections moved from header tabs to a **collapsible
> left sidebar** (deep navy, cyan active indicator per the brand guide); a section with sub-pages shows them as **tabs
> across the top of the main region** (Admin's areas replace the former Admin ▾ menu). The header keeps the mark,
> workspace switcher, freshness pill, job tray and user menu. The diagram below shows the earlier header-tab layout;
> section order, RBAC filtering and everything else in this section still apply.

```
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ [opportuniTY mark]  ACME v. Widget ▾ │ Documents  Search Terms Reports  Productions  Imports │
│                                      │ Exports  Jobs  Admin ▾          ● Current  [Jobs 2] [JD ▾] │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Workspace switcher** (name ▾): recent workspaces plus "All workspaces". Switching clears all workspace-scoped client state (`E15-T02`).
- **Section tabs** are shown only if the user's role allows them (RBAC-driven). Order is fixed as above. Reviewers typically see only *Documents* (and later *Review Batches*).
- **Freshness pill** (Q-10): Current / Updating / Delayed, in plain language.
- **Job tray**: running and finished jobs for this user, linking to the Jobs page (`E06-T07`).
- **User menu**: preferences (keyboard map, display time zone, density), cheat sheet (`?`), About (licence notice, Q-25), sign out.

There is **no separate top-level "Search" section.** Practitioners search *from the document list*: search lives in the Documents page (§3.1). `E15-T02`'s "Review, Search, Jobs, Admin" navigation is replaced by §2.1 (see ticket-review).

### 2.3 What each section contains

| Section | Contains | Primary users |
|---|---|---|
| **Documents** | Browser pane (Saved Searches · Field Browser · Imports), search panel (keyword + conditions + include family/duplicates/thread), document list with Views, Mass Actions, and Review mode (viewer + coding + related items) | Everyone |
| **Review Batches** (M5) | Batch Sets, batches, check-out/in, reviewer progress, QC sampling | Review managers, reviewers |
| **Searches** (Q-65) | Home for saved searches. Tabs across the top: *Saved Searches* (list with folders, owner, sharing, last run and hit count; create, edit conditions, duplicate, move, delete, share with users/groups, run in Documents) and *Search Terms Reports* (below). The Documents browser pane keeps quick access to run saved searches | Everyone with search; sharing needs `SavedSearch.Share` |
| ↳ **Search Terms Reports** | List of reports. New report: name, scope (saved search / frozen set / workspace), terms (paste or CSV). Results table and CSV/XLSX download. Clicking a term opens its hits in Documents | Case team, lit support |
| **Productions** | List (name, Bates range, doc/page counts, status). Production page tabs: *Settings · Documents · QC · Output · Privilege Log* | Production managers |
| **Imports** | Import history (each with report + error file) and *New Import* wizard. Import profiles | Lit support / admins (Q-31) |
| **Exports** | Export history and *New Export* wizard. Export profiles | Lit support |
| **Jobs** | All jobs in the workspace with two-phase progress ("Saved" / "Searchable"), errors, skipped lists, retry (admins) | Admins, job owners |
| **Admin** | Fields, Choices, Coding Layouts, Views (shared), Highlight Sets, Redaction Sets (reasons), Users & Groups, Roles & Security (restriction classes, Q-11), Ethical Walls (Q-13), Workspace Settings (time zone Q-28, Bates prefixes, thresholds), Audit | Workspace admins |

### 2.4 Moving between sections (must work)

| From | Action | Goes to |
|---|---|---|
| Any document row | `Enter` / double-click / Control Number link | Documents › Review mode at that document, cursor bound to the list (§3.2) |
| Review mode | "Back to list" / `Esc` (when not editing) | Same list, scroll position and selection kept |
| Saved search (browser) | Click | Runs it in the document list |
| Saved search ⋯ menu | *Search Terms Report*, *Export*, *Use in production*, *Mass Edit results* | Pre-filled wizard in the right section |
| STR term row | Click count | Documents filtered to that term's hits (and scope) |
| Production › QC exception | "Open in Documents" | Documents list of the exception documents |
| Production › Documents tab | Row | Review mode with Production viewer mode preselected |
| Import report row | "Open imported documents" | Documents filtered by `Import = <import name>` |
| Job (mass edit) | "Skipped documents" | Documents list of the skipped set, plus CSV download |
| Deep link `…/w/{ws}/documents/{controlNumber}` | Open | Review mode, or the standard "No access" page (indistinguishable from "does not exist"; Q-13) |

---

## 3. Core review screen

The Documents page has two modes on one route: **List mode** and **Review mode**. Experienced users expect the list to stay one click away while reviewing, so Review mode keeps a collapsible list strip.

### 3.1 List mode

```
┌ Browser ──────────────┬ Search panel ─────────────────────────────────────────────────────────┐
│ ▾ Saved Searches       │ Keyword  [ contract W/10 terminat*                       ] [Search]    │
│   ▾ Shared            │ Conditions  Custodian · is any of · Smith, Jones          [+ Add]       │
│     ▸ First Pass       │             Responsiveness · is not set                                 │
│     • Hot docs         │ Include  ☐ Family  ☐ Duplicates  ☐ Email thread    [Save as…] [Clear] │
│   ▾ My searches        ├────────────────────────────────────────────────────────────────────────┤
│     • Smith unreviewed │ View [ Default ▾ ]  ≈ 58,330 documents · Current as of 10:42 [Count exactly]│
│ ▸ Field Browser        │ Selected: 0   [Mass Actions ▾]                         [Columns] [⚙]  │
│   ▸ Custodian          │ ☐ │ Control No.  │ ⛓ │ Date (Family) │ From      │ Subject      │ Resp. │
│   ▸ Responsiveness     │ ☐ │ ACM0000101   │ P │ 2024-03-01    │ smith@…   │ Q1 terms     │ Resp  │
│ ▸ Imports              │ ☐ │ ACM0000102   │ └A│ 2024-03-01    │           │ terms.xlsx   │       │
│   • VOL003 2026-09-30  │ ☐ │ ACM0000107   │ D │ 2024-03-02    │ jones@…   │ RE: Q1 terms │       │
│ ▸ Frozen sets          │ … virtual rows, cursor-paged …                                         │
└────────────────────────┴────────────────────────────────────────────────────────────────────────┘
```

- **Browser pane (left, collapsible).** Saved Searches tree (folders, shared/private), Field Browser (choice and short-text fields with value counts, "≈" while updating), Imports (one node per import, so a load can be reviewed or QC'd), Frozen sets (snapshots the user created or may see). Folder browsing: see product question P-8.
- **Search panel.** Keyword box (query bar `E16-T01`) **and** a Conditions builder. Most practitioner searches are field conditions, not keywords. Operators per type: *is, is not, is any of, is none of, is set, is not set, contains (text), between / before / after (dates, numbers)*. "Include Family / Duplicates / Email thread" toggles show the delta: "1,240 hits + 3,102 family members" (`E16-T10`). *Save as…* creates a Saved Search.
- **Results header.** View selector, count (exact, or "≈"/"≥ 10,000 (approx.)" per Q-10/Q-32), freshness stamp, Selected count, Mass Actions, Columns.
- **Grid.** Checkbox column; Control Number pinned first; family/duplicate indicator column (P = parent, └A = attachment, D = has duplicates, with text alternatives); then View columns. Sort by clicking headers (up to 3 levels; `E16-T09`). Family-grouped mode keeps families contiguous with *Date (Family)* sort (`E09-T03`).
- **Paging.** Virtual scroll with cursor paging (`E16-T02`). The footer shows "Rows 1–100 of ≈58,330". No deep page jumps (Q-32, product question P-2).

### 3.2 Review mode

```
┌ ◂ Back to list │ Doc 1,204 of ≈58,330  [◂ Prev] [Next ▸]  ACM0000102 · terms.xlsx │ ● Current ┐
├ Viewer ─────────────────────────────────────────────────────┬ Coding ──────────────────────────┤
│ [Extracted Text] [Image] [Native] [Production] [Metadata]   │ Layout [ First Pass Review ▾ ]   │
│ Highlights: ☑ Search hits (37) ☑ Set: Key terms ☐ Set: Names │ ── Responsiveness * ──────────── │
│ Hit 4 of 37 "termination"  [◂ hit] [hit ▸]       Find [   ]  │ (1) ○ Responsive                 │
│ ┌─────────────────────────────────────────────────────────┐ │ (2) ○ Not Responsive             │
│ │ … the agreement may be ▓terminated▓ upon thirty days …  │ │ (3) ○ Needs Further Review       │
│ │                                                         │ │ ── Privilege 🔒 Affects access ── │
│ │                                                         │ │ Privilege Status [ Not Priv. ▾ ] │
│ │                                                         │ │ Privilege Basis  ☐ AC ☐ WP ☐ CI  │
│ │                                                         │ │ ── Issues ───────────────────── │
│ └─────────────────────────────────────────────────────────┘ │ ☐ Pricing ☐ Termination ☐ IP     │
├ Related Items ─────────────────────────────────────────────┤ Key Document  ○ Yes ○ No         │
│ [Family 3] [Duplicates 2] [Email Thread 7] [Produced in]    │ Comments [                     ] │
│ Rel.    │ Control No. │ Name/Subject  │ Date       │ Resp.  │                                  │
│ Parent  │ ACM0000101  │ Q1 terms      │ 2024-03-01 │ Resp   │ Saved · indexing ⟳                │
│ ▶ This  │ ACM0000102  │ terms.xlsx    │ 2024-03-01 │ —      │ [Apply to Family…]                │
│ Attach. │ ACM0000103  │ redline.docx  │ 2024-03-01 │ —      │ [Cancel] [Save] [Save & Next ▸]   │
└─────────────────────────────────────────────────────────────┴──────────────────────────────────┘
```

**Navigation (review cursor, `E16-T03`).**
- Prev/Next follow the list order of the set the reviewer came from (search, saved search, frozen set, related items do *not* change the cursor). "Doc n of ≈N" is always visible.
- Opening a related item shows a breadcrumb "Viewing related item ACM0000103 — [Return to ACM0000102]". Prev/Next still walk the original set.
- If the live set refreshes (PIT expiry, Q-33): show the "Results refreshed" notice. If the current document left the set, offer "Continue from next".
- At the end of the set: "Save & Next" saves and shows "End of list — return to list?". It never wraps around silently.
- **Unsaved changes.** Navigating away (Prev/Next, list, related item, mode change is fine) with unsaved edits prompts **Save / Discard / Cancel**. Default focus is Save.

**Viewer (`E16-T04`).** The mode buttons are always in this order, and only modes with an artifact are enabled. A disabled mode shows its reason in a tooltip.

| Mode | Shows | Notes |
|---|---|---|
| **Extracted Text** | Virtualised chunked text, search hits + Highlight Sets, find-in-document | Banner "Text truncated for search after 10 M characters; full text shown" (Q-29). |
| **Image** | Imported/rendered page images: thumbnails, go to page, zoom 25–400 %, fit width/page, rotate | Redaction mode lives here (M3, `E11-T04`). Highlighting is labelled "Text mode only" (`E16-T12`). |
| **Native** | Imported PDF natives rendered as pages. Other natives: file card (name, type, size, hashes) + **Download native** if the role permits (Q-18) | No in-browser native rendering in MVP (Q-36, product question P-3). |
| **Production** | Produced images (with Bates, endorsements and burned redactions) for each finalized production containing the document. A production selector appears if there are several | M3. Read-only. |
| **Metadata** | All fields with types, display time zone and raw imported value tooltip (Q-28) | Also reachable as a pop-over from Text mode. |

- **Default mode:** the user's last used mode if it is available for the document, otherwise Image → Extracted Text → Metadata. This is a per-user preference.
- **Persistent highlighting:** "Search hits" is a built-in set derived from the current search. Admin Highlight Sets can be toggled per reviewer and the toggles persist. Hits are shown with colour **and** outline/underline (`E16-T12`). Hit navigation crosses unloaded chunks and announces "Hit 4 of 37 'termination'".
- **Restricted/denied:** see §3.5.

### 3.3 Coding pane (`E16-T05`)

- **Layout selector** at the top lists the coding layouts the user's role may use. The last used layout is remembered per user per workspace. The default workspace template provides *First Pass Review* and *Privilege Review* (§3.4).
- Fields render in layout order and sections. Choice fields with ≤ 15 choices render as **radio buttons (single) / checkboxes (multiple)**, not dropdowns. Longer lists use a filterable list. Choices show their access digit "(1) … (9)".
- **Required** fields are marked `*`. Conditional fields appear when their condition holds (e.g. Privilege Basis when Privilege Status ∈ {Withhold, Redact}). Save & Next with a missing required field blocks the save and focuses the first invalid field.
- **Security-affecting** fields (privilege, confidentiality, wall membership; Q-11) carry a lock icon and "Affects access". A change that removes the user's own access asks for confirmation, then the viewer moves to the next document (`E16-T08`).
- **Explicit save.** Coding is saved only by **Save**, **Save & Next** or **Save & Previous**. There is no autosave on field change. **Cancel** reverts unsaved edits. After save: "Saved · indexing" until searchable, then "Saved" (`E10-T01`).
- **Conflicts.** On a stale version: "Changed by J. Smith at 10:42". The options are *Review differences* (side-by-side per field) or *Overwrite with mine*. There is never a silent last-write-wins.
- **Read-only** for users without coding permission. The pane shows values without inputs.
- **Apply to Family… / Apply to Duplicates…** (Q-14, Q-09; `E09-T05`): opens a dialog listing the fields to apply. The fields offered or preselected come from the layout or field configuration. The dialog previews the affected count and the conflicts ("3 members already coded differently", with values). Families of more than 1,000 documents run as a Mass Edit job (Q-07 skip rule applies). It is **never** automatic on save.
- **Document History** link: who changed what and when, including the job ID for mass edits (`E10-T05`).

### 3.4 Default workspace template (familiar out-of-the-box coding)

A new workspace is seeded with fields an experienced reviewer recognises. Admins can rename or extend them. The system privilege fields cannot be deleted (`E13-T01`).

| Field | Type | Choices |
|---|---|---|
| Responsiveness | Single Choice | Responsive · Not Responsive · Needs Further Review |
| Privilege Status (system) | Single Choice, security-affecting | Not Privileged · Withhold · Redact · Needs 2L Review |
| Privilege Basis (system) | Multiple Choice | Attorney-Client · Work Product · Common Interest · Other |
| Privilege Description (system) | Long Text | — |
| Confidentiality Designation | Single Choice, security-affecting | None · CONFIDENTIAL · HIGHLY CONFIDENTIAL – AEO |
| Issues | Multiple Choice | (empty; admin fills) |
| Key Document | Yes/No | — |
| Reviewer Comments | Long Text | — |

The *First Pass Review* layout contains Responsiveness, Privilege Status/Basis, Confidentiality, Issues, Key Document and Comments. The *Privilege Review* layout contains all privilege fields plus Confidentiality.

### 3.5 Restricted, walled and stale documents (Q-11, Q-12, Q-13)

| Situation | What the user sees |
|---|---|
| Search hit the user may not see (any reason) | **Nothing.** The API drops it before the page is returned (Q-12). Counts and facets may be approximate ("≈") during projection lag (Q-10). |
| Walled document (Q-13) | Nothing anywhere: no row, no family/duplicate member, no count, no Bates gap explanation. |
| Restricted-class document (Q-11) | Same as walled: invisible to unauthorised users everywhere. |
| Row loaded earlier, access revoked during the session | Row becomes "No longer available" with **no** metadata, snippet or artifact request. Opening it shows the standard no-access state (`E16-T08`). |
| Deep link to an inaccessible or non-existent document | One "No access or not found" page (no distinction). |
| Related Items family member the user may not see | **Omitted.** The family tab count reflects only visible members. Product question P-5 covers the family-integrity trade-off. |

### 3.6 Mass selection and Mass Actions (`E16-T06`)

- The checkbox header offers **This page**, **All ≈N results**, and **None**. After "all on page", a banner offers "Select all ≈58,330 results". "All results" is sent as query + generation, never as an ID list.
- **Mass Actions ▾**: *Mass Edit…*, *Apply to Family…*, *Export to Load File…*, *Export List (CSV)…* (role-gated, Q-18), later *Add to Batch Set…*.
- **Mass Edit dialog** (familiar shape):
  1. Choose a coding layout. Each field has a **"Change"** checkbox, and unchecked fields are untouched.
  2. Single-value fields: *Set to …* or *Clear*. Multiple Choice fields: each choice is tri-state **Add / Remove / Leave unchanged**, plus an option **Replace all values with …**.
  3. Include: none / family / duplicates / email thread, with delta counts.
  4. **Confirm**: "Frozen at 10:42 — 58,330 documents (+3,102 family)". The dialog explains how this differs from the grid count and states the Q-07 rule: *"Documents whose changed fields are edited by someone else after this job starts will be skipped and listed."* Typed confirmation is required above 10,000 documents or for security-affecting fields (Q-34).
  5. Submit returns immediately. The job tray tracks progress as *Saved n/N → Searchable n/N*. The completion toast reads "Updated 58,201 · Skipped 129 · [Download skipped list] [Show skipped in list]".

### 3.7 Freshness and counts (Q-10)

- Reviewers see "Current", "Updating — counts may not include recent changes", or "Delayed". Count labels get "≈" whenever the served generation is behind.
- Admin and support roles can open the detail popover with generations. Reviewers never see generation numbers.
- Production and privilege-review runs offer "Wait until index is current" before running.

---

## 4. Keyboard shortcuts (default map)

Our own map. It follows the spirit of reviewer conventions (keyboard-only code → save → next) and is designed to avoid browser, OS and screen-reader conflicts. All bindings are rebindable per user (`E15-T03`). The `?` cheat sheet shows active bindings for the focused region.

**Design rules.**
1. **`Alt+Shift`** (macOS **`⌥⇧`**) is the opportuniTY command chord. It is free in the supported browsers and avoids `Alt+←/→` (history), `Alt+letter` (Firefox menus), `Alt/Ctrl+digit` (tab switching), `Ctrl+Alt+arrows` (display rotation on some Windows drivers) and AltGr combinations.
2. **Ctrl/⌘+Enter** and **Ctrl/⌘+S** are the save family, because they work from inside text inputs.
3. **Single-key shortcuts** (no modifier) are active only when focus is *not* in a text input. Users can disable them all with one preference (WCAG 2.1.4). They are off by default when the user enables "screen-reader friendly shortcuts".
4. Bindings use `KeyboardEvent.code` so that layouts with dead keys and macOS Option characters still work.

| Group | Command | Default (Win/Linux) | macOS | Single-key alt. |
|---|---|---|---|---|
| Review flow | **Save & Next** | `Ctrl+Enter` | `⌘↩` | — |
| | Save & Previous | `Ctrl+Shift+Enter` | `⌘⇧↩` | — |
| | Save (stay) | `Ctrl+S` | `⌘S` | — |
| | Cancel unsaved edits | `Alt+Shift+Z` | `⌥⇧Z` | — |
| | Next document (prompts if unsaved) | `Alt+Shift+.` ("Alt+>") | `⌥⇧.` | `]` |
| | Previous document | `Alt+Shift+,` ("Alt+<") | `⌥⇧,` | `[` |
| | Back to list | `Alt+Shift+L` | `⌥⇧L` | `Esc` (when not editing) |
| | Open focused list row in viewer | `Enter` | `↩` | — |
| Viewer | Extracted Text / Image / Native / Production / Metadata | `Alt+Shift+1` … `5` | `⌥⇧1` … `5` | — |
| | Next / previous hit | `F3` / `Shift+F3` | `⌘G` / `⌘⇧G` | `n` / `Shift+N` |
| | Toggle all highlighting | `Alt+Shift+H` | `⌥⇧H` | — |
| | Find in document | `Ctrl+F` (inside viewer) | `⌘F` | — |
| | Next / previous page (Image/Production) | `PageDown` / `PageUp` | same | — |
| | Zoom in / out / fit | `Ctrl+=` / `Ctrl+-` / `Ctrl+0` (inside viewer) | `⌘=` … | `+` / `-` / `0` |
| | Rotate page | `Alt+Shift+R` | `⌥⇧R` | — |
| Focus regions | Cycle regions forward / back (list → viewer → coding → related) | `Alt+Shift+G` / `Alt+Shift+B` | `⌥⇧G` / `⌥⇧B` | — |
| | Focus coding pane (first field) | `Alt+Shift+C` | `⌥⇧C` | — |
| | Jump to coding field n (layout order) | `Alt+Shift+C`, then `1`–`9` | same | — |
| | Toggle choice n in focused choice field | `1`–`9` | same | — |
| | Focus keyword search | `Alt+Shift+K` | `⌥⇧K` | `/` |
| | Focus Related Items | `Alt+Shift+I` | `⌥⇧I` | — |
| Selection | Toggle row checkbox | `Space` (list focused) | same | — |
| | Extend selection | `Shift+↑/↓` | same | — |
| | Select all on page | `Ctrl+A` (list focused) | `⌘A` | — |
| | Select all results | `Alt+Shift+A` | `⌥⇧A` | — |
| | Clear selection | `Alt+Shift+0` | `⌥⇧0` | — |
| Actions | Mass Edit selected | `Alt+Shift+E` | `⌥⇧E` | — |
| | Apply to Family… | `Alt+Shift+F` | `⌥⇧F` | — |
| | Shortcut cheat sheet | `Alt+Shift+/` | `⌥⇧/` | `?` |

This table is implemented as the command registry's default map (`src/Opportunity.Web/src/app/core/commands/command-catalog.ts`). Rebinding, the single-key switch and the `?` cheat sheet are reached from the user menu ("Keyboard shortcuts…"), and the key map is saved to the user profile (`/api/v1/me/preferences`). The conflict matrix in docs/accessibility/wcag-2.2-aa-checklist.md marks `Alt+Shift+B`, `Alt+Shift+I` and `Alt+Shift+A` as "page first" overlaps with Chromium browser-UI keys, still to be confirmed in the M1 manual pass.

ADR-018 / `E15-T04` must run a conflict matrix (Chrome, Edge, Firefox, Safari × Windows, macOS, Linux × NVDA, JAWS, VoiceOver) and may change a default if a conflict is found. If a default changes, update this table in the same PR.

---

## 5. Workflows end to end

Each step lists what an experienced user expects to find. "→" means the next screen.

### 5.1 Load-file import (`E08-T01…T09`, Q-26, Q-27, Q-31)

**Imports → New Import**. This is a wizard with a step rail. Every step can go back without losing input.

| # | Step | What the user expects |
|---|---|---|
| 1 | **Source & mode** | Pick a **staging location**: a server-side import share or object-storage prefix, browsable as folders. This is the primary path, because volumes are many GB. Browser upload is for small loads only. Pick the DAT (or CSV), optional OPT, the volume root for relative paths, and the **Import mode: Append / Overlay / Append/Overlay**. Optionally choose an **Import profile** to pre-fill everything. Name the import (default `<DAT file name> <date>`). It becomes a field value used to find this load later. |
| 2 | **File format** | Detected encoding (DAT and text files separately) and delimiters. Each delimiter is shown as glyph **and** code, e.g. `¶ (182)`, `þ (254)`, `® (174)`, `; (59)`, `\ (92)`, `DC4 (20)`, because practitioners specify delimiters by their decimal ASCII codes. Presets use vendor-neutral names: **"Concordance-style (DC4 / þ / ®)"**, **"Pilcrow / thorn (¶ / þ / ®)"**, **"CSV (RFC 4180)"**, **"Custom"**. A **20-row preview** shows parsed columns, where mojibake (`Ã¾`) is obvious. A "First line contains field names" toggle (on). Options: newline-in-value `®` → line break (on). |
| 3 | **Field mapping** | Two-column mapping grid: **Load-file column · sample values · → · Workspace field · type · status**. Buttons: **Auto-map** (exact and alias names; each auto-mapped row shows "matched by alias `BEGDOC`"), **Clear**, **Save as Import profile**. Each row can map to an existing field, **Create new field…** (name, type; for choice fields an option to create choices from distinct values, previewing "will create 12 choices") or **Do not import**. Unmapped columns are listed explicitly. Coding and privilege fields are greyed out unless "Allow coding-field overlay" is ticked (admin, audited; Q-31). Per-column parsing: date format (incl. ISO 8601), merge with a time column, source time zone, multi-value delimiter, Yes/No values. The preview shows coerced values and per-column error counts. **Structural targets** are listed first: Control Number, Begin/End Bates, Begin/End Attachment, Parent ID, Family/Group ID, Native path, Text path, Folder path (P-8), hashes, duplicate group, email thread fields. |
| 4 | **Overlay settings** (Overlay, Append/Overlay) | **Overlay key** dropdown, listing only unique fields (default Control Number). "Blank values in the load file: *leave existing values* (default) / *clear existing values*". Multi-value: *Merge* / *Replace*. Text path: replace text and re-index. Native path: replace native (prior kept). Images: *Keep existing* / *Replace pages from OPT*. |
| 5 | **Files: natives, text, images** | Path rebasing (strip prefix `D:\Processing\`, with a live example), path separator auto-detect, "text in DAT column" option, OPT match key (Control Number / Begin Bates), missing-file behaviour (*flag and continue* default / *fail row*), text size cap note (Q-29). |
| 6 | **Validate** (pre-flight, no writes) | Summary counts: rows; **will create N / will update M / not found K / already exists J**; field-count errors; date errors; duplicate control numbers in file; missing natives/text/images (100 % or sample); OPT page-count mismatches; orphan OPT rows; family problems. **Errors** block. **Warnings** need a tick to acknowledge. Every number links to a downloadable CSV. "Stop after N errors" setting (default: continue). |
| 7 | **Run** | Starts a job and goes to the import detail page: two progress lines, **Saved** (PostgreSQL) and **Searchable**. The user can leave, and the job tray tracks it. |
| 8 | **Report** | Rows read / imported / overlaid / skipped / errored; natives, text, images linked/missing/truncated/hash-mismatch; families built / orphans; quarantined (`E08-T09`); elapsed time. Downloads: **Import report (CSV)**, **Error file** (`<dat-name>_errors.dat` in the *same* delimiters and encoding, original header + failing rows + trailing `ImportError` column), **OPT error file** (`<opt-name>_errors.opt`) when applicable, **Error detail (CSV)** (row, control number, column, error code, message). Actions: **Open imported documents**, **Re-import corrected error file** (pre-fills the same profile and mode), **Save settings as Import profile**. |

**Import profile** = delimiter profile + encodings + field mapping + parsing options + mode + overlay settings + path settings. It is saved per workspace, can be copied to another workspace, and can be exported/imported as a JSON file so lit-support teams can share it. Re-applying a profile to a file with different headers lists missing and new columns explicitly. It never drops them silently.

### 5.2 Searching (`E16-T01`, `E16-T11`, `E07-T09`, `E07-T10`)

1. **Documents** → type a keyword and/or add Conditions → *Search*. Errors are shown inline at the offending token, and the search does not run.
2. Use the Include toggles to add family, duplicates or email thread, with delta counts.
3. **Save as…** → name, folder, *Private / Shared*, View to use. The saved search appears in the browser.
4. Saved search ⋯ menu: Run · Edit · Copy · Move · Delete · Search Terms Report · Mass Edit results · Export · Use in production. Users who may not see some results get fewer results; the search itself is the same (permissions are the runner's, `E07-T09`).
5. **Searches → Search Terms Reports → New report**: name, scope (saved search / frozen set / whole workspace), paste terms one per line or upload CSV `Name,Expression` → run (a materialized snapshot is created). Results table columns: Term · Documents with hits · With family · Unique hits · Unique with family. Totals row: documents with ≥1 hit / with family / no hits. Invalid terms show a per-term error. Downloads: CSV, XLSX. The report header states scope, snapshot ID, "Current as of …", and warns if the index was not current at run time. Clicking a count opens Documents with those hits.

### 5.3 Review (ad-hoc now; batches in M5)

**Now (M1–M3):** the reviewer opens a shared saved search (e.g. "Smith — not yet reviewed" = *Custodian is Smith AND Responsiveness is not set*) → `Enter` on the first row → Review mode → code → **Save & Next** (`Ctrl+Enter`) through the set. Coded documents stay in the cursor order until the set refreshes. They do not vanish under the reviewer. On refresh, "Results refreshed — continue from next" appears (Q-33).

**Later (M5, `E10-T06`; domain in `E10-T05`):** Review Batches → Batch Set (source saved search, batch size, prefix → batches `FirstPass_0001…`, keep families together, optional keep threads together, reviewer group). Batch status: *Available → Checked out (user) → Completed*. A reviewer checks out the next available batch, which opens in Review mode with its cursor bound to the batch's frozen membership. Check in when done.

### 5.4 Mass coding (`E16-T06`, `E10-T04`, Q-07, Q-34)

Select → **Mass Actions → Mass Edit…** → layout and fields (§3.6) → Include options → **Confirm frozen target** → job. On completion: *Updated · Skipped (Q-07) · Failed*. *Show skipped in list* opens a Documents list. *Download skipped list* gives a CSV of Control Number, field, changed by, changed at. Partially failed chunks give "N failed — Retry failed / View errors" (admin).

### 5.5 Export (load-file volume, `E12-T01`, `E12-T08`)

**Exports → New Export** (or Mass Actions → Export to Load File):

| # | Step | Expectation |
|---|---|---|
| 1 | Source | Saved search, current selection, frozen set or production → a **frozen set is created** and its ID and count are shown. Include family/duplicates. |
| 2 | Volume & folders | Volume prefix `VOL`, start `1`, padding `3` → `VOL001`. Max volume size (optional). Subfolders `IMAGES\IMG0001`, `NATIVES\NATIVE0001`, `TEXT\TEXT0001`, `DATA` with max files per folder (default 1,000). Every prefix, start and padding is editable, and a live example path is shown. |
| 3 | Files | Natives (named `<Control Number>.<ext>`), images (as imported; one file per page named by page key), text (per document, encoding UTF-8 default / UTF-16LE option, or in DAT). Path separator in load files: **`\` (default)** / `/`. Relative paths (`.\VOL001\…` or `VOL001\…`). Line endings CRLF. |
| 4 | Load files | DAT: ordered **field list** (add/remove/reorder; rename the header per field, e.g. `ControlNumber` → `BEGDOC`), delimiters (same presets as import), encoding (UTF-8 BOM default, UTF-16LE option), date format and time zone. OPT (one row per page, `Y` + page count on the first page, volume column = volume name). Save as **Export profile**. |
| 5 | Review & run | Summary: documents, families, files, estimated size. Run → job. |
| 6 | Result | Volume download/location, **manifest (CSV with SHA-256)**, **exclusion report** (documents removed by the access re-check and why; Q-15), job log. |

### 5.6 Production (`E12-T02…T08`, `E13-*`, Q-08, Q-14, Q-19…Q-22)

**Productions → New Production**. The production page has tabs *Settings · Documents · QC · Output · Privilege Log*. Status: **Draft → Run (Bates assigned, output generated) → QC passed → Finalized** (locked) → delivered volume downloads. Bates lock point: see product question P-7.

| Area | Expectations |
|---|---|
| Source | Saved search or frozen set. Family handling: *Include families* (default on), with the delta shown. Privileged handling: *Withhold → placeholder "Withheld – Privileged"* (default), *Redact → produce images with burned redactions*. A "Wait until index is current" option (Q-10). |
| Numbering | Prefix, start (defaults to **next available number** for that prefix), padding (default 7 → `ACME0000001`), suffix, **Page-level** (default) / Document-level numbering. Natives get a page suffix policy (`ACME0000001.0001`) or a single number with a slip sheet. A **Bates preview** shows the first and last number and the total range before run. An overlap with a prior production on the same prefix is blocked. |
| Images & natives | TIFF G4 300 DPI (B&W) / JPG for colour by file type. Native production rules by type (spreadsheets, audio, video) with slip sheet "Document Produced in Native Format". Redacted documents are always imaged, never native (Q-22). Text from the redacted output only. |
| Endorsements | Six slots: **header left/centre/right, footer left/centre/right**. Each slot is *Bates number*, *Confidentiality Designation*, *free text* or *field value*. Defaults: **footer right = Bates, footer left = designation**. Font size and an "expand canvas so stamps never cover content" option. Preview on a sample page. |
| Placeholders | Text and optional field values (Bates, designation, reason). Types: Withheld – Privileged, Technical Issue, Produced in Native Format. Each consumes one Bates number. |
| Redactions | Choose the **Redaction Set**. Box style: black or labelled (reason text printed in the box). |
| Load files | Same DAT/OPT builder as export, with the default production field set (`E12-T05`) and header renaming. Production volume naming as export (`ACME_VOL001`). |
| QC tab | Each check shows pass/fail/warn with document-level exceptions and "Open in Documents". Blocking checks follow `E12-T07` and `E13-T02` (privilege conflicts, family inconsistencies per Q-14). An override needs a reason and is audited. **QC report** (PDF/CSV). |
| Output tab | Volume downloads, **manifest with checksums**, QC report, **Bates cross-reference**, **"Re-run with same specification"** (reproduces byte-identically, Q-08), *Copy settings to new production*. |
| Privilege Log tab | Generated from the production snapshot (Q-20): one row per withheld/redacted-for-privilege document, metadata columns + basis + description. Reviewers override the description through the *Privilege Description* field. CSV/XLSX. A versioned SHA-256 is shown. |
| Viewer | The finalized production's images appear in **Production** viewer mode. A **Produced in** tab in Related Items lists every production and Bates range for the document, its duplicates and its family (`E13-T04`). |

---

## 6. Interaction conventions (apply everywhere)

- **Counts** use thousands separators and "≈" when approximate. Never show a bare "10000" for a capped count.
- **Dates** use the workspace display time zone with a zone indicator and a per-user override (Q-28). The raw imported string is in a tooltip.
- **Destructive or broad actions** show the frozen target count and require a confirm. Typed confirmation per Q-34.
- **Every long operation is a job.** It returns immediately, shows two-phase progress, and is listed in the tray and on the Jobs page.
- **Every downloadable report** is CSV (plus XLSX where the guide says so). The file name includes the workspace, object name and timestamp, e.g. `ACME_STR_Key-terms_2026-10-03T1042.csv`. Platform-produced CSV/DAT neutralise formula-leading cells (`E08-T09`).
- **Empty, loading, error and no-access states** are designed for every panel. A blank pane is a bug.
- **Density:** compact is the default for Documents and Review mode (≥ 30 rows at 1080p, `E15-T01`). Comfortable is a preference.

---

## 7. Do-not-copy guardrails and legal note

### 7.1 Do not copy (applies to code, assets, docs, issues and PRs)

| Do not | Instead |
|---|---|
| Use the Relativity name or logo, or any other vendor's marks, in the product UI, including import presets, field labels, help tooltips, error messages, sample data and screenshots in the product | Vendor-neutral names (§1.3, §5.1) |
| Copy or trace logos, icons, illustrations, splash or login screens, favicons, fonts or loading animations | An OSI/permissive-licensed icon set, recorded in the licence check, and our own mark |
| Reproduce a vendor's colour palette, header/navigation styling, typography pairing, spacing, or other distinctive *look and feel* (trade dress), or any screen "pixel-for-pixel" | Our design tokens (`E15-T01`). Layout follows *function* (list / viewer / coding) with our own visual design |
| Copy text from vendor documentation, help pages, training material, UI strings, error messages, report templates or default slip-sheet wording | Write our own text. Generic legal phrases ("Withheld – Privileged", "Document Produced in Native Format") are industry-standard and allowed |
| Copy a vendor's complete default keyboard map, default workspace template, default field/choice catalogue or sample data verbatim | Our own defaults (§3.4, §4). Generic choices like "Responsive / Not Responsive" are fine |
| Commit screenshots, recordings or exports of proprietary products into the repo, issues or PRs, or use them as design references to trace | Describe the *expected behaviour* in words. Reference this guide |
| Reverse-engineer, decompile or inspect proprietary software or APIs to reproduce behaviour, or use a contributor's licensed access for that purpose | Clean-room: build from practitioner knowledge of *workflows* and public, generic file-format knowledge (DAT/OPT conventions) |
| Claim compatibility, certification, endorsement or partnership with a vendor in the UI | Documentation may describe *format* compatibility factually (§7.2) |

### 7.2 Short legal note (not legal advice)

- Workflow conventions, generic industry terms (workspace, saved search, coding layout, Bates, overlay, privilege log) and functional layouts (list + viewer + coding pane) are common across the market. Generally they are not protectable as trademarks or copyright. What *is* protected: **trademarks** (names, logos), **copyright** (code, documentation text, icons, graphics, UI text) and **trade dress** (a distinctive, non-functional overall look). Q-47 draws the line exactly there.
- Interop documentation *outside* the product UI may name another product only nominatively and only as far as needed to describe compatibility (e.g. "imports Concordance-style DAT files such as those produced by common processing tools"), with a trademark attribution. Whether docs may name Relativity at all is product question P-4.
- Contributors who hold vendor licences remain bound by those licence terms. Nothing in this project asks them to breach those terms.
- Before 1.0, counsel should review the UI, About page and documentation for trademark use and trade dress (tracked with the Q-25 licence notice work).
