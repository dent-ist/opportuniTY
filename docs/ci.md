# Continuous integration

The PR pipeline is [`.github/workflows/ci.yml`](../.github/workflows/ci.yml). It implements tier **T1 — PR** of the [test strategy](testing/test-strategy.md#3-ci-tiers): it runs on every pull request, on pushes to `main`, on the merge queue and on manual dispatch. The wall-clock budget is **15 min**. The jobs run in parallel and each has a 15-minute timeout.

## Jobs

| Job (check name) | What it does | Artifacts |
|---|---|---|
| `Versions consistency` | `tools/ci/verify-versions.sh`: fails if `versions.env` disagrees with `global.json` (`sdk.version`), `src/Opportunity.Web/.nvmrc` or the `engines.node` major in `package.json` | — |
| `.NET build, unit and architecture tests` | Restores, then builds `Opportunity.slnx` in Release with `-warnaserror`. Runs `tests/Opportunity.UnitTests` and `tests/Opportunity.ArchitectureTests` (ADR-019 layering rules) | `test-results-dotnet` (TRX + Markdown) |
| `Integration tests (Testcontainers)` | Sets `vm.max_map_count=262144` for OpenSearch, then builds and runs `tests/Opportunity.IntegrationTests` against real containers on the runner's Docker daemon | `test-results-integration` (TRX + Markdown) |
| `Angular build and unit tests` | `npm ci`, `prettier --check`, `npm run build`, `ng test --watch=false` (Vitest) | `test-results-web` (JUnit XML) |
| `CI gate` | Passes only if every job above succeeded. This is the one check that branch protection requires | — |

Shared behaviour:

- **Versions.** Each job loads `versions.env` into the job environment. `actions/setup-dotnet` installs `DOTNET_SDK` and `actions/setup-node` installs `NODE`. Change versions in `versions.env` first, then update `global.json` / `.nvmrc` to match. The `Versions consistency` job enforces this.
- **Caching.** NuGet (`~/.nuget/packages`) is cached with `actions/cache`. The key hashes `global.json`, `Directory.Packages.props`, `Directory.Build.props` and all `*.csproj`. npm is cached by `actions/setup-node`, keyed on `package-lock.json`.
- **Test results.** .NET suites write TRX for download and Markdown, which is appended to the run's job summary on the PR's Checks tab. The Angular suite writes JUnit XML. Artifacts are kept for 14 days.
- **Concurrency.** A new push to a PR cancels that PR's previous run. Runs on `main` are never cancelled.
- **Permissions.** The workflow token is `contents: read` only. Checkout does not persist credentials.
- **Supply chain.** Third-party actions are pinned to full commit SHAs, with the release tag in a comment. Dependabot (`github-actions` ecosystem) proposes updates.
- **No retries.** Per the [flake policy](testing/test-strategy.md#8-flake-policy), no step retries tests.

## Reproduce locally

Prerequisites: the .NET SDK from `global.json`, Node from `.nvmrc`, and a running Docker daemon for integration tests.

```bash
tools/ci/verify-versions.sh

dotnet restore Opportunity.slnx
dotnet build Opportunity.slnx -c Release --no-restore -warnaserror
dotnet test --project tests/Opportunity.UnitTests         -c Release --no-build --report-xunit-trx
dotnet test --project tests/Opportunity.ArchitectureTests -c Release --no-build --report-xunit-trx
dotnet test --project tests/Opportunity.IntegrationTests  -c Release --no-build --report-xunit-trx  # needs Docker

# Linux hosts running OpenSearch containers also need:
sudo sysctl -w vm.max_map_count=262144

cd src/Opportunity.Web
npm ci
npx prettier --check .
npm run build
npx ng test --watch=false
```

`dotnet test --solution Opportunity.slnx` runs every suite at once (including `ScaleTests`, which CI does not run on PRs).

## Branch protection (manual, repository admin)

Require the **`CI gate`** status check on `main`, and require branches to be up to date before merging. Because only the gate is required, adding or renaming jobs never needs a branch-protection change. Add the new job to `ci-gate.needs` instead.

## Extension points

- **Security and supply-chain scanning (#24, `E01-T04`).** Add the jobs to `ci.yml`, or to a separate workflow with its own `permissions` (e.g. `security-events: write` on that job only), and add them to `ci-gate.needs`.
- **Container image build/sign/publish (#25, `E01-T05`).** Use a separate workflow triggered on `main`/tags, with `packages: write` / `id-token: write` scoped to that workflow. Do not widen this workflow's permissions.
- **OpenAPI diff and message-contract tests (L5).** Add steps in the `.NET` job, after the architecture tests, once the suites exist.
- **Cross-workspace security suite (`E05-T05`).** It lives under `tests/Opportunity.IntegrationTests/Security`, so it already runs in the integration job. **Fault/idempotency smoke (`E18-T01`).** Give it its own job if it would push the integration job past the budget, and add that job to `ci-gate.needs`.
- **Coverage.** Not collected yet. Add `Microsoft.Testing.Extensions.CodeCoverage` (.NET) and `@vitest/coverage-v8` (Angular), then upload Cobertura reports next to the test results.
- **`dotnet format --verify-no-changes`.** Enabled in the `.NET` job (`Check formatting`). Run `dotnet format Opportunity.slnx` locally to fix.
