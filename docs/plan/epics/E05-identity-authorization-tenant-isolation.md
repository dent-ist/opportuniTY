# E05 — Identity, Authorization & Tenant Isolation

**Labels:** `epic`, `role:security`, `role:backend`, `role:data`, `role:qa`, `role:legal`, `role:devops`, `role:ui`, `P0`  
**Starts in:** M1 - First Vertical Slice  
**Tickets:** 10

## Goal
Make `WorkspaceId` a structurally enforced boundary in every store and transport, implement one default-deny policy decision point, and guarantee that search is only candidate discovery while every protected-content operation is re-authorized against PostgreSQL.

## Baseline sections
§2.3, §4, §11, §15, §16, §23, §24, §26, §31.6, §34

## Scope / out of scope
**In scope**
- OIDC with a BFF session
- Permission catalogue, roles and PDP
- PostgreSQL RLS and composite FKs
- Protected-content gateway
- Cross-workspace attack / stale-hit suite (CI gate, ADR-004 security gate)
- Document/field security, security-affecting fields, ethical walls
- Worker trust, secret/key provider, exfiltration controls

**Out of scope**
- Audit storage (E14)
- Render sandbox (E11)

## Contributing roles
- **Roles:** Security & Compliance, Backend, Data (PostgreSQL), QA, Legal / Discovery Counsel, DevOps / SRE, UI/UX
- **Source reviews:** Security & Compliance, Backend/Architecture, QA & Performance, Legal/Discovery Counsel, UI/UX, DevOps/SRE
- **Milestones spanned:** M1 - First Vertical Slice, M2 - 1M Benchmark & Architecture Blockers, M3 - MVP Feature Complete, M5 - Post-MVP / Deferred

## Exit criteria
- [ ] 0 unauthorized protected-resource retrievals in the attack suite and fault-injection runs (§26 gate)
- [ ] Revoking access denies the next retrieval with the index worker paused (§24 rule 5)
- [ ] Every endpoint carries an authorization policy or an explicit anonymous allowlist entry (architecture test)

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E05-T01](#e05-t01) | Implement OIDC authentication with a BFF session | M1 | M | E01-T02, E02-T08 |
| [E05-T02](#e05-t02) | Implement permission catalogue, workspace roles and policy decision point | M1 | L | E05-T01, E04-T02 |
| [E05-T03](#e05-t03) | Enforce PostgreSQL row-level security and composite tenant keys | M1 | M | E04-T02 |
| [E05-T04](#e05-t04) | Build protected-content gateway and authoritative access service | M1 | M | E05-T02, E14-T01, E19-T01 |
| [E05-T05](#e05-t05) | Build cross-workspace attack and stale-hit authorization suite as a CI gate | M2 | L | E05-T03, E05-T04, E07-T05, E03-T02, E10-T01, E12-T01 |
| [E05-T06](#e05-t06) | Implement document/field-level security, security-affecting fields and ethical walls | M3 | L | E05-T02, E10-T01 |
| [E05-T07](#e05-t07) | Harden message and worker trust with envelope verification and scoped identities | M3 | L | E06-T05, E05-T09 |
| [E05-T08](#e05-t08) | Build roles, permissions and user assignment UI | M3 | M | E05-T02, E15-T02, E05-T06 |
| [E05-T09](#e05-t09) | Introduce secret and key-provider abstraction with envelope encryption | M3 | M | E19-T01, E02-T08 |
| [E05-T10](#e05-t10) | Add exfiltration controls: rate limiting, viewer watermarking and export protection | M5 | M | E05-T04, E14-T02, E12-T01 |

---

### E05-T01

**Implement OIDC authentication with a BFF session**  
Labels: `role:security`, `role:backend`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§4 lists OIDC/OAuth2 with no flow. Security finding 13 recommends Authorization Code + PKCE via a backend-for-frontend so Angular holds no tokens; UI proposed silent renewal in the SPA (superseded by BFF).

#### Description
OIDC Authorization Code + PKCE through a BFF in the .NET API; generic OIDC providers (Keycloak for Lite/dev, Entra optional). Server-side sessions with idle/absolute timeouts, back-channel logout, optional per-workspace MFA via `acr`/`amr`, security headers.

#### Acceptance criteria
- [ ] Session cookie is `HttpOnly`, `Secure`, `SameSite`, `__Host-` prefixed; state-changing requests without anti-CSRF token → 403
- [ ] Idle (default 30 min) and absolute (default 12 h) timeouts are enforced server-side and tested
- [ ] Back-channel logout invalidates the server session; a deactivated IdP user loses access within the configured window
- [ ] Per-workspace MFA requirement via `acr`/`amr` → step-up or 403
- [ ] CSP without `unsafe-inline` scripts, HSTS, `frame-ancestors 'none'`, `X-Content-Type-Options` verified by test
- [ ] Developer profile ships a dev IdP configuration

#### Dependencies
- `E01-T02` — Build composable worker host, API conventions and health endpoints
- `E02-T08` — Produce STRIDE threat model and ADR-015 security architecture

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Backend
- **Source reviews:** Security & Compliance, Backend/Architecture, UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

#### Notes
Merges SEC-02, backend 'OIDC authentication and workspace RBAC' (authN part) and UI shell auth.

---

### E05-T02

**Implement permission catalogue, workspace roles and policy decision point**  
Labels: `role:security`, `role:backend`, `P0`, `size:L` · Milestone: M1 - First Vertical Slice

#### Context
§15, §24. Security finding 1: no permission vocabulary, role catalogue, composition or deny-precedence rule; every endpoint would invent its own check.

#### Description
Closed permission enum (e.g. `Document.View`, `Document.DownloadNative`, `Document.Print`, `Coding.Write`, `Coding.WritePrivilege`, `Redaction.Apply/Remove`, `Export.Create`, `Production.Create`, `Workspace.ManageUsers`, `Audit.Read`, `Job.Replay`), default roles (Workspace Admin, Reviewer, QC Reviewer, Privilege Reviewer, Production Manager, Read-only/Auditor), workspace-scoped role assignments. `IAuthorizationService` in `Opportunity.Security` is the single PDP: `Authorize(principal, workspaceId, permission, resource?)` → Allow/Deny + reason; default deny; deny overrides allow; batched `AuthorizeMany`.

#### Acceptance criteria
- [ ] Permission matrix in `docs/security/permission-matrix.md` is generated/validated from code (test fails on divergence)
- [ ] Architecture test: every endpoint has an authorization policy or is on an explicit `[AllowAnonymous]` allowlist
- [ ] Workspace membership is verified on every request; non-member access to a workspace → 404 (no enumeration); an OpenAPI-driven test enumerates all routes
- [ ] Every Deny emits an `AuthZ.Denied` audit event
- [ ] `AuthorizeMany` for 100 IDs meets ≤ 20 ms p95 at 1M docs (benchmark recorded)

#### Dependencies
- `E05-T01` — Implement OIDC authentication with a BFF session
- `E04-T02` — Create workspace, document and page core schema

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Backend
- **Source reviews:** Security & Compliance, Backend/Architecture, Legal/Discovery Counsel
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

---

### E05-T03

**Enforce PostgreSQL row-level security and composite tenant keys**  
Labels: `role:security`, `role:data`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§2.3, §6. Backend finding 13 and security finding 3: workspace isolation in PG is application-only; EF Core global filters are bypassed by Dapper, raw SQL and `COPY`.

#### Description
RLS on all workspace-owned tables using `current_setting('app.workspace_id')` set via `SET LOCAL` per unit of work from a scoped `IWorkspaceContext`; Dapper/COPY go through a connection factory that sets the GUC; app connects as non-owner `NOBYPASSRLS` role; separate migration/maintenance role; EF Core global filters as an additional layer.

#### Acceptance criteria
- [ ] A schema test enumerates every table with a `WorkspaceId` column and fails if it lacks an enabled+forced RLS policy
- [ ] A connection without `app.workspace_id` sees zero tenant rows
- [ ] Cross-workspace reads/writes via EF Core, Dapper and raw SQL return zero rows or fail
- [ ] RLS overhead is measured in `E18-T03`; if > 10% on bulk coding, an ADR documents mitigations rather than disabling RLS

#### Dependencies
- `E04-T02` — Create workspace, document and page core schema

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Data (PostgreSQL)
- **Source reviews:** Security & Compliance, Backend/Architecture
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

#### Notes
Proposed amendment — needs ADR/owner decision: database-enforced isolation (RLS) is not in the baseline.

---

### E05-T04

**Build protected-content gateway and authoritative access service**  
Labels: `role:security`, `role:backend`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§24: every open, view, native download, image retrieval, export and production inclusion re-checks authoritative access; OpenSearch is never the final decision. Security finding 6: presigned URL policy.

#### Description
`IDocumentAccessService` checks workspace role, document/field restrictions and security-affecting coding against PG (batched). A single gateway serves page images, thumbnails, natives, text and PDF renditions: PDP re-check → stream or issue a presigned URL (single object, GET-only, TTL ≤ 120 s default, `Content-Disposition: attachment` + `nosniff` for natives, excluded from logs/traces). Viewer prefetch also goes through the gateway.

#### Acceptance criteria
- [ ] No API route returns object-storage content or URLs without the gateway (architecture test)
- [ ] After a security-affecting coding change commits, the next view/download is denied with the index worker paused
- [ ] Native download and print require permissions distinct from view
- [ ] Every retrieval is audited with document ID, rendition type and outcome
- [ ] Log-scrubbing test proves presigned URLs never appear in logs/traces

#### Dependencies
- `E05-T02` — Implement permission catalogue, workspace roles and policy decision point
- `E14-T01` — Build append-only partitioned audit store and writer
- `E19-T01` — Implement object storage abstraction and provider contract suite

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Backend
- **Source reviews:** Security & Compliance, Backend/Architecture, Legal/Discovery Counsel, UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

#### Notes
Merges backend 'Authoritative protected-resource authorization service' and SEC-07.

---

### E05-T05

**Build cross-workspace attack and stale-hit authorization suite as a CI gate**  
Labels: `role:security`, `role:qa`, `P0`, `size:L` · Milestone: M2 - 1M Benchmark & Architecture Blockers

#### Context
§23 'automated tests must attempt cross-workspace access'; §26 gate '0 unauthorized protected-resource retrievals'; §31.6.

#### Description
Integration suite (`tests/IntegrationTests/Security`) provisioning ≥ 3 workspaces in shared-index (routing) and dedicated-index placements, attempting cross-workspace access across REST (IDOR via ID substitution on every route with an ID parameter), search, counts, aggregations, saved searches, snapshots, PIT/cursors, object keys/presigned URLs, job IDs, export downloads, RabbitMQ message injection and audit queries, plus a randomized property variant. Stale-projection scenario: privilege coding applied with the index worker stopped / refresh disabled; view/download/image/export all denied.

#### Acceptance criteria
- [ ] Routes with ID parameters are auto-discovered; any 2xx with a foreign-workspace ID fails the build
- [ ] 0 cross-workspace hits, counts or aggregation buckets across ≥ 1,000 generated queries
- [ ] Protected retrieval returns 403 in the same request after privilege coding commit with refresh paused
- [ ] Runs on every PR in < 10 min; results attach to the ADR-004 benchmark report
- [ ] Coverage report lists every §24 protected operation with ≥ 1 negative test

#### Dependencies
- `E05-T03` — Enforce PostgreSQL row-level security and composite tenant keys
- `E05-T04` — Build protected-content gateway and authoritative access service
- `E07-T05` — Build logical search service with mandatory workspace filter and cursor binding
- `E03-T02` — Build shared Testcontainers fixture library with Toxiproxy
- `E10-T01` — Implement interactive coding API with optimistic concurrency
- `E12-T01` — Export a materialized snapshot to a load-file volume

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** QA
- **Source reviews:** Security & Compliance, QA & Performance, Backend/Architecture
- **Milestone / phase / size:** M2 - 1M Benchmark & Architecture Blockers / P0 / L

#### Notes
Merges SEC-09 and QA T1.3.

---

### E05-T06

**Implement document/field-level security, security-affecting fields and ethical walls**  
Labels: `role:security`, `role:legal`, `role:backend`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§15, §24. Security finding 2; legal finding 12: walls must apply to every access path (search hits, viewer, exports, productions, bulk coding, admin impersonation, saved searches, reports).

#### Description
ABAC overlays on roles: security-affecting fields (`IsSecurityAffecting`) mapped to `securityTags` (§23); field-level restrictions (hidden / read-only per role); ethical walls as user/group × workspace/document-set/custodian explicit deny overriding any grant, including Workspace Admin (separately audited break-glass role); configurable family propagation of restrictions (none / parent→children / whole family). Security-affecting changes go to the priority index lane with an exported lag SLO metric.

#### Acceptance criteria
- [ ] A security-affecting change commits PG state first and the next View/Download for an affected user is denied with the index worker stopped
- [ ] Wall deny overrides any role grant; break-glass access is audited separately
- [ ] Restricted fields are omitted from API responses, grid columns, exports and search field lists (test per surface)
- [ ] Automated tests attempt access via each path as a walled user; all denied and logged
- [ ] Family propagation policy is configurable per workspace and tested
- [ ] Security-projection lag metric is exported against the agreed SLO (Q-10)

#### Dependencies
- `E05-T02` — Implement permission catalogue, workspace roles and policy decision point
- `E10-T01` — Implement interactive coding API with optimistic concurrency

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Legal / Discovery Counsel, Backend
- **Source reviews:** Security & Compliance, Legal/Discovery Counsel, Backend/Architecture, UI/UX
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
Q-11, Q-13, Q-14.

---

### E05-T07

**Harden message and worker trust with envelope verification and scoped identities**  
Labels: `role:security`, `role:backend`, `role:devops`, `P1`, `size:L` · Milestone: M3 - MVP Feature Complete

#### Context
§11, §21, §24. Security findings 7–8: any process with publish rights could make an export worker write WS-A docs into WS-B's export; workers use a shared superuser connection; access revoked between submission and execution.

#### Description
Per-worker-type RabbitMQ users with least-privilege vhost/exchange/queue permissions (publish only from dispatcher; consume own queue), TLS for AMQP, optional HMAC envelope signature (key from `E05-T09`), access-controlled DLQs. Distinct DB role, storage credential and broker user per worker type. Export/production workers re-authorize the initiating user per chunk against current PG state and audit the delta. (Resolution of WorkspaceId from the PG row is already part of `E06-T05`.)

#### Acceptance criteria
- [ ] A forged-WorkspaceId message is dead-lettered with an `Integrity.MessageRejected` audit event and no data written
- [ ] With signing enabled, invalid/missing HMAC messages are rejected
- [ ] Render worker credentials cannot publish to export exchanges nor read other queues (integration test against broker permissions)
- [ ] An export for a user whose access was revoked after submission excludes or fails denied docs per policy (Q-15) and records the delta
- [ ] No shared superuser connection strings exist in Compose profiles

#### Dependencies
- `E06-T05` — Build idempotent consumer framework
- `E05-T09` — Introduce secret and key-provider abstraction with envelope encryption

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** Backend, DevOps / SRE
- **Source reviews:** Security & Compliance, Backend/Architecture
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / L

#### Notes
SEC-08; Q-15.

---

### E05-T08

**Build roles, permissions and user assignment UI**  
Labels: `role:ui`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§15 workspace RBAC; UI ticket 'Roles, permissions and user assignment'.

#### Description
Role/permission matrix, user and group assignment, field-level restrictions and ethical-wall management screens.

#### Acceptance criteria
- [ ] Permission matrix is an accessible table with row/column headers, keyboard-toggleable
- [ ] UI states that changes apply immediately to protected access ('search lists may lag briefly')
- [ ] Removing one's own last admin role requires an explicit warning
- [ ] Wall creation/modification requires the designated role and is audited

#### Dependencies
- `E05-T02` — Implement permission catalogue, workspace roles and policy decision point
- `E15-T02` — Build application shell, session handling and workspace context
- `E05-T06` — Implement document/field-level security, security-affecting fields and ethical walls

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX, Security & Compliance
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

---

### E05-T09

**Introduce secret and key-provider abstraction with envelope encryption**  
Labels: `role:security`, `role:devops`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
§15, §34: 'capable of per-workspace keys'; matter deletion 'and keys' implies crypto-shredding, which needs a KeyId per object from day one.

#### Description
`ISecretProvider` and `IKeyProvider` (Docker secrets/`*_FILE` for Lite; Vault, Azure Key Vault, AWS KMS for Full). Object storage writes use envelope encryption or provider SSE with the `KeyId` (already recorded per object by `E19-T01`). Key rotation runbook and rewrap job.

#### Acceptance criteria
- [ ] No secrets in committed Compose files, appsettings, images or logs; a log-scrubbing test redacts connection strings/tokens from OTel exports
- [ ] Switching a workspace to a dedicated key affects new objects; a rewrap job exists and is tested
- [ ] Key rotation is tested in integration and documented
- [ ] Workspace deletion includes key destruction when per-workspace keys are enabled, audited

#### Dependencies
- `E19-T01` — Implement object storage abstraction and provider contract suite
- `E02-T08` — Produce STRIDE threat model and ADR-015 security architecture

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** DevOps / SRE
- **Source reviews:** Security & Compliance, DevOps/SRE
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
SEC-14; devops config/secrets finding.

---

### E05-T10

**Add exfiltration controls: rate limiting, viewer watermarking and export protection**  
Labels: `role:security`, `P2`, `size:M` · Milestone: M5 - Post-MVP / Deferred

#### Context
§13, §14, §24. Security finding 12–13: download/print/export are exfiltration paths; no per-user rate limits or reviewer watermarks; productions must never receive reviewer watermarks.

#### Description
Per-user/per-IP rate limits on search, view, download and export creation; anomaly alert on download volume; optional dynamic watermark (user, time, workspace) on viewer images and ad-hoc PDFs, distinct from production endorsements; encrypted export packages (AES-256 ZIP or recipient key) with expiring, user-bound download links.

#### Acceptance criteria
- [ ] Exceeding limits returns 429 with `Retry-After`; limits configurable per role/workspace; events audited
- [ ] Watermark appears on viewer images and downloaded PDFs when enabled and never on productions (test)
- [ ] Export link expires (default 24 h), is bound to the requester and download is audited
- [ ] Alert fires when a user downloads more than N documents/hour

#### Dependencies
- `E05-T04` — Build protected-content gateway and authoritative access service
- `E14-T02` — Complete audit coverage for protected content, search and administration
- `E12-T01` — Export a materialized snapshot to a load-file volume

#### Roles
- **Owner:** Security & Compliance
- **Contributing:** —
- **Source reviews:** Security & Compliance
- **Milestone / phase / size:** M5 - Post-MVP / Deferred / P2 / M

#### Notes
SEC-18; Q-18.
