# Backup and restore (Full profile)

Implements [ADR-016](../adr/0016-backup-dr-and-restore-consistency.md) and the
[restore-consistency design](../architecture/restore-consistency.md) (E19-T08, #164). Targets (Full only, Q-40):
**RPO ≤ 5 min, RTO ≤ 1 h**. Lite has no targets and holds no real data (Q-01); its procedure is at the end.

The one rule every step protects: **OpenSearch ≤ PostgreSQL ≤ object storage, and the key provider ⊇ what PostgreSQL
references.** OpenSearch behind PostgreSQL is lag and heals itself; OpenSearch *ahead* of PostgreSQL is corruption that
external versioning cannot repair, so the tools refuse every combination that could cause it.

## What is backed up, where, how often

| Store | Mechanism (Full) | Cadence | Retention | Loss on restore |
|---|---|---|---|---|
| PostgreSQL (application cluster) | pgBackRest: asynchronous WAL archiving (`archive_timeout = 60s`) + backups to bucket `opportunity-pgbackrest`, `aes-256-cbc` | WAL continuously; full weekly, differential daily, incremental every 6 h (by age, `pgbackrest` service) | 35 days (`repo1-retention-full-type=time`, = `WorkspaceDeletion:BackupRetention`) | ≤ 2 min to end of archive (measured: a quiet committed row reached the archive after 56 s) |
| OpenSearch | snapshots of `opp-*` (no global state) to bucket `opportunity-search-snapshots`, each with PostgreSQL **anchors** (per-workspace visible watermark) committed first | hourly (dispatcher, `Backup__SearchSnapshots__*`) | newest 48 + one per UTC day for 35 days | none: rebuilt from PostgreSQL above the anchor |
| Object storage (artifacts) | never rolled back; bucket **versioning** (deleted objects come back), optional `filer.sync` replica (`--profile replica`) | continuous | noncurrent versions 35 days (lifecycle rule) | none on the live store; replica lag after site loss |
| Keys (`keys` volume) | file copy after every key event, stored apart from data | after `rotate-kek`, `dedicate`, `rotate-signing` | ≥ 35 days | none if backed up after the last key event |
| RabbitMQ | none: the broker starts empty, work is redriven from PostgreSQL | — | — | — |
| SeaweedFS filer metadata (`filer-postgres`) | separate cluster, always recovered to the end of its WAL (ADR-016 §2.7) | — | — | — |

Secrets that must also be kept off this host: `full/secrets/pgbackrest-cipher-pass` (without it the PostgreSQL backups
cannot be read), the key backups ([keys-and-secrets.md](keys-and-secrets.md#back-up-the-keys)), and the operator's copy of
exported audit checkpoint public keys.

## PostgreSQL: pgBackRest

Configuration: [`deploy/docker-compose/full/pgbackrest/`](../../deploy/docker-compose/full/pgbackrest/) — `pgbackrest.conf`
(repo1 on the S3 gateways over TLS, path-style, encryption, retention, asynchronous archiving, zstd, block incremental),
`entrypoint.sh` (repository secrets from Docker secrets into `PGBACKREST_REPO1_*`), `scheduler.sh` (the `pgbackrest`
service) and `restore-to-time.sh`. PostgreSQL runs with `archive_mode=on`, `archive_command=pgbackrest … archive-push %p`,
`archive_timeout=60s` (`compose.full.yaml`); the image is the official one plus pgBackRest from the PGDG repository
(`full/pgbackrest/postgres.Dockerfile`, built locally from the pinned digest).

```sh
cd deploy/docker-compose
./opportunity.sh --profile full backup status          # scheduler outcomes (last full/diff/incr/verify) and `pgbackrest info`
./opportunity.sh --profile full backup full            # also diff | incr: a backup now
./opportunity.sh --profile full backup verify          # `pgbackrest verify` + restore test of the newest backup
./opportunity.sh --profile full backup info json       # what `restore plan --pgbackrest-info` reads
```

- The scheduler creates the stanza on first start and checks the archive (`pgbackrest check`). It takes a backup when
  the newest one of a kind is older than `BACKUP_FULL_EVERY` (7 d), `BACKUP_DIFF_EVERY` (1 d) or `BACKUP_INCR_EVERY`
  (6 h), so a missed window is made up at once. Tighten the incremental interval when replaying the WAL since the last
  backup takes longer than 10 minutes (ADR-016 §2.2; the drill prints replay times).
- **Weekly verification** (`BACKUP_VERIFY_EVERY`): `pgbackrest verify` checks every backup and WAL file in the
  repository; then the newest backup is restored into a scratch directory, started on a private socket, queried (database
  count, schema version) and deleted. Outcomes are in `docker compose logs pgbackrest` and `backup status`.
- **Alerts:** `WalArchiveLagHigh` (> 4 min while writing) and `WalArchiveFailing` ([alerts.md](alerts.md#walarchivelaghigh))
  are fed by `postgres-exporter` (`pg_stat_archiver`).
- **Off-site (Q-40):** add a second repository in `pgbackrest.conf` (`repo2-type=s3|sftp|posix`, its own credentials and
  `repo2-cipher-pass`, `repo2-retention-full`); archive-push then writes both. Run `backup full` once afterwards.
- **After a failover** the promoted replica archives with the same command; run `backup full` on the new primary.

## OpenSearch: snapshots with catch-up anchors

The dispatcher runs `SearchSnapshotService` every `Backup__SearchSnapshots__Interval` (1 h): (1) it records the snapshot
and, per workspace, `G_s` = the visible watermark plus the placement in `opportunity.search_snapshot(_anchor)` and
**commits**; (2) it snapshots `opp-*` without the global state and waits; (3) it records the outcome; then it deletes
snapshots outside retention together with their anchors. Because the anchors are committed before the snapshot starts,
every snapshot a restore may use (ending at least 60 s before the recovery target) still has them after a PITR.

The nodes reach the bucket with the S3 identity `opportunity-snapshots` (keystore, `full/opensearch/s3-snapshot-entrypoint.sh`);
the image adds the `repository-s3` plugin (`full/opensearch/opensearch.Dockerfile`). The dispatcher's OpenSearch user may
register the repository and create, list and delete snapshots; restoring is the operator's (`admin`).

```sh
./opportunity.sh --profile full ops backup search-snapshot     # a snapshot now (anchors first)
./opportunity.sh --profile full ops backup search-snapshots    # repository listing with the anchor records
./opportunity.sh --profile full ops backup prune               # apply retention now
```

`ops` runs the CLI in a one-off container of the import worker (its database login, object storage and key store) with
the audit sealer login, the OpenSearch `admin` user and the snapshot repository settings added.

## Object storage: versions and replica

The migrator turns versioning on for the artifact bucket (`ObjectStorage__S3__Versioning=true`) and installs a lifecycle
rule (noncurrent versions expire after `NoncurrentVersionDays` = 35, incomplete uploads after a day). The `IObjectStore`
contract suite passes unchanged against a versioned SeaweedFS bucket, including the write-once `If-None-Match` race
(`VersionedS3ObjectStoreContractTests`). Correctness never depends on versions (ADR-011 §3.4); they are a backup only,
and noncurrent versions left by a workspace deletion are backup residuals the destruction certificate already covers.

- **One deleted or damaged object:** `./opportunity.sh --profile full ops restore object --key
  ws/<ws>/docs/<doc>/native/<sha256> --list` shows versions and delete markers; `ops restore object --key …` removes the
  delete marker that hides it (or `--version <id>` copies that version back).
  Exit 1: no version exists — try the replica, otherwise re-import the document from its source volume.
- **Lifecycle check:** whether a server enforces `NoncurrentVersionExpiration` depends on it. After the first 35 days,
  list an old deleted object's versions; if they are still there, expire them with the server's own tool (SeaweedFS:
  `weed shell` `s3.bucket.*`/`fs.*` commands, or a TTL on the collection) and record it here.
- **Replica (ADR-016 §4.3):** `COMPOSE_PROFILES=full,replica` with `REPLICA_FILER=<host:port>` in `full/.env` runs
  `weed filer.sync` (active → passive) of `/buckets/opportunity` to a second SeaweedFS cluster that trusts the same CA
  for gRPC. Its lag is the object RPO after site loss; pass its sync point to `restore plan --object-sync-point`.

## Restore procedure

Decide the recovery target `T_r` first; every later step depends on it. Run the steps in order — each gate must pass
before the next step. `restore …` below means `./opportunity.sh --profile full ops restore …` (PostgreSQL, the sealer
login, OpenSearch as `admin`, the snapshot repository, keys and object storage; the object gates and the signed record
need object storage). Only PostgreSQL, OpenSearch and SeaweedFS run during R1–R6; start them with `./opportunity.sh
--profile full compose up -d postgres opensearch-1 opensearch-2 opensearch-3 seaweedfs-s3 data-lb` as needed.

| Phase | Command | Gate |
|---|---|---|
| R0 stop | `./opportunity.sh --profile full restore-pg …` stops web, api, every worker, the scheduler, the replica, RabbitMQ and PostgreSQL first (or `compose stop` them yourself) | no application connection in `pg_stat_activity` |
| R1 plan | `restore plan --pgbackrest-info info.json (--target-time <iso> \| --end-of-wal) --decided-at <incident> [--object-sync-point <iso>] [--key-backup-at <iso>] [--keep-live-search]` | `verdict ACCEPTED`; it prints the path, the backup set, the snapshot, the loss window and the next command |
| R2 keys | restore the `keys` volume if it was lost (keys-and-secrets.md) | `keys status` lists the installation KEK |
| R3 PostgreSQL | `./opportunity.sh --profile full restore-pg <T_r \| latest> [--set <label>]` (`restore-to-time.sh`: `--delta`, `--type=time`, promote) | the server promoted; then run the migrator; re-clone the replica (new timeline) |
| R4 OpenSearch | path A: `restore search-snapshot --snapshot <name>` (deletes the `opp-*` indices, restores the snapshot's with aliases); path C: delete the `opp-*` indices; path 0: keep the cluster | the command returns once every restored index is readable |
| R5 objects | keep the live store; after site loss point `ObjectStorage__S3__ServiceUrl` at the replica | inventory gate in R6 |
| R6 reconcile | `restore reconcile --restore-id <id> --path end-of-wal\|snapshot\|rebuild [--snapshot <name>] --target-time <T_r> --backup-label <label> --operator <name>` | per workspace: work above `G_s` re-pended, watermark lowered to `G_s`, in-flight work redriven, break-glass ended, search sessions dropped, open reindexes cancelled; every session revoked (`Restore`); purged workspaces purged from search again; the lost-window report |
| R6 record | `restore record --restore-id <id>` | `Integrity.Restored` in the system chain and every workspace chain (restore id, `T_r`, backup label, snapshot, restored head), `AfterRestore` checkpoints, signed record `sys/restores/<id>/<sha256>` |
| R6 verify | `restore verify --restore-id <id> [--full \| --sample 10000] [--acknowledge-lost-window] [--recover-missing-objects]` | exit 0 = every pre-open gate passed (table below) |
| R7 reopen | `./opportunity.sh --profile full up` (migrator → dispatcher → workers → api → web) | `/health/ready`; freshness *Updating* → *Current*; `jobs reindex` for each `reindex-after-reopen` workspace |
| R8 converge | `restore verify --restore-id <id> --converge` once the watermarks caught up | 0 ahead, 0 stale, 0 missing, no failed work |

`reconcile` is resumable until `record` and refused afterwards (writers may run by then). Path B (keep a live cluster
after an earlier target behind an exhaustive ahead purge) is not automated: `restore plan` refuses it — use A or C.

### Pre-open gates (`restore verify`)

| Gate | Passes when |
|---|---|
| `restore-record` | `restore record` ran (signed record stored, or database-only without an object store) |
| `audit-chains` | every chain verifies (hashes, links, Merkle roots, ES256) and has an `AfterRestore` checkpoint taken after the restore started |
| `keys` | every live workspace data key unwraps with the restored KEKs; every checkpoint key id has a public key |
| `work-state` | no Claimed/Dispatched/Running outbox row, index task or job chunk; no dispatcher claim |
| `watermark` | (paths A, C) every watermark ≤ its anchor and every record above it is Pending |
| `security-reset` | no live session, no open break-glass activation, no search session |
| `lost-window` | the report is empty, or acknowledged with `--acknowledge-lost-window` after review |
| `search-placements` | every placement resolves to an existing index and alias (or is marked for reindex) |
| `search-not-ahead` | for every workspace (all documents with `--full`, otherwise evenly spread pages of 1,000 up to `--sample`): no document indexed above its PostgreSQL version, indexed while deleted, or indexed without a row. **Any ahead document stops the restore**: the snapshot choice was wrong; use an older snapshot or path C |
| `objects` | every `stored_object` created since `T_r − 24 h` and a sample of older ones `HEAD`s with its recorded size (missing ones recovered from versions with `--recover-missing-objects`); orphans are reported, never deleted |

### The lost-window report

Work between `T_r` and the incident that left evidence outside PostgreSQL (design §6):

- `DeletionCertified` — a destruction certificate exists for a deletion the restored database did not complete. The
  tool closes the workspace; re-run its deletion (the certificate is the approval of record). Never reopen it.
- `ProductionOutput` — a production volume the database does not know: its `MANIFEST.json` Bates numbers may already
  be delivered. Record the ranges before finalizing another production in that workspace (the tool reports it; the
  finalization block is a follow-up, see below).
- `ExportOutput` — export output the database does not know; kept until reviewed.
- Unwrappable data keys (`keys` gate) — a KEK destroyed after `T_r`: treat like a certificate; the workspace stays closed.
- Holds, role and wall changes, privilege calls and coding in the window exist nowhere else. **Product-owner decision
  pending (design §6.4):** tell each Workspace Admin the window and ask them to re-check legal holds first.

## Lite (evaluation, no RPO/RTO)

Backup: `docker compose exec postgres pg_dump -Fc -U opportunity_owner opportunity > opportunity.dump`, then copy the
`objects` and `keys` volumes (in that order, ADR-016 §4.4). Restore: restore the dump into a fresh volume, put back the
two volumes, delete the OpenSearch volume, `./opportunity.sh up`, then `restore reconcile --path rebuild …`, `restore
record`, and `jobs reindex --workspace <ws>` for each workspace (path C).

## Drills

Automated (Testcontainers, `tests/Opportunity.IntegrationTests/Backup`):

| Drill | What it proves |
|---|---|
| `RestoreConsistencyTests` (every CI run) | snapshot job with anchors → work continues → OpenSearch restored from the older snapshot (behind, never ahead) → `restore plan` accepts the snapshot and refuses one ending too late → `reconcile` re-pends exactly the records above the anchor and lowers the watermark → `record` (3 chains, `AfterRestore`, signed record verifies) → every pre-open gate passes → reopen → `verify --converge`: 0 ahead / 0 stale / 0 missing; a database behind the index fails `search-not-ahead` |
| `PgBackRestDrillTests` (`OPPORTUNITY_BACKUP_DRILL=1`) | the Full `pgbackrest.conf` and `restore-to-time.sh` with a local repository: full backup, RPO wait of a quiet commit (56 s with `archive_timeout = 60s`), host loss → restore to the end of the archive (nothing archived lost), PITR to a timestamp (rows after it gone, before it present, new timeline), `pgbackrest verify` |
| `ObjectVersionRecoveryTests`, `VersionedS3ObjectStoreContractTests` | a deleted native is recovered from its version with the same bytes and hash; the contract suite passes on a versioned bucket |

Measured on a shared 4-vCPU developer VM (20,000 rows, 30 MB): full backup 2.1 s, restore of a lost data directory to the
end of the archive 3.1 s, PITR to a timestamp 2.7 s. **These are not the acceptance drills.**

**Operator drills still to run on Full hardware** (not runnable in the development sandbox: one host, ~7 GB free disk,
no PGDG/OpenSearch plugin mirrors, OpenSearch multi-node bootstrap checks fail):

1. PITR drill at 1M documents: restore time and WAL replay rate recorded here; 10M projected as `t_PG = S_PG / r_PG +
   t_replay` (ADR-016 §9) from the measured rates.
2. Snapshot path at 1M: restore the newest snapshot, catch up, `restore verify --converge`; record time to *Current*.
3. Full DR drill (all stores, ≤ 1 h at 1M): R0–R8 above with a stopwatch per phase; record RTO = decision → reviewers
   can sign in, search, view and code.
4. Object replica: `--profile replica` to a second cluster, kill the primary store, point the services at the replica.

Record each run below (date, version, hardware, sizes, times, deviations).

| Date | Version | Drill | Result |
|---|---|---|---|
| 2026-10-11 | E19-T08 branch | `RestoreConsistencyTests`, `PgBackRestDrillTests`, versioned contract suite (developer VM) | passed; numbers above |

## Exit codes

`restore …` / `backup …`: 0 success, 1 refused (plan refused, a gate failed, no version to recover), 2 usage, 3 failed
(an error the message explains).
