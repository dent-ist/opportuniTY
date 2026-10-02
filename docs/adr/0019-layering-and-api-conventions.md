# ADR-019: Solution layering, API conventions and message-contract versioning

| Field | Value |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-02 |
| **Owner (role)** | Backend |
| **Deciders** | Lead architect; contributing: Security & Compliance, DevOps / SRE, UI/UX |
| **Tracking issue** | #27 (`E02-T01`) |
| **Baseline sections** | §2.1, §2.5, §11, §18, §20, §21, §24, §32 |
| **Related** | Review findings A-01, A-02, A-16, A-17; `E01-T01` (#21), `E01-T02` (#22), `E06-T01` (#56); Q-32, Q-35, Q-43 |

**Why 019:** ADR-001…018 are reserved for the §19 list and the four gap ADRs (see [index](README.md#numbering)).
These conventions are cross-cutting rather than a §19 topic, so they take the next free number instead of a sub-letter.

## Context

The baseline asks for "a modular .NET application plus independently scalable workers" (§2.5) and says "keep RabbitMQ
APIs outside domain/application logic" (§11), but §18 only lists folder names: no dependency rules, no Contracts or
Application project, no Dispatcher or BulkCoding host (finding A-17). §4 is silent on API style, and §11 lists envelope
fields without a versioning rule (review finding 2.4). Code is being scaffolded now (`E01-T01`), so the rules must exist
before the first project reference is added. Separately, §20 and §32 describe two different first vertical slices (A-16).

## Decision

### 1. Projects and layers

| Layer | Projects (`src/`) | May reference |
|---|---|---|
| **Domain** | `Opportunity.Core` | nothing (BCL only) |
| **Contracts** | `Opportunity.Contracts` — message envelopes, message payloads, public API DTOs | nothing (BCL only) |
| **Application** | `Opportunity.Application` — use cases, ports (interfaces such as `IDocumentRepository`, `IMessagePublisher`, `ISearchService`, `IObjectStore`), authorization orchestration | Core, Contracts |
| **Feature modules** | `Opportunity.Import`, `Opportunity.Rendering`, `Opportunity.Production` | Core, Contracts, Application |
| **Infrastructure** | `Opportunity.Data` (Npgsql), `Opportunity.Search` (OpenSearch), `Opportunity.Storage` (S3/Azure/filesystem), `Opportunity.Messaging` (RabbitMQ), `Opportunity.Security` (OIDC, key/secret providers), `Opportunity.Jobs` (job/chunk state, leases, dispatch loop) | Core, Contracts, Application (to implement its ports) |
| **Host composition** | `Opportunity.Hosting` — shared host builder, health endpoints, validated options, worker module catalog (`E01-T02`) | anything in `src/` except hosts; referenced only by hosts |
| **Hosts** | `Opportunity.Api`, `Opportunity.Worker.Import`, `.Indexing`, `.Rendering`, `.Export`, `.Production`, `.Dispatcher`, `.BulkCoding`, `.All` (combined, Lite), `Opportunity.Migrator` (one-shot schema migrations + infrastructure bootstrap, `E04-T01`) | anything in `src/` except other hosts |
| **Frontend** | `Opportunity.Web` (Angular) | the published OpenAPI document only (generated client); no .NET references |

Rules:

1. **R1** `Core` and `Contracts` reference no other project and no NuGet package beyond the BCL (and
   `System.Text.Json` attributes for Contracts).
2. **R2** `Application` and feature modules MUST NOT reference infrastructure projects or vendor SDKs: `Npgsql*`,
   `Microsoft.EntityFrameworkCore*`, `Dapper`, `OpenSearch.*`, `RabbitMQ.Client`, `AWSSDK.*`, `Azure.*`,
   `Microsoft.AspNetCore.*`. Format libraries a feature genuinely needs (e.g. a TIFF codec in Rendering) are allowed
   and listed in the architecture test allow-list.
3. **R3** Infrastructure projects MUST NOT reference each other. Cross-store work (e.g. "commit coding + insert
   `IndexChunkTask`") is an Application use case over ports; the transaction boundary is an `IUnitOfWork` port
   implemented in `Data`. An exception requires an amendment to this ADR.
4. **R4** Domain types (`Core`) never appear in `Contracts`; contracts map to/from domain in Application or the host.
   Contracts are the only types that cross a process boundary (HTTP or broker).
5. **R5** Hosts are thin composition roots: DI wiring, configuration (`IOptions` + `ValidateOnStart`), health
   endpoints, consumer/endpoint registration. No business logic. All worker hosts share one generic-host builder so
   Lite can run every consumer in one process (`E01-T02`).
6. **R6** Only `Opportunity.Search` may reference the OpenSearch client, and every query it issues passes through the
   single search-service path that injects the authenticated workspace filter (§23). Only `Opportunity.Data` opens
   PostgreSQL connections, always with the workspace session context set (`E05-T03`).

```text
Hosts (Api, Worker.*) ──► Infrastructure (Data, Search, Storage, Messaging, Security, Jobs)
   │                          │
   ├──► Feature modules ──────┤
   │      (Import, Rendering, Production)
   ▼                          ▼
Application ──────────────► Core
   └──────────────────────► Contracts
```

**Enforcement:** `tests/Opportunity.ArchitectureTests` uses NetArchTest (`NetArchTest.Rules`) to assert R1–R4 and R6
(forbidden-dependency rules per layer, vendor-namespace rules, no host-to-host references). The tests run in the PR
pipeline (`E01-T03`) and fail the build. A new project must be added to the layer table above and to the test's layer
map in the same PR.

### 2. HTTP API conventions

1. **Style & description:** REST over HTTPS; OpenAPI 3.1 generated at build and diffed in CI — a breaking diff fails
   the build unless the major version changes (`E01-T02`).
2. **Versioning:** URL major version, `/api/v1/...`. Within v1 only additive changes (new endpoints, new optional
   request fields, new response fields). Clients MUST ignore unknown response fields and unknown enum values.
3. **Routes:** workspace-scoped resources live under `/api/v1/workspaces/{workspaceId}/...`
   (e.g. `/documents/{documentId}`, `/searches`, `/jobs/{jobId}`). Only installation-level resources
   (`/api/v1/workspaces`, `/api/v1/me`, `/api/v1/admin/...`) sit outside. Path segments are plural kebab-case nouns;
   actions that are not CRUD are sub-resources (`POST .../bulk-coding-jobs`), not verbs in query strings. The route
   `workspaceId` is authorized on every request; a resource in another workspace returns **404**, never 403, so that
   existence is not disclosed.
4. **Errors:** RFC 9457 `application/problem+json` for every 4xx/5xx. `type` is a stable URN
   `urn:opportunity:problem:<code>` (e.g. `...:validation`, `...:version-conflict`, `...:idempotency-key-reuse`);
   extensions `code`, `traceId` and, for validation, `errors` (field → messages). Problem details never contain
   protected content, stack traces or SQL.
5. **Long-running operations** (import, bulk coding, reindex, export, production, render): `202 Accepted`,
   `Location: /api/v1/workspaces/{workspaceId}/jobs/{jobId}`, body = the job resource. Progress via
   `GET .../jobs/{jobId}` and SSE `GET .../jobs/{jobId}/events` with polling fallback (Q-35).
6. **Idempotency:** every job-creating or bulk mutating `POST` REQUIRES an `Idempotency-Key` header (opaque, ≤ 128
   chars). Keys are scoped to (user, workspace, route) and retained ≥ 24 h. A repeat with the same key and same
   request hash returns the original response (same job); the same key with a different body returns 422
   `idempotency-key-reuse`. Missing key → 400.
7. **Concurrency:** versioned resources return `ETag`; mutating requests on them send `If-Match`, and a mismatch returns
   412. The version semantics come from ADR-001.
8. **Pagination:** cursor-based only: `?limit=` (default 50, max 500) and opaque `?cursor=`; response
   `{ "items": [...], "nextCursor": "…" | null, "total": { "value": n, "relation": "eq" | "gte" } }`. Cursors are
   bound to (user, workspace) and rejected otherwise. No offset paging or deep page jumps (Q-32).
9. **Representation:** JSON, camelCase property names, enums as camelCase strings, IDs as opaque strings. Timestamps
   are UTC ISO-8601 with `Z` and millisecond precision (`2026-10-02T14:03:22.123Z`); date-only values are
   `YYYY-MM-DD`. Display time zones are a client concern (Q-28).
10. **Cross-cutting headers:** W3C `traceparent` accepted and propagated; `X-Correlation-Id` echoed. Rate-limit
    responses are 429 with `Retry-After`.

### 3. Message contracts and versioning

1. **Envelope** (in `Opportunity.Contracts`, serialized as camelCase JSON), exactly the §11 fields: `messageId`,
   `messageType`, `schemaVersion`, `workspaceId`, `jobId`, `correlationId`, `causationId`, `idempotencyKey`,
   `createdAt` (UTC ISO-8601), `attempt`, `headers` (incl. W3C trace context), `payload`.
2. **`messageType`** is a stable dotted name without a version (e.g. `indexing.indexChunkTask`). **`schemaVersion`** is
   `"major.minor"` of the payload schema.
3. **Additive-only within a major:** a minor bump may add optional fields or enum values. Removing, renaming,
   re-typing a field, making it required or changing its meaning requires a new major.
4. **Tolerant readers:** consumers ignore unknown fields, treat missing optional fields as defaults and route unknown
   enum values to a safe "unsupported" path (dead-letter with reason), never crash.
5. **N and N-1:** every consumer supports the current and previous major of each message it handles. Rollout order for
   a new major: deploy consumers that read N and N-1, then switch producers to N, and drop N-1 only in a later release
   (aligns with Q-43 N/N-1 support).
6. **Payload-free by default:** payloads carry identifiers and versions, not document projections (§21). Envelope
   `workspaceId`/actor are routing hints only; workers resolve workspace and initiating actor from the PostgreSQL job
   row (ADR-010, `E06-T05`).
7. **Verification:** golden JSON samples per message type and major live with the contract tests; CI checks that the
   current reader accepts N and N-1 samples (`E06-T01`, `E01-T06` compatibility matrix).

### 4. Vertical slice reference

§32 is the normative definition of the first vertical slice; §20 is historical (see amendment B-2).

## Consequences

- **Positive:** vendor SDKs stay replaceable (Kafka evaluation, storage providers, OpenSearch candidates A–D) without
  touching use cases; workspace scoping has exactly one code path per store; API and message evolution is checkable in
  CI rather than by review.
- **Negative / costs:** more projects and mapping code (Contracts ↔ Core); ports for every store; R3 forces some logic
  that would be convenient in infrastructure up into Application. Mandatory `Idempotency-Key` adds client work.
- **Follow-up work:** `E01-T01` (#21) project references + NetArchTest rules; `E01-T02` (#22) API conventions,
  ProblemDetails, idempotency, OpenAPI diff; `E06-T01` (#56) envelope and contract tests; `E01-T06` (#26)
  compatibility matrix.
- **Verification:** architecture tests (R1–R4, R6); OpenAPI breaking-change diff; message contract tests; cross-
  workspace test suite (`E05-T05`) exercises the 404 rule.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Single "Infrastructure" project | Every host would load every SDK; violates the independent-worker goal (§2.5) and makes the RabbitMQ boundary (§11) unenforceable. |
| Microservices with separate repositories/solutions | Contradicts §2.5 "start modular, not microservice-heavy". |
| Header or media-type API versioning | Harder to route, cache and document; URL versioning is explicit in logs and OpenAPI. |
| Offset pagination | Infeasible at 100M documents and conflicts with `search_after`/PIT (§10, Q-32). |
| Schema registry / Protobuf for messages | Extra infrastructure for v1; JSON + golden-sample contract tests give the needed guarantees. May be revisited with Kafka (§11). |
| Allow Application to reference RabbitMQ.Client for convenience | Explicitly forbidden by §11. |

## Baseline amendments

All *Proposed* until lead-architect and product-owner sign-off; the baseline file is not edited by this ADR.

| ID | Amendment | Finding |
|---|---|---|
| B-1 | §19 item 4 becomes "ADR-004a PostgreSQL coding storage"; add "ADR-004b OpenSearch coding projection"; §23, §25, §26, §30, §34, §35 "ADR-004" read as ADR-004b; §27 is ADR-004a. Append ADR-015…018 to §19. | A-01, A-02 |
| B-2 | Mark §32 normative over §20 (add "Superseded by §32" to §20, as §10 already does for §22). | A-16 |
| B-3 | §18 layout gains `Opportunity.Contracts`, `Opportunity.Application`, `Opportunity.Worker.Dispatcher`, `Opportunity.Worker.BulkCoding`, `tests/Opportunity.ArchitectureTests`, `tools/Opportunity.Benchmarks`; layering per §1 of this ADR. | A-17 |

## Links

- Baseline: [§2, §11, §18, §20, §32](../architecture/architecture-baseline.md)
- Review findings: [review-findings.md](../plan/review-findings.md) A-01, A-02, A-16, A-17, §2.4, §3.5
- Decisions: [decisions.md](../plan/decisions.md) Q-28, Q-32, Q-35, Q-43
- Index: [docs/adr/README.md](README.md)
