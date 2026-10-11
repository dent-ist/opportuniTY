# ADR-016: Backup, disaster recovery and restore consistency

| Field | Value |
|---|---|
| **Status** | Proposed. §1–§8 are the binding interim position for `E19-T08` (#164). The §9 RTO verdicts are *pending measurement*: reindex and restore throughput are hardware-bound, and Q-70 defers that work to `E18-T09` (#156) and the `E19-T08` drills. |
| **Date** | 2026-10-11 |
| **Owner (role)** | DevOps / SRE |
| **Deciders** | Lead architect; contributing: Backend; review: QA & Performance, Security & Compliance (T-63); product owner (Q-40 scope, §6.4 lost-window policy) |
| **Tracking issue** | #163 (plan key `E19-T07`) |
| **Baseline sections** | [§2](../architecture/architecture-baseline.md#2-core-principles), [§11](../architecture/architecture-baseline.md), [§15](../architecture/architecture-baseline.md#15-security-audit-and-lifecycle), [§16](../architecture/architecture-baseline.md#16-cache-and-deployment-profiles), [§17](../architecture/architecture-baseline.md#17-non-functional-engineering-targets), [§21](../architecture/architecture-baseline.md), [§28](../architecture/architecture-baseline.md) |
| **Related** | [ADR-001](0001-search-consistency-outbox-and-version-model.md) (versions, generation, watermark), [ADR-006](0006-opensearch-index-strategy.md) (R13 reindex), [ADR-010](0010-job-chunk-idempotency-semantics.md) (leases, fencing), [ADR-011](0011-object-storage-addressing.md) (write-once, registry, orphans), [ADR-013](0013-audit-architecture-and-event-taxonomy.md) (hash chain), [ADR-014](0014-retention-and-deletion-lifecycle.md) (residuals, `BackupRetention`), [ADR-015](0015-security-architecture-and-trust-boundaries.md) (T-63, D10), [ADR-017](0017-observability-and-slos.md) (WAL-archive alert), [ADR-020](0020-bundled-object-storage.md) (SeaweedFS); findings A-02, OPS §4; QA finding 13; Q-01, Q-38, Q-40, Q-70 |
| **Design** | [Restore-consistency design](../architecture/restore-consistency.md): the restore procedure, catch-up algorithm, proofs and checks |

## Context

The §17 targets are **RPO ≤ 5 min** and **RTO ≤ 1 h**. Q-40 limits them to the **Full** profile and makes off-site DR
an operator responsibility in v1. Q-01 makes Lite evaluation-only: it holds no real client data. The installation has
six kinds of state, and each one needs its own strategy (DevOps review, OPS §4):

| Store | What the code keeps there | Authority |
|---|---|---|
| **PostgreSQL**, schemas `opportunity` and `audit` | every domain row (`workspace`, `document`, `document_projection_state`, coding, `job`/`job_chunk`, `search_outbox`, `index_chunk_task`, `workspace_search_generation`, `workspace_search_watermark`, `workspace_index_placement`, `search_reindex`, `stored_object`, `bates_range`, productions, `preservation_lock`, `workspace_deletion`, `destruction_certificate`, wrapped `workspace_data_key` rows, `user_session`, `break_glass_activation`, `data_protection_key`, `dead_letter`, …) and the audit trail (`audit.audit_event`, `audit.chain_head`, `audit.checkpoint`, `audit.chain_gap`) | **Authoritative** for everything (§2, ADR-001, ADR-010 §7, ADR-011 §2.3) |
| **OpenSearch** | projection indices `<prefix>-shared-NNN-gG` / `<prefix>-ws-<id>-gG` behind aliases (`IndexNames`), templates `<prefix>-projection-gG` | **Derived**: payload-free work records rebuild it from PostgreSQL (ADR-001 §1) |
| **Object storage** (`IObjectStore`: filesystem in Lite, SeaweedFS in Full) | natives, extracted text, OPT images, renditions, snapshot manifests, export and production volumes, reports, `sys/certificates/…` | **Bytes are authoritative** (originals cannot be regenerated); the **inventory** is PostgreSQL `stored_object` |
| **Key provider** (local key directory `kek/…`, `signing/…`; later KMS) | installation KEK, dedicated KEKs `ws-<id>`, the `audit-checkpoint` ES256 signing keys | **Authoritative** for key material; PostgreSQL holds only wrapped data keys |
| **RabbitMQ** | transport only; dead letters are copied to `opportunity.dead_letter` | **None**: job and search-work state lives in PostgreSQL (§11, ADR-010 §7) |
| **Configuration and secrets** | `.env`/Docker secrets, Keycloak or the IdP, broker users (`deploy/docker-compose/rabbitmq/users.sh`) | Operator-managed |

Three properties of the code decide how a restore can stay consistent:

1. **External versioning.** OpenSearch accepts a write only if its `DocumentVersion` is higher than the stored one, and
   it treats `409` as success (`ProjectionIndexWriter`, ADR-001 §3). If a restored PostgreSQL is *behind* OpenSearch,
   PostgreSQL issues version numbers again that OpenSearch already holds with other content. Those writes are then
   silently dropped as "stale", and the index stays wrong forever. **OpenSearch behind PostgreSQL is normal operation
   (lag, shown by the freshness watermark). OpenSearch ahead of PostgreSQL is corruption.**
2. **Write-once objects** (ADR-011 §4). Keys are content-addressed or carry random ids, so a key is never reused with
   different bytes on a new timeline. A restored PostgreSQL that references an object the store no longer holds is a
   data loss. An object without a row is only an orphan.
3. **Leases with fencing tokens** (ADR-010 §3) and claims are numbers in PostgreSQL. A worker process that survives a
   point-in-time restore can hold a token that the restored timeline issues again.

A full rebuild of the projection from PostgreSQL (alias reindex, ADR-006 R13, `ReindexCoordinator`) is always correct,
but its time grows with the corpus. At 10M–100M documents it cannot fit in one hour (§9).

## Decision

### 1. Consistency invariant and recovery point

1. **Invariant (restore order).** After any restore, and before any writer runs:
   **OpenSearch ≤ PostgreSQL ≤ object storage, and key provider ⊇ what PostgreSQL references.** In words:
   OpenSearch holds, per document, no state newer than PostgreSQL. Every object that a `stored_object` row references
   exists. Every KEK version that wraps a live `workspace_data_key` row, and every signing key id in `audit.checkpoint`,
   is available.
2. **The recovery target `T_r`** is the PostgreSQL point in time. The operator picks it, and the restore tool (§8)
   refuses one that breaks the invariant:
   - `T_r` ≥ the `end_time` of the chosen OpenSearch snapshot + `ε`, with `ε = 60 s` clock-skew allowance (NTP required
     on every host). If no snapshot qualifies, OpenSearch is rebuilt (§3.5).
   - `T_r` ≤ the point up to which the object store holds every object (live site: now; after site loss: the replica's
     sync point, §4.3).
   - `T_r` ≤ the newest key backup's coverage. A key created or rotated after the last key backup makes `T_r`
     earlier, or the key backup is taken first (keys must be backed up after every key event, §5).
3. **A restore to the end of WAL** (crash, host loss with the archive intact) loses nothing. It may keep the live
   OpenSearch and object store. A restore to an *earlier* `T_r` MUST NOT keep the live OpenSearch: OpenSearch is restored
   from a qualifying snapshot or rebuilt (path B in the design keeps it only after an exhaustive ahead-scan).
4. **No writer survives a restore.** Every API, worker, dispatcher and migrator process is stopped before PostgreSQL is
   restored. Every queue is emptied (§6) before any process starts again. Processes from the lost timeline may hold
   leases, claims, fencing tokens, cached placements and cached data keys that the restored timeline no longer knows.

### 2. PostgreSQL: continuous WAL archiving and PITR

1. **Tool: pgBackRest** (MIT). It provides parallel and `--delta` restore, `--type=time` targets, asynchronous
   archiving, repository encryption, S3/Azure/POSIX repositories, two repositories, and a `verify` command. WAL-G
   (Apache-2.0) is the documented alternative and has the same rules.
2. **Settings** (Full): `archive_mode = on`, `archive_timeout = 60s`, `archive-async = y`, `wal_level = replica`.
   Backups: **full weekly, differential daily, incremental every 6 h**. They are tightened when the WAL since the last
   backup takes longer than 10 minutes to replay (the measured replay rate is a drill output), because WAL replay is
   part of the RTO (§9).
3. **Repositories.** `repo1` is a bucket or container **separate from the artifact bucket**, with its own credentials
   that the application logins cannot read (T-63), and `repo-cipher-type = aes-256-cbc` with the passphrase from the
   secret provider. `repo2` is off-site and an operator responsibility (Q-40); the runbook documents it.
4. **Retention** is time-based and equals `WorkspaceDeletion:BackupRetention` (default **35 days**). The destruction
   certificate already states this as the date by which backups expire (ADR-014 §8, `workspace-deletion.md`).
5. **RPO signal.** `WalArchiveLagHigh` (> 4 min while WAL is written) and `WalArchiveFailing` already exist
   (`deploy/docker-compose/observability/alerts.yaml`, ADR-017). With `archive_timeout = 60s` the expected data loss is
   ≤ 2 min. The 5-minute target leaves room for one failed push.
6. **A streaming replica is not a backup.** The asynchronous replica (`docs/benchmarks/reference-environments.md`) serves
   HA failover to the end of WAL. Failover is covered by rule 1.3 and is not a PITR.
7. **The SeaweedFS filer metadata MUST live in a separate PostgreSQL cluster** (instance), not in a second database of
   the application cluster. PITR restores a whole cluster: rolling the filer back with the application would hide
   objects written after `T_r` and resurrect entries for deleted ones. This amends ADR-020 rule 2. The filer cluster
   gets its own continuous archive and is always recovered to the end of its WAL.

### 3. OpenSearch: snapshots with a PostgreSQL catch-up anchor

1. **Repository:** `repository-s3` against a dedicated bucket with its own credentials (T-63), never the artifact
   bucket. Snapshots cover `<prefix>-*` indices with `include_global_state: false`. Templates and the security plugin
   configuration come back from the migrator bootstrap and the Full deployment configuration.
2. **Cadence: hourly**, made by an opportuniTY snapshot job, not by a bare Snapshot Management policy. Before it starts
   a snapshot, the job records the anchor `G_s(ws)` = `workspace_search_watermark.indexed_through_generation` of every
   workspace in a PostgreSQL table (installation-level snapshot row plus per-workspace rows; schema in the design
   doc, migration in `E19-T08`). All work with generation ≤ `G_s` was applied and refreshed before the snapshot began,
   so the snapshot contains it. Because the anchor is committed **before** the snapshot starts and `T_r` ≥ the snapshot's
   end, every usable snapshot's anchor survives the PostgreSQL restore. No extra manifest store is needed. Retention:
   48 hourly snapshots plus daily ones up to `BackupRetention`. Snapshot Management may run the deletion side.
3. **Snapshot age bound.** A snapshot is usable for catch-up only if `T_r − start_time` ≤ **24 h**. Search-work
   partitions are dropped once applied and older than `SearchWorkHousekeepingOptions.Retention` = 3 days (`SearchWorkHousekeeper`, not configurable today).
   Past that window, the work records needed for the replay may be gone, so the tool takes path B or C.
4. **Catch-up** (design doc §4) runs offline, before any writer starts. Every `search_outbox` and `index_chunk_task`
   row with `search_generation > G_s(ws)` goes back to `Pending`. `workspace_search_watermark` is set to `G_s(ws)`: this
   is the one sanctioned exception to "the watermark never moves backwards" (ADR-001 §7.3), so freshness reads
   *Updating* instead of claiming search is current. Every workspace in `Deleting`/`Purged` is purged from search again.
   Reindex runs (`search_reindex`) that were not terminal at `T_r` are aborted. Placements whose physical index is not
   in the snapshot are rebuilt. Normal dispatch then re-drives the work: no new write path, and the same external
   versioning.
5. **Full rebuild** (alias reindex per workspace, `jobs reindex`) is always correct. It is the path when no snapshot
   qualifies, and the planned path for Lite. §9 states when it meets the RTO.

### 4. Object storage: versioning, replication, no point-in-time rollback

1. **Object storage is never rolled back to a point in time.** Objects are write-once, so the live store (or the
   replica after site loss) is used as it is. Single objects that were deleted or damaged are recovered from versions.
   This satisfies "objects ⊇ PostgreSQL" without coordinating timestamps.
2. **Full:** bucket **versioning on**, with a lifecycle rule `NoncurrentVersionExpiration = BackupRetention`, and a
   lifecycle expiry for `ws/*/tmp/` (ADR-011 §1.5). ADR-011 §3.4 still holds: correctness never depends on versioning.
   Versions are a backup mechanism only. Noncurrent versions left by a workspace deletion count as backup residuals,
   which the certificate already covers until `BackupRetention`, and are unreadable at once under crypto-shredding
   with a dedicated KEK. `E19-T08` MUST run the `IObjectStore` contract suite against SeaweedFS **with versioning
   enabled** (the write-once `If-None-Match` race test in particular) before it turns versioning on.
3. **Replication:** `weed filer.sync` to a second SeaweedFS cluster. Off-site placement is an operator choice (Q-40).
   Replication is asynchronous; its lag is the object RPO after site loss and is alerted at the same 4-minute
   threshold. After site loss, `T_r` ≤ the replica's sync point (rule 1.2).
4. **Lite (filesystem provider):** a file-level copy of the `objects` volume taken **after** the PostgreSQL backup of
   the same run, which keeps rule 1.1.

### 5. Keys and the audit chain

1. **Keys** are backed up separately from data, as already decided (`keys-and-secrets.md` "Back up the keys"). Back up
   after every `rotate-kek`, `dedicate` and `rotate-signing`, keep the backups **at least `BackupRetention`**, and store
   them with different access from the database and object backups. Destroyed KEKs must also expire from key backups
   before crypto-shredding is complete. `KeyManagement:Local:CreateMissingKeys = false` in Full, so a missing key
   directory fails loudly after a restore and no new key ring is created silently.
2. **The audit chain is in PostgreSQL** and is restored as a transaction-consistent prefix. `audit verify` MUST pass on
   every chain after a restore. Events and checkpoints after `T_r` are lost with the rest of the window (that is the
   RPO). Signed checkpoints exported before the restore may cover positions the restored chain will now reuse.
3. **Restore record.** Before reopening, the restore tool writes an event to the system chain and to every workspace
   chain. The event names the restore id, `T_r`, the backup label and the snapshot used, and for that chain the
   restored head (sequence, hash). The sealer then takes a checkpoint with a new reason `AfterRestore`. The same
   facts are stored as a signed restore record under `sys/restores/{restoreId}/{sha256}` in object storage, which a
   later restore cannot roll back. A verifier who holds an older exported checkpoint can then prove that the fork is a
   documented restore and not tampering. This needs taxonomy additions (`Integrity.Restored`, checkpoint reason
   `AfterRestore`) in `E19-T08`.

### 6. RabbitMQ and in-flight work: redrive from PostgreSQL

1. **No queue backup.** After a restore the broker starts empty: a new node, or a deleted and re-created vhost. Every
   queue, `*.dlq` and `*.parking` included, is emptied, because each message describes a timeline that no longer
   exists. Broker users and permissions come from configuration (`users.sh`). The **migrator** declares the topology
   again (`RabbitMqTopologyBootstrapStep`), together with the index templates.
2. **Redrive.** The restore tool resets every unfinished state of the old timeline to `Pending` in PostgreSQL. That
   covers `search_outbox` rows that are `Claimed`/`Dispatched`; `index_chunk_task` and `job_chunk` rows that are
   `Dispatched`/`Running`, with leases cleared; and dispatcher claims. These are the same statements as
   `jobs redispatch` (`SearchWorkMaintenance.RecoverAsync`), without the timeout. The dispatcher then publishes from
   PostgreSQL. Duplicates are harmless (ADR-010 §5, ADR-001 §3).
3. Jobs that were *created* after `T_r` are lost. Clients resubmit them. `Idempotency-Key` records are in PostgreSQL
   and were lost with them, so a resubmission is new work, not a duplicate.

### 7. Security state after a point-in-time restore

The restored PostgreSQL is security state from the past. Before reopening, the tool MUST:

1. revoke every `user_session` (`revoked_at`, reason `Restore`) so everyone signs in again against the IdP, which stays
   authoritative for accounts and groups;
2. end every open `break_glass_activation` (`ended_reason = 'Revoked'`);
3. drop every `search_session`/`search_cursor`, because their `pit_id`s point to readers that no longer exist.
   Point-in-time selections are never stored (`SelectionStrategy.PointInTime`). Materialized `document_set_snapshot`
   membership is in PostgreSQL and stays valid;
4. produce the **lost-window report** (design doc §6). Changes made after `T_r` cannot be read from PostgreSQL but leave
   traces outside it. **Deletion certificates** in `sys/certificates/` whose deletion is not `Completed` in the restored
   database mean the workspace must be fenced and its deletion run again before it reopens. Never let a destroyed
   workspace come back to life. **Production and export outputs** under `ws/*/productions/…` and `ws/*/exports/…`
   that the database does not know mean the Bates numbers in their `MANIFEST.json` may already be delivered. The
   workspace's production finalization stays blocked until an operator records them. **KEK versions** destroyed in the
   key provider while their wrapped keys are active again must be reported. Holds, role or wall changes, and
   privilege decisions made in the window are not recoverable from the platform. The report lists the window so that
   case teams can re-apply them (§6.4 of the design doc; product-owner decision requested).

### 8. Restore order and post-restore verification

The order is fixed. The design doc gives each step's commands, tables and failure handling.

| Phase | Action | Gate (must pass before the next phase) |
|---|---|---|
| R0 Stop | Stop all writers and the broker; record the incident time | no process connected to PostgreSQL except the tool (`pg_stat_activity`) |
| R1 Choose `T_r` | From the PostgreSQL archive, the OpenSearch snapshot list (state `SUCCESS`, `end_time`), the object replica's sync point and the key backups | rule 1.2 holds; the tool prints the chosen snapshot and the loss window |
| R2 Keys | Restore the key directory if lost | `keys status`: installation KEK and every signing key id present |
| R3 PostgreSQL | `pgbackrest restore --type=time --target=T_r` (or `--delta`), promote | recovery reached `T_r`; `pgbackrest verify` clean; migrator at the image's schema version |
| R4 OpenSearch (parallel with R3) | Restore the snapshot's `<prefix>-*` indices with aliases, or start empty for a rebuild | cluster green or yellow; every restored index readable |
| R5 Objects | Live store, or promote the replica; recover single objects from versions | inventory check (below) |
| R6 Reconcile | Catch-up (§3.4), redrive (§6.2), security reset (§7), deletion re-fence, restore record (§5.3), lost-window report | the checks below |
| R7 Reopen | Start migrator → dispatcher → workers → API | `/health/ready`; freshness *Updating* → *Current* |
| R8 Converge | After catch-up: full version reconciliation | 0 ahead, 0 stale, 0 missing |

**Verified after restore.** Gates marked "pre-open" must pass before R7.

| Check | Pre-open | Tool / basis |
|---|---|---|
| PostgreSQL reached `T_r`; repository verifies | yes | pgBackRest, recovery log |
| Every audit chain verifies; restore record sealed with an `AfterRestore` checkpoint | yes | `audit verify`, `audit seal --checkpoint` |
| Every live `workspace_data_key` unwraps; every `audit.checkpoint` key id has a public key | yes | `keys status` |
| Objects: 100% `HEAD` (size) of `stored_object` rows created in `[T_r − 24 h, T_r]`; a random sample (≥ 10,000 or 0.1%) of the rest; full pass afterwards with the ADR-011 §2.5 scrub | window + sample | `IObjectStore.HeadAsync`, `VerifyingReadStream` |
| Orphans: objects without a `stored_object` row (listing of `ws/<id>/`) are reported, not deleted. The ADR-011 §2.4 reconciler removes them after 24 h, except under `productions/`, `exports/` and `sys/`, which wait for the lost-window review | report | `IObjectStore.ListPrefixAsync` |
| Search placement: every `workspace_index_placement` resolves to an existing index and alias | yes | `IIndexManager` |
| **Search not ahead:** the chosen snapshot satisfies `end_time + ε ≤ T_r` (enforced). Sampled ahead-check: for 10,000 random documents per workspace plus every document with work above `G_s`, the OpenSearch `_version` is ≤ `document_projection_state.document_version`, and nothing is indexed for a document PostgreSQL does not have | yes | snapshot metadata; `_mget` versus PostgreSQL in one snapshot (the shadow-ledger and `ReindexValidator` comparison) |
| Work state: no `Claimed`/`Dispatched`/`Running` rows; broker queues empty | yes | SQL, broker API |
| Sessions revoked, break-glass ended, search sessions dropped | yes | SQL |
| Lost-window report produced and acknowledged by the operator | yes | §7.4 |
| **Convergence:** every workspace's watermark reaches its latest generation, then the full PG ↔ OpenSearch version reconciliation reports 0 ahead / 0 stale / 0 missing / 0 unexpected, and projection hashes match on the `ReindexValidator` sample | after | `ReindexValidator` logic as a standalone check (`E19-T08`); `opportunity-bench ledger` in drills |

### 9. RTO per scale milestone

RTO is counted from the decision to restore until reviewers can sign in, search, view and code with correct access
control. At that point search may still be catching up: the freshness pill says *Updating*. That is acceptable
because access never depends on the projection (Q-12 post-filter, ADR-001 §5.4), and OpenSearch is never *ahead*.
Time until search is *current* is reported separately.

**Model.** `RTO = t_decide (10 min) + max(t_PG, t_OS) + t_gates (5 min) + t_start (5 min)`. Here
`t_PG = S_PG / r_PG + t_replay` (with `t_replay` ≤ 10 min under rule 2.2) and `t_OS = S_OS / r_OS` for a snapshot
restore. A rebuild instead adds `N / R` *after* PostgreSQL, because it reads PostgreSQL and object storage.
**Assumptions, not measurements** (Q-70; `E19-T08`/`E18-T09` replace them): `S_PG` ≈ 10 KB per document;
`S_OS` ≈ 30 KB per document of primary shards (text dominates, Q-29); `r_PG` ≈ `r_OS` ≈ 500 MB/s aggregate.

| Docs | `t_PG` | `t_OS` (snapshot) | RTO, snapshot path | Rebuild budget left after PostgreSQL | Reindex rate `R` the rebuild would need | **Does rebuild-from-PostgreSQL meet the RTO?** |
|---|---|---|---|---|---|---|
| 1M | 0.3 + 10 ≈ 10.5 min | 1 min | ≈ 31 min | ≈ 29.5 min | ≥ **≈ 570 docs/s** sustained | **Expected yes, unmeasured.** Allowed as the fallback. If the `E19-T08` 1M drill measures less, 1M is snapshot-only too |
| 10M | 3.3 + 10 ≈ 13.3 min | 10 min | ≈ 33 min | ≈ 26.7 min | ≥ **≈ 6,250 docs/s** sustained, text included | **No — not relied on.** Above anything measured, and the §17 ≥ 10K docs/s bulk target is "not assumed". The snapshot path is required |
| 100M | 33 + 10 ≈ 43 min | **100 min** | **> 1 h** with the assumed `r_OS` | ≈ 6.7 min | ≥ **≈ 250,000 docs/s** | **No.** Even the snapshot path needs `r_OS` ≥ ≈ 1.25 GB/s and `r_PG` ≥ ≈ 0.55 GB/s (or a delta restore). Otherwise 100M needs a warm standby (PostgreSQL standby plus OpenSearch cross-cluster replication), which is out of scope for v1 (Q-41) and decided at M5 |

**Measured so far** (informational, shared 4-vCPU developer VM, `fsync=off`; `docs/testing/fault-injection.md` §3):
the PostgreSQL ↔ OpenSearch version and value reconciliation runs at **≈ 8,600 docs/s** with one reader. That is
≈ 2 min for 1M, ≈ 19 min for 10M and ≈ 3.2 h for 100M. So the *full* convergence check is a pre-open gate only up to
1M. Above 1M it runs after reopening (§8), and the pre-open gate is the enforced snapshot rule plus the sampled check.
**No reindex throughput has been measured** on any profile, because #156 and the reference hardware are deferred by
Q-70. The verdict column is therefore a requirement on the rate, not a measurement. It becomes final when `E19-T08`
records the 1M drill (restore time, reindex rate, WAL replay rate) and `E18-T09` records 10M.

**Search-current time** after reopening is `backlog / R`, where `backlog` is the work committed between
`G_s` and `T_r`. With hourly snapshots that is at most about one hour of the installation's write volume. The 1M drill
in `E19-T08` must reach *current* within the hour. At 10M/100M the time is reported, and the cadence moves to 15 min if
the reported time exceeds 1 h.

### 10. Profiles

| | **Full** (production, Q-01) | **Lite** (evaluation, no real data, Q-01) |
|---|---|---|
| Targets | RPO ≤ 5 min, RTO ≤ 1 h (§17, Q-40) | none (Q-40); "best effort" |
| PostgreSQL | pgBackRest, `archive_timeout = 60s`, §2 | nightly `pg_dump -Fc` (or pgBackRest without archiving), keep 7 |
| OpenSearch | hourly snapshots + anchor, §3 | no backup; rebuild (`jobs reindex` per workspace) after a restore |
| Objects | versioning + `filer.sync`, §4 | copy of the `objects` volume after the dump |
| Keys | key backup after every key event, §5 | copy of the `keys` volume, stored apart from the dump |
| RabbitMQ | none, §6 | none |
| Off-site | operator (`repo2`, replica placement), Q-40 | operator |

## Consequences

- **Positive:** one invariant ("OpenSearch ≤ PostgreSQL ≤ objects; keys ⊇ references") that the tool checks before
  writers start. Catch-up uses the existing work records, dispatcher and external versioning: no new index write path,
  and no reliance on clocks beyond the `ε`-checked snapshot choice. Write-once objects make the object store
  restore-free. Restores are documented in the audit chain instead of looking like tampering. Restored security state
  cannot revive sessions, break-glass or destroyed workspaces.
- **Negative / costs:** a second bucket for snapshots and one for pgBackRest. A separate PostgreSQL cluster for the
  SeaweedFS filer. Versioning roughly doubles storage for churned objects until `BackupRetention`. Every restore
  signs everyone out. Changes made in the loss window (holds, privilege calls, role changes) need manual re-entry from
  the lost-window report. 100M cannot meet a 1-hour RTO without hardware well beyond the assumptions, or a standby.
- **Follow-up work:** `E19-T08` (#164): pgBackRest configuration and `restore-to-time`, the snapshot job and anchor
  table, the `restore` tool (R1 checks, R6 reconcile, R8 convergence check), the versioning/replication setup and its
  contract run, taxonomy additions (§5.3), weekly backup verification and drills. `E19-T06` (#162): Full topology with
  the separate filer cluster. `E18-T09` (#156): 10M drill, 0-mismatch oracle after recovery.
- **Verification:** `E19-T08` drills (PITR ≤ 5 min loss; older snapshot then catch-up converges with the 0-stale-version
  check; a deleted native recovered from a version or replica; full drill ≤ 1 h at 1M). Integration tests for the
  catch-up and redrive SQL (re-pending above `G_s`, watermark reset, aborted reindex, re-fenced deletion). A
  fault-matrix cell "restore older snapshot with in-flight work" judged by the shadow-ledger oracle.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Rebuild OpenSearch from PostgreSQL only (no snapshots) | Correct, but at 10M+ it misses the RTO (§9). Kept as the 1M fallback and the Lite path |
| Keep the live OpenSearch after a PITR to an earlier time | It is ahead of PostgreSQL. Versions are re-issued, `409` hides the divergence and the index stays wrong. Allowed only behind an exhaustive ahead-scan (path B) |
| Shift every `DocumentVersion` by a large offset after restore | Restores correctness under any OpenSearch state, but forces a rewrite of every document, which is a full rebuild by another name |
| Snapshot timestamps instead of a generation anchor | Needs `applied_at` and clock reasoning across hosts. The anchor is exact and survives the restore by construction |
| Back up RabbitMQ queues | Messages are transport for PostgreSQL state. Restored messages would describe the lost timeline (stale claims and tokens) |
| Point-in-time rollback of object storage | Not needed for write-once objects, and it would delete outputs that may already be delivered (productions) |
| Filer metadata in the application PostgreSQL cluster | PITR would roll object metadata back with the application (§2.7) |
| Logical dumps (`pg_dump`) for Full | No PITR, and restore time grows with indexes. Lite only |

## Baseline amendments

All *Proposed* until lead-architect and product-owner sign-off.

| ID | Amendment |
|---|---|
| B-1 | §19: add ADR-016 Backup/DR & restore consistency (finding A-02) |
| B-2 | §17: RTO means "service restored with correct access, search possibly catching up and labelled", and the time until search is current is reported separately (§9). RPO/RTO apply to Full only (Q-40) |
| B-3 | §16/§17: the per-store strategy of §2–§6, and the invariant and order of §1/§8 |
| B-4 | ADR-001 §7.3: the restore catch-up may lower `workspace_search_watermark` to the snapshot anchor (offline, before writers start) |
| B-5 | ADR-020 rule 2: the SeaweedFS filer store runs in a PostgreSQL cluster separate from the application cluster |

## Implementation (E19-T08, #164)

§2–§8 are implemented as decided; the runbook is [backup-and-restore.md](../operations/backup-and-restore.md). Where the
implementation chose a detail the ADR left open:

- **Schema** (V0070): `opportunity.search_snapshot` (installation-level) and `opportunity.search_snapshot_anchor` (tenant,
  RLS, expires with its snapshot by cascade), `opportunity.restore_run`, taxonomy `Integrity.Restored`, checkpoint reason
  `AfterRestore`, session revocation reason `Restore`. The anchors of one snapshot are committed workspace by workspace
  (one transaction each) before the snapshot starts; each is a lower bound because the watermark only rises.
- **Scheduling:** backups are taken by age (newest full > 7 d, newest full/diff > 1 d, newest backup > 6 h) by the
  `pgbackrest` service instead of clock times, so a missed window is made up at once; verification (`pgbackrest verify`
  plus a restore test) weekly.
- **Path C** lowers every watermark to 0 so freshness reads *Updating* until `jobs reindex` switched; the tool lists the
  workspaces to reindex. **Path B** is refused by `restore plan` and `restore reconcile` (not automated).
- **Lost window:** certified deletions close the workspace; production and export outputs are reported and need operator
  acknowledgment before the pre-open gates pass. Blocking production finalization automatically is not implemented yet.
- **Objects:** versioning is turned on by the migrator bootstrap (`ObjectStorage:S3:Versioning`) after the contract suite
  passed against a versioned SeaweedFS bucket; single objects are recovered with `restore object`.
- **Not measured yet** (§9 verdicts stay pending): the 1M PITR and snapshot drills and the full DR drill need the Full
  reference host (Q-70); the development drills and their numbers are in the runbook.

## Links

- Design: [restore-consistency.md](../architecture/restore-consistency.md)
- Baseline: §2, §11, §15–§17, §21, §28
- Review: [devops.md](../plan/reviews/devops.md) (backup per store), [qa-perf.md](../plan/reviews/qa-perf.md) finding 13,
  [review-findings.md](../plan/review-findings.md) A-02 and OPS §4
- Decisions: [decisions.md](../plan/decisions.md) Q-01, Q-38, Q-40, Q-41, Q-70
- Runbooks this builds on: [keys-and-secrets.md](../operations/keys-and-secrets.md),
  [audit-chain.md](../operations/audit-chain.md), [re-dispatch-stuck-work.md](../operations/re-dispatch-stuck-work.md),
  [alias-reindex.md](../operations/alias-reindex.md), [workspace-deletion.md](../operations/workspace-deletion.md)
- Threat model: T-63 (backup exposure), [threat-model.md](../security/threat-model.md)
