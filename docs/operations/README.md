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
| [Secrets, keys and envelope encryption](keys-and-secrets.md) | supplying secrets, rotating KEKs and data keys (rewrap job), backing up keys, giving a workspace its own key, crypto-shredding (`keys …` CLI) |

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

The full alert set (E19-T05, #161) does not exist yet. Today `deploy/docker-compose/observability/alerts.yaml` holds one
rule; it carries a `runbook_url`, and a unit test (`AlertRunbookTests`) fails the build when an alert has no runbook or
links to a missing one. New alerts must follow the same rule.

| Alert / metric (ADR-017 catalog) | Runbook |
|---|---|
| **`OutboxOldestAgeHigh`** (`opportunity.outbox.oldest_age` > 60 s) | [re-dispatch-stuck-work.md#outboxoldestagehigh](re-dispatch-stuck-work.md#outboxoldestagehigh) |
| `opportunity.outbox.pending`, `opportunity.outbox.publish_latency`, `opportunity.dispatcher.published` | [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md) |
| `opportunity.index.chunk_tasks`, `opportunity.index.chunk_task.oldest_age` | [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md) |
| `opportunity.queue.depth{state="ready"}`, `opportunity.queue.consumers`, `opportunity.worker.heartbeat.age` | [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md) |
| `opportunity.queue.depth` with `opportunity.queue.state` = `dlq` or `parking` | [replay-failed-work.md#dead-letter-queues](replay-failed-work.md#dead-letter-queues) |
| `opportunity.index.chunk_task.attempts`, `opportunity.job.chunks` with `opportunity.outcome` = `failed` | [replay-failed-work.md](replay-failed-work.md) |
| `opportunity.search.index_lag`, `opportunity.search.generation.lag` (= `.committed` − `.indexed`; watermark stuck; also the dispatcher's "Search watermark tick failed" / refresh warnings) | [replay-failed-work.md](replay-failed-work.md), then [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md) |
| `opportunity.dlq.messages` (messages recorded by the dead-letter recorder, by queue and reason) | [replay-failed-work.md#dead-letter-queues](replay-failed-work.md#dead-letter-queues) |
| `opportunity.queue.consumers` for `opportunity.dead-letter.record` = 0, or its `ready` depth growing | the dispatcher (which runs the recorder) is down or cannot reach PostgreSQL: [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md) |

ADR-010 §7.6 also names alerts for *chunk Running > 15 min* and *oldest Pending age*; they arrive with E19-T05 and map
to [re-dispatch-stuck-work.md](re-dispatch-stuck-work.md).

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
