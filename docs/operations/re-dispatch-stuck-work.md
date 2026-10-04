# Runbook: re-dispatch stuck work

**Applies to:** job chunks, IndexChunkTasks and SearchOutbox rows that stop moving (Pending, Dispatched or Running
without progress). **Owner:** operator on call. **Risk:** low — every step returns work to `Pending`, and duplicate
delivery is harmless by design (ADR-010 §5: the claim is the inbox; OpenSearch writes are versioned, ADR-001 §3).

## How work normally moves

1. A committing transaction writes the work row (`Pending`) in PostgreSQL.
2. The dispatcher (worker type `dispatcher`, part of the combined `worker` in compose) claims due rows, publishes them
   with publisher confirms and marks them `Dispatched` (ADR-001 §6.1). SearchOutbox rows also wake it by `NOTIFY`;
   chunks and index tasks are polled (500 ms).
3. A worker claims the row (`Running`, lease + fencing token), does the work, and settles it (`Committed`/`Applied`,
   `RetryWait` or `Failed`) before acking.
4. Recovery is automatic: every 30 s the dispatcher returns `Running` rows whose lease expired > 10 s ago and
   `Dispatched` rows not picked up within 60 s to `Pending` (ADR-010 §3.3, ADR-001 §6.3). A message that dead-letters
   after repeated consumer stops is recovered this way (Q-69).

So work that is *stuck* means one of those loops is not running, or a dependency is down.

## Diagnose

1. Which work and since when:
   ```sh
   docker compose run --rm --no-deps worker jobs backlog --workspace <ws>
   docker compose run --rm --no-deps worker jobs list --workspace <ws> --status running,paused,cancelling
   docker compose run --rm --no-deps worker jobs show --workspace <ws> --job <job>
   ```
   Or the job monitor: `GET /api/v1/workspaces/<ws>/jobs/<job>` (`chunks.pending`/`running`, `searchable`).
2. Is the dispatcher alive? `opportunity.worker.heartbeat.age{worker_type="dispatcher"}` (Grafana *Worker heartbeat
   age*), `docker compose ps worker`, `docker compose logs --since 10m worker | grep -i dispatch`.
3. Is the broker reachable and consumed? RabbitMQ management UI (`http://localhost:15672` in compose): the queue of
   the work (`index.interactive`, `index.security`, `index.bulk`, `index.security-bulk`, `import.chunks`,
   `bulkcoding.chunks`, `export.chunks`, …) — *Ready* growing with 0 consumers means the worker type is down;
   `opportunity.queue.consumers` shows the same.
4. A paused job does not dispatch (`status` = `paused`, `statusReason` names the circuit breaker, ADR-010 §7.5): fix the
   cause, then resume or cancel — this is not stuck work.
5. Index backpressure: a job with more than 50 un-applied index tasks (4 for security-affecting bulk work) waits for
   indexing before more chunks are dispatched (ADR-010 §6). Look at the index tasks first.

## Act

1. **Restore the loop that is missing**: restart the worker (`docker compose restart worker`) or the dependency
   (RabbitMQ, OpenSearch). Recovery then happens within one sweep (≤ 30 s + 60 s).
2. **Force one recovery pass now** (same statements the sweepers run, safe to repeat):
   ```sh
   docker compose run --rm --no-deps worker jobs redispatch --workspace <ws>
   ```
   It prints how many outbox rows, index tasks and chunks returned to `Pending`, and how many chunks failed because
   their attempts ran out (those then follow [replay-failed-work.md](replay-failed-work.md)).
3. If rows are `Failed`, re-dispatching does nothing: use [replay-failed-work.md](replay-failed-work.md).
4. Never shovel messages back from a `*.dlq` or `*.parking` queue: the PostgreSQL row is the source of truth.

## Verify

- `jobs backlog` counts fall; `jobs show` shows chunks moving and `searchable` reaching `current`.
- `opportunity.outbox.oldest_age` and `opportunity.index.chunk_task.oldest_age` drop below their thresholds.

## OutboxOldestAgeHigh

Alert: `max by (opportunity_lane) (opportunity_outbox_oldest_age_seconds) > 60` for 1 min — the oldest SearchOutbox row
of a lane (interactive edits, security changes) has not been dispatched for over a minute. Interactive edits are then
not searchable (Q-10: security changes must be searchable within 5 s).

1. Check the dispatcher and RabbitMQ (Diagnose 2–3). A down broker is the usual cause: the dispatcher keeps rows
   `Pending` and retries with backoff.
2. If the oldest rows are `Failed` (`jobs failures --workspace <ws>` without `--job` lists them), the age includes them:
   fix the cause and replay them ([replay-failed-work.md#searchoutbox-rows](replay-failed-work.md#searchoutbox-rows)).
3. Otherwise restart the worker and run `jobs redispatch` (Act 1–2). The alert clears when the lane's oldest row is
   dispatched.
