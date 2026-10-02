# E19 — Deployment Profiles, Observability & Operations

**Labels:** `epic`, `role:backend`, `role:devops`, `role:performance`, `role:security`, `P0`  
**Starts in:** M0 - Foundation & Benchmark Harness  
**Tickets:** 9

## Goal
Run the platform from one command (developer, Lite, Full profiles), select and abstract object storage, instrument everything with OpenTelemetry and SLO alerts, and make RPO ≤5 min / RTO ≤1 h demonstrable through backup and restore-consistency tooling.

## Baseline sections
§2, §4, §7, §15, §16, §17, §21, §28, §29, §31.7, §33, §34

## Scope / out of scope
**In scope**
- Object-storage abstraction + provider evaluation
- Compose developer profile
- OTel instrumentation + observability profile
- Consistency metrics, dashboards, alerts
- Lite hardening + Full profile
- Backup/DR ADR, PITR, snapshots, replication
- Helm (deferred)

**Out of scope**
- Kubernetes-first deployment (§1 deferred)

## Contributing roles
- **Roles:** Backend, DevOps / SRE, Performance, Security & Compliance
- **Source reviews:** DevOps/SRE, Backend/Architecture, Security & Compliance, QA & Performance
- **Milestones spanned:** M0 - Foundation & Benchmark Harness, M2 - 1M Benchmark & Architecture Blockers, M3 - MVP Feature Complete, M5 - Post-MVP / Deferred

## Exit criteria
- [ ] Developer profile reaches all-healthy from a clean clone in < 5 min
- [ ] One trace spans API → outbox → dispatcher → RabbitMQ → worker → OpenSearch
- [ ] A DR drill restores all stores with ≤5 min data loss and converges OpenSearch to PG truth

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E19-T01](#e19-t01) | Implement object storage abstraction and provider contract suite | M0 | M | E01-T01, E02-T06 |
| [E19-T02](#e19-t02) | Evaluate bundled object-storage providers and record ADR | M0 | M | E19-T01 |
| [E19-T03](#e19-t03) | Create Docker Compose developer profile | M0 | M | E19-T02, E04-T01, E01-T02 |
| [E19-T04](#e19-t04) | Instrument with OpenTelemetry and add observability profile | M0 | M | E01-T02, E19-T03 |
| [E19-T05](#e19-t05) | Build consistency and pipeline metrics, dashboards and alerts | M2 | M | E19-T04, E07-T08, E06-T04 |
| [E19-T06](#e19-t06) | Harden Lite profile and add Full/scale Compose profile | M3 | L | E19-T03, E19-T05, E05-T09 |
| [E19-T07](#e19-t07) | Write backup/DR ADR and restore-consistency design | M3 | M | E19-T02, E07-T08 |
| [E19-T08](#e19-t08) | Implement PostgreSQL PITR, OpenSearch snapshots and artifact replication | M3 | L | E19-T07, E19-T06 |
| [E19-T09](#e19-t09) | Publish Helm chart and Kubernetes reference deployment | M5 | L | E01-T06, E18-T05 |

---

### E19-T01

**Implement object storage abstraction and provider contract suite**  
Labels: `role:backend`, `role:devops`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§4 provider-neutral storage, §15 prefix deletion, ADR-011.

#### Description
`IObjectStore` (put/get/stream/range/head/delete-prefix/presign, multipart, checksum) with S3 and Azure Blob providers plus a filesystem provider for Lite; deterministic workspace-prefixed addressing; `KeyId` recorded per object; contract test suite runnable against any provider.

#### Acceptance criteria
- [ ] S3-compatible, Azurite and filesystem providers pass the same contract suite
- [ ] Re-uploading identical content to the same key is a no-op verified by checksum
- [ ] Every object records a KeyId; signed URLs are only obtainable through the gateway API surface

#### Dependencies
- `E01-T01` — Scaffold repository layout, .NET solution and Angular workspace
- `E02-T06` — Write ADR-011/012/014: object storage addressing, redaction and page model, retention and deletion lifecycle

#### Roles
- **Owner:** Backend
- **Contributing:** DevOps / SRE
- **Source reviews:** Backend/Architecture, DevOps/SRE, Security & Compliance
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

---

### E19-T02

**Evaluate bundled object-storage providers and record ADR**  
Labels: `role:devops`, `P0`, `size:M`, `adr` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§16: choose bundled storage after checking current license, maintenance, S3 compatibility and DX. DevOps: MinIO server is AGPLv3 and in 2025 reportedly removed community console features, stopped publishing community images and moved to maintenance-only — must be re-verified.

#### Description
Evaluate MinIO community, SeaweedFS (Apache-2.0), Garage (AGPLv3), Zenko CloudServer (Apache-2.0), RustFS (Apache-2.0) and the filesystem provider on verified license, maintenance/release cadence, official images, S3 coverage (multipart, presigned, range, versioning, Object Lock, replication), footprint and DX, running the `E19-T01` contract suite against each.

#### Acceptance criteria
- [ ] ADR records verified license and maintenance status with date and links (MinIO 2025 changes explicitly checked)
- [ ] Contract suite passes against the chosen provider and against AWS S3 / Azurite
- [ ] A fallback provider is named; switching providers is configuration-only
- [ ] Object Lock support (needed for audit archival) is documented per candidate

#### Dependencies
- `E19-T01` — Implement object storage abstraction and provider contract suite

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** —
- **Source reviews:** DevOps/SRE, Security & Compliance
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Q-38.

---

### E19-T03

**Create Docker Compose developer profile**  
Labels: `role:devops`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§16, §29 developer regression profile: 1 PG, 1 OpenSearch node, 1 RabbitMQ, 1 object store, API, essential workers. DevOps: OpenSearch needs `discovery.type=single-node`, fixed heap, `vm.max_map_count ≥ 262144`, `OPENSEARCH_INITIAL_ADMIN_PASSWORD` (≥ 2.12); realistic RAM ~6–8 GB.

#### Description
Base Compose file with `dev` profile: postgres, opensearch (1 node), rabbitmq (management), chosen object store, migrator one-shot, api, combined worker, web; healthchecks on every service (`pg_isready`, `_cluster/health?wait_for_status=yellow`, `rabbitmq-diagnostics -q ping`, store health) with `depends_on: service_healthy/service_completed_successfully`; named volumes; `.env.example`; preflight script (RAM, `vm.max_map_count`, Docker version); seed script creating a demo workspace. No Redis/Valkey (§16, §34).

#### Acceptance criteria
- [ ] From a clean clone the stack reaches all-healthy in < 5 min on a 16 GB laptop (Linux, macOS, WSL2 documented)
- [ ] Preflight fails clearly on low `vm.max_map_count` or < 6 GB available memory
- [ ] `docker compose ps` shows every service healthy; no Redis/Valkey service exists
- [ ] Images are referenced by version/digest, never `latest`

#### Dependencies
- `E19-T02` — Evaluate bundled object-storage providers and record ADR
- `E04-T01` — Build migrator with SQL-first migrations and infrastructure bootstrap
- `E01-T02` — Build composable worker host, API conventions and health endpoints

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** —
- **Source reviews:** DevOps/SRE, Backend/Architecture, QA & Performance
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Q-39.

---

### E19-T04

**Instrument with OpenTelemetry and add observability profile**  
Labels: `role:devops`, `role:backend`, `P0`, `size:M` · Milestone: M0 - Foundation & Benchmark Harness

#### Context
§4 OpenTelemetry; devops: observability is a first-class blocker (§31.7).

#### Description
OTel SDK in API and workers (traces, metrics, logs, structured JSON with WorkspaceId, JobId, CorrelationId, trace_id), W3C trace context propagated through message headers; `observability` Compose profile with OTel Collector (contrib), Prometheus (RabbitMQ plugin, postgres_exporter, OpenSearch exporter), Tempo/Jaeger, Loki, Grafana with provisioned datasources. Bounded metric cardinality (top-N workspaces + other). Logging policy: no document content or PII.

#### Acceptance criteria
- [ ] One trace spans API → PG → outbox dispatcher → RabbitMQ → index worker → OpenSearch (once the pipeline exists)
- [ ] Logs carry trace_id, WorkspaceId, JobId and CorrelationId and contain no document content
- [ ] Observability profile adds ≤ ~1.5 GB RAM

#### Dependencies
- `E01-T02` — Build composable worker host, API conventions and health endpoints
- `E19-T03` — Create Docker Compose developer profile

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** Backend
- **Source reviews:** DevOps/SRE, Backend/Architecture
- **Milestone / phase / size:** M0 - Foundation & Benchmark Harness / P0 / M

#### Notes
Merges backend 'OpenTelemetry baseline' and devops 'OTel Collector and backend stack'. Q-42.

---

### E19-T05

**Build consistency and pipeline metrics, dashboards and alerts**  
Labels: `role:devops`, `role:performance`, `P0`, `size:M` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§7, §21, §26, §28: outbox lag, index lag and watermark are the SLO-driving signals.

#### Description
Metric catalog (`docs/operations/metrics.md`): outbox oldest-age/count, IndexChunkTask backlog by status/age, queue depth/unacked/DLQ/consumers, commit→searchable histogram (interactive vs bulk vs security-affecting), per-workspace projection vs committed generation, stale-version rejections, chunk retries, search latency by class, PG replication and WAL-archive lag, OpenSearch heap/merges/bulk rejections. Grafana dashboards and Prometheus alerts as code.

#### Acceptance criteria
- [ ] Alerts for interactive lag p95 > 1 s, bulk lag > 2 min, DLQ > 0, WAL archive lag > 4 min and outbox age > 60 s fire in an induced-failure test
- [ ] Dashboards are provisioned from the repo

#### Dependencies
- `E19-T04` — Instrument with OpenTelemetry and add observability profile
- `E07-T08` — Implement search generation watermark and freshness API
- `E06-T04` — Build outbox dispatcher service

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** Performance
- **Source reviews:** DevOps/SRE, Backend/Architecture, QA & Performance
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / M

---

### E19-T06

**Harden Lite profile and add Full/scale Compose profile**  
Labels: `role:devops`, `role:security`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§16 Lite and Full profiles; §15 TLS; security Q8/devops Q8 Lite trust model.

#### Description
Lite: `./opportunity.sh up` wrapper, first-run secret generation (no default passwords), local TLS reverse proxy, OpenSearch security plugin decision, documented trust model. Full: N API replicas behind a load balancer, separate scalable container per worker type, 3-node OpenSearch with TLS + security plugin, PG primary + streaming replica, RabbitMQ quorum queues, replicated object store, observability profile, distinct per-worker credentials.

#### Acceptance criteria
- [ ] Lite reaches healthy in < 5 min on an 8 GB machine with the combined worker; no hard-coded passwords
- [ ] `docker compose --profile full up --scale worker-index=4` spreads load across consumers
- [ ] OpenSearch reports green with 3 nodes; PG replication lag is exported
- [ ] Killing one OpenSearch node or one worker during bulk tagging does not fail the job

#### Dependencies
- `E19-T03` — Create Docker Compose developer profile
- `E19-T05` — Build consistency and pipeline metrics, dashboards and alerts
- `E05-T09` — Introduce secret and key-provider abstraction with envelope encryption

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** Security & Compliance
- **Source reviews:** DevOps/SRE, Security & Compliance
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
Q-01, Q-39, Q-41.

---

### E19-T07

**Write backup/DR ADR and restore-consistency design**  
Labels: `role:devops`, `role:backend`, `P1`, `size:M`, `adr` · Milestone: M3 - MVP Feature Complete

#### Context
§17 RPO/RTO; devops: each store needs its own strategy; a full reindex of 10M–100M docs cannot finish in 1 h.

#### Description
ADR-016: PG PITR (pgBackRest or WAL-G, `archive_timeout ≤ 60 s`), OpenSearch snapshot cadence and repository, object-store versioning/replication, RabbitMQ redrive from PG (no queue backup), post-restore reconciliation (OpenSearch snapshot + catch-up from PG generation/watermark, orphan blob sweep, re-dispatch pending work) so OpenSearch never ends ahead of PG; RTO math for 1M/10M/100M.

#### Acceptance criteria
- [ ] ADR states whether rebuild-from-PG meets RTO at each scale milestone, backed by measured reindex throughput
- [ ] Reconciliation steps guarantee OpenSearch never ends ahead of PG truth

#### Dependencies
- `E19-T02` — Evaluate bundled object-storage providers and record ADR
- `E07-T08` — Implement search generation watermark and freshness API

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** Backend
- **Source reviews:** DevOps/SRE, QA & Performance
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Q-40.

---

### E19-T08

**Implement PostgreSQL PITR, OpenSearch snapshots and artifact replication**  
Labels: `role:devops`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
ADR-016; devops backup tickets.

#### Description
Continuous WAL archiving to object storage with scheduled full/differential backups, retention, encryption and archive-lag metrics; `restore-to-time` scripts; OpenSearch S3 snapshot repository with Snapshot Management policies and a restore + catch-up tool replaying newer versions/generations; versioning + replication (or mirror job) for artifact buckets; weekly automated backup verification.

#### Acceptance criteria
- [ ] A drill restores PG to a timestamp with ≤ 5 min data loss; 1M-doc restore time recorded and 10M projected
- [ ] After restoring an older OpenSearch snapshot the catch-up tool converges and the 0-stale-version check passes
- [ ] A deleted native object is recoverable from version or replica
- [ ] Full DR drill (all stores) completes in ≤ 1 h on the 1M reference environment; runbook published

#### Dependencies
- `E19-T07` — Write backup/DR ADR and restore-consistency design
- `E19-T06` — Harden Lite profile and add Full/scale Compose profile

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** —
- **Source reviews:** DevOps/SRE
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

---

### E19-T09

**Publish Helm chart and Kubernetes reference deployment**  
Labels: `role:devops`, `P2`, `size:L` · Milestone: M5 - Post-MVP / Deferred

#### Context
§1, §33: Kubernetes-first deployment deferred.

#### Description
Helm chart for API, workers (HPA/KEDA on RabbitMQ queue depth), web and migrator Job (pre-upgrade hook); documentation for CloudNativePG, OpenSearch Operator and RabbitMQ Cluster Operator instead of bundling stateful services.

#### Acceptance criteria
- [ ] `helm install` on kind passes the smoke test in CI
- [ ] KEDA scales index workers on queue depth in a load test
- [ ] Chart passes `helm lint` and kubeconform and is signed/published as an OCI artifact

#### Dependencies
- `E01-T06` — Automate versioning, changelog and release workflow with upgrade test
- `E18-T05` — Decide ADR-004a/004b and update performance status

#### Roles
- **Owner:** DevOps / SRE
- **Contributing:** —
- **Source reviews:** DevOps/SRE
- **Milestone / phase / size:** M5 - Post-MVP / Deferred / P2 / L

#### Notes
Q-41.
