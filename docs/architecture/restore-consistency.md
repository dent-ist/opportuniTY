# Restore-consistency design

**Binding decision:** [ADR-016](../adr/0016-backup-dr-and-restore-consistency.md) (E19-T07, #163). **Implemented by:**
`E19-T08` (#164): snapshot job, anchor table, `restore` tool, drills. **Builds on:** ADR-001 (versions, generation,
watermark), ADR-010 (leases, redrive), ADR-011 (write-once objects, registry), ADR-013 (hash chain), ADR-014
(deletion).

This document says how each store is brought back after a restore so that **OpenSearch never ends ahead of PostgreSQL
truth**, and how that claim is checked. Nothing here adds an OpenSearch write path. Recovery only changes rows in
PostgreSQL and lets the existing dispatcher and index workers rebuild documents from PostgreSQL with external versions.

## 1. Terms

| Term | Meaning |
|---|---|
| `T_r` | PostgreSQL recovery target (commit timestamp). PostgreSQL holds exactly the transactions committed at or before it |
| `S` | The OpenSearch snapshot used, with the repository's `start_time`/`end_time` and state `SUCCESS` |
| `ε` | Clock-skew allowance between OpenSearch and PostgreSQL hosts: 60 s (NTP required) |
| `G_s(ws)` | **Anchor**: the workspace's `workspace_search_watermark.indexed_through_generation`, committed to PostgreSQL *before* `S` started (§3) |
| `v_PG(d)` | `document_projection_state.document_version` of document *d* in the restored database |
| `v_OS(d)` | `_version` (= `projectionVersion`, ADR-001 §2) of *d* in the restored index, or "absent" |
| Ahead | `v_OS(d) > v_PG(d)`, or *d* indexed while PostgreSQL has no live row for it. This is corruption |
| Behind | `v_OS(d) < v_PG(d)` or absent while live. This is normal lag: the watermark shows it and catch-up removes it |

## 2. Why "ahead" must never happen

`ProjectionIndexWriter` sends `index`/`delete` with `version_type=external` and treats `409` as `StaleNoOp`. Suppose a
document holds `v_OS = 7` with content from a transaction that the restore removed, and the restored database has
`v_PG = 5`. The next edit makes PostgreSQL issue version 6 and then 7 again. Both writes are rejected as stale, the
work rows become `Applied`, and the index keeps content that no longer exists anywhere. That includes coding values,
security tags (`securityTags`) and privilege status. Nothing re-detects it later: the watermark advances normally. A
restore therefore makes "ahead" impossible **by construction** (§4) and also **checks** it (§7).

## 3. Snapshot anchor (E19-T08)

The hourly snapshot job (dispatcher host, or `backup search-snapshot` in the operations CLI) runs these steps:

1. In one installation-level transaction it inserts `search_snapshot(snapshot_id, snapshot_name, requested_at)`, plus
   one `search_snapshot_anchor(workspace_id, snapshot_id, indexed_through_generation, placement_state,
   placement_generation)` row per workspace, read from `workspace_search_watermark` and `workspace_index_placement`.
   Then it commits. (Schema: new migration in `E19-T08`. The anchor table is a tenant table, so it needs RLS and an
   entry in `PreservationLockEnforcementTests`. Rows expire with the snapshot.)
2. It calls `PUT _snapshot/<repo>/<snapshot_name>` for `<prefix>-*`, `include_global_state: false`, and waits for the
   result.
3. It records the outcome (`SUCCESS`, `start_time`, `end_time`) on the `search_snapshot` row. If the database is later
   restored to a time before this step, the repository stays the authority on the outcome.

**Why `G_s` is safe.** The watermark is raised only after `POST /{index}/_refresh` succeeded on every shard copy, and
only up to `A(t₀) = min(non-applied generation) − 1` (ADR-001 §7.2–§7.3, `SearchWatermarkAdvancer`). So every work
record with `search_generation ≤ G_s` was applied, or coalesced by a newer applied write of the same document, before
step 1 committed. That is before `S` started. A shard snapshot flushes the shard first, so every applied operation
is in `S`. `G_s` can only be too *low* (it was read a little earlier), and that just means more replay.

**Placements that were being rebuilt or moved at capture time** (`placement_state` `Building`/`Moving`) are excluded
from catch-up and rebuilt (§4.5). During a generation switch the older index is not written (ADR-001 §7, "Implementation
E07-T11"), so its watermark over-states that index.

## 4. Restore procedure (phases of ADR-016 §8)

### 4.1 R0–R1: stop everything, choose `T_r` and the search path

Stop the API, every worker type, the dispatcher and the broker (ADR-016 §1.4). `pg_stat_activity` must show no
application login before R3. Then the tool reads four things: the archive's recoverable range (`pgbackrest info`), the
snapshot list (`GET _snapshot/<repo>/_all`), the replica's sync point if the primary object store is lost, and the
key backups. It proposes the newest `T_r` that satisfies ADR-016 §1.2 and picks the path:

| Path | When | OpenSearch handling | Cost |
|---|---|---|---|
| **0 — end of WAL** | Nothing is lost: crash, failover, host replaced with the archive intact | Keep the live cluster; redrive only (§4.4) | minutes |
| **A — snapshot + catch-up** (default) | Some `S` has `end_time + ε ≤ T_r` and `T_r − start_time ≤ 24 h` | Restore `S`, then replay above `G_s` (§4.3) | `S_OS / r_OS` + replay |
| **B — keep live + ahead purge** | The live cluster survived, no snapshot qualifies, and a rebuild would miss the RTO | Exhaustive ahead-scan, unconditional delete of ahead documents, wait `gc_deletes`, then replay (§5) | one full scan + 10 min |
| **C — rebuild** | No usable cluster or snapshot; Lite; or per workspace when §4.5 requires it | Empty cluster, `jobs reindex` per workspace | `N / R` (ADR-016 §9) |

The tool refuses path A with a snapshot that violates `end_time + ε ≤ T_r`. It refuses path 0 unless recovery
reached the end of the archived WAL.

### 4.2 R2–R5: restore the stores

- **Keys (R2)** come first, because R6 needs them (restore record signature, `keys status`).
- **PostgreSQL (R3):** `pgbackrest restore --type=time --target=<T_r> --target-action=promote` (add `--delta` when the
  data directory survives). Then run the **migrator** of the image version that runs next. The migrator also redeclares
  the RabbitMQ topology and the index templates.
- **OpenSearch (R4, parallel with R3):** close or delete the existing `<prefix>-*` indices, then
  `POST _snapshot/<repo>/<S>/_restore` with `indices: <prefix>-*`, `include_aliases: true`, `include_global_state:
  false`. Replicas may recover after reopening (yellow is searchable).
- **Objects (R5):** keep the live store, or point `ObjectStorage:S3` at the replica (configuration only, ADR-020). Do not
  roll it back.

### 4.3 R6a: search catch-up (path A)

All statements run in one transaction per workspace, as the schema owner, before any application process starts.
`G_s` = the anchor of `S` for the workspace, or `0` if the workspace has no anchor row because it was created after
`S` started.

```sql
-- 1. Re-pend every interactive work record above the anchor (Applied, Failed and in-flight alike).
UPDATE opportunity.search_outbox
   SET status = 1, claim_owner = NULL, claim_expires_at = NULL, dispatched_at = NULL, applied_at = NULL,
       attempt_count = 0, available_at = now(), last_error = NULL, updated_at = now()
 WHERE workspace_id = @ws AND search_generation > @g_s;

-- 2. The same for chunk index tasks. Reindex tasks (task_kind 4) carry no generation and are handled by 4.5.
UPDATE opportunity.index_chunk_task
   SET status = 1, claim_owner = NULL, claim_expires_at = NULL, lease_owner = NULL, lease_expires_at = NULL,
       dispatched_at = NULL, started_at = NULL, completed_at = NULL, attempt_count = 0, available_at = now(),
       last_error = NULL, error_class = NULL, updated_at = now()
 WHERE workspace_id = @ws AND search_generation > @g_s;

-- 3. Lower the visible watermark to what the restored index really reflects (ADR-016 B-4; the only place it moves back).
UPDATE opportunity.workspace_search_watermark
   SET indexed_through_generation = LEAST(indexed_through_generation, @g_s), refreshed_at = now(), updated_at = now()
 WHERE workspace_id = @ws;
```

Membership needed by the replay is in PostgreSQL (`import_batch_member`, `document_set_snapshot_page`, `document_ids`).
A re-pended task therefore resolves exactly the documents of its chunk. The lease fence keeps working: the claim
statement increments `lease_token` from its restored value, and no process from the old timeline is alive (R0).

Coverage check before commit: the oldest retained partition of `search_outbox` and `index_chunk_task` must start
before the first generation above `G_s` was committed. `SearchWorkHousekeepingOptions.Retention` (3 days) against the 24 h
snapshot age bound guarantees this. If it fails, the workspace falls back to path C.

### 4.4 R6b: redrive, security reset, deletion re-fence

1. **Redrive** (ADR-016 §6.2). Return every `job_chunk` and `index_chunk_task` in `Dispatched`/`Running`, and every
   `search_outbox` row in `Claimed`/`Dispatched`, to `Pending`, with claims and leases cleared. These are the statements
   of `SearchWorkMaintenance.RecoverAsync` and the chunk lease sweeper, without the age condition. Jobs keep their
   status. A `Running` job continues with its pending chunks. A `Cancelling`/`Paused` job behaves as it did before.
   The broker is empty.
2. **Security reset** (ADR-016 §7). `UPDATE opportunity.user_session SET revoked_at = now(), revoked_reason =
   'Restore' WHERE revoked_at IS NULL`. End every open `break_glass_activation` with `ended_reason = 'Revoked'`. Delete
   `search_cursor`/`search_session` rows.
3. **Workspaces being deleted.** A workspace in `Deleting` resumes its run on its own: the purge is resumable and each
   step is idempotent. A workspace in `Purged` has nothing left in PostgreSQL, but `S` can hold its documents if the
   purge finished after `S` started. The tool runs the deletion coordinator's search purge for every `Purged`
   workspace (delete by `workspaceId` in its shared index, drop a dedicated index). It then checks that the count is 0.
4. **Deletions certified after `T_r`** (lost-window report, §6): for each certificate in `sys/certificates/` whose
   `deletionId` is not `Completed` in the restored database, the workspace is fenced (`Deleting`) at once. The
   operator re-runs the deletion; the certificate is the approval of record. The workspace does not reopen.

### 4.5 R6c: placements and reindex runs

For each `workspace_index_placement`:

| Situation at `T_r` | Action |
|---|---|
| `Active`, its physical index is in `S`, anchor `placement_state` was `Active` | none (catch-up covers it) |
| `Active`, but its physical index or alias is not in `S` (switched, moved or created after `S`) | create the index from the template, mark the workspace for **path C**: one `jobs reindex` right after reopening (search reads *Updating* until it switches) |
| `Building`/`Moving` (a `search_reindex` run not terminal) | set the run to abort, so the coordinator drops the target and returns the placement to its current location. Then the action of the row above. Re-request the reindex after reopening |
| Anchor `placement_state` was `Building`/`Moving` | path C for that workspace |

A shared index restored from `S` holds the workspaces of its pool. Path C for one of them is the usual move/rebuild
of that workspace. It never touches the other workspaces in the pool.

### 4.6 R6d: audit restore record

The tool writes `Integrity.Restored` (proposed taxonomy addition) through `IAuditEventWriter` into the system chain and
every workspace chain. The details are the restore id, `T_r`, the pgBackRest backup label, the snapshot name, and the
restored head `(sequence, event_hash)` of that chain, read from `audit.chain_head`. The sealer (or `audit seal`) seals
these events, and a checkpoint with reason `AfterRestore` signs them. The tool stores the canonical restore record,
signed with the `audit-checkpoint` key like the destruction certificate, under `sys/restores/{restoreId}/{sha256}`.
`audit verify` must then pass on every chain.

### 4.7 R7–R8: reopen and converge

Start the migrator (already done in R3; it is idempotent), then the dispatcher, then the workers, then the API. The
dispatcher publishes the re-pended rows. The interactive and security lanes keep their reserved capacity, so new
reviewer edits are not queued behind the catch-up. The watermark ticker raises each workspace's watermark as its work
applies. The freshness API shows `updating`/`delayed` until then. Convergence is checked by §7.3.

## 5. Path B: keeping a live cluster after an earlier `T_r`

Use it only when no snapshot qualifies and a rebuild cannot meet the RTO. All writers stay stopped until step 4.

1. **Exhaustive ahead-scan** per workspace, in keyset pages of 1,000 (the `ReindexValidator` access pattern). For each
   page, read one PostgreSQL snapshot of `document_projection_state` and range-scan the index's `documentId`s with
   versions. Collect *ahead* documents: `v_OS > v_PG`, indexed without a PostgreSQL row, or `is_deleted` in PostgreSQL
   while indexed with `v_OS ≥ v_PG`.
2. **Unconditional delete** of each ahead document from every index of the placement (no version). This is safe for the
   same reason as ADR-001 §4 R1: `DocumentId`s are never reused.
3. **Wait `index.gc_deletes` (10 min) plus `ε`.** An unconditional delete leaves a tombstone with an internal version
   above `v_OS`. Until it is pruned, the external writes at `v_PG` would get `409`, count as applied, and leave the
   document missing.
4. Insert one `Repair` `index_chunk_task` (`task_kind = 5`, `ExplicitIds` ≤ 1,000 per task) per batch of ahead
   documents. Each insert takes the normal generation increment as its last statement, so the watermark stays below
   the task until it applies. No other replay and no watermark reset is needed. The surviving cluster only moved
   forward, so every record that is `Applied` in the restored database is reflected in it (or the document is ahead
   and was just repaired). `Pending` work is redriven by §4.4. Then reopen.

The scan cost is the reconciliation rate (≈ 8,600 docs/s per reader measured on a developer VM, `fault-injection.md`
§3). Readers run in parallel by key range. Path B is therefore realistic up to roughly 10M documents. Above that,
path A is the only option within the RTO.

## 6. Lost-window report

Between `T_r` and the incident, work happened that PostgreSQL no longer knows about. Some of it left evidence outside
PostgreSQL. The tool lists that evidence, and the operator acknowledges the report before R7.

| Evidence outside PostgreSQL | Meaning | Handling |
|---|---|---|
| `sys/certificates/{deletionId}/…` with no `Completed` deletion in the database | A workspace was destroyed after `T_r` | Fence the workspace; it does not reopen; re-run the deletion (§4.4) |
| `ws/{ws}/productions/{productionId}/…` or `…/{runId}/` unknown to `production`/volume rows, with `MANIFEST.json` | A production ran or was finalized after `T_r`; its Bates numbers may already be delivered | Block finalization in the workspace until an operator records the manifest's Bates ranges (`bates_range` must not reissue them) |
| `ws/{ws}/exports/{exportId}/…` unknown to `export` | Export output after `T_r` | Report. The reconciler keeps it until reviewed |
| Other objects without a `stored_object` row | Uploads, imports and renders after `T_r` | Report. ADR-011 §2.4 reconciler deletes them after 24 h |
| KEK versions destroyed in the key provider while the database has their data keys `Active` | Crypto-shred after `T_r` | Treat as the certificate case: the workspace stays fenced |
| Signed checkpoints or audit exports that the operator holds (case files) | Audit events after `T_r` | Explained by the restore record (§4.6); attach the record to the case file |
| Nothing (only in PostgreSQL) | Holds placed or released, role/wall/restriction changes, coding and privilege calls, acknowledgments, in the window | **§6.4** |

### 6.4 Decision requested from the product owner

Changes that live only in PostgreSQL cannot be recovered for the loss window (≤ 5 min under the Full RPO, or longer
after site loss). The proposal: the lost-window report states the window per workspace. Each Workspace Admin gets
an in-product notice naming the window, and is asked to re-check legal holds, security changes and privilege
decisions. The platform does not attempt automatic re-application. **A legal hold placed in the window is the
highest-risk item:** the notice must say so explicitly.

## 7. Verification after restore

### 7.1 Before reopening (gates)

The table in ADR-016 §8 lists the gates. The search-specific ones:

1. **Snapshot rule** (enforced in R1): `S.end_time + ε ≤ T_r`.
2. **Placement check:** every placement resolves through `IIndexManager` to an existing alias and physical index, or
   is marked for path C.
3. **Sampled ahead-check:** per workspace, 10,000 random live documents, plus every document named by a work record
   above `G_s` (ExplicitIds, outbox `document_id`; import and bulk tasks resolved through their membership). For
   each, `_mget` the index *first*, then read `document_projection_state` in one REPEATABLE READ snapshot. PostgreSQL
   can only be the same or newer between the two reads; with writers stopped, it cannot change at all. Then assert
   `v_OS ≤ v_PG`, and that no document is indexed without a live PostgreSQL row unless a re-pended delete covers it.
   **Any ahead document stops the restore.** It means the snapshot choice was wrong. The fix is path B or C, never
   reopening.
4. **Watermark check:** for every workspace, `indexed_through_generation ≤ G_s` and every record above it is
   `Pending`.

### 7.2 Objects

- Inventory: `HeadAsync` must return the recorded `size_bytes` for 100% of `stored_object` rows created in
  `[T_r − 24 h, T_r]`, and for a random sample (≥ 10,000 or 0.1%) of the rest. A missing object is restored from its
  noncurrent version or the replica. If that fails, it is reported as data loss for the document (re-import from the
  source volume).
- The full pass runs after reopening as the ADR-011 §2.5 scrub (`VerifyingReadStream` checks SHA-256).

### 7.3 Convergence (after reopening)

Wait until every workspace's watermark equals its `workspace_search_generation.value` with nothing `Failed`. Then run
the full reconciliation per workspace. It is the `ReindexValidator` comparison against the live alias instead of a
rebuild target: every document's version, plus the projection hash on the sampled pages. It must report **0 ahead,
0 stale, 0 missing, 0 unexpected**. In drills, the shadow-ledger oracle (`opportunity-bench ledger`) provides the
independent verdict with `staleOverwrites = 0`. A stale or missing document left after convergence is replayed with a
`Repair` task and investigated as a defect.

## 8. Correctness argument

1. **Prefix.** A PITR to `T_r` yields the database state of the original history at `T_r`. Versions are bumped once per
   transaction that changes a projection input (ADR-001 §2), so for every document the versions `1…v_PG(d)` mean the
   same states in the original and the restored history.
2. **Snapshot ⊆ prefix.** Every document in `S` was written as `(v, P(v))`, read from committed PostgreSQL state
   before the write (read-consistency rule, ADR-001 §3). The write happened before `S.end_time ≤ T_r − ε`. Hence
   `v ≤ v_PG(d)`, and `P(v)` is the prefix's state at `v`: **`S` is never ahead.** Workspaces that were purged later
   are removed (§4.4.3).
3. **No old writers.** R0 stops every process and empties the broker. After reopening, every write is built from the
   restored database. Every version it carries is one the prefix issued or issues next, and OpenSearch holds no
   higher one (2.). So an accepted `409` can only mean "already at that version with that content".
4. **Catch-up is complete.** Take a document with `v_OS(d) < v_PG(d)`. The transaction that produced `v_PG(d)` wrote a
   work record (ADR-001 §1 R1) with some generation `g`. If `g ≤ G_s`, the record was applied before `S` started
   (§3), so `v_OS(d) ≥ v_PG(d)`, a contradiction. Hence `g > G_s`, and §4.3 re-pends it. Its execution reads the current
   state and writes `v_PG(d)` (or later). Deleted documents are covered the same way through `ChangeMask.Delete`.
5. **Freshness is honest.** The watermark is set to `≤ G_s`. It rises again only through the normal ticker, so the
   API never reports search as current before the re-pended work is applied and refreshed.

## 9. Lite

Lite has no RPO/RTO (Q-40) and holds no real data (Q-01). Backup: stop the stack, or keep it running and accept a
crash-consistent dump. Run `pg_dump -Fc` first, then copy the `objects` and `keys` volumes. Restore: restore the dump,
put back the volumes, delete the OpenSearch data volume, run `./opportunity.sh up`, apply §4.4 (redrive, security reset),
then `jobs reindex --workspace <ws>` for each workspace (path C). The same ordering rule holds: objects copied *after*
the dump.

## 10. What `E19-T08` builds

| Piece | Notes |
|---|---|
| pgBackRest configuration for Full, `restore-to-time` script, weekly `pgbackrest verify` plus a restore test | ADR-016 §2 |
| `backup search-snapshot` job and the anchor tables (migration) | §3 |
| `restore plan` (R1: lists `T_r` candidates, snapshot, path, loss window) | refuses invalid combinations |
| `restore reconcile` (R6: §4.3–§4.6, idempotent, audited as `service:ops-cli`) | per workspace, resumable |
| `restore verify` (§7.1 gates, §7.2 inventory, §7.3 convergence) | exit code 0 only when every gate passes |
| Taxonomy `Integrity.Restored`, checkpoint reason `AfterRestore`, `sys/restores/…` record | ADR-016 §5.3 |
| Versioning, lifecycle and `filer.sync` setup, plus a contract-suite run with versioning on | ADR-016 §4 |
| Runbook `docs/operations/backup-and-restore.md` and the drills of the `E19-T08` acceptance criteria | |
