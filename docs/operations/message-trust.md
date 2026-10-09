# Message and worker trust

How opportuniTY keeps a forged or tampered message, a stolen worker credential or a user who lost access after
submitting a job from turning into a data leak (E05-T07, #52; ADR-015 D9, threat model T-31, T-32, T-35, T-37). This
runbook covers what is enforced, how to configure it, how to rotate its credentials and what to do when a message is
rejected.

## What is enforced

| Control | Where | Effect |
|---|---|---|
| **Envelope verification** (ADR-015 D9.2) | every consumer: `JobChunkConsumer` (import, bulk coding, render, export, production), `ChunkIndexTaskConsumer`, `InteractiveIndexWorker` | The envelope's `WorkspaceId` is only a hint. The worker loads the work row (job chunk, index task, SearchOutbox row) **under RLS with the hinted workspace**. If the row is invisible, or its workspace, job, operation, sequence, document or idempotency key differs from the envelope, the message is rejected: an installation-level `Integrity.MessageRejected` audit event (reason `EnvelopeMismatch`, claimed workspace and job, message type and id, queue, the field that disagreed), the message is dead-lettered, and **nothing** is written (a lease the check needed is given back). Everything the work does — workspace, documents, snapshot, actor — comes from PostgreSQL. |
| **HMAC envelope signing** (D9.5, optional, on in the Compose profile) | publisher and every consumer (`Opportunity.Messaging`) | The publisher adds `opportunity-signature-kid` and `opportunity-signature` (HMAC-SHA256 over the exact envelope body and the destination queue). With signing on, a consumer rejects a message whose signature is missing (`SignatureMissing`), wrong (`SignatureInvalid`, which includes a valid message replayed onto another queue) or made with a key id it does not accept (`SignatureKeyUnknown`): `Integrity.MessageRejected`, dead-lettered with failure reason `signature-missing` / `signature-invalid` / `signature-key-unknown`, and the handler never runs. If the audit event cannot be written the delivery is retried, so a rejection is never lost. Signing is tamper evidence, not authority: verification still re-derives everything from PostgreSQL. |
| **Broker user per component** (D9.5) | RabbitMQ permissions (`RabbitMqPermissions`, `deploy/docker-compose/rabbitmq/users.sh`) | Only the dispatcher's user may publish work (`opportunity.work`); it reads only the dead-letter recorder's queue. Each queue area's user (`import`, `index`, `render`, `export`, `production`, `bulkcoding`) may consume only its own work queues and publish only to its own `<area>.retry` and `<area>.dlx`. No runtime user can read another area's queue, a `*.dlq` or a `*.parking` queue, configure anything, or use the management UI; the diagnostic queues are the operator's (`RABBITMQ_USER`). The API has no broker user. |
| **Per-area connections in one process** | `Messaging:RabbitMq:AreaConnectionStrings:<area>` | The combined Lite worker opens one connection per area with that area's user, so a render consumer cannot publish to the export exchanges even inside the same process. |
| **Database login per component** (D7.3, D9.6) | `deploy/docker-compose/postgres/init/30-component-logins.sh` | The API (`opportunity_api`), the worker (`opportunity_worker`) and the metrics exporter (`opportunity_monitor`, `pg_monitor` only) each have their own login. None is a superuser, owns objects, creates roles or bypasses RLS; the owner (`opportunity_owner`) is used by the one-shot migrator only. |
| **Async re-authorization** (D9.4, Q-15) | export, production volume and bulk coding chunk executors | Each chunk rebuilds the initiator's principal from PostgreSQL. If they lost the job's permission, the remaining chunks are cancelled. Documents they can no longer access are handled **per policy**: an **export** (and bulk coding) **excludes** them — listed in `EXCLUSIONS.csv` and the job's item results with the generic reason `AccessChanged`, the precise reason (restriction class, ethical wall, deleted) audited as `Export.DocumentsExcluded`; a **production volume fails** the run, because a production cannot leave a member out of its Bates numbering — the chunk fails with `AccessChanged` and a count, and the denied members with their precise reasons are audited as `Job.Failed` (details `Policy=FailRun`, `Documents`). |

Accepted limits (Lite, AR-04): the combined worker holds every area's broker password and one database login with
the union of the worker types' privileges. Database privileges are not yet narrowed per worker type: every runtime
login is a member of `opportunity_app` (DML on application tables, RLS applies). Split worker hosts should each get a
login of their own (for example `opportunity_worker_render`, created like the logins in `30-component-logins.sh`);
per-table grants per worker type (the D9.6 matrix) are left to the Full profile (E19-T06, #162). AMQPS works by
giving `amqps://` URIs; the Compose profile uses plain AMQP on the private Compose network.

## Configuration

| Setting | Meaning |
|---|---|
| `ConnectionStrings__RabbitMq` | Default broker user of the process: the dispatcher's in a worker that publishes work |
| `Messaging__RabbitMq__AreaConnectionStrings__<area>` | Broker user for one queue area (`import`, `index`, `render`, `export`, `production`, `bulkcoding`); areas without an entry use the default |
| `Messaging__RabbitMq__Signing__Enabled` | `true` signs every published message and rejects unsigned or badly signed ones; set it the same on every publisher and consumer |
| `Messaging__RabbitMq__Signing__KeyId` | Key id new messages are signed with (default `k1`); `[A-Za-z0-9_-]{1,32}` |
| `Messaging__RabbitMq__Signing__AcceptedKeyIds` | Further key ids consumers accept, comma-separated (rotation overlap) |
| secret `envelope-hmac-<keyId>` | The HMAC key, at least 32 bytes, through `ISecretProvider`: the Docker secret `/run/secrets/envelope-hmac-<keyId>` (Full), `OPPORTUNITY_SECRET_ENVELOPE_HMAC_<KEYID>_FILE`, or — in the development Compose profile, whose read-only containers cannot receive a generated secret file — `OPPORTUNITY_SECRET_ENVELOPE_HMAC_K1` from `ENVELOPE_HMAC_SECRET_KEY` in `.env` |

In the Compose profile `./opportunity.sh init` generates every password and the HMAC key into `.env`, and
`./opportunity.sh up` creates or updates the database logins and broker users before the application starts (rerun
`init` after an update to add new secrets). `OPPORTUNITY_ENVELOPE_SIGNING=false` in `.env` switches signing off.

## Rotate the envelope HMAC key

Messages in flight are signed with the old key, so rotate in three steps, restarting every publisher and consumer
(in Compose: the `worker` service) after each:

1. Add the new key as the secret `envelope-hmac-k2` (Compose: `OPPORTUNITY_SECRET_ENVELOPE_HMAC_K2` on the worker) and set `Messaging__RabbitMq__Signing__AcceptedKeyIds=k2` (consumers
   now accept both; publishers still sign with `k1`).
2. Set `Messaging__RabbitMq__Signing__KeyId=k2` and `AcceptedKeyIds=k1`.
3. Once the queues hold no message signed with `k1` (retry tiers wait at most 5 minutes; check the DLQ and parking
   depths), remove `AcceptedKeyIds` and the `envelope-hmac-k1` secret.

In the Compose profile, a single-step rotation is acceptable while the stack is stopped: change
`ENVELOPE_HMAC_SECRET_KEY` in `.env`, then `./opportunity.sh up`. Messages already queued with the old key will be
rejected and recorded as dead letters; replay their work from PostgreSQL ([replay-failed-work.md](replay-failed-work.md)).

## Rotate a broker or database password

Change the value in `.env` (or the secret store) and run `./opportunity.sh up`: `rabbitmq/users.sh` and
`postgres/init/30-component-logins.sh` reset the passwords, then the services restart with the new values. Outside
Compose: `rabbitmqctl change_password <user>` / `ALTER ROLE <login> PASSWORD …`, update the secret, restart the
component. The permission patterns must stay those of `RabbitMqPermissions` (`ComposeCredentialTests` fails the build
when `users.sh` drifts from them).

## A message was rejected

Symptoms: `DeadLetterQueueNotEmpty` fires, `opportunity.dlq.messages` grows with a `signature-*` or `permanent` reason,
or the audit log shows `Integrity.MessageRejected`.

1. Find the events (installation-level, so `workspace_id` is null; no audit API exists yet, query PostgreSQL as the
   operator):
   `SELECT occurred_at, reason_code, actor_display, details FROM audit.audit_event WHERE category = 'Integrity' AND action = 'MessageRejected' ORDER BY occurred_at DESC LIMIT 50;`
   and the dead-letter records: `jobs dlq list` / `jobs dlq show --message <id>` ([README](README.md#dead-letter-records)).
   A forged message names no existing workspace or a workspace it does not belong to, so its record is usually
   installation-level (`jobs dlq list` without `--workspace`).
2. Read the reason code:
   - `EnvelopeMismatch` — the envelope disagrees with PostgreSQL. A single one after restoring a database backup or
     deleting a workspace can be a stale message; repeated ones, or ones naming a workspace the row does not belong
     to, mean something with publish rights is forging work: treat it as a security incident (which component's
     broker user published it is in the broker's connection log; rotate that user's password).
   - `SignatureMissing` — a publisher without signing (mis-configured `Signing__Enabled`, an old component) or a
     message injected around the dispatcher.
   - `SignatureInvalid` — a publisher with the wrong key, a tampered body, or a message replayed onto another queue.
   - `SignatureKeyUnknown` — a rotation step was skipped (see above).
3. Nothing was written for a rejected message, so no data needs repair. Fix the cause, then replay the affected work
   from PostgreSQL ([replay-failed-work.md](replay-failed-work.md)); rejected messages are never re-published.

## Verification

- `MessageTrustTests` (integration, real broker): unsigned and forged signatures are dead-lettered and audited and the
  handler never runs; the render worker's user cannot publish to the export exchanges or read any other queue, DLQ or
  parking queue (ACCESS_REFUSED from the broker); per-area users inside one process; `users.sh` applied to a broker.
- `JobChunkConsumerTransportTests`, `IdempotentConsumerTests`, `InteractiveIndexWorkerTests`: forged `WorkspaceId`
  messages are rejected with `Integrity.MessageRejected` and write nothing.
- `ExportExecutionTests`, `ProductionVolumeTests`: access lost after submission excludes (export) or fails (production)
  per policy and records the delta.
- `ComposeDatabaseLoginTests` (the init scripts on a fresh PostgreSQL container) and `ComposeCredentialTests` (no
  superuser, owner or broker administrator credential in any runtime service; one login and broker user per component).
