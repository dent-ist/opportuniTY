# Alert runbooks

One section per alert in `deploy/docker-compose/observability/alerts.yaml` (E19-T05); each alert's `runbook_url` points
here (or, for `OutboxOldestAgeHigh`, to [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md#outboxoldestagehigh)).
Thresholds, metrics and dashboards: [metrics.md](metrics.md). The dashboard *opportuniTY search consistency and
pipeline* shows every signal named below. The procedures that change state are in
[re-dispatch-stuck-work.md](re-dispatch-stuck-work.md) and [replay-failed-work.md](replay-failed-work.md); the `jobs …`
commands are the operations CLI ([README.md](README.md#tools)).

Search stays **correct** while any of these fire: results are re-checked against PostgreSQL per page (Q-12), so lag
makes recent changes invisible or late, never exposes restricted documents.

## InteractiveCommitToSearchableSlow

Interactive edits (coding, single-document changes) take more than 1 s (p95) to become searchable.

1. *Outbox oldest age* and *Commit → broker confirm p95*: if the outbox is slow, the dispatcher or RabbitMQ is the
   bottleneck — see [OutboxOldestAgeHigh](re-dispatch-stuck-work.md#outboxoldestagehigh).
2. *Ready messages by queue* for `index.interactive`: growing means too few index consumers or a slow OpenSearch
   (*Bulk items per second* with `transient` outcomes, [OpenSearchBulkRejections](#opensearchbulkrejections),
   [OpenSearchHeapHigh](#opensearchheaphigh)).
3. Both fast but the histogram slow: the watermark ticker (dispatcher) refreshes late — check the dispatcher logs for
   "Search watermark tick failed" and the [SearchWatermarkStuck](#searchwatermarkstuck) steps.
4. Large bulk jobs share OpenSearch with the interactive lane; their lanes are throttled (ADR-010 §6), but a burst
   can still raise interactive latency for a while. If it persists, scale the indexing worker.

## SecurityProjectionLagHigh

Security-affecting changes (restriction classes, walls, privilege designations) reach the index later than 5 s p95
(Q-10). Access is still enforced by the per-page PostgreSQL re-check, so users may briefly see fewer hits, not more.

1. `index.security` / `index.security-bulk` consumers and ready depth (*Consumers by queue*, *Ready messages by queue*):
   0 consumers is [IndexQueueWithoutConsumers](#indexqueuewithoutconsumers).
2. Security outbox lane age (*Outbox oldest undispatched age*, lane `security`).
3. Then as for [InteractiveCommitToSearchableSlow](#interactivecommittosearchableslow). Treat as an incident: Q-10 is
   a security objective.

## BulkIndexLagHigh

A workspace's oldest committed change has not been searchable for more than 2 minutes (§26).

1. *Index tasks by status*: many `Pending`/`Dispatched` → dispatcher or broker
   ([re-dispatch-stuck-work.md](re-dispatch-stuck-work.md)); `Running` growing → index workers or OpenSearch;
   `Failed` → [IndexChunkTasksFailed](#indexchunktasksfailed).
2. `jobs backlog --workspace <ws>` names the oldest un-applied work and its commit time.
3. A very large import may legitimately take longer than 2 minutes end to end; the alert then clears as the job
   drains. Compare *Index task attempts per second* with the backlog to estimate when.

## SearchWatermarkStuck

Committed generations are ahead of the visible watermark and it has not advanced for 10 minutes (§28). The watermark
waits for the oldest un-applied record, so one stuck or failed record holds back everything after it.

1. `jobs backlog --workspace <ws>`: the oldest un-applied outbox row or index task is the blocker.
2. `Failed` rows: fix the cause and replay ([replay-failed-work.md](replay-failed-work.md)). Rows stuck in
   `Dispatched`/`Running`: [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md).
3. Nothing un-applied but the watermark still behind: the refresh step fails — check the dispatcher logs ("Search
   watermark tick failed", refresh warnings) and that OpenSearch is reachable; the ticker retries every second.

## DeadLetterQueueNotEmpty

A message landed in a `<queue>.dlq` (it crashed its consumer repeatedly) or an `<area>.parking` queue (unknown type or
schema version). Follow [replay-failed-work.md](replay-failed-work.md#dead-letter-queues): inspect the PostgreSQL
copies with `jobs dlq list`, settle the rows behind them, then purge the queue — that clears the alert. Parking queue
messages mean an out-of-order deployment; finish the rollout.

## IndexQueueWithoutConsumers

No indexing worker consumes an `index.*` lane: nothing on that lane becomes searchable.

1. `docker compose ps worker` (or the `worker-indexing` deployment), its logs and
   [WorkerHeartbeatStale](#workerheartbeatstale).
2. A worker that cannot reach OpenSearch or PostgreSQL stops consuming; restore the dependency, then restart the worker.
3. Work waits safely in PostgreSQL and RabbitMQ and is indexed when consumers return.

## DeadLetterRecorderDown

The dispatcher's dead-letter recorder does not consume `opportunity.dead-letter.record`, so dead-lettered messages are
not copied into PostgreSQL (they still wait in that queue and in the DLQs). Check the dispatcher is running and can
reach PostgreSQL ([README.md](README.md#dead-letter-records)); restart it once PostgreSQL is available.

## IndexChunkTasksFailed

IndexChunkTasks exhausted their attempts (`Failed`). Their documents are not searchable with their latest state and
the watermark stops before them. Find the cause with `jobs failures --workspace <ws> --job <job>`, fix it, and replay:
[replay-failed-work.md](replay-failed-work.md).

## IndexChunkTaskRunningTooLong

An IndexChunkTask has held its lease for 15 minutes (ADR-010 §7.6 maximum chunk runtime). The worker is alive (it
renews the lease) but not finishing: OpenSearch is very slow or rejecting, or the chunk is pathological.

1. [OpenSearchBulkRejections](#opensearchbulkrejections), [OpenSearchHeapHigh](#opensearchheaphigh), *Bulk items per
   second* with `transient` outcomes.
2. Indexing worker logs for the task id (`jobs failures`/`jobs show` give the job).
3. Restarting the indexing worker releases the lease; the task is re-leased and retried (writes are versioned, so a
   repeat is harmless): [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md).

## JobChunkRunningTooLong

A job chunk (import, bulk coding, render, export, production) has been Running for 15 minutes. The worker watchdog
should abort chunks at that limit, so this usually means a worker that hangs while still renewing its lease, or a
dependency (object storage, renderer sandbox, PostgreSQL locks) that blocks.

1. `jobs show --workspace <ws> --job <job>` and the worker logs for that job.
2. PostgreSQL lock waits (`pg_stat_activity`), object-storage reachability.
3. Restart the worker of that job type; the lease expires and the sweeper returns the chunk to `Pending`
   ([re-dispatch-stuck-work.md](re-dispatch-stuck-work.md)).

## JobChunksFailed

A chunk failed permanently (attempts exhausted or a permanent error); its job ends *completed with errors* or is
paused by the circuit breaker. `jobs failures --workspace <ws> --job <job>` shows the error class; fix the cause and
replay ([replay-failed-work.md](replay-failed-work.md)). Repeated failures across jobs of one type point at a shared
dependency.

## WorkerHeartbeatStale

A worker type has not reported a heartbeat for more than 2 minutes: the process is down, hung, or its host cannot
export telemetry. Check `docker compose ps` / the orchestrator, the worker's logs and `/health/ready`; restart it. The
work it owned is recovered from PostgreSQL ([re-dispatch-stuck-work.md](re-dispatch-stuck-work.md)).

## SearchLatencyHigh

Search p95 is above target (simple 1 s, complex 3 s; §17). On developer hardware these numbers are comparative only
(Q-44).

1. [OpenSearchHeapHigh](#opensearchheaphigh), *Merges* and *Thread pool rejections* (`search`).
2. A heavy bulk import competes for OpenSearch I/O; it is throttled but large merges follow it.
3. *Searches per second by outcome*: a jump in load, or many `rejected` (query complexity limits) searches.
4. PostgreSQL latency (*PostgreSQL client operation p95* on the overview): the per-page re-check runs there.

## ApiErrorBudgetBurn

More than 1.44 % of API requests fail with 5xx over the last hour and the last 5 minutes: at this rate 2 % of the
monthly 99.9 % availability budget is gone in an hour. Check the API logs (Loki: `{service_name="opportunity-api"}`,
level error) for the failing route (overview *Latency p95 by route*, *Error ratio*), then its dependency (PostgreSQL,
OpenSearch, object storage, the identity provider); `/health/ready` reports the dependency checks.

## WalArchiveLagHigh

PostgreSQL is writing WAL but has not archived a segment for more than 4 minutes: the RPO (5 minutes, §17) is at risk.

1. `SELECT * FROM pg_stat_archiver;` — `last_failed_wal`/`last_failed_time` newer than `last_archived_time` means
   `archive_command` fails ([WalArchiveFailing](#walarchivefailing)); otherwise it hangs or is very slow.
2. Check the archive target (object storage reachability, credentials, free space) and the PostgreSQL log.
3. Watch `pg_wal` disk usage: unarchived segments are kept and can fill the volume.
4. Confirm `archive_timeout` is ≤ 60 s; without it a quiet database archives too rarely and this alert can fire
   when writes resume.

## WalArchiveFailing

`archive_command` keeps failing. PostgreSQL retries and keeps the segments; follow [WalArchiveLagHigh](#walarchivelaghigh)
steps 1–3.

## PostgresReplicationLagHigh

A standby replays WAL more than 60 s behind the primary. Check the standby's I/O and CPU, network to the primary, and
long-running queries on the standby that conflict with replay (`pg_stat_database_conflicts`). Do not fail over to a
lagging standby without accepting the data loss window.

## OpenSearchBulkRejections

OpenSearch rejects bulk writes (write thread pool queue full). Index workers retry with backoff, so nothing is lost,
but lag grows. Reduce concurrent bulk indexing (fewer indexing worker replicas, smaller bulk batches), check
[OpenSearchHeapHigh](#opensearchheaphigh) and merges, and size the cluster for the import rate.

## OpenSearchHeapHigh

A node's JVM heap is above 90 % for 15 minutes; garbage collection pauses and circuit-breaker rejections follow. Check
for large aggregations or deep pagination in search load, field data usage, and shard count per node. Raise the heap
(at most half the node's memory) or add nodes; restarting a node only helps briefly.
