# DevOps / SRE Review — opportuniTY Architecture Baseline

Reviewer role: DevOps / SRE. Source: `docs/architecture/architecture-baseline.md` (normative per §35).
Repo state at review: `LICENSE` (MIT), `README.md`, `docs/architecture/` only. No `src/`, `deploy/`, `.github/` or ADRs exist yet, so everything below is greenfield.

## Review findings

- **Lite vs Full compose profiles are named but not defined (§4, §16, §29).** §16 lists Lite = Angular, API, PostgreSQL, OpenSearch, RabbitMQ, S3-compatible/local storage, "essential workers"; Full = multiple APIs, OpenSearch cluster, worker pools, observability. Proposal: one `deploy/docker-compose/` base file plus Compose `profiles:`. **Lite** = `postgres`, `opensearch` (1 node), `rabbitmq`, `objectstore`, `api`, `web`, and a single combined `worker` process that hosts the import, index, render and export consumers. That matches the §29 "developer regression profile" exactly. **Full** adds a separate container per worker type (`worker-import/index/render/export/production`) with `deploy.replicas`, a 3-node OpenSearch, a PostgreSQL streaming replica, a load balancer (Traefik/Caddy/nginx) in front of N API replicas, and the `observability` profile (OTel Collector, Prometheus, Grafana, Loki/Tempo or equivalent). The worker host must therefore be composable: one binary with consumers selected by config. This needs to be agreed with backend early (§18 lists separate `Opportunity.Worker.*` projects).
- **Picking the object store is a licensing and maintenance decision, not just a technical one (§16, §34).** The project is MIT. MinIO's server is AGPLv3. During 2025 MinIO also removed most admin-console features from the community edition, stopped publishing community binaries and Docker images, and reportedly moved the community repo to maintenance-only. **This must be re-verified before choosing.** Running an unmodified AGPL server as a separate container is usually not considered to infect the MIT code, but redistributing it inside an "official" bundle and relying on images that may disappear are real risks. Candidates to evaluate: **SeaweedFS** (Apache-2.0, active, S3 gateway; its S3 compatibility is partial), **Garage** (AGPLv3, lightweight, geo-distributed, S3 subset with no object versioning/locking as of last check), **Zenko CloudServer** (Apache-2.0), **RustFS** (Apache-2.0, young project), and a **filesystem-backed provider** for Lite. Verify all of them. Key S3 features to test: multipart upload, presigned URLs, ranged GET, object versioning/Object Lock (useful for audit archival §15 and immutable snapshot manifests §22), bucket replication, and the SDK we use (AWSSDK.S3 / MinIO .NET client). An `IObjectStorage` contract test suite run against each provider in CI should be the selection gate.
- **OpenSearch dev-mode memory is the main obstacle to "easy to run" (§8, §16, §29).** A single node needs `discovery.type=single-node`, a fixed heap (`OPENSEARCH_JAVA_OPTS=-Xms1g -Xmx1g` as the dev minimum, 2g+ for 1M benchmarks), `vm.max_map_count>=262144` on the host (a frequent first-run failure on Linux and WSL2 that Docker Desktop hides), `ulimits memlock/nofile`, and from 2.12 onward `OPENSEARCH_INITIAL_ADMIN_PASSWORD`. Realistically Lite needs **~6–8 GB RAM** for the whole stack (OS 1–2g heap plus off-heap, PG, RabbitMQ, API, workers, web). Document this, add a preflight script, and decide whether the dev profile disables the security plugin (fast) or runs TLS (closer to prod; §23 suggests DLS as defense in depth, which requires the plugin).
- **RPO ≤5 min / RTO ≤1 h (§17) drive the backup design, so each store needs its own strategy.** **PostgreSQL** is authoritative and needs continuous WAL archiving with PITR, for example pgBackRest or WAL-G to object storage, with `archive_timeout ≤ 60s` so the RPO is met even when traffic is light, plus daily or weekly base backups. Restoring a large DB within 1 h requires parallel restore/delta restore and has to be measured. §27 already warns that bulk coding generates heavy WAL, so size archive throughput from those benchmarks. **OpenSearch** is derived (§2), so in principle it can be rebuilt from PG. A full reindex of 10M–100M docs with large text cannot finish in 1 h, though, so the RTO forces **OpenSearch snapshots to object storage** (`repository-s3`) plus **replay of outbox / IndexChunkTask / projection-generation deltas since the snapshot timestamp** (§21, §28). This "snapshot + catch-up from PG watermark" procedure needs an ADR and a drill. **Object storage** holds natives and renders, which cannot be regenerated. It needs bucket replication or versioning to a second site/bucket, and the bundled provider must support that (see the previous bullet). **RabbitMQ** carries transport only (§11, PG owns job state), so the target is redrive from PG rather than queue backup: quorum queues plus a "re-dispatch pending outbox/chunks" tool. **Cross-store consistency** matters: after a PG PITR, the object store may hold orphan blobs and OpenSearch may be ahead of PG. Restore runbooks need a reconciliation step, using version-aware indexing (§21) so OpenSearch is rolled back or rebuilt to the PG truth.
- **Observability is a first-class blocker, not a nice-to-have (§4, §7, §28, §31.7).** Use the OTel SDK in API and workers, sending OTLP to an OTel Collector, which fans out to Prometheus (metrics), Tempo/Jaeger (traces) and Loki/OpenSearch (logs). The **SLO-driving metrics** should be: outbox lag (oldest unpublished `SearchOutbox` age and count), IndexChunkTask backlog/age by status, RabbitMQ queue depth, consumer count, unacked messages and DLQ depth, **index lag** (commit → searchable, histogram, matching the §26 gate ≤2 min bulk and ≤1 s p95 interactive), **projection generation vs committed generation per workspace** (§28 watermark), stale-version-rejection count (a correctness signal, §26), job/chunk retry and attempt counts, search p50/p95/p99 by query class, PG replication lag, WAL archive lag (the RPO signal), and OpenSearch heap, merges and rejected bulk threads. Trace context must travel through the message envelope (`CorrelationId`/`CausationId` in §11 should map to W3C `traceparent`) so one trace spans API → outbox → dispatcher → RabbitMQ → worker → OpenSearch.
- **The CI pipeline needs service containers and scale tests from day one (§18, §20, §32).** Use GitHub Actions with these stages: lint/format, then .NET build + unit, then Angular build + unit, then integration tests against real PG/OpenSearch/RabbitMQ/object store via Testcontainers (preferred over Actions `services:` for parity), then a cross-workspace-access security test suite (§23), then a fault-injection idempotency suite (§26), then image build. Run a nightly/scheduled "regression scale" job (100K–1M synthetic docs from `tools/Opportunity.DataGenerator`) on a self-hosted or larger runner, because hosted runners (16 GB / 4 vCPU on public repos) are too small and too noisy for §29 benchmark numbers. Benchmark results must store hardware, versions, JVM settings, seed and topology (§17, §29), so publish them as JSON artifacts.
- **Container image publishing.** Publish multi-arch (amd64/arm64) images to GHCR (`ghcr.io/<org>/opportunity-{api,worker,web,migrator}`). Use small non-root runtime images (`mcr.microsoft.com/dotnet/aspnet` chiseled/distroless; nginx-unprivileged for Angular). Generate an SBOM (Syft), scan (Trivy/Grype), sign with keyless cosign, and attach SLSA provenance via `actions/attest-build-provenance`. Tags: immutable `sha-<short>`, `vX.Y.Z`, `X.Y`, `edge` from main. Do **not** use `latest` in compose files.
- **Versioning and release.** Use SemVer for the product. Images and compose bundles are versioned together. Use Conventional Commits with release-please (or similar) to generate changelogs and tags. Version DB schema, search mapping (projection generation) and message `SchemaVersion` (§11) independently, publish a compatibility matrix per release, and support only N/N-1 rolling compatibility between API/worker and schema. Release artifacts: images, a `docker-compose` bundle tarball with `.env.example`, a Helm chart later (P2), and a third-party license manifest. Because this is open source, add LICENSE/NOTICE aggregation and a DCO or CLA decision.
- **Config and secrets.** Follow 12-factor: .NET `IOptions` bound from env vars (`Opportunity__Storage__Provider=s3`), validated at startup (`ValidateOnStart`), with fail-fast on missing config. Lite ships `.env.example` and generates random passwords on first run (no default creds baked in). Full/prod reads Docker secrets / files (`*_FILE` convention) and later Kubernetes Secrets / external secret stores. OIDC (§4) client secrets and the storage keys used for future per-workspace encryption (§15, §34) must go through a pluggable secret provider. Add gitleaks secret scanning plus GitHub push protection in CI.
- **DB migrations in deploy.** Migrations must not run implicitly at API startup when multiple replicas start at once. Use a dedicated one-shot `migrator` image/job (EF Core migration bundle, or DbUp/Flyway-style SQL, to be decided with backend) that runs before API/workers (`depends_on: condition: service_completed_successfully`). Migrations follow the expand/contract pattern for zero-downtime upgrades and need a PG advisory lock. Partitioning (§6, §27) means large DDL needs a strategy for online index creation (`CREATE INDEX CONCURRENTLY` cannot run inside a transaction, which affects tool choice). OpenSearch mapping changes follow the §8 alias-based reindex. The migrator should also bootstrap index templates, aliases, RabbitMQ topology (exchanges, queues, DLX, quorum queues) and buckets idempotently.
- **Health checks.** Separate `/health/live` (process only) from `/health/ready` (PG, OpenSearch, RabbitMQ and object store reachable; migrations at expected version). Workers also expose readiness plus a heartbeat/last-consumed metric. Compose `healthcheck:` entries go on every infra service (`pg_isready`, `_cluster/health?wait_for_status=yellow`, `rabbitmq-diagnostics -q ping`, an object-store health endpoint) and are combined with `depends_on: condition: service_healthy`. Readiness should **not** fail just because index lag is high. Report lag as a metric/alert instead, so search remains available with a staleness indicator (§28).
- **Gaps and risks in the baseline.** The doc never says which PostgreSQL / OpenSearch / RabbitMQ / .NET / Node major versions it targets, which is needed for reproducible benchmarks (§29). It does not mention TLS termination or internal mTLS in compose (§15 requires TLS). Audit archival to immutable storage (§15, §33) needs object-lock support, which narrows the object-store choice. No ADR covers backup/DR or observability, so propose adding "ADR-015 Backup/DR & restore consistency" and "ADR-016 Observability & SLOs" to §19.

## Proposed epics and tickets

### EPIC: Repository, CI and supply-chain foundation
Set up the §18 repository layout and GitHub Actions pipeline so every PR builds, tests against real dependencies and produces signed, scannable images. This is the base for automated scale/regression tests required in §20 and §32.
Baseline sections: §18, §20, §26, §29, §32, §35.

#### Scaffold repo layout and build tooling
- **Role:** DevOps
- **Description:** Create the §18 directory skeleton (`src/`, `tests/`, `tools/`, `deploy/docker-compose/`, `deploy/kubernetes/` placeholder, `docs/adr/`). Add a .NET solution with `Directory.Build.props`, central package management, `global.json` pinning the SDK, `.editorconfig`, an Angular workspace, and `CODEOWNERS`, PR/issue templates, `CONTRIBUTING.md`, `SECURITY.md`. Pin tool versions (.NET, Node, PG, OpenSearch, RabbitMQ) in a single `versions` file that compose, CI and benchmarks all read.
- **Acceptance criteria:**
  - `dotnet build` and `npm ci && npm run build` succeed from a clean clone.
  - Tool and service versions are defined in exactly one place and consumed by CI and compose.
  - Dependabot/Renovate is configured for NuGet, npm, Docker and GitHub Actions.
- **Dependencies:** none
- **Phase:** P0
- **Size:** S

#### PR CI pipeline with integration tests on real dependencies
- **Role:** DevOps
- **Description:** GitHub Actions workflow running lint/format, then .NET and Angular unit tests, then integration tests using Testcontainers (PostgreSQL, OpenSearch single-node, RabbitMQ, chosen object store). Cache NuGet/npm. Publish test results and coverage. Add required jobs for the cross-workspace access suite (§23) and the idempotency/fault-injection suite (§26) once those exist.
- **Acceptance criteria:**
  - PRs are blocked on failing build, unit, integration or security-isolation tests (branch protection configured).
  - Integration job completes in under 15 min on GitHub-hosted runners.
  - OpenSearch container starts reliably in CI (heap limited, `vm.max_map_count` set or the Testcontainers equivalent).
  - Test reports are visible in the PR UI.
- **Dependencies:** Scaffold repo layout
- **Phase:** P0
- **Size:** M

#### Security and supply-chain scanning
- **Role:** DevOps
- **Description:** Add CodeQL (C#, TypeScript), gitleaks/secret scanning with push protection, dependency review, Trivy image scanning, and a third-party license check that fails on disallowed licenses in linked dependencies (policy agreed with PO, given the MIT project license). Add OpenSSF Scorecard.
- **Acceptance criteria:**
  - CodeQL and secret scanning run on every PR and on main.
  - A critical CVE in a built image fails the release workflow, with documented waiver handling.
  - The license report artifact is produced per build, and a GPL/AGPL linked dependency fails the check.
- **Dependencies:** PR CI pipeline
- **Phase:** P0
- **Size:** S

#### Nightly scale-regression workflow
- **Role:** DevOps
- **Description:** Scheduled workflow on a self-hosted or large runner that brings up the §29 developer regression topology, generates a seeded synthetic corpus (100K nightly, 1M weekly) with `Opportunity.DataGenerator`, runs import → index → search → bulk-tag → export, and records p50/p95/p99, index lag and environment metadata as JSON artifacts with trend comparison.
- **Acceptance criteria:**
  - Each run stores hardware, service versions, JVM settings, shard layout, corpus seed and config (§17, §29).
  - A regression of more than X% (threshold set with PO) in a tracked metric opens an issue or fails the run.
  - Results are retained for at least 90 days and viewable as a trend (Grafana or a static report).
- **Dependencies:** PR CI pipeline, Lite compose profile, DataGenerator (backend)
- **Phase:** P1
- **Size:** L

### EPIC: Local development and Compose deployment profiles
Deliver `deploy/docker-compose/` with Lite and Full profiles that run the whole platform from one command, including migrations, bootstrap, health checks and secure-by-default configuration. Includes the object-storage provider selection that §16 explicitly requires.
Baseline sections: §3, §4, §11, §15, §16, §29, §34.

#### Object-storage provider evaluation and ADR
- **Role:** DevOps
- **Description:** Evaluate bundled S3-compatible options (MinIO community, SeaweedFS, Garage, Zenko CloudServer, RustFS, plus a filesystem provider for Lite) on **current** license, maintenance and release cadence, availability of official images, S3 API coverage (multipart, presigned, range, versioning, Object Lock, replication), resource footprint and DX. Run the `IObjectStorage` contract suite against each. Write the ADR (§19 item 11 companion).
- **Acceptance criteria:**
  - The ADR records verified license and maintenance status with a date and links (MinIO 2025 changes explicitly checked).
  - The contract test suite passes against the chosen provider and against AWS S3 / Azure Blob emulators (Azurite).
  - A fallback provider is named, and switching providers requires config only.
- **Dependencies:** Storage abstraction (backend)
- **Phase:** P0
- **Size:** M

#### Lite compose profile
- **Role:** DevOps
- **Description:** Base compose file and `lite` profile: PG, OpenSearch single node, RabbitMQ (management plugin), chosen object store, `migrator` one-shot, `api`, combined `worker`, `web`, reverse proxy with local TLS. Add healthchecks and `depends_on` conditions on all services, named volumes, `.env.example`, a first-run secret generator, and a `make up` / `./opportunity.sh up` wrapper with a preflight check (RAM, `vm.max_map_count`, Docker version).
- **Acceptance criteria:**
  - From a clean clone, `./opportunity.sh up` reaches all-healthy in under 5 min on an 8 GB machine (Linux, macOS, WSL2 documented).
  - No hard-coded default passwords; secrets are generated into `.env` on first run.
  - The preflight script fails clearly on low `vm.max_map_count` or less than 6 GB available memory.
  - The §32 vertical slice smoke test (import sample DAT → search → code → export) passes against the stack.
- **Dependencies:** Object-storage ADR, migrator image, health endpoints
- **Phase:** P0
- **Size:** M

#### Migrator and infrastructure bootstrap job
- **Role:** DevOps
- **Description:** Build a `migrator` image that idempotently applies PG migrations under an advisory lock, creates OpenSearch index templates and aliases, declares RabbitMQ exchanges, queues, DLX/retry queues (quorum queues), and creates buckets. API and workers start only after it completes successfully. Document the expand/contract migration rules.
- **Acceptance criteria:**
  - Running the migrator twice is a no-op with exit code 0.
  - Two concurrent migrator runs do not corrupt state (advisory lock test).
  - The API `/health/ready` reports unready if the schema version is behind the expected version.
  - Restarting the RabbitMQ topology with existing queues causes no error.
- **Dependencies:** Scaffold repo, Data/Messaging projects (backend)
- **Phase:** P0
- **Size:** M

#### Health checks and readiness contract
- **Role:** DevOps
- **Description:** Agree with backend on, and wire, `/health/live` and `/health/ready` for API and workers (ASP.NET HealthChecks), dependency checks with timeouts, and the worker heartbeat metric. Add compose healthchecks for all infra containers.
- **Acceptance criteria:**
  - Killing PG makes `/health/ready` fail within 10 s, and `/health/live` stays OK.
  - High index lag does not flip readiness (it is covered by an alert instead).
  - Every compose service has a healthcheck, and `docker compose ps` shows all healthy.
- **Dependencies:** Lite compose profile
- **Phase:** P0
- **Size:** S

#### Full / scale compose profile
- **Role:** DevOps
- **Description:** `full` profile: N API replicas behind the load balancer, a separate scalable container per worker type, 3-node OpenSearch with TLS and the security plugin, PG primary plus streaming replica, RabbitMQ with quorum queues, object store in distributed/replicated mode if supported, and the observability profile enabled. This matches the §29 enterprise reference topology on a single large host or a small Swarm.
- **Acceptance criteria:**
  - `docker compose --profile full up --scale worker-index=4` works, and the load is spread across consumers.
  - OpenSearch cluster reports green with 3 nodes, and PG replication lag is exported as a metric.
  - Killing one OpenSearch node or one worker during bulk tagging does not fail the job (§2.4 retry assumptions).
- **Dependencies:** Lite compose profile, Observability stack
- **Phase:** P1
- **Size:** L

### EPIC: Observability and SLOs
Instrument API and workers with OpenTelemetry and ship a self-hostable metrics, traces and logs stack with dashboards and alerts for the consistency signals the baseline makes mandatory (outbox lag, index lag, projection watermark).
Baseline sections: §4, §7, §17, §21, §26, §28, §31.7.

#### OTel Collector and backend stack
- **Role:** DevOps
- **Description:** Add an `observability` compose profile with an OTel Collector (contrib) receiving OTLP from services, Prometheus (with the RabbitMQ built-in Prometheus plugin, postgres_exporter and the OpenSearch exporter/plugin), Tempo or Jaeger for traces, Loki for logs, and Grafana with provisioned datasources. Collector config lives in the repo and ships as part of the release.
- **Acceptance criteria:**
  - A single API request shows a trace spanning API → PG → outbox dispatcher → RabbitMQ → index worker → OpenSearch.
  - Logs carry `trace_id`, `WorkspaceId`, `JobId`, `CorrelationId`, with no document content or PII in logs (logging policy documented).
  - The stack runs in at most ~1.5 GB additional RAM.
- **Dependencies:** Lite compose profile; envelope `traceparent` propagation (backend)
- **Phase:** P0
- **Size:** M

#### Consistency and pipeline metrics, dashboards and alerts
- **Role:** DevOps
- **Description:** Define the metric names and semantics with backend, then build Grafana dashboards and Prometheus alert rules for: outbox oldest-age and count, IndexChunkTask backlog by status and age, queue depth / unacked / DLQ / consumer count, commit → searchable latency histogram (interactive vs bulk vs security-affecting §24), per-workspace projection generation vs committed generation (§28), stale-version rejections, chunk retries, search latency by class, PG replication and WAL-archive lag, and OpenSearch heap, merges and bulk rejections.
- **Acceptance criteria:**
  - Dashboards are provisioned from the repo as code.
  - The alerts for interactive lag p95 > 1 s, bulk lag > 2 min (§26), DLQ > 0, WAL archive lag > 4 min (RPO) and outbox age > 60 s fire in a test using induced failures.
  - The metric catalog is documented in `docs/operations/metrics.md`.
- **Dependencies:** OTel Collector stack; outbox/IndexChunkTask implementation (backend)
- **Phase:** P0
- **Size:** M

### EPIC: Backup, disaster recovery and operations
Make RPO ≤5 min and RTO ≤1 h demonstrable by drill rather than aspirational, across all four stores, with a documented restore-consistency procedure that relies on PostgreSQL authority and version-aware reindexing.
Baseline sections: §2, §7, §8, §15, §17, §21, §27, §28.

#### Backup/DR ADR and restore-consistency design
- **Role:** DevOps
- **Description:** Write an ADR covering PG PITR (tool choice, for example pgBackRest or WAL-G), OpenSearch snapshot cadence and repository, object-store replication/versioning, RabbitMQ redrive strategy, and the post-restore reconciliation procedure (OpenSearch snapshot plus catch-up from PG generation/watermark; orphan blob sweep; re-dispatch of pending outbox and chunks). Include RTO math for 1M/10M/100M docs.
- **Acceptance criteria:**
  - The ADR is approved and added to §19.
  - It states explicitly whether "rebuild OpenSearch from PG" meets RTO at each scale milestone, backed by measured reindex throughput.
  - Reconciliation steps are defined so OpenSearch never ends up ahead of PG truth.
- **Dependencies:** Object-storage ADR; projection generation design (backend)
- **Phase:** P1
- **Size:** M

#### PostgreSQL WAL archiving and PITR
- **Role:** DevOps
- **Description:** Configure continuous WAL archiving with `archive_timeout` ≤ 60 s to object storage, plus scheduled full/differential backups, retention, and encryption. Expose archive-lag metrics. Provide `restore-to-time` scripts for compose.
- **Acceptance criteria:**
  - A drill restores to a timestamp with ≤5 min data loss.
  - Restoring a 1M-doc benchmark DB completes, with the time recorded. A projected 10M-doc restore time is documented.
  - Backups verify automatically (`pgbackrest verify` or a restore test) on a weekly schedule.
- **Dependencies:** Backup/DR ADR, Lite/Full compose
- **Phase:** P1
- **Size:** M

#### OpenSearch snapshots and object-storage replication
- **Role:** DevOps
- **Description:** Register an S3 snapshot repository, schedule snapshots via Snapshot Management policies, and build a restore + catch-up tool that replays index work for documents whose version or generation is newer than the snapshot. Configure versioning plus replication (or a mirror job) for the artifact buckets.
- **Acceptance criteria:**
  - After restoring an older snapshot, the catch-up tool converges and the 0-stale-version check passes (§26).
  - Deleting a native object can be recovered from a version or the replica.
  - The full DR drill (all stores) completes in ≤1 h on the 1M reference environment, and a runbook is published.
- **Dependencies:** PG PITR, Backup/DR ADR
- **Phase:** P1
- **Size:** L

#### Operational runbooks and DLQ / redrive tooling
- **Role:** DevOps
- **Description:** Provide an admin CLI or scripts for inspecting and redriving DLQs, re-dispatching stuck outbox/IndexChunkTask records, triggering an alias-based reindex (§8) and workspace deletion verification (§15). Write runbooks for each alert.
- **Acceptance criteria:**
  - Every alert links to a runbook.
  - Redrive is idempotent, as shown by a test that replays the same DLQ twice.
  - Runbooks are tested in a game-day exercise and recorded.
- **Dependencies:** Consistency metrics/alerts
- **Phase:** P1
- **Size:** M

### EPIC: Release engineering and Kubernetes
Produce versioned, signed, reproducible releases that open-source users can deploy with confidence, and add Kubernetes packaging once the 1M benchmark passes (§33 defers K8s).
Baseline sections: §4, §11, §18, §33, §34.

#### Image build, signing and publishing
- **Role:** DevOps
- **Description:** Multi-stage, non-root, multi-arch images for `api`, `worker`, `web` and `migrator`, published to GHCR with cosign keyless signatures, SBOM (SPDX) and build-provenance attestations. Use OCI labels and reproducible tags.
- **Acceptance criteria:**
  - `cosign verify` and `gh attestation verify` succeed for released images.
  - Images run as non-root with a read-only root filesystem in compose.
  - amd64 and arm64 images both pass the Lite smoke test.
- **Dependencies:** PR CI pipeline
- **Phase:** P0
- **Size:** M

#### Versioning, changelog and release workflow
- **Role:** DevOps
- **Description:** Adopt SemVer with Conventional Commits and release-please. A tag triggers a release with images, the compose bundle (`opportunity-compose-vX.Y.Z.tgz` with pinned image digests), changelog, third-party NOTICE, and the compatibility matrix (DB schema version, projection generation/mapping version, message SchemaVersion). Also an upgrade test: deploy N-1, load data, upgrade to N, run the smoke test.
- **Acceptance criteria:**
  - The release workflow is fully automated from merging a release PR.
  - The upgrade N-1 → N test passes in CI before publish.
  - The compose bundle references images by digest.
- **Dependencies:** Image publishing, Migrator
- **Phase:** P1
- **Size:** M

#### Helm chart and Kubernetes reference deployment
- **Role:** DevOps
- **Description:** Helm chart for API, workers (HPA/KEDA scaling on RabbitMQ queue depth), web and the migrator Job (pre-upgrade hook). Document integration with operators (CloudNativePG, OpenSearch Operator, RabbitMQ Cluster Operator) rather than bundling stateful services.
- **Acceptance criteria:**
  - `helm install` on kind passes the smoke test in CI.
  - KEDA scales index workers on queue depth in a load test.
  - The chart passes `helm lint` and kubeconform, and is signed/published as an OCI artifact.
- **Dependencies:** Release workflow; 1M benchmark passed (§33)
- **Phase:** P2
- **Size:** L

## Open questions for the product owner

1. **Object-storage bundling and license policy:** Is it acceptable to ship an AGPL-licensed object store (MinIO or Garage) as an unmodified container in the official compose bundle, or should the default be permissive-only (for example SeaweedFS or a filesystem provider for Lite)? Who owns the third-party license policy?
2. **Minimum Lite hardware:** What is the target minimum machine for Lite (8 GB laptop? 16 GB?), and must it run on Windows/WSL2 and Apple Silicon? This decides the OpenSearch heap, whether to merge workers, and multi-arch builds.
3. **RPO/RTO scope:** Do the RPO ≤5 min and RTO ≤1 h targets (§17) apply to the Lite/self-hosted compose deployment, or only to a Full/enterprise deployment? Is cross-region or off-site DR in scope for v1?
4. **Benchmark infrastructure:** Who funds and owns the §29 reference hardware (self-hosted runners vs cloud on demand), and may benchmark results and environment details be published publicly?
5. **Supported deployment targets for v1:** Compose only, or also a single-host Docker Swarm? When should Helm/K8s become officially supported (after 1M or after 10M)?
6. **Observability backend:** Should we ship our own Grafana/Prometheus/Loki/Tempo stack, or only an OTel Collector config and let operators bring their own backend? Is OpenSearch an acceptable log backend, given we already run it?
7. **Release cadence and support policy:** What release cadence (for example monthly minor) and how many versions back receive security fixes? Do upgrades need to be zero-downtime from v1.0?
8. **Security defaults:** Must Lite run TLS and the OpenSearch security plugin by default (closer to production, §15/§23 DLS), or may local dev disable them for simplicity, with a "production hardening" profile instead?
