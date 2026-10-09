# Docker Compose: developer profile (Lite)

> **Evaluation and development only. Do not load real client data.**
> This profile has no TLS, runs OpenSearch with its security plugin **disabled** (no authentication on port 9200),
> has no malware scanning and no sandboxed renderers, and keeps secrets in a local `.env` file. Full is the only
> supported production profile (decision Q-01); it arrives with `E19-T06`.

One machine, one of everything (architecture baseline §16/§29 developer regression profile):

| Service | Image | What it does | Host port (127.0.0.1) |
|---|---|---|---|
| `postgres` | `postgres:$POSTGRES@$POSTGRES_DIGEST` | Authoritative store. First start creates the database and logins (`postgres/init`) | 5432 |
| `opensearch` | `opensearchproject/opensearch:$OPENSEARCH@…` | Single node, `discovery.type=single-node`, fixed heap (`OPENSEARCH_HEAP`, default 1g) | 9200 |
| `rabbitmq` | `rabbitmq:$RABBITMQ@…` | Broker with the management UI and Prometheus plugins | 5672, 15672 (UI) |
| `keycloak` | `keycloak/keycloak:$KEYCLOAK@…` (Apache-2.0) | Developer OIDC provider: realm `opportunity` with demo users and groups, imported from [`keycloak/opportunity-realm.json`](keycloak/opportunity-realm.json) on every start (in-memory, nothing persists) | 8180 |
| `migrator` | built from `deploy/docker/dotnet.Dockerfile` (`migrator`) | One-shot: applies migrations and bootstrap steps, then exits 0 | — |
| `api` | `…` (`api`) | REST API; healthy = `/health/ready` (includes the schema version check) | 8081 |
| `worker` | `…` (`worker`), `Workers__Enabled=all` | Combined worker: every worker type in one process | — |
| `web` | `deploy/docker/web.Dockerfile` | Angular UI served by nginx | 8080 |
| `seaweedfs` | `chrislusf/seaweedfs:$SEAWEEDFS@…` | **Optional** (`--profile s3`): S3-compatible store | 8333 |
| `otel-collector`, `prometheus`, `jaeger`, `loki`, `grafana`, `postgres-exporter`, `opensearch-exporter` | see [Observability profile](#observability-profile) | **Optional** (`--profile observability`): telemetry backends | 3000 (Grafana), 9090, 16686, 4317/4318 |

- **Versions** come from [`versions.env`](../../versions.env), the single source of truth: third-party images are
  referenced as `tag@digest`, never `latest`. Compose reads it through `--env-file ../../versions.env`; the helper
  script always passes it.
- **Start order** is enforced with `depends_on`: postgres, opensearch and rabbitmq must be `service_healthy` before
  the migrator runs (it declares the RabbitMQ topology from `ConnectionStrings__RabbitMq`: work exchange, one quorum
  queue per lane and worker type, retry tiers, DLX/`*.dlq` and parking queues; index templates follow with E07); api
  and worker wait for `migrator: service_completed_successfully`; web waits for a healthy api.
- **Object store**: the filesystem provider on the named volume `objects`, shared by api and worker (ADR-020 rule 1).
  For S3 semantics, start SeaweedFS with `COMPOSE_PROFILES=s3` and set `OPPORTUNITY_OBJECT_STORAGE=S3` in `.env`.
- **Encryption at rest**: workspace objects are envelope-encrypted with per-workspace data keys (E05-T09). The local key
  store lives in the named volume `keys` (api and worker), separate from `objects`; back it up separately and see
  [docs/operations/keys-and-secrets.md](../../docs/operations/keys-and-secrets.md) for rotation and the `keys …` CLI.
  `OPPORTUNITY_OBJECT_ENCRYPTION=ProviderSse` in `.env` switches it off. No secret is committed: every password, the
  S3 secret and the dev OIDC client secret come from `.env` (`./opportunity.sh init`; rerun it after an update to add
  new ones), and any setting can also be passed as a file (`<Setting>_FILE`, e.g. `ConnectionStrings__App_FILE`).
- **No Redis/Valkey** (§16, §34). CI fails if one appears.
- **Database logins**: `opportunity_owner` runs the migrator and owns every object (no superuser, no CREATEROLE);
  `opportunity_runtime` (api and worker, `ConnectionStrings__App`) is a member of `opportunity_app`: DML only.
- The application containers run as in production: non-root, `read_only`, `cap_drop: [ALL]`, `no-new-privileges`.

## Signing in (developer IdP)

Open the app at <http://localhost:8080/> and choose **Sign in**: you sign in at Keycloak and come back to your
workspace list. The web container forwards `/api` and `/bff` to the API (`web/api-proxy.conf`), so the app, the API and
the BFF session share one origin, as the BFF design requires
([docs/security/authentication.md](../../docs/security/authentication.md)). Use `localhost`, not `127.0.0.1`: the
session cookie is `Secure` and `__Host-` prefixed, which browsers accept over plain HTTP only for `localhost`.

| User | Groups | Password |
|---|---|---|
| `admin.dev` | `workspace-admins` | `opportunity` |
| `reviewer.dev` | `reviewers` | `opportunity` |
| `privilege.dev` | `reviewers`, `privilege-reviewers` | `opportunity` |
| `walled.dev` | `reviewers`, `wall-project-falcon` (ethical-wall demo, Q-13) | `opportunity` |
| `auditor.dev` | `auditors` | `opportunity` |

Keycloak's admin console is <http://localhost:8180/admin> (user `admin`, password `KEYCLOAK_ADMIN_PASSWORD` from
`.env`); changes made there are lost when the container is re-created. The realm registers redirect URIs for the app
on port 8080 (and the API directly on 8081), `ng serve` on 4200 (set `OPPORTUNITY_PUBLIC_ORIGIN=http://localhost:4200` and proxy `/api` and `/bff`
to the API) and `https://localhost`, and back-channel logout to `http://api:8080/bff/backchannel-logout`. These demo
credentials are public: never expose this IdP beyond your machine.

## Hardware and platforms (Q-39)

| | Minimum | Recommended |
|---|---|---|
| Memory available to Docker | **8 GB** (combined worker, `OPENSEARCH_HEAP=1g`) | **16 GB** (`OPENSEARCH_HEAP=2g` is comfortable) |
| CPU | 4 cores | 8 cores |
| Disk | 15 GB free (images, build cache, volumes) | 30 GB |

Supported: **Linux**, **macOS on Apple Silicon** (Docker Desktop, or OrbStack/Colima) and **Windows with WSL2**
(Docker Desktop with the WSL2 backend; keep the clone inside the WSL filesystem, not under `/mnt/c`, or builds are
slow). Docker Engine ≥ 25 and Docker Compose ≥ 2.24. Images are multi-arch (`linux/amd64`, `linux/arm64`).
On Docker Desktop, set the memory limit under *Settings → Resources* (or `.wslconfig` on WSL2): the preflight checks
what Docker reports, not the laptop's total.

## Commands

Run from this directory (`deploy/docker-compose`). `make <target>` does the same as `./opportunity.sh <command>`.

| Command | What it does |
|---|---|
| `./opportunity.sh init` | Creates `.env` from [`.env.example`](.env.example) with random passwords (mode 600, git-ignored). Keeps an existing `.env`, adding only secrets that are new in `.env.example` |
| `./opportunity.sh preflight` | Fails on Docker Engine < 25, Compose < 2.24, < 6 GB memory for Docker or free on a Linux host, or `vm.max_map_count` < 262144; warns below 16 GB |
| `./opportunity.sh up` | `init` + `preflight` + build the four images + start + wait until every service is healthy (default timeout 10 min, `OPPORTUNITY_WAIT_TIMEOUT`) |
| `./opportunity.sh seed` | Creates the demo workspace `00000000-0000-4000-8000-00000000d3e0` with demo coding fields (Responsiveness, Issues, Key Document, Reviewer Comments) in its default coding layout (idempotent) |
| `./opportunity.sh demo-documents [file.dat]` | Imports demo documents into the demo workspace through the real import API, signed in as `admin.dev`. Default: `seed/demo-volume`, 10 readable, made-up documents with extracted text (a supplier price dispute: emails, an email with two attachments, a privileged legal email, meeting notes and one unrelated memo), copied into the import share so the import reads the text. Try searches such as `"price increase"`, `Heron`, `"shipment delay"`, `privileged`, `penalty` or `picnic`, then open a result: hits are highlighted in the viewer. A `file.dat` argument imports that load file as metadata only (e.g. `seed/demo-documents.dat`: 10 generated documents with an email family, a duplicate and loose files). Each run imports another copy; run `reset` first for a clean set. Bigger sets: `dotnet run --project tools/Opportunity.DataGenerator -- generate --seed 42 --documents 500 --volumes --no-natives --no-images --no-text --out ./demo` and pass `./demo/VOL001/DATA/VOL001.dat` |
| `./opportunity.sh ps` | Service status; every service shows `(healthy)`, the migrator `Exited (0)` |
| `./opportunity.sh logs [service]` | Follows logs (`make logs SERVICE=api`) |
| `./opportunity.sh down` | Stops and removes the containers; data volumes are kept |
| `./opportunity.sh reset [-y]` | Removes containers **and all volumes** (database, index, queues, objects) |
| `./opportunity.sh compose …` | Any other Compose command with the right files, e.g. `compose config`, `compose build api` |

**Import share owned by root?** If `demo-documents` says the import share is not writable, Docker created `import-share/` before the script did (older versions of this script): run `sudo chown -R "$USER": deploy/docker-compose/import-share` once. The script now creates the folder itself before starting containers.

Without the script:

```bash
cp .env.example .env    # then fill in every empty *_PASSWORD / *_SECRET_KEY
docker compose --env-file ../../versions.env --env-file .env up -d --build --wait
docker compose --env-file ../../versions.env --env-file .env ps
curl -fsS http://127.0.0.1:8081/health/ready
docker compose --env-file ../../versions.env --env-file .env down          # add --volumes to reset
```

From a clean clone with a warm Docker cache the stack is healthy in under a minute; the first run builds the four
images (about 3–6 minutes, mostly `dotnet publish` and `ng build`) and pulls about 2 GB of third-party images.

### Published images instead of local builds

By default the application images are built locally from `deploy/docker/*.Dockerfile` and tagged
`opportunity-local/opportunity-<name>:dev`. To run the images CI published to GHCR (`docs/ci.md#container-images`):

```bash
OPPORTUNITY_IMAGE_TAG=sha-1a2b3c4 ./opportunity.sh --images ghcr up     # or make up IMAGES=ghcr
```

The tag must be immutable (`sha-<commit>` or a release `X.Y.Z`); `latest`/`edge` are refused. This adds
[`compose.images.yaml`](compose.images.yaml), which drops the `build:` sections. Verify signatures as described in
`docs/ci.md` before running published images.

### Ports and settings

Every host port is published on `127.0.0.1` only and can be changed in `.env` (`WEB_PORT`, `API_PORT`,
`POSTGRES_PORT`, `OPENSEARCH_PORT`, `RABBITMQ_PORT`, `RABBITMQ_MANAGEMENT_PORT`, `S3_PORT`, and for the observability
profile `GRAFANA_PORT`, `PROMETHEUS_PORT`, `JAEGER_UI_PORT`, `OTLP_GRPC_PORT`, `OTLP_HTTP_PORT`). Do not set
`OPPORTUNITY_BIND=0.0.0.0` on a shared network: OpenSearch has no authentication in this profile.

- Web UI: <http://localhost:8080/> (sign in from here; use `localhost`, not `127.0.0.1`)
- API readiness: <http://127.0.0.1:8081/health/ready> (`/health/live` for liveness)
- RabbitMQ management: <http://127.0.0.1:15672/> (user `opportunity`, password `RABBITMQ_PASSWORD` from `.env`)
- PostgreSQL: `psql -h 127.0.0.1 -U opportunity_owner opportunity` (password `OPPORTUNITY_DB_OWNER_PASSWORD`)
- OpenSearch: `curl http://127.0.0.1:9200/_cluster/health`

The web container serves the UI only; there is no edge proxy routing `/api` in this profile yet.

Local tweaks that should not be committed (extra ports, a build proxy) go in `compose.override.yaml` next to
`compose.yaml`. It is git-ignored and the helper script includes it automatically.

### Import share (natives and extracted text)

Load-file volumes are not uploaded through the browser: the worker reads them from the **import share**, a host folder
mounted read-only at `/var/lib/opportunity/import` (`Import:VolumeShareRoot`). By default that is
`deploy/docker-compose/import-share/` (git-ignored; set `OPPORTUNITY_IMPORT_SHARE` in `.env` to use another folder).
Copy a volume there, e.g. `import-share/matter-a/VOL001/{DATA,NATIVES,TEXT}`, upload its DAT as usual and set the
import profile's `paths.volumeRoot` to `matter-a/VOL001`; `NativeLink`/`TextLink` paths resolve inside that folder.
The folder must be readable by the container user (uid 1654). See
[docs/architecture/import-volumes.md](../../docs/architecture/import-volumes.md) for path rules and outcomes.

## Observability profile

OpenTelemetry is built into every host ([ADR-017](../../docs/adr/0017-observability-and-slos.md)) and is **off unless
`OTEL_EXPORTER_OTLP_ENDPOINT` is set**. Without it the api and worker still write JSON logs with trace IDs and the
correlation scope to stdout (`./opportunity.sh logs api`), but they export nothing. The optional `observability`
profile (decision Q-42) runs a local backend stack:

| Service | Image (`versions.env`) | License | Role | Host port |
|---|---|---|---|---|
| `otel-collector` | `otel/opentelemetry-collector-contrib:$OTEL_COLLECTOR@…` | Apache-2.0 | Receives OTLP from api/worker, drops sensitive attributes again, fans out | 4317 (gRPC), 4318 (HTTP) |
| `prometheus` | `prom/prometheus:$PROMETHEUS@…` | Apache-2.0 | Metrics: OTLP receiver, plus scrapes RabbitMQ (`rabbitmq_prometheus`) and the exporters | 9090 |
| `jaeger` | `jaegertracing/jaeger:$JAEGER@…` | Apache-2.0 | Traces, in memory (lost on restart) | 16686 (UI) |
| `loki` | `grafana/loki:$LOKI@…` | **AGPL-3.0** | Logs, 72 h retention | — |
| `grafana` | `grafana/grafana:$GRAFANA@…` | **AGPL-3.0** | Provisioned Prometheus/Jaeger/Loki datasources and the *opportuniTY overview* dashboard | 3000 |
| `postgres-exporter` | `prometheuscommunity/postgres-exporter:$POSTGRES_EXPORTER@…` | Apache-2.0 | PostgreSQL statistics | — |
| `opensearch-exporter` | `prometheuscommunity/elasticsearch-exporter:$OPENSEARCH_EXPORTER@…` | Apache-2.0 | OpenSearch cluster/JVM metrics | — |

**Licenses (Q-38).** Grafana and Loki are AGPL-3.0. They run only as optional, unmodified, external services in this
profile; no opportuniTY code links them, and the default stack does not need them. Every other component is
Apache-2.0. Logs never go to the product OpenSearch cluster (Q-42).

Turn it on in `.env` (to combine with S3: `COMPOSE_PROFILES=s3,observability`):

```bash
COMPOSE_PROFILES=observability
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
```

Then run `./opportunity.sh up` (the first run of `init` after this change also adds `GRAFANA_ADMIN_PASSWORD` to an
existing `.env`) and open:

- Grafana: <http://127.0.0.1:3000/>. The home dashboard *opportuniTY overview* shows API request rate, p95 latency
  and 5xx ratio by route, worker heartbeats and message processing, RabbitMQ/PostgreSQL/OpenSearch health, .NET runtime
  metrics, recent API traces and logs. Anonymous users get read-only access; sign in as `admin` with
  `GRAFANA_ADMIN_PASSWORD` to explore. Under *Explore*, each Loki log line links to its Jaeger trace.
- Prometheus: <http://127.0.0.1:9090/>. Application metrics have Prometheus names: `opportunity.worker.heartbeat.age`
  becomes `opportunity_worker_heartbeat_age_seconds`, `http.server.request.duration` becomes
  `http_server_request_duration_seconds_bucket`, and `job` is `opportunity/<service.name>`.
- Jaeger: <http://127.0.0.1:16686/> (services `opportunity-api`, `opportunity-worker-all`).

To send telemetry from an API or worker started from the IDE (`dotnet run`), set
`OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317` in its environment while the profile runs.

- **Memory:** limits cap the profile at 1.5 GiB (collector, Prometheus, Jaeger and Loki 256 MiB each, Grafana
  384 MiB, exporters 64 MiB each). About 0.4 GiB is in use when idle.
- **What is (not) recorded:** IDs, counts and durations only. Query strings, headers, cookies, client IPs, SQL text
  and exception messages are removed before export (ADR-017 §4). `Telemetry__RecordSqlStatements=true` adds
  parameterized SQL text for local debugging; never use it with real data.
- **Tuning:** `OTEL_METRIC_EXPORT_INTERVAL` (ms, default 15000 here) and the standard `OTEL_*` variables
  (`OTEL_TRACES_SAMPLER`, …) are passed through. Configuration lives in [`observability/`](observability/). The dashboard
  JSON is generated by `observability/grafana/generate-dashboard.py`; edit the script, not the JSON.
- `./opportunity.sh reset` also deletes the `prometheus-data`, `loki-data` and `grafana-data` volumes.

## Job operations

The job monitor's live updates come from `GET /api/v1/workspaces/{ws}/job-events`, a server-sent-events stream through
the same origin and session cookie as every other API call. `web/api-proxy.conf` gives it its own location with
response buffering and compression off and a one-hour read timeout; put the same settings on any other proxy in front
of the API (the API sends a heartbeat every 15 s and `X-Accel-Buffering: no`, and ends a stream after 30 minutes for
the browser to reconnect; `Jobs__Events__*` settings). The polling fallback is `GET …/jobs?updatedSince=`.

Failed or stuck work is recovered from PostgreSQL, never by re-publishing dead-lettered messages. The worker image has
an operations CLI for that, e.g. `docker compose run --rm --no-deps worker jobs failures --workspace <id> --job <id>`;
see the runbooks in [docs/operations](../../docs/operations/README.md).

## Troubleshooting

**OpenSearch exits with `max virtual memory areas vm.max_map_count [65530] is too low`** (preflight fails on it too).
OpenSearch needs `vm.max_map_count ≥ 262144` on the Docker host:

- Linux: `sudo sysctl -w vm.max_map_count=262144`; persist with
  `echo 'vm.max_map_count=262144' | sudo tee /etc/sysctl.d/99-opensearch.conf`.
- WSL2 (Docker Desktop): `wsl -d docker-desktop sysctl -w vm.max_map_count=262144` (lost on restart); to persist, add
  `kernelCommandLine = "sysctl.vm.max_map_count=262144"` under `[wsl2]` in `%UserProfile%\.wslconfig`, then `wsl --shutdown`.
- macOS (Docker Desktop): the VM usually ships with 262144; otherwise
  `docker run --rm --privileged alpine sysctl -w vm.max_map_count=262144` (lost when Docker Desktop restarts).

**`api`/`worker` stay `unhealthy`.** `curl http://127.0.0.1:8081/health/ready` lists the failing check. An
unhealthy `postgres-schema` means the schema is behind this build: check `./opportunity.sh logs migrator`.

**`migrator` exits non-zero**: `1` migration failed or was refused (an applied script changed: never edit a merged
migration; `reset` a dev database instead), `2` configuration error, `3` a bootstrap step failed.

**Password authentication failed after editing `.env`.** Logins are created only on the first start of an empty
`postgres-data` volume. Run `./opportunity.sh reset`, or change the password with `ALTER ROLE`.

**OpenSearch is killed (exit 137) or the machine swaps.** Give Docker more memory, or lower `OPENSEARCH_HEAP`
(minimum `512m`). The heap is about half of OpenSearch's footprint.

**Port already in use.** Change the port in `.env` (see above).

**Image build fails behind a TLS-intercepting proxy.** The Dockerfiles accept an optional `build-ca` secret. Add it
in `compose.override.yaml`:

```yaml
services:
  api: { build: { secrets: [build-ca] } }   # likewise migrator, worker, web
secrets:
  build-ca: { file: /path/to/corporate-ca.pem }
```

If the builder cannot reach the Alpine package mirrors, set `WEB_APK_UPGRADE=0` in `.env` (skips `apk upgrade` in
the web image).

**`rlimit ... operation not permitted`** (rootless Docker, some VMs): the profile sets no memlock/nofile ulimits for
that reason; remove any you added in an override.
