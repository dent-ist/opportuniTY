# Benchmark reference environments and result bundles

| | |
|---|---|
| **Ticket** | E17-T03 (#141): reference environments and result-bundle schema |
| **Binding** | Baseline §17, §26, §29; decisions Q-03, Q-04, Q-05, Q-44; [test strategy](../testing/test-strategy.md) §3–§5 |
| **Code** | `tools/Opportunity.Benchmarks` (`opportunity-bench`), `deploy/benchmarks/` (`bench.sh`, Compose files) |
| **Schemas** | `tools/Opportunity.Benchmarks/schema/result-bundle.v1.schema.json`, `environment-manifest.v1.schema.json` |
| **Gates** | `tools/Opportunity.Benchmarks/gates.yaml` (status **draft** until PO + architect sign-off, Q-04) |

Every benchmark number in this project is a measurement on a stated environment, never a product claim (§17, §30).
This document defines the two environments results come from, the settings recorded with every result, and the
result bundle that carries them. Use synthetic corpora only (test strategy §7).

## 1. The two profiles

| | Developer regression | Enterprise reference |
|---|---|---|
| Manifest `profile` | `developer-regression` | `enterprise-reference` |
| Topology (§29) | 1 PostgreSQL, 1 OpenSearch node, 1 RabbitMQ, 1 object store, API, combined worker | 3-node OpenSearch, PostgreSQL primary + streaming replica, RabbitMQ, object store, 2 APIs, worker pools |
| Codified as | `deploy/docker-compose/compose.yaml` + `deploy/benchmarks/compose.bench.yaml` | `deploy/benchmarks/compose.reference.yaml` |
| Hardware | Whatever developer machine or self-hosted runner is available (Q-03); recorded, never assumed | Sponsored or rented hardware, shapes in §3. **Not provisioned yet** (Q-03): 10M/M4 waits for a sponsor |
| Tiers | T2 nightly regression, T3 1M comparative spike (Q-03) | T4 10M validation (`E18-T07`..`T09`); T3 reruns if a sponsor arrives first |
| Durability | Production-like by default; relaxed **only** for nightly (T2) and PR smoke (T1) runs, and only when recorded with a justification (Q-05, Q-46). T3 spike runs need production-like durability | Production-like, always. The validator rejects relaxed reference bundles (Q-05) |
| Absolute latency gates (index lag ≤ 2 min, coding→searchable ≤ 1 s) | **Comparative** (Q-44): a miss disqualifies a candidate only if it is also worse than the other candidates on the same machine in the same run | **Hard** |
| Relative gates (p95 degradation vs idle), correctness, security | Hard | Hard |

Runs on different machines are never compared directly: a new machine starts a new baseline series (test strategy
§3). The manifest makes "same machine" checkable (CPU model, cores, RAM, disks, kernel, runtime, versions).

## 2. Developer-regression environment

`./deploy/benchmarks/bench.sh up --profile dev` starts the Compose developer profile (E19-T03) as project
`opportunity-bench-dev` with `compose.bench.yaml` on top. The override changes no topology; it pins every setting
that moves a benchmark number so the manifest never depends on image defaults:

| Component | Setting (override) | Default here | Why it is pinned |
|---|---|---|---|
| PostgreSQL | `fsync`, `synchronous_commit`, `full_page_writes` | `on`, `on`, `on` | Q-05 production-like durability |
| PostgreSQL | `wal_level`, `max_wal_size`, `checkpoint_timeout`, `checkpoint_completion_target` | `replica`, `8GB` (`BENCH_PG_MAX_WAL_SIZE`), `15min`, `0.9` | Checkpoint storms distort bulk-load windows |
| PostgreSQL | `shared_buffers`, `effective_cache_size`, `work_mem`, `maintenance_work_mem`, `max_connections` | `2GB`, `6GB`, `32MB`, `512MB`, `200` (`BENCH_PG_*`) | Memory sizing; set for a 16 GB host and recorded |
| PostgreSQL | `random_page_cost`, `track_io_timing`, `shm_size` | `1.1`, `on`, `2g` | SSD planner costs; I/O timing for §29 Phase 2 analysis |
| OpenSearch | `OPENSEARCH_JAVA_OPTS` | `-Xms4g -Xmx4g` (`BENCH_OPENSEARCH_HEAP`) | Heap size dominates GC pauses |
| OpenSearch | `bootstrap.memory_lock`, `ulimits` memlock/nofile | `true`, unlimited / 65536 | No swapping of the heap |
| Worker | `Workers__Enabled` | `all` (combined Lite worker, Q-39) | Recorded as the worker pool |
| Object store | `ObjectStorage__Provider` | `FileSystem` (named volume); `--profile s3` of the dev stack for SeaweedFS | Recorded as `objectStore` |

Variants, all recorded in the manifest:

- `--relaxed` adds `compose.bench-relaxed.yaml` (`fsync=off`, `synchronous_commit=off`, `full_page_writes=off`) for
  nightly runs only. `capture-env` then lists each deviation and refuses to write a manifest without
  `--relaxed-durability "<why>"`. Index-level relaxations (`index.translog.durability: async`) are detected the same way.
- `--no-ulimits` (`compose.no-ulimits.yaml`) for hosts that cannot raise rlimits (rootless Docker, sandboxed CI):
  `bootstrap.memory_lock=false`. Fine for trends, not for numbers anyone quotes.

Minimum host for the 1M spike (Q-03): 8 physical cores, 32 GB RAM, local NVMe with 300 GB free, Linux with
`vm.max_map_count ≥ 262144`, nothing else running. The load generator should be a second machine; if it shares the
host, its CPU must stay below 70% (gates `policy.validity`) and the manifest must list both roles.

## 3. Enterprise reference environment

`compose.reference.yaml` is the executable definition (`./bench.sh up --profile reference`). On one large host every
service runs there; on the real multi-host reference, each tier runs on its own host(s) with the same service
definitions (or translated to the target's IaC, see §8). Image tags and digests always come from `versions.env`.

### 3.1 Topology

| Tier | Count | Configuration |
|---|---|---|
| OpenSearch data + cluster-manager nodes | 3 | `os1..os3`, one cluster, heap `REF_OPENSEARCH_HEAP` = **31g** on reference hardware (half of 64 GB, below the compressed-oops limit), memory lock on, nofile 65536. Indices: ADR-006 placement; benchmark indices with `number_of_replicas: 1`, `translog.durability: request`, `refresh_interval: 1s` (unless the candidate under test defines otherwise, which is then recorded). Capture after the ADR-006 R13 restore step (replicas and refresh restored, cluster green). |
| PostgreSQL primary | 1 | Production-like settings shared with the replica (`x-postgres-args`): `fsync=on`, `synchronous_commit=on`, `full_page_writes=on`, `wal_level=replica`, `wal_compression=lz4`, data checksums on, `shared_buffers=16GB`, `effective_cache_size=48GB`, `max_wal_size=32GB`, `checkpoint_timeout=15min`, `effective_io_concurrency=200`, `max_connections=400`, `huge_pages=try` (all `REF_PG_*`) |
| PostgreSQL replica | 1 | Streaming replica cloned with `pg_basebackup -R` (`application_name=pg_replica`). **Asynchronous by default** (`REF_PG_SYNC_STANDBY=''`, matching the RPO ≤ 5 min target in §17); `'FIRST 1 (pg_replica)'` makes it synchronous. Recorded as `synchronous_standby_names` and `pg_stat_replication.sync_state` |
| RabbitMQ | 1 | Version from `versions.env`, management + Prometheus plugins; durable/quorum queues (non-durable work queues are reported as deviations) |
| Object store | 1 | SeaweedFS (ADR-020) on its own volume, or the cloud provider's S3 in the same region (`--object-store S3=<implementation>`) |
| API | 2 (`REF_API_INSTANCES`) | `DOTNET_ENVIRONMENT=Production`; the load generator spreads requests over both (no proxy in the measured path) |
| Workers | dispatcher 1, indexing 4, bulk-coding 2, import 2, rendering/export/production 1 (`REF_*_WORKERS`) | Same worker image, one type per pool via `Workers__Enabled` |
| Load generator | 1 host | k6 (E17-T04), separate host, CPU < 70% for a valid run |

### 3.2 Instance shapes

Shapes are an example to make the reference reproducible, not a vendor choice. Keep every node in one zone with a
low-latency network (cluster placement group or equivalent, ≥ 10 Gbit/s).

| Role | AWS example | Bare-metal equivalent |
|---|---|---|
| OpenSearch node ×3 | `i4i.2xlarge`: 8 vCPU, 64 GiB, 1 × 1,875 GB local NVMe | 8–16 cores, 64 GB RAM, 2 × 1.92 TB enterprise NVMe (RAID 0 or JBOD data paths; replicas provide redundancy) |
| PostgreSQL primary, replica | `r7i.2xlarge`: 8 vCPU, 64 GiB, gp3 1 TB at 16,000 IOPS / 1,000 MB/s (or `i4i.2xlarge` local NVMe) | 8–16 cores, 64 GB RAM, 2 × 1.92 TB enterprise NVMe in RAID 1 with power-loss protection |
| RabbitMQ + object store | `m7i.xlarge`: 4 vCPU, 16 GiB, gp3 2 TB (or S3 in-region) | 4–8 cores, 16–32 GB RAM, 2+ TB SSD |
| API + worker pools | 2 × `c7i.2xlarge`: 8 vCPU, 16 GiB | 2 × 8–16 cores, 32 GB RAM |
| Load generator | `c7i.2xlarge` | 8–16 cores, 16 GB RAM |

10M documents with the §29 profile (mean extracted text ≈ 70 KB) is ≈ 0.7 TB of raw text; with one replica, plan
≈ 2 TB of OpenSearch storage across the three nodes, and record actual `store.size` in the manifest.

### 3.3 Host settings

On every host: Linux with a current LTS kernel, `vm.max_map_count=262144`, `vm.swappiness=1` (or swap off),
transparent huge pages `madvise` or `never`, CPU governor `performance`, NTP synchronised clocks, no co-tenant load.
`capture-env` records kernel, distribution, `max_map_count`, swappiness, THP, governor and disk scheduler, so a
deviation shows up in the manifest even if nobody noticed it.

### 3.4 Status

The reference is fully specified but not standing anywhere (Q-03). In the sandbox E17-T03 was built in, the PostgreSQL
primary/replica, RabbitMQ and SeaweedFS tiers of `compose.reference.yaml` were brought up and captured
(streaming replica reported as `pg_replica`/`async`, with production durability) using dry-run memory settings; the
3-node OpenSearch tier needs `nofile ≥ 65535`, which that sandbox does not allow, so it is exercised only on real hosts.

## 4. What every result records

`opportunity-bench capture-env` writes the environment manifest (`environment-manifest.v1.schema.json`); the
result bundle embeds it. §17/§29 requirement → where it lives:

| Requirement | Manifest / bundle field | Source |
|---|---|---|
| Hardware | `environment.hosts[]`: CPU model, vendor, sockets, physical/logical cores, max MHz, ISA flags, hypervisor flag, governor; RAM, swap, THP; each block device with type (nvme/ssd/hdd/virtual), size, model, scheduler and which device backs the container runtime; platform (bare-metal/vm/cloud, vendor, instance type, region) | `/proc/cpuinfo`, `/proc/meminfo`, `/sys/block`, `/sys/class/dmi`, `/proc/self/mounts`; `--cloud-provider/--instance-type/--region` when not detectable |
| OS and runtime | `hosts[].os` (kernel, distribution, `max_map_count`, swappiness), `hosts[].containerRuntime` (Docker/Compose version, storage driver, cgroup version/driver, CPUs, memory) | `/proc/sys`, `/etc/os-release`, `docker info` |
| Versions | `software.versionsEnv` (whole `versions.env` + SHA-256), `software.images` (pinned tag + digest), `containers[].image` (reference, image id, repo digests) and `containers[].pin.matches`, `software.applicationImages`, .NET runtime | `versions.env`, `docker inspect` |
| JVM settings | `opensearch.nodes[].jvm`: version, VM, heap init/max, full JVM input arguments, GC collectors; container `OPENSEARCH_JAVA_OPTS` | `_nodes/jvm,os`, `docker inspect` |
| OpenSearch settings | `opensearch.clusterSettings` (persistent, transient, selected defaults: allocation, indexing buffer, caches, breakers, thread pools) | `_cluster/settings?include_defaults` |
| Shard topology | `opensearch.indices[]`: primaries, replicas, refresh interval, translog durability/sync interval, codec, docs, store bytes, and every shard copy with its node and state | `_settings`, `_cat/indices`, `_cat/shards` |
| PostgreSQL settings | `postgres[].settings`: the benchmark-relevant `pg_settings` rows (about 55) (value, unit, source) covering durability/WAL, checkpoints, memory, parallelism, planner, autovacuum; `role`; replication state | `pg_settings` (the `SHOW ALL` subset), `pg_is_in_recovery()`, `pg_stat_replication` |
| Durability | `postgres[].durability` (fsync, synchronous_commit, full_page_writes, wal_level, wal_sync_method, synchronous_standby_names, data_checksums), index translog durability, queue durability; `durability.mode`, `deviations[]`, `justification` | Computed by `DurabilityPolicy` from the captured settings |
| Broker, store, workers | `rabbitmq` (version, Erlang, nodes, queues with type/durable), `objectStore`, `workers[]` (type, instances, concurrency) | management API, containers, `--worker` |
| Corpus seed/configuration | `corpus`: generator name/version, seed, profile name and hash, document count, load path, cache key, text cap; the `corpus-manifest.json` itself is bundled | E17-T01 manifest |
| Workload scripts | `workload`: name, version, query-taxonomy version, query seed, model, mix, offered rate, reviewers/think time; every script bundled with its SHA-256 and a combined `workloadSha256` | E17-T04 |
| Gates | `gates`: path, SHA-256, version, draft/frozen; `gates.yaml` itself is bundled | this repo |
| Run metadata | `run`: git SHA, dirty flag, ref, start/end UTC, operator, tier, suite, candidate, repetition `index of N`, cold/warm cache, tool version | `git`, the harness |

Credentials never enter a manifest: connection strings are passed as `env:VAR`, only `host:port/database` is
recorded, container environment is allow-listed (JVM options, cluster settings, worker selection, storage provider)
and anything named like a password, secret, token or key is dropped; command-line `*password*=` values are redacted.

## 5. Result bundle

One run directory per repetition of one suite, named by `runId`
(`<yyyyMMdd't'HHmmss'z'>-<suite>-<candidate>-r<n>`):

```text
artifacts/bench/<runId>/          # artifacts/ is git-ignored
  bundle.json                     # result-bundle.v1 (validated)
  corpus-manifest.json            # from the generator (seed, profile hash, distributions)
  gates.yaml                      # the gates file the run used (hash in bundle.json)
  workload/*.js                   # k6 scripts as run
  metrics/*.jsonl                 # time series, e.g. index lag at 1 s resolution
```

`bundle.json` holds: `run`, the complete `environment` manifest, `corpus`, `workload`, `gates`, `scenarios[]` and
`oracles`. Each scenario (`idle-baseline`, `bulk-load`, `bulk-ceiling`, `fault-window`, `calibration`, `smoke`) has
its time window, warm-up/drain seconds, validity (dropped-iteration ratio, load-generator CPU, reasons), per query
class (`simple`, `complex` and taxonomy buckets) request/error counts and latency (count, min, p50, p90, p95, p99,
p99.9, max, mean in µs **plus the raw HDR histogram**, V2 compressed base64, interoperable with HdrHistogram's
Java/Go/JS ports), the Q-12 post-filter cost, throughput (queries/s, bulk docs/s committed and reflected in the
watermark, coding ops/s, import docs/s), index lag (resolution, samples, max, p95, series file) and the
coding→searchable probe (samples, poll interval, probe traffic share, HDR latency). `oracles` carries the
shadow-ledger counters (`staleOverwrites`, `versionRegressions`, `missingDocs`, `valueMismatches`), security
(`unauthorizedRetrievals`) and idempotency (`trials`, `idempotentTrials`, ratio); `not-run` is explicit and counts as
unproven. A complete example is `tools/Opportunity.Benchmarks/schema/examples/bundle.example.json` (synthetic numbers).
The shadow-ledger oracle (E17-T07) writes `verdict.json` (`shadow-ledger-verdict.v1.schema.json`); its `shadowLedger`
object is copied into `oracles.shadowLedger` unchanged (see [fault-injection.md](../testing/fault-injection.md)).

### 5.1 Completeness rules (`opportunity-bench validate`)

A bundle that fails any rule is rejected by `validate`, by the writer (`BundleWriter.Write`) and by `publish`:

1. Schema-valid against `result-bundle.v1.schema.json`. The environment must include containers, PostgreSQL, OpenSearch
   (with at least one index), RabbitMQ, object store and workers: a partial capture is not a complete manifest.
2. Every listed file exists with the recorded size and SHA-256; the corpus manifest, gates file and every workload
   script are bundled and their hashes match the references; the corpus manifest's seed, profile hash, generator
   and document count match `corpus`.
3. Percentiles agree exactly with the bundled HDR histograms (the histogram is authoritative).
4. Durability deviations are recomputed from the recorded settings and must equal the declared ones; relaxed
   durability needs a justification and is accepted only on `developer-regression` bundles of tier T2 (nightly);
   it is rejected for T1, T3 (the 1M comparative spike) and T4, and on `enterprise-reference` (Q-05).
5. T3/T4 runs need a **frozen** gates file (Q-04), a clean git tree and `repetition.of ≥ 3`; T4 runs only on
   `enterprise-reference`, which must have a PostgreSQL replica and 3 OpenSearch nodes.
6. Scenarios marked valid must respect the gates' validity limits (dropped iterations ≤ 0.5%, load-generator CPU <
   70%); invalid ones must state reasons. Oracle status must agree with its counters.
7. Images that do not match their `versions.env` digest are errors on the reference and warnings on the developer
   profile. `--gates <file>` additionally requires the bundle to have used exactly that gates file.

### 5.2 Repetitions and cache state

At least 3 repetitions per scenario for T3/T4 (one bundle each, `repetition.index` of `repetition.of`); relative
gates must hold in every repetition (`gates.yaml` `policy.repetitions`). Each bundle states `cacheState`: `cold`
(services restarted and OS caches dropped before the run) or `warm` (after a warm-up pass). Never mix cold and warm
repetitions in one comparison.

## 6. Where bundles are published

1. **Local**: the harness writes run directories under `artifacts/bench/` (git-ignored); environment captures from
   `bench.sh capture` land there too. Results never go into git.
2. **Object storage**: `opportunity-bench publish --bundle <run dir> --dest <root>` validates the bundle, then copies it
   write-once to `<root>/<profile>/<runId>/` and appends one line to `<root>/index.jsonl` (run id, location, bundle
   SHA-256, profile, tier, suite, candidate, repetition, git SHA, start, gates hash and status). Republishing the same
   bundle is a no-op; different content under an existing run id is refused. `<root>` is a directory: a mounted
   bucket, or a staging directory synced to the project's benchmark bucket (`opportunity-benchmarks`, one prefix per
   profile) with `aws s3 sync` / `rclone sync`. Bucket retention: keep every T3/T4 run for the life of the decision
   it supports; nightly T2 runs for 90 days.
3. **Git-indexed summary**: for decision runs (T3/T4) the `index.jsonl` lines of the runs a decision rests on are
   committed with the report that states the verdict (E17-T08/E18), so the git history names the exact bundles (by
   SHA-256) and anyone can re-run the evaluator on them.

## 7. Gates (`gates.yaml`)

`tools/Opportunity.Benchmarks/gates.yaml` encodes the eight §26 gates with thresholds, the bundle paths each gate
reads, and the Q-04/Q-44 policy (tighten-only, correctness before performance, ≥ 3 repetitions, validity limits,
calibration CV ≤ 5%, offered rate = calibrated maximum of the weakest candidate, material advantage ≥ 20% p95 or
≥ 1.5× bulk throughput in every repetition, material failure at 10M, absolute gates comparative on
`developer-regression` and hard on `enterprise-reference`). `opportunity-bench gates` checks the file, checks every
gate path against the result-bundle schema (including allowed selector values) and prints its SHA-256.

The file stays `status: draft`. Freezing it is a governance step, not a code change: the product owner and the
lead architect fill in `signOff` and set `status: frozen` in one reviewed commit before the comparative run (E18-T02);
afterwards a change may only tighten a threshold. The gate evaluator (E17-T08) reports the file's hash.

## 8. Commands

```bash
# developer regression: provision, capture, destroy
deploy/benchmarks/bench.sh up --profile dev            # add --relaxed (nightly T2 / PR smoke T1 only), --no-ulimits (constrained hosts)
deploy/benchmarks/bench.sh capture --profile dev       # -> artifacts/bench/environment-developer-regression-<utc>.json
deploy/benchmarks/bench.sh down --profile dev          # removes containers and volumes

# enterprise reference (sponsor hardware; REF_* variables size it)
deploy/benchmarks/bench.sh up --profile reference      # --infra-only skips API/workers
deploy/benchmarks/bench.sh capture --profile reference
deploy/benchmarks/bench.sh down --profile reference

# harness
dotnet run --project tools/Opportunity.Benchmarks -- capture-env --help
dotnet run --project tools/Opportunity.Benchmarks -- validate --bundle artifacts/bench/<runId> --gates tools/Opportunity.Benchmarks/gates.yaml
dotnet run --project tools/Opportunity.Benchmarks -- publish  --bundle artifacts/bench/<runId> --dest /mnt/opportunity-benchmarks
dotnet run --project tools/Opportunity.Benchmarks -- gates
dotnet run --project tools/Opportunity.Benchmarks -- schema --name result-bundle

# shadow-ledger oracle (E17-T07): sample during a run, reconcile after quiescence, write verdict.json (exit 1 on any counter > 0)
dotnet run --project tools/Opportunity.Benchmarks -- ledger --postgres env:PG --opensearch http://127.0.0.1:9200 \
  --workspace <id> --index <alias> [--routing <workspace id>] --sample-for 00:10:00 --scope touched --out artifacts/bench/<runId>/verdict.json

# workloads (E17-T04, query-taxonomy.md §7)
dotnet run --project tools/Opportunity.Benchmarks -- queries --corpus-manifest <corpus>/corpus-manifest.json --seed 42 --out queries.json
QUERIES=queries.json tools/Opportunity.Benchmarks/k6/run.sh mixed.js artifacts/bench/<k6-run>
dotnet run --project tools/Opportunity.Benchmarks -- ingest-k6 --raw artifacts/bench/<k6-run>/k6-raw.json.gz ...
dotnet run --project tools/Opportunity.Benchmarks -- stub-api --queries queries.json   # fake API for script validation
```

`capture-env` on bare-metal services (no Docker): `--no-docker --postgres primary=env:PG --opensearch https://user:pass@os:9200 --rabbitmq-management env:RMQ --object-store S3=<impl> --worker indexing=4`.

## 9. Not in this ticket

- Filling the remaining scenario data: coding→searchable probe and index-lag series (E17-T06), gate evaluator and
  report (E17-T08). The shadow-ledger oracle (E17-T07) exists (`ledger`, above). They write bundles through `BundleWriter`, which already enforces
  §5.1. The k6 workloads and `ingest-k6` (E17-T04) are described in [query-taxonomy.md](query-taxonomy.md).
- Cloud IaC (e.g. Terraform for the shapes in §3.2) waits for a sponsor (Q-03); the Compose definition is the
  reference until then.
- Direct S3 upload from `publish`; today it publishes to a directory that is mounted or synced.
