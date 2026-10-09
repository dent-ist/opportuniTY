# ADR-011: Object-storage addressing, immutability and delivery

| Field | Value |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-02 |
| **Owner (role)** | Backend |
| **Deciders** | Lead architect; contributing: Security & Compliance, Legal / Discovery Counsel |
| **Tracking issue** | #32 (plan key `E02-T06`) |
| **Baseline sections** | [§2](../architecture/architecture-baseline.md#2-core-principles), [§4](../architecture/architecture-baseline.md#4-technology-stack), [§13](../architecture/architecture-baseline.md#13-rendering-viewer-and-redactions), [§15](../architecture/architecture-baseline.md#15-security-audit-and-lifecycle), [§16](../architecture/architecture-baseline.md#16-cache-and-deployment-profiles), [§24](../architecture/architecture-baseline.md#24-security-consistency-rules) |
| **Related** | ADR-012 (pages/renditions), ADR-013 (audit), ADR-014 (deletion), ADR-015 (security), ADR-016 (backup), [ADR-019](0019-layering-and-api-conventions.md) (`IObjectStore` port); review findings A-05, A-21, §5.6, §5.7; Q-01, Q-08, Q-18, Q-22, Q-29, Q-38 |

## Context

§2.1 says object storage holds artifacts and §4 asks for a provider-neutral abstraction (Azure Blob, S3 or
compatible). §2.3 makes `WorkspaceId` a storage and lifecycle boundary, and §15 expects matter deletion to remove
"storage prefixes … and keys". Nothing defines the key layout, whether objects may change, how integrity is proven, or
how a browser gets bytes.

The reviews require: workspace-prefixed, non-guessable keys; presigned URLs issued only after the §24 authoritative
check, scoped to one object, GET only, short TTL, `attachment` disposition for natives and never logged; worker
credentials scoped per worker type; a `KeyId` recorded per object from day one so per-workspace keys and
crypto-shredding stay possible (security findings 6 and 9). Legal counsel needs a chain of custody: the hash at import
must be re-verifiable later, and originals must never change (attorney finding 10, defensibility pack `E14-T05`).
Q-08 needs productions to be reproducible, so the inputs they read must be immutable. Q-38 makes the Lite store a
filesystem provider and the Full store a permissively licensed S3 implementation; Q-01 makes Lite evaluation-only.

If nothing is decided, `E19-T01` (`IObjectStore`), `E08-T04` (natives/text), `E08-T05` (OPT images), `E11-T02`
(render output) and `E05-T04` (content gateway) will each invent keys, and deletion (`E20-T02`) cannot be proven
complete.

## Decision

### 1. Logical keys

1. Every object has a **logical key**. Providers map logical keys to physical names (§3 below); no code outside
   `Opportunity.Storage` sees a physical name, bucket, container or path.
2. Grammar: lowercase ASCII `[a-z0-9._-]` segments separated by `/`, no empty segment, no `.` or `..` segment, at most
   512 bytes. Identifiers are written as 32-character lowercase hex GUIDs (`N` format) or lowercase hex SHA-256.
3. **No user- or load-file-supplied string ever appears in a key**: not file names, control numbers, custodian names,
   received Bates numbers or OPT paths. This rules out path traversal (`..\..\` in OPT rows) and stops PII or matter
   names from leaking into provider access logs. The single exception is §1.6.
4. Workspace-owned objects live under `ws/{workspaceId}/`. Installation-level objects live under `sys/`. Nothing else
   is allowed at the root.
5. Areas and layouts:

| Area | Logical key | Addressing | Mutability |
|---|---|---|---|
| Native (original) | `ws/{ws}/docs/{documentId}/native/{sha256}` | content | immutable original |
| Extracted text (original, full, Q-29) | `ws/{ws}/docs/{documentId}/text/{sha256}` | content | immutable original |
| Imported page image (OPT) | `ws/{ws}/docs/{documentId}/image/{sha256}` | content | immutable original |
| Rendition (page image, thumbnail, word layer, PDF) | `ws/{ws}/docs/{documentId}/rend/{renditionId}/{name}` | random `renditionId` | immutable once committed |
| Received load files (DAT/OPT as delivered) | `ws/{ws}/imports/{importId}/source/{sha256}` | content | immutable original |
| Browser upload staging | `ws/{ws}/imports/{importId}/upload/{uploadId}` | random | quarantined until scanned (§5.6) |
| Snapshot membership manifests (§22) | `ws/{ws}/snapshots/{snapshotId}/{chunk:000000}.bin` | sequence | immutable |
| Export output | `ws/{ws}/exports/{exportId}/{runId}/…` | generated | immutable once run completes |
| Production output | `ws/{ws}/productions/{productionId}/v{version}/{runId}/…` | generated (§1.6) | immutable once run completes |
| Reports (QC, privilege log, STR, hit reports) | `ws/{ws}/reports/{reportId}/{sha256}` | content | immutable |
| Job scratch | `ws/{ws}/tmp/{jobId}/…` | random | deletable; expires after 7 days |
| Destruction certificates (ADR-014) | `sys/certificates/{deletionId}/{sha256}` | content | immutable |
| Audit archive (post-MVP, `E14-T07`) | `sys/audit/…` in a separate WORM bucket/container | content | immutable (provider lock) |

   `{name}` in renditions is system-generated: `p000001.png`, `p000001.thumb.webp`, `p000001.words.json`,
   `doc.pdf`. Page ordinals are 1-based and six-digit zero-padded; ADR-012 owns the meaning of renditions and pages.
6. Production and export volumes are legal artifacts whose relative layout (`VOL001/IMAGES/IMG001/ABC0000001.tif`)
   is itself the deliverable, so their keys append the generated relative path. The only user-influenced part is the
   Bates prefix, which `E12-T02` validates against `^[A-Za-z0-9_-]{1,20}$` and lowercases only in the key, never in the
   delivered file name (the manifest maps key → delivered path).
7. Identifier randomness: `renditionId`, `uploadId`, `runId`, `importId`, `snapshotId` and `deletionId` are random
   v4 GUIDs (122 random bits). Keys are **not** capabilities, because authorization never depends on not knowing a
   key, but random IDs keep enumeration useless if a credential leaks.

### 2. Content addressing, integrity and the object registry

1. **SHA-256 is the integrity hash** for every object, computed by the writer while streaming. MD5/SHA-1 values from
   load files are stored as received metadata and compared at import (a mismatch is a warning, `E08-T04`); they are never
   used as keys.
2. Content-addressed keys are scoped **per document**. There is no cross-document or cross-workspace deduplication of
   bytes (see Alternatives). Two documents with the same native store it twice.
3. PostgreSQL is the authoritative inventory. Table `StoredObject`: `(WorkspaceId, ObjectId)` PK, `LogicalKey`
   (unique), `Area`, `DocumentId` (nullable), `Sha256`, `SizeBytes`, `ContentType` (sniffed from magic bytes, never
   taken from the load file), `KeyId`, `EncryptionScheme` (`ProviderSse` | `Envelope`), `WrappedDek` (nullable),
   `State` (`Committed` | `Quarantined`), `CreatedAt`, `CreatedByJobId`. Domain rows (Document, Page, rendition,
   production file) reference `ObjectId`, never a key string.
4. Write protocol: (a) upload to the key; (b) the provider confirms the length and, where supported, its own checksum
   (`x-amz-checksum-sha256`, Azure `Content-MD5`); (c) insert `StoredObject` in the same PostgreSQL transaction as the
   domain row that references it. Re-uploading identical bytes to a content key is a no-op after a HEAD plus hash
   comparison (`E19-T01` acceptance criterion). An object without a committed `StoredObject` row is an orphan: the
   reconciler deletes orphans older than 24 h by comparing prefix listings with the registry.
5. Chain of custody: the hash recorded at import is re-verified (a) on every read by export/production workers, which
   stream through a hashing reader and fail the chunk on mismatch, and (b) by a scrub job that samples
   `StoredObject` rows (initial value 1 % per week, tuned in `E14-T05`). A mismatch raises
   `Integrity.HashMismatch` (ADR-013).

### 3. Provider neutrality

1. `IObjectStore` (`E19-T01`) exposes: `Put` (single and multipart, ≥ 64 MiB parts), `Get`/`OpenRead` with byte
   ranges, `Head`, `ListPrefix`, `DeletePrefix`, `PresignGet`, `PresignPut` (staging only), each taking a logical key.
   No method takes a bucket, container or physical path.
2. Physical mapping: **S3-compatible** — one bucket per installation (configurable), object name
   `{installationPrefix}/{logicalKey}`; **Azure Blob** — one container per installation, blob name = same string;
   **Filesystem** (Lite) — `{root}/{logicalKey}` with `/` mapped to the OS separator, files mode `0600`, root outside
   any web root, resolved paths verified to stay under the root.
3. A `WorkspacePlacement` row (default: installation bucket) lets a large or sensitive matter get a dedicated
   bucket/container later without changing logical keys.
4. Correctness must not depend on provider-specific features: no reliance on object versioning, tags, metadata
   headers, server-side copy semantics or event notifications. Provider features may be used as optimizations or
   defense in depth (lifecycle rule for `tmp/`, bucket policies, Object Lock for `sys/audit`).
5. The same contract suite runs against S3 (the Q-38 bundled store), Azurite and the filesystem provider.

### 4. Immutability

1. **Objects are write-once.** No API overwrites an existing key with different bytes; a put that would do so fails
   (`If-None-Match: *` where the provider supports it, HEAD-and-compare otherwise).
2. **Originals** (native, extracted text, imported images, received load files) are never modified or deleted except
   by an ADR-014 deletion. A metadata overlay that replaces a native or text creates a new object and a new reference;
   the prior object and its `StoredObject` row are retained and remain reachable from document history.
3. Renditions are immutable once committed. Re-rendering creates a new `renditionId`. A rendition is
   garbage-collectable only when no current page, no redaction revision (ADR-012) and no finalized production
   manifest references it, and only after a 30-day grace period.
4. Store credentials enforce the rules where the provider can (§5.7). Lite's filesystem provider enforces them in
   process only; this is a documented reduced guarantee consistent with Q-01 (Lite holds no real client data).

### 5. Delivery to browsers: gateway, presigned URLs and streaming

1. Bytes reach a browser only through the protected-content gateway (`E05-T04`). The sequence is fixed:
   **PDP check (§24) → audit event (ADR-013) → presign or stream**. If the audit write fails, the request is denied.
2. Presigned URL scope: one object; `GET` (with `Range`) only; TTL **120 s default, 300 s hard maximum**
   (configurable per installation); response overrides `Content-Type` and `Content-Disposition`;
   `Cache-Control: private, no-store`.
3. Natives and any non-image rendition are always `Content-Disposition: attachment` with a sanitized RFC 6266
   `filename*` and `Content-Type: application/octet-stream`. Natives are never served inline (Q-36); HTML or SVG
   natives are never served as `text/html` or `image/svg+xml`.
4. Delivery mode is per provider: `presign` for S3 and Azure (default for page images, thumbnails and downloads);
   `stream` (the API proxies bytes) for the filesystem provider and for any provider that cannot force the response
   headers above. Streamed responses also carry `X-Content-Type-Options: nosniff`.
5. One gateway grant covers one document and one rendition kind (for example "all review page images of document D"),
   returning one URL per object. It produces one audit event, not one per page.
6. Presigned URLs are bearer secrets: they are **never logged**, traced, put in audit records, error messages or
   ProblemDetails, or stored. Audit records the `ObjectId`(s) and rendition kind. Request-logging middleware and OTel
   exporters redact `X-Amz-Signature`, `sig=` and `se=` query parameters (log-scrubbing test in `E05-T04`).
   Operators are told to disable or restrict provider access logs, because they contain keys.
7. Browser uploads (import volumes) may use `PresignPut` only for `ws/{ws}/imports/{importId}/upload/{uploadId}`,
   with TTL ≤ 900 s, a fixed content length and type. The object is `Quarantined` until the malware scan and hash
   check pass (`E08-T09`). No other area ever receives a presigned PUT, and no presigned DELETE exists.
8. Credentials are scoped per component (enforced through bucket policies or SAS scopes where available):

| Component | Read | Write | Delete |
|---|---|---|---|
| API (gateway) | presign/stream only | `imports/*/upload` presign | none |
| Import worker | `imports/*` | `docs/*/{native,text,image}`, `imports/*/source` | `tmp/*` |
| Render worker (sandboxed, `E11-T03`) | one presigned GET for its input | one presigned PUT per output | none, and no store credentials at all |
| Render orchestrator | `docs/*` | `docs/*/rend/*` | `tmp/*` |
| Export / production worker | `docs/*`, `snapshots/*` | `exports/*`, `productions/*`, `reports/*` | `tmp/*` |
| Lifecycle (deletion) worker | `ListPrefix` | `sys/certificates/*` | `DeletePrefix` on allowed prefixes |

### 6. Encryption and `KeyId`

1. Every `StoredObject` row records a `KeyId` from the first object written. In the MVP the value is the installation
   default key (`installation-default`) with provider-side encryption.
2. When per-workspace keys are enabled (`E05-T09`), new objects use envelope encryption: a per-object data key wrapped
   by the workspace key-encryption key, with `WrappedDek` stored in `StoredObject`. A rewrap job moves existing objects.
3. Destroying the workspace key (ADR-014) makes every remaining copy unreadable, including copies in backups. This is
   what crypto-shredding means here, and it only works because `KeyId` is recorded per object.

*Amendment 2026-10-09 (E05-T09, #54):* the data key is **per workspace and versioned**, not per object.
`opportunity.workspace_data_key` (V0053) holds each workspace's data keys, wrapped by the installation KEK or the
workspace's dedicated KEK; exactly one version is active for new objects. Each object derives its own AES-256-GCM key
with HKDF from the data key, a random salt in a 64-byte object header and the logical key, and is sealed in 64 KiB
chunks so byte ranges stay cheap and truncation is detected. The header names the data key version, `KeyId` records it
as `wdk-v<n>`, and `WrappedDek` stays NULL for envelope objects too (reserved; V0053 relaxes the check). Because objects
are write-once, a per-object wrapped key could not be rewrapped in the object; keeping wrapped keys in a few PostgreSQL
rows makes KEK rotation a rewrap of those rows (the "rewrap job" of §6.2) and never touches objects. "Switching a
workspace to a dedicated key" creates a new data key version wrapped by the dedicated KEK for new objects; the rewrap job
moves the older versions under it, after which destroying the dedicated KEK shreds every copy. Envelope encryption is an
installation setting (`ObjectStorage:Encryption:Mode`, default `ProviderSse`); it is applied by a decorator in front of
any provider, covers `ws/…` objects only (`sys/…` keeps provider SSE), reads objects written before it was switched on
unchanged, and makes delivery `stream` for every provider (§5.4: a presigned URL would serve ciphertext). Listings
report stored (ciphertext) sizes; the registry keeps plaintext sizes and hashes. Runbook:
[docs/operations/keys-and-secrets.md](../operations/keys-and-secrets.md).

### 7. Deletion by prefix

1. `DeletePrefix` accepts only these shapes: `ws/{ws}/`, `ws/{ws}/{area}/`, `ws/{ws}/docs/{documentId}/`,
   `ws/{ws}/tmp/{jobId}/`. Any other prefix is rejected. Only the lifecycle worker holds delete rights.
2. It is idempotent and resumable (paginated list-then-delete), returns object and byte counts, and also removes
   non-current versions and soft-deleted copies when the provider has versioning or soft delete enabled. If they
   cannot be removed, it reports them with their expiry date as residuals for the destruction certificate (ADR-014).
3. Completion is verified by a fresh `ListPrefix` returning zero objects, and by zero `StoredObject` rows for the
   prefix. ADR-014 owns ordering, fencing and holds.

## Consequences

- **Positive:** keys are predictable for deletion and verification, yet carry no matter data. Immutable originals and
  per-object SHA-256 give a chain of custody a declarant can rely on. Q-08 reproduction reads inputs that cannot
  change. Crypto-shredding remains possible without migration. Providers are interchangeable under one contract suite.
- **Negative / costs:** no cross-document byte deduplication (duplicate-heavy corpora store more bytes; accepted for
  simpler deletion and isolation). The PostgreSQL registry adds one row per object (about 3–10 per document). Streaming
  mode on Lite loads the API. Provider access logs remain a residual exposure that operators must manage.
- **Follow-up work:** `E19-T01` (`IObjectStore`, registry, contract suite, `KeyId`), `E05-T04` (gateway, presign
  policy, log scrubbing), `E08-T04`/`E08-T05` (content-addressed originals), `E11-T02`/`E11-T03` (renditions,
  sandbox handles), `E05-T09` (envelope encryption), `E19-T02` (bundled store, bucket policies), `E20-T02` (prefix
  deletion), `E14-T05` (hash re-verification in chain-of-custody reports).
- **Verification:** contract suite on every provider (write-once, idempotent re-put, prefix shapes, range reads);
  architecture test that only `Opportunity.Storage` references provider SDKs and only the gateway calls `PresignGet`;
  log-scrubbing test; a key-grammar property test that rejects user strings and traversal; a reconciler test for
  orphans.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Installation-wide content-addressed store (one copy per hash, reference counted) | Saves bytes, but cross-workspace sharing breaks prefix deletion and per-workspace crypto-shredding, needs reference counting under concurrency, and lets one tenant probe whether another holds a given file. |
| Keys built from control numbers or original file names | Readable, but leaks matter data into provider logs, invites path traversal and breaks on renames or overlays (Q-27). |
| Bucket/container per workspace by default | Natural isolation, but S3 accounts default to 100 buckets while Q-02 allows ≤ 1,000 workspaces, and it complicates provisioning. Kept as an opt-in through `WorkspacePlacement`. |
| Always stream through the API | Simplest security story, but puts all image bandwidth on the API tier and hurts the 500 ms next-document target. Kept as the mode for the filesystem provider. |
| Long-lived or user-bound URLs | Presigned URLs cannot be bound to a user; a short TTL plus authorize-then-audit is the control. |
| Mutable objects with provider versioning | Provider-specific, and "current version" semantics make reproduction and deletion verification harder. |

## Baseline amendments

- *Proposed* — §15: add "Object keys are workspace-prefixed, carry no user-supplied strings, are write-once, and
  every object records SHA-256 and `KeyId` in a PostgreSQL registry."
- *Proposed* — §34 decision log, row "Object storage": append "addressing and delivery per ADR-011".

## Links

- Baseline: §2, §4, §13, §15, §16, §24
- Review findings: [review-findings.md](../plan/review-findings.md) A-05, A-21, §5.6, §5.7, §6.2, §6.3
- Reviews: [security.md](../plan/reviews/security.md) findings 6, 9, 11; [attorney.md](../plan/reviews/attorney.md) finding 10
- Decisions: [decisions.md](../plan/decisions.md) Q-01, Q-08, Q-18, Q-29, Q-36, Q-38
