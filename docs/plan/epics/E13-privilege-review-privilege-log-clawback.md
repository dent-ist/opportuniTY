# E13 — Privilege Review, Privilege Log & Clawback

**Labels:** `epic`, `role:legal`, `role:backend`, `role:ediscovery`, `role:ui`, `P1`  
**Starts in:** M3 - MVP Feature Complete  
**Tickets:** 6

## Goal
Model privilege as a first-class, auditable workflow distinct from responsiveness; keep privilege calls consistent across families and duplicates; generate FRCP 26(b)(5)(A)-compliant logs; support FRE 502(b)/(d) clawback.

## Baseline sections
§5, §6, §14, §15, §22, §24, §27

## Scope / out of scope
**In scope**
- Privilege system fields
- Family/duplicate consistency checks
- Privilege log generation
- Where-produced lookup
- Clawback & re-production (deferred)
- Second-level review (deferred)

**Out of scope**
- Automated privilege detection (AI deferred)

## Contributing roles
- **Roles:** Legal / Discovery Counsel, Backend, eDiscovery Practitioner, UI/UX
- **Source reviews:** Legal/Discovery Counsel, eDiscovery Practitioner, Security & Compliance
- **Milestones spanned:** M3 - MVP Feature Complete, M5 - Post-MVP / Deferred

## Exit criteria
- [ ] Every document withheld or redacted for privilege appears exactly once on the log; no produced-in-full document appears
- [ ] A production cannot be finalized with unresolved privilege conflicts unless an audited override exists

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E13-T01](#e13-t01) | Model privilege designations as system fields | M3 | M | E04-T04, E05-T06 |
| [E13-T02](#e13-t02) | Detect family and duplicate privilege inconsistencies | M3 | M | E13-T01, E09-T03 |
| [E13-T03](#e13-t03) | Generate privilege logs in configurable formats | M3 | L | E13-T01, E12-T05 |
| [E13-T04](#e13-t04) | Provide where-produced lookup and Bates cross-reference | M3 | S | E12-T03, E09-T02 |
| [E13-T05](#e13-t05) | Implement clawback and replacement re-production workflow | M5 | L | E13-T04, E13-T03, E12-T07 |
| [E13-T06](#e13-t06) | Add second-level privilege review queue | M5 | M | E13-T01, E10-T06 |

---

### E13-T01

**Model privilege designations as system fields**  
Labels: `role:legal`, `role:backend`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§14 privilege-log support, §15 privilege-sensitive fields, §24. Legal finding 1: privilege is a field, not a process.

#### Description
System fields in every workspace (extendable, not deletable): Privilege Status (Not Privileged / Withhold / Redact / Needs 2L Review), Privilege Basis (multi-choice: Attorney-Client, Work Product, Common Interest, configurable Other), Privilege Description (log-ready), Attorneys Involved, Log Category. Privilege fields are security-affecting.

#### Acceptance criteria
- [ ] Withhold/Redact without a Basis is rejected by validation
- [ ] Every change creates a CodingEvent with actor, timestamp, prior and new value
- [ ] Changing to Withhold immediately blocks inclusion in any unfinalized production, independent of index freshness

#### Dependencies
- `E04-T04` — Create interim coding current-state and CodingEvent provenance tables
- `E05-T06` — Implement document/field-level security, security-affecting fields and ethical walls

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** Backend
- **Source reviews:** Legal/Discovery Counsel, Security & Compliance, UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Q-20.

---

### E13-T02

**Detect family and duplicate privilege inconsistencies**  
Labels: `role:legal`, `role:ediscovery`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§14 conflict detection; legal finding 8: inconsistent privilege calls across duplicates is a frequent waiver argument.

#### Description
On-demand report of conflicts: (a) family members whose privilege/responsiveness calls produce an incomplete or misleading family; (b) exact duplicates with inconsistent privilege calls; optional 'propagate privilege call to duplicates' bulk action with provenance.

#### Acceptance criteria
- [ ] Report lists DocumentIds, ControlNumbers, values and the reviewers who set them
- [ ] A production cannot be finalized with unresolved conflicts unless an authorized override with reason is recorded (audited)
- [ ] Propagation runs as a bulk job with provenance

#### Dependencies
- `E13-T01` — Model privilege designations as system fields
- `E09-T03` — Expand families, duplicates and threads for search, snapshots and bulk actions

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Legal/Discovery Counsel, eDiscovery Practitioner
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E13-T03

**Generate privilege logs in configurable formats**  
Labels: `role:legal`, `role:ediscovery`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§14; FRCP 26(b)(5)(A). Legal finding 2: logs must be generated, versioned artifacts tied to a production snapshot.

#### Description
Logs from a production or snapshot for documents withheld or redacted for privilege: (1) document-by-document with configurable columns (Priv ID / Bates or slip-sheet Bates, Date, Author, From, To, CC, BCC, Subject/Filename, Doc Type, Basis, Description, Family range, Redacted/Withheld); (2) metadata-only; (3) categorical by Log Category with counts and date ranges. Column templates per workspace; recorded exclusion rules (e.g. post-complaint outside-counsel communications); CSV and XLSX; privacy-only redactions excluded unless configured (separate redaction log).

#### Acceptance criteria
- [ ] Every withheld/redacted-for-privilege document appears exactly once; no produced-in-full document appears
- [ ] Log is versioned with SHA-256, linked to production ID and snapshot ID; regenerating the same version is byte-identical
- [ ] Exclusion rules are recorded in the log metadata

#### Dependencies
- `E13-T01` — Model privilege designations as system fields
- `E12-T05` — Generate production volume outputs and DAT/OPT load files

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Legal/Discovery Counsel, eDiscovery Practitioner
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
Q-20.

---

### E13-T04

**Provide where-produced lookup and Bates cross-reference**  
Labels: `role:legal`, `role:backend`, `P1`, `size:S` · Milestone: M3 - MVP Feature Complete

#### Context
Legal finding 3: clawback speed depends on finding every volume and Bates range in which a document, its duplicates and family were produced.

#### Description
Lookup returning all productions, volumes and Bates ranges for a DocumentId including duplicates and family members; exportable cross-reference.

#### Acceptance criteria
- [ ] Lookup returns all productions/Bates for a document including duplicates, within the matter
- [ ] Cross-reference export is available to authorized roles and audited

#### Dependencies
- `E12-T03` — Allocate Bates numbers with integrity guarantees
- `E09-T02` — Import upstream duplicate and email-thread identifiers

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** Backend
- **Source reviews:** Legal/Discovery Counsel
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / S

---

### E13-T05

**Implement clawback and replacement re-production workflow**  
Labels: `role:legal`, `role:ediscovery`, `P2`, `size:L` · Milestone: M5 - Post-MVP / Deferred

#### Context
FRE 502(b)/(d). Legal finding 3: clawback must be first-class, not ad hoc.

#### Description
Record clawback (date, basis, notice, receiving-party sequestration/destruction confirmation); generate replacement re-production (slip sheet or newly redacted image) reusing original Bates, overlay load file and updated privilege log version; mark clawed-back documents privileged and block future production.

#### Acceptance criteria
- [ ] Clawback record is immutable once submitted and appears in the audit report
- [ ] Replacement production reuses original Bates and links to the original production version
- [ ] Privilege log is regenerated as a new version including clawed-back documents

#### Dependencies
- `E13-T04` — Provide where-produced lookup and Bates cross-reference
- `E13-T03` — Generate privilege logs in configurable formats
- `E12-T07` — Enforce production QC gate, finalization and manifest

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Legal/Discovery Counsel
- **Milestone / phase / size:** M5 - Post-MVP / Deferred / P2 / L

---

### E13-T06

**Add second-level privilege review queue**  
Labels: `role:legal`, `role:ui`, `P2`, `size:M` · Milestone: M5 - Post-MVP / Deferred

#### Context
§14 QC/re-review; legal ticket 'Second-level privilege review queue'.

#### Description
Route 'Needs 2L Review' and first-pass Withhold/Redact to a restricted privilege-review group; only that role can set final Withhold/Redact when 2L is enabled.

#### Acceptance criteria
- [ ] Only privilege reviewers can set final calls when 2L is enabled
- [ ] Report shows first-pass and second-level calls side by side with reviewer identities

#### Dependencies
- `E13-T01` — Model privilege designations as system fields
- `E10-T06` — Deliver review batching and QC workflow

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** UI/UX
- **Source reviews:** Legal/Discovery Counsel
- **Milestone / phase / size:** M5 - Post-MVP / Deferred / P2 / M
