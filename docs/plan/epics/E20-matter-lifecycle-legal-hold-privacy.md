# E20 — Matter Lifecycle, Legal Hold & Privacy

**Labels:** `epic`, `role:legal`, `role:backend`, `role:security`, `role:ui`, `role:devops`, `P1`  
**Starts in:** M3 - MVP Feature Complete  
**Tickets:** 5

## Goal
Prevent spoliation by blocking destruction of held data, support certified destruction at matter end, capture reviewer acknowledgments, and provide privacy controls (PII assist, data residency).

## Baseline sections
§1, §8, §13, §15, §16, §19 (ADR-014)

## Scope / out of scope
**In scope**
- Workspace preservation lock
- Fenced deletion with destruction certificate and retention profiles
- Reviewer attestation
- PII/PHI assist (deferred)
- Data residency (deferred)

**Out of scope**
- Custodian legal-hold notices (deferred §1)

## Contributing roles
- **Roles:** Legal / Discovery Counsel, Backend, Security & Compliance, UI/UX, DevOps / SRE
- **Source reviews:** Legal/Discovery Counsel, Backend/Architecture, Security & Compliance, UI/UX
- **Milestones spanned:** M3 - MVP Feature Complete, M5 - Post-MVP / Deferred

## Exit criteria
- [ ] Any delete/purge on a locked workspace fails and is audited
- [ ] Deletion leaves 0 residual documents in PG, OpenSearch and storage and produces a certificate

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E20-T01](#e20-t01) | Add workspace preservation lock (legal hold) | M3 | S | E04-T05, E14-T01 |
| [E20-T02](#e20-t02) | Delete workspaces defensibly with fencing and destruction certificate | M3 | L | E20-T01, E02-T06, E06-T02, E07-T01, E19-T01, E05-T09 |
| [E20-T03](#e20-t03) | Require reviewer attestation and protective-order acknowledgment | M3 | S | E05-T02, E14-T01 |
| [E20-T04](#e20-t04) | Provide pattern-assisted PII/PHI privacy redaction | M5 | M | E11-T04, E10-T04 |
| [E20-T05](#e20-t05) | Record data residency and processing details per workspace | M5 | M | E07-T01, E19-T01 |

---

### E20-T01

**Add workspace preservation lock (legal hold)**  
Labels: `role:legal`, `role:backend`, `P1`, `size:S` · Milestone: M3 - MVP Feature Complete

#### Context
§15 deletion 'subject to retention/hold policy'; legal finding 11: deleting held data is spoliation risk (FRCP 37(e)).

#### Description
Workspace-level (optionally document-set-level) preservation lock blocking deletion/purge of documents, artifacts, coding history, productions and audit; place/release requires a designated role, reason and optional second approver; lock state visible in header and admin console.

#### Acceptance criteria
- [ ] Any delete/purge API on a locked workspace fails with an explicit error and an audit event
- [ ] Placing/releasing a lock requires role + reason (+ optional second approver) and is audited

#### Dependencies
- `E04-T05` — Expose workspace management API
- `E14-T01` — Build append-only partitioned audit store and writer

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** Backend
- **Source reviews:** Legal/Discovery Counsel, UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / S

#### Notes
Q-23.

---

### E20-T02

**Delete workspaces defensibly with fencing and destruction certificate**  
Labels: `role:backend`, `role:legal`, `role:security`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§15 matter deletion; ADR-014; backend finding 3 (fence in-flight tasks to prevent resurrection); legal: certified destruction within protective-order deadlines.

#### Description
Two-step workflow (request, approval after configurable waiting period) executed as a fenced job: mark Deleting → workers abort at fence points → drain/cancel jobs → drop OpenSearch data → drop PG data/partitions → delete storage prefixes → destroy keys (if per-workspace) → certificate. Retention profiles: purge all vs retain productions, privilege logs and audit.

#### Acceptance criteria
- [ ] Deletion is blocked while a preservation lock exists
- [ ] Deleting with in-flight bulk and index tasks leaves no OpenSearch docs, PG rows or storage objects and no resurrected docs after 5 min
- [ ] Certificate lists matter, requester, approver, date, stores purged with object/record counts and residual items (e.g. backups expiring by date X) and is retained outside the workspace
- [ ] The deletion audit record survives the deletion

#### Dependencies
- `E20-T01` — Add workspace preservation lock (legal hold)
- `E02-T06` — Write ADR-011/012/014: object storage addressing, redaction and page model, retention and deletion lifecycle
- `E06-T02` — Implement Job and JobChunk state machine with leases
- `E07-T01` — Implement index management and logical index resolution
- `E19-T01` — Implement object storage abstraction and provider contract suite
- `E05-T09` — Introduce secret and key-provider abstraction with envelope encryption

#### Roles
- **Owner:** Backend
- **Contributing:** Legal / Discovery Counsel, Security & Compliance
- **Source reviews:** Backend/Architecture, Legal/Discovery Counsel, Security & Compliance, QA & Performance
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
Merges backend 'Workspace deletion lifecycle' and legal 'Defensible matter deletion'. Q-23.

---

### E20-T03

**Require reviewer attestation and protective-order acknowledgment**  
Labels: `role:legal`, `role:ui`, `P1`, `size:S` · Milestone: M3 - MVP Feature Complete

#### Context
Legal finding 14: protective-order 'Exhibit A', confidentiality and conflicts acknowledgments are expected for contract reviewers and AEO access.

#### Description
Workspace-specific acknowledgments required before first access, re-acknowledgment when text changes; AEO access requires a recorded acknowledgment; exportable roster.

#### Acceptance criteria
- [ ] Access is blocked until acknowledgment; acceptance stores text-version hash, user and timestamp
- [ ] Acknowledgment roster is exportable per workspace

#### Dependencies
- `E05-T02` — Implement permission catalogue, workspace roles and policy decision point
- `E14-T01` — Build append-only partitioned audit store and writer

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** UI/UX
- **Source reviews:** Legal/Discovery Counsel
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / S

---

### E20-T04

**Provide pattern-assisted PII/PHI privacy redaction**  
Labels: `role:legal`, `role:backend`, `P2`, `size:M` · Milestone: M5 - Post-MVP / Deferred

#### Context
Legal finding 13: privacy redactions (GDPR, HIPAA, state privacy) and Sedona International Principles.

#### Description
Pattern-assisted identification (SSN, account numbers, emails, phone, DOB) that proposes but never auto-applies redactions; reviewable, bulk-applicable with provenance; separate redaction log.

#### Acceptance criteria
- [ ] Proposals require reviewer confirmation; bulk apply records provenance
- [ ] Burned label per reason is configurable (e.g. 'Redacted – PII')
- [ ] Privacy-only redactions are excluded from the privilege log unless configured

#### Dependencies
- `E11-T04` — Implement non-destructive redactions API and redaction tool
- `E10-T04` — Build bulk coding job and worker

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** Backend
- **Source reviews:** Legal/Discovery Counsel
- **Milestone / phase / size:** M5 - Post-MVP / Deferred / P2 / M

#### Notes
Q-24.

---

### E20-T05

**Record data residency and processing details per workspace**  
Labels: `role:legal`, `role:devops`, `P2`, `size:M` · Milestone: M5 - Post-MVP / Deferred

#### Context
§8 placement, §16 storage; legal finding 13 cross-border transfers.

#### Description
Workspace data-residency region enforced at provisioning (index placement, storage, backups) and a record of processing (controller, legal basis, transfer mechanism, custodian jurisdictions).

#### Acceptance criteria
- [ ] A workspace cannot be created in a region whose storage or search cluster lies outside the selected residency
- [ ] Report lists every physical location holding the workspace's data

#### Dependencies
- `E07-T01` — Implement index management and logical index resolution
- `E19-T01` — Implement object storage abstraction and provider contract suite

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** DevOps / SRE
- **Source reviews:** Legal/Discovery Counsel
- **Milestone / phase / size:** M5 - Post-MVP / Deferred / P2 / M

#### Notes
Q-24.
