# Architecture Decision Records

This directory holds the Architecture Decision Records (ADRs) for opportuniTY. ADRs elaborate, and where marked,
propose amendments to the normative [architecture baseline](../architecture/architecture-baseline.md) (§35). The
baseline wins over an ADR until a proposed amendment is signed off; product-owner decisions in
[decisions.md](../plan/decisions.md) win over defaults in both.

## Process

1. **Register** — every ADR gets a number in the index below before it is written. Numbers are never reused.
2. **Write** — copy [0000-template.md](0000-template.md) to `NNNN-kebab-case-title.md` (ADR-004a/004b use
   `0004a-`/`0004b-`) and open a PR labelled `adr` that links the tracking issue.
3. **Review** — the owner role writes; the lead architect and every contributing role named in the ticket review. A
   change that touches a product-owner decision (Q-NN) also needs the product owner.
4. **Accept** — merge with status `Accepted` and update this index in the same PR. An ADR may merge as `Proposed` with
   an explicit *interim position* that code may rely on until it is accepted.
5. **Change** — an accepted ADR is not rewritten. Minor clarifications are edited in place with a dated note; a changed
   decision is a new ADR that marks the old one `Superseded by ADR-NNN`.

**Statuses:** `Proposed` (registered or drafted, interim position may apply) · `Accepted` · `Spike pending`
(decision deliberately blocked on a benchmark/spike) · `Superseded` · `Deprecated` · `Rejected`.

**Baseline amendments:** an ADR that changes the baseline lists the change under *Baseline amendments* as
*Proposed*. The baseline document is edited only after lead-architect and product-owner sign-off, and the matching
entry in [review-findings.md](../plan/review-findings.md) is marked resolved.

## Numbering

Decided in [ADR-019](0019-layering-and-api-conventions.md) (resolves review finding **A-01** and registers **A-02**):

- Baseline §19 item 4 ("Coding storage", PostgreSQL) and §25/§26 "ADR-004" (OpenSearch coding projection) collided.
  The number 004 is split: **ADR-004a — PostgreSQL coding storage** (the §27 spike) and **ADR-004b — OpenSearch coding
  projection** (the §25/§26 Candidate A–D spike). Every baseline reference to "ADR-004" in §23, §25, §26, §30, §34 and
  §35 means ADR-004b; §27 "alongside ADR-004" means 004a is decided together with 004b (`E18-T05`).
- ADR-001…014 keep the §19 order. ADR-015…018 are added for the gaps the reviews found. ADR-019 onward are assigned
  in order of registration.

## Index

| ADR | Title | Baseline § | Status | Owner (role) | Tracking issue |
|---|---|---|---|---|---|
| ADR-001 | PostgreSQL ↔ OpenSearch consistency / outbox & version model | §7, §21, §23, §28 | Proposed | Backend | #28 (`E02-T02`) |
| ADR-002 | Bulk snapshot semantics (PIT vs materialized) | §10, §22 | Proposed | Search (OpenSearch) | #29 (`E02-T03`) |
| ADR-003 | Metadata / custom-field model | §5, §6 | Proposed | Data (PostgreSQL) | #30 (`E02-T04`) |
| ADR-004a | PostgreSQL coding storage | §6, §27 | Spike pending | Data (PostgreSQL) | #150 (`E18-T03`), decided in #152 (`E18-T05`) |
| ADR-004b | OpenSearch coding projection (Candidates A–D) | §23, §25, §26, §29 | Spike pending | Search (OpenSearch) | #151 (`E18-T04`), decided in #152 (`E18-T05`) |
| ADR-005 | PostgreSQL partitioning | §6, §33 | Proposed (interim in #30; final after spike) | Data (PostgreSQL) | #30 (`E02-T04`), final #155 (`E18-T08`) |
| ADR-006 | OpenSearch index strategy | §8, §23 | Proposed | Search (OpenSearch) | #31 (`E02-T05`) |
| ADR-007 | Search mapping strategy | §8, §23 | Proposed | Search (OpenSearch) | #31 (`E02-T05`) |
| ADR-008 | Query language (minimal AST subset) | §9, §33 | Proposed | Search (OpenSearch) | #31 (`E02-T05`) |
| ADR-009 | Family / dedupe / thread identity | §5, §12 | Proposed | Data (PostgreSQL) | #30 (`E02-T04`) |
| ADR-010 | Job / chunk / idempotency semantics | §11, §21 | Proposed | Backend | #28 (`E02-T02`) |
| ADR-011 | Object-storage addressing | §4, §15, §16 | Proposed | Backend | #32 (`E02-T06`) |
| ADR-012 | Redactions and page model | §5, §13 | Proposed | Backend | #32 (`E02-T06`) |
| ADR-013 | Audit architecture and event taxonomy | §15, §33 | Proposed | Security & Compliance | #33 (`E02-T07`) |
| ADR-014 | Retention / deletion lifecycle | §15 | Proposed | Backend | #32 (`E02-T06`) |
| ADR-015 | Security architecture & trust boundaries (STRIDE) | §3, §15, §24 | Proposed | Security & Compliance | #34 (`E02-T08`) |
| ADR-016 | Backup/DR & restore consistency | §16, §17 | Proposed | DevOps / SRE | #163 (`E19-T07`) |
| ADR-017 | Observability & SLOs | §4, §17, §28 | Proposed | DevOps / SRE | #160 (`E19-T04`), #161 (`E19-T05`) |
| ADR-018 | Frontend architecture & UX baseline | §3, §4, §28 | Proposed | UI/UX | #122 (`E15-T01`) |
| [ADR-019](0019-layering-and-api-conventions.md) | Solution layering, API conventions and message-contract versioning | §2, §11, §18, §20, §32 | **Accepted** | Backend | #27 (`E02-T01`) |
| [ADR-020](0020-bundled-object-storage.md) | Bundled object storage (Lite filesystem, Full SeaweedFS, RustFS fallback) | §4, §15, §16, §17 | **Accepted** | DevOps / SRE | #158 (`E19-T02`) |

ADR-001…018 are registered but not yet written; the file link is added when the ADR's PR merges. ADR-017 has no
dedicated authoring ticket: it is written by the DevOps / SRE owner as part of `E19-T04`/`E19-T05` and must be
Accepted before the SLO alerts in `E19-T05` ship.
