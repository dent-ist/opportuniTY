# Releasing

Implements #26 (`E01-T06`): Conventional Commits and release-please, a release workflow that runs end to end once the
release PR is merged, an N-1 → N upgrade test that must pass before anything is published, and per-release
artifacts: signed images, a Compose bundle that pins every image by digest, a compatibility matrix, a changelog and
the third-party NOTICE. Release cadence and support follow Q-43: a minor release every month, with security fixes
for N and N-1.

## Versions

| What | Scheme | Source of truth | Changes when |
|---|---|---|---|
| Product | SemVer `X.Y.Z`, tag `vX.Y.Z` | `.release-please-manifest.json` (mirrored in `version.txt`) | release-please, from the commits since the last release |
| Database schema | integer `V####` | `src/Opportunity.Data/Migrations/Scripts` (`MigrationCatalog.LatestVersion`) | a migration is added |
| Search projection | generation `G`, mapping checksum | `src/Opportunity.Search/Mappings/projection.v{G}.json` | the index mapping changes (alias-based reindex) |
| Message contracts | `major.minor` per message type | `[MessageContract]` types in `src/Opportunity.Contracts/Messaging` | a payload changes (ADR-019 §3) |

The last three are versioned independently of the product version, and each release records them in its
**compatibility matrix** (`COMPATIBILITY.md` / `compatibility.json`, generated from the shipped code by
`tools/Opportunity.Release`). Before 1.0, a breaking change bumps the minor version (`bump-minor-pre-major`).

Rolling-upgrade rules that the matrix makes visible:

- **Database:** forward-only, expand/contract migrations ([Migrations README](../src/Opportunity.Data/Migrations/README.md)).
  Release N's API and workers run against schema N and newer, so N's migrator can run before N-1 is stopped.
- **Messages:** consumers accept major N and N-1 of each message type; producers write N
  (`MessageTypeRegistry.RegisterPrevious`).
- **Search:** a new projection generation is placed by an alias-based reindex ([alias reindex](operations/alias-reindex.md));
  older generations stay readable until their indexes are gone.

## Conventional Commits

Pull requests are squash-merged and the **pull-request title** becomes the commit on `main`. The `PR title` workflow
(`.github/workflows/pr-title.yml`, `tools/release/check-conventional-title.py`) checks it:

```
<type>(<optional scope>)<optional !>: <subject>
feat(review): add batch check-in
fix(import)!: reject load files without a control number column
```

| Type | Release effect | Changelog section |
|---|---|---|
| `feat` | minor | Features |
| `fix`, `perf`, `revert`, `deps`, `docs` | patch | Bug fixes, Performance, Reverts, Dependencies, Documentation |
| `refactor`, `test`, `build`, `ci`, `chore`, `style` | none on their own | hidden |
| `!` after the type, or a `BREAKING CHANGE:` footer | major (minor before 1.0) | marked as breaking |

Commits on `main` that are not Conventional Commits (such as the wave merges before this was adopted) are ignored by
release-please; `bootstrap-sha` in `release-please-config.json` starts the history at main after wave 20. To make the
title check blocking, add `PR title / Conventional Commits title` to the branch-protection rule (see [CI](ci.md)).

## How a release happens

1. **Every push to `main`** runs the `Release PR` job (`.github/workflows/release.yml`): release-please opens or
   updates one pull request, `chore: release X.Y.Z`, that bumps the manifest and `version.txt` and prepends the
   changelog section. Review it like any other pull request; edit the changelog text in it if needed.
2. **Merging the release PR** is the only manual step. The push of the merge commit is detected by
   `tools/release/detect-release.sh` (the manifest names a version that has no tag yet, and this commit bumped it),
   and the same workflow run continues:
   1. `Upgrade test (N-1 to N)`, below. Nothing is published if it fails.
   2. `Images`: the reusable [container-images.yml](../.github/workflows/container-images.yml) builds, scans
      (Trivy, vulnerability policy), publishes `X.Y.Z`, `X.Y` and `sha-<short>` for `linux/amd64` and `linux/arm64`,
      signs with cosign (keyless) and attests provenance and SBOM, then verifies the published images on both
      architectures.
   3. `GitHub release`: resolves the four image digests from the `X.Y.Z` tags and verifies their signatures, writes the
      compatibility matrix, the SBOMs (license policy checked again) and the third-party NOTICE, builds the Compose
      bundle with `--verify` and attests it (SLSA provenance). Only then does release-please create the `vX.Y.Z` tag on
      the release commit and the GitHub release with the changelog section, and the assets are uploaded.

The images are published before the tag exists; a failure in the last job leaves a version with images but no tag
and no GitHub release, which is never presented as released.

**Token.** GitHub does not start workflows for pull requests opened with the workflow's own token, so the release PR
would get no CI. Add a repository secret `RELEASE_PLEASE_TOKEN` (GitHub App installation token or fine-grained PAT:
contents and pull requests, read and write); the workflow falls back to `GITHUB_TOKEN` without it. Tags created with
that token do not start a second release: `release.yml` does not run on tag pushes.

**If a release run fails**, fix the cause and use *Re-run failed jobs* on that run: it releases the exact release
commit. While a merged version is untagged, later pushes to `main` report `pending` and do not update the release PR.
To abandon the version instead, revert the release commit (and delete any `X.Y.Z` image tags it published).

**Patch releases for N-1** (Q-43 security fixes) are not automated yet: branch `release/X.Y` from the tag, cherry-pick
the fix, and run release-please there with `--target-branch release/X.Y`; the workflow only watches `main`.

## Release assets

| Asset | Contents |
|---|---|
| `opportunity-compose-vX.Y.Z.tgz` | The Compose profile of the release commit: `compose.yaml`, `opportunity.sh`, `.env.example` (naming the release), configuration files, `versions.env`; opportuniTY images pinned by digest in a generated `compose.images.yaml`, third-party services by digest in `versions.env`; plus `README.md`, `CHANGELOG.md` (this release), `COMPATIBILITY.md`, `compatibility.json`, `images.env`, `THIRD-PARTY-NOTICES.md`, `LICENSE` and `SHA256SUMS`. Reproducible: sorted entries, fixed owner, mtime = commit time |
| `THIRD-PARTY-NOTICES.md` | Every linked or bundled third-party component (from the CycloneDX SBOMs of shipped code) grouped by license, with versions, copyright and package URLs; reviewed classifications from the license policy's exceptions; base images; services run as separate containers (Q-38, Q-42) |
| `COMPATIBILITY.md`, `compatibility.json` | Schema version and latest migration, projection generation and mapping checksums, message contract versions written and read, supported upgrade source |
| `images.env` | `api`, `worker`, `migrator`, `web` image references by digest |
| `opportunity-dotnet.cdx.json`, `opportunity-web.cdx.json` | SBOMs of shipped code |
| `SHA256SUMS` | Checksums of the assets above |

Verify a bundle and install it:

```sh
gh attestation verify opportunity-compose-vX.Y.Z.tgz --repo plogramer/opportuniTY   # SLSA provenance
tar -xzf opportunity-compose-vX.Y.Z.tgz && cd opportunity-compose-vX.Y.Z
sha256sum -c SHA256SUMS
cd deploy/docker-compose && OPPORTUNITY_IMAGES=ghcr ./opportunity.sh up
```

Images: see [Verify a published image](ci.md#verify-a-published-image-operators). To upgrade, follow the bundle's
`README.md`: back up, unpack next to the old bundle, copy `.env` across, run `up`; the migrator upgrades the schema
before the API and workers start.

## Upgrade test

"Deploy N-1, load data, upgrade to N, run smoke tests", in two tiers that share the same data and checks
(`tools/release/upgrade/seed.sql`, `tools/release/upgrade/smoke.sql`: two workspaces, coding fields and choices,
20 documents with a family, coding on every document, a saved search, a role assignment).

| Tier | What is N-1 / N | Runs |
|---|---|---|
| Database: `tests/Opportunity.IntegrationTests/Migrations/UpgradeTests.cs` | N-1: the previous release's migration scripts from its tag (`OPPORTUNITY_UPGRADE_FROM_SCRIPTS`), else the baseline schema in `tools/release/upgrade/baseline.env`. N: this build's migrator and code | every integration run (PR CI) and the release run |
| Deployment: `tools/release/upgrade-test.sh` | N-1: the previous release's **migrator image** (by digest, signature verified) or, before the first release, one built from the baseline commit. N: the release commit's migrator and API images | the release run |

Both apply N-1 to an empty PostgreSQL (the `versions.env` image), load the data, upgrade with N, and check that a
second run of the migrator is a no-op and the data is intact. The database tier then reads and writes the upgraded
data with N's repositories under row-level security, including a coding write whose document version continues from
the N-1 data, and fails if the previous release's scripts were edited (checksum) or are missing. The deployment tier
starts N's API against the upgraded database and requires `/health/ready`.

Run locally:

```sh
dotnet test --project tests/Opportunity.IntegrationTests -- --filter-class "*UpgradeTests"
# from a release tag's scripts, or from an older schema version:
git archive v0.1.0 src/Opportunity.Data/Migrations/Scripts | tar -x -C /tmp/n-1
OPPORTUNITY_UPGRADE_FROM=v0.1.0 OPPORTUNITY_UPGRADE_FROM_SCRIPTS=/tmp/n-1/src/Opportunity.Data/Migrations/Scripts \
  dotnet test --project tests/Opportunity.IntegrationTests -- --filter-class "*UpgradeTests"
OPPORTUNITY_UPGRADE_FROM_SCHEMA=40 dotnet test --project tests/Opportunity.IntegrationTests -- --filter-class "*UpgradeTests"

# deployment tier with images, or with `dotnet publish` output folders (dir:<path>):
tools/release/upgrade-test.sh --from image:ghcr.io/plogramer/opportunity-migrator:0.1.0 \
  --to image:opportunity-migrator:dev --to-api image:opportunity-api:dev
```

**Keeping it meaningful.** `seed.sql` must load into the baseline schema; write it against long-lived core tables
and extend it when a release adds data whose upgrade matters (for example, a migration that rewrites existing rows).
After a release, move `baseline.env` forward to that tag and its latest `V####`.

## Contributions: DCO, not a CLA

Proposed for product-owner confirmation (recorded in [CONTRIBUTING](../CONTRIBUTING.md#sign-off-dco)): contributions
are accepted under the project's MIT license with a Developer Certificate of Origin sign-off (`git commit -s`), and
no Contributor License Agreement. A CLA would need a legal entity to sign with and a signing service; the DCO keeps
contribution friction low and matches inbound = outbound licensing. Enforcement (the DCO GitHub App, or a check on
`Signed-off-by:` trailers) is not switched on yet.

## Validation and limits

- The workflows are checked by `actionlint` (with shellcheck) and by `tests/Opportunity.UnitTests/Release` (triggers,
  job order: nothing published before the upgrade test, bundle before tag; actions pinned by SHA; release-please
  files consistent). `release-please-config.json` validates against the release-please 17 JSON schema. They have not
  run on GitHub yet: the first release run is their end-to-end test.
- The scripts run locally: `upgrade-test.sh` (images and `dir:` folders, N-1 = main after wave 19, schema 63 → 66,
  API ready), `build-compose-bundle.sh --verify` (byte-identical archives on re-runs), `generate-notice.py` (from
  the same SBOMs as the CI license gate), `detect-release.sh` (scenarios in a throwaway repository).
- Not covered: per-release license texts are referenced by SPDX identifier rather than copied into the NOTICE;
  Apache-2.0 components' own `NOTICE` files are not aggregated; maintenance branches (above); the Kubernetes manifests
  (`deploy/kubernetes`) are not part of the bundle yet.
