# Continuous integration

The PR pipeline is [`.github/workflows/ci.yml`](../.github/workflows/ci.yml). It implements tier **T1 — PR** of the [test strategy](testing/test-strategy.md#3-ci-tiers): it runs on every pull request, on pushes to `main`, on the merge queue and on manual dispatch. The wall-clock budget is **15 min**. The jobs run in parallel and each has a 15-minute timeout.

## Jobs

| Job (check name) | What it does | Artifacts |
|---|---|---|
| `Versions consistency` | `tools/ci/verify-versions.sh`: fails if `versions.env` disagrees with `global.json` (`sdk.version`), `src/Opportunity.Web/.nvmrc` or the `engines.node` major in `package.json` | — |
| `.NET build, unit and architecture tests` | Restores, then builds `Opportunity.slnx` in Release with `-warnaserror`. Runs `tests/Opportunity.UnitTests` and `tests/Opportunity.ArchitectureTests` (ADR-019 layering rules) | `test-results-dotnet` (TRX + Markdown) |
| `Integration tests (Testcontainers)` | Sets `vm.max_map_count=262144` for OpenSearch, then builds and runs `tests/Opportunity.IntegrationTests` against real containers on the runner's Docker daemon | `test-results-integration` (TRX + Markdown) |
| `Angular build and unit tests` | `npm ci`, `prettier --check`, `npm run build`, `ng test --watch=false` (Vitest) | `test-results-web` (JUnit XML) |
| `Dependency vulnerabilities (NuGet, npm)` | `dotnet list package --vulnerable --include-transitive` and `npm audit` (lockfile only, dev toolchain included), then `tools/ci/security/check-vulnerabilities.py` fails on High/Critical findings that have no unexpired exception | `dependency-audit` (raw JSON + Markdown) |
| `Secret scan (gitleaks)` | A checksum-verified gitleaks binary scans the full git history, with secrets redacted in the output | `gitleaks-report` (SARIF, on failure only) |
| `Licenses and SBOM (CycloneDX)` | Builds CycloneDX SBOMs of shipped code (non-test .NET projects and npm production dependencies). `tools/ci/security/check-licenses.py` then enforces the license policy | `sbom` (two `.cdx.json` files + `license-report.md`, 90 days) |
| `Compose developer profile` | Sets `vm.max_map_count`, generates `.env`, fails if a Redis/Valkey service exists, validates the optional `observability` profile (Compose config, `otelcol validate`, `promtool check config`, dashboard JSON; ADR-017) without starting it, then `deploy/docker-compose/opportunity.sh up`: preflight, builds the four images from `deploy/docker`, starts the stack and waits until every service is healthy. Then checks that the migrator exited 0, `/health/ready` on the API returns 200, the web serves `index.html` and the demo seed runs. Dumps logs on failure ([developer profile](../deploy/docker-compose/README.md), `E19-T03`) | — |
| `CI gate` | Passes only if every job above succeeded. This is the one check that branch protection requires | — |

Shared behaviour:

- **Versions.** Each job loads `versions.env` into the job environment. `actions/setup-dotnet` installs `DOTNET_SDK` and `actions/setup-node` installs `NODE`. Change versions in `versions.env` first, then update `global.json` / `.nvmrc` to match. The `Versions consistency` job enforces this.
- **Caching.** NuGet (`~/.nuget/packages`) is cached with `actions/cache`. The key hashes `global.json`, `Directory.Packages.props`, `Directory.Build.props` and all `*.csproj`. npm is cached by `actions/setup-node`, keyed on `package-lock.json`.
- **Test results.** .NET suites write TRX for download and Markdown, which is appended to the run's job summary on the PR's Checks tab. The Angular suite writes JUnit XML. Artifacts are kept for 14 days.
- **Concurrency.** A new push to a PR cancels that PR's previous run. Runs on `main` are never cancelled.
- **Permissions.** The workflow token is `contents: read` only. Checkout does not persist credentials. Only `codeql.yml` and `scorecard.yml` get `security-events: write`, and they grant it per job.
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

## Security and supply-chain gates

These gates implement #24 (`E01-T04`) and threat-model entries T-57 and T-59 ([threat model](security/threat-model.md#tb12--supply-chain--installation)). Image scanning, signing and provenance (#25, `E01-T05`, T-58) are described under [Container images](#container-images).

| Gate | Where | Runs on | Blocks merge |
|---|---|---|---|
| Dependency vulnerabilities (NuGet + npm, transitive) | `ci.yml` › `dependency-audit` | every PR, `main`, merge queue | yes, through `CI gate` |
| Secret scan (gitleaks, full history) | `ci.yml` › `secret-scan` | every PR, `main`, merge queue | yes, through `CI gate` |
| License policy + CycloneDX SBOM | `ci.yml` › `licenses-sbom` | every PR, `main`, merge queue | yes, through `CI gate` |
| CodeQL SAST: C#, JavaScript/TypeScript, GitHub Actions (`security-extended`) | `codeql.yml` | every PR, `main`, merge queue, weekly | yes, once the code-scanning rule below is set |
| OpenSSF Scorecard | `scorecard.yml` | `main`, weekly, branch-protection changes | no (tracked score) |
| GitHub secret scanning + push protection | repository setting | every push | push protection blocks the push |
| Image vulnerabilities (Trivy, OS + .NET/npm packages in each image) | `images.yml` › `Build and scan` | every PR, `main`, `v*` tags | blocks publishing; on PRs once the check is required |

### Vulnerability policy and exceptions

Both advisory feeds come from the GitHub Advisory Database. `tools/ci/security/check-vulnerabilities.py` fails the job on any **High** or **Critical** advisory. The same script and exception file gate the Trivy image scans (`--trivy`); image findings use the CVE (or GHSA) id Trivy reports and the package name as reported, e.g. an OS package such as `libc6`. Lower severities appear in the report and do not block. An advisory can be accepted temporarily by adding an entry to [`tools/ci/security/vulnerability-exceptions.json`](../tools/ci/security/vulnerability-exceptions.json):

```json
{ "advisory": "GHSA-xxxx-xxxx-xxxx", "package": "Some.Package", "reason": "Not reachable: we never call X; upgrade blocked by #123",
  "expires": "2026-12-31", "approvedBy": "@security-reviewer" }
```

Every field is required. `expires` may be at most 90 days out. Once an exception expires the job fails again, so the advisory has to be fixed or the exception re-reviewed. CODEOWNERS routes changes to this file to Security & Compliance. The npm audit includes dev dependencies, because a compromised build toolchain is a supply-chain risk even though it does not ship.

### License policy

The project is MIT-licensed. [`tools/ci/security/license-policy.json`](../tools/ci/security/license-policy.json) applies only to **shipped** components, as listed in the SBOMs: the dependencies of non-test .NET projects (including the `tools/` projects) and npm production dependencies, which is what the Angular bundle contains. The policy works like this:

- **Default deny.** A license must be on `allowed` (permissive licenses such as MIT, Apache-2.0, BSD, ISC and PostgreSQL). For SPDX expressions, `A OR B` needs one allowed side and `A AND B` needs both. A component with no license, or with only a license URL, fails.
- **`denied`** lists the GPL/AGPL family, SSPL, EUPL, BUSL, the Elastic License and similar. A linked dependency under one of these fails **even if an exception names it**. This follows Q-38 in [decisions.md](plan/decisions.md): AGPL software may only be an optional, unmodified *external service* (for example an object store), never linked code.
- **`exceptions`** accept one component (`component` is a purl, with or without `@version`) that is neither allowed nor denied, for example an LGPL or MPL library used unmodified. Each needs `reason` and `approvedBy`. `expires` is optional. The product owner owns the policy.

`license-report.md` in the `sbom` artifact is the per-build license report. It also appears in the job summary.

### SBOM

`opportunity-dotnet.cdx.json` comes from the CycloneDX .NET tool, pinned in [`.config/dotnet-tools.json`](../.config/dotnet-tools.json). `opportunity-web.cdx.json` comes from `@cyclonedx/cyclonedx-npm`, pinned by `CYCLONEDX_NPM` in `versions.env` and run on `package-lock.json` only. Both use CycloneDX 1.6+ JSON and are kept for 90 days. Per-image SBOMs are described under [Container images](#container-images).

### Secret scanning

gitleaks (`GITLEAKS` and `GITLEAKS_SHA256` in `versions.env`) runs as the release binary with its checksum verified. It does not use `gitleaks-action`, which needs a paid license key for organization repositories. To suppress a false positive, add a `.gitleaksignore` entry with the finding fingerprint from the report, or a `.gitleaks.toml` allowlist, and get Security review. Never allowlist a real secret: rotate it, because it stays in history.

### Reproduce locally

```bash
dotnet restore Opportunity.slnx
dotnet list Opportunity.slnx package --vulnerable --include-transitive --format json > nuget.json
(cd src/Opportunity.Web && npm audit --json > ../../npm.json)
python3 tools/ci/security/check-vulnerabilities.py --dotnet nuget.json --npm npm.json

dotnet tool restore
dotnet CycloneDX Opportunity.slnx --exclude-test-projects --output-format Json --output sbom --filename opportunity-dotnet.cdx.json
(cd src/Opportunity.Web && npx --yes @cyclonedx/cyclonedx-npm@6.0.1 --package-lock-only --omit dev \
   --flatten-components --output-format JSON --output-file ../../sbom/opportunity-web.cdx.json)
python3 tools/ci/security/check-licenses.py sbom/*.cdx.json

gitleaks git --redact .      # https://github.com/gitleaks/gitleaks/releases
```

### Not covered here

- **`actions/dependency-review-action` and OSV-Scanner.** Not added, because the two audits above already query the same advisory database on every PR and use one exception file. Dependency review also needs the dependency graph, plus GitHub Advanced Security on private repositories. Add one of them if the project wants findings to appear as PR annotations.

## Container images

Implements #25 (`E01-T05`), ADR-015 D17.2 and threat-model entries T-57/T-58. Four images, all multi-arch (`linux/amd64`, `linux/arm64`), published to GHCR:

| Image | Dockerfile (`--target`) | Base (pinned by index digest) | Runs as | Health |
|---|---|---|---|---|
| `ghcr.io/plogramer/opportunity-api` | `deploy/docker/dotnet.Dockerfile` (`api`) | `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` | `app` (1654) | `HEALTHCHECK` on `/health/live`, port 8080 |
| `ghcr.io/plogramer/opportunity-worker` | `deploy/docker/dotnet.Dockerfile` (`worker`) | same | `app` (1654) | same |
| `ghcr.io/plogramer/opportunity-migrator` | `deploy/docker/dotnet.Dockerfile` (`migrator`) | `mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled` | `app` (1654) | none (one-shot) |
| `ghcr.io/plogramer/opportunity-web` | `deploy/docker/web.Dockerfile` (`web`) | `nginxinc/nginx-unprivileged:1.30-alpine-slim` | `nginx` (101) | `HEALTHCHECK` on `/healthz`, port 8080 |

- **Worker.** One image runs every worker type: `Workers__Enabled=all` (the default, Lite) or one type or a comma-separated subset per container (Full), e.g. `Workers__Enabled=import`. Unknown names fail at startup.
- **Readiness.** When `ConnectionStrings__App` is set, api and worker register the `postgres-schema` readiness check (`SchemaVersionHealthCheck`): `/health/ready` returns 503 until the migrator has brought the schema up to the version the image was built for. A newer schema is ready (expand/contract).
- **Migrator.** Applies PostgreSQL migrations and bootstrap steps, then exits: `0` success, `1` migration failed, `2` configuration error (no `ConnectionStrings__Migrator`), `3` bootstrap step failed. Verified against PostgreSQL 17: the first run applies the migrations, a second run is a no-op that exits `0`.
- **Kerberos.** The chiseled images do not include `libgssapi_krb5`, so PostgreSQL GSSAPI/Kerberos authentication is not supported. Npgsql probes for it and prints `libgssapi_krb5.so.2: cannot open shared object file` once; add `GSS Encryption Mode=Disable` to PostgreSQL connection strings to skip the probe.
- **Web.** The Angular production build, served by nginx with SPA fallback, `no-cache` on `index.html`, immutable caching for content-hashed bundles and the ADR-015 D4 response headers. The CSP allows `'unsafe-inline'` for styles only, the documented interim exception until `ngCspNonce` (`E15-T01`). TLS and `/api` routing are the edge proxy's job.
- **Hardening.** Multi-stage builds; the .NET images are chiseled (no shell, no package manager). The SDK and Node stages run on the build host and cross-compile, so only the web image's `apk upgrade` step needs emulation for arm64. Run every container with `read_only: true`, `cap_drop: [ALL]`, `security_opt: [no-new-privileges:true]` and a tmpfs at `/tmp` (required by nginx for its pid and temp files; used by the .NET diagnostics socket). The chiseled images have no curl or wget, so their `HEALTHCHECK` runs `src/Opportunity.HealthProbe`, a 40 KB .NET probe shipped in the image.
- **Labels.** `org.opencontainers.image.{title,description,source,licenses,version,revision,created,…}`. CI passes `VERSION`, `REVISION` and `CREATED` as build args.
- **Build context.** The repository root, with an allowlist [`.dockerignore`](../.dockerignore): only `src/`, the root build props, `.editorconfig`, `global.json` and `deploy/docker/` reach the builder, so `.env` files and git history never do.

### Workflows

[`container-images.yml`](../.github/workflows/container-images.yml) is a reusable workflow with three jobs. [`images.yml`](../.github/workflows/images.yml) calls it for pull requests (`publish: false`) and `main` (`publish: true`); [`release.yml`](../.github/workflows/release.yml) calls it for `v*` tags.

| Job | Runs on | What it does | Permissions |
|---|---|---|---|
| `Build and scan (<image>)` | PRs, `main`, tags | Builds `linux/amd64` into the runner's Docker (GitHub Actions layer cache). Trivy (checksum-pinned release binary, `TRIVY`/`TRIVY_SHA256` in `versions.env`) writes a JSON vulnerability report and a CycloneDX SBOM. `check-vulnerabilities.py --trivy` fails on High/Critical without an exception. Then a smoke test: non-root user, `--read-only --cap-drop ALL`, `/health/live` (`/healthz` for web) answers, the migrator exits `2` without configuration | `contents: read` |
| `Publish, sign and attest (<image>)` | `main`, tags | Rebuilds both architectures from the same cache and pushes. BuildKit adds an SPDX SBOM and SLSA provenance (`mode=max`) to the image index. `cosign sign` (keyless, GitHub OIDC) signs the digest. `actions/attest-build-provenance` and `actions/attest-sbom` (the CycloneDX SBOM from the scan job) add GitHub attestations, also pushed to the registry | inherited from the caller: `packages: write`, `id-token: write`, `attestations: write` |
| `Verify and smoke-test (<arch>)` | `main`, tags | On `ubuntu-24.04` and `ubuntu-24.04-arm`: `cosign verify` and `gh attestation verify` (provenance and SBOM) for every image, then the hardened run as above against the published `sha-<short>` images | `contents/packages/attestations: read` |

Pull-request builds never push. The callers set `permissions: {}` at workflow level and grant write scopes only to the publishing call. Images are published only after their scan passed. The publish job reuses the scanned amd64 layers from the cache. A cache miss rebuilds them (for web, that can pick up newer Alpine packages), which is why the verify job runs after publishing.

**Tags.** Every publish gets the immutable `sha-<7-char commit>`. `main` moves `edge`; a `vX.Y.Z` tag publishes `X.Y.Z` and `X.Y`. `latest` is never published. Compose and Kubernetes manifests reference images **by digest** (`E01-T06` produces the release bundle).

**Image vulnerability waivers.** Same file and rules as above ([Vulnerability policy and exceptions](#vulnerability-policy-and-exceptions)), with the CVE id and the package name from the Trivy report, for example `{ "advisory": "CVE-2026-12345", "package": "libc6", ... }`. Prefer a base-image bump (Dependabot `docker` ecosystem on `deploy/docker`). The web image runs `apk upgrade` at build time, because Alpine usually ships a fix before the nginx image is rebuilt.

**Base images.** Pinned as `tag@sha256:<index digest>` directly in the `FROM` lines, where Dependabot updates them. To bump by hand: `docker buildx imagetools inspect <image:tag>` and copy the top-level `Digest`.

### Verify a published image (operators)

```bash
IMAGE=ghcr.io/plogramer/opportunity-api@sha256:<digest>
cosign verify "$IMAGE" \
  --certificate-identity-regexp '^https://github.com/plogramer/opportuniTY/.github/workflows/container-images.yml@' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
gh attestation verify "oci://$IMAGE" --repo plogramer/opportuniTY                                   # SLSA provenance
gh attestation verify "oci://$IMAGE" --repo plogramer/opportuniTY --predicate-type https://cyclonedx.org/bom   # SBOM
docker buildx imagetools inspect "$IMAGE" --format '{{ json .SBOM }}'                                  # BuildKit SPDX SBOM
```

### Build and run locally

```bash
docker buildx build -f deploy/docker/dotnet.Dockerfile --target api      -t opportunity-api:dev --load .
docker buildx build -f deploy/docker/dotnet.Dockerfile --target worker   -t opportunity-worker:dev --load .
docker buildx build -f deploy/docker/dotnet.Dockerfile --target migrator -t opportunity-migrator:dev --load .
docker buildx build -f deploy/docker/web.Dockerfile                      -t opportunity-web:dev --load .

docker run --rm --read-only --tmpfs /tmp --cap-drop ALL -p 8080:8080 opportunity-api:dev    # curl localhost:8080/health/live
docker run --rm --read-only --tmpfs /tmp --cap-drop ALL -e Workers__Enabled=import -p 8081:8080 opportunity-worker:dev
docker run --rm --read-only opportunity-migrator:dev    # exits 2: ConnectionStrings__Migrator is not configured

# Scan like CI (Trivy from versions.env):
trivy image --scanners vuln --format json -o trivy-api.json opportunity-api:dev
python3 tools/ci/security/check-vulnerabilities.py --trivy trivy-api.json
```

Behind a TLS-intercepting proxy, pass its CA bundle as a build secret (never stored in a layer): `--secret id=build-ca,src=/path/to/ca-bundle.crt`. Without access to the Alpine mirrors, build web with `--build-arg APK_UPGRADE=0`.

Approximate sizes (linux/amd64, compressed as pulled / unpacked): api 61 / 141 MB, worker 61 / 141 MB, migrator 46 / 107 MB, web 6 / 15 MB. The chiseled ASP.NET base accounts for about 55 MB compressed.

### Not covered yet

- **Compose developer-profile smoke test on both architectures.** The verify job runs each published image on amd64 and arm64 runners with the Compose hardening flags. The developer-profile smoke test (the `Compose developer profile` job in `ci.yml`) runs on amd64 with locally built images on every PR; running it against the published images on both architectures remains open (`E01-T06`).
- **Release notes, Compose bundle with digests, changelog.** These come with #26 (`E01-T06`), which builds on `release.yml`.
- **arm64 runners.** `ubuntu-24.04-arm` is free for public repositories. A private repository needs a larger-runner plan or a self-hosted arm64 runner.

## Branch protection and repository settings (manual, repository admin)

Require the **`CI gate`** status check on `main`, and require branches to be up to date before merging. Because only the gate is required, adding or renaming jobs never needs a branch-protection change. Add the new job to `ci-gate.needs` instead.

The security gates also need these settings:

- **Code scanning:** add a ruleset rule *Require code scanning results* (CodeQL, alerts at *High or higher* / security severity *High or higher*). This makes CodeQL block merges without listing each matrix check by name.
- **Secret scanning** and **push protection:** turn both on (Settings › Code security).
- **Private vulnerability reporting:** turn it on (Settings › Code security), as [SECURITY.md](../SECURITY.md) requires.
- **Pull request reviews:** require at least one approving review, and *Require review from Code Owners*. CODEOWNERS covers `src/Opportunity.Security/`, the workflows, `tools/ci/security/` and `SECURITY.md`. Add the auth and audit paths when that code lands.
- **Scorecard:** `publish_results: true` publishes the score to the OpenSSF API for public repositories. Track it with the badge `https://api.securityscorecards.dev/projects/github.com/<owner>/<repo>/badge` and in the code-scanning view (category `scorecard`).

## Extension points

- **Container images (#25, `E01-T05`).** Implemented in `images.yml` / `release.yml` / `container-images.yml` ([Container images](#container-images)). Write scopes stay out of `ci.yml`. To require the image scan on PRs, add `Images / Build and scan (…)` checks to branch protection, or add an image job to `ci-gate.needs`.
- **OpenAPI diff and message-contract tests (L5).** Add steps in the `.NET` job, after the architecture tests, once the suites exist.
- **Cross-workspace security suite (`E05-T05`).** It lives under `tests/Opportunity.IntegrationTests/Security`, so it already runs in the integration job. **Fault/idempotency smoke (`E18-T01`).** Give it its own job if it would push the integration job past the budget, and add that job to `ci-gate.needs`.
- **Coverage.** Not collected yet. Add `Microsoft.Testing.Extensions.CodeCoverage` (.NET) and `@vitest/coverage-v8` (Angular), then upload Cobertura reports next to the test results.
- **OpenAPI gate.** `tools/ci/openapi-check.sh` runs in the `.NET` job after build: fails on uncommitted drift of `src/Opportunity.Api/openapi/opportunity-api-v1.json` and on breaking changes vs the base branch (oasdiff).
- **`dotnet format --verify-no-changes`.** Enabled in the `.NET` job (`Check formatting`). Run `dotnet format Opportunity.slnx` locally to fix.
