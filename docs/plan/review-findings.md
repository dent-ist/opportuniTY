# Consolidated Review Findings

Seven role reviews of the architecture baseline produced about 110 findings. This document merges the overlapping ones and groups them by theme. Each finding cites the baseline section(s), names the contributing review(s), and points to the plan ticket(s) that address it.

Legend:
- **⚠ Proposed amendment — needs ADR/owner decision**: resolving this finding would change or add to the normative baseline (§35). The baseline has **not** been edited. The ticket listed under "Resolved via" carries the decision.
- Reviews: UI = UI/UX, BE = Backend/Architecture, LEG = Legal/Discovery Counsel, EDS = eDiscovery Practitioner, SEC = Security & Compliance, QA = QA & Performance, OPS = DevOps/SRE.

## Summary of proposed amendments

| ID | Amendment | Baseline § | Raised by | Resolved via |
|---|---|---|---|---|
| A-01 | Renumber ADRs: §19 item 4 "Coding storage" collides with §25 "ADR-004" (OpenSearch projection). Proposal: ADR-004a PG coding storage, ADR-004b OpenSearch coding projection | §19, §25–§27 | BE | `E02-T01` |
| A-02 | Add missing ADRs to §19: 015 Security/threat model, 016 Backup/DR, 017 Observability/SLOs, 018 Frontend architecture | §19 | SEC, OPS, UI | `E02-T01`, `E02-T08`, `E19-T07`, `E15-T01` |
| A-03 | External versioning works only with full `index`, not `_update`; ADR-004 must test full reindex vs guarded update separately and name the write primitive per candidate | §7, §21 step 5, §25 | BE | `E02-T02`, `E18-T04` |
| A-04 | Define one authoritative `DocumentVersion` (bumped on any projected-field change, written as external version) and a separate `ProjectionGeneration` that is never compared with it; clarify `projectionVersion`/`SearchGeneration` | §5, §7, §10, §21, §23, §28 | BE, QA | `E02-T02` |
| A-05 | Deletes and tombstones: `index.gc_deletes` (60 s) lets a delayed retry resurrect deleted docs. A missing PG row must mean delete, and deletion must fence in-flight tasks | §15, §21 | BE | `E02-T02`, `E02-T06`, `E20-T02` |
| A-06 | Remove `Payload` from SearchOutbox (§7) so interactive rows are payload-free like §21; this makes coalescing trivial | §7, §21 | BE | `E02-T02`, `E06-T03` |
| A-07 | IndexChunkTask for import/reindex: nullable `SnapshotId`, `TaskKind`, chunk membership for every job type | §12, §21 | BE | `E02-T02`, `E06-T03` |
| A-08 | State explicitly that per-document message ordering is not guaranteed and not needed when version-safety holds | §7, §11 | BE | `E02-T02` |
| A-09 | Watermark definition: commit-ordered low watermark that advances only after OpenSearch refresh; interactive and bulk work share one generation sequence | §28, §31.7 | BE, QA | `E02-T02`, `E07-T08` |
| A-10 | Database-enforced tenant isolation via PostgreSQL RLS (`SET LOCAL app.workspace_id`, `NOBYPASSRLS` app role) and composite `(WorkspaceId, Id)` FKs | §2.3, §6 | SEC, BE | `E05-T03` |
| A-11 | Stale-hit metadata, snippets and highlights are protected content: post-filter each page of hits against PG; set a numeric SLO for security-projection lag | §24 | SEC, UI | `E07-T05`, `E05-T06` |
| A-12 | Render-worker sandboxing (non-root, read-only rootfs, seccomp/AppArmor, no egress, resource limits, one document per process) and malware scanning on ingest | §12, §13 | SEC | `E11-T03`, `E08-T09` |
| A-13 | Per-page model (document, ordinal, image key/page Bates, object ref, dimensions/DPI, rotation) added to the §5 domain model | §5, §12, §13 | EDS | `E02-T06`, `E04-T02` |
| A-14 | Production reproducibility covers *how* as well as *which*: frozen spec, redaction-set version, coding-state version, renderer versions | §10, §14, §22 | LEG, EDS | `E12-T02` |
| A-15 | Pull minimal tamper evidence (per-workspace hash chain plus signed checkpoints) forward from §33; keep WORM archival deferred | §15, §33 | LEG, SEC | `E14-T03`, `E14-T07` |
| A-16 | Mark §32 normative over §20 (two different versions of the vertical slice) | §20, §32 | BE | `E02-T01` |
| A-17 | Extend §18 layout: Contracts, Application, Dispatcher host, BulkCoding worker, Benchmarks, ArchitectureTests; align root name with repo | §18 | BE | `E01-T01` |
| A-18 | Add UX acceptance cases (sort/filter/facet on coding columns) and coding-filter queries to the ADR-004 gates and §29 query mix | §25, §26, §29 | UI, QA | `E17-T04`, `E18-T04` |
| A-19 | Add a frontend architecture and UX baseline section (WCAG 2.2 AA, design system, paging model, UI performance targets) | §3, §4 | UI | `E15-T01` |
| A-20 | Pin target versions (.NET, PostgreSQL, OpenSearch, RabbitMQ, Node) for reproducible benchmarks | §4, §29 | OPS, BE | `E01-T01` |
| A-21 | Legal hold / preservation lock blocks deletion; deletion produces a destruction certificate | §15 | LEG | `E20-T01`, `E20-T02` |
| A-22 | Define numerically "simple" vs "complex" query, "sustained bulk load", "material repeatable advantage" and "fails materially at 10M" | §17, §26, §29 | QA | `E17-T04`, `E18-T02` |

---

## 1. Search consistency, versioning and indexing (§7, §21, §23, §25, §28)

1. **⚠ External versioning vs partial updates** (BE). `version_type=external` only applies to full `index` operations. Candidate A therefore re-sends whole documents, up to ~10 MB of text, on every coding change, and any `_update` path needs `if_seq_no`/`if_primary_term` or a painless `projectionVersion` guard. → A-03. *Proposed amendment — needs ADR/owner decision.*
2. **⚠ Version-field relationships** (BE, QA). The baseline does not say what increments DocumentVersion (metadata re-import, text replacement, family re-link), whether `projectionVersion == DocumentVersion`, or how version safety survives a mapping-driven reindex. Under Candidates B/C, each index needs its own counter or a single shared document version. → A-04. *Proposed amendment.*
3. **⚠ Tombstones and deleted documents** (BE). A retried task after `gc_deletes` can resurrect a document deleted in PG. Workspace deletion must fence workers before it drops data. → A-05. *Proposed amendment.*
4. **⚠ Payload vs payload-free** (BE). §7 lists `Payload`, while §21 says tasks carry identifiers only. Payload-free interactive rows read current state, which makes coalescing trivial. → A-06. *Proposed amendment.*
5. **⚠ Import chunks lack a snapshot** (BE). §21 step 1 "resolve the chunk's exact document IDs" is undefined for import, where the chunk creates the documents. Reindex also needs a task kind. → A-07. *Proposed amendment.*
6. **Dispatcher semantics undefined** (BE, OPS). The ≤ 1 s p95 target rules out naive polling. Proposal: SKIP LOCKED + LISTEN/NOTIFY, publisher confirms before marking dispatched, partitioned retention. **⚠ No per-document ordering guarantee** should be stated explicitly (A-08). → `E02-T02`, `E06-T04`.
7. **Coalescing has no defined place** (BE): dispatcher, worker, or implicit through current-state reads. → `E02-T02`, `E18-T04`.
8. **⚠ Watermark computation** (BE, QA, UI). Sequences commit out of order, so the watermark needs a low watermark over in-flight work. It must advance only after OpenSearch refresh, otherwise the lag gate passes falsely. The baseline also does not define behaviour across alias switches or a snapshot's SearchGeneration. → A-09. *Proposed amendment.*
9. **Chunk sizing must bound bytes as well as count** (BE, EDS). OpenSearch `http.max_content_length` defaults to 100 MB, §29 texts reach ~10 MB, and real corpora include 100 MB+ texts. Highlighting truncates at `index.highlight.max_analyzed_offset` (1M chars). → `E02-T02`, `E07-T04`, `E08-T04`, Q-29.
10. **Security-affecting priority has no mechanism** (BE, SEC). The baseline needs a priority queue or lane and a numeric SLO. Also, `securityTags` lives in the content projection while privilege is coding whose placement ADR-004 decides, so security-field placement must be an ADR-004 criterion. → `E06-T01`, `E05-T06`, `E18-T04`, Q-10.

## 2. Jobs, messaging and idempotency (§2.4, §11)

1. **Idempotency key derivation and consumer dedupe unspecified** (BE). Proposal: `hash(WorkspaceId, JobId, ChunkSequence, OperationKind, ProjectionGeneration)`, plus semantic idempotency so a re-applied "add tag" writes no second CodingEvent. → `E02-T02`, `E06-T05`.
2. **No job/chunk state machine** (BE). Missing: statuses, cancel/pause, completed-with-errors, max attempts, lease-based ownership for crash-after-commit-before-ack. → `E06-T02`.
3. **DLQ is not the source of truth** (BE, OPS). Replay should reset PG state, keeping the DLQ for diagnostics only, with a poison policy and alerts. → `E06-T06`.
4. **Message schema evolution** (BE, OPS). Additive-only within a major version, tolerant readers, N/N-1 during rolling deploys, contract tests, and a compatibility matrix per release. → `E06-T01`, `E01-T06`.
5. **Envelope trust** (SEC). A forged `WorkspaceId` could cross-write exports. Workers must resolve the workspace and actor from PG; use per-worker broker users, TLS and optional HMAC. → `E06-T05`, `E05-T07`.
6. **Async re-authorization** (SEC). Export/production workers must re-check the initiating user's access per chunk at execution time. → `E05-T07`, `E12-T01`, Q-15.

## 3. Architecture documentation and repository structure (§18, §19, §20, §32)

1. **⚠ ADR numbering clash §19 vs §25** (BE). → A-01. *Proposed amendment.*
2. **⚠ Missing ADRs** for security/threat model (SEC), backup/DR and observability (OPS), and frontend (UI). → A-02. *Proposed amendment.*
3. **⚠ §18 layout gaps** (BE): no Contracts, Application/Domain split, Dispatcher host, BulkCoding worker or Benchmarks; `opportunity/` vs `opportuniTY`. → A-17. *Proposed amendment.*
4. **⚠ §20 vs §32 duplication** (BE). → A-16. *Proposed amendment.*
5. **API and platform conventions absent** (BE). REST + OpenAPI 3.1, `/api/v1`, ProblemDetails, cursor pagination, 202 + job resource, Idempotency-Key, and .NET LTS pin. → `E01-T02`, `E02-T01`.
6. **⚠ Target versions unpinned** (OPS). → A-20. *Proposed amendment.*
7. **Migrations vs partitioning** (BE, OPS). EF migrations handle partitions, RLS and `CREATE INDEX CONCURRENTLY` poorly. Use SQL-first migrations in a dedicated migrator under an advisory lock, never at API startup. → `E04-T01`.

## 4. Snapshot semantics and review sets (§10, §22)

1. **PIT threshold needs numbers** (BE): estimated runtime vs `keep_alive × safety factor`. → `E02-T03`, `E10-T03`.
2. **PIT is effectively unused in the MVP** (BE). §32 requires restartable bulk, which §22 says must be materialized. → `E02-T03`.
3. **Expansion timing** (BE, EDS). Family/duplicate expansion must happen before materialization for deterministic membership. → `E02-T03`, `E09-T03`.
4. **Review cursor outlives PIT** (UI). Long reviewer sessions need cursor re-establishment or a light review snapshot. → `E10-T03`, `E16-T02`, `E16-T03`, Q-33.
5. **Reproducibility stops at membership** (LEG, BE). → A-14 (see §9 below), Q-08.

## 5. Authorization, tenant isolation and data protection (§2.3, §15, §23, §24)

1. **Authorization model named but not specified** (SEC, BE). Needs a closed permission enum, a role catalogue, a single default-deny PDP and deny precedence. → `E05-T02`.
2. **⚠ PostgreSQL isolation is application-only** (SEC, BE). EF global filters are bypassed by Dapper, raw SQL and COPY. → A-10. *Proposed amendment — needs ADR/owner decision.*
3. **OpenSearch isolation must be structural** (SEC): non-removable filter-context clause, architecture test for client usage, PIT/cursor binding to (user, workspace), and filters on aggregations/highlights/continuations. → `E07-T05`, `E05-T05`.
4. **⚠ Search results themselves leak** (SEC, UI). File names, snippets, highlights and facets of a stale privileged hit are protected content. → A-11. *Proposed amendment.* Q-12.
5. **Ethical walls and security-affecting coding lack a data model** (SEC, LEG). Needs `IsSecurityAffecting`, users vs groups, family propagation, every access path covered, and proof that walls held. → `E05-T06`, `E14-T06`, Q-13, Q-14.
6. **Object storage scoping and presigned URLs** (SEC). Workspace-prefixed, non-guessable keys; presign only after the check; single-object, GET-only, short TTL; `attachment` disposition; never logged; scoped worker credentials. → `E02-T06`, `E05-T04`, `E19-T01`.
7. **Secrets and keys** (SEC, OPS). Needs `ISecretProvider`/`IKeyProvider`, a `KeyId` recorded per object from day one (crypto-shredding), rotation, and no secrets in Compose, images or telemetry. → `E05-T09`, `E19-T01`.
8. **Session and API hardening** (SEC). BFF + PKCE, HttpOnly `__Host-` cookies, CSRF, idle/absolute timeouts, MFA via `acr`/`amr`, security headers, rate limits, query-complexity limits, and ASVS L2 as the target. → `E05-T01`, `E07-T07`, `E05-T10`.
9. **Exfiltration controls** (SEC): download/print permissions, reviewer watermarks distinct from production endorsements, encrypted expiring exports. → `E05-T04`, `E05-T10`, Q-18.
10. **Threat model and secure SDLC missing** (SEC, OPS): STRIDE, SCA/SAST, secret scanning, SBOM, cosign, SLSA, SECURITY.md. → `E02-T08`, `E01-T04`, `E01-T05`.

## 6. Untrusted content (§12, §13)

1. **⚠ Render workers are the highest-risk component** (SEC). LibreOffice, Ghostscript, ImageMagick and PDFium all have RCE histories. → A-12. *Proposed amendment — needs ADR/owner decision.* → `E11-T03`.
2. **Malware, type verification, parser hardening** (SEC): ClamAV/ICAP, magic bytes, archive bombs, OPT path traversal (`..\..\`), giant lines, CSV injection in produced load files. → `E08-T01`, `E08-T04`, `E08-T09`, `E12-T01`.
3. **Safe viewer** (SEC, UI): derived renditions only, strict CSP, natives as attachments, HTML natives never served as `text/html`. → `E11-T03`, `E16-T04`.

## 7. Audit and defensibility (§15, §27, §33)

1. **Audit taxonomy must be fixed now** (SEC, LEG, BE). A closed event taxonomy, reserved hash fields, view/download/print/search/permission/denied events, bulk audited at job/chunk granularity with SnapshotId, and audit cross-linked to CodingEvent provenance. → `E02-T07`, `E14-T01`, `E14-T02`.
2. **⚠ Tamper evidence timing** (LEG, SEC). Hash-chaining is cheap now, and FRCP 26(g)/37(e) declarations need it. → A-15. *Proposed amendment.* Q-17.
3. **Prefetch must not count as "viewed"** (UI). → `E02-T07`, `E16-T03`.
4. **Ethical-wall proof and access reviews** (LEG, SEC). → `E14-T06`.
5. **Defensibility reports** (LEG): hit reports tied to snapshot/generation, review summary, production history and chain of custody. → `E14-T05`.
6. **AI/TAR provenance marker** (LEG). CodingEvent ActorType (Human, BulkHuman, SystemRule, Model) costs little now and is expensive to retrofit. → `E04-T04`.

## 8. Import realism (§5, §6, §12)

1. **DAT parsing contract unspecified** (EDS, BE). Concordance default: column U+0014, qualifier `þ` U+00FE, newline `®` U+00AE, multi `;`, nested `\`. Vendors often use `¶` U+00B6. CSV per RFC 4180. → `E08-T01`.
2. **Encoding detection** (EDS). BOM sniffing; `þ` is 0xFE in 1252 but `C3 BE` in UTF-8; TXT files have their own encoding. → `E08-T01`.
3. **OPT/LFP cross-reference** (EDS): page count only on the break row, multi-page TIFF, path styles; LFP priority. → `E08-T05`, `E12-T09`, Q-26.
4. **⚠ No per-page entity in §5** (EDS). → A-13. *Proposed amendment.*
5. **Overlay vs append** (EDS, BE). Append/Overlay/Append-Overlay, overlay key, blank-overwrite option, multi-value merge vs replace, routing through §21 and §24. → `E08-T07`, Q-31.
6. **Validation reports and error files** (EDS). A pre-flight check and a re-loadable error file in the same delimiter profile and encoding. → `E08-T06`.
7. **ControlNumber identity** (EDS). Normalized uniqueness, case rules, natural sort, received BegBates vs internal ControlNumber, import prefix. → `E02-T04`, `E04-T02`, Q-27.
8. **Family reconstruction** (EDS, BE). Range (BegAttach/EndAttach), pointer (ParentID/AttachmentIDs) and group (GroupIdentifier) modes; cross-chunk/volume families; orphans. → `E09-T01`.
9. **Dedupe scope and email hashing** (EDS, BE). Family-level dedupe, global vs custodial, primary rule, AllCustodians/DuplicateCustodians/AllPaths, upstream `DedupeHash` distinct from file MD5. → `E09-T02`, `E09-T04`, Q-09.
10. **Upstream thread IDs** (EDS): ConversationIndex, thread group, InclusiveEmail. → `E09-T02`.
11. **Dates and time zones** (EDS, UI): multiple formats, separate time columns, source time zone, UTC + raw string, matter display zone, `documentDate`/family-date derivation. → `E02-T04`, `E08-T02`, `E15-T05`, Q-28.
12. **Extracted text size** (EDS, BE): cap, `TextTruncated` flag, full text in storage. → `E08-T04`, Q-29.

## 9. Production defensibility and privilege (§13, §14, §22)

1. **Privilege is a field, not a process** (LEG). Status, basis, description, attorneys, log category; second-level review. → `E13-T01`, `E13-T06`.
2. **Privilege log content undefined** (LEG, EDS): document-by-document, metadata-only and categorical logs; versioned; tied to the snapshot. → `E13-T03`, Q-20.
3. **No clawback workflow** (LEG). FRE 502(b)/(d): where-produced lookup, clawback record, replacement production with the same Bates. → `E13-T04`, `E13-T05`.
4. **Redaction burn asserted, not verified** (LEG). The most common leak is an unredacted .txt; PDFs also carry metadata, annotations and layers. → `E12-T06`.
5. **Natives with redactions** (LEG, EDS) must be blocked unless a documented native-redaction method exists. → `E12-T05`, `E12-T06`, Q-22.
6. **Confidentiality designations** (LEG): stamping, load-file field, AEO access, family consistency, re-designation workflow. → `E12-T04`.
7. **Bates integrity** (LEG, QA, EDS): unique per matter+prefix, gap rules, immutable, never reissued, idempotent under retries. → `E12-T03`.
8. **Family integrity and placeholders** (LEG, EDS): slip sheets consume a Bates number; privilege conflicts across families and duplicates. → `E12-T05`, `E13-T02`.
9. **⚠ Reproducibility beyond membership** (LEG). → A-14. *Proposed amendment.* Q-08.
10. **Output formats unspecified** (EDS): DAT field set, OPT, LFP, TIFF G4/JPG/PDF, slip sheets, naming, folder layout, page-suffix Bates. → `E12-T05`, `E12-T09`, Q-21.
11. **QC gates are only a phrase** (EDS, LEG): blocking and warning checks, reconciliation, audited overrides. → `E12-T07`.
12. **Search term reports** (EDS): per-term hits, with family, unique hits, snapshot-bound. → `E07-T10`, Q-30.

## 10. Matter lifecycle and privacy (§1, §15, §16)

1. **⚠ Deletion vs legal hold** (LEG). Deleting held data is spoliation risk; protective orders often require certified destruction. → A-21. *Proposed amendment.* → `E20-T01`, `E20-T02`, Q-23.
2. **Privacy and cross-border** (LEG): PII/PHI reason codes, pattern assist, data residency, processing record. → `E11-T04`, `E20-T04`, `E20-T05`, Q-24.
3. **Reviewer attestation** (LEG): protective-order "Exhibit A" acknowledgments, AEO. → `E20-T03`.

## 11. Performance methodology (§17, §26, §29, §30)

1. **Index lag has no measurement point** (QA). Use the commit timestamp on work rows, a refresh-aware watermark and a 1 s gauge. → `E07-T08`, `E17-T06`.
2. **Coding→searchable needs a black-box probe** (QA): public API in and public search out. → `E17-T06`.
3. **⚠ Simple vs complex undefined; mix lacks coding filters** (QA, UI). → A-18, A-22. *Proposed amendment.* → `E17-T04`.
4. **No think-time/session model; coordinated omission** (QA): open and closed models. → `E17-T04`.
5. **No oracle for stale overwrites** (QA): shadow ledger. → `E17-T07`.
6. **Fault catalogue and harness missing** (QA, BE). → `E18-T01`.
7. **Result bundles and reproducibility rule** (QA, OPS): manifest schema, ≥ 3 repetitions, numeric "material advantage". → `E17-T03`, `E17-T08`, Q-04.
8. **CI cost tiers** (QA, OPS): PR 10K, nightly 100K–1M, weekly/on-demand 10M; corpus caching. → `E03-T01`, `E17-T05`, `E18-T06`.
9. **Generator distributions underspecified** (QA, EDS): "1:3" ambiguity, duplicate types, text distribution, vocabulary/needles. → `E17-T01`, Q-06.
10. **⚠ Bulk-load definition** (QA): fixed offered rate vs the ≥ 10K docs/s ceiling. → A-22. → `E18-T02`, Q-04.
11. **RPO/RTO untested** (QA, OPS). → `E18-T09`, `E19-T08`.
12. **E2E UI testing absent** (QA, UI). → `E03-T03`, `E16-T07`, `E16-T08`.

## 12. Deployment and operations (§4, §16, §17)

1. **Lite vs Full undefined** (OPS). Lite should be one combined worker process; Full one container per worker type plus a 3-node OpenSearch, PG replica and LB. → `E01-T02`, `E19-T03`, `E19-T06`.
2. **Object-store licensing** (OPS). MinIO is AGPLv3 and its 2025 community-edition changes must be re-verified; evaluate SeaweedFS, Garage, Zenko, RustFS and a filesystem provider. → `E19-T02`, Q-38.
3. **OpenSearch dev-mode memory** (OPS): `vm.max_map_count`, heap, initial admin password; ~6–8 GB for Lite. → `E19-T03`, Q-39.
4. **Backup per store** (OPS): PITR with `archive_timeout ≤ 60 s`, OpenSearch snapshots plus catch-up (rebuild cannot meet RTO at 10M+), bucket replication, RabbitMQ redrive from PG, post-restore reconciliation. → `E19-T07`, `E19-T08`.
5. **Observability is a blocker** (OPS, BE): OTel end to end, SLO metrics, alerts. → `E19-T04`, `E19-T05`.
6. **Images, release and config** (OPS): multi-arch, signed, no `latest`, SemVer, compatibility matrix, 12-factor config, `ValidateOnStart`. → `E01-T02`, `E01-T05`, `E01-T06`.
7. **Health checks** (OPS): live vs ready; readiness must not flip on index lag. → `E01-T02`.

## 13. Reviewer UX (§9, §13, §14, §22, §24, §28)

1. **⚠ No frontend architecture section** (UI). → A-19. *Proposed amendment.*
2. **Grid paging at 100M** (UI): `search_after` + PIT, approximate totals, no deep page jumps. → `E07-T05`, `E16-T02`, Q-32.
3. **⚠ ADR-004 constrains grid sort/filter/facet** (UI). → A-18.
4. **Watermark wording for reviewers** (UI): Current / Updating / Delayed, "as of" stamps, admin-only raw generations. → `E16-T07`, Q-10.
5. **Read-your-own-writes** (UI): local overlay plus a "saved · indexing" badge; the API returns versions. → `E10-T01`, `E16-T05`.
6. **Viewer modes and term hits** (UI, EDS): chunked text API, server-side hit offsets, persistent highlight sets. → `E11-T01`, `E16-T04`, `E16-T12`.
7. **Coding panel model** (UI): layouts, required/conditional fields, save-and-next, conflict UX, family propagation. → `E04-T03`, `E16-T05`, `E09-T05`, Q-14.
8. **Security-affecting coding UX** (UI). → `E16-T08`.
9. **Frozen counts in bulk confirmations; undo; partial failure UX** (UI). → `E16-T06`, `E06-T07`, Q-34.
10. **Two-phase progress; push vs polling** (UI). → `E06-T02`, `E06-T07`, Q-35.
11. **Family/duplicate/thread display** (UI). → `E16-T10`.
12. **Keyboard-driven review and WCAG 2.2 AA** (UI): 2.4.11, 2.5.7, 2.5.8, 3.3.7, 2.1.4. → `E15-T03`, `E15-T04`, Q-37.
13. **Redaction UX** (UI): pending renders, reason picklist, preview, concurrency. → `E11-T04`, Q-22.
14. **UI performance targets** (UI): next doc ≤ 500 ms p95, coding ack ≤ 200 ms. → `E15-T01`, `E16-T03`, `E16-T05`.
