# opportuniTY Implementation Plan

This directory holds the consolidated implementation plan for the normative architecture baseline ([`docs/architecture/architecture-baseline.md`](../architecture/architecture-baseline.md)). The plan is a proposal. Where it needs the baseline to change, it says so in [review-findings.md](review-findings.md) and does not edit the baseline itself.

## How the plan was produced

1. **Seven role reviews.** Seven experts each reviewed the whole baseline (§1–§35) from their own role, then wrote findings, proposed epics and tickets, and open questions for the product owner. The raw reviews are kept verbatim in [`reviews/`](reviews/):

   | Review | Perspective |
   |---|---|
   | [ui-ux.md](reviews/ui-ux.md) | Legal-tech review UI/UX, accessibility, Angular frontend |
   | [backend.md](reviews/backend.md) | .NET / PostgreSQL / OpenSearch / RabbitMQ architecture |
   | [attorney.md](reviews/attorney.md) | US discovery counsel: FRCP 26/34/37, FRE 502, protective orders |
   | [ediscovery.md](reviews/ediscovery.md) | Litigation support: DAT/OPT/LFP, families, dedupe, productions |
   | [security.md](reviews/security.md) | Security architecture, multi-tenant isolation, audit, secure SDLC |
   | [qa-perf.md](reviews/qa-perf.md) | Test strategy, benchmark methodology, oracles, fault injection |
   | [devops.md](reviews/devops.md) | Compose profiles, CI/CD, observability, backup/DR, releases |

2. **Consolidation by the technical program manager.** The reviews proposed about 187 draft tickets, and many overlapped. For example, the data generator came from QA, eDiscovery and backend; audit from security, legal and backend; cross-workspace tests from security and QA; production from eDiscovery, legal, backend and UI; and CI from DevOps and security. Overlapping drafts were merged into single tickets. Each merged ticket keeps the most specific acceptance criteria and technical, legal and format details from every source, and lists every contributing role and source review.

3. **Result:** **20 epics, 150 tickets.** Each ticket has a key such as `E07-T03`, exactly one milestone, phase and size labels, explicit dependencies (acyclic, and never on a later milestone), testable acceptance criteria, and references to baseline sections and open questions.

Related documents:
- [review-findings.md](review-findings.md): consolidated, deduplicated findings grouped by theme. Items marked **"Proposed amendment — needs ADR/owner decision"** would change the baseline.
- [open-questions.md](open-questions.md): consolidated product-owner questions (Q-01 … Q-43), each with a suggested default.
- [`epics/`](epics/): one file per epic, with the epic description and the full text of all its tickets.

## Milestones

| # | Milestone | Goal | Exit criteria | Tickets |
|---|---|---|---|---|
| 1 | **M0 – Foundation & Benchmark Harness** | Repository, CI and supply chain; developer Compose profile; solution scaffolding; domain-model skeleton; data generator; benchmark harness; priority ADRs; threat model | Clean clone builds and tests; PR CI on real dependencies; all §19 ADRs Accepted or Proposed-with-interim; generator is deterministic by seed; `bench up` emits a valid result bundle | 27 |
| 2 | **M1 – First Vertical Slice** (§32) | Import → index → search → view → interactive code → search new coding → bulk tag (chunk-level index tasks) → verify → export | The §32 path passes in Playwright on the developer profile; PG stays authoritative; interactive edits use the document outbox and bulk uses payload-free IndexChunkTasks; protected content is re-authorized via the gateway | 41 |
| 3 | **M2 – 1M Benchmark & Architecture Blockers** (§31) | ADR-004 Candidate A–D spike; PG coding spike; deterministic snapshot rule; security re-check proofs; search watermark and freshness UX; fault injection | ADR-004a/004b accepted under frozen gates; 0 stale overwrites, 0 unauthorized retrievals, 100% idempotency; watermark is property-tested and visible in the UI | 16 |
| 4 | **M3 – MVP Feature Complete** (§1) | Saved searches, STR, redactions, family/duplicate workflows, defensible production, privilege log, complete audit, job monitoring, overlay import, Lite/Full deployment, backup/DR, legal hold and deletion | Every §1 initial-scope item is usable end to end; production QC gate and redaction verification block unsafe output; DR drill meets RPO/RTO on the 1M environment | 52 |
| 5 | **M4 – 10M Validation** (§29 phase 2) | Validate the winning design at 10M; finalize partitioning; recovery drill at scale | Published 10M report with no material regression vs 1M; ADR-005 final; RPO/RTO measured at scale | 3 |
| 6 | **M5 – Post-MVP / Deferred** (§33) | Full legal query language, native rendering, WORM audit archival, review batching/QC UI, clawback, 2L privilege review, LFP/PDF, Helm/K8s, privacy/residency, exfiltration controls | Scheduled after MVP; architectural boundaries are preserved by earlier tickets | 11 |

Phase labels: **P0** = M0–M2, **P1** = M3, **P2** = M4–M5.

## Epic index

| Key | Epic | Starts | Tickets | Roles |
|---|---|---|---|---|
| [E01](epics/E01-repository-scaffolding-ci-supply-chain.md) | Repository, Scaffolding & CI/Supply Chain | M0 | 6 | devops, backend, security, qa |
| [E02](epics/E02-architecture-decision-records-threat-model.md) | Architecture Decision Records & Threat Model | M0 | 8 | backend, search, data, security, legal, ediscovery, devops, ui, qa |
| [E03](epics/E03-test-strategy-correctness-infrastructure.md) | Test Strategy & Correctness Infrastructure | M0 | 4 | qa, devops, ui |
| [E04](epics/E04-domain-model-authoritative-data.md) | Domain Model & Authoritative Data | M0 | 7 | data, backend, ediscovery, legal, devops, ui |
| [E05](epics/E05-identity-authorization-tenant-isolation.md) | Identity, Authorization & Tenant Isolation | M1 | 10 | security, backend, data, qa, legal, devops, ui |
| [E06](epics/E06-messaging-outbox-job-infrastructure.md) | Messaging, Outbox & Job Infrastructure | M1 | 7 | backend, data, search, security, devops, ui |
| [E07](epics/E07-search-projection-query-language-search-features.md) | Search Projection, Query Language & Search Features | M1 | 12 | search, backend, ediscovery, performance, security |
| [E08](epics/E08-load-file-import-dat-opt-txt-natives.md) | Load-File Import (DAT/OPT/TXT/Natives) | M1 | 9 | ediscovery, backend, security, ui |
| [E09](epics/E09-family-duplicate-thread-relationships.md) | Family, Duplicate & Thread Relationships | M1 | 5 | ediscovery, data, search, backend |
| [E10](epics/E10-coding-snapshots-bulk-operations.md) | Coding, Snapshots & Bulk Operations | M1 | 6 | backend, search, data, qa, ediscovery, ui |
| [E11](epics/E11-rendering-viewer-backend-redactions.md) | Rendering, Viewer Backend & Redactions | M1 | 5 | backend, security, ui |
| [E12](epics/E12-export-defensible-production.md) | Export & Defensible Production | M1 | 9 | ediscovery, legal, backend, qa, ui |
| [E13](epics/E13-privilege-review-privilege-log-clawback.md) | Privilege Review, Privilege Log & Clawback | M3 | 6 | legal, ediscovery, backend, ui |
| [E14](epics/E14-audit-defensibility-evidence.md) | Audit & Defensibility Evidence | M1 | 7 | security, legal, data, ediscovery, devops, ui |
| [E15](epics/E15-frontend-foundation-accessibility.md) | Frontend Foundation & Accessibility | M1 | 5 | ui, qa |
| [E16](epics/E16-review-workspace-ui.md) | Review Workspace UI | M1 | 12 | ui, security, search |
| [E17](epics/E17-synthetic-data-generator-benchmark-harness.md) | Synthetic Data Generator & Benchmark Harness | M0 | 9 | performance, qa, ediscovery, data, devops |
| [E18](epics/E18-architecture-spikes-scale-validation.md) | Architecture Spikes & Scale Validation | M2 | 9 | performance, qa, search, data, devops |
| [E19](epics/E19-deployment-profiles-observability-operations.md) | Deployment Profiles, Observability & Operations | M0 | 9 | devops, backend, performance, security |
| [E20](epics/E20-matter-lifecycle-legal-hold-privacy.md) | Matter Lifecycle, Legal Hold & Privacy | M3 | 5 | legal, backend, security, ui, devops |

Ticket distribution per epic and milestone:

| Epic | M0 | M1 | M2 | M3 | M4 | M5 |
|---|---|---|---|---|---|---|
| E01 | 5 | | | 1 | | |
| E02 | 8 | | | | | |
| E03 | 2 | 1 | 1 | | | |
| E04 | 4 | 1 | | 2 | | |
| E05 | | 4 | 1 | 4 | | 1 |
| E06 | | 5 | | 2 | | |
| E07 | | 7 | 1 | 3 | | 1 |
| E08 | | 5 | | 4 | | |
| E09 | | 2 | | 3 | | |
| E10 | | 3 | 1 | 1 | | 1 |
| E11 | | 1 | | 3 | | 1 |
| E12 | | 1 | | 7 | | 1 |
| E13 | | | | 4 | | 2 |
| E14 | | 1 | | 5 | | 1 |
| E15 | | 4 | | 1 | | |
| E16 | | 6 | 2 | 4 | | |
| E17 | 4 | | 4 | 1 | | |
| E18 | | | 5 | 1 | 3 | |
| E19 | 4 | | 1 | 3 | | 1 |
| E20 | | | | 3 | | 2 |
| **Total** | **27** | **41** | **16** | **52** | **3** | **11** |

## Critical paths

These are the longest dependency chains, computed from `depends_on`. Parallel work fans out from them. The chains below set the minimum schedule.

### To M0 (foundation)
`E02-T01` ADR process → `E02-T06` ADR-011/012/014 → `E19-T01` object-storage abstraction → `E19-T02` provider evaluation ADR → `E19-T03` developer Compose profile → `E17-T03` reference environments & result-bundle schema → `E17-T04` query taxonomy & workloads.

These run in parallel: `E01-T01` → `E01-T03` CI → `E01-T04/T05`; `E04-T01` migrator → `E04-T02..T04` schema; `E17-T01` → `E17-T02` generator; and the ADRs `E02-T02..T08`.

### To M1 (first vertical slice, §32)
`E01-T01` scaffold → `E04-T01` migrator → `E04-T02` core schema → `E06-T02` job/chunk state machine → `E06-T03` outbox/IndexChunkTask tables → `E06-T04` dispatcher → `E07-T03` interactive index worker → `E10-T01` interactive coding API → `E16-T05` coding panel → `E03-T03` Playwright slice E2E.

These must converge on the same path:
- Import: `E08-T01` → `E08-T02` → `E08-T03` → `E08-T04/T05`, then `E09-T01`.
- Search: `E07-T01` → `E07-T02` → `E07-T05` → `E07-T06/T07`.
- Security: `E05-T01` → `E05-T02` → `E05-T04` gateway → `E11-T01` content API.
- Bulk and export: `E10-T02` snapshot → `E10-T04` bulk coding → `E12-T01` export → `E16-T06` bulk dialog.

### To M2 (1M benchmark & §31 blockers)
`E01-T01` → `E04-T01` → `E04-T02` → `E06-T02` → `E06-T03` → `E06-T04` → `E07-T03` → `E07-T08` watermark → `E17-T06` coding→searchable probe → `E17-T08` gate evaluator → `E18-T02` freeze methodology → `E18-T04` ADR-004 comparative run → `E18-T05` ADR-004a/004b decision.

These feed the same chain:
- `E17-T07` shadow-ledger oracle → `E18-T01` fault-injection matrix.
- `E05-T05` cross-workspace attack suite (the §26 security gate).
- `E17-T05` fast-path loaders and corpus cache.
- `E18-T03` PostgreSQL coding spike.

The schedule risk sits in the ADR-004 spike's timebox (about one sprint, §26) and in the open questions about reference hardware and budget (Q-03, Q-04).

## Labeling scheme

| Label | Meaning |
|---|---|
| `epic` | Epic issue; its tickets are attached as sub-issues |
| `role:ui`, `role:backend`, `role:data`, `role:search`, `role:security`, `role:legal`, `role:ediscovery`, `role:qa`, `role:performance`, `role:devops` | Roles involved; the first role listed in a ticket's **Roles** section is the owner |
| `P0` / `P1` / `P2` | Phase: M0–M2 / M3 / M4–M5 |
| `size:S` / `size:M` / `size:L` | About one PR / a few PRs / a multi-PR feature (split before starting if it grows) |
| `adr` | The ticket's main deliverable is an ADR |
| `spike` | Timeboxed benchmark or evaluation whose output is evidence and a decision |

Milestones map 1:1 to the GitHub milestones `M0` … `M5`. Every ticket belongs to exactly one milestone. An epic's milestone is the earliest milestone among its tickets.

## How to use this plan

- **Pick work:** filter by milestone first, then by `role:*`. Start a ticket only when every `depends_on` ticket is done, or when it can merge behind a feature flag.
- **Ticket anatomy:** *Context* (baseline § and the review finding behind it), *Description*, *Acceptance criteria* (checkboxes, all must pass), *Dependencies* (ticket keys), *Roles* (owner, contributors, source reviews), *Notes* (proposed amendments and open-question IDs).
- **Open questions:** tickets that cite `Q-xx` assume that question's *suggested default* (see [open-questions.md](open-questions.md)) until the product owner decides. A different decision means updating the affected tickets.
- **Proposed amendments:** these are in [review-findings.md](review-findings.md). Each one is resolved through the ADR ticket that cites it, which must be Accepted (or explicitly rejected) before dependent implementation tickets merge. Under §35 the baseline is only changed by an explicit owner decision.
- **Keeping it in sync:** the machine-readable source is `issues.json`, generated alongside this directory. It is used to create the GitHub issues, where `` `Exx-Tyy` `` references are rewritten into issue links. Update this directory when ticket scope changes materially.

## GitHub issues

All epics and tickets are tracked as GitHub issues. See [issue-map.md](issue-map.md) for the plan-key → issue-number mapping.

Product-owner answers are recorded in [decisions.md](decisions.md).
