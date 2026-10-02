# E03 — Test Strategy & Correctness Infrastructure

**Labels:** `epic`, `role:qa`, `role:devops`, `role:ui`, `P0`  
**Starts in:** M0 - Foundation & Benchmark Harness  
**Tickets:** 4

## Goal
Define the layered test strategy and CI tiers, and provide the shared fixtures and invariant tests that make retries, version safety, snapshot stability and isolation provable rather than assumed.

## Baseline sections
§2.4, §18, §20, §21, §22, §23, §26, §31, §32

## Scope / out of scope
**In scope**
- Test strategy and CI tiers (PR / nightly / weekly / on-demand) mapped to every §26 gate and §31 blocker
- Shared Testcontainers fixtures with Toxiproxy
- Property/model-based invariants (FsCheck)
- Vertical-slice Playwright E2E

**Out of scope**
- Benchmark oracles and fault-injection harness (E17/E18)
- Cross-workspace attack suite (E05)

## Contributing roles
- **Roles:** QA, DevOps / SRE, UI/UX
- **Source reviews:** QA & Performance, DevOps/SRE, UI/UX
- **Milestones spanned:** M0 - Foundation & Benchmark Harness, M1 - First Vertical Slice, M2 - 1M Benchmark & Architecture Blockers

## Exit criteria
- [ ] Each §26 gate has a named suite and oracle in the strategy
- [ ] Invariant suites run on PR (≥100 cases) and nightly (≥10,000 cases)
- [ ] The §32 path runs end-to-end in Playwright on the developer Compose profile

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E03-T01](#e03-t01) | Write test strategy and CI tier definitions | M0 | S | — |
| [E03-T02](#e03-t02) | Build shared Testcontainers fixture library with Toxiproxy | M0 | M | E01-T01, E03-T01 |
| [E03-T03](#e03-t03) | Automate the vertical-slice E2E suite in Playwright | M1 | M | E03-T02, E16-T05, E16-T06, E12-T01, E08-T03 |
| [E03-T04](#e03-t04) | Implement property- and model-based correctness invariants | M2 | L | E03-T02, E17-T07, E10-T04, E10-T03, E07-T08 |

---

### E03-T01

**Write test strategy and CI tier definitions**  
Labels: `role:qa`, `P0`, `size:S` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§18 lists only Unit/Integration/ScaleTests. QA finding 9: contract, property, E2E, security and fault layers are missing; finding 8: 10M runs are too expensive for PR CI.

#### Description
Write `docs/testing/test-strategy.md`: unit, property/model-based (FsCheck), integration (Testcontainers, no store mocks), contract (envelope schema §11, OQL→DSL golden files §9), component fault, E2E (Playwright), scale/benchmark. For each layer: owner, runtime budget, CI trigger. Tiers: PR 10K docs ≤ 15 min; nightly 100K–1M ≤ 3 h; weekly/on-demand 10M on ephemeral reference infrastructure. Map every §26 gate and §31 blocker to the suite and oracle that proves it.

#### Acceptance criteria
- [ ] Every §26 gate is listed with its measuring suite and oracle
- [ ] CI tiers are defined with wall-clock budgets
- [ ] Reviewed and merged with architecture and PO sign-off

#### Dependencies
- None

#### Roles
- **Owner:** QA
- **Contributing:** —
- **Source reviews:** QA & Performance, DevOps/SRE
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / S

#### Notes
QA finding 8–9.

---

### E03-T02

**Build shared Testcontainers fixture library with Toxiproxy**  
Labels: `role:qa`, `role:devops`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
QA T1.2; backend and devops require integration tests on real containers.

#### Description
Create `tests/Opportunity.Testing` with xUnit collection fixtures for PostgreSQL, OpenSearch (single node), RabbitMQ and the S3-compatible store, pinned by digest. Each test gets an isolated WorkspaceId and fast reset (template DB / index deletion). Toxiproxy in front of each dependency.

#### Acceptance criteria
- [ ] A sample integration test runs import → index → search on a 100-doc corpus in < 60 s on a CI runner (enabled once `E08-T03` lands)
- [ ] Containers are reused across a collection; running the suite 4× with parallelism shows no flakes
- [ ] Toxiproxy can inject latency, reset and timeout on PG, OpenSearch and RabbitMQ links from test code

#### Dependencies
- `E01-T01` — Scaffold repository layout, .NET solution and Angular workspace
- `E03-T01` — Write test strategy and CI tier definitions

#### Roles
- **Owner:** QA
- **Contributing:** DevOps / SRE
- **Source reviews:** QA & Performance, Backend/Architecture, DevOps/SRE
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

---

### E03-T03

**Automate the vertical-slice E2E suite in Playwright**  
Labels: `role:qa`, `role:ui`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§20/§32 require automated scale/regression tests; QA finding 14 asks for Playwright on the Lite profile with a 1K seeded corpus.

#### Description
Playwright suite on the developer Compose profile with a 1K seeded corpus (imported via API) covering the §32 path: search → view → interactive code → search new coding → bulk tag → verify → export. Freshness-banner and stale-hit scenarios are added in `E16-T07`/`E16-T08`.

#### Acceptance criteria
- [ ] Suite passes headless in CI (nightly, and on PRs touching `Opportunity.Web` or the API) in ≤ 20 min
- [ ] Trace, video and HAR artifacts are uploaded on failure
- [ ] Flake rate < 1% over 50 consecutive nightly runs
- [ ] The keyboard-only variant of the path (`E15-T04`) is included

#### Dependencies
- `E03-T02` — Build shared Testcontainers fixture library with Toxiproxy
- `E16-T05` — Build coding panel rendered from coding layouts
- `E16-T06` — Build selection model and bulk action dialog with frozen-target confirmation
- `E12-T01` — Export a materialized snapshot to a load-file volume
- `E08-T03` — Orchestrate import jobs with chunk-level index tasks

#### Roles
- **Owner:** QA
- **Contributing:** UI/UX
- **Source reviews:** QA & Performance, UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E03-T04

**Implement property- and model-based correctness invariants**  
Labels: `role:qa`, `P0`, `size:L` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§2.4, §11, §21, §22, §23, §28. QA finding 10 lists the invariants; backend asks for a property test on the watermark.

#### Description
FsCheck properties and a model-based state-machine test (reference in-memory model, Testcontainers SUT) for: chunk idempotency (N× apply, any interleaving; exactly one CodingEvent per logical change per idempotency key); version monotonicity (interactive edits vs retried bulk tasks: final OS version = max PG version, observed version never decreases); snapshot membership stability across coding, reindex and alias switch; §22 decision table (16 combinations); watermark safety; workspace isolation for random multi-workspace corpora (no hit, count or aggregation crosses workspaces). Bates continuity is covered in `E12-T03`.

#### Acceptance criteria
- [ ] Each invariant has a named property; shrunk counterexamples log their seed and replay via `FSCHECK_SEED`
- [ ] PR runs use ≥ 100 cases per property; nightly runs ≥ 10,000
- [ ] A deliberately unversioned OpenSearch write (test build flag) is detected by the version property within the nightly budget

#### Dependencies
- `E03-T02` — Build shared Testcontainers fixture library with Toxiproxy
- `E17-T07` — Build stale-version-overwrite shadow-ledger oracle
- `E10-T04` — Build bulk coding job and worker
- `E10-T03` — Implement deterministic PIT-vs-materialized rule engine and PIT lifecycle
- `E07-T08` — Implement search generation watermark and freshness API

#### Roles
- **Owner:** QA
- **Contributing:** —
- **Source reviews:** QA & Performance, Backend/Architecture, Security & Compliance
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / L
