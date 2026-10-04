# opportuniTY — guide for AI agents and contributors

opportuniTY is a free, open-source, self-hosted eDiscovery review platform (.NET 10 API/workers, PostgreSQL,
OpenSearch, RabbitMQ, Angular web app). Read this file before changing anything.

## UI work — mandatory reading

Any task that touches the user interface (Angular components, styles, icons, layout, wording shown to users,
screenshots, UX docs) MUST follow, in this order of precedence:

1. **[docs/ux/ai-ui-guidelines.md](docs/ux/ai-ui-guidelines.md)** — product-owner UI rules: *copy the workflow, not the
   pixels*. Familiar to experienced eDiscovery reviewers, but visually, verbally and in branding unmistakably
   opportuniTY. Never pixel-copy, trace or imitate another product's screens, icons, CSS or assets; never use another
   vendor's name or branding in the product UI; run the §19 similarity check and the §23 checklist before finishing.
2. **[docs/ux/brand-and-angular-theme-guide.md](docs/ux/brand-and-angular-theme-guide.md)** and
   [assets/brand/](assets/brand/) — brand colours, logo and icons. Colours reach the app only through design tokens
   (`src/Opportunity.Web/src/styles/_tokens.scss`); text colours must meet WCAG 2.2 AA contrast.
3. **[docs/ux/review-platform-familiarity-guide.md](docs/ux/review-platform-familiarity-guide.md)** and
   [docs/ux/ticket-review.md](docs/ux/ticket-review.md) — workflow conventions and per-ticket UX notes.
   Where they conflict with (1), (1) wins.
4. Decisions **Q-47, Q-51, Q-64** in [docs/plan/decisions.md](docs/plan/decisions.md) and **ADR-018** (frontend).

Before finishing UI work: reuse existing components (`src/Opportunity.Web/src/app/ui`), keep keyboard access and
visible focus, and keep the web gates green: `npx prettier --check .`, `npm run api:check`, `npx ng test --watch=false`,
`npx ng build --stats-json && npm run budget:bundle`, `npm run e2e` (axe in all themes, keyboard path, performance).

## Everything else

- Normative spec: [docs/architecture/architecture-baseline.md](docs/architecture/architecture-baseline.md). Product
  decisions (override plan defaults): [docs/plan/decisions.md](docs/plan/decisions.md). ADRs: [docs/adr/](docs/adr/).
  Tickets and plan: [docs/plan/](docs/plan/). Test strategy: [docs/testing/test-strategy.md](docs/testing/test-strategy.md).
- Security is non-negotiable: every API endpoint declares a PDP permission; tenant data only through
  `WorkspaceTransaction` (PostgreSQL RLS); search only through `Opportunity.Search`; document content only through the
  protected-content gateway. Architecture tests enforce these rules — never weaken them to make a build pass.
- Build and test: `dotnet build Opportunity.slnx` (warnings are errors), `dotnet test --solution Opportunity.slnx`,
  `dotnet format --verify-no-changes`. Migrations are numbered SQL files in
  `src/Opportunity.Data/Migrations/Scripts` (use the next free number). After API changes, commit the regenerated
  OpenAPI document and run `npm run api:generate` in `src/Opportunity.Web`.
- Local stack: `deploy/docker-compose/README.md` (`./opportunity.sh up`, app at http://localhost:8080).
- No real client data anywhere (tests, fixtures, screenshots).
