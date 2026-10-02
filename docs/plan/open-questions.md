# Open Questions for the Product Owner

These are the product-owner questions from the seven role reviews, deduplicated and merged. Tickets cite them as `Q-xx`. Until the PO decides, each question's **suggested default** is the working assumption. If the PO decides differently, the tickets listed as affected must be updated.

Raised-by abbreviations: UI = UI/UX, BE = Backend, LEG = Legal/Discovery Counsel, EDS = eDiscovery Practitioner, SEC = Security, QA = QA & Performance, OPS = DevOps/SRE.

**Priority:** the eight questions marked ★ block M0–M2 work or the ADR-004 decision. Answer them first.

## Summary

| ID | Question (short) | Affected epics | Raised by |
|---|---|---|---|
| Q-01 ★ | Hosting/trust model: self-hosted vs hosted; is Lite for real data? | E02, E05, E19 | BE, LEG, SEC, OPS |
| Q-02 | Workspace count and size distribution | E02, E07, E18 | BE |
| Q-03 ★ | Reference benchmark hardware, budget, ownership, publication | E17, E18 | QA, BE, OPS |
| Q-04 ★ | Gate sign-off and numeric definitions | E17, E18 | QA |
| Q-05 | Durability settings for benchmarks | E17, E18 | QA |
| Q-06 | Real-matter statistics for generator defaults | E17 | QA |
| Q-07 ★ | Interactive vs bulk conflict rule | E02, E10 | BE |
| Q-08 ★ | Production/export reproducibility scope | E02, E12 | BE, LEG |
| Q-09 | Dedupe policy in MVP | E02, E09 | BE, EDS |
| Q-10 | Staleness tolerance and security-projection lag SLO | E05, E07, E16 | BE, SEC, UI |
| Q-11 | Security-affecting fields and document-level restrictions in MVP | E05, E13 | BE |
| Q-12 ★ | Are stale-hit snippets/metadata protected content? | E05, E07, E16, E18 | UI, SEC |
| Q-13 | Ethical-wall semantics | E05, E14 | SEC, LEG |
| Q-14 | Family propagation of privilege and coding | E05, E09, E16 | SEC, UI |
| Q-15 | Export authorization when access is revoked mid-job | E05, E12 | SEC |
| Q-16 | Search audit content and audit retention | E02, E14 | SEC, LEG |
| Q-17 | Tamper evidence in first release? | E02, E14 | SEC, LEG |
| Q-18 | Native download/print defaults; reviewer watermark | E05 | SEC |
| Q-19 | Jurisdictions and ESI protocol templates | E12, E13 | LEG |
| Q-20 ★ | Privilege log format and scope in MVP | E13 | LEG, EDS, BE |
| Q-21 | Production depth and image format in MVP | E12 | BE, EDS |
| Q-22 | Native/text/term-based redaction scope | E11, E12 | LEG, EDS, UI |
| Q-23 | Matter-end defaults and approvers; hold scope | E02, E20 | LEG |
| Q-24 | Cross-border/privacy requirements | E20 | LEG |
| Q-25 | Legal disclaimer | E01 | LEG |
| Q-26 | Import source priority and LFP | E08, E12 | EDS |
| Q-27 | Identity model for ControlNumber and Bates | E02, E04 | EDS |
| Q-28 | Time-zone and locale policy | E08, E15 | EDS, UI |
| Q-29 | Extracted text cap | E02, E07, E08 | BE, EDS |
| Q-30 | STR reproducibility and syntax familiarity | E07 | EDS |
| Q-31 | Overlay governance | E08 | EDS |
| Q-32 | Grid totals and paging | E16 | UI |
| Q-33 | Review session stability | E10, E16 | UI |
| Q-34 | Bulk undo and confirmation thresholds | E10, E16 | UI |
| Q-35 | Push vs polling for progress | E06, E15 | UI |
| Q-36 | Native viewing before the renderer is chosen | E11, E16 | UI |
| Q-37 | Accessibility, browser and localisation commitments | E03, E15 | UI, QA |
| Q-38 | Object-storage license policy | E01, E19 | OPS |
| Q-39 | Minimum Lite hardware and platforms | E19 | OPS |
| Q-40 | RPO/RTO scope | E18, E19 | OPS |
| Q-41 | Supported deployment targets and Kubernetes timing | E19 | OPS |
| Q-42 | Observability backend: bundled or bring-your-own | E19 | OPS |
| Q-43 | Release cadence and support policy | E01 | OPS |

---

## Deployment, scale and benchmarking

### Q-01 ★ Hosting and trust model — ANSWERED (see decisions.md)
- **Question:** Is the MVP self-hosted per organization (law firm/corporate) or a multi-tenant hosted service? Is the Lite profile meant for real client data (TLS everywhere, RLS, sandboxed renderer, malware scanning, OpenSearch security plugin) or for evaluation only, with documented reduced guarantees?
- **Why it matters:** The answer drives shared-index placement, how strict RLS must be, the per-workspace key roadmap, GDPR processor status, and the Lite security defaults.
- **Affected epics:** E02, E05, E19. **Raised by:** BE (Q6), LEG (Q5), SEC (Q8), OPS (Q8).
- **Suggested default:** Self-hosted, single organization with many workspaces. Lite is for evaluation/development and has documented reduced guarantees. Full is the production profile with TLS, the security plugin, RLS and the sandbox. The architecture keeps multi-tenant boundaries regardless.

### Q-02 Workspace count and size distribution
- **Question:** About how many workspaces will one installation hold, and how are their sizes distributed?
- **Why it matters:** It sets the shared vs dedicated index thresholds (§8) and decides whether hash partitioning (§6) is worth its complexity.
- **Affected epics:** E02, E07, E18. **Raised by:** BE (Q7).
- **Suggested default:** ≤ 1,000 workspaces per installation, most under 1M documents. Shared index below 5M documents or 50 GB, dedicated above that. All thresholds are configurable.

### Q-03 ★ Reference benchmark hardware, budget and publication — ANSWERED (see decisions.md)
- **Question:** Which cloud SKUs or on-prem spec define the §29 enterprise reference? What is the budget per 1M/10M run and per month? Who owns it, and may results and environment details be published?
- **Why it matters:** The ADR-004 spike (M2) and the 10M validation (M4) cannot start without it. Estimated need: 3 OpenSearch data nodes on NVMe, PG primary + replica, 2–4 load generators, multi-TB storage, 8–24 h per run.
- **Affected epics:** E17, E18. **Raised by:** QA (Q1), BE (Q10), OPS (Q4).
- **Suggested default:** Ephemeral cloud infrastructure provisioned via IaC (`E17-T03`), with a pre-approved budget per run. Results are published publicly with full manifests. No customer data is involved.

### Q-04 ★ Gate sign-off and numeric definitions
- **Question:** Who signs off the frozen gates, query taxonomy and think-time model? Can gates be relaxed after calibration, or only tightened? What fixed bulk rate applies to the degradation gates? What counts as a "material, repeatable advantage" (§26), and what counts as "failing materially at 10M" (§29)?
- **Why it matters:** Without these numbers the ADR-004 decision cannot be made mechanically or defended later.
- **Affected epics:** E17, E18. **Raised by:** QA (Q2, Q3, Q5, Q6).
- **Suggested default:** Sign-off by the PO and lead architect. Gates may only be tightened after calibration. Degradation gates run at the calibrated maximum rate of the weakest candidate, and ≥ 10K docs/s is measured separately as a ceiling. A material advantage is ≥ 20% p95 improvement on gated classes or ≥ 1.5× bulk throughput, consistent across all repetitions. A material 10M failure is any missed §26 gate, > 50% p95 growth from 1M to 10M, or a super-linear disk/merge trend.

### Q-05 Benchmark durability settings
- **Question:** Must reference runs use production-grade durability (PG `synchronous_commit=on` with a sync replica, OpenSearch translog `request`, replicas ≥ 1)?
- **Why it matters:** Results under relaxed settings are not comparable to the §17 RPO target.
- **Affected epics:** E17, E18. **Raised by:** QA (Q7).
- **Suggested default:** Yes for reference runs. Relaxed settings are allowed only on the nightly developer profile and must be recorded in the manifest.

### Q-06 Corpus realism
- **Question:** Can anonymised real-matter statistics be obtained (family-size distribution, duplicate types, text-size percentiles, field cardinality), or is §29 the only authority?
- **Why it matters:** ADR-004 results may not transfer to real matters.
- **Affected epics:** E17. **Raised by:** QA (Q4).
- **Suggested default:** Use §29 plus the eDiscovery practitioner defaults in `E17-T01`. Re-weight if real statistics become available.

## Consistency, coding and search semantics

### Q-07 ★ Interactive vs bulk conflict rule — ANSWERED (see decisions.md)
- **Question:** A reviewer changes a field interactively while a bulk job targeting the same field is running. Which value wins in PostgreSQL: the last commit, or does the bulk job skip documents edited after its snapshot?
- **Why it matters:** This is a product rule as well as a technical one, and it shapes provenance reporting and the bulk worker's SQL.
- **Affected epics:** E02, E10. **Raised by:** BE (Q1).
- **Suggested default:** Last commit wins per field, with full CodingEvent provenance. The bulk job report counts documents that were edited after the snapshot.

### Q-08 ★ Export/production reproducibility scope
- **Question:** Must re-running an export or production reproduce the content as it was at snapshot time, or only the membership (current content of the frozen IDs)?
- **Why it matters:** §22 guarantees membership only. Legal defensibility needs a frozen specification and the state of the redactions and privilege decisions that were used.
- **Affected epics:** E02, E12. **Raised by:** BE (Q2), LEG (finding 9).
- **Suggested default:** Freeze membership, the production specification, the redaction-set version and the coding-state version for privilege/confidentiality decisions (`E12-T02`). Other metadata uses current values, and any difference is reported.

### Q-09 Dedupe policy in MVP
- **Question:** In the MVP, does opportuniTY only honour upstream dedupe (import AllCustodians and duplicate groups), or must it also compute duplicate groups itself? If it computes them, is the default scope global or custodial, and which hash defines an email duplicate? Is suppression needed in review or export?
- **Why it matters:** It affects the import mapping, ADR-009 and the grid and export behaviour.
- **Affected epics:** E02, E09. **Raised by:** BE (Q3), EDS (Q3).
- **Suggested default:** Honour upstream dedupe by default. Optional computed grouping is family-level with global scope and uses the upstream `DedupeHash`/email hash when present, otherwise SHA-256. Grouping is informational only: no suppression in the MVP.

### Q-10 Staleness tolerance and security-projection lag SLO
- **Question:** Are visibly stale counts with a freshness banner acceptable during bulk indexing (§28), or must some workflows, such as privilege review, block until the index is current? What is the maximum stale window after privilege or wall changes, and must counts be exact during that window? Should reviewers see raw generation numbers?
- **Why it matters:** It shapes the UI design, the priority-lane SLO and the ADR-004 gates.
- **Affected epics:** E05, E07, E16. **Raised by:** BE (Q4), SEC (Q3), UI (Q7).
- **Suggested default:** A banner plus "approximate" labels is acceptable, with an optional "wait until current" for privilege workflows. Security-projection lag SLO is ≤ 5 s p95, and counts during that window are labelled approximate. Raw generations are shown only to admins and support.

### Q-11 Security-affecting fields and document-level restrictions in MVP
- **Question:** Which fields are security-affecting in the MVP, and are document-level restrictions in MVP scope or only workspace RBAC (§15 says "optional")?
- **Why it matters:** It determines the scope of `E05-T06` and of the privilege model.
- **Affected epics:** E05, E13. **Raised by:** BE (Q5).
- **Suggested default:** Privilege status, confidentiality designation (AEO) and ethical-wall membership are security-affecting. Document-level restrictions ship in M3.

### Q-12 ★ Are stale-hit snippets and metadata protected content?
- **Question:** Do grid column values, snippets and highlight fragments from OpenSearch count as protected content under §24?
- **Why it matters:** If yes, every page of hits must be post-filtered against PG. That adds latency and changes the ADR-004 benchmark.
- **Affected epics:** E05, E07, E16, E18. **Raised by:** UI (Q3), SEC (finding 5).
- **Suggested default:** Yes. Post-filter each returned page (a batched PG check, ≤ 20 ms p95 for 100 IDs) and include the cost in the benchmark.

### Q-13 Ethical-wall semantics
- **Question:** Do walls apply to users only or also to IdP groups? Do they hide documents entirely (including from counts and facets) or only block content? Can a Workspace Admin be walled off, or is a separately audited break-glass role required?
- **Affected epics:** E05, E14. **Raised by:** SEC (Q1), LEG (finding 12).
- **Suggested default:** Walls apply to users and groups and hide documents entirely, including from counts. Admins can be walled. Break-glass is a separate role that is audited separately.

### Q-14 Family propagation of privilege and coding
- **Question:** When a parent email is coded Privileged/Withhold, do attachments inherit the restriction (for reviewers, for exports, or neither)? Is "apply to family/duplicates" in the MVP, is it automatic for some fields, and above what size does it become a bulk job?
- **Affected epics:** E05, E09, E16. **Raised by:** SEC (Q2), UI (Q4).
- **Suggested default:** No automatic inheritance. Propagation is an explicit action with a conflict preview. Above 1,000 documents it becomes a bulk job. Production QC flags inconsistent families.

### Q-15 Export authorization when access is revoked mid-job
- **Question:** If a user's access is revoked between export submission and execution, should the export fail entirely, exclude the denied documents with a report, or continue under the original authorization?
- **Affected epics:** E05, E12. **Raised by:** SEC (Q6).
- **Suggested default:** Exclude the denied documents with an exception report. A per-workspace "strict" option fails the whole job instead.

## Audit, security policy and legal scope

### Q-16 Search audit content and audit retention
- **Question:** Must executed search query text be stored in the audit log, or hashed or redacted? How long is audit retained after a matter closes?
- **Affected epics:** E02, E14. **Raised by:** SEC (Q4), LEG (Q6).
- **Suggested default:** Store query text, readable only with `Audit.Read`. Audit survives workspace deletion for a configurable retention period (default 7 years).

### Q-17 Tamper evidence in the first release
- **Question:** Is tamper-evident audit (hash chain + signed checkpoints, later WORM) required for the first public release?
- **Affected epics:** E02, E14. **Raised by:** SEC (Q5), LEG (Q6).
- **Suggested default:** Hash chain and signed checkpoints in M3 (`E14-T03`). WORM archival post-MVP (`E14-T07`).

### Q-18 Native download/print defaults and reviewer watermark
- **Question:** Should native download and print be off by default for the Reviewer role? Is a dynamic reviewer watermark a v1 requirement?
- **Affected epics:** E05. **Raised by:** SEC (Q7).
- **Suggested default:** Download and print off for Reviewer and on for Admin and Production Manager. Watermarking is post-MVP.

### Q-19 Jurisdictions and ESI protocol templates
- **Question:** Is the initial target US federal civil litigation only, or also state courts, regulatory responses (Second Requests, CIDs, SEC) and non-US proceedings? Which model orders and protocols should ship as templates, and are DOJ/FTC field lists in scope for the MVP?
- **Affected epics:** E12, E13. **Raised by:** LEG (Q1, Q2).
- **Suggested default:** US federal civil litigation, with generic configurable templates (default DAT field set, document-by-document log). DOJ/FTC specs come post-MVP.

### Q-20 ★ Privilege log format and scope in MVP
- **Question:** Is privilege-log generation part of the MVP? If so, which formats: categorical, metadata-only or document-by-document, and with which column templates?
- **Affected epics:** E13. **Raised by:** LEG (Q3), EDS (Q7), BE (Q9).
- **Suggested default:** M3 ships document-by-document and metadata-only logs with configurable column templates. Categorical logs are in the same ticket if the timebox allows, otherwise the next minor release.

### Q-21 Production depth and image format in MVP
- **Question:** Does the MVP need full production (Bates, burned redactions, endorsements, placeholders, privilege log), or is export plus basic Bates enough? Is TIFF G4 + JPG-for-color required, or is PDF-per-document acceptable? Is color detection needed?
- **Affected epics:** E12. **Raised by:** BE (Q9), EDS (Q5).
- **Suggested default:** Full production in M3 with TIFF G4 300 DPI and JPG chosen by file type rather than by detection, plus natives with slip sheets, text, placeholders and DAT/OPT. PDF and LFP come post-MVP.

### Q-22 Redaction scope
- **Question:** Is rectangle-on-image enough for the MVP, or are text-mode redaction, term-based "redact all hits" and native (cell-level) spreadsheet redaction required? May redacted documents ever be produced natively?
- **Affected epics:** E11, E12. **Raised by:** LEG (Q4), EDS (Q6), UI (Q9).
- **Suggested default:** Rectangle-on-image only. Redacted documents are always imaged and never produced natively. Term-based and native redaction come post-MVP.

### Q-23 Matter-end defaults, approvers and hold scope
- **Question:** Should deletion default to "purge all" or to "retain productions, privilege logs and audit; purge review data"? Who may approve destruction? Is a workspace-level preservation lock enough for the MVP?
- **Affected epics:** E02, E20. **Raised by:** LEG (Q7, Q8).
- **Suggested default:** Retain productions, logs and audit by default. Deletion needs two-person approval (requester plus approver with a designated role). The workspace-level lock is sufficient for the MVP.

### Q-24 Cross-border and privacy
- **Question:** Are EU/UK custodians expected in early users' matters, requiring data residency and pseudonymization before transfer to US review?
- **Affected epics:** E20. **Raised by:** LEG (Q9).
- **Suggested default:** Not in the MVP. Record a residency field per workspace now and ship enforcement and PII assist post-MVP.

### Q-25 Legal disclaimer
- **Question:** Should the project ship a standard disclaimer that it provides no legal advice and that users remain responsible for privilege, production and automated-coding validation?
- **Affected epics:** E01 (README/about page). **Raised by:** LEG (Q10).
- **Suggested default:** Yes, in the README, the documentation and the UI "About" page.

## Import, identity and search features

### Q-26 Import source priority and LFP
- **Question:** Which processing tools' exports must import cleanly at MVP (Relativity, Nuix, Reveal/Brainspace, Everlaw, Venio, GoldFynch)? Is LFP needed in the MVP?
- **Affected epics:** E08, E12. **Raised by:** EDS (Q1).
- **Suggested default:** Relativity, Nuix and generic Concordance/CSV presets and alias maps. LFP post-MVP.

### Q-27 Identity model
- **Question:** Is ControlNumber the immutable internal identifier, with received-production Bates kept as separate fields, or may Bates be the key for opposing-party productions? Can a control number ever be renamed?
- **Affected epics:** E02, E04. **Raised by:** EDS (Q2).
- **Suggested default:** ControlNumber is immutable and unique per workspace on a normalized form, with an optional import prefix. Received BegBates/EndBates are typed fields. No renaming.

### Q-28 Time-zone and locale policy
- **Question:** Is there one matter display time zone or one per user? Must productions output dates in the time zone set by the ESI protocol? Which locales and date formats ship in the MVP?
- **Affected epics:** E08, E15. **Raised by:** EDS (Q4), UI (Q10).
- **Suggested default:** A matter display time zone with a per-user override. Productions use the time zone in their specification. MVP locales are en-US and en-GB formats.

### Q-29 Extracted text cap
- **Question:** What is the maximum indexed text size per document? Is it acceptable for search and highlighting to cover only the indexed portion if the document is flagged?
- **Affected epics:** E02, E07, E08. **Raised by:** BE (Q8), EDS (Q8).
- **Suggested default:** Index the first 10 M characters (configurable; eDiscovery suggested up to 50 MB), set `TextTruncated=true`, keep the full text in object storage, and show a viewer banner.

### Q-30 STR reproducibility and syntax familiarity
- **Question:** Must search term reports be exchangeable with opposing counsel (snapshot + generation stamp + export format)? Should the syntax mirror dtSearch/Relativity (`W/n`, `PRE/n`, `!`)?
- **Affected epics:** E07. **Raised by:** EDS (Q9).
- **Suggested default:** Yes, STRs are snapshot-bound and exportable. `W/n` ships in M1 and `PRE/n` and `!` post-MVP (`E07-T12`).

### Q-31 Overlay governance
- **Question:** Who may run overlays? Must overlays of coding/privilege fields be blocked to protect review provenance (§27)?
- **Affected epics:** E08. **Raised by:** EDS (Q10).
- **Suggested default:** Workspace Admin only. Coding and privilege fields are not overlayable unless an admin explicitly enables it per import, which is audited.

## Reviewer UX

### Q-32 Grid totals and paging
- **Question:** Are approximate counts ("~1.2M", "≥ 10,000") acceptable for large result sets? Do users need "jump to page N" or "last page"?
- **Affected epics:** E16. **Raised by:** UI (Q1).
- **Suggested default:** Approximate counts above 10,000, with a "count exactly" action. No arbitrary deep page jumps. Sorting toward the end serves the "last page" need.

### Q-33 Review session stability
- **Question:** Should a reviewer's next/previous order be frozen for the whole session (a lightweight session snapshot), or may it re-query and shift as others code?
- **Affected epics:** E10, E16. **Raised by:** UI (Q2).
- **Suggested default:** A live PIT cursor with a "results refreshed" notice in the MVP. Frozen review sets come from review batches post-MVP.

### Q-34 Bulk undo and confirmation thresholds
- **Question:** Should a completed bulk coding job be revertible from CodingEvent history? Who may do it? What threshold requires a typed confirmation?
- **Affected epics:** E10, E16. **Raised by:** UI (Q5).
- **Suggested default:** No undo in the MVP (an admin-only "revert job" is a post-MVP candidate). Typed confirmation above 10,000 documents or for any security-affecting field.

### Q-35 Progress delivery
- **Question:** May the UI use a push channel (SSE/SignalR through the API pool) for job and watermark updates, or must v1 poll?
- **Affected epics:** E06, E15. **Raised by:** UI (Q6).
- **Suggested default:** SSE with a polling fallback (≥ 5 s interval).

### Q-36 Native viewing before the renderer is chosen
- **Question:** Until native rendering is chosen (§33), is "download native + extracted text + imported images/PDF" acceptable for MVP review? Do any formats need in-browser rendering on day one?
- **Affected epics:** E11, E16. **Raised by:** UI (Q8).
- **Suggested default:** Yes. No in-browser native rendering in the MVP.

### Q-37 Accessibility, browser and localisation commitments
- **Question:** Can the project commit formally to WCAG 2.2 AA (and publish a VPAT/ACR)? Which browsers must the E2E suite cover? Is UI latency a gated metric or diagnostic only?
- **Affected epics:** E03, E15. **Raised by:** UI (Q10), QA (Q8).
- **Suggested default:** WCAG 2.2 AA with an ACR at 1.0. Latest two versions of Chrome, Edge, Firefox and Safari. UI latency is diagnostic, with budgets enforced in CI.

## Operations and release

### Q-38 Object-storage license policy
- **Question:** May an AGPL object store (MinIO, Garage) ship unmodified in the official Compose bundle, or must the default be permissively licensed? Who owns the third-party license policy?
- **Affected epics:** E01, E19. **Raised by:** OPS (Q1).
- **Suggested default:** The default is permissively licensed (filesystem provider for Lite plus a verified Apache-2.0 S3 store for Full). AGPL stores are allowed only as documented, optional, unmodified external services. The PO owns the license policy.

### Q-39 Minimum Lite hardware and platforms
- **Question:** What is the minimum machine for Lite (8 GB or 16 GB)? Must it run on Windows/WSL2 and Apple Silicon?
- **Affected epics:** E19. **Raised by:** OPS (Q2).
- **Suggested default:** 16 GB recommended and 8 GB minimum with the combined worker. Linux, macOS (Apple Silicon) and WSL2 supported. Multi-arch images.

### Q-40 RPO/RTO scope
- **Question:** Do RPO ≤ 5 min and RTO ≤ 1 h (§17) apply to Lite/self-hosted Compose or only to Full? Is cross-region/off-site DR in scope for v1?
- **Affected epics:** E18, E19. **Raised by:** OPS (Q3).
- **Suggested default:** Full profile only. Off-site DR is documented as an operator responsibility in v1.

### Q-41 Supported deployment targets and Kubernetes timing
- **Question:** Is v1 Compose only, or also single-host Docker Swarm? When does Helm/Kubernetes become officially supported?
- **Affected epics:** E19. **Raised by:** OPS (Q5).
- **Suggested default:** Compose only for v1. Helm after the 10M validation (M5).

### Q-42 Observability backend
- **Question:** Should the project ship its own Grafana/Prometheus/Loki/Tempo stack, or only an OTel Collector configuration? Is OpenSearch acceptable as the log backend?
- **Affected epics:** E19. **Raised by:** OPS (Q6).
- **Suggested default:** Ship an OTel Collector configuration plus an optional `observability` profile with the Grafana stack. Do not use the product OpenSearch cluster for logs.

### Q-43 Release cadence and support policy
- **Question:** What release cadence, and how many versions back receive security fixes? Must upgrades be zero-downtime from v1.0?
- **Affected epics:** E01. **Raised by:** OPS (Q7).
- **Suggested default:** Monthly minor releases, with security fixes for N and N-1. Short maintenance-window upgrades before 1.0, and expand/contract migrations so zero-downtime is possible afterwards.
