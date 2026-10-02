# ADR-006: OpenSearch index strategy

| Field | Value |
|---|---|
| **Status** | Proposed (structure decided; numeric shard-sizing defaults are tuned by `E18`/`E17` benchmarks without reopening this ADR) |
| **Date** | 2026-10-02 |
| **Owner (role)** | Search (OpenSearch) |
| **Deciders** | Lead architect; contributing: Security & Compliance, DevOps / SRE, eDiscovery Practitioner |
| **Tracking issue** | #31 (plan key `E02-T05`) |
| **Baseline sections** | [§8](../architecture/architecture-baseline.md#8-opensearch-architecture), [§23](../architecture/architecture-baseline.md#23-search-projection-baseline), §15, §24, §28, §34 |
| **Related** | ADR-001 (versions, watermark), ADR-004b (coding projection), ADR-007 (mapping), ADR-008 (query language), ADR-014 (deletion), ADR-019 R6; Q-02, Q-10, Q-11, Q-12, Q-13 |

## Context

§8 sketches three placements (shared index with routing, dedicated index, dedicated multi-shard). Application code
uses a logical search service, and Index Management decides physical placement. Reindexing is alias-based. §23 forbids
one filtered alias per workspace at high workspace counts and requires a **single search-service path that always
injects the authenticated workspace filter**, tested by cross-workspace attack tests. Q-02 sets the scale: ≤ 1,000
workspaces per installation, most under 1M documents, shared below **5M documents or 50 GB**, dedicated above, all
configurable. The security review requires the filter to be structurally non-removable and PIT/cursors to be bound to
(user, workspace). `E07-T01` (index manager), `E07-T05` (search service) and `E07-T11` (reindex) implement this ADR.

## Decision

### 1. Logical model

1. **R1** Application code addresses a **workspace**, never an index. `IIndexManager` (in `Opportunity.Search`)
   resolves `(WorkspaceId, purpose: Read | Write)` to a `Placement`: physical read alias, write targets (one, or two
   during a reindex), routing value, and `ProjectionGeneration`. Physical names never leave `Opportunity.Search`
   (architecture test, ADR-019 R6).
2. **R2** Placement is authoritative in PostgreSQL: `WorkspaceIndexPlacement(WorkspaceId, Kind {Shared, Dedicated},
   IndexAlias, Routing, ProjectionGeneration, State {Active, Building, Moving, Deleting}, PendingTarget, UpdatedAt)`.
   It is cached per process for ≤ 5 s and invalidated on change notification.

### 2. Placement tiers and thresholds

| Tier | Physical layout | Routing | When |
|---|---|---|---|
| **Shared** | One of a pool of shared indexes; `P_shared` primaries (Lite 1, Full 3) | `routing = WorkspaceId` (one shard per workspace) | Default for new workspaces below thresholds |
| **Dedicated** | One index per workspace, 1 primary | none (filter still injected) | ≥ `DedicatedDocs` **or** ≥ `DedicatedBytes`, or admin flag `DedicatedIndex` (large or sensitive matter) |
| **Dedicated multi-shard** | One index, `P = ceil(1.5 × projectedPrimaryBytes / TargetShardBytes)` primaries (1.5 = growth headroom), ≥ 2 | none | Projected primary store ≥ `MultiShardBytes`; outgrowing `P` triggers a reindex into a larger `P` (R13) |

Configuration (installation settings, validated at start-up):

| Setting | Default | Source |
|---|---|---|
| `DedicatedDocs` | 5,000,000 documents | Q-02 |
| `DedicatedBytes` | 50 GB primary store (estimated, see R5) | Q-02 |
| `MultiShardBytes` | 50 GB projected primary store | shard ≤ 50 GB guidance |
| `TargetShardBytes` | 30 GB (accepted range 10–50 GB) | tuned by `E18` benchmarks |
| `SharedIndexCloseAtBytes` | 70% of `P_shared × TargetShardBytes` | stops new assignments to that shared index |
| `MaxSharedIndexes` | 32 | bounds cluster shard count |

3. **R3** Routing a shared workspace to a single shard means a shared workspace can never exceed one shard. The
   dedicated threshold (50 GB) is therefore also the shard ceiling, and the two numbers MUST stay consistent: start-up
   validation rejects `DedicatedBytes > 50 GB` while `routing_partition_size = 1`.
4. **R4** Placement is evaluated (a) at import planning, from the load's row count and text sizes, so a workspace that
   will be large is placed dedicated **before** its first document is indexed; (b) after every import job; (c) nightly.
   Promotion is **one-way**. Dedicated workspaces are never demoted automatically.
5. **R5** Per-workspace bytes in a shared index cannot be read from `_stats`. They are estimated from PostgreSQL as
   `Σ min(TextLength, indexed cap) + Σ metadata bytes`, multiplied by a calibration factor (initial 1.3). The factor is
   recalibrated from dedicated-index `_stats` and benchmark results.
6. **R6** New workspaces are assigned to the open shared index with the most free capacity. A new shared index is
   created when all are closed. Shard skew inside a shared index (hash of `WorkspaceId`) is tolerated. A shard above
   45 GB raises an alert and makes its largest workspace a promotion candidate.

### 3. Naming and aliases

| Object | Physical index | Alias (stable) |
|---|---|---|
| Shared | `{prefix}-shared-{nnn}-g{G}` | `{prefix}-shared-{nnn}` |
| Dedicated | `{prefix}-ws-{workspaceId:N}-g{G}` | `{prefix}-ws-{workspaceId:N}` |

`{prefix}` is installation-configurable (default `opp`). `G` is the `ProjectionGeneration` (mapping template version,
ADR-007), and `{nnn}` is a zero-padded pool number. Aliases are **unfiltered** and exist per physical index family, not
per workspace. At most 1,000 dedicated aliases plus `MaxSharedIndexes`, so §23's "no per-workspace filtered aliases"
holds.

### 4. The single search path (§23)

1. **R7** Every read (search, count, aggregation, highlight, PIT open, `search_after` continuation, term-hit report)
   goes through `ISearchService`. It builds:
   `bool { filter: [ term workspaceId = <authenticated>, <security filters> ], must: [ <planned user query> ] }`.
   The user AST (ADR-008) is planned **only** into `must`/`must_not`/`should` **inside** this wrapper. It can never
   reach the outer `filter`, so OR and NOT cannot remove the workspace clause. `workspaceId` and `securityTags` are not
   addressable from the query language.
2. **R8** Shared placements also pass `routing = WorkspaceId`. Dedicated placements receive the same filter, so one
   code path serves both. Routing is an optimization; the filter is the isolation control.
3. **R9** The workspace id comes from the authenticated request context (membership verified, ADR-019), never from a
   client field. PIT ids and cursors are stored server-side, keyed by (user, workspace, PIT), and clients get an opaque
   handle. A handle used by another user or workspace returns 404.
4. **R10** Security filters (Q-11/Q-13: restricted classes, ethical walls) are added to the outer `filter` as
   defence in depth. Authoritative trimming is the per-page post-filter against PostgreSQL (Q-12, `E07-T05`). Search
   never makes the final access decision (§24).
5. **R11** In the Full profile the OpenSearch security plugin gives the application user index permissions on
   `{prefix}-*` only, and workers get write-only roles. Per-workspace document-level security is **not** used, because
   it needs per-workspace roles (§23). Dedicated-index DLS for sensitive matters is a post-MVP option.

### 5. Writes and alias-based reindex

1. **R12** Writers ask `IIndexManager` for write targets and always send the external version (ADR-001). In the
   `Building`/`Moving` states the targets are **both** the current and the pending physical index (dual-target).
   Version safety makes order irrelevant.
2. **R13** Reindex protocol (mapping change, shard re-sizing, DR rebuild, shared→dedicated move):

| Step | Action | Abort behaviour |
|---|---|---|
| 1 | Create target from template `G+1` (`refresh_interval: -1`, `number_of_replicas: 0`) | Delete target |
| 2 | Set placement `State = Building`/`Moving`, `PendingTarget`; writers start dual-targeting | Clear state; stop dual-target |
| 3 | Backfill with `IndexChunkTask(kind=Reindex)` over `DocumentId` ranges, reading current PostgreSQL state | Same |
| 4 | Restore refresh and replicas, wait for green, `_refresh` | Same |
| 5 | Validate: per-workspace doc count = PostgreSQL count; projection hash over sampled ranges (full hash for ≤ 1M docs); golden query parity | Same — old alias keeps serving |
| 6 | Switch: one `_aliases` request (remove old, add new), or for a move, update the placement row to the new `Kind`/alias | — |
| 7 | Old copy: for a move, `delete_by_query` on the shared index (term workspaceId + routing); for a generation switch, keep it read-only for 24 h, then delete | — |

   Watermark behaviour across step 6 is owned by ADR-001 (`E07-T08`).
3. **R14** Workspace deletion (ADR-014): after fencing, dedicated → delete index and alias; shared → `delete_by_query`
   with term and routing, then verify zero hits.

### 6. Initial index settings

| Setting | Lite | Full | Note |
|---|---|---|---|
| `number_of_replicas` | 0 | 1 | 0 during backfill |
| `refresh_interval` | 1 s | 1 s | Needed for ≤ 1 s coding→searchable (§17) |
| `routing_partition_size` | 1 | 1 | Shared only |
| `index.mapping.total_fields.limit` | 2,000 | 2,000 | Static slot mapping, ADR-007 |
| `index.max_result_window` | 10,000 | 10,000 | Deep paging via PIT + `search_after` only (Q-32) |

Codec, merge policy and bulk sizes are benchmark parameters (`E17`/`E18`) and are not fixed here.

## Consequences

- **Positive:** shard count stays bounded at 1,000 workspaces. Isolation depends on one structurally enforced code
  path plus tests, not on alias hygiene. Dedicated placement for large or sensitive matters is a configuration change
  that goes through the normal reindex machinery.
- **Negative / costs:** shared-tier bytes are estimates. Hash skew can put several mid-size workspaces on one shard.
  A promotion costs a full rebuild of that workspace from PostgreSQL. Dual-target writes double write load during a
  reindex.
- **Follow-up work:** `E07-T01`, `E07-T05`, `E07-T11`, `E05-T05` (isolation tests), `E20-T02` (deletion),
  `E18` (shard-size calibration).
- **Verification:** architecture test (no physical names outside `Opportunity.Search`); property test that 10K random
  ASTs yield an outer filter holding the authenticated workspace term (`E07-T05`); cross-workspace attack suite
  (crafted fields, saved searches, routing and cursor reuse, aggregations, highlights) with 0 leaks; the same search
  suite runs against shared and dedicated placements.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| One filtered alias per workspace | Rejected by §23 at high counts: cluster-state growth, and isolation becomes alias configuration. |
| One index per workspace always | 1,000+ indexes × shards × replicas exceed sensible shard counts for small matters (§8). |
| One global shared index | Unbounded shard growth; one hot workspace affects all; no per-index lifecycle. |
| `routing_partition_size > 1` for shared | Spreads a workspace over k shards and lifts the 50 GB ceiling, but complicates sizing; dedicated placement already covers big workspaces. |
| Per-workspace DLS roles as the primary control | Needs per-workspace roles (same scaling problem); the application filter plus PostgreSQL post-filter is authoritative anyway. |

## Baseline amendments

- *Proposed:* §8 — the shared tier is a **pool** of shared indexes. Shared workspaces route to a single shard, so the
  dedicated threshold is also the shard ceiling. Placement is recorded in PostgreSQL.

## Links

- Baseline: §8, §15, §23, §24, §28, §34
- Review findings: [review-findings.md](../plan/review-findings.md) §5.3, §5.4, §13.2
- Decisions: [decisions.md](../plan/decisions.md) Q-02, Q-10, Q-11, Q-12, Q-13, Q-32
- Related ADRs: [ADR-007](0007-search-mapping-strategy.md), [ADR-008](0008-minimal-query-language.md), [ADR-019](0019-layering-and-api-conventions.md)
