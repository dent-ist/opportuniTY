# Runbook: replay failed chunks, index tasks and SearchOutbox rows

**Applies to:** job chunks, IndexChunkTasks and SearchOutbox rows in `Failed` (permanent errors, or attempts used up);
jobs that ended *completed with errors*; dead-letter queue depth > 0. **Permission:** `Job.Replay` (Workspace Admin by
default) in the API; the operations CLI needs the runtime database login. **Audit:** every replay that changes something
writes `Job.Replayed` (actor, counts) in the same transaction as the reset.

## Principle (ADR-010 §7)

PostgreSQL is the retry and failure ledger. Replay = reset the row: `Failed → Pending`, attempts set to 0, replay count
+ 1; the dispatcher publishes it again within a second. Workers re-read **current** PostgreSQL state when they run
(ADR-010 §9, ADR-001 §3), so a replayed index task indexes the documents as they are now, not as they were when it
failed. A second replay of the same failures finds nothing `Failed` and changes nothing. Messages in `*.dlq` queues
are copies for diagnosis; they are never re-published.

What replays:

| Row | Replayed when | Effect on the job |
|---|---|---|
| Job chunk | job is `running`, `paused` or `completedWithErrors` | `completedWithErrors` returns to `running` and completes again when the chunks commit |
| IndexChunkTask | always (also for completed, failed or cancelled jobs) | none: the job's PostgreSQL phase is over; its *searchable* progress catches up |
| SearchOutbox row | always (workspace-wide; not tied to a job) | none |

Chunks of a `failed` or `cancelled` job are not replayed: start a new job instead.

## 1. Find the failures and their cause

```sh
docker compose run --rm --no-deps worker jobs list --workspace <ws> --status completedWithErrors,failed,paused
docker compose run --rm --no-deps worker jobs failures --workspace <ws> --job <job>   # chunks and index tasks
docker compose run --rm --no-deps worker jobs failures --workspace <ws>               # SearchOutbox rows
```

API: `GET /api/v1/workspaces/<ws>/jobs/<job>/failures` (`kind` = `chunk` | `indexTask`, `attempts`, `error`,
`failedAt`) and `GET …/search-outbox/failures`. The job detail's `lastError` and `chunks.failed` /
`chunks.deadLettered` (failed because attempts ran out: crash loops, poison messages) summarise it.

Typical causes: OpenSearch rejected documents (`BulkItemsRejected: … mapper_parsing_exception` — a mapping problem;
fix the mapping or the offending field definition first), a dependency was down for longer than the retry budget
(5 attempts with backoff for chunks and tasks, 10 for outbox rows), a deleted field or revoked permission (job-level
failures are final by design, e.g. Q-69), or a worker crash loop (`AttemptsExhausted`).

## 2. Fix the cause

Replaying without fixing the cause only fails again (and uses another set of attempts). Check worker logs around
`failedAt` (`docker compose logs worker`), trace by the job's `correlationId` in Jaeger, and the dead-letter queues
below.

## 3. Replay

- One job: `POST /api/v1/workspaces/<ws>/jobs/<job>/retry-failed` → `202 { chunksReplayed, indexTasksReplayed, job }`,
  or `jobs replay --workspace <ws> --job <job> --operator "<your name>"`.
- Interactive edits: `POST …/search-outbox/retry-failed` → `202 { rowsReplayed }`, or
  `jobs replay-outbox --workspace <ws> --operator "<your name>"`.

## 4. Verify

- `jobs failures` lists nothing for the job; `jobs show` / `GET …/jobs/<job>`: chunks move to *Done*, `searchable.state`
  becomes `current` (index tasks applied and the workspace's refresh-aware search watermark,
  `searchable.indexedThroughGeneration`, at or past `searchable.jobGeneration`; Failed rows hold the watermark back,
  ADR-001 §7.2/§7.3). The dispatcher's watermark ticker observes a refresh about once a second; `GET …/search-freshness`
  shows the workspace's `state`, `pendingChanges` and `lagSeconds`.
- `audit.audit_event` has one `Job` / `Replayed` row per replay with `ChunksReplayed` / `IndexTasksReplayed`
  (`RowsReplayed` for the outbox).

## SearchOutbox rows

An outbox row fails after 10 publish attempts or when the index worker rejects it permanently. It keeps its document's
newer edits from being considered indexed and holds the watermark back, so the coding panel shows the document as
*not yet searchable*. Fix the cause and replay the workspace's failed rows (step 3); the index worker coalesces by
document version, so replaying old rows never overwrites newer state.

## Dead-letter queues

Each work queue has a `<queue>.dlq` (max 100,000 messages, TTL 14 days) and each area an `<area>.parking` queue for
unknown message types or unsupported schema majors (ADR-010 §7.2–7.3). Depth > 0 (`opportunity.queue.depth` with
`opportunity.queue.state` = `dlq`/`parking`, or `opportunity.dlq.messages` counting up) means a message crashed its
consumer repeatedly or could not be read. The dispatcher's dead-letter recorder copies every such message into
PostgreSQL ([README.md#dead-letter-records](README.md#dead-letter-records)); inspect the copies there, not in the
broker.

1. List the records: `jobs dlq list --workspace <ws> [--job <job>]` (newest first: message id, queue, reason, how often
   the broker dead-lettered it, message type, job, error), or `GET …/jobs/<job>/failures` (`kind: deadLetter`) for the
   job's records whose chunk or index task is still failed. Messages that name no existing workspace (unreadable
   bodies, forged or stale envelopes) are listed by `jobs dlq list` without `--workspace`.
2. `jobs dlq show [--workspace <ws>] --message <id>` prints one record: the `x-death` header and the transport's
   failure headers, the envelope fields (`workspaceId`, `jobId`, correlation id) and the body (the payload's `chunkId`,
   `taskId` or `outboxId` names the row).
3. Find that PostgreSQL row — it is either `Failed` already (replay it as above once fixed), or still
   `Pending`/`Dispatched`/`Running`, in which case the sweepers re-dispatch it
   ([re-dispatch-stuck-work.md](re-dispatch-stuck-work.md)).
4. Do **not** move or shovel DLQ messages back to a work queue. Purge a DLQ only after its rows are settled, and note it
   in the incident record; the PostgreSQL records stay for 30 days either way. The RabbitMQ management UI is no longer
   needed for inspection (if you use it anyway: *Get messages* with **Ack mode: Nack message requeue true**).
5. Parking-queue messages mean a version mismatch between publisher and consumer (deployment out of order): finish the
   rollout; the rows behind them are re-dispatched from PostgreSQL.
