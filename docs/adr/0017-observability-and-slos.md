# ADR-017: Observability and SLOs

| Field | Value |
|---|---|
| **Status** | Proposed (§1–§5 and §7 binding as the interim position; the §6 SLO/alert table is accepted with `E19-T05`) |
| **Date** | 2026-10-03 |
| **Owner (role)** | DevOps / SRE |
| **Deciders** | Lead architect; contributing: Backend, Security & Compliance (telemetry trust boundary TB13); product owner (Q-38, Q-42) |
| **Tracking issue** | #160 (plan key `E19-T04`), #161 (`E19-T05`) |
| **Baseline sections** | [§4](../architecture/architecture-baseline.md#4-technology-stack), [§17](../architecture/architecture-baseline.md#17-non-functional-engineering-targets), [§28](../architecture/architecture-baseline.md#28-search-generation-and-reviewer-ux), §31.7 |
| **Related** | ADR-001 (watermark, lanes, Q-10 metric), ADR-010 (jobs, chunks, DLQ alerts), ADR-013 §7 (never logged), ADR-015 D10.5/TB13, ADR-019 §2.10/§3.1 (headers, envelope), ADR-020; Q-10, Q-38, Q-42, Q-44 |

## Context

Baseline §4 names OpenTelemetry for traces, metrics and logs. §17 sets the targets (simple search p95 < 1 s, complex
< 3 s, single coding → searchable p95 < 1 s, 99.9 % API availability) and requires p50/p95/p99. §28 and §31.7 make the
search generation and watermark observable before the coding/search path counts as stable. ADR-001 defines the
security lane SLO (Q-10: security-projection lag ≤ 5 s p95, metric `security_projection_lag_seconds`) and the index-lag
gauge; ADR-010 §7.6 lists the job alerts (DLQ depth > 0, any `Failed` row, chunk `Running` > 15 min, oldest `Pending`
age, watermark stuck). ADR-015 TB13 treats telemetry as a trust boundary: T-60 is document content leaking into
telemetry, and D10.5 restricts telemetry to IDs, counts and durations behind an attribute allow-list with a scrubbing
test. ADR-013 §7 lists what audit, logs and traces never contain; audit is the evidentiary record and is not
telemetry. Q-42 asks for a shipped OpenTelemetry Collector configuration plus an optional `observability` Compose
profile with the Grafana stack, and forbids using the product OpenSearch cluster for logs. Q-38 allows AGPL software
only as an optional, unmodified external service.

Without one decision, every epic would pick its own metric names, label sets and log fields. The SLO dashboards and
alerts of `E19-T05` would then have nothing stable to target, and one careless attribute could put search text or a
file name into a third-party backend.

## Decision

### 1. Instrumentation lives in the shared host builder

1. **R1 — Off unless configured.** Every host (API and all workers) calls `AddOpportunityHostDefaults`, which calls
   `AddOpportunityTelemetry` (`src/Opportunity.Hosting/Telemetry`). The OpenTelemetry SDK is registered only when
   `OTEL_EXPORTER_OTLP_ENDPOINT` is set (`Telemetry:Enabled=false` forces it off; `true` registers it without an
   exporter, for tests). Unset, no tracer, meter or logger provider exists, so `ActivitySource`/`Meter` calls are
   no-ops. Export is OTLP only; the standard `OTEL_*` variables (protocol, headers, sampler, export interval) apply.
2. **R2 — Signals and sources.** Traces: ASP.NET Core server spans (health probes excluded), `HttpClient`, Npgsql
   (`Npgsql` source), and the application source `Opportunity`. Metrics: ASP.NET Core and Kestrel, `HttpClient`,
   `System.Runtime` (.NET runtime metrics), `Npgsql`, and the meters `Opportunity` and `Opportunity.Workers`. Logs:
   `ILogger` through the OpenTelemetry logger provider, with scopes and formatted messages.
3. **R3 — Resource.** `service.name` is derived from the host assembly (`Opportunity.Worker.Indexing` →
   `opportunity-worker-indexing`; `OTEL_SERVICE_NAME` overrides it), `service.namespace=opportunity`,
   `service.version` = the informational version (includes the commit), `service.instance.id` = host name + process
   ID, and `deployment.environment.name`. Workers in the combined Lite process share one `service.name`
   (`opportunity-worker-all`) and are told apart by the `worker.type` attribute.

### 2. Context propagation

1. **R4 — W3C Trace Context only.** HTTP accepts and forwards `traceparent`/`tracestate` (ADR-019 §2.10). Messages
   carry them in the envelope `headers` map (ADR-019 §3.1, keys in `Opportunity.Contracts.Messaging.MessageHeaderNames`).
   `Opportunity.Application.Telemetry.MessageTracePropagation` injects and extracts them without any broker
   dependency: `StartPublish` opens a producer span `send {destination}` and injects its context;
   `StartProcess` opens a consumer span `process {destination}` whose **parent** is the producer context, so one trace
   spans API → PostgreSQL → outbox dispatcher → RabbitMQ → worker → OpenSearch once those components exist (the
   `E19-T04` acceptance criterion completes with `E06-T04`/`E07`). W3C `baggage` is neither extracted nor injected
   (`TraceContextOnlyPropagator`): caller-supplied baggage would otherwise ride on every span and leave the process.
2. **Correlation.** `CorrelationId` is the API's `X-Correlation-Id` (or the trace ID when the client sent none) and is
   copied unchanged into every message the request causes. `CausationId` is the `messageId` of the message being
   handled when a new one is published (`MessageCorrelation.CausedBy`). Both are span attributes
   (`opportunity.correlation_id`, `opportunity.causation_id`) and log-scope keys.
3. Workers wrap each message in `WorkerTelemetry.BeginProcessing(...)`: consumer span, logging scope, the
   `messaging.process.duration`/`messaging.client.consumed.messages` metrics and the worker's last-consumed time.

### 3. Logs

1. **R5 — Structured and correlated.** Containers log JSON to stdout (`Logging__Console__FormatterName=json`, scopes
   on). Every record inside a request or message carries `TraceId`, `SpanId`, `CorrelationId` and, where known,
   `WorkspaceId`, `JobId`, `CausationId`, `MessageId` (`LogScopeKeys`). The API adds `WorkspaceId` from the route,
   and only when it parses as a GUID. OTLP log records carry the same fields plus `trace_id`, which Loki keeps as
   structured metadata and Grafana links to the trace.
2. Use `LoggerMessage` source-generated methods with named placeholders. Message templates name IDs and counts,
   never values that come from documents or users.
3. The `Microsoft.AspNetCore.Hosting.Diagnostics` category is capped at `Warning` in every host: its request
   start/finish records contain the raw query string, which carries search text.

### 4. What telemetry must never contain

1. **R6 — Attribute allow-list.** `TelemetryScrubbingProcessor` runs on every finished span before any exporter and
   removes every attribute that `TelemetryAttributePolicy` does not allow: HTTP method/route/status/path/scheme,
   server address/port, protocol, `error.type`; PostgreSQL system/namespace/operation/collection; messaging
   system/operation/destination/message id; and `opportunity.*`/`worker.type`. Dropped by construction:
   `url.query` (search text), every `http.request.header.*`/`http.response.header.*` (authorization, cookies),
   `client.address`, `user_agent.original`, `network.peer.*`, `db.npgsql.data_source` (a connection string) and SQL
   text (`db.query.text`, `db.statement`). `url.full` loses its query, fragment and user info. Error status
   descriptions are cleared, because exception messages can quote data values; `error.type` stays.
2. **SQL text is off.** `Telemetry:RecordSqlStatements=true` keeps the parameterized statement text for local
   debugging only. Parameter values are never recorded (Npgsql does not emit them). The setting MUST NOT be enabled
   in Full.
3. Exceptions are not recorded as span events (`RecordException=false` for ASP.NET Core and `HttpClient`).
4. ADR-013 §7 applies to all telemetry: no document content, metadata values, control numbers, file names,
   free-text notes, secrets, tokens, cookies, presigned URLs, connection strings, full bodies or search text. Search
   text is recorded only in audit `RestrictedDetails` (Q-16). Audit is not telemetry: audit events MUST NOT be
   derived from or shipped through the telemetry pipeline, and telemetry is never evidence.
5. **Defence in depth.** The shipped collector configuration deletes the same attributes again
   (`attributes/scrub`), so a misconfigured client does not leak through the collector.
6. **Verification.** Integration tests with in-memory exporters send a request with search text in the query, a
   bearer token, a cookie and baggage, and assert that no span, span attribute or log record contains any of them;
   a PostgreSQL test asserts that Npgsql spans have no statement text, data source or parameter values.

### 5. Metric catalog

1. **R7 — Names and units.** OpenTelemetry semantic-convention names where one exists (`http.server.request.duration`,
   `db.client.operation.duration`, `messaging.process.duration`); otherwise `opportunity.<area>.<name>`, lowercase,
   dot-separated, no unit in the name. Units are UCUM (`s`, `By`, `1`) or annotations (`{message}`). Prometheus sees
   `opportunity_search_index_lag_seconds`, `opportunity_jobs_total`, and so on. Durations and lags are histograms in
   seconds with explicit buckets that include the target thresholds (1 s, 5 s, 120 s).
2. **Catalog first.** Every application instrument is defined in `OpportunityMetricCatalog`
   (`src/Opportunity.Application/Telemetry`) with its unit, attribute keys and the ticket that records it, and is
   obtained only through `OpportunityMetrics`, so names cannot drift. A unit test fails if a catalog metric is not in
   the table below.
3. **Bounded cardinality.** Metric attributes are enums, worker types, lanes, statuses, queue names and job types.
   `opportunity.workspace_id` appears only on the per-workspace generation and lag gauges, mapped through
   `BoundedAttributeValues`: the first `Telemetry:MaxWorkspaceAttributeValues` (default 20) workspaces seen by a
   process keep their ID and the rest report as `other`. Document, job, message or correlation IDs are never metric
   attributes; they belong on spans and logs. `E19-T05` may replace first-seen with a true top-N by activity.

| Metric | Kind | Unit | Attributes | Recorded by |
|---|---|---|---|---|
| `opportunity.worker.heartbeat.age` | gauge | s | `worker.type` | `E01-T02` (today) |
| `opportunity.worker.last_consumed.age` | gauge | s | `worker.type` | `E01-T02` (today) |
| `messaging.process.duration` | histogram | s | destination, `opportunity.message.type`, `worker.type`, `error.type` | `E19-T04` `WorkerTelemetry` |
| `messaging.client.consumed.messages` | counter | {message} | same as above | `E19-T04` `WorkerTelemetry` |
| `opportunity.outbox.pending` | gauge | {record} | `opportunity.lane` | `E06-T04` dispatcher |
| `opportunity.outbox.oldest_age` | gauge | s | `opportunity.lane` | `E06-T04` dispatcher |
| `opportunity.outbox.publish_latency` | histogram | s | `opportunity.lane` | `E06-T04` dispatcher (commit → broker confirm) |
| `opportunity.dispatcher.published` | counter | {message} | destination, `opportunity.outcome` (confirmed, unconfirmed) | `E06-T04` dispatcher |
| `opportunity.queue.depth` | gauge | {message} | destination, `opportunity.queue.state` (ready, unacked, dlq) | `E06-T01` |
| `opportunity.queue.consumers` | gauge | {consumer} | destination | `E06-T01` |
| `opportunity.dlq.messages` | counter | {message} | destination, `error.type` | `E06-T06` dead-letter recorder |
| `opportunity.search.generation.committed` | gauge | {generation} | `opportunity.workspace_id` (bounded) | `E07-T08` |
| `opportunity.search.generation.indexed` | gauge | {generation} | `opportunity.workspace_id` (bounded) | `E07-T08` (visible watermark) |
| `opportunity.search.index_lag` | gauge | s | `opportunity.workspace_id` (bounded) | `E07-T08` (ADR-001 §7.4 `search.index_lag_seconds`) |
| `opportunity.search.commit_to_searchable` | histogram | s | `opportunity.lane` | `E07-T06` |
| `opportunity.search.security_projection_lag` | histogram | s | `opportunity.lane` | `E05-T06` (ADR-001 §5.4 `security_projection_lag_seconds`) |
| `opportunity.search.stale_version_rejections` | counter | {document} | `opportunity.lane` | `E07-T06` |
| `opportunity.index.chunk_tasks` | gauge | {task} | `opportunity.status`, `opportunity.lane` | `E07-T06` |
| `opportunity.index.chunk_task.oldest_age` | gauge | s | `opportunity.status`, `opportunity.lane` | `E07-T06` |
| `opportunity.search.request.duration` | histogram | s | `opportunity.search.class` (simple, complex), `opportunity.outcome` | `E07-T07` |
| `opportunity.search.post_filter.dropped` | counter | {document} | `opportunity.search.drop_reason` (PDP reason, `integrity`) | `E07-T05` (Q-12 page post-filter) |
| `opportunity.jobs` | counter | {job} | `opportunity.job.type`, `opportunity.status` | `E06-T05` |
| `opportunity.jobs.active` | up-down counter | {job} | `opportunity.job.type` | `E06-T05` |
| `opportunity.job.chunks` | counter | {chunk} | `opportunity.job.type`, `opportunity.outcome`, `error.type` | `E06-T05` |
| `opportunity.job.chunk.duration` | histogram | s | `opportunity.job.type`, `opportunity.outcome` | `E06-T05` |
| `opportunity.audit.write.duration` | histogram | s | `opportunity.outcome` | `E14-T01` |

Infrastructure metrics come from their own exporters, not the application: RabbitMQ (`rabbitmq_prometheus` plugin),
PostgreSQL (`postgres_exporter`), OpenSearch (`elasticsearch_exporter`, which also reads OpenSearch). The PostgreSQL
replication and WAL-archive lag (RPO ≤ 5 min) come from `postgres_exporter` once `E19-T08` configures archiving.

### 6. SLIs, SLOs and first alerts (proposed; accepted with `E19-T05`)

The targets below are §17 targets, not product guarantees, until benchmarked (Q-44: absolute latency gates on
developer hardware are comparative). The SLIs use the catalog metrics.

| SLI | Metric | Objective | Alert (initial, `E19-T05` tunes) |
|---|---|---|---|
| Security-projection lag (Q-10) | `opportunity.search.security_projection_lag` | ≤ 5 s p95 | p95 > 5 s for 5 min |
| Interactive commit → searchable | `opportunity.search.commit_to_searchable{lane=interactive}` | < 1 s p95 (§17) | p95 > 1 s for 10 min |
| Bulk index lag | `opportunity.search.index_lag` | ≤ 2 min (§26) | > 2 min for 10 min |
| Watermark progress (§28) | `opportunity.search.generation.indexed` vs `.committed` | indexed advances while committed > indexed | no advance for 5 min while behind |
| Outbox age | `opportunity.outbox.oldest_age` | ≤ 60 s | > 60 s |
| Dead letters | `opportunity.queue.depth{opportunity.queue.state=dlq}` | 0 | > 0 |
| Stuck chunks (ADR-010 §7.6) | `opportunity.index.chunk_task.oldest_age{status=Running}` | < 15 min | ≥ 15 min |
| Search latency (§17) | `opportunity.search.request.duration` | simple < 1 s, complex < 3 s p95 | p95 above target for 15 min |
| API availability (§17) | `http.server.request.duration` (5xx ratio) | 99.9 % over 30 days | burn rate 14.4× over 1 h |
| Worker liveness | `opportunity.worker.heartbeat.age` | < 2 × heartbeat interval | > 2 min |
| WAL archive lag (RPO) | `postgres_exporter` | ≤ 5 min | > 4 min |

### 7. Collector, backends and the `observability` profile (Q-42)

1. **R8 — Collector in the middle.** Applications export OTLP to an OpenTelemetry Collector, never directly to a
   backend. The repository ships `deploy/docker-compose/observability/otel-collector.yaml` (receivers, memory limiter,
   scrub processor, batching); Full deployments point its exporters at the operator's own backends.
2. **R9 — Never the product cluster.** Logs and traces MUST NOT be written to the product OpenSearch cluster: no
   resource contention with search, and telemetry stays out of the data tier. Exporters only *read* OpenSearch
   metrics.
3. **Optional profile.** `COMPOSE_PROFILES=observability` adds the stack below. Images are pinned `tag@digest` in
   `versions.env`, Grafana datasources and a starter dashboard are provisioned from the repository, and memory limits
   cap the profile at 1.5 GiB (about 0.4 GiB in use when idle). Nothing phones home: Grafana analytics, update checks
   and Loki usage reporting are off.

| Component | Role | License | Status under Q-38 |
|---|---|---|---|
| OpenTelemetry Collector (contrib) | OTLP in, fan-out, scrubbing | Apache-2.0 | default |
| Prometheus | metrics (OTLP receiver and scrapes) | Apache-2.0 | default |
| Jaeger v2 (in-memory) | traces | Apache-2.0 | default |
| postgres_exporter, elasticsearch_exporter | infrastructure metrics | Apache-2.0 | default |
| Grafana | dashboards and exploration | AGPL-3.0 | optional, unmodified external service |
| Loki | logs | AGPL-3.0 | optional, unmodified external service |

Jaeger stays on 2.20 because 2.21 removed the legacy query API that Grafana 13's Jaeger datasource uses. Nothing in
opportuniTY links Grafana or Loki code, and both are optional. An operator can replace them with any OTLP-capable
backend by changing the collector exporters.

## Consequences

- **Positive:** one switch (`OTEL_EXPORTER_OTLP_ENDPOINT`) instruments every host the same way. Traces cross the
  broker without a RabbitMQ dependency in Application. Later epics emit metrics that are already named, typed and
  documented, which unblocks `E19-T05` dashboards and alerts. Leaks are prevented by an allow-list plus tests, not by
  reviewer vigilance.
- **Negative / costs:** the allow-list drops attributes that new instrumentation adds until the policy is extended
  (on purpose; extending it is a reviewed change). Dropping error descriptions makes spans less self-explanatory, so
  logs carry the error class and the operator correlates by trace ID. Bounded workspace labels hide workspaces
  beyond the first N in metrics; spans and logs still carry them. Jaeger's in-memory store and the 72 h Loki retention
  are developer conveniences, not an operations design.
- **Follow-up work:** `E06-T01`/`E06-T04` (#56) wrap publish/consume in `MessageTracePropagation`/`WorkerTelemetry`
  and record the outbox and queue metrics; `E07-T06`/`E07-T08` record the index and watermark metrics; `E05-T06`
  the security lane; `E19-T05` (#161) dashboards and alerts as code, induced-failure alert tests and the acceptance
  of §6; `E19-T06` Full-profile collector, TLS on OTLP and a dedicated `pg_monitor` login for `postgres_exporter`.
- **Verification:** `tests/Opportunity.IntegrationTests/Telemetry` (API trace attributes and propagation, no
  sensitive data in spans or logs, Npgsql spans without SQL text, worker consumer spans continuing the producer trace,
  worker metrics, telemetry off by default); `tests/Opportunity.UnitTests/Telemetry` (propagation round trip, catalog
  naming/units and this table, cardinality bound); architecture tests keep the propagation helper free of transport
  SDKs.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Logs in the product OpenSearch cluster | Forbidden by Q-42: competes with search for heap and I/O and mixes telemetry with protected data. |
| Grafana Tempo for traces | AGPL-3.0; Jaeger (Apache-2.0) gives the same local experience under the default license policy. |
| VictoriaLogs or a separate OpenSearch for logs | VictoriaLogs needs a Grafana plugin download at start-up. A second OpenSearch alone would exceed the 1.5 GiB budget. Loki is optional and unmodified (Q-38). |
| Prometheus exporter in each app (`/metrics`) | One more port per host and pull-only. OTLP through the collector keeps one pipeline for all three signals and works where the apps cannot be scraped. |
| OpenTelemetry auto-instrumentation agent | Adds a native profiler to the chiseled images and cannot apply our allow-list before export. |
| Deny-list scrubbing | Fails open: a new library attribute leaks until someone notices. The allow-list fails closed. |
| Telemetry on by default (export to localhost) | Lite users without a collector would get exporter errors, and a self-hosted product should not emit data unless the operator chooses to. |

## Baseline amendments

None. This ADR elaborates §4, §17 and §28. The ADR-001 metric names `search.index_lag_seconds` and
`security_projection_lag_seconds` are realized as `opportunity.search.index_lag` and
`opportunity.search.security_projection_lag` (Prometheus: `opportunity_search_index_lag_seconds`,
`opportunity_search_security_projection_lag_seconds`). This is a naming alignment, not a change of meaning.

## Links

- Baseline: §4, §11, §17, §28, §31.7
- ADRs: [ADR-001](0001-search-consistency-outbox-and-version-model.md) §5.4, §7; [ADR-010](0010-job-chunk-idempotency-semantics.md) §7;
  [ADR-013](0013-audit-architecture-and-event-taxonomy.md) §7; [ADR-015](0015-security-architecture-and-trust-boundaries.md) TB13, D10.5;
  [ADR-019](0019-layering-and-api-conventions.md) §2.10, §3.1
- Decisions: [decisions.md](../plan/decisions.md) Q-10, Q-16, Q-38, Q-42, Q-44
- Developer profile: [deploy/docker-compose/README.md](../../deploy/docker-compose/README.md#observability-profile)
