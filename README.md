# opportuniTY

<img width="1340" height="805" alt="opportuniTY-logo-white-bg" src="https://github.com/user-attachments/assets/828c71de-ea8c-4d35-a9e4-f985b5aba19e" />


An open-source, self-hosted, horizontally scalable eDiscovery document review platform.
Organizations install and run opportuniTY in their own environment; it is not offered as a hosted service.

> **Status:** early development (milestone M0 — foundation). Not ready for real matters.

## Documentation

- [Architecture baseline](docs/architecture/architecture-baseline.md) — the normative implementation specification
- [Architecture decision records](docs/adr/README.md)
- [Implementation plan](docs/plan/README.md), [product decisions](docs/plan/decisions.md)
- [Test strategy](docs/testing/test-strategy.md)
- [Contributing](CONTRIBUTING.md)

## Repository layout

```text
src/        .NET solution projects (Core, Contracts, Application, infrastructure, feature modules, API and worker hosts)
            and the Angular frontend (src/Opportunity.Web)
tests/      Unit, integration, architecture and scale test projects
tools/      Data generator and benchmark harness
deploy/     Docker Compose profiles (Kubernetes later)
docs/       Architecture, ADRs, plan and testing docs
versions.env  Single source of truth for tool and service versions
```

## Building

Prerequisites: .NET SDK 10 and Node.js 24 (see `versions.env`).

```bash
dotnet build Opportunity.slnx
dotnet test --solution Opportunity.slnx

cd src/Opportunity.Web
npm ci
npm run build
npx ng test --watch=false
```

## Legal disclaimer

opportuniTY is software, not legal advice. It does not guarantee the defensibility of any review, privilege
determination, redaction or production. Users and their counsel remain responsible for verifying all outputs and for
compliance with applicable rules, court orders, protective orders and ESI agreements.

## License

See [LICENSE](LICENSE).
