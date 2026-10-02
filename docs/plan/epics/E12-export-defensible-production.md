# E12 — Export & Defensible Production

**Labels:** `epic`, `role:backend`, `role:ediscovery`, `role:legal`, `role:qa`, `role:ui`, `P0`  
**Starts in:** M1 - First Vertical Slice  
**Tickets:** 9

## Goal
Export and produce frozen document sets that conform to standard ESI protocols and can be defended: reproducible specifications, gap-free Bates, designations/endorsements, placeholders, burned and verified redactions, DAT/OPT load files, blocking QC gates and checksummed manifests.

## Baseline sections
§1, §10, §13, §14, §22, §24, §32 (EXPORT)

## Scope / out of scope
**In scope**
- Basic export (slice)
- Frozen production specification
- Bates allocation integrity
- Designations/endorsements
- Image/native/text/placeholder outputs and load files
- Redaction burn verification
- QC gate, finalization and manifest
- Production wizard UI
- LFP/PDF (deferred)

**Out of scope**
- Native (cell-level) redaction (Q-22)
- Clawback workflow (E13)

## Contributing roles
- **Roles:** Backend, eDiscovery Practitioner, Legal / Discovery Counsel, QA, UI/UX
- **Source reviews:** eDiscovery Practitioner, Legal/Discovery Counsel, Backend/Architecture, UI/UX, Security & Compliance, QA & Performance
- **Milestones spanned:** M1 - First Vertical Slice, M3 - MVP Feature Complete, M5 - Post-MVP / Deferred

## Exit criteria
- [ ] A produced volume re-imports into opportuniTY with 0 errors and identical page counts and Bates
- [ ] Bates numbers are unique per matter+prefix, never reused, and identical after crash/restart
- [ ] No produced text, PDF or native contains content under a redaction

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E12-T01](#e12-t01) | Export a materialized snapshot to a load-file volume | M1 | L | E10-T02, E05-T04, E06-T02, E08-T01 |
| [E12-T02](#e12-t02) | Freeze production specifications with versioning | M3 | L | E12-T01, E10-T03 |
| [E12-T03](#e12-t03) | Allocate Bates numbers with integrity guarantees | M3 | M | E12-T02 |
| [E12-T04](#e12-t04) | Apply confidentiality designations and endorsements | M3 | M | E12-T02, E05-T06 |
| [E12-T05](#e12-t05) | Generate production volume outputs and DAT/OPT load files | M3 | L | E12-T03, E12-T04, E11-T02, E11-T04 |
| [E12-T06](#e12-t06) | Verify redaction burn-in automatically | M3 | M | E12-T05 |
| [E12-T07](#e12-t07) | Enforce production QC gate, finalization and manifest | M3 | M | E12-T05, E12-T06, E13-T01, E13-T02 |
| [E12-T08](#e12-t08) | Build export and production wizard UI | M3 | L | E12-T07, E06-T07 |
| [E12-T09](#e12-t09) | Add LFP load files and PDF production output | M5 | M | E12-T05, E08-T05 |

---

### E12-T01

**Export a materialized snapshot to a load-file volume**  
Labels: `role:backend`, `role:ediscovery`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§32 EXPORT; §22 exports use materialized snapshots; §24 export re-checks access.

#### Description
`Opportunity.Worker.Export`: chunked export from a materialized snapshot; re-authorizes each document; writes natives/text/images, a DAT (same delimiter/encoding/field-selection options as import profiles, identity = ControlNumber, includes coding and relationship fields FamilyId, BegAttach/EndAttach, DuplicateGroupId, EmailThreadId) and OPT, plus a manifest with per-file SHA-256; formula-leading cells neutralized.

#### Acceptance criteria
- [ ] Re-running an export of a 100K-document snapshot yields identical manifest checksums
- [ ] Documents that became restricted after snapshot creation are excluded and reported (§24)
- [ ] Restart after a worker crash resumes from the last completed chunk
- [ ] The exported volume re-imports into a new workspace with identical document count, families and coding

#### Dependencies
- `E10-T02` — Build materialized DocumentSetSnapshot service
- `E05-T04` — Build protected-content gateway and authoritative access service
- `E06-T02` — Implement Job and JobChunk state machine with leases
- `E08-T01` — Build streaming DAT parser with delimiter profiles and encoding detection

#### Roles
- **Owner:** Backend
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Backend/Architecture, eDiscovery Practitioner, Security & Compliance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

#### Notes
Merges backend 'Basic export job' and eDiscovery 'Export (non-production) to load file'.

---

### E12-T02

**Freeze production specifications with versioning**  
Labels: `role:legal`, `role:ediscovery`, `role:backend`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§14 reproducible definitions; legal finding 9: snapshots freeze *which* documents, not *how* they were produced.

#### Description
A production = materialized snapshot + immutable specification: Bates prefix/start/padding/suffix, numbering level (page vs document, page-suffix `ABC0000001.0001` for natives), image format and DPI, native/image/text rules per file type, load-file formats (fields, order, delimiters, encoding, date format/time zone), endorsement templates, redaction set version, coding-state version for privilege/confidentiality, renderer/tool versions. Finalizing locks it; changes require a new production version.

#### Acceptance criteria
- [ ] After finalization the specification and membership cannot be edited
- [ ] The manifest records all specification values and software versions
- [ ] Re-running a finalized production yields per-file SHA-256 matching the manifest, or an explicit logged difference report

#### Dependencies
- `E12-T01` — Export a materialized snapshot to a load-file volume
- `E10-T03` — Implement deterministic PIT-vs-materialized rule engine and PIT lifecycle

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** eDiscovery Practitioner, Backend
- **Source reviews:** Legal/Discovery Counsel, eDiscovery Practitioner, Backend/Architecture
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
Proposed amendment — reproducibility beyond membership (§22). Q-08.

---

### E12-T03

**Allocate Bates numbers with integrity guarantees**  
Labels: `role:legal`, `role:backend`, `role:qa`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§14 Bates numbering; legal finding 7: retries and parallel chunks make duplicate/skipped numbers a realistic failure; QA: Bates continuity property.

#### Description
Transactional, idempotent allocation unique per matter + prefix across all productions; deterministic in production sort order; family adjacency preserved; placeholders consume one number; voided productions record ranges that are never reissued; start-number overlap check with prior productions; Bates→DocumentId cross-reference queryable for the life of the matter; per-document ProdBegBates/ProdEndBates/ProdBegAttach/ProdEndAttach written back.

#### Acceptance criteria
- [ ] Integrity check after every production: no duplicate Bates in the matter; every in-volume gap is attributed (void, slip sheet)
- [ ] Fault-injection (kill mid-chunk, redeliver) produces identical Bates assignment
- [ ] Property test: gap-free, unique, monotonic in sort order, family-adjacent, deterministic across crash/restart
- [ ] Overlapping start number with a prior production of the same prefix is blocked

#### Dependencies
- `E12-T02` — Freeze production specifications with versioning

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** Backend, QA
- **Source reviews:** Legal/Discovery Counsel, eDiscovery Practitioner, QA & Performance, Backend/Architecture
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E12-T04

**Apply confidentiality designations and endorsements**  
Labels: `role:legal`, `role:ediscovery`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§14 endorsements; legal finding 6: protective-order designations must be stamped, recorded and consistent across families/duplicates.

#### Description
Workspace-configurable designations (default None, CONFIDENTIAL, HIGHLY CONFIDENTIAL – AEO) as a security-affecting field; AEO access limited to designated roles; endorsement text/position (bottom-left/center/right, top), font size, margin/expanded-canvas option so images are not overwritten; family inherits highest designation unless overridden with audited reason; re-designation report listing affected Bates ranges with overlay load file support.

#### Acceptance criteria
- [ ] Every page of a designated document carries the endorsement; the load-file designation field matches 100% (QC check)
- [ ] Family inheritance rule is configurable and audited
- [ ] Designation change after production produces a re-designation report and overlay

#### Dependencies
- `E12-T02` — Freeze production specifications with versioning
- `E05-T06` — Implement document/field-level security, security-affecting fields and ethical walls

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Legal/Discovery Counsel, eDiscovery Practitioner, Security & Compliance
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E12-T05

**Generate production volume outputs and DAT/OPT load files**  
Labels: `role:ediscovery`, `role:backend`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§13 burned redactions + Bates; §14 native/image/PDF output, load files; eDiscovery production format spec; legal finding 4: the most common leak is the unredacted .txt.

#### Description
Per format profile: single-page TIFF G4 300 DPI (B&W) / JPG for color (per file type); native production rules by type (xls/xlsx/csv/audio/video) with slip sheet 'Document Produced in Native Format' + Bates + confidentiality; files named `<ProdBegBates>.<ext>` / `.txt` (UTF-8 default, UTF-16LE option); redacted docs: text regenerated from redacted output, never original extracted text, native never produced; placeholders 'Withheld – Privileged' and 'Technical Issue' consuming one Bates; folder layout `<VOL>/IMAGES/IMG0001/`, `NATIVES/`, `TEXT/`, `DATA/` with max files per folder (default 1,000). DAT with user-selected fields (default set ProdBegBates, ProdEndBates, ProdBegAttach, ProdEndAttach, Custodian, AllCustodians, FileName, FileExtension, DateSent, DateCreated, DateLastModified, From, To, CC, BCC, Subject, MD5Hash, Confidentiality, Redacted, PageCount, NativeLink, TextLink), Concordance delimiters default, UTF-8 BOM default; OPT one row per page with `Y` + page count on first page.

#### Acceptance criteria
- [ ] Burned redactions and Bates/endorsements are flattened into images; sources are unchanged
- [ ] Each placeholder consumes exactly one Bates number and appears in the DAT
- [ ] OPT page rows = image files = Bates span per document
- [ ] Round-trip: the produced volume imports back into opportuniTY with 0 errors and identical page counts and Bates

#### Dependencies
- `E12-T03` — Allocate Bates numbers with integrity guarantees
- `E12-T04` — Apply confidentiality designations and endorsements
- `E11-T02` — Build render worker pipeline for imported PDFs and images
- `E11-T04` — Implement non-destructive redactions API and redaction tool

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** Backend
- **Source reviews:** eDiscovery Practitioner, Legal/Discovery Counsel, Backend/Architecture
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
Q-21. PDF output and LFP are in `E12-T09`.

---

### E12-T06

**Verify redaction burn-in automatically**  
Labels: `role:legal`, `role:qa`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§13 asserts burning; legal finding 4–5: defensibility requires proof (flattened pixels, regenerated text, natives withheld, PDF metadata/annotations/layers/embedded files/XMP stripped).

#### Description
Post-production verification per redacted document: produced text contains no string under any redaction region of the source text; produced images/PDFs contain no annotation objects, optional content groups or extractable text under boxes; no native for redacted docs unless a documented native-redaction method is recorded.

#### Acceptance criteria
- [ ] Verification runs automatically for every production; results are stored in the manifest and QC report
- [ ] A seeded leak (original text shipped for a redacted doc) is detected and blocks finalization
- [ ] Finalization is blocked if a redacted document has a native in the output without a recorded method

#### Dependencies
- `E12-T05` — Generate production volume outputs and DAT/OPT load files

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** QA
- **Source reviews:** Legal/Discovery Counsel, eDiscovery Practitioner, Security & Compliance
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Q-22.

---

### E12-T07

**Enforce production QC gate, finalization and manifest**  
Labels: `role:ediscovery`, `role:legal`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§14 validation/QC, manifests/checksums; §24 re-check at production inclusion.

#### Description
Blocking checks: privileged-withhold docs without placeholder; 'Redact' docs with zero redactions; redacted docs as native; Bates overlap with a finalized production; unplaceholdered render failures; unresolved privilege conflicts (`E13-T02`). Warnings (acknowledge): incomplete families, TextMissing, blank confidentiality. Reconciliation: images = OPT rows = Σ(ProdEnd − ProdBeg + 1); natives = DAT NativeLink count; text files = DAT rows. Authorization re-check per doc. Finalize → immutable; manifest CSV (path, size, SHA-256) plus volume-level hashes; byte-identical regeneration.

#### Acceptance criteria
- [ ] Each check returns pass/fail with document-level exceptions
- [ ] Failures block finalization; an authorized override needs a reason, is audited and printed in the QC report
- [ ] QC report (PDF/CSV) is retained with the production
- [ ] Regenerating load files for the same definition and snapshot is byte-identical

#### Dependencies
- `E12-T05` — Generate production volume outputs and DAT/OPT load files
- `E12-T06` — Verify redaction burn-in automatically
- `E13-T01` — Model privilege designations as system fields
- `E13-T02` — Detect family and duplicate privilege inconsistencies

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** Legal / Discovery Counsel
- **Source reviews:** eDiscovery Practitioner, Legal/Discovery Counsel, Security & Compliance, UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Merges eDiscovery 'Production QC gates' + 'Manifest, checksums', legal 'Pre-finalization production QC gate'.

---

### E12-T08

**Build export and production wizard UI**  
Labels: `role:ui`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§14; UI ticket 'Export and basic production wizard'.

#### Description
Wizard: source (saved search or selection, materialized) → family-aware inclusion → outputs (natives, images, text, load-file format) → Bates prefix/start/padding and endorsements → redaction burn-in → validation summary → run. Past productions show the frozen definition, QC report, manifest/checksum downloads and 'Re-run with same definition'.

#### Acceptance criteria
- [ ] Every export/production shows its snapshot ID and document count
- [ ] Validation errors block with downloadable lists; Bates range preview shown before run
- [ ] Every download triggers the authorization re-check
- [ ] Re-run produces identical membership

#### Dependencies
- `E12-T07` — Enforce production QC gate, finalization and manifest
- `E06-T07` — Build job monitor UI, job tray and notifications

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX, eDiscovery Practitioner
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

---

### E12-T09

**Add LFP load files and PDF production output**  
Labels: `role:ediscovery`, `P2`, `size:M` · Milestone: M5 - Post-MVP / Deferred

#### Context
eDiscovery: LFP (IPRO `IM,` / `OF,` records) is common for older productions; PDF-per-document production deferred.

#### Description
LFP import (`IM,<Bates>,<D|blank>,<offset>,@<Volume>;<path>;<file>;<type>`) and LFP production output; searchable PDF per document production option.

#### Acceptance criteria
- [ ] An LFP volume imports with page reconciliation equivalent to OPT
- [ ] LFP and PDF productions round-trip through import with identical page counts and Bates

#### Dependencies
- `E12-T05` — Generate production volume outputs and DAT/OPT load files
- `E08-T05` — Load OPT image cross-references and reconcile pages

#### Roles
- **Owner:** eDiscovery Practitioner
- **Contributing:** —
- **Source reviews:** eDiscovery Practitioner
- **Milestone / phase / size:** M5 - Post-MVP / Deferred / P2 / M

#### Notes
Q-21, Q-26.
