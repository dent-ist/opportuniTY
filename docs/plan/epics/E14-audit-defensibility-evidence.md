# E14 — Audit & Defensibility Evidence

**Labels:** `epic`, `role:security`, `role:data`, `role:legal`, `role:ui`, `role:ediscovery`, `role:devops`, `P0`  
**Starts in:** M1 - First Vertical Slice  
**Tickets:** 7

## Goal
Make the audit trail declaration-grade: a closed event taxonomy in an append-only store, complete protected-content/search coverage, hash-chained tamper evidence, ethical-wall proof, defensibility reports and compliance evidence.

## Baseline sections
§5, §9, §15, §19 (ADR-013), §22, §24, §27, §28, §33

## Scope / out of scope
**In scope**
- Append-only partitioned audit store
- Coverage of view/download/print/search/permission/denied events
- Hash chain + signed checkpoints + verify CLI
- Audit viewer and document history UI
- Defensibility report pack
- Ethical-wall proof, access reviews, control mapping
- WORM archival (deferred)

**Out of scope**
- SIEM integrations

## Contributing roles
- **Roles:** Security & Compliance, Data (PostgreSQL), Legal / Discovery Counsel, UI/UX, eDiscovery Practitioner, DevOps / SRE
- **Source reviews:** Security & Compliance, Legal/Discovery Counsel, Backend/Architecture, UI/UX
- **Milestones spanned:** M1 - First Vertical Slice, M3 - MVP Feature Complete, M5 - Post-MVP / Deferred

## Exit criteria
- [ ] The app role cannot UPDATE/DELETE audit rows; a protected endpoint without an audit event fails CI
- [ ] The verify CLI detects modification, deletion and reordering of any event

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E14-T01](#e14-t01) | Build append-only partitioned audit store and writer | M1 | M | E02-T07, E04-T01 |
| [E14-T02](#e14-t02) | Complete audit coverage for protected content, search and administration | M3 | M | E14-T01, E05-T04, E07-T05 |
| [E14-T03](#e14-t03) | Hash-chain audit events with signed checkpoints and a verify CLI | M3 | M | E14-T01, E05-T09 |
| [E14-T04](#e14-t04) | Build audit log viewer and document coding history UI | M3 | M | E14-T02, E10-T05, E15-T02 |
| [E14-T05](#e14-t05) | Generate defensibility report pack | M3 | L | E14-T02, E07-T10, E12-T07 |
| [E14-T06](#e14-t06) | Produce ethical-wall proof, access reviews and compliance control mapping | M3 | M | E05-T06, E14-T02 |
| [E14-T07](#e14-t07) | Archive audit checkpoints to immutable object storage | M5 | M | E14-T03, E19-T02 |

---

### E14-T01

**Build append-only partitioned audit store and writer**  
Labels: `role:security`, `role:data`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§15 'partitioned append-only PostgreSQL initially'; ADR-013.

#### Description
AuditEvent tables range-partitioned by month, schema per ADR-013 including reserved PrevHash/EventHash; app role INSERT/SELECT only; partition drop only via a retention job under a separate role; security-relevant audit writes in the same transaction as the action (or via outbox); automated partition creation ahead of time; audit query API restricted to `Audit.Read` and workspace-scoped.

#### Acceptance criteria
- [ ] UPDATE/DELETE on audit rows as the app role fails (test)
- [ ] A coding/privilege change and its audit event commit atomically; fault injection shows no action without audit
- [ ] Audit write overhead is recorded at 100 concurrent reviewers
- [ ] Partitions are created ahead of time automatically

#### Dependencies
- `E02-T07` — Write ADR-013: audit architecture and event taxonomy
- `E04-T01` — Build migrator with SQL-first migrations and infrastructure bootstrap

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Data (PostgreSQL)
- **Source reviews:** Security & Compliance, Backend/Architecture, Legal/Discovery Counsel
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

#### Notes
Merges backend 'Partitioned append-only audit log' and SEC-11.

---

### E14-T02

**Complete audit coverage for protected content, search and administration**  
Labels: `role:security`, `role:legal`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§15, §24. Legal finding 10: view events, search terms and permission changes are needed for FRCP 26(g)/37(e); denied attempts too.

#### Description
Emit audit events for every view, image retrieval, native download, print, export, production inclusion, search execution (query text per policy, snapshot/generation, hit count), permission/role/wall changes and denied attempts; bulk ops per job + chunk with SnapshotId; prefetch not logged as view until displayed.

#### Acceptance criteria
- [ ] Each event records actor, workspace, object, action, outcome, UTC time, client IP/session, correlation ID
- [ ] Integration test: every protected-content endpoint emits an audit event; a missing event fails CI
- [ ] No application role, including workspace admin, can edit or delete audit events

#### Dependencies
- `E14-T01` — Build append-only partitioned audit store and writer
- `E05-T04` — Build protected-content gateway and authoritative access service
- `E07-T05` — Build logical search service with mandatory workspace filter and cursor binding

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Legal / Discovery Counsel
- **Source reviews:** Legal/Discovery Counsel, Security & Compliance, UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Q-16.

---

### E14-T03

**Hash-chain audit events with signed checkpoints and a verify CLI**  
Labels: `role:security`, `role:legal`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§15 tamper evidence 'where compliance requires it'; §33 defers implementation. Legal and security both ask for at least hash-chaining in v1 (cheap now).

#### Description
Per-workspace hash chain (`EventHash = H(PrevHash || canonical(event))`) by a single-writer sequencer per workspace or periodic batch sealer; signed Merkle checkpoints every N minutes using the key provider; `opportunity audit verify` CLI; exportable checkpoint hashes for declaration exhibits.

#### Acceptance criteria
- [ ] The verify CLI detects modification, deletion and reordering of any event (tests tamper with the DB directly)
- [ ] Checkpoint signatures verify with the CLI
- [ ] Chain sealing adds ≤ 5 ms p95 to interactive coding

#### Dependencies
- `E14-T01` — Build append-only partitioned audit store and writer
- `E05-T09` — Introduce secret and key-provider abstraction with envelope encryption

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Legal / Discovery Counsel
- **Source reviews:** Security & Compliance, Legal/Discovery Counsel
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Proposed amendment — pull minimal tamper evidence forward from §33. Q-17.

---

### E14-T04

**Build audit log viewer and document coding history UI**  
Labels: `role:ui`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§1 audit; §27 provenance; UI ticket 'Audit log viewer'.

#### Description
Server-paged, filterable audit list (actor, action, document, job, date range), export to CSV as a job; per-document history tab in the viewer from CodingEvent (field, old, new, actor, time, JobId or 'interactive').

#### Acceptance criteria
- [ ] Filters combine; results page by cursor and are never loaded all at once
- [ ] Audit export runs as a job and is itself audited

#### Dependencies
- `E14-T02` — Complete audit coverage for protected content, search and administration
- `E10-T05` — Expose coding history API and review-batch domain support
- `E15-T02` — Build application shell, session handling and workspace context

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX, Legal/Discovery Counsel
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E14-T05

**Generate defensibility report pack**  
Labels: `role:legal`, `role:ediscovery`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
Legal finding 10; §22, §28.

#### Description
On-demand reports: search-term hit report tied to snapshot/generation; review population and coding summary by reviewer/date; production history (volumes, Bates ranges, counts, hashes, QC results); per-document chain of custody (import source/load file, hash at import, hash re-verified from object storage, derived artifacts, productions).

#### Acceptance criteria
- [ ] Each report states its data-as-of point (snapshot ID or generation) and is reproducible for that point
- [ ] Hit reports note index lag at execution time
- [ ] Import hash is shown alongside the re-verified storage hash

#### Dependencies
- `E14-T02` — Complete audit coverage for protected content, search and administration
- `E07-T10` — Generate search term reports with unique hits
- `E12-T07` — Enforce production QC gate, finalization and manifest

#### Roles
- **Owner:** Legal / Discovery Counsel
- **Contributing:** eDiscovery Practitioner
- **Source reviews:** Legal/Discovery Counsel, eDiscovery Practitioner
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

---

### E14-T06

**Produce ethical-wall proof, access reviews and compliance control mapping**  
Labels: `role:security`, `role:legal`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
Legal finding 12 (prove walls held); SEC-13 access reviews and SOC 2 / ISO 27001 mapping for self-hosters.

#### Description
Report of all access attempts by walled users to walled content for a date range (expected: denials only); workspace access-review export (users, roles, walls, last access); audit export with chain verification result; `docs/security/control-mapping.md` mapping controls to SOC 2 CC6/CC7 and ISO 27001:2022 Annex A 5.15–5.18, 8.15, 8.16 (platform vs operator responsibility).

#### Acceptance criteria
- [ ] Wall proof report and access review export are generated by authorized roles and their generation is audited
- [ ] Audit export includes checkpoint hashes and verifies offline with the CLI
- [ ] Control mapping document is published

#### Dependencies
- `E05-T06` — Implement document/field-level security, security-affecting fields and ethical walls
- `E14-T02` — Complete audit coverage for protected content, search and administration

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Legal / Discovery Counsel
- **Source reviews:** Legal/Discovery Counsel, Security & Compliance
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E14-T07

**Archive audit checkpoints to immutable object storage**  
Labels: `role:security`, `role:devops`, `P2`, `size:M` · Milestone: M5 - Post-MVP / Deferred

#### Context
§15 'archival to immutable/object storage later'; §33.

#### Description
Ship sealed audit partitions and signed checkpoints to S3 Object Lock / Azure immutable blob in the Full profile; document the reduced guarantee for Lite.

#### Acceptance criteria
- [ ] Archived objects cannot be deleted before retention expiry (provider test)
- [ ] Verify CLI validates archived checkpoints offline

#### Dependencies
- `E14-T03` — Hash-chain audit events with signed checkpoints and a verify CLI
- `E19-T02` — Evaluate bundled object-storage providers and record ADR

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** DevOps / SRE
- **Source reviews:** Security & Compliance, DevOps/SRE
- **Milestone / phase / size:** M5 - Post-MVP / Deferred / P2 / M
