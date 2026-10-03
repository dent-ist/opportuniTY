# Legal / Discovery Counsel Review — opportuniTY Architecture Baseline

**Reviewer perspective:** US litigation / discovery counsel (FRCP 26(b)(5), 26(f), 34(b)(2)(E), 37(e); FRE 502(b)/(d); protective orders; ESI protocols; cross-border data transfer).
**Document reviewed:** `docs/architecture/architecture-baseline.md` (§1–§35).
**Framing:** The baseline is a strong engineering foundation (PostgreSQL authority, materialized snapshots for productions §22, provenance §27, authoritative re-authorization §24). The gaps below are not about scalability. They are about whether a producing party can **stand behind** the platform's output in a meet-and-confer, a motion to compel, a clawback dispute, or a sworn declaration about its review and production process.

---

## Review findings

1. **Privilege workflow is a field, not a process (§14, §15, §24).** The baseline names "privilege-log support" and "privilege-sensitive fields" but has no privilege domain model: privilege basis (ACP / WP / common interest / other), privilege status distinct from responsiveness (Withhold / Redact / Produce / Not privileged), log description, author/recipient attorney identification, or second-level privilege review. Without that model, FRCP 26(b)(5)(A) compliance depends on spreadsheets kept outside the system, which breaks provenance and reproducibility.

2. **Privilege log content is undefined (§14).** ESI protocols increasingly require either document-by-document logs with specific metadata columns (Date, Author, From/To/CC/BCC, Subject/Filename, Privilege Type, Description, Bates/Priv ID, family relationship) or **categorical / metadata-only logs** and carve-outs (e.g., post-complaint communications with outside counsel). The baseline must make the log a generated, versioned artifact tied to a production snapshot, with configurable columns and consistency checks (every withheld/redacted-for-privilege document appears on the log; nothing on the log was produced unredacted).

3. **No clawback / inadvertent production workflow (§14, §15).** FRE 502(b) and any 502(d) order make the speed and documentation of a clawback legally material. The spec has no notion of: identifying every production volume and Bates range in which a document (and its duplicates/family members) was produced; generating a clawback notice; recording the receiving party's sequestration certification; issuing a replacement (slip-sheet or redacted) re-production with the same or overlay Bates. This must be first-class, not ad hoc.

4. **Redaction "burn" is asserted, not verified (§13).** §13 says production "burns redactions." Defensibility requires proof that (a) redacted pixels are flattened into the image, (b) the extracted/OCR text for redacted pages is regenerated or redacted (the most common real-world leak is the unredacted .txt shipped in the load file), (c) natives of redacted documents are withheld/replaced by images, (d) PDF metadata, annotations, hidden layers, and embedded objects are stripped. No automated post-production verification is specified.

5. **Native production of documents with redactions or privilege is not governed (§13, §14).** Spreadsheets and other natives are routinely ordered to be produced natively. The spec needs an explicit rule that redacted or partially privileged documents cannot be produced natively unless a native-redaction method is used and documented, and a QC gate that blocks the production otherwise.

6. **Confidentiality designations/endorsements are not modeled (§14, §24).** "Endorsements" appear only as a word. Protective orders require designations (e.g., CONFIDENTIAL, HIGHLY CONFIDENTIAL – ATTORNEYS' EYES ONLY) to be stamped on each page, recorded in the load file, and carried consistently across families and duplicates. Designation changes after production (de-designation, challenge outcomes) need a re-production / overlay workflow and audit trail.

7. **Bates integrity rules are missing (§14, §22).** No requirement that Bates numbers are unique per matter/prefix, gapless within a volume (or that gaps are deliberate and logged, e.g., withheld slip-sheets), immutable once a production is finalized, and never reissued after a production is voided. Concurrency and retries (§2.4, §11) make duplicate or skipped numbers a realistic failure mode; Bates allocation must be part of the idempotency design.

8. **Family integrity is "family-aware" but not enforced (§14, §20).** Most ESI protocols require production of complete families, with withheld members slip-sheeted ("Document Withheld – Privileged") and BegAttach/EndAttach preserved. The spec should define family completeness checks, privilege/responsiveness conflict detection across a family, and how duplicates of privileged documents are kept consistent (inconsistent privilege calls across duplicates is a frequent waiver argument).

9. **Production reproducibility stops at membership (§10, §22).** Materialized snapshots freeze *which* documents, but a defensible production also requires freezing *how*: the production specification (image format, DPI, load-file layout, field list, endorsement text, redaction set version, coding state used for privilege/confidentiality decisions, renderer version). Otherwise re-running the production after redaction edits or renderer upgrades silently produces different output with the same Bates numbers.

10. **Audit trail is not yet declaration-grade (§15, §33).** Tamper evidence is deferred (§33). For a FRCP 26(g) certification or a 37(e) dispute, counsel must be able to attest who reviewed what, when, what they saw (view events, not just edits), what search terms and snapshots defined review populations, and that logs were not altered. The spec should require view/download/export events, search-term and hit-report capture, and at minimum hash-chaining (cheap, can be done in v1) even if WORM archival is deferred.

11. **Matter deletion vs legal hold and retention (§15, §1).** §15 allows matter deletion "subject to retention/hold policy," but collection/legal hold is deferred (§1) and no retention model exists. Deleting a workspace that is still under a litigation hold, appeal window, or a protective order's return/destroy obligation is spoliation risk (FRCP 37(e)). Conversely, protective orders often require *certified destruction* within 60 days of case end. The platform needs a hold flag that blocks deletion, a documented deletion workflow, and a deletion certificate.

12. **Ethical walls are mentioned but not specified (§15, §24).** Ethical walls must apply to every access path (search hits, viewer, exports, productions, bulk coding, admin impersonation, saved-search results, audit report viewing), and the audit trail must demonstrate the wall held. §24's authoritative re-check is the right mechanism; the requirement and the demonstrating report are missing.

13. **Privacy / cross-border (GDPR, state privacy, HIPAA) is absent.** No PII/PHI detection or redaction reason codes, no data-residency control on object storage/OpenSearch placement (§8, §16), no data-minimization or pseudonymization support for EU custodians, and no per-workspace record of transfer basis. Sedona Conference International Principles and GDPR Art. 6/49 transfers routinely require privacy redactions and a documented processing record.

14. **User attestation and reviewer accountability (§14, §27).** Coding provenance exists, but there is no reviewer attestation (training acknowledgment, protective-order acknowledgment / "Exhibit A" signature for outside reviewers, conflicts check), which courts and clients expect for contract review teams and AEO access.

15. **AI/TAR deferral needs a guardrail statement (§1, §20, §33).** When AI/TAR arrives, use of it may need disclosure and validation (recall/elusion testing) under the ESI protocol. The baseline should state now that any automated coding will be recorded with a distinct provenance actor type so it can be identified, validated, and disclosed later — cheap now, expensive to retrofit.

---

## Proposed epics and tickets

### EPIC: Privilege Review and Privilege Log
Model privilege as a first-class, auditable workflow distinct from responsiveness, and generate privilege logs that satisfy FRCP 26(b)(5)(A) and the common ESI-protocol log formats (document-by-document, metadata-only, categorical). Ensure privilege calls are consistent across families and duplicates.
**Baseline sections:** §5, §6, §14, §15, §22, §24, §27.

#### Privilege designation data model
- **Role:** Legal (requirements)
- **Description:** Provide system privilege fields separate from responsiveness: Privilege Status (Not Privileged / Withhold / Redact / Needs 2L Review), Privilege Basis (multi-choice: Attorney-Client, Work Product, Common Interest, Other-configurable), Privilege Description (free text, log-ready), Attorneys Involved (list of names/flags), and Log Category (for categorical logs). Changes are security-affecting coding under §24.
- **Acceptance criteria:**
  - Fields exist in every workspace by default and cannot be deleted, only extended.
  - Setting status to Withhold or Redact without a Basis is rejected by validation.
  - Every change creates a CodingEvent (§27) with actor, timestamp, prior and new value.
  - A change to Withhold immediately blocks the document from inclusion in any unfinalized production (authoritative check per §24), independent of search-index freshness.
- **Dependencies:** §27 coding model; §24 security consistency.
- **Phase:** P0
- **Size:** M

#### Family and duplicate privilege consistency checks
- **Role:** Legal (requirements)
- **Description:** Detect and report conflicts where (a) members of the same family have privilege/responsiveness calls that would produce an incomplete or misleading family, and (b) exact duplicates (same DuplicateGroupId/hash) have inconsistent privilege calls.
- **Acceptance criteria:**
  - On-demand report lists each conflict with DocumentIds, ControlNumbers, field values and the reviewers who set them.
  - A production cannot be finalized while unresolved privilege conflicts exist among its members unless an authorized user records an override with a reason (audited).
  - Optional "propagate privilege call to duplicates" bulk action, logged as a bulk job with provenance.
- **Dependencies:** Privilege data model; DocumentSetSnapshot (§22).
- **Phase:** P1
- **Size:** M

#### Privilege log generation (configurable formats)
- **Role:** Legal (requirements)
- **Description:** Generate a privilege log from a production (or a snapshot) for all documents withheld or redacted for privilege. Support: (1) document-by-document log with configurable columns (Priv ID / Bates or slip-sheet Bates, Date, Author, From, To, CC, BCC, Subject/Filename, Doc Type, Privilege Basis, Description, Family range, Redacted/Withheld); (2) metadata-only log (fields auto-populated from metadata, no description); (3) categorical log grouped by Log Category with counts and date ranges. Export CSV and XLSX.
- **Acceptance criteria:**
  - Every document withheld or redacted for privilege in the production appears exactly once; no produced-in-full document appears.
  - Log is versioned and stored with a SHA-256 checksum, linked to production ID and snapshot ID.
  - Regenerating the log for the same production version produces a byte-identical file.
  - Column templates can be saved per workspace (to match a negotiated ESI protocol).
  - Configurable exclusion rules (e.g., communications with outside litigation counsel after a date) are recorded in the log's metadata.
- **Dependencies:** Privilege data model; Production specification freeze.
- **Phase:** P1
- **Size:** L

#### Second-level privilege review queue
- **Role:** Legal (requirements)
- **Description:** Route documents marked "Needs 2L Review" or first-pass Withhold/Redact to a restricted reviewer group for confirmation before they can be logged.
- **Acceptance criteria:**
  - Only users in a designated privilege-review role can set final Withhold/Redact when 2L is enabled for the workspace.
  - Report shows first-pass and second-level calls side by side with reviewer identities.
- **Dependencies:** Privilege data model; review batches (§14).
- **Phase:** P2
- **Size:** M

### EPIC: Defensible Production (Bates, Endorsements, Redaction Verification, QC, Clawback)
Make every production reproducible, internally consistent and verifiably free of redaction leaks, with immutable Bates allocation and a first-class clawback / re-production workflow supporting FRE 502(b)/(d).
**Baseline sections:** §10, §11, §13, §14, §22, §24.

#### Frozen production specification
- **Role:** Legal (requirements)
- **Description:** A production is defined by a materialized snapshot (§22) plus an immutable specification: Bates prefix/start/padding, image format and DPI, native/image/text rules per file type, load-file formats (DAT/OPT, field list and order, delimiters), endorsement templates, redaction set version, coding-state version for privilege/confidentiality, and renderer/tool versions. Finalizing locks it.
- **Acceptance criteria:**
  - After finalization, the specification and membership cannot be edited; changes require a new production version.
  - Production manifest records all specification values and software versions.
  - Re-running a finalized production yields output whose per-file SHA-256 hashes match the manifest (or an explicit, logged difference report).
- **Dependencies:** §22 materialized snapshots; §11 job model.
- **Phase:** P0
- **Size:** L

#### Bates allocation integrity
- **Role:** Legal (requirements)
- **Description:** Allocate Bates numbers transactionally and idempotently so retries, worker crashes and parallel chunks (§2.4, §11) cannot create duplicates or unexplained gaps. Numbers are unique per matter + prefix across all productions and never reused, even if a production is voided.
- **Acceptance criteria:**
  - Integrity check after every production: no duplicate Bates in the matter; every gap inside a volume is attributed (voided production, slip-sheet) in the manifest.
  - Fault-injection test (kill worker mid-chunk, redeliver messages) produces identical Bates assignment.
  - Bates-to-DocumentId cross-reference is queryable and exportable for the life of the matter.
  - Voiding a production records the voided range and reason; the range is never reissued.
- **Dependencies:** Frozen production specification.
- **Phase:** P0
- **Size:** M

#### Confidentiality designations and endorsements
- **Role:** Legal (requirements)
- **Description:** Workspace-configurable confidentiality designations per protective order (default: None, CONFIDENTIAL, HIGHLY CONFIDENTIAL – AEO). Designation drives page endorsement text/position, a load-file field, and access restrictions (AEO accessible only to designated roles).
- **Acceptance criteria:**
  - Every page of a designated document carries the endorsement; the load file's designation field matches 100% (QC check).
  - Family members inherit the highest designation in the family unless overridden with audit reason (configurable).
  - Designation change after production generates a re-designation report listing affected Bates ranges and supports an overlay load file / re-stamped re-production.
- **Dependencies:** Frozen production specification; §24 security-affecting coding.
- **Phase:** P1
- **Size:** M

#### Redaction burn verification
- **Role:** Legal (requirements)
- **Description:** Automated post-production verification that redactions are irreversible: images flattened, text for redacted documents regenerated from the redacted image (or redacted text) rather than the original extracted text, natives withheld for redacted documents, PDF metadata/annotations/layers/embedded files/XMP stripped.
- **Acceptance criteria:**
  - For each redacted document, the produced text file contains no string that falls under a redaction region in the source text (automated diff against the redacted-region text).
  - No produced PDF contains annotation objects, optional content groups, or text under redaction boxes (verified by programmatic text extraction of the output).
  - Production finalization is blocked if any redacted document has a native in the output, unless a documented native-redaction method is recorded.
  - Verification results are stored in the production manifest and included in the QC report.
- **Dependencies:** §13 rendering/redactions; Frozen production specification.
- **Phase:** P0
- **Size:** L

#### Pre-finalization production QC gate
- **Role:** Legal (requirements)
- **Description:** A required checklist executed before a production can be finalized: privilege screen (no Withhold docs, no unredacted Redact docs), family completeness (withheld members slip-sheeted, BegAttach/EndAttach consistent), designation/endorsement match, redaction verification, page counts image vs OPT, load-file field validation, hash manifest.
- **Acceptance criteria:**
  - Each check returns pass/fail with document-level exceptions.
  - Failures block finalization; an authorized override requires a reason and is audited and printed in the QC report.
  - QC report (PDF/CSV) is retained with the production.
- **Dependencies:** All production tickets above; privilege data model.
- **Phase:** P1
- **Size:** M

#### Clawback and re-production workflow
- **Role:** Legal (requirements)
- **Description:** Given a document (or set), identify every production, volume and Bates range in which it or its duplicates and family members were produced; record the clawback (date, basis, notice sent, recipient sequestration/destruction confirmation); generate a replacement (slip-sheet or newly redacted image) re-production retaining original Bates numbers, plus an overlay load file and an updated privilege log.
- **Acceptance criteria:**
  - "Where produced" lookup returns all productions/Bates for a DocumentId including duplicates, within the matter.
  - Clawback record is immutable once submitted and appears in the audit report.
  - Replacement production reuses original Bates numbers and is linked to the original production version.
  - Privilege log is regenerated as a new version including the clawed-back documents.
  - Clawed-back documents are automatically marked privileged and blocked from future productions.
- **Dependencies:** Bates allocation integrity; privilege log generation.
- **Phase:** P1
- **Size:** L

### EPIC: Audit and Defensibility Reporting
Make the audit trail sufficient to support a FRCP 26(g) certification, a declaration on review methodology, or testimony in a 37(e) dispute, including evidence that ethical walls held.
**Baseline sections:** §5, §9, §15, §22, §24, §27, §33.

#### Complete protected-content access audit
- **Role:** Legal (requirements)
- **Description:** Audit every view, image retrieval, native download, export, production inclusion, print, search execution (query text, snapshot/generation, hit count) and permission change — not only edits. Denied attempts (§24) are logged too.
- **Acceptance criteria:**
  - Each event records actor, workspace, document/object, action, outcome (allow/deny), timestamp (UTC), client IP/session, correlation ID.
  - Integration test: every protected-content endpoint emits an audit event; missing event fails CI.
  - Audit events are not editable or deletable by any application role, including workspace admins.
- **Dependencies:** §15 audit store; §24 authorization.
- **Phase:** P0
- **Size:** M

#### Tamper-evident audit chain (minimum v1)
- **Role:** Legal (requirements)
- **Description:** Even with WORM archival deferred (§33), chain audit records per workspace partition with a hash of the prior record (or periodic signed checkpoints) so later alteration is detectable.
- **Acceptance criteria:**
  - Verification tool reports chain intact / first broken record.
  - Checkpoint hashes exportable for inclusion in a declaration exhibit.
- **Dependencies:** Complete protected-content access audit.
- **Phase:** P1
- **Size:** S

#### Defensibility report pack
- **Role:** Legal (requirements)
- **Description:** On-demand reports for counsel: search-term hit report (per term, unique hits, with family) tied to snapshot/generation; review population and coding summary by reviewer/date; production history (volumes, Bates ranges, counts, hashes, QC results); chain of custody per document (import source/load file, hash at import, derived artifacts, productions).
- **Acceptance criteria:**
  - Each report states the data-as-of point (snapshot ID or search generation, §28) and is reproducible for that point.
  - Hit reports note when the index was not current (generation lag) at execution time.
  - Import hash (MD5/SHA-256 from §5) is shown alongside hash re-verified from object storage.
- **Dependencies:** §22 snapshots; §28 generation watermark; production manifest.
- **Phase:** P1
- **Size:** L

#### Ethical wall enforcement and proof report
- **Role:** Legal (requirements)
- **Description:** Define ethical walls as user/group exclusions applied to documents, custodians or entire workspaces, enforced at every protected path (§24) including saved-search results, exports, bulk jobs, reports and admin functions. Provide a report demonstrating no walled user accessed walled content.
- **Acceptance criteria:**
  - Automated tests attempt access via each path as a walled user; all denied and logged.
  - Report lists all access attempts by walled users to walled content (expected: denials only), for a date range.
  - Wall creation/modification is audited and requires a designated role.
- **Dependencies:** Complete protected-content access audit; §24.
- **Phase:** P1
- **Size:** M

### EPIC: Matter Lifecycle, Legal Hold, Retention and Privacy
Prevent spoliation by blocking destruction of held data, support certified destruction at matter end per protective orders, and provide privacy controls (PII/PHI redaction, data residency) for GDPR and US privacy regimes.
**Baseline sections:** §1, §8, §13, §15, §16, §19 (ADR 14).

#### Legal hold / preservation lock on workspaces
- **Role:** Legal (requirements)
- **Description:** A workspace-level (and optionally document-set-level) preservation lock that blocks deletion of documents, artifacts, coding history, productions and audit records. This is distinct from custodian legal-hold notices (deferred, §1).
- **Acceptance criteria:**
  - Any delete/purge API on a locked workspace fails with an explicit error and an audit event.
  - Placing or releasing a lock requires a designated role, a reason and (optionally) a second approver.
  - Lock state is visible in the workspace header and admin console.
- **Dependencies:** §15 lifecycle; ADR 14.
- **Phase:** P0
- **Size:** S

#### Defensible matter deletion with destruction certificate
- **Role:** Legal (requirements)
- **Description:** Two-step deletion workflow (request, approval after configurable waiting period) covering PostgreSQL, OpenSearch, object storage prefixes, derived artifacts, backups policy statement and keys (§15). Produce a destruction certificate suitable for a protective order's return-or-destroy certification.
- **Acceptance criteria:**
  - Deletion blocked if a preservation lock exists.
  - Certificate lists matter, requester, approver, date, data stores purged with object/record counts, and residual items (e.g., backups expiring by date X) and is retained outside the deleted workspace.
  - Post-deletion verification confirms zero remaining documents in search and storage for the WorkspaceId.
  - Option to retain productions, privilege logs and audit trail while purging review data (configurable retention profile).
- **Dependencies:** Legal hold lock; §15.
- **Phase:** P1
- **Size:** M

#### PII/PHI privacy redaction support
- **Role:** Legal (requirements)
- **Description:** Redaction reason codes for privacy (PII, PHI, Personal Data – GDPR, Non-responsive if permitted by ESI protocol) separate from privilege, with pattern-assisted identification (SSN, account numbers, emails, phone, DOB) that proposes but never auto-applies redactions without reviewer confirmation.
- **Acceptance criteria:**
  - Redaction label printed on the burned redaction box is configurable per reason (e.g., "Redacted – PII").
  - Pattern search results are reviewable and bulk-applicable with provenance.
  - Privilege log excludes privacy-only redactions unless configured; a separate redaction log can be generated.
- **Dependencies:** §13 redactions; redaction burn verification.
- **Phase:** P1
- **Size:** M

#### Data residency and processing record per workspace
- **Role:** Legal (requirements)
- **Description:** Each workspace records its data-residency region and the storage/search/backup locations used, enforced at provisioning (§8 index placement, §16 storage), plus a record of processing fields (controller, legal basis, transfer mechanism, custodian jurisdictions).
- **Acceptance criteria:**
  - Workspace cannot be created in a region whose configured storage or search cluster is outside the selected residency.
  - Report lists all physical locations holding the workspace's data.
- **Dependencies:** §8, §16 deployment profiles.
- **Phase:** P2
- **Size:** M

#### Reviewer attestation and protective-order acknowledgment
- **Role:** Legal (requirements)
- **Description:** Require users to accept workspace-specific acknowledgments (protective order "Exhibit A", confidentiality, conflicts) before first access, with re-acknowledgment when text changes. AEO access requires a recorded acknowledgment.
- **Acceptance criteria:**
  - Access blocked until acknowledgment; acceptance stored with text version hash, user, timestamp.
  - Exportable acknowledgment roster per workspace.
- **Dependencies:** §15 RBAC.
- **Phase:** P1
- **Size:** S

#### Automated-decision provenance marker (AI/TAR readiness)
- **Role:** Legal (requirements)
- **Description:** Coding provenance (§27) distinguishes human reviewer, bulk human action, rule/propagation, and future model-generated decisions, so automated coding can later be isolated, validated and disclosed under the ESI protocol.
- **Acceptance criteria:**
  - CodingEvent has an actor type enum including Human, BulkHuman, SystemRule, Model (reserved).
  - Reports can filter coding by actor type.
- **Dependencies:** §27 coding model.
- **Phase:** P0
- **Size:** S

---

## Open questions for the product owner

1. **Jurisdictions and forums:** Is the initial target US federal civil litigation only, or also state courts, regulatory responses (Second Requests, CIDs, SEC), and non-US proceedings (UK disclosure, EU)? This drives log formats, designation defaults and privacy features.
2. **ESI protocol templates:** Which standard protocols should be supported out of the box (e.g., N.D. Cal. / D. Del. model orders, Sedona-style templates, DOJ/FTC Second Request production specs)? Are DOJ/FTC metadata field lists in scope for MVP productions?
3. **Privilege log format default:** Is a categorical/metadata-only log acceptable as MVP with document-by-document logs in P1, or must full logs ship with the first production capability?
4. **Native redaction:** Will MVP permit native production of redacted spreadsheets (requires native-redaction tooling), or will redacted documents always be imaged?
5. **Users and hosting model:** Will the platform be self-hosted by law firms/corporations (who own compliance) or offered as a hosted service (making the project/host a processor under GDPR and a likely deponent on audit integrity)?
6. **Audit retention and tamper evidence:** How long must audit records be retained after matter close, and is hash-chaining acceptable for v1 in place of WORM storage?
7. **Matter-end obligations:** Should deletion default to "purge all" or "retain productions, privilege logs and audit; purge review data"? Who may approve destruction?
8. **Legal hold scope:** Is the workspace-level preservation lock sufficient for MVP, given custodian legal-hold notices are deferred (§1)?
9. **Cross-border:** Are EU/UK custodians expected in early users' matters, requiring data residency and pseudonymization before transfer to US review?
10. **Disclaimers:** Will the project ship a standard disclaimer that the software does not provide legal advice and that users remain responsible for privilege and production decisions and for validating any future automated coding?
