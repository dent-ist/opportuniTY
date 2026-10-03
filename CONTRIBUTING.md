# Contributing to opportuniTY

Thanks for helping build an open-source eDiscovery review platform.

## Before you start

- Read the [architecture baseline](docs/architecture/architecture-baseline.md). It is normative; changes to it go
  through an ADR (see [docs/adr/README.md](docs/adr/README.md)).
- Pick an issue from the current milestone. Issues carry role, phase (P0/P1/P2) and size labels; epics carry `epic`.
- Product decisions that override plan defaults are in [docs/plan/decisions.md](docs/plan/decisions.md).

## Development setup

- .NET SDK 10 and Node.js 24, as pinned in [`versions.env`](versions.env), `global.json` and
  `src/Opportunity.Web/.nvmrc`.
- Docker is required for integration tests (Testcontainers) and the Compose developer profile.

## Rules that the build enforces

- **Layering** ([ADR-019](docs/adr/0019-layering-and-api-conventions.md)): `Core` and `Contracts` depend on nothing;
  `Application` and feature modules never reference store/transport SDKs; infrastructure projects do not reference each
  other. `tests/Opportunity.ArchitectureTests` fails the build on violations.
- Nullable reference types and warnings-as-errors are on for every project.
- Package versions are managed centrally in `Directory.Packages.props`; do not put versions in `.csproj` files.

## Pull requests

- One issue per PR where practical; reference it with `Closes #N`.
- Follow the [test strategy](docs/testing/test-strategy.md): integration tests use real dependencies via
  Testcontainers, not store mocks.
- Never commit real client documents or data. Use synthetic data from `tools/Opportunity.DataGenerator`.
- Security issues: report privately via GitHub Security Advisories, not public issues.

## Legal disclaimer

opportuniTY is software, not legal advice. Contributions must not claim otherwise in UI text, documentation or
defaults. See the disclaimer in the [README](README.md#legal-disclaimer).
