# Nightly scale regression and performance regression guard

Plan key `E18-T06` (#153); test-strategy §3 tier T2 and §3 "Nightly regression guard"; decision Q-70 (no reference
hardware: the guard runs on GitHub-hosted runners and every gate is **relative**, never an absolute hardware gate).

| Piece | Where |
|---|---|
| Workflow (schedule + manual dispatch, not on PRs) | [`.github/workflows/scale-regression.yml`](../../.github/workflows/scale-regression.yml) |
| The run (import → index → search → bulk tag → export) | [`tests/Opportunity.IntegrationTests/Scale`](../../tests/Opportunity.IntegrationTests/Scale) (`Category=ScaleRegression`, opt-in) |
| Bundle writer, history, evaluator, report, dashboard | [`tools/Opportunity.Benchmarks/Regression`](../../tools/Opportunity.Benchmarks/Regression), CLI `opportunity-bench regress` / `dashboard` |
| Policy (tolerance, baseline, alert rule, metric rules) | [`tools/Opportunity.Benchmarks/scale-regression.yaml`](../../tools/Opportunity.Benchmarks/scale-regression.yaml) |
| Unit tests of the guard | `tests/Opportunity.Benchmarks.Tests/ScaleRegressionTests.cs` |

## 1. What one run does

`ScaleRegressionTests.Nightly_scale_regression_run` (skipped unless `OPPORTUNITY_SCALE_REGRESSION=1`):

1. **Generate** a seeded synthetic corpus with `Opportunity.DataGenerator.Corpus` (default enterprise profile, extracted
   text capped at median 2 KiB / p99 16 KiB / max 64 KiB; DAT + text, no natives or images) into a temp import share.
2. **System under test** (`ScaleWorld`): Testcontainers PostgreSQL, OpenSearch and RabbitMQ, and one real worker host
   built with `OpportunityWorkerHost` exactly like `Opportunity.Worker.All` (dispatcher with outbox/job relays and the
   search watermark ticker, import, indexing, bulk-coding and export workers; production defaults except a 1 s
   workspace refresh). Work flows through RabbitMQ. The "API side" makes the calls the API makes (import start,
   bulk-coding submission, export creation) and searches through the real search service with PDP checks.
3. **Import → index**: the import job until Completed (`stage.import`), then until no committed search work is above
   the visible watermark (`stage.import-searchable`). Index lag is sampled every second meanwhile.
4. **Search (idle)**: a fixed query mix, one or more queries per taxonomy bucket (term, phrase, field-filter, date-range,
   boolean, content-coding, expansion, grid-coding, proximity, wildcard — [query taxonomy](../benchmarks/query-taxonomy.md)),
   2 warm-up rounds then 40 measured rounds; HDR histograms per bucket plus the gated `simple`/`complex` aggregates.
   Then 20 coding → searchable probes (save, then poll the search service every 50 ms until the new value is found).
5. **Bulk tag** half the corpus (Responsive = true, Issues += Supply contract) while the same searches and the probe
   keep running (`bulk` scenario, role `bulk-load`); until the job is complete and its index tasks are visible
   (`stage.bulk-tag`).
6. **Export** every document (metadata DAT) through the export coordinator and chunks (`stage.export`).
7. **Correctness**: pipeline counters (import row errors, missing documents in PostgreSQL / the index / the export,
   bulk items unaccounted for or not searchable, search errors, probe timeouts), an outsider (signed in, no role) runs
   every query and must get nothing (`oracles.security`), and the shadow-ledger oracle reconciles the whole workspace
   (`oracles.shadowLedger`, [fault-injection.md §1](fault-injection.md)). Any counter above 0 fails the test.
8. **Bundle**: a schema-valid [E17-T03 result bundle](../benchmarks/reference-environments.md) (tier T2, suite
   `nightly/scale-regression`, validated before it is written) with the **environment manifest** captured from the
   live containers — host hardware, kernel and container runtime, `versions.env` and image digests, OpenSearch nodes
   with **JVM** version/heap/arguments, every index with its **shard layout**, PostgreSQL settings, RabbitMQ — the
   corpus manifest (**seed**, profile hash), `gates.yaml`, the resolved queries and scenario source, index-lag series,
   the ledger verdict, and `scale-run.json` with the effective **configuration**, the stage timings and the counters.
   Durability is recorded as relaxed (Testcontainers defaults), which the bundle rules allow only on tier T2 (Q-05).

Scenario validity is recorded honestly: the load generator runs in the same process and host as the system under
test, so a scenario whose host CPU reached the gates' 70% limit is marked invalid for §26 decisions. The nightly guard
compares trends only and never produces a §26 verdict.

## 2. The regression guard

`opportunity-bench regress --run <run dir> [--history history.jsonl] --out <dir> [--rebaseline] [--run-url URL]` reduces
the run to metrics (`search.<scenario>.<bucket>.p50|p95|p99` and `coding-to-searchable.<scenario>.*` in ms,
`index-lag.<scenario>.p95|max` in s, `stage.<stage>.docs-per-second|seconds`) and counters, then judges it:

- **Series.** Runs are compared only with runs of the same series: runner CPU model, architecture, logical cores and
  memory, corpus size and seed, and `ScenarioVersion`. A different machine or a changed workload starts a new series
  and is never compared with the old one (test-strategy §3). Bump `ScaleRegressionTests.ScenarioVersion` whenever the
  workload changes.
- **Baseline.** Median of the last 7 *accepted* runs of the series (pass or still collecting); with fewer than 3 the
  series is "collecting baseline" and nothing is judged. Regressed and correctness-failed runs never join the baseline,
  so a lasting regression keeps alerting until it is fixed or a maintainer starts a new baseline on purpose
  (dispatch with `rebaseline`).
- **Tolerance band.** A gated metric is *regressed* when it is more than 15% worse than the baseline (p95 up, or
  throughput down) **and** the absolute change exceeds the metric's noise floor (3 ms for search p95, 100 ms for the
  probe, 2 s for index lag). Gated: search p95 of every bucket idle and under bulk load, coding → searchable p95, index-lag
  p95, and the throughput of import, import-searchable, bulk tag and export. p50/p99, max lag and durations are recorded
  for the dashboard only.
- **Alert** when the same metric regresses on 2 consecutive runs of the series, or as soon as a correctness counter is
  above 0 on a single run. A single regressed night is reported as a warning in the job summary. A correctness failure
  is never noise (a lost document or a search leaking to an outsider is a real bug), so product decision Q-86 makes it
  alert on the first night — stricter than the acceptance criterion's "2 consecutive runs"; repeats are marked
  sustained.
- **Outputs**: `verdict.json`, `summary.md` (job summary and issue body), `history.jsonl` (90 days, one line per run)
  and `dashboard.html`. Exit 1 = alert (sustained regression or any correctness failure), 0 otherwise.

`opportunity-bench dashboard --history history.jsonl [--series KEY] [--days 90]` re-renders the dashboard: a
self-contained HTML page (no scripts) with 90-day trends of search p95 by bucket (simple and complex classes), simple vs
complex idle and under bulk load, coding → searchable p95, index lag p95 and throughput per stage; every point has a
tooltip, regressed runs are ringed, and each chart has a data table.

## 3. The workflow

Nightly (Mon–Sat, 25K documents) and weekly (Sunday, 100K documents, its own series) on `ubuntu-latest`, plus manual
dispatch (`documents`, `seed`, `rebaseline`). Steps: build → scale run → download the newest unexpired
`scale-regression-history` artifact produced on `main` → `regress` → upload the run bundle (30 days) and the
history/verdict/summary/dashboard (90 days, which is what keeps the 90-day trend) → on an alert on `main`, open or
comment on the open issue labelled `scale-regression` → fail the job on an alert or a correctness failure. Runs are
serialized (`concurrency`), because each reads the history the previous one wrote.

**Only on GitHub**: the schedule, the artifact-based history chain, the job summary and the issue. Locally the same
test and CLI run unchanged (below); the workflow YAML is checked with `actionlint` + `shellcheck`.

## 4. Run it locally

```bash
dotnet build tests/Opportunity.IntegrationTests
OPPORTUNITY_SCALE_REGRESSION=1 OPPORTUNITY_SCALE_DOCS=2000 OPPORTUNITY_SCALE_OUT=/tmp/scale/run \
  dotnet test --project tests/Opportunity.IntegrationTests --no-build -- --filter-trait "Category=ScaleRegression"
dotnet tools/Opportunity.Benchmarks/bin/Debug/net10.0/opportunity-bench.dll regress \
  --run /tmp/scale/run/<run id> --history /tmp/scale/history.jsonl --out /tmp/scale/guard
```

| Variable | Default | |
|---|---|---|
| `OPPORTUNITY_SCALE_REGRESSION` | — | `1` runs the test (otherwise skipped) |
| `OPPORTUNITY_SCALE_DOCS` | 2000 | corpus size |
| `OPPORTUNITY_SCALE_SEED` | 20261012 | corpus seed |
| `OPPORTUNITY_SCALE_SEARCH_ROUNDS` | 40 | measured rounds of the query mix (idle) |
| `OPPORTUNITY_SCALE_OUT` | temp (deleted) | parent directory of the result bundle |
| `OPPORTUNITY_SCALE_TIMEOUT` | 00:30:00 | per-stage wait limit |
| `OPPORTUNITY_SCALE_OPERATOR` | user name | recorded in the bundle |

The corpus and every temp directory are deleted when the test ends; only the bundle under `OPPORTUNITY_SCALE_OUT` stays.
On a 4-vCPU machine 1K documents take ~1.5 min and 10K ~3 min end to end (containers already started).

## 5. Limits

- Hosted runners are noisy and shared: the guard needs 3 accepted runs per series before it judges, smooths with the
  median, ignores changes under the noise floors and alerts only on regressions sustained over two runs.
- In-process topology on one host (§29 developer-regression shape, not the enterprise reference); no §26 decision is
  taken from these runs. The 1M weekly slot of test-strategy §3 needs a self-hosted/large runner (Q-70, #154).
- Fast-path loaders and corpus caching (E17-T05) do not exist yet: every run generates and imports its corpus.
