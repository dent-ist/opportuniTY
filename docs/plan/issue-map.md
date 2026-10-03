# Plan key → GitHub issue map

Issues were created from this plan in `plogramer/opportuniTY`. Epics carry the `epic` label; tickets are sub-issues of their epic.

## E01 — [[Epic] Repository, Scaffolding & CI/Supply Chain](https://github.com/plogramer/opportuniTY/issues/1) (#1)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E01-T01 | #21 | Scaffold repository layout, .NET solution and Angular workspace | M0 |
| E01-T02 | #22 | Build composable worker host, API conventions and health endpoints | M0 |
| E01-T03 | #23 | Set up PR CI pipeline with real-dependency integration tests | M0 |
| E01-T04 | #24 | Add security and supply-chain scanning gates | M0 |
| E01-T05 | #25 | Build, sign and publish container images | M0 |
| E01-T06 | #26 | Automate versioning, changelog and release workflow with upgrade test | M3 |

## E02 — [[Epic] Architecture Decision Records & Threat Model](https://github.com/plogramer/opportuniTY/issues/2) (#2)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E02-T01 | #27 | Establish ADR process, resolve numbering and record layering/API conventions | M0 |
| E02-T02 | #28 | Write ADR-001 and ADR-010: search consistency, version model and job/chunk/idempotency semantics | M0 |
| E02-T03 | #29 | Write ADR-002: bulk snapshot semantics with numeric PIT policy | M0 |
| E02-T04 | #30 | Write ADR-003/005/009: metadata model, interim partitioning, identity and dates | M0 |
| E02-T05 | #31 | Write ADR-006/007/008: index strategy, mapping strategy and minimal query language | M0 |
| E02-T06 | #32 | Write ADR-011/012/014: object storage addressing, redaction and page model, retention and deletion lifecycle | M0 |
| E02-T07 | #33 | Write ADR-013: audit architecture and event taxonomy | M0 |
| E02-T08 | #34 | Produce STRIDE threat model and ADR-015 security architecture | M0 |

## E03 — [[Epic] Test Strategy & Correctness Infrastructure](https://github.com/plogramer/opportuniTY/issues/3) (#3)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E03-T01 | #35 | Write test strategy and CI tier definitions | M0 |
| E03-T02 | #36 | Build shared Testcontainers fixture library with Toxiproxy | M0 |
| E03-T03 | #37 | Automate the vertical-slice E2E suite in Playwright | M1 |
| E03-T04 | #38 | Implement property- and model-based correctness invariants | M2 |

## E04 — [[Epic] Domain Model & Authoritative Data](https://github.com/plogramer/opportuniTY/issues/4) (#4)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E04-T01 | #39 | Build migrator with SQL-first migrations and infrastructure bootstrap | M0 |
| E04-T02 | #40 | Create workspace, document and page core schema | M0 |
| E04-T03 | #41 | Implement field definitions, choices and coding layouts | M0 |
| E04-T04 | #42 | Create interim coding current-state and CodingEvent provenance tables | M0 |
| E04-T05 | #43 | Expose workspace management API | M1 |
| E04-T06 | #44 | Build field definition and coding layout editor UI | M3 |
| E04-T07 | #45 | Build workspace management screens | M3 |

## E05 — [[Epic] Identity, Authorization & Tenant Isolation](https://github.com/plogramer/opportuniTY/issues/5) (#5)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E05-T01 | #46 | Implement OIDC authentication with a BFF session | M1 |
| E05-T02 | #47 | Implement permission catalogue, workspace roles and policy decision point | M1 |
| E05-T03 | #48 | Enforce PostgreSQL row-level security and composite tenant keys | M1 |
| E05-T04 | #49 | Build protected-content gateway and authoritative access service | M1 |
| E05-T05 | #50 | Build cross-workspace attack and stale-hit authorization suite as a CI gate | M2 |
| E05-T06 | #51 | Implement document/field-level security, security-affecting fields and ethical walls | M3 |
| E05-T07 | #52 | Harden message and worker trust with envelope verification and scoped identities | M3 |
| E05-T08 | #53 | Build roles, permissions and user assignment UI | M3 |
| E05-T09 | #54 | Introduce secret and key-provider abstraction with envelope encryption | M3 |
| E05-T10 | #55 | Add exfiltration controls: rate limiting, viewer watermarking and export protection | M5 |

## E06 — [[Epic] Messaging, Outbox & Job Infrastructure](https://github.com/plogramer/opportuniTY/issues/6) (#6)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E06-T01 | #56 | Implement message contracts and RabbitMQ transport adapter | M1 |
| E06-T02 | #57 | Implement Job and JobChunk state machine with leases | M1 |
| E06-T03 | #58 | Create SearchOutbox and IndexChunkTask tables | M1 |
| E06-T04 | #59 | Build outbox dispatcher service | M1 |
| E06-T05 | #60 | Build idempotent consumer framework | M1 |
| E06-T06 | #61 | Provide job operations API, DLQ inspection, PG-driven replay and runbooks | M3 |
| E06-T07 | #62 | Build job monitor UI, job tray and notifications | M3 |

## E07 — [[Epic] Search Projection, Query Language & Search Features](https://github.com/plogramer/opportuniTY/issues/7) (#7)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E07-T01 | #63 | Implement index management and logical index resolution | M1 |
| E07-T02 | #64 | Create baseline search projection mapping and projection builder | M1 |
| E07-T03 | #65 | Build version-safe interactive index worker | M1 |
| E07-T04 | #66 | Build chunk index worker for IndexChunkTask | M1 |
| E07-T05 | #67 | Build logical search service with mandatory workspace filter and cursor binding | M1 |
| E07-T06 | #68 | Implement minimal query parser and AST | M1 |
| E07-T07 | #69 | Build search planner with field resolution and proximity | M1 |
| E07-T08 | #70 | Implement search generation watermark and freshness API | M2 |
| E07-T09 | #71 | Implement saved searches | M3 |
| E07-T10 | #72 | Generate search term reports with unique hits | M3 |
| E07-T11 | #73 | Implement alias-based reindex and projection generation switch | M3 |
| E07-T12 | #74 | Extend query language to full legal syntax | M5 |

## E08 — [[Epic] Load-File Import (DAT/OPT/TXT/Natives)](https://github.com/plogramer/opportuniTY/issues/8) (#8)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E08-T01 | #75 | Build streaming DAT parser with delimiter profiles and encoding detection | M1 |
| E08-T02 | #76 | Implement field mapping, typed parsing, dates/time zones and mapping templates | M1 |
| E08-T03 | #77 | Orchestrate import jobs with chunk-level index tasks | M1 |
| E08-T04 | #78 | Link natives and extracted text into object storage | M1 |
| E08-T05 | #79 | Load OPT image cross-references and reconcile pages | M1 |
| E08-T06 | #80 | Add pre-flight validation, import report and re-loadable error file | M3 |
| E08-T07 | #81 | Support overlay and append/overlay import modes | M3 |
| E08-T08 | #82 | Build import wizard UI | M3 |
| E08-T09 | #83 | Harden ingest with malware scanning, type verification and quarantine | M3 |

## E09 — [[Epic] Family, Duplicate & Thread Relationships](https://github.com/plogramer/opportuniTY/issues/9) (#9)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E09-T01 | #84 | Reconstruct families from BegAttach/EndAttach, ParentID and GroupIdentifier | M1 |
| E09-T02 | #85 | Import upstream duplicate and email-thread identifiers | M1 |
| E09-T03 | #86 | Expand families, duplicates and threads for search, snapshots and bulk actions | M3 |
| E09-T04 | #87 | Compute duplicate groups with family-level, scoped dedupe policy | M3 |
| E09-T05 | #88 | Implement family and duplicate coding propagation | M3 |

## E10 — [[Epic] Coding, Snapshots & Bulk Operations](https://github.com/plogramer/opportuniTY/issues/10) (#10)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E10-T01 | #89 | Implement interactive coding API with optimistic concurrency | M1 |
| E10-T02 | #90 | Build materialized DocumentSetSnapshot service | M1 |
| E10-T03 | #91 | Implement deterministic PIT-vs-materialized rule engine and PIT lifecycle | M2 |
| E10-T04 | #92 | Build bulk coding job and worker | M1 |
| E10-T05 | #93 | Expose coding history API and review-batch domain support | M3 |
| E10-T06 | #94 | Deliver review batching and QC workflow | M5 |

## E11 — [[Epic] Rendering, Viewer Backend & Redactions](https://github.com/plogramer/opportuniTY/issues/11) (#11)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E11-T01 | #95 | Build document content API for metadata, chunked text, page images and natives | M1 |
| E11-T02 | #96 | Build render worker pipeline for imported PDFs and images | M3 |
| E11-T03 | #97 | Sandbox render workers and deliver safe renditions | M3 |
| E11-T04 | #98 | Implement non-destructive redactions API and redaction tool | M3 |
| E11-T05 | #99 | Select native rendering technology | M5 |

## E12 — [[Epic] Export & Defensible Production](https://github.com/plogramer/opportuniTY/issues/12) (#12)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E12-T01 | #100 | Export a materialized snapshot to a load-file volume | M1 |
| E12-T02 | #101 | Freeze production specifications with versioning | M3 |
| E12-T03 | #102 | Allocate Bates numbers with integrity guarantees | M3 |
| E12-T04 | #103 | Apply confidentiality designations and endorsements | M3 |
| E12-T05 | #104 | Generate production volume outputs and DAT/OPT load files | M3 |
| E12-T06 | #105 | Verify redaction burn-in automatically | M3 |
| E12-T07 | #106 | Enforce production QC gate, finalization and manifest | M3 |
| E12-T08 | #107 | Build export and production wizard UI | M3 |
| E12-T09 | #108 | Add LFP load files and PDF production output | M5 |

## E13 — [[Epic] Privilege Review, Privilege Log & Clawback](https://github.com/plogramer/opportuniTY/issues/13) (#13)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E13-T01 | #109 | Model privilege designations as system fields | M3 |
| E13-T02 | #110 | Detect family and duplicate privilege inconsistencies | M3 |
| E13-T03 | #111 | Generate privilege logs in configurable formats | M3 |
| E13-T04 | #112 | Provide where-produced lookup and Bates cross-reference | M3 |
| E13-T05 | #113 | Implement clawback and replacement re-production workflow | M5 |
| E13-T06 | #114 | Add second-level privilege review queue | M5 |

## E14 — [[Epic] Audit & Defensibility Evidence](https://github.com/plogramer/opportuniTY/issues/14) (#14)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E14-T01 | #115 | Build append-only partitioned audit store and writer | M1 |
| E14-T02 | #116 | Complete audit coverage for protected content, search and administration | M3 |
| E14-T03 | #117 | Hash-chain audit events with signed checkpoints and a verify CLI | M3 |
| E14-T04 | #118 | Build audit log viewer and document coding history UI | M3 |
| E14-T05 | #119 | Generate defensibility report pack | M3 |
| E14-T06 | #120 | Produce ethical-wall proof, access reviews and compliance control mapping | M3 |
| E14-T07 | #121 | Archive audit checkpoints to immutable object storage | M5 |

## E15 — [[Epic] Frontend Foundation & Accessibility](https://github.com/plogramer/opportuniTY/issues/15) (#15)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E15-T01 | #122 | Write frontend architecture ADR and build design tokens and core components | M1 |
| E15-T02 | #123 | Build application shell, session handling and workspace context | M1 |
| E15-T03 | #124 | Build keyboard command framework and user preferences | M1 |
| E15-T04 | #125 | Add accessibility and UI-performance gates to CI | M1 |
| E15-T05 | #126 | Localise UI with date/time-zone display rules | M3 |

## E16 — [[Epic] Review Workspace UI](https://github.com/plogramer/opportuniTY/issues/16) (#16)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E16-T01 | #127 | Build query bar with syntax validation and error positions | M1 |
| E16-T02 | #128 | Build virtualized, cursor-paged review grid | M1 |
| E16-T03 | #129 | Build review workspace layout and review cursor | M1 |
| E16-T04 | #130 | Build viewer modes for text, metadata, native and page images | M1 |
| E16-T05 | #131 | Build coding panel rendered from coding layouts | M1 |
| E16-T06 | #132 | Build selection model and bulk action dialog with frozen-target confirmation | M1 |
| E16-T07 | #133 | Show search freshness and two-phase progress in plain language | M2 |
| E16-T08 | #134 | Handle security-affecting coding and access-restricted states | M2 |
| E16-T09 | #135 | Add column configuration and saved grid views | M3 |
| E16-T10 | #136 | Display families and duplicates with relationship panel and propagation | M3 |
| E16-T11 | #137 | Build saved search panel | M3 |
| E16-T12 | #138 | Implement term-hit highlighting and persistent highlight sets | M3 |

## E17 — [[Epic] Synthetic Data Generator & Benchmark Harness](https://github.com/plogramer/opportuniTY/issues/17) (#17)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E17-T01 | #139 | Build deterministic seeded corpus generator with realistic distributions | M0 |
| E17-T02 | #140 | Write load-file volumes with defect injection | M0 |
| E17-T03 | #141 | Define reference environments and result-bundle schema | M0 |
| E17-T04 | #142 | Implement query taxonomy and k6 workload scripts | M0 |
| E17-T05 | #143 | Build fast-path loaders and corpus artifact caching | M2 |
| E17-T06 | #144 | Build end-to-end coding→searchable probe and index-lag measurement | M2 |
| E17-T07 | #145 | Build stale-version-overwrite shadow-ledger oracle | M2 |
| E17-T08 | #146 | Build gate evaluator and benchmark report generator | M2 |
| E17-T09 | #147 | Generate known-answer STR and production-QC fixtures | M3 |

## E18 — [[Epic] Architecture Spikes & Scale Validation](https://github.com/plogramer/opportuniTY/issues/18) (#18)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E18-T01 | #148 | Build fault-injection harness and idempotency matrix | M2 |
| E18-T02 | #149 | Freeze reference hardware, gates and methodology | M2 |
| E18-T03 | #150 | Run PostgreSQL coding-model spike | M2 |
| E18-T04 | #151 | Run ADR-004 1M comparative spike for Candidates A–D | M2 |
| E18-T05 | #152 | Decide ADR-004a/004b and update performance status | M2 |
| E18-T06 | #153 | Run nightly scale-regression workflow with performance regression guard | M3 |
| E18-T07 | #154 | Validate the winning design at 10M | M4 |
| E18-T08 | #155 | Benchmark PostgreSQL partitioning and finalize ADR-005 | M4 |
| E18-T09 | #156 | Drill recovery and measure RPO/RTO at scale | M4 |

## E19 — [[Epic] Deployment Profiles, Observability & Operations](https://github.com/plogramer/opportuniTY/issues/19) (#19)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E19-T01 | #157 | Implement object storage abstraction and provider contract suite | M0 |
| E19-T02 | #158 | Evaluate bundled object-storage providers and record ADR | M0 |
| E19-T03 | #159 | Create Docker Compose developer profile | M0 |
| E19-T04 | #160 | Instrument with OpenTelemetry and add observability profile | M0 |
| E19-T05 | #161 | Build consistency and pipeline metrics, dashboards and alerts | M2 |
| E19-T06 | #162 | Harden Lite profile and add Full/scale Compose profile | M3 |
| E19-T07 | #163 | Write backup/DR ADR and restore-consistency design | M3 |
| E19-T08 | #164 | Implement PostgreSQL PITR, OpenSearch snapshots and artifact replication | M3 |
| E19-T09 | #165 | Publish Helm chart and Kubernetes reference deployment | M5 |

## E20 — [[Epic] Matter Lifecycle, Legal Hold & Privacy](https://github.com/plogramer/opportuniTY/issues/20) (#20)

| Key | Issue | Title | Milestone |
|---|---|---|---|
| E20-T01 | #166 | Add workspace preservation lock (legal hold) | M3 |
| E20-T02 | #167 | Delete workspaces defensibly with fencing and destruction certificate | M3 |
| E20-T03 | #168 | Require reviewer attestation and protective-order acknowledgment | M3 |
| E20-T04 | #169 | Provide pattern-assisted PII/PHI privacy redaction | M5 |
| E20-T05 | #170 | Record data residency and processing details per workspace | M5 |
