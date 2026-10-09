# Operations runbooks

Runbooks for the job and search pipeline (E06-T06, #61). PostgreSQL owns job and search-work state (baseline §11,
ADR-010 §7): every recovery here changes rows in PostgreSQL and lets the dispatcher publish again. RabbitMQ
dead-letter queues (`*.dlq`) are kept for diagnostics only and are **never** re-published; the dispatcher's dead-letter
recorder copies every dead-lettered or parked message into PostgreSQL (`jobs dlq list`), so they are inspected there.

| Runbook | Use it when |
|---|---|
| [Re-dispatch stuck work](re-dispatch-stuck-work.md) | work sits in Pending/Dispatched/Running without progress; outbox or index backlog grows |
| [Replay failed work](replay-failed-work.md) | chunks, index tasks or SearchOutbox rows are `Failed`; a job ended *completed with errors*; DLQ depth > 0 |
| [Alias reindex](alias-reindex.md) | the search projection must be rebuilt into a new index generation (mapping change, corruption) |
| [Deletion verification](deletion-verification.md) | proving that a workspace's data is gone from PostgreSQL, OpenSearch and object storage |
| [Alert runbooks](alerts.md) | a Prometheus alert fired (one section per alert) |
| [Metrics, dashboards and alerts](metrics.md) | finding the metric, dashboard panel or alert threshold for a pipeline signal |
| [Secrets, keys and envelope encryption](keys-and-secrets.md) | supplying secrets, rotating KEKs and data keys (rewrap job), backing up keys, giving a workspace its own key, crypto-shredding (`keys …` CLI) |
| [Audit hash chain and `audit verify`](audit-chain.md) | proving the audit trail was not modified, deleted or reordered; exporting signed checkpoints for an exhibit; sealing before an audit purge or deletion run (`audit …` CLI) |

## Tools

**Job monitor API** (workspace-scoped, `/api/v1/workspaces/{ws}`; permissions in
[permission-matrix.md](../security/permission-matrix.md)):

| Call | Permission |
|---|---|
| `GET /jobs?type=&status=&createdBy=me\|all&from=&to=&updatedSince=&cursor=&limit=` | member (own jobs; all with `Job.ViewAll`) |
| `GET /jobs/{jobId}` — committed vs searchable progress, chunks by state, attempts, last error, ETA | own job or `Job.ViewAll` |
| `GET /jobs/{jobId}/failures` — failed chunks and index tasks | own job or `Job.ViewAll` |
| `POST /jobs/{jobId}/cancel` | own job or `Job.Manage` |
| `POST /jobs/{jobId}/retry-failed` — replay, audited `Job.Replayed` | `Job.Replay` |
| `GET /search-outbox/failures`, `POST /search-outbox/retry-failed` | `Job.ViewAll` / `Job.Replay` |
| `GET /job-events` — server-sent events, heartbeat every 15 s | member (own jobs; all with `Job.ViewAll`) |

**Operations CLI** in the worker image (same PostgreSQL operations as the API; runtime login, RLS applies; replays are
audited as `service:ops-cli` with the operator's name):

```sh
cd deploy/docker-compose
docker compose run --rm --no-deps worker jobs list       --workspace <ws> [--status failed,completedWithErrors] [--type import]
docker compose run --rm --no-deps worker jobs show       --workspace <ws> --job <job>
docker compose run --rm --no-deps worker jobs failures   --workspace <ws> [--job <job>]   # without --job: failed SearchOutbox rows
docker compose run --rm --no-deps worker jobs replay     --workspace <ws> --job <job> --operator "<your name>"
docker compose run --rm --no-deps worker jobs replay-outbox --workspace <ws> --operator "<your name>"
docker compose run --rm --no-deps worker jobs backlog    --workspace <ws>
docker compose run --rm --no-deps worker jobs redispatch --workspace <ws>
docker compose run --rm --no-deps worker jobs dlq list   [--workspace <ws>] [--job <job>] [--limit 50]
docker compose run --rm --no-deps worker jobs dlq show   [--workspace <ws>] --message <message id>
```

`jobs dlq …` reads the dead-letter records (below); without `--workspace` it lists the installation-level ones
(messages that name no existing workspace, e.g. unreadable bodies).

The dedicated dispatcher image (`Opportunity.Worker.Dispatcher`) accepts the same `jobs …` arguments. Exit codes: 0
done, 1 not found, 2 usage.

## Alerts and metrics → runbooks

The metric catalog, dashboards and the full alert set (E19-T05, #161) are described in [metrics.md](metrics.md); each
alert has a section in [alerts.md](alerts.md). The rules live in `deploy/docker-compose/observability/alerts.yaml`;
every alert carries a `runbook_url` (`AlertRunbookTests` fails the build when one is missing or points at a missing
heading) and fires in an induced-failure case of `alerts.test.yaml` (`promtool test rules`). New alerts must follow the
same rules.

| Alert / metric (ADR-017 catalog) | Runbook |
|---|---|
| **`OutboxOldestAgeHigh`** (`opportunity.outbox.oldest_age` > 60 s) | [re-dispatch-stuck-work.md#outboxoldestagehigh](re-dispatch-stuck-work.md#outboxoldestagehigh) |
| **`BulkIndexLagHigh`**, **`SearchWatermarkStuck`**, **`IndexChunkTaskRunningTooLong`**, **`JobChunkRunningTooLong`**, **`IndexQueueWithoutConsumers`**, **`WorkerHeartbeatStale`** | [alerts.md](alerts.md), then [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md) |
| **`IndexChunkTasksFailed`**, **`JobChunksFailed`** | [alerts.md](alerts.md), then [replay-failed-work.md](replay-failed-work.md) |
| **`DeadLetterQueueNotEmpty`** (`opportunity.queue.depth` with state `dlq`/`parking` > 0), **`DeadLetterRecorderDown`** | [alerts.md](alerts.md), then [replay-failed-work.md#dead-letter-queues](replay-failed-work.md#dead-letter-queues) |
| **`InteractiveCommitToSearchableSlow`**, **`SecurityProjectionLagHigh`**, **`SearchLatencyHigh`**, **`ApiErrorBudgetBurn`** | [alerts.md](alerts.md) |
| **`WalArchiveLagHigh`**, **`WalArchiveFailing`**, **`PostgresReplicationLagHigh`**, **`OpenSearchBulkRejections`**, **`OpenSearchHeapHigh`** | [alerts.md](alerts.md) |
| `opportunity.outbox.pending`, `opportunity.outbox.publish_latency`, `opportunity.dispatcher.published` | [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md) |
| `opportunity.index.chunk_tasks`, `opportunity.index.chunk_task.oldest_age`, `opportunity.job.chunk.backlog`, `opportunity.job.chunk.oldest_age`, `opportunity.jobs.active` | [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md) |
| `opportunity.dlq.messages` (messages recorded by the dead-letter recorder, by queue and reason) | [replay-failed-work.md#dead-letter-queues](replay-failed-work.md#dead-letter-queues) |

## Dead-letter records

The dead-letter recorder (ADR-010 §7.3) runs in the dispatcher. Each area's dead-letter exchange delivers every
dead-lettered or parked message twice: to its diagnostic queue (`<queue>.dlq` with a 14-day TTL, or `<area>.parking`;
both untouched) and to `opportunity.dead-letter.record`, which the recorder drains into PostgreSQL:

- `opportunity.dead_letter` — the envelope names an existing workspace: a workspace row (RLS), shown to the job's
  viewers in `GET …/jobs/{jobId}/failures` as `kind: deadLetter` while the chunk or index task it carries is failed;
- `opportunity.dead_letter_installation` — no workspace, an unreadable body, or a workspace that does not exist (kept
  as `claimed_workspace_id`).

A record holds the message id (AMQP id, else the envelope's, else `body-<hash>`), queue, exchange and routing key, the
AMQP headers and envelope fields (`headers`, JSON, without the payload), the death reason (`permanent`,
`retries-exhausted`, `malformed`, `unknown-message-type`, … from the consumer, or the broker's `delivery_limit`) and
count, the error, and the body up to 64 KiB (`body_size` is the full size). It is idempotent by message id (a
duplicate dead-letter changes nothing) and deleted 30 days after it was recorded (`Messaging__DeadLetterRecorder__Retention`).
A delivery is acknowledged only after its record is committed; while PostgreSQL is down the recorder retries and the
messages wait in `opportunity.dead-letter.record`. The dispatcher's broker user may read that queue only (ADR-015 D9.5).

## Game day

The ticket asks for the runbooks to be exercised in a recorded game day. That needs the running stack and an operator
and has **not** been done yet; the integration tests exercise each procedure's PostgreSQL steps (`JobOperationsApiTests`,
`IndexTaskReplayTests`). Record the game day here (date, scenario, runbook, outcome, follow-ups) when it happens.
