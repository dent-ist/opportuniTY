# PDP `AuthorizeMany` benchmark (E05-T02)

Acceptance target: `AuthorizeMany` for 100 document IDs ≤ 20 ms p95 at 1M documents (ADR-015 D5.1).

## How to run

```sh
OPPORTUNITY_PDP_BENCHMARK=1 OPPORTUNITY_PDP_BENCHMARK_REPORT=/tmp/pdp.txt \
  dotnet test --project tests/Opportunity.IntegrationTests -- \
  --filter-class Opportunity.IntegrationTests.Authorization.AuthorizeManyBenchmarkTests
```

The test (`AuthorizeManyBenchmarkTests`) migrates a fresh PostgreSQL 17 database (Testcontainers), loads 1,000,000
documents into one workspace, restricts ~10 % (half `Privileged`, half `AttorneysEyesOnly`), puts ~1 % behind a wall
naming the user, and gives the user a direct role and a group role. It then runs 2,000 calls (after 200 warm-up
calls) of `AuthorizeMany(Document.View)` on 100 random IDs, through the real `PostgresSecurityStateReader` as an RLS-bound
`opportunity_app` login, in two modes:

- **in-request**: the scope already ran PEP-1 (principal state cached), as for the search page post-filter (Q-12);
  one round trip of document attributes per call. The acceptance assertion uses this mode.
- **fresh scope**: each call also reads the principal state (first check of a request, worker chunk). Reported only.

## Results

| Date | Environment | In-request p50 / p95 / p99 | Fresh scope p50 / p95 / p99 |
|---|---|---|---|
| 2026-10-03 | Shared 4-vCPU sandbox, 15 GB RAM, PostgreSQL 17 container with default settings, load average ≈ 18–29 | 2.28 / **6.43** / 12.72 ms | 3.22 / 7.88 / 12.68 ms |

Both modes meet the 20 ms p95 target. An earlier run of the fresh-scope mode while the same host was saturated by other
test suites (load average ≈ 50 on 4 vCPUs) measured p50 7.98 ms / p95 41.34 ms. The tail is host contention, not
the query: the median stayed under 10 ms. Re-run on the reference environment (docs/benchmarks/reference-environments.md)
before using these numbers as a gate.
