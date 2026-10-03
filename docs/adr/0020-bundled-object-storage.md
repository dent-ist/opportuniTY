# ADR-020: Bundled object storage

| Field | Value |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-03 |
| **Owner (role)** | DevOps / SRE |
| **Deciders** | Lead architect; product owner (license policy, Q-38); contributing review: Security & Compliance |
| **Tracking issue** | #158 (plan key `E19-T02`) |
| **Baseline sections** | [§4](../architecture/architecture-baseline.md#4-technology-stack), [§15](../architecture/architecture-baseline.md#15-security-audit-and-lifecycle), [§16](../architecture/architecture-baseline.md#16-cache-and-deployment-profiles), [§17](../architecture/architecture-baseline.md#17-non-functional-engineering-targets) |
| **Related** | [ADR-011](0011-object-storage-addressing.md) (addressing, write-once, presign), ADR-013 (audit WORM archive), ADR-015 (security), ADR-016 (backup/DR); Q-01, Q-38, Q-39, Q-40, Q-41 |

## Context

§16 asks us to pick the object store we ship "after checking current license, maintenance status, S3 compatibility and
developer experience". Q-38 sets the license policy: the default is permissive (filesystem for Lite, a **verified
Apache-2.0** S3 store for Full); AGPL stores are allowed only as documented, optional, unmodified external services.
The DevOps review reported that MinIO changed its community distribution in 2025; that had to be re-verified.

What the store must do is fixed by ADR-011 and enforced by the `E19-T01` provider contract suite
(`tests/Opportunity.IntegrationTests/Storage/ObjectStoreContractTests.cs`, 20 tests): atomic write-once
(`If-None-Match: *` on `PutObject` **and** `CompleteMultipartUpload`, including under concurrent writers), SHA-256
verified single PUTs (`x-amz-checksum-sha256`), multipart, ranged GET, user metadata, list and delete by prefix
(`DeleteObjects`), and SigV4 presigned GET/PUT with `response-content-disposition`. ADR-013/015 additionally need
**Object Lock** (compliance mode) for the post-MVP audit archive and finalized productions (`E14-T07`), and Full needs
HA and replication within the RPO ≤ 5 min / RTO ≤ 1 h targets (§17, ADR-016).

### Method (2026-10-03)

- **License** read from the repository `LICENSE` file on the default branch and, where present, the OCI
  `org.opencontainers.image.licenses` label of the evaluated image.
- **Maintenance** from release tags (`git ls-remote --tags`), Docker Hub tag dates (`hub.docker.com/v2/repositories/…`)
  and the project README. The GitHub REST API and `ghcr.io`/`quay.io` were not reachable from the evaluation sandbox.
- **Conformance**: the unchanged contract suite was run against every candidate with a pullable Docker Hub image, by
  setting `OPPORTUNITY_TEST_S3_PROVIDER` (see *Verification*). The concurrent write-once test was then repeated five
  times per passing candidate, since a race can pass by luck.
- **Footprint**: uncompressed image size and idle memory (`docker stats`, single node, empty store, 30 s after start).
- **Feature coverage** beyond the suite (Object Lock, versioning, replication) from the project's own documentation.

## Decision

1. **Lite** MUST default to the **filesystem provider** (`ObjectStorage:Provider=FileSystem`, Q-01/Q-38). No object
   store container is required for Lite. Operators who want S3 semantics on one machine MAY run the bundled SeaweedFS
   container (rule 2) as a single `weed server` process.
2. **Full** MUST bundle **SeaweedFS** (Apache-2.0) as its S3-compatible store, pinned in `versions.env` as
   `SEAWEEDFS`/`SEAWEEDFS_DIGEST` (currently `4.48@sha256:4e61d15f…`, image `chrislusf/seaweedfs`). The Full topology
   (`E19-T06`) SHOULD be: 3 masters (Raft), ≥ 2 volume servers with a replicating placement (e.g. `010`), filers backed
   by the `postgres2` filer store in a dedicated database of the Full PostgreSQL, and ≥ 2 stateless S3 gateways behind
   the load balancer. Cross-site replication for DR uses `weed filer.sync` (ADR-016 decides how).
3. The **fallback** S3 store is **RustFS** (Apache-2.0). It passes the same contract suite unchanged and supports
   Object Lock, versioning and erasure-coded distributed mode. Switching is configuration only (`ObjectStorage:S3`
   endpoint and credentials; no code change, no key-layout change) plus a data copy (`E19-T08` tooling). Because RustFS
   reached 1.0 on 2026-09-16, it MUST be re-evaluated at the first quarterly dependency review before it can replace
   SeaweedFS as the default.
4. Supported **external** S3 services (operator-provided, never shipped by us): AWS S3, Azure Blob (own provider),
   Ceph RGW, Versity S3 Gateway, and any other store that passes the contract suite in `external` mode. AGPL stores
   (MinIO, Garage) are only allowed this way, unmodified (Q-38), **and** only once they pass the suite — Garage
   currently does not (rule 6).
5. A store MUST NOT be bundled or documented as supported unless the full contract suite passes against the exact
   pinned image. Version bumps of `SEAWEEDFS` MUST keep the suite green in CI.
6. **Rejected** for bundling: MinIO (AGPL-3.0, community edition unmaintained and source-only, no community images),
   Garage (AGPL-3.0; fails atomic write-once; no versioning or Object Lock), Zenko CloudServer (no current images on
   Docker Hub; fails 3/20 on the newest available one), S3Proxy (no Object Lock or versioning on local backends; no
   HA), Ceph RGW (LGPL, heavy multi-daemon footprint for a bundle). See the table.

### Evaluation results

Contract column: tests passed out of 20 on 2026-10-03; "race ×5" is the concurrent write-once test repeated.

| Candidate (image evaluated) | License (verified) | Maintenance (verified 2026-10-03) | Contract suite | Object Lock | Versioning | HA / replication | Footprint (image / idle RSS) | Verdict |
|---|---|---|---|---|---|---|---|---|
| **SeaweedFS** `chrislusf/seaweedfs:4.48` | Apache-2.0 (`LICENSE`; image label) | Active: 4.48 published 2026-09-28; `dev` images daily | **20/20**; race ×5: 5/5 | Yes, governance + compliance, legal hold (bucket created with lock; needs versioning) | Yes | Raft masters, volume replication / EC, filer on PostgreSQL, `filer.sync` cross-cluster | 724 MB / 72 MiB (master, volume, filer and S3 in one process) | **Bundled (Full); optional Lite** |
| **RustFS** `rustfs/rustfs:1.0.0` | Apache-2.0 (`LICENSE`; image label) | Active: 1.0.0 GA 2026-09-16, `1.0.1-preview.16` 2026-10-02 | **20/20**; race ×5: 5/5 | Yes (README) | Yes | Distributed erasure-coded mode, bitrot healing | 401 MB / 67 MiB | **Fallback** (re-evaluate, rule 3) |
| **Versity S3 Gateway** `versity/versitygw:v1.8.0` | Apache-2.0 (`LICENSE`; image label) | Active: v1.8.0 2026-09-04, roughly monthly | **20/20**; race ×5: 5/5 | Yes (posix backend) | Yes (`--versioning-dir`) | Stateless gateways; durability/HA delegated to the shared POSIX filesystem | 101 MB / 9 MiB | External option (has no storage layer of its own) |
| **S3Proxy** `andrewgaul/s3proxy:4.1.1` | Apache-2.0 (`LICENSE`; image label) | Active: 4.1.1 2026-09-10, commits daily | **20/20**; race ×5: 5/5 | No (README) | No on filesystem backend | None for local backends | 455 MB / 97 MiB (JVM) | Rejected (gateway, no WORM) |
| **Garage** `dxflrs/garage:v2.4.1` | **AGPL-3.0** (`LICENSE`, GitHub mirror `deuxfleurs-org/garage`) | Active: v2.4.1 2026-09-08 | **19/20** — concurrent conflicting puts: several writers "won"; race ×5: 0/5 | No (docs: missing) | No (docs: missing) | Built-in multi-zone replication | 99 MB / 5 MiB | Rejected (AGPL; write-once not atomic) |
| **Zenko CloudServer** `zenko/cloudserver:latest` (2022 build) | Apache-2.0 (`LICENSE`, `package.json`) | Repo active (`9.x`, `9.5.0-preview`), but Docker Hub images stale since 2022–23; current images only on `ghcr.io` | **17/20** on the stale image — `DeleteObjects` rejected (Content-MD5 vs SDK CRC checksum), conflicting puts not atomic | Yes (Scality docs) | Yes | Needs Scality metadata/data backends for HA | 2.05 GB / 544 MiB | Rejected (no current public image; failures) |
| **MinIO** community | **AGPL-3.0** (`LICENSE`) | **Unmaintained**: README says "THIS REPOSITORY IS NO LONGER MAINTAINED" and "distributed as source code only"; last tag `RELEASE.2025-10-15T17-29-55Z`; `minio/minio` no longer exists on Docker Hub (pull denied, Hub API 404) | Not run (no image) | Yes | Yes | Yes (erasure sets, site replication) | — | Rejected (AGPL, unmaintained, no images) |
| **Ceph RGW** `quay.io/ceph/ceph` v21 | LGPL-2.1 or LGPL-3 (`COPYING`) | Active: `v21.3.0` tag | Not run (`quay.io` blocked in the sandbox); runnable via `external` mode | Yes | Yes | Yes (multisite) | MON + MGR + OSD + RGW daemons, ≥ 3 nodes for HA | External option only |
| **Filesystem provider** (ours) | Project license | Ours | **20/20** | n/a (Lite has no WORM archive) | n/a | Single host; backup per ADR-016 | none | **Lite default** |

Azurite (MIT, `AZURITE=3.37.0`) passes the suite 20/20 for the Azure Blob provider.

## Consequences

- **Positive:** the shipped bundle is Apache-2.0 end to end (Q-38). SeaweedFS documents its conditional write as
  atomic cluster-wide and it held under the concurrent test. Its filer can keep metadata in PostgreSQL, which Full
  already operates and backs up. It is already the store the shared Testcontainers fixtures use, so dev, CI and Full run
  the same software. A tested fallback exists, and moving to it needs only configuration.
- **Negative / costs:** SeaweedFS has several components (master, volume, filer, S3) that Full has to orchestrate. Its
  image (724 MB) is the largest of the Go/Rust candidates, though idle memory (72 MiB) is close to RustFS. The project is driven mostly by one maintainer (bus factor).
  Admin identities bypass governance retention, so the audit archive (`E14-T07`) MUST use compliance mode under a
  non-admin identity. Object Lock and replication are taken from the documentation, not tested here.
- **Follow-up work:** `E19-T03` adds the developer Compose profile with the pinned image. `E19-T06` builds the Full
  topology in rule 2. `E19-T07`/`E19-T08` decide and implement replication/DR and the provider-migration copy. `E14-T07`
  adds an Object Lock contract test (compliance-mode retention, legal hold). The quarterly dependency review re-checks
  this table and RustFS (rule 3).
- **Verification:** the S3 contract suite runs in CI against the pinned SeaweedFS image. Any candidate is checked with
  the same tests:

  ```bash
  OPPORTUNITY_TEST_S3_PROVIDER=rustfs dotnet test --project tests/Opportunity.IntegrationTests \
    -- --filter-class "Opportunity.IntegrationTests.Storage.S3ObjectStoreContractTests"
  # seaweedfs (default) | garage | rustfs | versitygw | s3proxy | cloudserver | external
  # external: OPPORTUNITY_TEST_S3_ENDPOINT, _ACCESS_KEY, _SECRET_KEY, _REGION, _BUCKET (e.g. AWS S3)
  # image override: OPPORTUNITY_TEST_IMAGE_<KEY>=<ref> (SEAWEEDFS, RUSTFS, GARAGE, VERSITYGW, S3PROXY, CLOUDSERVER)
  ```

  The AWS S3 run (`external`) was **not** performed during this evaluation because no AWS credentials are available to
  the sandbox. It is a pre-release check for whoever holds an AWS test account.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| RustFS as the bundled default | Passes everything and has the simplest topology, but went GA two weeks before this ADR. Too little production history for a chain-of-custody store; it is the named fallback. |
| Versity S3 Gateway | Passes everything, but it is a gateway: durability and HA come from a POSIX filesystem we would also have to ship. Good external option for operators with existing NAS/parallel filesystems. |
| MinIO community | AGPL-3.0 (Q-38). The community repository is marked unmaintained and ships source only. No official images are left. |
| Garage | AGPL-3.0. Fails the concurrent write-once test, and has no versioning or Object Lock. |
| Zenko CloudServer | Current builds are only on `ghcr.io`. The newest Docker Hub image is from 2022 and fails 3 tests. HA needs Scality's wider stack. |
| S3Proxy | No Object Lock or versioning on local backends, and no HA. |
| Ceph RGW | Mature and complete, but too heavy to bundle (several daemons, ≥ 3 nodes). Supported as an external service. |
| Filesystem for Full | No presigned delivery, no multi-node HA (Q-01 limits it to Lite). |

## Baseline amendments

None. This ADR fills the §16 choice ("choose bundled object-storage software …") without changing the baseline text.

## Links

- Baseline: §4, §15, §16, §17
- Decisions: [decisions.md](../plan/decisions.md) Q-01, Q-38
- ADRs: [ADR-011](0011-object-storage-addressing.md), [ADR-013](0013-audit-architecture-and-event-taxonomy.md),
  [ADR-015](0015-security-architecture-and-trust-boundaries.md)
- Contract suite and candidate fixtures: `tests/Opportunity.IntegrationTests/Storage/` (`S3Providers.cs`,
  `S3StoreFixture.cs`)
- Primary sources (read 2026-10-03):
  - MinIO: <https://github.com/minio/minio/blob/master/README.md>, <https://github.com/minio/minio/blob/master/LICENSE>,
    <https://hub.docker.com/r/minio/minio> (gone)
  - SeaweedFS: <https://github.com/seaweedfs/seaweedfs/blob/master/LICENSE>,
    <https://github.com/seaweedfs/seaweedfs/wiki/Amazon-S3-API>,
    <https://github.com/seaweedfs/seaweedfs/wiki/S3-Object-Lock-and-Retention>,
    <https://github.com/seaweedfs/seaweedfs/wiki/S3-Conditional-Operations>,
    <https://hub.docker.com/r/chrislusf/seaweedfs/tags>
  - RustFS: <https://github.com/rustfs/rustfs/blob/main/LICENSE>, <https://github.com/rustfs/rustfs/blob/main/README.md>,
    <https://hub.docker.com/r/rustfs/rustfs/tags>
  - Versity: <https://github.com/versity/versitygw/blob/main/LICENSE>, <https://hub.docker.com/r/versity/versitygw/tags>
  - S3Proxy: <https://github.com/gaul/s3proxy/blob/master/README.md>, <https://hub.docker.com/r/andrewgaul/s3proxy/tags>
  - Garage: <https://github.com/deuxfleurs-org/garage/blob/main-v2/LICENSE>,
    <https://github.com/deuxfleurs-org/garage/blob/main-v2/doc/book/reference-manual/s3-compatibility.md>,
    <https://hub.docker.com/r/dxflrs/garage/tags>
  - Zenko CloudServer: <https://github.com/scality/cloudserver/blob/development/9.0/package.json>,
    <https://hub.docker.com/r/zenko/cloudserver/tags>
  - Ceph: <https://github.com/ceph/ceph/blob/main/COPYING>
