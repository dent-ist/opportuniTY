# opportunity-web

The Angular 22 reviewer UI (zoneless, signals, Vitest). Architecture, design system and UX rules:
[ADR-018](../../docs/adr/0018-frontend-architecture-and-ux-baseline.md).

## Commands

```bash
npm ci
npm start                # dev server on http://localhost:4200
npm run build            # production build (dist/opportunity-web)
npx ng test --watch=false
npx prettier --check .
npm run api:generate     # regenerate src/app/core/api/generated from the API's OpenAPI document
npm run api:check        # fail if the committed client is out of date (CI)

# Accessibility and UI-performance gates (E15-T04, docs/ci.md); budgets in perf-budgets.json
npx ng build --stats-json
npm run budget:bundle    # gzip budgets of initial/lazy JavaScript, feature code stays lazy
npx playwright install chromium   # once (or set OPP_E2E_CHROMIUM to an existing Chromium binary)
npm run e2e              # axe in every theme, keyboard-only and page-performance tests, API mocked
```

The component showcase (development builds only) is at `/dev/components`.

## Layout

- `src/styles/_tokens.scss`: design tokens; the only place literal colours may appear.
- `src/app/ui/`: design-system components (`opp-*`), built on Angular CDK and `@angular/aria`.
- `src/app/core/`: API client, problem-details handling, BFF session, preferences, workspace context.
- `src/app/shell/`: application shell (E15-T02): sign-in, authenticated layout, Workspaces list, workspace
  switcher and section navigation, "Not available", error and About pages.
- `src/app/features/<area>/`: lazy-loaded feature pages under `/w/:workspaceId/…`.
- `src/app/dev/`: development-only routes, removed from production builds.
- `e2e/`: Playwright browser gates against the production build (`e2e/support`: static server with the
  production headers, API mock, axe runner).
