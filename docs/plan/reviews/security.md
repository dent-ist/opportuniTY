# Security & Compliance Review — opportuniTY Architecture Baseline

Reviewer role: Security Architect / Compliance (OIDC/OAuth2, RBAC/ABAC, multi-tenant isolation, OWASP ASVS L2/L3, SOC 2 / ISO 27001, key management, audit, secure SDLC).
Input: `docs/architecture/architecture-baseline.md` (normative, §35).

## Review findings

1. **Authorization model is named but not specified (§15, §5, §24).** §15 says "workspace RBAC plus optional document/field restrictions, privilege-sensitive fields and ethical walls" and §5 lists `Roles / Permissions` under Workspace, but there is no permission vocabulary, no role catalogue, no statement of how document-level and field-level restrictions compose with roles, and no deny-precedence rule. §24 requires an "authoritative authorization check" on every protected operation, yet nothing defines *what* is checked. Without a single policy decision point (PDP) in `Opportunity.Security` (§18) and a closed permission enum, every endpoint will invent its own check. Recommend: closed permission set (e.g. `Document.View`, `Document.DownloadNative`, `Coding.Write`, `Coding.WritePrivilege`, `Redaction.Apply`, `Redaction.Remove`, `Export.Create`, `Production.Create`, `Workspace.ManageUsers`, `Audit.Read`, ...), workspace-scoped role assignments, ABAC overlays for `securityTags` (§23), ethical walls as explicit deny that overrides any grant, and a default-deny fallback.

2. **Ethical walls and security-affecting coding need a defined data model (§24 "Security-affecting coding").** §24 correctly orders "PostgreSQL first, enforce immediately, search later", but does not say which fields are security-affecting, how they map to `securityTags`, or whether a wall applies to users, groups, or both, and whether it applies to families (a privileged parent with non-privileged children). Field definitions (§6) need an `IsSecurityAffecting` flag so that the coding write path can route those changes to the priority index lane and to the security audit stream. Family-propagation rules for privilege/walls must be explicit or reviewers will leak attachments of withheld emails.

3. **Cross-workspace isolation in PostgreSQL is application-only (§2.3, §6).** `WorkspaceId` is declared a first-class boundary, but nothing requires database-enforced isolation. With hash-by-workspace partitioning (§6) and shared tables, a single missing `WHERE WorkspaceId = @ws` leaks a matter. Recommend PostgreSQL Row-Level Security on all workspace-owned tables, keyed on a per-transaction `SET LOCAL app.workspace_id`, with the application connecting as a non-owner, `NOBYPASSRLS` role; a separate migration/maintenance role. Benchmark RLS overhead inside the ADR-004 / §27 spikes so it is not ripped out later for performance. Composite FKs `(WorkspaceId, DocumentId)` prevent cross-workspace references (e.g. a snapshot in WS-A pointing to a document in WS-B).

4. **OpenSearch isolation relies on one code path (§8, §23, §34).** "A single search-service path that always injects the authenticated workspace filter" is the right pattern, but must be enforced structurally: the planner (§9) must make `workspaceId` filter + routing non-removable (filter context, not query context, so it cannot be OR'ed away by user-supplied AST), the raw OpenSearch client must not be resolvable from API/feature code (architecture test), and aggregations, term-hit reporting, highlighting, PIT and `search_after` continuations must all carry the filter. PIT IDs and scroll cursors must be bound server-side to (user, workspace) — a PIT id is a bearer token to a result set. OpenSearch DLS (§23 "defense in depth") should be evaluated for dedicated-index sensitive matters. Search responses must not return `text` or sensitive metadata beyond what the grid needs, since §24 only re-checks on open/view/download.

5. **Search results themselves can leak (§24).** §24 says a stale hit "may temporarily remain visible" — hit *metadata* (file name, custodian, snippets/highlights, term-hit counts, aggregations) is itself protected content for privileged documents. Recommend: snippets/highlights and grid fields for any hit are post-filtered against the authoritative security state (cheap batched PG check on the page of hits), and counts/facets are labelled as approximate while a security-affecting change is in flight. Define a maximum security-projection lag SLO (§24 item 3 says "stronger SLA" with no number).

6. **Object storage scoping and presigned URLs undefined (§3, §13, §16, §19 ADR-11).** The API talks directly to object storage and the viewer needs page images/natives. Requirements: storage keys prefixed by workspace (`ws/{WorkspaceId}/...`) with non-guessable object IDs; content served either by streaming through the API after the §24 check or via presigned GET URLs issued *only after* the check, scoped to a single object, short TTL (≤60–300 s), `GET` only, `Content-Disposition: attachment` for natives, and never logged. Worker credentials must be scoped per worker type (render worker cannot read export output, etc.). Lite profile (§16) local storage must not be served by a static file server. Matter deletion (§15) must cover storage prefixes plus crypto-shred where per-workspace keys exist.

7. **RabbitMQ envelope trust (§11, §21).** The envelope carries `WorkspaceId`, but workers must not trust it as authority. Any process with publish rights could inject a message that makes an export worker write WS-A documents into WS-B's export. Recommend: workers resolve `WorkspaceId` from the PostgreSQL `Job`/`IndexChunkTask` row by `JobId`/`TaskId` and reject on mismatch; payload-free tasks (§21) help — extend that principle to export/production/render. Per-worker-type RabbitMQ users with vhost and exchange/queue permissions (publish only from dispatcher; consume only own queue), TLS on AMQP, and optional HMAC on envelopes (key from secret store) for tamper evidence. Dead-letter queues contain workspace identifiers and must be access-controlled. Initiating actor (`CreatedBy`) must be preserved for authorization re-check in the worker, since a user's access may be revoked between job submission and execution.

8. **Worker identity and re-authorization in async paths (§2.2, §11, §24).** Export and production are "protected-content operations" (§24) but run asynchronously. The baseline does not state whether workers re-check the *initiating user's* access per document at execution time or rely on the snapshot. Recommend: each export/production chunk re-evaluates the authoritative policy for the job owner (and ethical walls) against current PG state, excludes or fails on denied documents per a configured policy, and audits the delta. Workers authenticate to API/PG/storage/OpenSearch with distinct workload identities (OIDC client credentials or mTLS), not a shared superuser connection string.

9. **Secrets and key management are a single sentence (§15, §34).** "TLS and infrastructure encryption at rest" plus "capable of per-workspace keys" needs an abstraction now: `IKeyProvider`/`ISecretProvider` with Docker-secrets/env for Lite and Vault / Azure Key Vault / AWS KMS for Full; envelope encryption for object storage with key-ID recorded per object so per-workspace KEKs can be added without re-architecture; rotation procedure; no secrets in Compose files committed to the repo, images, or OpenTelemetry attributes (§4). Matter deletion "and keys" (§15) implies crypto-shredding — only possible if per-object key IDs are recorded from day one.

10. **Audit taxonomy and tamper evidence are deferred without a contract (§15, §19 ADR-13, §33).** §33 defers tamper evidence, which is acceptable only if the event schema reserves the fields now. Needed: a closed event taxonomy (authN, authZ deny, document view/download/print, search executed (query text hashed or stored per policy), coding change incl. privilege, redaction add/remove, export/production create/download, role/permission/wall change, admin config, key/secret ops, job lifecycle), with `WorkspaceId`, actor, on-behalf-of, client IP, user agent, correlation/causation IDs, outcome. Per-workspace hash chain (`PrevHash`, `EventHash`, sequence) and periodic signed checkpoints to WORM object storage. Distinguish *audit* from *coding provenance* (§27 `CodingEvent`) but cross-link. Bulk operations must audit at job/chunk granularity with the materialized snapshot ID rather than 10M rows, without losing "who could have seen what". Application role must have INSERT-only on audit partitions.

11. **Untrusted imported content: malware and parser exploits (§12, §13).** DAT/OPT/natives arrive from opposing parties and are hostile by assumption. No malware scanning, file-type verification, archive-bomb limits, or DAT/OPT parser hardening (CSV injection, path traversal in OPT image paths like `..\..\`, giant lines, encoding attacks) is specified. Render workers (§13) parsing Office/PDF/images are the highest-risk component (historical RCE in LibreOffice, Ghostscript, ImageMagick, PDFium). Require: render workers in a sandbox (non-root, read-only rootfs, seccomp/AppArmor, no network egress, no credentials other than a scoped object handle, CPU/memory/time limits, one-document-per-process or ephemeral container), ClamAV-or-pluggable scanner on ingest with quarantine state, and SHA-256 verification against the load file. Viewer must serve derived renditions (images/sanitized PDF) with strict CSP and never execute active content; natives always download as attachments.

12. **Download/export controls and watermarking (§13, §14, §24).** Native download, print, and export are the exfiltration paths; baseline has no per-role download permission, no watermark (user/time/workspace) on viewer images or ad-hoc PDF downloads, and no export package encryption/expiry. Productions are legally controlled (Bates/endorsements) and must not receive reviewer watermarks — so watermarking must be a rendition-time option distinct from production branding. Export packages: encrypted (password or recipient key), checksummed manifests (§14 already wants manifests/checksums), expiring download links, audited.

13. **Session management, rate limiting and API hardening are absent (§4, §17).** OIDC/OAuth2 is listed with no flow. Recommend: Authorization Code + PKCE via BFF pattern (Angular holds no tokens; HttpOnly, Secure, SameSite cookies; CSRF protection), idle (e.g. 30 min) and absolute (e.g. 12 h) session timeouts, back-channel logout, MFA delegated to IdP with `acr`/`amr` enforcement configurable per workspace, workspace selection validated on every request (never trust a client-supplied workspace header without membership check). Rate limiting per user/IP on search, view, download, export creation (also an exfiltration detector), plus query-complexity limits on the query language (§9: wildcard/proximity DoS on OpenSearch). Security headers (CSP, HSTS, frame-ancestors) and ASVS L2 as the declared target.

14. **No threat model or secure SDLC for an OSS project (§18, §19).** The ADR list (§19) has no security/threat-model ADR, and the repo layout has no security tests. For an open-source eDiscovery platform that will be self-hosted by law firms, needed: STRIDE threat model per trust boundary in §3, SECURITY.md + coordinated disclosure, dependency/SCA scanning, SAST, secret scanning, CycloneDX SBOMs, signed container images (cosign/Sigstore) and SLSA provenance, pinned base images, and a release checklist. SOC 2 / ISO 27001 adopters will need control mappings and evidence (audit export, access reviews).

## Proposed epics and tickets

### EPIC: Identity, Session and Authorization Model
Establish OIDC-based authentication with a hardened browser session and a single, default-deny policy decision point that implements workspace RBAC, document/field-level security and ethical walls. Every protected operation in the vertical slice must call this PDP. Baseline: §2.3, §4, §5, §15, §23, §24.

#### SEC-01 Threat model and security ADR for the vertical slice
- **Role:** Security
- **Description:** Produce a STRIDE threat model covering the §3 trust boundaries (browser↔API, API↔PG/OpenSearch/storage, dispatcher↔RabbitMQ↔workers, render worker↔untrusted content) and record security decisions as ADR-015 "Security architecture & trust boundaries". Declare OWASP ASVS L2 as baseline target (L3 controls for authorization and audit).
- **Acceptance criteria:**
  - `docs/adr/` contains the threat model with a data-flow diagram and per-boundary threat table (threat, mitigation, owning ticket).
  - Every High threat maps to a ticket ID in this backlog or an explicit accepted-risk entry signed off by the product owner.
  - ASVS target level and scope documented; model reviewed whenever a new worker type is added (checklist in PR template).
- **Dependencies:** none
- **Phase:** P0
- **Size:** M

#### SEC-02 OIDC authentication with BFF session
- **Role:** Security
- **Description:** Implement OIDC Authorization Code + PKCE through a backend-for-frontend in the .NET API; Angular never handles access/refresh tokens. Support generic OIDC providers (Keycloak for Lite/dev, Entra optional per §4).
- **Acceptance criteria:**
  - Session cookie is `HttpOnly`, `Secure`, `SameSite=Lax|Strict`, `__Host-` prefixed; anti-CSRF token required on all state-changing requests (integration test sends request without token → 403).
  - Configurable idle (default 30 min) and absolute (default 12 h) timeouts enforced server-side; tests prove expiry.
  - Back-channel/front-channel logout invalidates the server session; deactivated IdP user loses access within the configured window.
  - Optional per-workspace requirement for MFA via `acr`/`amr` claim; request without required claim → step-up or 403.
  - Security headers (CSP without `unsafe-inline` scripts, HSTS, `frame-ancestors 'none'`, `X-Content-Type-Options`) verified by automated test.
- **Dependencies:** SEC-01
- **Phase:** P0
- **Size:** M

#### SEC-03 Permission catalogue, workspace roles and policy decision point
- **Role:** Security
- **Description:** Define a closed permission enum and default workspace roles (Workspace Admin, Reviewer, QC Reviewer, Privilege Reviewer, Production Manager, Read-only/Auditor). Implement `IAuthorizationService` in `Opportunity.Security` as the single PDP: `Authorize(principal, workspaceId, permission, resource?)` returning Allow/Deny with reason code. Default deny; deny overrides allow.
- **Acceptance criteria:**
  - Permission matrix (role × permission) checked into `docs/security/permission-matrix.md` and generated/validated from code (test fails if they diverge).
  - Architecture test: every API endpoint carries an authorization attribute/policy, or an explicit `[AllowAnonymous]` allowlist; build fails otherwise.
  - Workspace membership is verified on every request; a client-supplied workspace ID for a workspace the user is not a member of → 404 (not 403, to avoid enumeration).
  - Every Deny emits an `AuthZ.Denied` audit event (SEC-11).
  - Batched authorization API (`AuthorizeMany` for N document IDs) meets ≤20 ms p95 for 100 IDs at 1M docs (benchmark recorded).
- **Dependencies:** SEC-02
- **Phase:** P0
- **Size:** L

#### SEC-04 Document/field-level security and ethical walls
- **Role:** Security
- **Description:** Implement ABAC overlays on top of roles: security-affecting field definitions (`IsSecurityAffecting` flag, §6), document `securityTags` (§23), field-level restrictions (hide/read-only field per role), and ethical walls (user/group × workspace/document-set explicit deny). Define family propagation rule for privilege/walls.
- **Acceptance criteria:**
  - Changing a security-affecting field commits PG security state first and the next `View`/`Download` call for an affected user is denied with no dependence on OpenSearch (test with index worker stopped).
  - Ethical-wall deny overrides any role grant, including Workspace Admin (configurable break-glass role audited separately).
  - Restricted fields are omitted from API responses, grid columns, exports and search field lists for unauthorized users (test per surface).
  - Family propagation policy (none / parent→children / whole family) is configurable per workspace and covered by tests.
  - Security-affecting changes are queued on a priority index lane; lag SLO metric exported (target agreed in open question Q3).
- **Dependencies:** SEC-03
- **Phase:** P1
- **Size:** L

### EPIC: Tenant Isolation and Authoritative Re-check
Make `WorkspaceId` a structurally enforced boundary in every store and transport, and guarantee that search is only candidate discovery while every content retrieval is re-authorized against PostgreSQL. Includes an automated cross-workspace attack suite that gates CI and the ADR-004 security gate. Baseline: §2.3, §8, §11, §15, §21, §23, §24, §26, §31.6.

#### SEC-05 PostgreSQL row-level security for workspace-owned tables
- **Role:** Security
- **Description:** Enable RLS on all workspace-scoped tables using `current_setting('app.workspace_id')` set via `SET LOCAL` per unit of work. Application connects as non-owner `NOBYPASSRLS` role; migrations use a separate role. Use composite keys/FKs `(WorkspaceId, Id)` to forbid cross-workspace references.
- **Acceptance criteria:**
  - Schema test enumerates all tables with a `WorkspaceId` column and fails if any lacks an enabled+forced RLS policy.
  - Query executed without `app.workspace_id` set returns zero rows (not an error that is swallowed).
  - Attempt to insert a snapshot/job/coding row referencing a document of another workspace fails on FK.
  - RLS overhead measured in the §27 coding spike and recorded; if >10% on bulk coding, an ADR documents mitigations rather than disabling RLS.
- **Dependencies:** SEC-03
- **Phase:** P0
- **Size:** M

#### SEC-06 Workspace-enforcing search service and query planner guard
- **Role:** Security
- **Description:** Ensure the logical search service (§8) is the only OpenSearch access path for request-serving code and that the planner (§9) always injects `workspaceId` as a non-removable filter-context clause plus routing. Bind PIT IDs and `search_after` cursors to (user, workspace) server-side. Post-filter highlights/snippets against authoritative security state.
- **Acceptance criteria:**
  - Architecture test: the OpenSearch client type is only referenced from `Opportunity.Search` infrastructure.
  - Property/fuzz test: 10K randomly generated ASTs (incl. OR, NOT, nested groups, field `workspaceId:` injection) all produce DSL whose top-level `bool.filter` contains the authenticated workspace term.
  - A PIT/cursor created by user A in WS-1 cannot be used by user B or in WS-2 (→ 404).
  - Highlights for documents the user cannot view are suppressed in the response.
  - Query-complexity limits (max clauses, wildcard expansion, proximity span) enforced with 400 responses.
- **Dependencies:** SEC-03
- **Phase:** P0
- **Size:** M

#### SEC-07 Protected-content gateway (view, image, native, download)
- **Role:** Security
- **Description:** Single gateway for all protected-resource retrieval (§24): page images, thumbnails, natives, text, PDF renditions. Re-checks PDP against PG, then streams or issues a presigned URL. Object keys are workspace-prefixed with random object IDs; presigned URLs are single-object, GET-only, short-TTL.
- **Acceptance criteria:**
  - No API route returns object-storage content or URLs without passing through the gateway (architecture test).
  - Presigned URL TTL configurable, default ≤120 s; `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff` for natives; URLs excluded from logs/traces (log-scrubbing test).
  - Revoking access (role removal or privilege coding) causes the next retrieval to be denied with the index worker paused (§24 rule 5).
  - Native download and print require distinct permissions from view.
  - Every retrieval audited with document ID, rendition type, and outcome.
- **Dependencies:** SEC-03, SEC-11
- **Phase:** P0
- **Size:** M

#### SEC-08 Message and worker trust: envelope verification and scoped identities
- **Role:** Security
- **Description:** Workers treat envelope `WorkspaceId` as a hint only: resolve workspace and initiating actor from the PG `Job` / `IndexChunkTask` row and reject mismatches. Per-worker-type RabbitMQ users with least-privilege vhost/queue permissions, TLS for AMQP; HMAC signature on envelope (key from SEC-14) . Export/production workers re-authorize the initiating user per chunk at execution time.
- **Acceptance criteria:**
  - Injected message with forged `WorkspaceId` (valid JobId of WS-1, envelope says WS-2) is dead-lettered and an `Integrity.MessageRejected` audit event is written; no data written.
  - Message with invalid/missing HMAC rejected when signing is enabled.
  - Render worker credentials cannot publish to export exchanges nor read other workers' queues (integration test against RabbitMQ permissions).
  - Export job for a user whose access was revoked after submission excludes/fails the denied documents per policy and records the delta.
  - Each worker type uses distinct DB role, storage credential and broker user; no shared superuser connection strings in Compose profiles.
- **Dependencies:** SEC-05, SEC-14
- **Phase:** P1
- **Size:** L

#### SEC-09 Automated cross-workspace attack test suite (CI gate)
- **Role:** Security
- **Description:** Build an integration test suite (in `tests/IntegrationTests/Security`) that provisions two or more workspaces (shared index + dedicated index) and systematically attempts cross-workspace access across all surfaces: REST endpoints (ID substitution / IDOR), search, saved searches, snapshots, PIT cursors, object keys/presigned URLs, job IDs, export downloads, RabbitMQ message injection, audit queries. Also feeds the ADR-004 gate "0 unauthorized protected-resource retrievals" (§26).
- **Acceptance criteria:**
  - Suite auto-discovers all routes with an ID parameter and attempts substitution with a foreign-workspace ID; any 2xx fails the build.
  - Covers both shared-index and dedicated-index placements (§8) and both PG partition strategies under benchmark.
  - Includes stale-projection scenario: privilege coding applied with index worker stopped; attempts to view/download/export the document all denied.
  - Runs on every PR in the developer profile (§29) in <10 min; results attached to ADR-004 benchmark report.
  - Coverage report lists every protected operation from §24 with at least one negative test.
- **Dependencies:** SEC-05, SEC-06, SEC-07
- **Phase:** P0
- **Size:** L

### EPIC: Tamper-evident Audit and Compliance Evidence
Define a stable audit event contract and append-only store now, with hash-chaining and immutable archival layered on without schema change, so §33's deferral of tamper evidence does not become a migration. Provide the evidence SOC 2 / ISO 27001 adopters need. Baseline: §5, §15, §19 (ADR-13), §27, §33.

#### SEC-10 ADR-013 audit architecture and event taxonomy
- **Role:** Security
- **Description:** Write ADR-013 defining the closed audit event taxonomy and envelope: EventId, WorkspaceId (nullable for system), Sequence, OccurredAt, Actor, OnBehalfOf, ClientIp, UserAgent, SessionId (hashed), Category, Action, ResourceType/Id, Outcome, ReasonCode, CorrelationId, CausationId, JobId, SnapshotId, Details (bounded JSON), PrevHash, EventHash. Clarify relationship with `CodingEvent` provenance (§27) and bulk-granularity rules.
- **Acceptance criteria:**
  - Taxonomy covers at least: AuthN, AuthZ.Denied, Document.View/Download/Print, Search.Executed, Coding.Changed (privilege flagged), Redaction.Added/Removed, Export/Production.Created/Downloaded, Role/Permission/EthicalWall.Changed, Workspace.Created/Deleted, Key/Secret ops, Job lifecycle, Integrity violations.
  - Policy for search query text (stored, hashed, or redacted) decided.
  - Bulk ops audited per job + chunk with SnapshotId, and the snapshot is retained as long as the audit record.
  - Hash fields reserved even if chaining ships in P1.
- **Dependencies:** SEC-01
- **Phase:** P0
- **Size:** S

#### SEC-11 Append-only partitioned audit store and writer
- **Role:** Security
- **Description:** Implement the audit store as partitioned append-only PostgreSQL tables (§15). Application role has INSERT/SELECT only; UPDATE/DELETE revoked; partition drop only via retention job under a separate role. Audit writes for security-relevant actions are in the same transaction as the action (or via outbox) so they cannot be lost.
- **Acceptance criteria:**
  - Attempt to UPDATE/DELETE audit rows as app role fails (test).
  - Coding/privilege change and its audit event commit atomically; fault-injection test shows no action without audit.
  - Audit query API restricted to `Audit.Read` permission and scoped by workspace (covered by SEC-09).
  - Throughput benchmark: audit write overhead recorded at 100 concurrent reviewers (§29 workload).
- **Dependencies:** SEC-10, SEC-05
- **Phase:** P0
- **Size:** M

#### SEC-12 Hash-chaining, signed checkpoints and WORM archival
- **Role:** Security
- **Description:** Per-workspace hash chain (`EventHash = H(PrevHash || canonical(event))`) computed by a single-writer sequencer per workspace or periodic batch sealer; signed Merkle checkpoints every N minutes exported to object storage with object lock/immutability (§15 "archival to immutable/object storage"). Provide `opportunity audit verify` CLI.
- **Acceptance criteria:**
  - Verification tool detects modification, deletion, and reordering of any event (tests tamper with DB directly).
  - Checkpoints signed with a key from the key provider (SEC-14); signature verified by CLI.
  - Archival to S3 Object Lock / Azure immutable blob configured in Full profile; Lite profile documents the reduced guarantee.
  - Chain sealing does not add >5 ms p95 to interactive coding.
- **Dependencies:** SEC-11, SEC-14
- **Phase:** P1
- **Size:** M

#### SEC-13 Compliance evidence: access reviews and audit export
- **Role:** Security
- **Description:** Provide workspace access-review report (users, roles, walls, last access), audit export (CSV/JSON with chain verification result) and a SOC 2 / ISO 27001 Annex A control-mapping doc for self-hosters.
- **Acceptance criteria:**
  - Workspace admin can generate an access review export; generation itself is audited.
  - Audit export includes checkpoint hashes and verifies offline with the CLI.
  - `docs/security/control-mapping.md` maps platform controls to SOC 2 CC6/CC7 and ISO 27001:2022 Annex A 5.15–5.18, 8.15, 8.16, distinguishing platform vs operator responsibility.
- **Dependencies:** SEC-11, SEC-12
- **Phase:** P2
- **Size:** S

### EPIC: Secrets, Supply Chain and Untrusted Content
Protect the platform and its operators from hostile documents and compromised dependencies: sandboxed rendering, malware scanning, hardened import parsing, managed secrets and keys, and a verifiable open-source release pipeline. Baseline: §4, §12, §13, §14, §15, §16, §18, §34.

#### SEC-14 Secret and key-provider abstraction with envelope encryption
- **Role:** Security
- **Description:** Introduce `ISecretProvider` and `IKeyProvider` (Docker secrets/file for Lite; Vault, Azure Key Vault, AWS KMS for Full). Object storage writes use envelope encryption or provider SSE with a recorded `KeyId` per object so per-workspace keys and crypto-shredding (§15, §34) can be enabled later without data migration.
- **Acceptance criteria:**
  - No secrets in committed Compose files, appsettings, images, or logs; secret-scanning in CI (SEC-17) passes and a log-scrubbing test checks connection strings/tokens are redacted from OpenTelemetry exports.
  - Every stored object has a `KeyId` in metadata/DB; switching a workspace to a dedicated key affects new objects and a rewrap job exists.
  - Documented key-rotation runbook; rotation tested in integration.
  - Matter deletion (§15) includes key destruction step when per-workspace keys are enabled, audited.
- **Dependencies:** SEC-01
- **Phase:** P1
- **Size:** M

#### SEC-15 Ingest hardening: malware scanning, type verification, parser limits
- **Role:** Security
- **Description:** Add a pluggable scanner stage (ClamAV default; ICAP for enterprise) to import; verify file type by magic bytes vs extension; verify MD5/SHA-256 against DAT values; harden DAT/OPT parsers against path traversal (OPT image paths), oversized lines/fields, encoding attacks, and archive/decompression bombs. Infected or suspicious items go to quarantine state with restricted access.
- **Acceptance criteria:**
  - EICAR test file is quarantined, not rendered, and requires `Document.ViewQuarantined` to download; audit event written.
  - OPT/DAT paths containing `..`, absolute paths, UNC paths, or symlinks outside the import root are rejected (fuzz tests).
  - Hash mismatch vs load file flags the document and is reported in the import report.
  - Configurable limits (max file size, max line length, max fields, zip ratio) enforced with tests.
  - Formula-leading cells (`=`, `+`, `-`, `@`) are neutralized in CSV/DAT exports and load files produced by the platform.
- **Dependencies:** SEC-08
- **Phase:** P1
- **Size:** M

#### SEC-16 Render-worker sandbox and safe viewer
- **Role:** Security
- **Description:** Run render workers as least-privileged, isolated processes: non-root, read-only rootfs, dropped capabilities, seccomp/AppArmor profile, no network egress except a scoped object-storage endpoint (or input/output via mounted temp dir by a broker process), CPU/memory/wall-clock limits, one document per process with teardown. Viewer serves only derived renditions with strict CSP; never executes document scripts, macros, or external resource loads.
- **Acceptance criteria:**
  - Container security test: worker cannot reach the internet, PG, OpenSearch or RabbitMQ management endpoints from the render process (network policy test).
  - Malicious corpus (PDF JS, Office macros, XXE, SVG with script, image decompression bomb, polyglot files) renders or fails safely within limits; no outbound connections observed.
  - Renderer timeout/OOM kills only the single job; chunk marked failed with retry ceiling.
  - Viewer CSP blocks inline script; HTML natives rendered to image/PDF, never served as `text/html` from the app origin.
  - Renderer selection ADR (§13) includes a security/CVE-history criterion.
- **Dependencies:** SEC-01, SEC-07
- **Phase:** P1
- **Size:** L

#### SEC-17 Secure SDLC pipeline: SCA, SAST, secret scanning, SBOM, signed images
- **Role:** Security
- **Description:** CI pipeline for the open-source repo: dependency scanning (NuGet/npm, e.g. Dependabot + OSV-Scanner), SAST (CodeQL for C#/TypeScript), secret scanning with push protection, container scanning (Trivy), CycloneDX SBOM per image and release, cosign keyless signing and SLSA provenance for published images, pinned base images by digest, SECURITY.md with coordinated-disclosure policy.
- **Acceptance criteria:**
  - PRs fail on new Critical/High vulnerabilities without a documented exception (expiry date required).
  - Each released image has an attached SBOM and cosign signature verifiable with documented `cosign verify` command; Compose profiles reference images by digest.
  - Branch protection: required reviews, signed commits or verified merges for `main`, CODEOWNERS for `Opportunity.Security`, auth, and audit code.
  - SECURITY.md published with contact, supported versions, and response SLA; GitHub private vulnerability reporting enabled.
  - OpenSSF Scorecard run in CI; score tracked.
- **Dependencies:** none
- **Phase:** P0
- **Size:** M

#### SEC-18 Exfiltration controls: rate limiting, watermarking, export protection
- **Role:** Security
- **Description:** Per-user and per-IP rate limiting on search, view, download and export creation; anomaly alerts on unusual download volume. Optional dynamic watermark (user, timestamp, workspace) on viewer images and ad-hoc PDF downloads, separate from production endorsements (§14). Export packages encrypted (AES-256 ZIP or recipient public key), with expiring single-use download links and checksummed manifests.
- **Acceptance criteria:**
  - Exceeding configured limits returns 429 with `Retry-After`; limits configurable per role/workspace; events audited.
  - Watermark appears on viewer page images and downloaded PDFs when enabled; never on productions (test).
  - Export download link expires (default 24 h), is bound to the requesting user, and download is audited; manifest checksums verified in test.
  - Alert fires (OpenTelemetry metric + audit) when a user downloads more than N documents per hour (configurable).
- **Dependencies:** SEC-07, SEC-11
- **Phase:** P2
- **Size:** M

## Open questions for the product owner

1. **Ethical-wall semantics:** Are walls applied to users only or also IdP groups; do they hide documents entirely (including from counts/facets) or only block content; and can a Workspace Admin be walled off (or is a separately audited break-glass role required)?
2. **Privilege family propagation:** When a parent email is coded Privileged/Withheld, should attachments automatically inherit the restriction for reviewers, for exports, or neither by default?
3. **Security-projection lag SLO:** What maximum window is acceptable for a stale search hit (metadata/snippet only) after privilege or wall changes — e.g. ≤5 s p95 — and must counts/facets during that window be exact?
4. **Search audit policy:** Must executed search query text be retained in audit (often required by clients) or hashed/redacted for privacy, and what is the default audit retention period vs matter-deletion policy (§15)?
5. **Tamper evidence timing:** Is tamper-evident audit (hash chain + WORM) required for the first public release given target customers (law firms, corporate legal, government), or acceptable as P1 per §33?
6. **Async export authorization:** If a user's access is revoked between export submission and execution, should the export fail entirely, silently exclude denied documents (with a report), or continue under the original snapshot authorization?
7. **Watermarking and download policy:** Should native download and print be off by default for Reviewer roles, and is a dynamic reviewer watermark a v1 requirement?
8. **Deployment trust model for Lite profile:** Is the Lite profile (§16) intended for real client data (requiring TLS everywhere, RLS, sandboxed renderer, malware scanning) or explicitly evaluation-only with documented reduced guarantees?
