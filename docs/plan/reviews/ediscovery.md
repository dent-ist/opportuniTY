# eDiscovery Practitioner Review — opportuniTY Architecture Baseline

Reviewer lens: litigation support / processing / production practitioner (EDRM processing → review → production; Concordance DAT, Opticon OPT, IPRO LFP; Relativity/Nuix/Reveal interop).

Overall: the baseline is strong on scale, consistency and security plumbing (§7, §21–§24) but thin on the *file-format and defensibility* contracts that decide whether a real vendor-delivered volume imports cleanly and whether a production survives opposing-counsel QC. §12 and §14 are one paragraph each; both need a functional spec before the first vertical slice (§20/§32) can be called "useful".

## Review findings

- **§12 — DAT parsing contract is unspecified.** "MVP supports DAT" without naming delimiters is a guaranteed import failure on day one. Concordance default is column separator ASCII 020 `` (U+0014, DC4), text qualifier ASCII 254 `þ` (U+00FE) around each value, newline-in-value ASCII 174 `®` (U+00AE), multi-value `;` and nested-value `\`. In practice vendors ship `¶` (U+00B6) as separator + `þ` as qualifier, or CSV. The importer must support a configurable delimiter profile (column, quote, newline, multi-value, nested-value), with presets ("Concordance default", "Relativity default", "CSV").
- **§12 — Encoding detection is unaddressed.** Real volumes arrive as UTF-8 (with/without BOM), UTF-16LE with BOM (Relativity/Nuix exports), and Windows-1252/"ANSI". `þ` is 0xFE in 1252 but `C3 BE` in UTF-8; mis-detection silently shifts every column. Need BOM sniffing, explicit override, and a pre-flight "preview first N rows" that shows header parsing before commit. Extracted-text TXT files have their *own* encoding independent of the DAT.
- **§12 — OPT/LFP image cross-reference is not modeled.** OPT rows are `ImageKey,Volume,RelativePath,DocBreak(Y/blank),FolderBreak,BoxBreak,PageCount`; page count is only on the first page row and must reconcile with the number of rows until next `Y`. Multi-page TIFF vs single-page TIFF/JPG, missing images, and paths using `\` vs `/` and volume-relative roots all need handling. LFP (IPRO, `IM,`/`OF,` records) is common for older productions; decide P1 vs P2. The §5 Document model has "artifact object IDs" but no page-level entity (page number, image key/Bates, object ref) — production and redaction (§13) both need per-page identity.
- **§12 — Overlay vs append is missing.** Every real matter re-loads: overlay corrected metadata, append a new volume, replace extracted text after OCR, link natives later. Need explicit modes: Append (new only; error on existing key), Overlay (existing only; error on new), Append/Overlay; overlay key field (ControlNumber default, or other unique identifier); per-field "overlay blank values?" behavior and multi-value merge vs replace. Overlay of a field that participates in search must flow through §21 chunk indexing, and overlay of security-affecting fields through §24.
- **§12/§11 — Import validation reports and error files.** Practitioners expect: pre-flight validation (header match, field-count per row, required fields, date parse, path existence, duplicate control numbers in file, existing in workspace), a downloadable error file in the *same DAT format* containing only failed rows plus an error column so it can be fixed and re-loaded, and a summary report (rows read/imported/skipped/errored, natives linked/missing, text linked/missing, images/pages linked). `JobChunk` errors (§11) are not a user-facing report.
- **§5 — ControlNumber uniqueness and identity.** The baseline lists ControlNumber but does not state it is unique per workspace, case sensitivity, or how BegBates/ProdBeg vs ControlNumber coexist. Productions *received* from opposing party use their Bates as identity; productions *we create* add new Bates fields per production. Need: unique index (workspace, normalized control number), optional prefix/volume namespacing, and a separate "Production Bates" per-production field set.
- **§5/§19 ADR-9 — Family reconstruction from BegAttach/EndAttach.** Upstream DATs commonly give `BegAttach/EndAttach` (ranges), `ParentID/AttachmentIDs` (lists), or `GroupIdentifier` (Relativity). Range-based families require ordering by control number/Bates — which is lexical and only correct if padding is consistent. The importer must normalize all three into FamilyId/ParentDocumentId/FamilySequence (§5) and flag orphan attachments, broken ranges and parents missing from the load. Families split across volumes (overlay of attachments later) must re-resolve.
- **§5/§14 — Dedupe scope and "All Custodians".** §5 says "Deduplication policy remains configurable" but the model only has DuplicateGroupId. Practitioners need: scope (global vs custodial vs none), *family-level* dedupe (dedupe on top-level parent hash, never on individual attachments, or families break), a primary/master selection rule, and preservation of `AllCustodians` / `DuplicateCustodians` / `AllPaths` (multi-value) on the surviving record — this is often required by ESI protocols. When upstream already deduped, those fields arrive in the DAT and must be imported as multi-value, not recomputed.
- **§5 — Hash normalization for email.** MD5/SHA256 of an MSG/EML file differs between two copies of the same email (transport headers, PST vs MSG extraction). Industry practice: email dedupe hash computed over normalized fields (From, To, CC, BCC, Subject, Sent date UTC, body text, attachment hashes) — each vendor differs. Since processing is deferred (§1), the importer must accept an upstream `DedupeHash`/`EmailHash` field distinct from file MD5/SHA1/SHA256 and record which hash drove grouping.
- **§5/§33 — Upstream email thread IDs.** Thread analytics UI is deferred, but EmailThreadId must be importable from upstream (Nuix `ThreadIndex`, Relativity `EmailThreadGroup`/`Email Threading ID`, Outlook `Conversation Index`, `InclusiveEmail` Y/N). Recommend importing raw ConversationIndex + upstream thread group + inclusive flag as typed fields so a later threading engine can recompute.
- **§6/§23 — Dates and time zones.** DAT dates come as `MM/DD/YYYY`, `YYYYMMDD`, `DD/MM/YYYY` (UK/EU matters), separate `DateSent` + `TimeSent` columns, with or without a UTC offset, often processed in a matter time zone (e.g. "processed in UTC-05:00"). Need per-import date format + source time zone, storage as UTC instant + original string, and a matter display time zone. `documentDate` (§23) needs a defined derivation (sent date for email, last-modified for e-docs, parent date for attachments — "family date" sorting is a reviewer expectation).
- **§12/§29 — Extracted text size limits.** §29 targets ~10 MB text docs, but real corpora include 100 MB+ TXT (log dumps, spreadsheets). OpenSearch `http.max_content_length` (100 MB default) and highlighting `index.highlight.max_analyzed_offset` (1M chars default) will fail or silently truncate highlighting. Need a configured cap, a `TextTruncated` flag/field searchable by reviewers, and full text kept in object storage for viewer/production.
- **§14 — Production output formats unspecified.** Need: DAT (configurable field list and delimiters, `ProdBegBates/ProdEndBates/BegAttach/EndAttach/Custodian/AllCustodians/NativeLink/TextLink/Confidentiality`), OPT and optionally LFP, single-page TIFF G4 (B&W) / JPG (color) or PDF, natives with *slip sheets* ("Document Produced in Native Format" with Bates/confidentiality) for spreadsheets/media, text per document named by Bates, folder structure `VOL001/IMAGES/IMG001/`, `NATIVES/`, `TEXT/`, `DATA/`. Bates: prefix, start number, zero-padding (e.g. `ABC0000001`), page-level vs document-level numbering for natives, suffix for page sequence on natives (`ABC0000001.0001`) policy, endorsement position (footer left/center/right) and confidentiality legend.
- **§14 — Placeholders and withheld documents.** Privileged-withheld family members typically require a placeholder slip sheet ("Withheld – Privileged") consuming one Bates number to preserve family integrity; technical-issue placeholders ("Document could not be imaged") likewise. Without this, family completeness QC always fails.
- **§14 — Production QC gates are only a phrase.** Must include blocking checks: privilege-coded docs in set (excluding those set to redact/placeholder), redaction-coded docs with no redactions applied, family completeness (every family member either produced, placeholdered or explicitly excluded), image count reconciliation (OPT page rows = rendered pages = Bates range span), Bates gaps/overlaps across prior productions, missing text/native files, and checksum manifest. A privilege log export (§14) should be generated from the same frozen snapshot (§22).
- **§9 — Search term reports (STR).** "Term-hit reporting" needs definition: per term, documents with hits, *unique hits* (docs hit by only that term), documents with hits incl. family, and total unique across all terms; run against a frozen snapshot for reproducibility because STRs are exchanged with opposing counsel during meet-and-confer. Note hit counts must be computed from OpenSearch but displayed with the §28 generation watermark.
- **§5/§14 — Saved search ↔ batching and persistent highlighting.** Review batches are typically created from saved searches (family-grouped, batch size N, kept-together families). Persistent highlighting (term sets with colors applied in the viewer independently of the active search) is a baseline reviewer expectation; §13 viewer must support highlight term sets against extracted text and image/OCR coordinates.

## Proposed epics and tickets

### EPIC: Load-File Import (DAT / OPT / TXT / Natives, Overlay, Validation)
Deliver a vendor-grade importer that ingests real-world processed volumes from Relativity, Nuix, Reveal and other processing tools without hand-massaging, supports overlay/append re-loads, and produces defensible validation reports and fixable error files. Baseline: §1, §5, §6, §11, §12, §20, §21, §24, §32.

#### DAT parser with configurable delimiter profiles
- **Role:** eDiscovery (functional spec)
- **Description:** Parse Concordance-style DAT and CSV with a delimiter profile: column separator, text qualifier, newline-in-value replacement, multi-value separator, nested-value separator. Ship presets and allow custom.
- **Acceptance criteria:**
  - Presets: "Concordance default" (column `` U+0014, quote `þ` U+00FE, newline `®` U+00AE, multi `;`, nested `\`); "Common vendor" (column `¶` U+00B6, quote `þ` U+00FE, newline `®`); "CSV" (`,`, `"`, RFC 4180 doubled-quote escaping).
  - `®` in a value is converted to `\n` on import; a literal `®` can be preserved via a profile option.
  - Embedded column separator inside a qualified value does not split the field.
  - A row whose field count ≠ header field count is rejected to the error file with row number and observed vs expected count.
  - Header row required; duplicate header names reported as a pre-flight error.
  - Parser is streaming (memory bounded; 5 GB DAT imports without loading into memory).
  - Golden-file unit tests for each preset including empty fields, qualifier at value start/end, and CRLF vs LF line endings.
- **Dependencies:** none
- **Phase:** P1
- **Size:** M

#### Encoding detection and preview
- **Role:** eDiscovery (functional spec)
- **Description:** Detect and allow override of DAT and TXT encodings; show a preview before the job is committed.
- **Acceptance criteria:**
  - Detects UTF-8 BOM (`EF BB BF`), UTF-16LE BOM (`FF FE`), UTF-16BE BOM (`FE FF`); BOM-less defaults to UTF-8 with fallback heuristic to Windows-1252 if invalid UTF-8 sequences are found.
  - User can override encoding separately for DAT and for extracted text files.
  - Preview shows first 20 parsed rows in a grid with detected encoding and delimiter; a mis-detected `þ` (e.g. `Ã¾`) is visibly obvious.
  - Test fixtures: identical content in UTF-8, UTF-8-BOM, UTF-16LE-BOM and 1252 all import to byte-identical stored values.
  - TXT with invalid bytes are imported with replacement characters and flagged `TextEncodingWarning=true`, not failed.
- **Dependencies:** DAT parser
- **Phase:** P1
- **Size:** S

#### Field mapping, typed parsing and multi-value fields
- **Role:** eDiscovery (functional spec)
- **Description:** Map DAT columns to workspace field definitions (§6) or create new ones; parse dates, numbers, booleans and multi-value lists with per-import settings. Save mappings as reusable templates.
- **Acceptance criteria:**
  - Each column maps to: existing field, new field (with type from §6 list), or ignore.
  - Auto-map by exact and alias names (e.g. `BEGDOC`, `BegBates`, `Control Number` → ControlNumber; `CUSTODIAN`; `DATESENT`/`Date Sent`).
  - Date formats configurable per import: `MM/DD/YYYY`, `DD/MM/YYYY`, `YYYY-MM-DD`, `YYYYMMDD`, ISO 8601 with offset; optional companion time column (`TimeSent` `HH:mm:ss` / `hh:mm tt`) merged into one instant.
  - Source time zone setting (IANA id); stored as UTC plus original raw string; `00/00/0000` and blank stored as null without error.
  - MultiChoice/multi-value Keyword fields split on the profile multi-value delimiter, trimmed, de-duplicated; `AllCustodians` "A; B; A" stores `[A, B]`.
  - Boolean accepts Y/N, Yes/No, True/False, 1/0 (case-insensitive).
  - Mapping templates saved per workspace and selectable on subsequent loads.
- **Dependencies:** DAT parser; §6 field definitions
- **Phase:** P1
- **Size:** M

#### Native and extracted-text file linking
- **Role:** eDiscovery (functional spec)
- **Description:** Link natives and TXT via path columns (`NativeLink`, `TextLink`/`ExtractedText`) relative to a load root, store in object storage, compute/verify hashes.
- **Acceptance criteria:**
  - Paths accept `\` or `/`, leading `.\`, and are resolved relative to the configured volume root; absolute paths (`D:\VOL001\...`) are rebased by stripping a configurable prefix.
  - Missing file → row imported with `NativeMissing`/`TextMissing` flag and reported (not a row failure), configurable to fail instead.
  - Optional "text in DAT field" mode (text inline in a column) supported.
  - SHA-256 computed on stored native; if DAT provides MD5/SHA1 and they mismatch the file, report a hash-mismatch warning.
  - Extracted text above configured cap (default 50 MB chars) is indexed up to the cap, full text stored in object storage, and `TextTruncated=true` is set and searchable.
  - Re-running the same chunk does not create duplicate objects (idempotent object keys, §2.4).
- **Dependencies:** field mapping; object storage (§19 ADR-11)
- **Phase:** P1
- **Size:** M

#### OPT image load and page model
- **Role:** eDiscovery (functional spec)
- **Description:** Import Opticon OPT files, link page images to documents, and persist a page entity (document, page number, image key, object ref) for viewer, redaction and production.
- **Acceptance criteria:**
  - Parses `ImageKey,Volume,Path,DocBreak,FolderBreak,BoxBreak,PageCount`; `Y` in col 4 starts a document.
  - Document matched by ImageKey = ControlNumber (or BegBates field configurable).
  - Reconciliation: PageCount on the break row equals number of rows in that doc; mismatch reported as warning with both counts.
  - Supports single-page TIFF (G4), JPG, PNG, and multi-page TIFF where one OPT row references a file containing N pages.
  - Missing image file reported per page; doc gets `ImagesIncomplete=true`.
  - OPT ImageKeys not found in workspace reported as orphan rows.
  - Page entity stores ordinal, image key (page Bates), width/height/DPI for redaction coordinates (§13).
- **Dependencies:** DAT import (documents must exist or be in same job)
- **Phase:** P1
- **Size:** M

#### Overlay and append import modes
- **Role:** eDiscovery (functional spec)
- **Description:** Support Append, Overlay and Append/Overlay with a chosen overlay key; route changed searchable fields through chunk indexing (§21) and security-affecting fields through §24.
- **Acceptance criteria:**
  - Append: existing key → row error "already exists". Overlay: missing key → row error "not found". Append/Overlay: both allowed.
  - Overlay key selectable among unique fields (default ControlNumber).
  - Per-import option "Blank values overwrite existing" (default off); multi-value fields option Replace vs Merge.
  - Overlay of TextLink replaces stored text and re-indexes; overlay of NativeLink replaces native, prior object retained for audit.
  - Each overlaid document increments DocumentVersion; an audit event records old/new values per field.
  - Overlay touching FamilyId/ParentID triggers family re-resolution (see family epic).
  - Overlaying a privilege/confidentiality field enforces the new restriction before index completion (§24 test).
- **Dependencies:** field mapping; §21; §24
- **Phase:** P1
- **Size:** L

#### Pre-flight validation, import report and error file
- **Role:** eDiscovery (functional spec)
- **Description:** Validate before commit, and produce a downloadable summary and a re-loadable error file after the job.
- **Acceptance criteria:**
  - Pre-flight (no writes): header/mapping check, row field counts, required fields present, date parse failures, duplicate control numbers within file, collisions with workspace for Append mode, path existence sample (configurable 100% or sampled).
  - Summary report (CSV + UI): rows read, imported, overlaid, skipped, errored; natives linked/missing; text linked/missing/truncated; images/pages linked/missing; families built/orphans; elapsed time.
  - Error file is a DAT in the *same delimiter profile and encoding* as input, containing original failing rows plus trailing `ImportError` column; re-importing it after correction succeeds.
  - Job can be configured "stop on first N errors" (default: continue).
  - Report retained with the job and auditable.
- **Dependencies:** DAT parser; §11 jobs
- **Phase:** P1
- **Size:** M

#### Control number uniqueness and Bates-range identity
- **Role:** eDiscovery (functional spec)
- **Description:** Enforce workspace-unique control numbers and support received-production identity (BegBates/EndBates) distinct from internal control numbers.
- **Acceptance criteria:**
  - Unique constraint on (WorkspaceId, normalized ControlNumber); normalization trims whitespace and is case-insensitive (configurable).
  - Optional import prefix applied to control numbers (e.g. `VOL002_`) to avoid collisions across vendors.
  - Sort by ControlNumber uses natural ordering (prefix + numeric) so `ABC10` > `ABC9` when padding is inconsistent.
  - BegBates/EndBates imported as typed fields; page count derived from range is compared with OPT page count.
- **Dependencies:** field mapping
- **Phase:** P0 (schema decision needed before first slice)
- **Size:** S

### EPIC: Family, Duplicate and Thread Relationships
Normalize upstream relationship metadata into the §5 structural fields so family-aware search, review and production are correct from day one, and preserve upstream dedupe/thread decisions rather than recomputing them in MVP. Baseline: §1, §5, §9, §14, §19 (ADR-9), §23, §32, §34.

#### Family reconstruction from BegAttach/EndAttach, ParentID and GroupIdentifier
- **Role:** eDiscovery (functional spec)
- **Description:** Build FamilyId, ParentDocumentId and FamilySequence from any of the common upstream representations.
- **Acceptance criteria:**
  - Mode A (range): docs whose ControlNumber falls within [BegAttach, EndAttach] of a parent form a family; parent = doc whose ControlNumber = BegAttach.
  - Mode B (pointer): `ParentID` field on children; `AttachmentIDs` multi-value on parent cross-validated.
  - Mode C (group): `GroupIdentifier`/`FamilyID` shared value; parent = lowest ControlNumber or doc with blank ParentID.
  - FamilySequence = 0 for parent, 1..n by control-number order for children; nested attachments (attachment of attachment) flattened to the top-level family with ParentDocumentId pointing to immediate parent.
  - Report: orphan attachments (parent not in workspace), ranges spanning non-existent control numbers, docs claimed by two families.
  - Re-resolution runs when a later overlay/append supplies missing members; families updated through chunk indexing (§21).
- **Dependencies:** Load-file import epic
- **Phase:** P1
- **Size:** M

#### Import upstream duplicate groups and custodian rollups
- **Role:** eDiscovery (functional spec)
- **Description:** Accept upstream dedupe results and duplicate custodian fields; optionally compute duplicate groups from a chosen hash.
- **Acceptance criteria:**
  - DuplicateGroupId computable from a selectable hash field (file SHA-256, upstream MD5/SHA1, or upstream `DedupeHash`/email hash); the hash field used is recorded on the group.
  - Grouping computed at family level: only top-level parents are compared; a family is a duplicate only if parent hash matches; attachments inherit parent group.
  - Scope setting: Global or Custodial (custodian + hash).
  - `AllCustodians`, `DuplicateCustodians`, `AllPaths`/`DuplicatePaths` imported as multi-value and searchable (`custodian:` searches can optionally include AllCustodians).
  - Primary document flag per group (rule: earliest date, then lowest control number), surfaced in grid.
- **Dependencies:** family reconstruction; field mapping
- **Phase:** P1
- **Size:** M

#### Upstream email threading fields
- **Role:** eDiscovery (functional spec)
- **Description:** Import thread identity from processing tools to populate EmailThreadId and supporting typed fields; no threading engine in MVP.
- **Acceptance criteria:**
  - EmailThreadId populated from a mapped upstream thread group field (e.g. Relativity "Email Thread Group", Nuix thread ID).
  - Raw `ConversationIndex` (hex) and `ConversationTopic`, `InclusiveEmail` (Y/N), `ThreadSortOrder` stored as typed fields.
  - Search supports `emailThreadId` grouping/expansion like family expansion (§9).
- **Dependencies:** field mapping
- **Phase:** P1
- **Size:** S

#### Family/duplicate expansion in search and bulk actions
- **Role:** eDiscovery (functional spec)
- **Description:** Provide "include family", "include duplicates" and "include thread" expansion for search results, bulk coding and snapshots.
- **Acceptance criteria:**
  - Search result count displays base hits and expanded count separately.
  - Bulk coding dialog offers "apply to family" and "propagate to duplicates" for designated fields (e.g. privilege/responsiveness propagation config per field).
  - Expansion is applied before snapshot materialization (§22), so membership is deterministic.
  - Family grid sort ("family date", then FamilySequence) keeps families contiguous.
- **Dependencies:** family reconstruction; §22
- **Phase:** P1
- **Size:** M

### EPIC: Search Term Reports, Saved Searches and Highlighting
Make search defensible for meet-and-confer: reproducible term reports with standard hit metrics, saved searches that drive batching, and persistent highlighting in the viewer. Baseline: §5, §9, §13, §14, §22, §28, §33.

#### Search term report (STR) with unique hits
- **Role:** eDiscovery (functional spec)
- **Description:** Run a list of terms (OQL expressions) against a scope and report standard metrics.
- **Acceptance criteria:**
  - Input: list of named terms (paste or CSV `Name,Expression`), scope = saved search / snapshot / whole workspace.
  - Per term columns: Documents with hits; Documents with hits including family; Unique hits (docs hit by this term and no other term in the report); Unique hits including family.
  - Totals row: total docs with ≥1 hit, with family, docs with no hits in scope.
  - Report executes against a materialized snapshot (§22) and records SnapshotId, SearchGeneration (§28), executed-by/time; re-running on the same snapshot gives identical numbers.
  - Export to CSV and XLSX; invalid term syntax returns per-term error without failing the report.
  - Terms are clickable to open the hit set as a search.
- **Dependencies:** §9 parser; §22 snapshots
- **Phase:** P1
- **Size:** M

#### Saved searches with conditions, columns and sort
- **Role:** eDiscovery (functional spec)
- **Description:** Persist query, family/duplicate expansion, display fields and sort, with owner/sharing permissions.
- **Acceptance criteria:**
  - Saved search stores OQL text and parsed AST version, expansion options, column set, sort.
  - Can be nested (saved search referenced as criterion) with cycle detection.
  - Results honor the user's current permissions at execution time (§24), not the creator's.
  - Audit records create/modify/run.
- **Dependencies:** §9
- **Phase:** P1
- **Size:** S

#### Review batch creation from saved search
- **Role:** eDiscovery (functional spec)
- **Description:** Create review batches from a saved search with family/thread integrity.
- **Acceptance criteria:**
  - Settings: batch size (e.g. 500), keep families together (batch may exceed size to fit a family), optional keep threads together, batch name prefix.
  - Batch membership is materialized (snapshot) at creation; documents already in an active batch set can be excluded.
  - Batch status (Pending/In progress/Complete) and assigned reviewer persisted per §14 domain model.
- **Dependencies:** saved searches; family expansion; §22
- **Phase:** P2 (domain support P1 per §34; UI later)
- **Size:** M

#### Persistent highlight term sets in viewer
- **Role:** eDiscovery (functional spec)
- **Description:** Workspace-level highlight sets (term list + color) applied in the text viewer regardless of the active search, plus active-search hit highlighting.
- **Acceptance criteria:**
  - Admin defines sets; reviewers toggle sets on/off; each term has a color.
  - Highlighting supports phrases and wildcard terms; hit navigation (next/previous, count per term).
  - Documents with `TextTruncated=true` show a banner that highlighting covers only the indexed portion.
  - Works for docs with text >1M characters without timing out (highlight computed client-side or chunked server-side).
- **Dependencies:** §13 viewer
- **Phase:** P1 (text view); P2 for image-coordinate highlighting
- **Size:** M

### EPIC: Production and Export Formats
Generate productions that conform to standard ESI protocols — Bates-endorsed images, natives with slip sheets, text, and DAT/OPT/LFP load files — with blocking QC checks and reproducible, checksummed output. Baseline: §1, §10, §13, §14, §22, §24, §32.

#### Production set definition and Bates numbering
- **Role:** eDiscovery (functional spec)
- **Description:** Define a production from a frozen snapshot with Bates and endorsement settings.
- **Acceptance criteria:**
  - Bates prefix (e.g. `ABC`), optional separator, start number, zero padding width (e.g. 7 → `ABC0000001`), optional suffix.
  - Numbering level: page-level for imaged docs; for native-only docs, one number per document (slip sheet page) with option for page-suffix style `ABC0000001.0001`.
  - Start number validation: warns/blocks if range overlaps any prior production with same prefix.
  - Endorsements: Bates and a confidentiality legend (from a field value mapping, e.g. "CONFIDENTIAL", "HIGHLY CONFIDENTIAL – AEO") at selectable positions (bottom-left/center/right, top); font size; does not overwrite image content (stamp in margin/expanded canvas option).
  - Per-document Production fields written back: ProdBegBates, ProdEndBates, ProdBegAttach, ProdEndAttach, ProductionId, page count.
  - Definition is saved and rerunnable; the snapshot is materialized (§22).
- **Dependencies:** §22; render pipeline §13
- **Phase:** P1
- **Size:** L

#### Image, native, text and placeholder outputs
- **Role:** eDiscovery (functional spec)
- **Description:** Produce per-document outputs per a format profile with placeholders for withheld/native/technical-issue docs.
- **Acceptance criteria:**
  - Image types: single-page TIFF Group IV 300 DPI (B&W), JPG for color (configurable per file type), or searchable PDF per doc.
  - Native production rules by file type (e.g. xls/xlsx/csv/audio/video always native) with a slip sheet image: "Document Produced in Native Format" + Bates + confidentiality.
  - Native file named `<ProdBegBates>.<ext>`; text named `<ProdBegBates>.txt` (UTF-8 default; UTF-16LE option).
  - Redacted docs: text is regenerated from OCR of redacted images or replaced by unredacted portions only — never the original extracted text; native of redacted doc never produced.
  - Placeholders: "Withheld – Privileged" (for family members coded privileged-withhold), "Technical Issue" (render failure); each consumes one Bates number.
  - Folder layout: `<VOL>/IMAGES/IMG0001/`, `<VOL>/NATIVES/NATIVE0001/`, `<VOL>/TEXT/TEXT0001/`, `<VOL>/DATA/`, max files per folder configurable (default 1,000).
  - Burned redactions per §13; source unchanged.
- **Dependencies:** Bates numbering; §13
- **Phase:** P1 (TIFF/JPG, natives, text, placeholders); PDF P2
- **Size:** L

#### Production load files (DAT, OPT, LFP)
- **Role:** eDiscovery (functional spec)
- **Description:** Emit load files describing the production volume.
- **Acceptance criteria:**
  - DAT: user-selected field list and order; delimiter profile (default Concordance `þ`/``/`®`); encoding UTF-8 with BOM (default) or UTF-16LE; multi-value with `;`; dates in a selected format and time zone; `NativeLink` and `TextLink` relative paths; one row per produced document including placeholders.
  - Standard default field set: ProdBegBates, ProdEndBates, ProdBegAttach, ProdEndAttach, Custodian, AllCustodians, FileName, FileExtension, DateSent, DateCreated, DateLastModified, From, To, CC, BCC, Subject, MD5Hash, Confidentiality, Redacted (Y/N), PageCount, NativeLink, TextLink.
  - OPT: one row per page; `Y` and page count on first page; paths relative to volume root; page count per doc equals image file count.
  - LFP (IPRO): `IM,<Bates>,<D|blank>,<offset>,@<Volume>;<path>;<file>;<type>` option.
  - Round-trip test: the produced volume imports back into opportuniTY with 0 errors and identical page counts and Bates.
- **Dependencies:** image/native/text outputs; DAT parser (for round-trip)
- **Phase:** P1 (DAT/OPT); P2 (LFP)
- **Size:** M

#### Production QC gates
- **Role:** eDiscovery (functional spec)
- **Description:** Blocking and warning checks before a production can be finalized.
- **Acceptance criteria:**
  - Blocking: any doc coded privileged-withhold present without placeholder; any doc coded "Redact" with zero redactions; any doc with redactions but produced as native; Bates overlap with a prior finalized production; render failures not placeholdered.
  - Warning (acknowledge to proceed): incomplete families (member not produced and not placeholdered); docs with `TextMissing`; confidentiality field blank.
  - Reconciliation: images rendered = OPT page rows = sum(ProdEndBates − ProdBegBates + 1) across docs; native count = DAT NativeLink count; text file count = DAT rows.
  - Authorization re-check (§24) at production-inclusion time for each doc.
  - QC report exported with production; overrides recorded with user and reason in audit.
- **Dependencies:** production set definition
- **Phase:** P1
- **Size:** M

#### Manifest, checksums, and privilege log
- **Role:** eDiscovery (functional spec)
- **Description:** Finalize production with manifest and support privilege log generation from the same snapshot.
- **Acceptance criteria:**
  - Manifest CSV lists every file with relative path, size, SHA-256; volume-level MD5/SHA-256 for the package (zip or folder).
  - Production marked Finalized becomes immutable; re-generation produces byte-identical load files given same definition and snapshot.
  - Privilege log export (CSV/XLSX): ProdBegBates or ControlNumber, Date, Author/From, Recipients, Doc type, Privilege type, Description field; including withheld family members and placeholders.
- **Dependencies:** production outputs; QC gates
- **Phase:** P1 (manifest); P2 (privilege log templates)
- **Size:** S

#### Export (non-production) to load file
- **Role:** eDiscovery (functional spec)
- **Description:** Export a search/snapshot to DAT/OPT with natives/text/images, without Bates endorsement, for vendor hand-off or migration (§20/§32 EXPORT step).
- **Acceptance criteria:**
  - Same delimiter/encoding/field-selection options as production DAT; identity field = ControlNumber.
  - Includes coding fields and relationship fields (FamilyId, BegAttach/EndAttach, DuplicateGroupId, EmailThreadId).
  - Exported volume re-imports into a new workspace with identical document count, families and coding.
- **Dependencies:** DAT parser; §22
- **Phase:** P0/P1 (vertical-slice export)
- **Size:** M

### EPIC: Synthetic Test-Corpus Realism (Data Generator)
Make `tools/Opportunity.DataGenerator` (§18) emit corpora and load files that look like real vendor volumes, so import, family, dedupe, search and production paths are exercised under realistic shape, not just volume. Baseline: §18, §29, §25–§26.

#### Generate realistic load-file volumes
- **Role:** eDiscovery (functional spec)
- **Description:** Output DAT/OPT/TXT/NATIVES/IMAGES volume structures, not only DB rows, for importer testing.
- **Acceptance criteria:**
  - Output folders `VOL001/DATA|NATIVES|TEXT|IMAGES` with configurable files per folder.
  - DAT in any delimiter preset and encoding (UTF-8, UTF-8-BOM, UTF-16LE, 1252).
  - Configurable injected defects at a given rate: wrong field count rows, unparseable dates, missing natives/text/images, OPT page count mismatch, duplicate control numbers, orphan attachments.
  - Deterministic by seed (§29 requires seed storage).
- **Dependencies:** DAT parser (for round-trip validation)
- **Phase:** P0
- **Size:** M

#### Realistic metadata, families, duplicates and threads distribution
- **Role:** eDiscovery (functional spec)
- **Description:** Model realistic distributions matching §29 enterprise reference plus practitioner realism.
- **Acceptance criteria:**
  - ~60% email / 40% e-docs; email families average 1 parent : 3 members (§29) with long tail (some families >200 members, e.g. zip containers).
  - Nested attachments (depth up to 3).
  - ~20% duplicates (§29) generated as whole-family duplicates across different custodians; populate AllCustodians/DuplicateCustodians accordingly; some near-duplicate text variants (not exact).
  - Email threads: chains of 2–50 messages with quoted prior text, ConversationIndex prefixes consistent with thread, InclusiveEmail flags.
  - ~30 custom fields (§29) including multi-value (To/CC/BCC with 1–500 recipients), dates across time zones, Unicode names (CJK, accented, RTL), and values containing the delimiter and `®`/newlines.
  - Heavy-tailed text: median ~5 KB, p99 ~1 MB, some ≥10 MB (§29), a few >100 MB to test truncation.
  - Custodian count configurable (e.g. 50–5,000) with Zipf distribution of documents per custodian.
- **Dependencies:** none
- **Phase:** P0
- **Size:** M

#### Search-term realism and known-answer STR fixtures
- **Role:** eDiscovery (functional spec)
- **Description:** Seed text with controllable term frequencies so STR counts, unique hits, proximity and family-expanded counts have known expected answers.
- **Acceptance criteria:**
  - Generator emits a ground-truth file: for a given term list, expected docs-with-hits, unique hits, and with-family counts.
  - Includes proximity pairs at known distances (`W/5` true/false cases), stemming variants, hyphenated and apostrophe tokens, and terms only present in attachments (family-expansion test).
  - STR integration test asserts exact equality with ground truth on 1M corpus.
- **Dependencies:** STR ticket; §9
- **Phase:** P1
- **Size:** M

#### Production-ready fixtures: images, privilege and redaction coding
- **Role:** eDiscovery (functional spec)
- **Description:** Generate page images and coding states that drive production QC paths.
- **Acceptance criteria:**
  - Page counts heavy-tailed (1–2,000 pages); mix of single-page TIFF G4, JPG color, and multi-page TIFF.
  - Seeded coding: privileged-withhold family members, redact-coded docs with/without redactions, spreadsheet natives, render-failure docs.
  - Expected QC results file (blocking/warning counts) for automated production QC tests.
- **Dependencies:** generator volumes; production QC
- **Phase:** P1
- **Size:** S

## Open questions for the product owner

1. **Import source priority:** Which processing tools' exports must import cleanly at MVP (Relativity, Nuix, Reveal/Brainspace, Everlaw, Venio, GoldFynch)? This determines default delimiter presets, field-name alias maps and whether LFP is P1.
2. **Identity model:** Is ControlNumber the immutable internal identifier, with received-production Bates as separate fields — or do we allow Bates to be the key for opposing-party productions? Can a control number ever be renamed?
3. **Dedupe in MVP:** Do we only *honor upstream* dedupe (import AllCustodians, DuplicateGroup), or must opportuniTY compute duplicate groups itself at import? If computed, global or custodial by default, and which email hash definition?
4. **Time zone policy:** Is there a single matter display time zone, or per-user? Must productions output dates in the processing time zone specified in the ESI protocol (often UTC or a party's local zone)?
5. **Production image format:** Is TIFF G4 + JPG-for-color the required MVP format, or is PDF-per-document acceptable? Which rendering engine (§13) will produce TIFF, and is color detection required?
6. **Native production with redactions:** For redacted spreadsheets, is "image-only with redactions" acceptable in MVP, or is native redaction (cell-level) a requirement? (Strongly suggest P2.)
7. **Privilege log scope:** Is privilege-log generation part of MVP "privilege-log support" (§14), and which fields/templates (category vs document-by-document logs)?
8. **Extracted text cap:** What maximum indexed text size per document is acceptable (e.g. 50 MB), and is it acceptable for search/highlighting to cover only the indexed portion if flagged?
9. **STR reproducibility:** Must STRs be exchangeable with opposing counsel (i.e. require snapshot + generation stamp and export format), and should term syntax mirror dtSearch/Relativity syntax (e.g. `W/n`, `PRE/n`, `!` root expander) for familiarity?
10. **Overlay governance:** Who may run overlays, and must overlays of coding/privilege fields be blocked (import should only overlay metadata) to protect review provenance (§27)?
