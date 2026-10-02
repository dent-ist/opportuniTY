# E08 — Load-File Import (DAT/OPT/TXT/Natives)

**Labels:** `epic`, `role:ediscovery`, `role:backend`, `role:security`, `role:ui`, `P0`  
**Starts in:** M1 - First Vertical Slice  
**Tickets:** 9

## Goal
Deliver a vendor-grade importer that ingests real processed volumes (Relativity, Nuix, Concordance-style) without hand-massaging: delimiter/encoding profiles, typed mapping, chunked idempotent import that creates IndexChunkTasks, OPT page linking, overlay modes, validation reports, error files and ingest hardening.

## Baseline sections
§1, §5, §6, §11, §12, §20, §21, §24, §32

## Scope / out of scope
**In scope**
- Streaming DAT parser with delimiter presets and encoding detection
- Field mapping, dates/time zones, multi-value, templates
- Chunked import with one IndexChunkTask per chunk
- Native/text linking, text cap, OPT page reconciliation
- Pre-flight validation, reports, re-loadable error file
- Append/Overlay modes
- Import wizard UI
- Malware scanning, type verification, quarantine

**Out of scope**
- Native processing/extraction (deferred §1)
- LFP (E12-T09, M5)

## Contributing roles
- **Roles:** eDiscovery Practitioner, Backend, Security & Compliance, UI/UX
- **Source reviews:** eDiscovery Practitioner, Backend/Architecture, Security & Compliance, UI/UX, QA & Performance
- **Milestones spanned:** M1 - First Vertical Slice, M3 - MVP Feature Complete

## Exit criteria
- [ ] A 1M-row DAT imports with constant memory and exactly one IndexChunkTask per committed chunk
- [ ] Identical content in UTF-8, UTF-8-BOM, UTF-16LE-BOM and Windows-1252 imports to byte-identical values
- [ ] A failed-rows error file can be corrected and re-imported successfully

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E08-T01](#e08-t01) | Build streaming DAT parser with delimiter profiles and encoding detection | M1 | L | E01-T01 |
| [E08-T02](#e08-t02) | Implement field mapping, typed parsing, dates/time zones and mapping templates | M1 | M | E08-T01, E04-T03 |
| [E08-T03](#e08-t03) | Orchestrate import jobs with chunk-level index tasks | M1 | L | E08-T02, E06-T02, E06-T03 |
| [E08-T04](#e08-t04) | Link natives and extracted text into object storage | M1 | M | E08-T03, E19-T01 |
| [E08-T05](#e08-t05) | Load OPT image cross-references and reconcile pages | M1 | M | E08-T03, E04-T02 |
| [E08-T06](#e08-t06) | Add pre-flight validation, import report and re-loadable error file | M3 | M | E08-T03, E08-T05 |
| [E08-T07](#e08-t07) | Support overlay and append/overlay import modes | M3 | L | E08-T03, E09-T01, E05-T06 |
| [E08-T08](#e08-t08) | Build import wizard UI | M3 | L | E08-T06, E15-T02, E06-T07 |
| [E08-T09](#e08-t09) | Harden ingest with malware scanning, type verification and quarantine | M3 | M | E08-T04, E05-T04 |

---

### E08-T01

**Build streaming DAT parser with delimiter profiles and encoding detection**  
Labels: `role:ediscovery`, `role:backend`, `role:security`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§12. eDiscovery: Concordance default is column ASCII 020 (U+0014), qualifier `þ` (U+00FE), newline `®` (U+00AE), multi-value `;`, nested `\`; vendors often ship `¶` (U+00B6); encodings vary (UTF-8 ±BOM, UTF-16LE BOM, Windows-1252) and mis-detection shifts every column. Security: parser hardening (giant lines, encoding attacks).

#### Description
Streaming parser for Concordance DAT and CSV with a delimiter profile (column, quote, newline-in-value, multi-value, nested-value). Presets: 'Concordance default' (U+0014/U+00FE/U+00AE/`;`/`\`), 'Common vendor' (U+00B6/U+00FE/U+00AE), 'CSV' (RFC 4180). BOM sniffing (EF BB BF, FF FE, FE FF), BOM-less UTF-8 with Windows-1252 fallback on invalid sequences, explicit override, separately for DAT and TXT. Preview endpoint returning the first 20 parsed rows. Configurable limits on line length and field count.

#### Acceptance criteria
- [ ] Golden-file tests per preset incl. empty fields, qualifier at value start/end, CRLF vs LF, embedded separators in qualified values
- [ ] `®` converts to `\n` (option to preserve literal `®`)
- [ ] A row whose field count ≠ header count is rejected to the error stream with row number and observed vs expected count; duplicate headers are a pre-flight error
- [ ] A 5 GB / 1M-row DAT parses with constant memory (< 200 MB working set)
- [ ] Identical content in UTF-8, UTF-8-BOM, UTF-16LE-BOM and 1252 parses to byte-identical values; a mis-detected `þ` (`Ã¾`) is visibly obvious in preview
- [ ] Over-limit lines/fields fail safely with a reported error

#### Dependencies
- `E01-T01` — Scaffold repository layout, .NET solution and Angular workspace

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** Backend, Security & Compliance
- **Source reviews:** eDiscovery Practitioner, Backend/Architecture, Security & Compliance, QA & Performance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

#### Notes
Merges eDiscovery 'DAT parser' + 'Encoding detection and preview', backend parser, SEC-15 parser limits. Q-26.

---

### E08-T02

**Implement field mapping, typed parsing, dates/time zones and mapping templates**  
Labels: `role:ediscovery`, `role:backend`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§6, §12. eDiscovery: dates arrive as `MM/DD/YYYY`, `DD/MM/YYYY`, `YYYYMMDD`, separate `DateSent`+`TimeSent`, with/without offsets.

#### Description
Map each column to a structural column, an existing FieldDefinition, a new field (type from §6) or ignore. Auto-map by exact and alias names (`BEGDOC`, `BegBates`, `Control Number` → ControlNumber; `CUSTODIAN`; `DATESENT`/`Date Sent`). Per-import date format (incl. ISO 8601 with offset), companion time column merge (`HH:mm:ss`/`hh:mm tt`), source IANA time zone; store UTC + raw string. Multi-value split on the profile delimiter, trimmed, de-duplicated. Booleans Y/N, Yes/No, True/False, 1/0. Coercion preview on first N rows with per-column error counts. Mapping templates per workspace.

#### Acceptance criteria
- [ ] `00/00/0000` and blank dates store null without error
- [ ] `AllCustodians` 'A; B; A' stores `[A, B]`
- [ ] Mapping preview shows coerced values and per-column error counts; unmapped columns are explicitly ignored or stored as metadata
- [ ] Templates can be saved and re-applied to a later load

#### Dependencies
- `E08-T01` — Build streaming DAT parser with delimiter profiles and encoding detection
- `E04-T03` — Implement field definitions, choices and coding layouts

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** Backend
- **Source reviews:** eDiscovery Practitioner, Backend/Architecture, UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

#### Notes
Q-28.

---

### E08-T03

**Orchestrate import jobs with chunk-level index tasks**  
Labels: `role:backend`, `role:ediscovery`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§12, §21: import creates one IndexChunkTask atomically with each chunk's PG commit, never per-document outbox rows.

#### Description
Import Job split into JobChunks by row range. Each chunk transaction inserts Documents + metadata, sets DocumentVersion, records ImportBatch membership and inserts one IndexChunkTask(kind=Import). Append mode (existing key → row error). Idempotent by ControlNumber + ImportBatchId. Summary counters for the job.

#### Acceptance criteria
- [ ] Each committed import chunk has exactly one IndexChunkTask; no SearchOutbox rows are created by import
- [ ] Re-running a chunk after a crash produces no duplicate documents
- [ ] A 1M-document import completes end-to-end (PG + index) and throughput is recorded
- [ ] A generator-produced 10K volume (`E17-T02`) imports with 0 errors

#### Dependencies
- `E08-T02` — Implement field mapping, typed parsing, dates/time zones and mapping templates
- `E06-T02` — Implement Job and JobChunk state machine with leases
- `E06-T03` — Create SearchOutbox and IndexChunkTask tables

#### Roles
- **Owner:** Backend
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner, QA & Performance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

---

### E08-T04

**Link natives and extracted text into object storage**  
Labels: `role:ediscovery`, `role:backend`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§12; eDiscovery: path conventions, missing files, hash mismatches, text > 100 MB; security: path traversal, hash verification.

#### Description
Resolve `NativeLink`/`TextLink` relative to a volume root (`\` or `/`, leading `.\`, absolute paths rebased by stripping a configurable prefix); optional text-in-DAT-column mode; stream to object storage with content-hash addressing; compute SHA-256 (+MD5); compare with DAT-provided hashes; apply the indexed-text cap with `TextTruncated=true` while keeping full text in storage; flag `NativeMissing`/`TextMissing`.

#### Acceptance criteria
- [ ] Paths containing `..`, absolute/UNC paths or symlinks outside the import root are rejected (fuzz tests)
- [ ] Missing files flag the row (configurable to fail) without failing the chunk
- [ ] DAT hash mismatch produces a warning in the import report
- [ ] Text above the cap is indexed up to the cap, full text stored, and `TextTruncated` is searchable
- [ ] Retrying a chunk uploads nothing new (idempotent object keys)

#### Dependencies
- `E08-T03` — Orchestrate import jobs with chunk-level index tasks
- `E19-T01` — Implement object storage abstraction and provider contract suite

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** Backend
- **Source reviews:** eDiscovery Practitioner, Backend/Architecture, Security & Compliance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

#### Notes
Q-29.

---

### E08-T05

**Load OPT image cross-references and reconcile pages**  
Labels: `role:ediscovery`, `role:backend`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§12, §32 (DAT/OPT). eDiscovery: OPT rows are `ImageKey,Volume,Path,DocBreak,FolderBreak,BoxBreak,PageCount`; page count only on the break row.

#### Description
Parse OPT, match documents by ImageKey = ControlNumber (or configurable BegBates field), create Page rows (ordinal, image key, object ref, width/height/DPI), support single-page TIFF G4/JPG/PNG and multi-page TIFF, reconcile PageCount with rows until next `Y`, report orphans and missing images.

#### Acceptance criteria
- [ ] `Y` in column 4 starts a document; PageCount mismatch is reported with both counts
- [ ] Missing image files are reported per page and set `ImagesIncomplete=true`
- [ ] OPT ImageKeys not found are reported as orphan rows
- [ ] OPT image paths with traversal sequences are rejected

#### Dependencies
- `E08-T03` — Orchestrate import jobs with chunk-level index tasks
- `E04-T02` — Create workspace, document and page core schema

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** Backend
- **Source reviews:** eDiscovery Practitioner, Security & Compliance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E08-T06

**Add pre-flight validation, import report and re-loadable error file**  
Labels: `role:ediscovery`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§11, §12. eDiscovery: JobChunk errors are not a user-facing report; practitioners expect a fixable error file in the same format.

#### Description
Pre-flight (no writes): header/mapping check, row field counts, required fields, date parse failures, duplicate control numbers within file, collisions for Append, path existence (100% or sampled). Summary report (CSV + UI): rows read/imported/overlaid/skipped/errored; natives/text linked/missing/truncated; images/pages linked/missing; families built/orphans; elapsed. Error file in the **same delimiter profile and encoding** with original failing rows + trailing `ImportError` column. 'Stop after N errors' option.

#### Acceptance criteria
- [ ] Pre-flight performs no writes and lists error counts with downloadable detail
- [ ] Correcting and re-importing the error file succeeds
- [ ] The report is retained with the job and audited

#### Dependencies
- `E08-T03` — Orchestrate import jobs with chunk-level index tasks
- `E08-T05` — Load OPT image cross-references and reconcile pages

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** —
- **Source reviews:** eDiscovery Practitioner, UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E08-T07

**Support overlay and append/overlay import modes**  
Labels: `role:ediscovery`, `role:backend`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§12, §21, §24. eDiscovery: every real matter re-loads; overlay must flow through chunk indexing and security-affecting overlays through §24. Backend: overlay concurrent with coding must lose neither change.

#### Description
Modes Append / Overlay / Append-Overlay with a selectable unique overlay key (default ControlNumber); 'blank values overwrite' option (default off); multi-value Replace vs Merge; TextLink overlay replaces text and re-indexes; NativeLink overlay replaces native (prior object retained); overlay never touches coding fields unless explicitly permitted; family re-resolution when FamilyId/ParentID/BegAttach change.

#### Acceptance criteria
- [ ] Append: existing key → row error; Overlay: missing key → row error
- [ ] Each overlaid document increments DocumentVersion and is reindexed via IndexChunkTask; an audit event records old/new values
- [ ] Overlaying a privilege/confidentiality field enforces the restriction before index completion (§24 test)
- [ ] An overlay concurrent with interactive coding loses neither change
- [ ] Overlay supplying missing family members re-resolves families

#### Dependencies
- `E08-T03` — Orchestrate import jobs with chunk-level index tasks
- `E09-T01` — Reconstruct families from BegAttach/EndAttach, ParentID and GroupIdentifier
- `E05-T06` — Implement document/field-level security, security-affecting fields and ethical walls

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** Backend
- **Source reviews:** eDiscovery Practitioner, Backend/Architecture, Legal/Discovery Counsel
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
Q-31.

---

### E08-T08

**Build import wizard UI**  
Labels: `role:ui`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§12; UI ticket 'Import wizard with DAT/OPT field mapping'.

#### Description
Wizard: select/upload load files and folders (or an object-storage prefix) → detect delimiters/encoding with 20-row preview → map columns (type inference) → map OPT/text/native paths → validation pass → run as a job. Mapping templates.

#### Acceptance criteria
- [ ] Detected delimiter, quote and encoding are shown and overridable
- [ ] Every mapped column shows target field and type; unmapped columns are explicit
- [ ] Validation errors block; warnings can be acknowledged; detail is downloadable
- [ ] The job links to the job monitor with stored vs searchable progress

#### Dependencies
- `E08-T06` — Add pre-flight validation, import report and re-loadable error file
- `E15-T02` — Build application shell, session handling and workspace context
- `E06-T07` — Build job monitor UI, job tray and notifications

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX, eDiscovery Practitioner
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

---

### E08-T09

**Harden ingest with malware scanning, type verification and quarantine**  
Labels: `role:security`, `role:ediscovery`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§12, §13. Security finding 11: imported content is hostile by assumption.

#### Description
Pluggable scanner stage (ClamAV default; ICAP for enterprise), magic-byte type verification vs extension, archive/decompression-bomb limits, quarantine state with restricted access, formula neutralization for platform-produced CSV/DAT.

#### Acceptance criteria
- [ ] EICAR is quarantined, not rendered, and downloadable only with `Document.ViewQuarantined`; audit event written
- [ ] Configurable limits (max file size, line length, fields, zip ratio) are enforced with tests
- [ ] Hash mismatch vs load file flags the document in the import report
- [ ] Formula-leading cells (`=`, `+`, `-`, `@`) are neutralized in platform-produced CSV/DAT

#### Dependencies
- `E08-T04` — Link natives and extracted text into object storage
- `E05-T04` — Build protected-content gateway and authoritative access service

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Security & Compliance
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
SEC-15.
