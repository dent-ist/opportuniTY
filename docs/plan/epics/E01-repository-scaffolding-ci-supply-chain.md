# E01 — Repository, Scaffolding & CI/Supply Chain

**Labels:** `epic`, `role:backend`, `role:devops`, `role:qa`, `role:security`, `P0`  
**Starts in:** M0 - Foundation & Benchmark Harness  
**Tickets:** 6

## Goal
Stand up the §18 repository, the .NET/Angular solution with enforced layering, and a GitHub Actions pipeline that tests every PR against real dependencies and produces signed, scanned, versioned images. Every other epic builds on this.

## Baseline sections
§2.5, §4, §18, §20, §26, §29, §32, §35

## Scope / out of scope
**In scope**
- Repository layout, solution and project layering (with the projects §18 omits: Contracts, Application, Dispatcher, BulkCoding worker, Benchmarks, ArchitectureTests)
- Composable worker host, API conventions (OpenAPI 3.1, ProblemDetails, 202 + job resource, Idempotency-Key), health endpoints
- PR CI with Testcontainers, security/supply-chain scanning, signed multi-arch images, release workflow

**Out of scope**
- Kubernetes/Helm packaging (E19)
- Benchmark infrastructure (E17)

## Contributing roles
- **Roles:** Backend, DevOps / SRE, QA, Security & Compliance
- **Source reviews:** Backend/Architecture, DevOps/SRE, Security & Compliance, QA & Performance
- **Milestones spanned:** M0 - Foundation & Benchmark Harness, M3 - MVP Feature Complete

## Exit criteria
- [ ] A clean clone builds and tests with `dotnet build`/`dotnet test` and `npm ci && npm run build`
- [ ] PRs are blocked on build, unit, integration, architecture and security-scan failures
- [ ] Released images are signed, carry SBOM + provenance, and are referenced by digest

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E01-T01](#e01-t01) | Scaffold repository layout, .NET solution and Angular workspace | M0 | M | — |
| [E01-T02](#e01-t02) | Build composable worker host, API conventions and health endpoints | M0 | M | E01-T01, E02-T01 |
| [E01-T03](#e01-t03) | Set up PR CI pipeline with real-dependency integration tests | M0 | M | E01-T01 |
| [E01-T04](#e01-t04) | Add security and supply-chain scanning gates | M0 | M | E01-T03 |
| [E01-T05](#e01-t05) | Build, sign and publish container images | M0 | M | E01-T02, E01-T03 |
| [E01-T06](#e01-t06) | Automate versioning, changelog and release workflow with upgrade test | M3 | M | E01-T05, E04-T01 |

---

### E01-T01

**Scaffold repository layout, .NET solution and Angular workspace**  
Labels: `role:backend`, `role:devops`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§18 defines the repository layout; §2.5 asks for a modular .NET application plus independently scalable workers. Backend found that §18 is missing a Contracts project, an Application/Domain split, a Dispatcher host, a BulkCoding worker (§21) and a Benchmarks harness, and that the root name `opportunity/` differs from the repo name.

#### Description
Create the §18 skeleton (`src/`, `tests/`, `tools/`, `deploy/docker-compose/`, `deploy/kubernetes/` placeholder, `docs/adr/`) and `Opportunity.sln` with the extra projects `Opportunity.Contracts`, `Opportunity.Application`, `Opportunity.Worker.Dispatcher`, `Opportunity.Worker.BulkCoding`, `tests/ArchitectureTests`, `tools/Opportunity.Benchmarks`. Pin the .NET SDK (proposal: .NET 10 LTS) in `global.json`; use `Directory.Build.props`, central package management, nullable + warnings-as-errors, `.editorconfig` and analyzers. Create the Angular workspace for `Opportunity.Web`. Add CODEOWNERS, PR/issue templates and CONTRIBUTING.md. Pin .NET, Node, PostgreSQL, OpenSearch and RabbitMQ versions in a single `versions` file that Compose, CI and benchmarks all read (the baseline never names target versions).

#### Acceptance criteria
- [ ] `dotnet build`, `dotnet test` and `npm ci && npm run build` succeed from a clean clone
- [ ] NetArchTest rules fail the build if `Core` references Npgsql, OpenSearch.Client, RabbitMQ.Client or AWS/Azure SDKs, or if `Application` references RabbitMQ.Client
- [ ] Tool and service versions are defined in exactly one file and consumed by CI and Compose
- [ ] Dependabot or Renovate is configured for NuGet, npm, Docker and GitHub Actions
- [ ] Project reference rules match the layering ADR (`E02-T01`)

#### Dependencies
- None

#### Roles
- **Owner:** Backend
- **Contributing:** DevOps / SRE
- **Source reviews:** Backend/Architecture, DevOps/SRE
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Addresses backend finding 16 (§18 layout gaps) and devops finding on unpinned versions.

---

### E01-T02

**Build composable worker host, API conventions and health endpoints**  
Labels: `role:backend`, `role:devops`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§2.5 and §16 require independently scalable workers but a Lite profile with "essential workers". DevOps proposes one worker binary whose consumers are selected by configuration; backend proposes REST conventions (§4 is silent on API style).

#### Description
Implement a generic-host worker executable that hosts any subset of consumers (import, index, render, export, production, bulk coding, dispatcher) selected by config, so Lite runs one combined worker and Full runs one container per type. Establish API conventions: OpenAPI 3.1 generated at build, `/api/v1` URL versioning, RFC 9457 ProblemDetails, cursor pagination, long-running operations return `202 Accepted` + `Location: …/jobs/{jobId}`, `Idempotency-Key` header on job-creating POSTs. Add `/health/live` (process only) and `/health/ready` (PG, OpenSearch, RabbitMQ, object store reachable; schema at expected version) to API and workers, plus a worker heartbeat/last-consumed metric. Bind config via `IOptions` with `ValidateOnStart` and fail fast on missing configuration.

#### Acceptance criteria
- [ ] The same worker image runs as combined (all consumers) or single-type via configuration only
- [ ] OpenAPI document is generated during build and diffed in CI; breaking changes fail the check
- [ ] Re-POSTing a job creation with the same Idempotency-Key returns the original job
- [ ] Killing PostgreSQL makes `/health/ready` fail within 10 s while `/health/live` stays OK
- [ ] High index lag does not flip readiness (it is reported as a metric/alert instead, §28)

#### Dependencies
- `E01-T01` — Scaffold repository layout, .NET solution and Angular workspace
- `E02-T01` — Establish ADR process, resolve numbering and record layering/API conventions

#### Roles
- **Owner:** Backend
- **Contributing:** DevOps / SRE
- **Source reviews:** Backend/Architecture, DevOps/SRE
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Merges backend 'Solution scaffolding' API conventions and devops 'Health checks and readiness contract'.

---

### E01-T03

**Set up PR CI pipeline with real-dependency integration tests**  
Labels: `role:devops`, `role:qa`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§18/§20/§32 require automated tests; QA and DevOps both require integration tests against real PostgreSQL/OpenSearch/RabbitMQ/object storage rather than fakes.

#### Description
GitHub Actions workflow: lint/format → .NET build + unit → Angular build + unit → integration tests via Testcontainers (PG, OpenSearch single node, RabbitMQ, chosen object store) → architecture tests → OpenAPI diff → message-contract tests. Cache NuGet/npm. Publish test results and coverage. Reserve required-job slots for the cross-workspace suite (`E05-T05`) and the fault/idempotency suite (`E18-T01`). Configure branch protection.

#### Acceptance criteria
- [ ] PRs are blocked on failing build, unit, integration, architecture or contract tests
- [ ] PR pipeline completes in < 15 min on GitHub-hosted runners
- [ ] OpenSearch container starts reliably in CI (heap limited, `vm.max_map_count` handled)
- [ ] Test reports and coverage are visible in the PR UI; no in-memory fakes are used for PG/OpenSearch in integration tests

#### Dependencies
- `E01-T01` — Scaffold repository layout, .NET solution and Angular workspace

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** QA
- **Source reviews:** DevOps/SRE, Backend/Architecture, QA & Performance
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Merges backend 'CI pipeline with containerized integration tests' and devops 'PR CI pipeline'. Nightly/scale tiers are defined in `E03-T01` and implemented in `E18-T06`.

---

### E01-T04

**Add security and supply-chain scanning gates**  
Labels: `role:security`, `role:devops`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
Security finding 14: an open-source eDiscovery platform self-hosted by law firms needs a secure SDLC; §19 has no security ADR and §18 no security tests.

#### Description
Add CodeQL (C#, TypeScript), secret scanning with push protection plus gitleaks, dependency review and OSV-Scanner, Trivy image scanning, a third-party license check (policy agreed with the PO given the MIT license), OpenSSF Scorecard, SECURITY.md with coordinated-disclosure policy and GitHub private vulnerability reporting. Configure branch protection with required reviews and CODEOWNERS for `Opportunity.Security`, auth and audit code.

#### Acceptance criteria
- [ ] CodeQL and secret scanning run on every PR and on main
- [ ] PRs fail on new Critical/High vulnerabilities unless a documented exception with an expiry date exists
- [ ] A GPL/AGPL *linked* dependency fails the license check; the license report is produced per build
- [ ] SECURITY.md lists contact, supported versions and response SLA; private vulnerability reporting is enabled
- [ ] OpenSSF Scorecard runs in CI and the score is tracked

#### Dependencies
- `E01-T03` — Set up PR CI pipeline with real-dependency integration tests

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** DevOps / SRE
- **Source reviews:** Security & Compliance, DevOps/SRE
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Merges SEC-17 and devops 'Security and supply-chain scanning'. Q-38 decides whether AGPL *bundled* services (e.g. object store) are acceptable.

---

### E01-T05

**Build, sign and publish container images**  
Labels: `role:devops`, `role:security`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
DevOps and Security both require signed, non-root, scanned images with SBOM and provenance (§18, §35).

#### Description
Multi-stage, non-root, multi-arch (amd64/arm64) images for `api`, `worker`, `web` (nginx-unprivileged) and `migrator`, published to GHCR. Chiseled/distroless .NET runtime images, base images pinned by digest. SBOM (SPDX/CycloneDX) per image, keyless cosign signatures, SLSA build provenance via `actions/attest-build-provenance`. Tags: immutable `sha-<short>`, `vX.Y.Z`, `X.Y`, `edge`; never `latest` in Compose.

#### Acceptance criteria
- [ ] `cosign verify` and `gh attestation verify` succeed for published images
- [ ] Images run as non-root with a read-only root filesystem in Compose
- [ ] amd64 and arm64 images both pass the developer-profile smoke test
- [ ] Each image has an attached SBOM; a Critical CVE fails the publish workflow with documented waiver handling

#### Dependencies
- `E01-T02` — Build composable worker host, API conventions and health endpoints
- `E01-T03` — Set up PR CI pipeline with real-dependency integration tests

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** Security & Compliance
- **Source reviews:** DevOps/SRE, Security & Compliance
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

---

### E01-T06

**Automate versioning, changelog and release workflow with upgrade test**  
Labels: `role:devops`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
DevOps proposes SemVer with independent versioning of DB schema, search mapping (ProjectionGeneration) and message `SchemaVersion` (§11) and N/N-1 rolling compatibility; backend requires consumers to accept SchemaVersion N and N-1.

#### Description
Adopt Conventional Commits + release-please. A tag produces images, a Compose bundle (`opportunity-compose-vX.Y.Z.tgz`, images by digest, `.env.example`), changelog, third-party NOTICE aggregation, and a compatibility matrix (DB schema version, projection generation/mapping version, message SchemaVersion). Add an upgrade test: deploy N-1, load data, upgrade to N, run smoke tests. Decide DCO vs CLA.

#### Acceptance criteria
- [ ] The release workflow is fully automated from merging the release PR
- [ ] The N-1 → N upgrade test passes in CI before publish
- [ ] The Compose bundle references images by digest and includes the compatibility matrix
- [ ] Third-party license NOTICE is generated per release

#### Dependencies
- `E01-T05` — Build, sign and publish container images
- `E04-T01` — Build migrator with SQL-first migrations and infrastructure bootstrap

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** —
- **Source reviews:** DevOps/SRE, Backend/Architecture
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Q-43 (release cadence/support policy).
