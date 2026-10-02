# ADR-014: Matter retention, legal hold and defensible deletion

| Field | Value |
|---|---|
| **Status** | Proposed (interim position below applies until Accepted) |
| **Date** | 2026-10-02 |
| **Owner (role)** | Backend |
| **Deciders** | Lead architect; contributing: Legal / Discovery Counsel, Security & Compliance, DevOps / SRE; product owner (Q-16, Q-23) |
| **Tracking issue** | #32 (plan key `E02-T06`) |
| **Baseline sections** | [§1](../architecture/architecture-baseline.md#1-goals-and-scope), [§2](../architecture/architecture-baseline.md#2-core-principles), [§6](../architecture/architecture-baseline.md#6-metadata-coding-and-postgresql-partitioning), [§15](../architecture/architecture-baseline.md#15-security-audit-and-lifecycle), [§21](../architecture/architecture-baseline.md#21-bulk-indexing-contract) |
| **Related** | ADR-001 (versions, tombstones), ADR-005 (partitioning), ADR-010 (jobs, leases), ADR-011 (prefix deletion, `KeyId`), ADR-012 (redaction revisions), ADR-013 (audit), ADR-016 (backup); review findings A-05, A-21, §10.1; Q-13, Q-16, Q-23, Q-40 |

## Context

§15 says matter deletion "may remove PostgreSQL data/partitions, OpenSearch data, storage prefixes, derived artifacts
and keys, subject to retention/hold policy", but no retention or hold model exists and §1 defers custodian legal-hold
notices. Counsel's finding 11: deleting a workspace still under hold, inside an appeal window or under a protective
order's return-or-destroy clause is spoliation risk (FRCP 37(e)); protective orders often require **certified**
destruction within a deadline. Backend finding A-05: a delayed index retry after `index.gc_deletes` (60 s) can
resurrect a deleted document, so deletion must fence in-flight work.

Binding product decisions:

- **Q-23** — retain productions, logs and audit by default; deletion needs two-person approval (requester plus an
  approver with a designated role); a workspace-level lock is sufficient for the MVP.
- **Q-16** — audit is retained for the life of the matter plus a configurable period, default **7 years after matter
  close**, set per installation.
- **Q-13** — break-glass access is separately audited and reportable, which must survive deletion.
- **Q-40** — off-site DR is an operator responsibility, so backups outside the installation are residuals we can only
  report, not purge.

This ADR stays *Proposed* because its full implementation (`E20-T01`, `E20-T02`) lands in M3 and Legal must review the
certificate wording; the interim position fixes what M0–M2 code must already respect.

## Decision

### 1. Workspace lifecycle

```text
          close                 request+approve+wait        all steps verified
Active ──────────► Closed ───────────────────────────► Deleting ───────────────► Purged
  ▲                  │ reopen (audited)                    │
  └──────────────────┘                                     └─ hold placed mid-run → Deleting(Halted)
Preservation lock (legal hold): separate records, any number, valid in Active / Closed / Deleting
```

1. `Workspace.Status` ∈ {`Active`, `Closed`, `Deleting`, `Purged`}. (`E04-T02` lists `Locked`; it is replaced by
   `Closed` here. A preservation lock is a separate record, not a status, because several holds can overlap.)
2. **Close** (Workspace Admin, audited): review data becomes read-only (no coding, redaction, import or new
   productions). Productions, privilege logs, reports and audit stay readable and downloadable under normal permissions.
   `ClosedAt` starts the retention clock: `AuditRetainUntil = ClosedAt + AuditRetentionPeriod` (installation setting,
   default 7 years, Q-16). **Reopen** clears `ClosedAt` and the clock.
3. `Purged` is a tombstone: the Workspace row keeps `WorkspaceId`, name, matter number, timestamps, retention profile
   and certificate reference, so audit records and certificates still resolve. A `WorkspaceId` is never reused.

### 2. Preservation lock (legal hold)

1. `PreservationLock`: `LockId`, `WorkspaceId`, `Scope` (`Workspace` in the MVP; `DocumentSet` reserved), `Reason`,
   `MatterReference`, `PlacedBy`, `PlacedAt`, `ReleaseRequestedBy`, `ReleaseApprovedBy`, `ReleasedAt`.
2. **Placing** a lock needs one person with `Workspace.ManageHolds` and takes effect immediately (preservation must
   never wait for a second approver). **Releasing** a lock needs two people (Q-23 pattern, §3.2).
3. While any lock is active the system refuses, with `423 Locked` ProblemDetails and an audit event: workspace deletion
   requests, approvals and runs; deletion of any document, artifact, rendition (including ADR-011 garbage collection),
   coding history, redaction revision, snapshot, production, privilege log or report; and audit purge for the workspace.
   Closing and reopening remain allowed.
4. Every destructive step of a deletion run re-checks for active locks in the same transaction that records the step's
   start (§4.2). A lock placed during a run halts it before the next destructive step (`Deleting(Halted)`); already
   completed steps are irreversible and are listed in the halt report.

### 3. Deletion requests and two-person approval (Q-23)

1. A **request** names the workspace, the retention profile (§5), a reason and an optional external reference
   (protective-order paragraph, court order). Requester needs `Workspace.RequestDeletion` (Workspace Admin by default).
2. An **approval** needs a different person holding the installation role **Retention Approver** (assigned by an
   installation admin; it is not part of any workspace role). Approver ≠ requester is enforced by user ID and by IdP
   subject. Approving is a separate request, with step-up authentication when the IdP supports it (`acr`).
3. The run may start only after a **waiting period** from approval: default 7 days, configurable 1–90 days; 0 is
   permitted only in the Lite profile. Requester, approver and the workspace's admins are notified at request, approval
   and start. Either party may cancel until the run starts. Unapproved requests expire after 30 days.
4. The same request/approve/wait flow applies to deleting a single production or privilege log, releasing a hold, and
   purging a workspace's audit after `AuditRetainUntil` (§6).

### 4. Deletion run: sequence and fencing

The run is a job (ADR-010) executed by the lifecycle worker, with one `DeletionStep` row per step recording start,
end, counts before and after, and outcome. Every step is idempotent and resumable after a crash.

| # | Step | Rule |
|---|---|---|
| 0 | Preconditions | In one transaction with `SELECT … FOR UPDATE` on the Workspace row: request approved, waiting period elapsed, not cancelled, **no active lock**. Then set `Status = Deleting` and increment `Workspace.Epoch`. |
| 1 | Fence | From that commit the API returns 410 for every workspace route except deletion status; the dispatcher stops dispatching the workspace's outbox rows and chunk tasks; new jobs are refused. |
| 2 | Drain / cancel | All non-terminal jobs → `Cancelled (WorkspaceDeleting)`. Wait until no chunk lease for the workspace is held or unexpired (lease TTL from ADR-010). Dead-lettered messages for the workspace are purged by the DLQ tooling. |
| 3 | Inventory | Record counts per PostgreSQL table, per OpenSearch index, per storage area (from the ADR-011 registry, objects and bytes) and per key. These "before" counts go on the certificate. |
| 4 | OpenSearch | Dedicated index: delete index and aliases. Shared index: `delete_by_query` on `workspaceId` with routing, then refresh. Repeat after `2 × lease TTL + index.gc_deletes` and require a zero count both times. |
| 5 | PostgreSQL | Dedicated partitions: `DETACH` + `DROP`. Shared partitions: batched `DELETE … WHERE WorkspaceId = $1` (initial batch 10,000 rows, tuned in `E18-T08`). Tables kept by the retention profile (§5) are skipped. |
| 6 | Object storage | `DeletePrefix` per area as the profile dictates (`ws/{ws}/` for purge-all, per-area prefixes otherwise), including non-current versions (ADR-011 §7). |
| 7 | Keys | If the workspace has its own key-encryption key: rewrap retained objects to the installation records key, then schedule destruction of the workspace key in the key provider (provider minimum waiting periods apply and are recorded). |
| 8 | Verify | §7. Any non-zero count re-runs the failing step (max 3 attempts), then stops as `CompletedWithResiduals` for an operator. |
| 9 | Certify | §8. Set `Status = Purged`. Write `Workspace.Deleted` (ADR-013). |

**Fencing rule.** Every worker, at each fence point — before starting a chunk, before each external write
(OpenSearch bulk request, object put, export file write) and before committing — verifies in PostgreSQL that the
workspace is `Active` or `Closed` and that `Workspace.Epoch` equals the epoch recorded on its job row. If either check
fails it aborts without writing and acks the message. An index worker therefore cannot write a document after step 4's
first pass, and the second pass in step 4 removes anything that slipped through the check-then-write window. Together
with ADR-001's "row missing in PG = delete" rule this gives `E20-T02`'s "no resurrected documents after 5 min".

Workers implement the fence check from M1 even though no deletion API exists until M3 (interim position), because
retrofitting it into every handler later is error-prone.

### 5. Retention profiles

| Profile | Purged | Retained (outside review use, read-only) |
|---|---|---|
| **RetainRecords** (default, Q-23) | Documents, natives, text, page images and renditions, coding current state and history, redaction revisions, search index, imports, unreferenced snapshots, reports other than those retained, saved searches, review batches | Production outputs, manifests and QC reports; privilege logs; snapshot **membership** (DocumentId lists) referenced by productions or by audit events of bulk jobs and exports (ADR-013); acknowledgment rosters; the certificate; audit |
| **PurgeAll** | Everything above plus productions, privilege logs and snapshot membership | Certificate; audit |

1. Audit is never purged by a workspace deletion. It follows §6 only.
2. Retained records stay in their workspace areas (`ws/{ws}/productions/…`, PostgreSQL production tables). The
   `Purged` workspace exposes them only to Production Manager, Workspace Admin and Auditor roles.
3. After RetainRecords, a production can be downloaded but no longer re-run: its source pages and redaction revisions
   are gone. The certificate states this. A later deletion of retained records is a separate two-person request.

### 6. Audit retention

1. Audit events of a workspace are kept until `AuditRetainUntil` (Q-16), regardless of workspace deletion. A workspace
   never closed has no expiry.
2. After expiry, an audit purge removes the workspace's **entire** chain (ADR-013) in one run, under the retention
   role, after two-person approval, and only with no active lock. It leaves an `Audit.Purged` event in the installation
   chain with the workspace's final sequence number, chain head hash and event count. Partial purges are not allowed;
   they would break the chain.

### 7. Verification of deletion

| Store | Check (must be zero unless retained by profile) |
|---|---|
| PostgreSQL | `count(*)` per workspace-owned table by `WorkspaceId`, run as a role that bypasses RLS (security finding 3) |
| OpenSearch | `_count` for `workspaceId` across every index and alias (shared and dedicated), on both step-4 passes |
| Object storage | `ListPrefix` per purged area returns no objects or versions; zero registry rows for purged areas |
| Keys | Key provider reports the workspace key as destroyed or pending destruction, with the date |
| Messaging | No queued or dead-lettered message carries the workspace ID (best effort; reported) |

### 8. Destruction certificate

Stored in an installation-level table and as a JSON document under `sys/certificates/{deletionId}/` (ADR-011),
signed with the audit checkpoint key once `E14-T03` exists, and hashed into the installation audit chain. It contains:
workspace ID, name and matter number; requester, approver and their reasons; request, approval, start and finish
times; retention profile; per-store counts before and after; key destruction status; **residuals**, i.e. data the
platform cannot purge (backups and PITR archives with their expiry dates per ADR-016, off-site copies per Q-40,
productions already delivered to other parties, retained records); software version. It never contains document
content or metadata values.

### 9. Crypto-shredding readiness

Every object records `KeyId` from day one (ADR-011 §6). Per-workspace keys are optional in the MVP. When enabled,
step 7 makes residual copies in backups unreadable and the certificate can say so. Without them, residual backups
are readable until they expire, and the certificate states that expiry date instead.

### Interim position (binding until this ADR is Accepted)

1. No API, job or admin tool deletes a workspace, document, artifact, production, privilege log or audit event. Data
   only accumulates.
2. `Workspace.Status` includes `Deleting`, and `Workspace.Epoch` exists; workers implement the §4 fencing rule.
3. Every stored object records `KeyId` and SHA-256 (ADR-011).
4. Audit tables grant the application role INSERT and SELECT only (ADR-013).
5. Redaction revisions and coding events are insert-only (ADR-012, §27).

## Consequences

- **Positive:** holds cannot be bypassed by any path, including garbage collection. Deletion is ordered so that search
  cannot resurrect data, is verifiable store by store and ends in a certificate counsel can attach to a
  return-or-destroy declaration. The default keeps the records a producing party is most often asked for later.
- **Negative / costs:** two people, a waiting period and a long run make deletion slow by design; an urgent
  protective-order deadline must plan for the waiting period (minimum 1 day in Full). Retained records and 7-year audit
  keep storage growing after matters end. Shared-partition deletes are slow at 10M+ documents until ADR-005 decides on
  dedicated partitions.
- **Follow-up work:** `E04-T02` (status set, epoch), `E06-T02`/`E06-T05` (fence points in workers), `E20-T01`
  (locks), `E20-T02` (run, verification, certificate), `E05-T09` (key destruction, rewrap), `E14-T01`/`E14-T03`
  (audit retention role, `Audit.Purged`), `E19-T07` (backup residual dates), `E07-T01` (index deletion paths).
- **Verification:** fault-injection test (`E18-T01`) deleting a workspace with in-flight bulk and index work, then
  checking all stores after 5 minutes; a test per destructive path that a lock yields 423 plus an audit event; a test
  that the same user cannot request and approve; certificate golden-file test.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Soft delete only (flag rows, never purge) | Fails protective-order destruction duties and GDPR erasure; not a deletion. |
| Immediate purge on single admin action | Spoliation risk; contradicts Q-23. |
| Hold as a workspace status | Overlapping holds from different matters or orders cannot be modelled or released independently. |
| Delete PostgreSQL first, then OpenSearch | Opens the resurrection window (A-05) and loses the inventory needed for verification. |
| Purge audit with the workspace | Contradicts Q-16 and removes the evidence that a wall held or that deletion was authorized. |
| Mandatory per-workspace keys | Strongest crypto-shredding, but key-provider operations for every installation; §34 makes them optional. |

## Baseline amendments

- *Proposed* — §15: replace the last paragraph with "Matter deletion follows ADR-014: preservation locks block every
  destructive path; deletion is two-person approved, fenced, verified per store and certified; audit is retained per
  Q-16 independently of matter deletion" (resolves A-21 and the deletion part of A-05).
- *Proposed* — §5: `Workspace` gains `Status`, `Epoch`, `ClosedAt`, `AuditRetainUntil` and `PreservationLock`.

## Links

- Baseline: §1, §2, §6, §15, §21
- Review findings: [review-findings.md](../plan/review-findings.md) A-05, A-21, §10.1
- Reviews: [attorney.md](../plan/reviews/attorney.md) finding 11, "Legal hold" and "Defensible matter deletion" tickets;
  [security.md](../plan/reviews/security.md) findings 6, 9
- Decisions: [decisions.md](../plan/decisions.md) Q-13, Q-16, Q-23, Q-40
