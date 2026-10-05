# Shadow-ledger oracle and fault-injection matrix

Plan keys `E17-T07` (#145) and `E18-T01` (#148); test-strategy layer L6 and the §26 rows "0 stale-version
overwrites", "100% idempotent in the fault-injection suite" and "0 unauthorized protected-resource retrievals".

## 1. Shadow-ledger oracle (E17-T07)

Code: [`tools/Opportunity.Correctness/ShadowLedger`](../../tools/Opportunity.Correctness/ShadowLedger) (tooling only;
`src/` must never reference it, architecture test). CLI: `opportunity-bench ledger` (see
[reference-environments.md §8](../benchmarks/reference-environments.md#8-commands)). Tests:
`tests/Opportunity.IntegrationTests/Faults/ShadowLedgerOracleTests.cs`, `tests/Opportunity.Benchmarks.Tests/ShadowLedgerVerdictTests.cs`.

**Capture: CodingEvent, not logical decoding.** `CodingEvent` is written in the same transaction as every coding change
and carries the `DocumentVersion` that change produced (§27, ADR-010 §5.4), so it already is the committed ledger. Logical
decoding would need `wal_level=logical`, a replication slot and replication privileges (the developer and
Testcontainers topologies run `wal_level=replica`), and an abandoned slot pins WAL. The ledger tails `coding_event`
with a 30 s overlap window (de-duplicated by event id, so late commits inside the window are not missed); the full
reconciliation reads touched documents from PostgreSQL itself, so it never depends on what the tail saw. Changes that
bump `DocumentVersion` without coding (deletes, overlays) are covered with `--scope workspace` or `ExtraDocuments`.

**Continuous sampling** (while the run is live, every 50–100 ms): a seeded reservoir of ≤ 10,000 touched documents is
read from OpenSearch with real-time `_mget` *first*, then their current version from PostgreSQL; PostgreSQL can only
have moved forward in between, so `os.projectionVersion ≤ pg.DocumentVersion` must hold. It also asserts
`_version == projectionVersion` (ADR-001 §2; a write that bypassed `version_type=external` breaks it) and that no
document's indexed version ever decreases or vanishes while live.

**Full reconciliation** after quiescence (every work record Applied/Failed): keyset pages of 1,000 documents, one
REPEATABLE READ snapshot and one `_mget` each, so memory is bounded by the batch whatever the corpus. For 100% of
touched documents: `projectionVersion == DocumentVersion`, every projected coding value equal to PostgreSQL (compared in
a canonical form written independently of the product's projection builder), deleted documents absent.

**Counters** (`verdict.json`, schema `tools/Opportunity.Benchmarks/schema/shadow-ledger-verdict.v1.schema.json`; its
`shadowLedger` object is the result bundle's `oracles.shadowLedger` unchanged):

| Counter | Meaning |
|---|---|
| `staleOverwrites` | distinct documents whose index ever held an older state than one already indexed, or older than PostgreSQL after quiescence, or a version PostgreSQL never committed, or a write that bypassed external versioning, or a resurrected delete |
| `versionRegressions` | observations where a document's indexed version went down (or it vanished while live) |
| `missingDocs` | live touched documents absent after quiescence |
| `valueMismatches` | same version, different coding values (or a foreign `workspaceId`) |
| `versionConflictRejections` | 409 no-ops (`opportunity.search.stale_version_rejections`) during the run; informational |

**Sensitivity.** The test-only switch `FaultFlags.UnversionedProjectionWrite` makes the projection writer omit
`version_type=external`. It exists only in builds with `OPPORTUNITY_FAILPOINTS` (every build except
`dotnet publish -c Release`, which every shipped image uses), is read only through a test-registered `IFaultInjector`,
and no configuration key reaches it (architecture test `FailpointBuildTests`). In 10 seeded trials the canonical
ADR-001 §9 race is played (worker A reads v, stalls; v+1 is indexed; A writes) with only A's write unversioned: the
oracle reports every raced document as a stale overwrite in 10/10 trials; the versioned control run passes and counts
one conflict per race.

**Scale (Q-70).** The 1M-document ≤ 15 min target is hardware-bound and is not gated. Measured at CI scale (20,000
touched documents, batch 1,000, Testcontainers on a shared 4-vCPU developer VM): see §3. The work is linear in documents
(one keyset page query, one snapshot of versions/values and one `_mget` per 1,000), so 1M documents take
1,000,000 ÷ measured rate; batches can also be split by key range across parallel readers if a reference machine
needs it.

## 2. Fault-injection matrix (E18-T01)

Code: `tests/Opportunity.IntegrationTests/Faults` (`FaultMatrix`, `FaultWorld`, `FaultTrial`, `MatrixFaultInjector`,
`OpenSearchFaultHandler`). The matrix runs in its own xUnit collection with its own PostgreSQL, OpenSearch and RabbitMQ
containers, each behind Toxiproxy, against the **real** dispatcher host (outbox/task/chunk relays, lease sweeper,
search-work recovery) and the **real** worker host (bulk-coding, chunk-index and interactive-index modules, composed as in
`Opportunity.Worker.All`, test timings).

**Failpoints** (`IFaultInjector`, `src/Opportunity.Application/Faults`): compiled only with `OPPORTUNITY_FAILPOINTS`,
so release builds contain neither hooks nor call sites; without a registered injector each is a null check. The matrix
uses `job-chunk.after-claim` (before the PostgreSQL commit), `job-chunk.after-commit` (after commit, before the RabbitMQ
ack), `index-task.before-bulk`, `index-task.after-bulk` (after the OpenSearch ack; mid-chunk when it is the first of
several `_bulk` requests of the task), `index-task.after-applied` (before the ack), `outbox.before-bulk`,
`outbox.after-bulk` and `relay.after-publish` (dispatcher: broker confirm received, rows not yet marked Dispatched).

**Fault types**: kill -9 (the host is torn down, its unsettled deliveries return to the broker, a fresh host takes over;
the delivery's handler records nothing more), duplicate delivery, redelivery after ack timeout (the lease is expired and
the copy delivered while the original stalls, then the original resumes with a stale fencing token), out-of-order
delivery (held while later work overtakes it), OpenSearch 429 and 503 (whole `_bulk` requests), partial bulk failure
(a seeded subset of actions withheld from the cluster and answered with 429/503 items), OpenSearch partition
(Toxiproxy) and pause (`docker pause`), PostgreSQL partition and restart, RabbitMQ partition and node restart
(`rabbitmqctl stop_app/start_app`). **PostgreSQL failover is not available** in the developer/Testcontainers topology
(one primary, no replica); a restart stands in for it until a replicated topology exists.

**Workloads**: `Mixed` (a bulk coding job in small chunks plus interactive edits of plain and security-affecting fields
on overlapping documents), and `Bulk` / `Interactive` alone for the kill cells.

**Pass criteria per trial**: shadow-ledger verdict passed; final PostgreSQL coding equal to the no-fault reference
(bulk values, interactive edits winning per field under Q-07, the restricted document excluded for the initiator); no
duplicate `CodingEvent` per idempotency key, document and field, and at most one job event per document field; every
`JobChunk` Committed, `IndexChunkTask` and `SearchOutbox` row Applied, job counters adding up; authorization: the
restricted document never reaches a reviewer without its grant, callers of another workspace see nothing, and the PDP
still decides from PostgreSQL. "Idempotent %" = passing trials ÷ trials and must be 100%.

**Runs.** Pull requests: the `FaultMatrix.PullRequest` subset (every fault type and every failpoint, kill at every
failpoint), one trial per cell, inside the CI integration job (≈ 2–3 min). Nightly: `.github/workflows/fault-matrix.yml`,
every cell, five trials each, one parallel job per fault type, then a summary job with Idempotent % and the
bundle-shaped `oracles.json`; not in `ci-gate.needs`. `OPPORTUNITY_FAULT_CELLS` restricts a local run to matching cells.

**Seeds and replay.** Trial seeds derive from the base seed, the cell and the trial number. A failure prints its seed
and the replay command, e.g.
`OPPORTUNITY_FAULT_SEED=<seed> OPPORTUNITY_FAULT_CELLS='Crash@index-task.after-bulk/Mixed' dotnet test --project tests/Opportunity.IntegrationTests -- --filter-trait "Category=FaultMatrix"`.
The seed fixes the workload and the fault (which hit, windows, withheld items); thread interleaving is not controlled.

**ADR-004 candidates.** Only the interim Candidate A exists (Q-70). The candidate is pluggable:
`OPPORTUNITY_FAULT_CANDIDATE` (workflow input `candidate`) selects the oracle's projection layout
(`ProjectionLayouts`); a candidate without a layout — and a product implementation — is refused with that message.
Adding B/C/D means registering their layout and running the workflow with the input.

## 3. Measurements (2026-10-05, shared 4-vCPU / 15 GB developer VM, Testcontainers, `fsync=off`; informational, Q-44/Q-70)

| What | Result |
|---|---|
| Full reconciliation, 100,000 touched documents, batch 1,000 | 11.6 s, **≈ 8,600 docs/s**, 100 batches, all counters 0 |
| Extrapolation to 1M documents (linear, one reader) | ≈ 1,000,000 ÷ 8,600 ≈ **2 min** (target ≤ 15 min, to be confirmed on reference hardware with durable PostgreSQL) |
| CI-scale default (`OPPORTUNITY_LEDGER_RECONCILE_DOCS`, 20,000) | runs in the integration job |
| Oracle sensitivity, unversioned stale write | detected in **10/10** seeded trials |
| Fault matrix, PR subset (20 cells × 1 trial) | 20/20 idempotent, ≈ 2.5 min including container start |
| Fault matrix, full (109 cells × 1 trial) | 109/109 idempotent after two test-expectation fixes (no product defect found); 2–36 s per trial depending on fault and machine load (PostgreSQL and RabbitMQ restarts are the slowest) |

The nightly workflow shards the cells by fault type (13 parallel jobs) so five trials per cell stay within the 75-minute
L6 budget.
