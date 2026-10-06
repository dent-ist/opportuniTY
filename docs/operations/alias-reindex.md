# Runbook: alias reindex (rebuild a workspace's search projection)

**Applies to:** a projection mapping change, a shard re-size, a shared → dedicated move, or a disaster-recovery rebuild of
a workspace's OpenSearch index. **Binding design:** ADR-006 R13 (protocol), ADR-001 §7.5 (dual writes, generation and
watermark across the switch). Reads always go through the workspace's stable alias, so a reindex never changes what
the API addresses.

> **Status (2026-10-06):** implemented by E07-T11 (#73). Start a reindex with the API
> (`POST /api/v1/workspaces/<ws>/search-index/reindexes`, `Job.Manage`, Idempotency-Key) or the operations CLI
> (`jobs reindex`); the reindex coordinator in the indexing worker drives it. Follow it in the job monitor or with
> `GET …/search-index` (`Job.ViewAll`) / `jobs reindex-status`.

## What the reindex job does

| Phase | What happens | Abort |
|---|---|---|
| `Pending` | Target index created (`refresh_interval -1`, no replicas); placement `Building`/`Moving`; every writer dual-targets | — |
| `Building` | Waits `Search:Reindex:WriterSettleDelay` (35 s: placement cache TTL + `MaxReadToWriteAge`) so no write can still come from a placement cached before the dual-target start | cancel the job |
| `Backfilling` | Job chunks = DocumentId key ranges (2,000 documents); each commit creates one `Reindex` IndexChunkTask (no search generation, so the watermark is untouched) that the bulk lane writes to the **target only**; at most `TaskWindow` (8) un-applied at once | cancel the job |
| `Validating` | Every document (≤ 1M; sampled pages + exact count above) compared with PostgreSQL by version; disagreeing ones re-checked once the applied watermark covers them; projection rebuilt from PostgreSQL compared with the stored source | automatic on failure: job `Failed`, old alias keeps serving |
| `Switching` | Target refreshed, replicas restored, one `_aliases` request (or the placement row for a move); job `Completed` | — |
| `Switched` → `Retaining` → `Completed` | After the settle delay the old dedicated index is write-blocked; after `Retention` (24 h) it is deleted (a shared index loses the workspace's documents) | — |
| `Aborting` → `Aborted` | Placement back to its current location, target dropped, dropped once more after the settle delay | — |

Every step is idempotent and saved in `search_reindex` under a coordinator lease, so a crashed or restarted worker (or
another replica once the lease expired) resumes where the run stood. A dedicated rebuild of the same mapping generation
gets a new physical index revision (`<alias>-r<n>-g<G>`); a shared workspace is rebuilt into a new generation or moved
to a dedicated index. Failed reindex tasks keep the run in `Backfilling` until they are replayed (`jobs replay`) or the
job is cancelled.

## Check the current placement

1. API: `GET /api/v1/workspaces/<ws>` → `searchPlacement { kind, projectionGeneration, state }` (`state` = `active`,
   `building`, `moving`, `deleting`).
2. PostgreSQL (runtime login): `BEGIN; SET LOCAL app.workspace_id = '<ws>'; SELECT kind, generation, state,
   pending_kind, pending_generation FROM opportunity.workspace_index_placement; COMMIT;`
3. OpenSearch (dedicated placements; a shared workspace lives in a shared index, filtered by `workspaceId` and routed):
   `GET _cat/aliases/*<ws>*?v` and `GET _cat/indices/<prefix>-*?v&h=index,health,docs.count` — exactly one
   index behind the read alias; a second target only while `building`/`moving`.

## Procedure

1. Confirm the cluster is green and has room for a second copy of the workspace (`_cat/allocation`).
2. Start the reindex job for the workspace: `jobs reindex --workspace <ws> --operator <name> [--placement dedicated]
   [--generation <n>] [--shards <n>]` or the API. Placement becomes `Building` (`Moving` for a move); writes go to both
   targets from now on.
3. Follow it like any job: `jobs show --workspace <ws> --job <job>`, or the job monitor. Its index tasks carry no search
   generation, so the workspace's watermark keeps advancing for ordinary work during the rebuild.
4. Validation (R13 step 5) compares every document's version with PostgreSQL and the stored projection with one
   rebuilt from PostgreSQL (sampled above 1M documents). A failed validation fails the job and keeps the old alias
   serving; `jobs reindex-status` shows what disagreed; investigate, retry. Golden query parity is not automated.
5. The alias switch is one `_aliases` request. The old generation stays read-only for 24 h (or the maximum point-in-time
   age), then is deleted.
6. Failed reindex tasks are replayed like any other ([replay-failed-work.md](replay-failed-work.md)).

## Verify

- Placement `state` back to `active` with the new `projectionGeneration`.
- `_count` with `{"query": {"term": {"workspaceId": "<ws>"}}}` on the alias equals the workspace's live document count in
  PostgreSQL (`SELECT count(*) FROM opportunity.document_projection_state WHERE NOT is_deleted`).
- A saved or ad-hoc search returns the same hits as before (spot check).

## Small repairs without a reindex

- **A few documents are stale or missing in search:** replay the failed index tasks / outbox rows of the affected jobs
  ([replay-failed-work.md](replay-failed-work.md)). The workers rebuild each document from current PostgreSQL state; a
  full reindex is only needed when much of the index is damaged, the mapping generation changes or the workspace moves.
- **The index is lost or unusable:** restore OpenSearch from its snapshot repository if one is configured and replay, or
  run a reindex (above), which rebuilds the index from PostgreSQL.
