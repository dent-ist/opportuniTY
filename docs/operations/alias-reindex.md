# Runbook: alias reindex (rebuild a workspace's search projection)

**Applies to:** a projection mapping change, a shard re-size, a shared → dedicated move, or a disaster-recovery rebuild of
a workspace's OpenSearch index. **Binding design:** ADR-006 R13 (protocol), ADR-001 §7.5 (dual writes, generation and
watermark across the switch). Reads always go through the workspace's stable alias, so a reindex never changes what
the API addresses.

> **Status (2026-10-04):** the placement half of the protocol exists (`IIndexManager.BeginRebuildAsync`,
> `CompleteRebuildAsync`, `AbortRebuildAsync`, #63) and every index writer already dual-writes while a placement is
> `Building`/`Moving`. The **reindex job** that backfills with `IndexChunkTask(kind = Reindex)` and validates (R13 steps
> 3–5) is E07-T11 and is **not built yet**, so there is no supported trigger in the API or the operations CLI. Do not
> start a rebuild by hand: without the backfill the new index stays incomplete. Until E07-T11 lands, recover from
> index-side damage with the procedures below.

## Interim: repair without a reindex

- **Some documents are stale or missing in search:** replay the failed index tasks / outbox rows of the affected jobs
  ([replay-failed-work.md](replay-failed-work.md)). The workers rebuild each document from current PostgreSQL state.
- **The index is lost or unusable:** restore OpenSearch from its snapshot repository if one is configured, then replay;
  otherwise the workspace's search stays unavailable until E07-T11 provides the rebuild.

## Check the current placement

1. API: `GET /api/v1/workspaces/<ws>` → `searchPlacement { kind, projectionGeneration, state }` (`state` = `active`,
   `building`, `moving`, `deleting`).
2. PostgreSQL (runtime login): `BEGIN; SET LOCAL app.workspace_id = '<ws>'; SELECT kind, generation, state,
   pending_kind, pending_generation FROM opportunity.workspace_index_placement; COMMIT;`
3. OpenSearch (dedicated placements; a shared workspace lives in a shared index, filtered by `workspaceId` and routed):
   `GET _cat/aliases/*<ws>*?v` and `GET _cat/indices/<prefix>-*?v&h=index,health,docs.count` — exactly one
   index behind the read alias; a second target only while `building`/`moving`.

## Procedure (once E07-T11 provides the trigger)

1. Confirm the cluster is green and has room for a second copy of the workspace (`_cat/allocation`).
2. Start the reindex job for the workspace (target generation and/or placement). Placement becomes `Building`
   (`Moving` for a move); writes go to both targets from now on.
3. Follow it like any job: `jobs show --workspace <ws> --job <job>`, or the job monitor. Its index tasks carry no search
   generation, so the workspace's watermark keeps advancing for ordinary work during the rebuild.
4. Validation (R13 step 5) compares document counts with PostgreSQL, a projection hash over sampled ranges and golden
   query parity. A failed validation keeps the old alias serving; abort, investigate, retry.
5. The alias switch is one `_aliases` request. The old generation stays read-only for 24 h (or the maximum point-in-time
   age), then is deleted.
6. Failed reindex tasks are replayed like any other ([replay-failed-work.md](replay-failed-work.md)).

## Verify

- Placement `state` back to `active` with the new `projectionGeneration`.
- `_count` with `{"query": {"term": {"workspaceId": "<ws>"}}}` on the alias equals the workspace's live document count in
  PostgreSQL (`SELECT count(*) FROM opportunity.document_projection_state WHERE NOT is_deleted`).
- A saved or ad-hoc search returns the same hits as before (spot check).
