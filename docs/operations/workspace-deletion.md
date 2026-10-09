# Runbook: workspace deletion

**Applies to:** deleting a workspace defensibly (E20-T02, #167; ADR-014 §3–§8, Q-23), following a run, a run that
halted or waits, and reading the destruction certificate. **Who:** a Workspace Admin requests; a **Retention
Approver** approves; an installation operator watches the run and investigates residuals.

## 1. Roles and configuration

| Setting (section `WorkspaceDeletion`, API and indexing worker) | Default | Meaning |
|---|---|---|
| `WaitingPeriod` | `7.00:00:00` | Time from approval to the earliest start of the run (1–90 days). |
| `AllowShortWaitingPeriod` | `false` | Accept a waiting period under one day (Lite profile, demonstrations, tests only). |
| `RequestExpiry` | `30.00:00:00` | An unapproved request expires. |
| `DrainSettleDelay` | `00:01:00` | The drain waits at least this long after the fence, even when no lease is visible. |
| `ResurrectionGuardDelay` | `00:05:00` | Wait between the first search purge and the second pass (2 × lease TTL + `index.gc_deletes`). |
| `BatchSize` | `10000` | Rows per database purge batch. |
| `BackupRetention` | `35.00:00:00` | How long backups and PITR archives are kept (ADR-016); the certificate's "backups expire by" date. |
| `PollInterval`, `LeaseDuration` | `00:00:15`, `00:02:00` | The coordinator's pass interval and run lease. |

The Retention Approver role is held by members of the IdP groups in `Authorization:RetentionApproverGroups` (empty means
nobody, default deny); it carries the installation permission `Installation.ApproveDeletion`, is separate from
Installation Admin and from every workspace role (ADR-014 §3.2), and approving needs an MFA session. Requesting needs
`Workspace.RequestDeletion` (Workspace Admin).

## 2. The flow

1. **Request** (Admin › Workspace Settings › Delete workspace, or `POST /api/v1/workspaces/{ws}/deletions`): retention
   profile (`retainRecords`, the Q-23 default, or `purgeAll`), a reason, an optional order reference and the workspace
   name typed. Refused with 423 while a legal hold applies; one open request per workspace.
2. **Approve** (Workspace deletions page, or `POST /api/v1/workspace-deletions/{id}/approve` with If-Match): a different
   person with `Installation.ApproveDeletion`. The requester or an approver may cancel until the run starts.
3. **Run** — the deletion coordinator in the **indexing worker** starts it once the waiting period has passed. Steps,
   each recorded in `opportunity.workspace_deletion_step` with counts:
   `Fence` (workspace → `Deleting`, epoch + 1; the API answers 404 for it and PostgreSQL refuses new jobs, documents,
   objects, search work and dead-letter rows with SQLSTATE `O0410`) → `Drain` (unfinished jobs cancelled; waits for live
   leases and claims) → `Inventory` (first calls the optional `IBeforeWorkspaceDeletion` seam, reserved for the audit
   chain checkpoint of #117) → `SearchPurge` → `DatabasePurge` → `StoragePurge` → `KeyDestruction` (PurgeAll:
   crypto-shredding, see [keys-and-secrets.md](keys-and-secrets.md)) → `Verification` (after the resurrection guard
   delay: second search pass, every store counted; a store with data left is purged again up to 3 times) →
   `Certification` (workspace → `Purged`, certificate stored, `Workspace.Deleted` audited in the workspace's and the
   installation's chain).
4. **Certificate** — `GET /api/v1/workspace-deletions/{id}/certificate` (requester and approvers): the certificate, its
   canonical text, SHA-256 and ES256 signature with the audit checkpoint key (unsigned when no key is available). The
   same bytes are in object storage under `sys/certificates/{deletionId}/{sha256}`.

What survives: the workspace row (tombstone), its legal hold records, its data key records (destroyed under PurgeAll),
its audit trail (Q-16), the deletion's own records and the certificate; under RetainRecords also productions with their
members, Bates ranges and volume outputs (`ws/{ws}/productions/`), their snapshots, jobs and Redaction Sets — and the
data keys those outputs need.

## 3. Following and fixing a run

```sql
SELECT deletion_id, status, step, error, started_at, next_step_at FROM opportunity.workspace_deletion
 WHERE deletion_workspace_id = '<workspace id>' ORDER BY requested_at DESC;
SELECT step, attempt, started_at, finished_at, outcome, counts FROM opportunity.workspace_deletion_step
 WHERE deletion_id = '<deletion id>' ORDER BY started_at;
```

| Symptom | Cause and action |
|---|---|
| `Approved`, error "Waiting: the workspace is under a legal hold" | A hold was placed after approval; the run starts once every hold is released (audited `DeletionBlocked` once). |
| `Halted` | A hold was placed during the run; completed steps are irreversible (see the step rows). The run resumes by itself once every hold is released. |
| `Running`, step `Drain` for long | Leases or claims of the workspace are still live (workers that have not reached a fence). They expire on their own; check for a stuck worker ([re-dispatch-stuck-work.md](re-dispatch-stuck-work.md)). |
| `Running` with an `error` | A store failed; the step is retried every pass. Fix the store (OpenSearch, object storage, key provider). |
| `CompletedWithResiduals` | Verification still counted data after three purges. Use [deletion-verification.md](deletion-verification.md) to find it; the certificate lists it under `VerificationResiduals`. |

Nothing in this flow can be undone after the fence. Never edit `workspace_deletion` rows by hand except to clear a stale
`lease_owner` of a dead worker (it expires after `LeaseDuration` anyway).
