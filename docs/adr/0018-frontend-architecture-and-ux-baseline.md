# ADR-018: Frontend architecture & UX baseline

| Field | Value |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-03 |
| **Owner (role)** | UI/UX |
| **Deciders** | Lead architect; contributing: Security & Compliance (§6 BFF/CSP), QA & Performance (§14–§15); product owner (Q-10, Q-28, Q-32–Q-35, Q-37, Q-47) |
| **Tracking issue** | #122 (plan key `E15-T01`) |
| **Baseline sections** | [§3](../architecture/architecture-baseline.md#3-high-level-architecture), [§4](../architecture/architecture-baseline.md#4-technology-stack), [§28](../architecture/architecture-baseline.md#28-search-generation-and-reviewer-ux) |
| **Related** | ADR-015 D4 (BFF session, CSRF, CSP), ADR-019 §2 (API conventions), ADR-001 (generations, Q-10), ADR-017 (SLOs); review findings A-02, A-19, A-11; UI/UX review findings 1, 5, 6, 7, 14, 16; Q-10, Q-28, Q-32, Q-33, Q-34, Q-35, Q-37, Q-47 |

## Context

The baseline says only "Angular — review grid, viewer, coding, admin" (§3, §4). The UI/UX review (finding 1) lists
what is missing: an accessibility target, a design system, state management, i18n, browser support and a minimum
viewport. Finding 16 adds that there are no UI performance targets. Review finding A-19 proposes a "Frontend
architecture & UX baseline" section and A-02 registers this ADR.

Reviewers spend whole working days in the product: thousands of documents a day, mostly by keyboard. Density,
predictability, keyboard reach and low visual fatigue matter more than decoration. The product owner has decided:

- **Q-37:** WCAG 2.2 AA with an ACR at 1.0; the latest two versions of Chrome, Edge, Firefox and Safari; UI latency is
  diagnostic, with budgets enforced in CI.
- **Q-28:** en-US and en-GB formats in the MVP; a matter display time zone with a per-user override.
- **Q-10:** stale counts are acceptable with a visible freshness banner and "≈" labels; reviewers see plain-language
  freshness, raw generations are for admins and support only.
- **Q-32/Q-33:** approximate counts above 10,000 with "count exactly"; no deep page jumps; a live PIT cursor with a
  "results refreshed" notice.
- **Q-34:** no undo; typed confirmation above 10,000 documents or for security-affecting fields.
- **Q-35:** SSE with a polling fallback (≥ 5 s).
- **Q-47:** the product must feel familiar to experienced users of established review platforms without copying any
  of them (§9).

Constraints from other ADRs: the SPA never holds tokens and talks to a BFF over a `__Host-` cookie session with
anti-forgery protection, under a CSP without `'unsafe-inline'` scripts (ADR-015 D4; the BFF itself is being built in
#46, `E05-T01`). The API is REST under `/api/v1`, RFC 9457 problem details, cursor paging, `Idempotency-Key`, ETags
and 202 + job resources (ADR-019 §2). Without one decision, each feature ticket in E15/E16 would choose its own state
pattern, component library, error display and layout, and the slice would ship screens that share nothing.

## Decision

### 1. Application stack and structure

1. Angular 22 standalone components, **zoneless** change detection, `OnPush` everywhere, signal inputs/outputs/models,
   built-in control flow. No NgModules, no `zone.js`.
2. Source layout under `src/Opportunity.Web/src/app`:

   | Folder | Contains | May import |
   |---|---|---|
   | `core/` | API client (`core/api`, generated code in `core/api/generated`), session, preferences, app-wide services | Angular, CDK |
   | `ui/` | The design system (§7): tokens consumers, components, test helpers; public surface `ui/index.ts` | `core/preferences`, `core/api/problem-details` |
   | `features/<area>/` | Lazy-loaded feature routes: `review`, `search`, `jobs`, `admin`, `viewer` (E15-T02 onward) | `core`, `ui` |
   | `dev/` | Development-only routes (component showcase); replaced by `dev-routes.prod.ts` in production builds | anything |

   Features never import each other's internals; shared pieces move to `ui/` or `core/`.

### 2. State management: signals

1. **Signals are the state primitive.** Component state is `signal`/`computed`/`linkedSignal`; inputs are `input()`,
   two-way state is `model()`.
2. **Feature stores are plain injectable classes** that keep private `WritableSignal`s and expose read-only signals,
   `computed` selectors and intention-revealing methods (`codeDocument()`, `selectAll()`). No NgRx, no global store.
3. **Scope = lifetime.** Workspace-scoped stores are provided on the workspace shell component of the
   `/w/:workspaceId` route (beside `WorkspaceContext`) or on feature components, never in route `providers`: Angular
   keeps route injectors alive when only a parameter changes. A route-reuse strategy re-creates the workspace shell
   whenever `:workspaceId` changes, so switching workspace destroys those stores and all workspace data with them
   (`E15-T02` acceptance). Only identity, preferences and the toast/announcer services are root-scoped.
4. **Server state** is read with Angular `resource()` / `httpResource()` over the generated client (§4) and is never
   copied into a second cache. Mutations go through store methods that call the API and then reconcile.
5. **The URL is state** for everything a reviewer may bookmark or share: workspace, saved search, document, viewer
   mode. Route params bind to component inputs (`withComponentInputBinding`).
6. **RxJS stays at the edges** (HTTP, SSE streams, debouncing); it is converted to signals at the store boundary.
7. **Read-your-own-writes** (review finding 6): the coding store keeps a short-lived local overlay of the reviewer's
   own saved edits, badged "Saved · indexing", until the API reports a projected version ≥ the saved version.

### 3. Routing

1. Workspace routes live under `/w/:workspaceId/…` (review, search, jobs, admin); the `workspaceId` route parameter is
   the only source of the active workspace and every workspace-scoped API call takes it from there (ADR-019 §2.3).
2. Every feature area is **lazy-loaded** (`loadChildren`/`loadComponent`); the viewer and admin areas are separate
   chunks (`E15-T04`).
3. Guards consult the permissions the API returns; a forbidden or missing workspace/document shows a standard
   **No access** page that does not reveal whether the resource exists (ADR-019: 404, never 403).
4. Every route has a `title` (WCAG 2.4.2). On navigation, focus moves to the new page's main heading.
5. Development-only routes (`/dev/components`) are compiled only into development builds (`fileReplacements`).

### 4. API client generated from OpenAPI

1. The client is generated from `src/Opportunity.Api/openapi/opportunity-api-v1.json` with **ng-openapi-gen 1.1.0**
   (MIT), pinned exactly, configured in `ng-openapi-gen.json`. Output goes to `src/app/core/api/generated/` and is
   **committed** and never edited by hand.
2. Reproducibility: `npm run api:generate` regenerates and formats the output with the pinned Prettier;
   `npm run api:check` regenerates and fails if the committed client differs. CI runs `api:check` in the web job, so an
   API change that is not followed by a regenerated client fails the PR. Upgrading the generator is a deliberate PR
   that regenerates the client.
3. We use the generator's tree-shakable function-per-operation output (`services: false`) invoked through the
   generated `OpportunityApi` helper, so only operations a route uses are bundled. The functions use Angular
   `HttpClient`, so the interceptors in §5–§6 apply to every call.
4. Client rules from ADR-019: same-origin relative URLs (`/api/v1/…`, no CORS); ignore unknown fields and unknown enum
   values (render them as "Other"); send `If-Match` from the `ETag` on versioned mutations; create one
   `Idempotency-Key` (`crypto.randomUUID()`) per user action and reuse it on retries of that action; follow
   `nextCursor`, never compute offsets; treat timestamps as UTC and format only for display (§11).

### 5. Error handling with problem details

1. `problemDetailsInterceptor` normalises every HTTP failure into one type, `ApiError` (status, RFC 9457 body,
   stable `code` from `urn:opportunity:problem:<code>`, `traceId`, `Retry-After`). Components never parse
   `HttpErrorResponse`.
2. `describeError()` turns an `ApiError` into a plain-language title, detail, support reference (the trace id) and a
   retryable flag. 5xx responses never show server text; 404 is phrased so it does not disclose existence.
3. Display by context:

   | Failure | Display |
   |---|---|
   | `validation` (400/422 with `errors`) | Field errors next to the fields (linked by `aria-describedby`) plus a summary announced on submit |
   | Loading a region fails | `<opp-error-state>` in place of the region, with Retry when retryable and the reference |
   | Background action fails (job, bulk, export) | Persistent error toast with an action ("View job") |
   | `version-conflict` / 412 | Dialog: "Changed by someone else", reload and reapply; never silently overwrite |
   | 401 | Session expired flow (§6) |
   | 403 / 404 | No access page or inline "Not available" |
   | 429 / 503 with `Retry-After` | Message with the wait time; automatic retry only for idempotent reads |
   | Network failure | "Cannot reach the server", retryable |

4. Unexpected client exceptions go to the global error handler, which logs them (OpenTelemetry web export is
   `E19-T04`'s decision) and shows a generic error toast with no stack trace.

### 6. Authentication via the BFF cookie session

1. The SPA holds **no tokens** (ADR-015 D4.1). The browser sends the HttpOnly `__Host-opp-session` cookie
   automatically on same-origin requests.
2. The SPA depends on this BFF contract, configured in one place (`core/session/session.ts`, `SESSION_CONFIG`) so it
   can follow #46 without touching features:

   | Endpoint / name | Contract |
   |---|---|
   | `GET /api/v1/me` | 200 with the principal (id, display name, permissions) or 401 problem details. API routes **never** redirect to the IdP. |
   | `GET /bff/login?returnUrl=<relative path>` | Top-level navigation that starts OIDC code + PKCE and returns to `returnUrl` (validated as same-origin relative on both sides) |
   | `POST /bff/logout` | Anti-forgery protected; revokes the server session; 200 `{ "endSessionUrl": "<IdP end-session>" \| null }` |
   | Anti-forgery | Readable cookie `__Host-opp-xsrf` set by the BFF; header `X-XSRF-TOKEN` on every unsafe method (Angular `withXsrfConfiguration`) |

   If #46 chooses different paths or names, it changes `DEFAULT_SESSION_CONFIG` only.
3. Any 401 from the API sets the session signal to `expired`; the shell (`E15-T02`) then shows a modal "Your session
   has ended" with **Sign in again** and discards workspace-scoped state. Unsaved coding is never written to browser
   storage, because it can contain protected content.
4. **CSP:** the production web image sends `style-src 'self' 'nonce-…'` with a per-response nonce that nginx also
   writes into `index.html` (`<app-root ngCspNonce>`), so Angular's runtime component styles carry it. This removes the
   interim `'unsafe-inline'` style exception (ADR-015 D4.6). Critical-CSS inlining is off because its loader needs
   inline script. Templates contain no `style` attributes (enforced by the design lint, §7.3); dynamic styling uses
   property bindings (CSSOM), which CSP allows.

### 7. Design system

1. **Built on headless, permissively licensed primitives, not a component suite.** Behaviour comes from
   **Angular CDK** (MIT: focus trap, dialog, overlay, live announcer, bidi, id generation) and **@angular/aria**
   (MIT, the Angular team's headless WAI-ARIA patterns: tabs now; listbox, combobox, menu, grid, tree later). Visuals
   are ours. Reasons: full control of density and keyboard behaviour for review grids, no fight with another
   product's visual language (Q-47), a small bundle, and maintenance by the Angular team. Angular Material was
   rejected because its Material Design density and look would have to be overridden everywhere; PrimeNG and similar
   suites because they are large and carry their own theming systems.
2. **Tokens are CSS custom properties** in `src/styles/_tokens.scss`, the only file allowed to contain literal colours:
   colour (surfaces, text, borders, accent, focus, status tones, privileged/AEO/restricted), typography (system font
   stacks only, so there are no web fonts to license, host or allow in CSP), spacing (4 px grid), radius, elevation,
   focus ring, motion, z-index, and density (control height, row height, cell padding).
3. **Themes:** light (default), dark, high-contrast, and "system" (follows `prefers-color-scheme`), selected by
   `data-theme` on `<html>`. Components also honour `forced-colors: active` and `prefers-reduced-motion`.
   **Density:** comfortable (32 px controls/rows) and compact (24 px, the WCAG 2.5.8 minimum), selected by
   `data-density` on `<html>` or any container, so a grid can be compact inside a comfortable page.
4. **Enforced by unit tests** (part of `ng test`, so CI fails on violations):
   - every text pair is ≥ 4.5:1 and every non-text pair (borders, focus ring, fills) is ≥ 3:1 in every theme
     (`ui/tokens/tokens.spec.ts`, pairs listed there);
   - no hard-coded colours (hex, colour functions, named colours) anywhere in `src/` except the token file, and no
     inline `styles`/`style` attributes (`ui/tokens/design-lint.spec.ts`);
   - compact density keeps 24 px targets and fits ≥ 30 grid rows at 1080p: 955 px viewport (1080 minus ~125 px
     browser chrome) minus a 160 px shell budget (header 40, list toolbar 40, grid header 28, status bar 28, freshness
     banner 24) = 795 px ÷ 24 px = 33 rows. Feature screens must stay within that shell budget.
5. **Core components** (`ui/`, selector prefix `opp-`):

   | Component | Built on | Notes |
   |---|---|---|
   | Button, icon button | native `<button>`/`<a>` | Variants primary/secondary/ghost/danger; `busy`; icon buttons require a label (name + tooltip) |
   | Text field, select | native `<input>`/`<select>` | Label, hint, error linked by `aria-describedby`; Signal Forms `FormValueControl` |
   | Checkbox | native checkbox | 24 px hit area, indeterminate; Signal Forms `FormCheckboxControl` |
   | Dialog, confirm dialog | CDK Dialog | Focus trap, initial focus, focus restore, Escape, `aria-modal`, labelled by its heading; typed confirmation (Q-34) |
   | Toast, announcer | CDK LiveAnnouncer | Errors persist and are assertive; others auto-dismiss after 6 s, paused on hover/focus; per-key throttling (freshness ≤ 1 per 30 s) |
   | Tabs | @angular/aria tabs | APG keyboard model; lazy panel content |
   | Split pane | own (WAI-ARIA window splitter) | Keyboard resize/collapse, pointer drag optional, sizes persisted per user (§9) |
   | Badge, protection badge, freshness, count, status pill, progress | own | §8 |
   | Loading, empty, error states | own | Every async region shows exactly one of content/loading/empty/error |

   Multi-choice, date, combobox, menu and the virtualised review grid (CDK virtual scroll + @angular/aria grid
   semantics) are built by the tickets that first need them (E16), on the same tokens and primitives.
6. **Showcase instead of Storybook.** Every component is shown in every state on the development-only route
   `/dev/components`, with its keyboard interaction documented on the page; every component also has a unit test that
   runs axe-core. This deviates from the ticket's "Storybook story": Storybook would add a second build toolchain and
   several hundred dev dependencies for the same evidence. Revisit if designers outside the codebase need a hosted
   catalogue.

### 8. Data-state indicators

All states are carried by **text** (plus an icon where useful), never by colour alone (WCAG 1.4.1).

1. **Freshness** (Q-10, §28): `Current as of 10:42` · `Updating · ≈ 1,240 changes pending` · `Delayed · results as of
   10:30`. Raw generations are shown only in an admin/support detail. Changes are announced politely at most once per
   30 s.
2. **Counts** (ADR-019 `total.relation`, Q-10, Q-32): exact `12,400`; lower bound `≥ 10,000` (spoken "at least");
   stale `≈ 12,400` (spoken "approximately").
3. **Protection:** `Privileged`, `AEO` (expanded to "Attorneys' Eyes Only" for screen readers and on hover),
   `Confidential`, and `Access restricted` for a stale hit the authoritative check denied (finding 7, A-11). The
   restricted state never shows the hit's metadata.
4. **Coding acknowledgement:** `Saved · indexing` until the saved version is searchable (§2.7).
5. **Job status:** Queued, Running, Completed, Completed with errors, Failed, Cancelled; two-phase progress
   "Committed" / "Searchable" (finding 12).

### 9. Familiarity with established review platforms (Q-47)

Experienced reviewers should be able to work in opportuniTY without training. The binding UX guide for E15 and E16 is
[`docs/ux/review-platform-familiarity-guide.md`](../ux/review-platform-familiarity-guide.md); this section fixes the
architectural consequences.

1. **Terminology** follows industry-standard eDiscovery usage: Workspace, Saved Search, Field, Choice, Coding Layout,
   Batch, Production, Related Items, Persistent Highlighting, Extracted Text, Native, Image, Production (viewer modes),
   Control Number, Family, Duplicates. New labels need a reason.
2. **Layout:** left browser (saved searches and folders), centre document list (dense grid), document viewer with
   Extracted Text / Native / Image / Production modes, coding pane on the right, related-items pane. It is built from
   nested `opp-split-pane`s; every pane is resizable by pointer and keyboard and collapsible where it is optional.
3. **Persistent pane layouts:** pane sizes and collapsed state persist per user under a stable `storageKey`. Today
   `PreferenceStorage` keeps them in the browser; `E15-T03` moves them to the user-preferences API so a layout follows
   the reviewer across machines. Grid column layouts follow the same rule (E16).
4. **Dense by default for review:** the review workspace uses compact density for the grid; comfortable remains the
   default elsewhere and is a user preference.
5. **Keyboard-driven review:** every review action has a command and binding through the command registry
   (`E15-T03`), with defaults that follow reviewer conventions and single-key shortcuts that can be remapped or turned
   off (WCAG 2.1.4).
6. **Not a copy:** we MUST NOT reuse another product's branding, name, logos, icons, colours, visual design, trade
   dress or proprietary assets, and the UI MUST NOT name other review products. Icons are our own minimal set
   (`ui/icon`); the palette (calm neutrals, teal accent) is our own.

### 10. Accessibility: WCAG 2.2 AA (Q-37)

1. Target WCAG 2.2 AA for every screen; an ACR (VPAT-style) is published for 1.0. Criteria that need explicit design:
   2.1.4 character key shortcuts (remappable), 2.4.7/2.4.13 visible focus (one product focus ring token), 2.4.11
   focus not obscured (scroll padding below sticky bars and toasts), 2.5.7 dragging alternatives (keyboard splitter;
   redaction drawing in E11/E16), 2.5.8 target size ≥ 24×24, 3.3.7 redundant entry, 4.1.3 status messages (one
   announcer, throttled).
2. Native elements first; ARIA only through CDK/@angular/aria patterns or the WAI-ARIA APG patterns documented in
   each component.
3. Automated checks: axe-core runs in every component's unit test (jsdom, colour contrast excluded there and covered
   by the token test) and against the whole showcase page; `E15-T04` adds axe in Playwright against real browsers,
   failing CI on new serious/critical violations.
4. Manual checks before each milestone: keyboard-only pass, NVDA + Firefox, JAWS + Chrome, VoiceOver + Safari, 200 %
   zoom, Windows high-contrast (forced colours).

### 11. Internationalisation and time

1. UI text is English. Display formats are **en-US or en-GB** per user (Q-28), applied through `Intl` with the locale
   from `UiPreferences`; `<html lang>` follows it.
2. All API timestamps are UTC (ADR-019). Display uses the matter display time zone, overridable per user (Q-28), via
   `Intl.DateTimeFormat` with `timeZone`; productions use the time zone in their specification. `E15-T05` implements the
   formatting service and display rules.
3. Sentences are not built by string concatenation of translatable parts, so `@angular/localize` extraction can be
   added later without rewrites; adding it is deferred until a translated locale is required.

### 12. Browser support and viewport

1. The latest two versions of Chrome, Edge, Firefox and Safari (Q-37). No Internet Explorer or legacy Edge; no
   polyfills beyond Angular's defaults.
2. Minimum viewport for the review workspace: **1280 × 720** CSS px; below that, optional panes collapse. Admin and
   job pages work from 1024 px. Phones are not supported. Pages other than two-dimensional data grids reflow at 320 px
   equivalent width (WCAG 1.4.10, 400 % zoom); grids scroll in two dimensions as the criterion allows.

### 13. Data loading and push

1. **Paging:** cursor paging only (`limit`, `nextCursor`), virtual scrolling with cached pages, a PIT-backed live cursor
   with a "results refreshed" notice (Q-33), approximate totals above 10,000 with "count exactly" (Q-32), no page jumps.
2. **Push:** SSE (`EventSource`, same-origin, cookie-authenticated) for job progress and freshness, falling back to
   polling at ≥ 5 s when the stream fails (Q-35). One stream per tab per workspace, multiplexed by event type. The
   edge proxy MUST serve HTTP/2 so streams do not exhaust the browser's per-origin connection limit.
3. **Prefetch:** the viewer prefetches the next document's content after the authoritative check; prefetching is not
   audited as a view until the document is displayed (UI/UX finding 16).

### 14. Performance budgets

Budgets are diagnostic targets enforced in CI by `E15-T04` (Q-37), measured on the reference machine:

| Metric | Budget |
|---|---|
| Initial JavaScript (shell) | ≤ 250 kB gzip (build budget: warning 500 kB, error 1 MB raw) |
| Lazy route chunk | ≤ 150 kB gzip |
| Component stylesheet | warning 4 kB, error 8 kB |
| Next document visible (with prefetch) | ≤ 500 ms p95 |
| Coding save acknowledged | ≤ 200 ms p95 |
| Grid scroll, 10k loaded rows | ≥ 50 fps |
| Interaction to next paint | ≤ 200 ms p75 |

The shell is 95 kB gzip today.

### 15. Testing

1. **Unit (L1):** Vitest + jsdom through the Angular unit-test builder, zoneless `TestBed`; specs next to the code
   (`*.spec.ts`), test-only helpers in `*.testing.ts` (excluded from the app build). Every component spec includes an
   axe check (`ui/testing/axe.testing.ts`); a guard test proves the helper fails on violations.
2. **E2E (L8, later):** Playwright (Apache-2.0) under `src/Opportunity.Web/e2e` against the developer Compose profile
   (`docs/testing/test-strategy.md`), with `@axe-core/playwright` for real-browser checks including contrast, and a
   keyboard-only run of the vertical slice (`E15-T03`, `E15-T04`).
3. Visual regression testing is not adopted now.

### 16. Dependencies and licences

| Package | Licence | Scope | Purpose |
|---|---|---|---|
| `@angular/cdk`, `@angular/aria` | MIT | runtime | Headless primitives (§7.1) |
| `ng-openapi-gen` (exact 1.1.0) | MIT | dev | API client generation (§4) |
| `axe-core` | MPL-2.0 | dev | Accessibility checks in tests |
| `@types/node` | MIT | dev | Node APIs in token/lint specs |

The licence policy (`tools/ci/security/license-policy.json`) applies to shipped components only; the npm SBOM is built
with `--omit dev` and the web image contains only the compiled bundle, so axe-core is neither in the SBOM nor in the
image. MPL-2.0 is file-level copyleft and we do not modify or ship it. Stylelint was evaluated for the colour lint and
rejected: its transitive `braces` dependency has an unfixed High advisory that the dependency audit would block; the
lint is a unit test instead.

## Consequences

- **Positive:** one state pattern, one error type, one design language and one layout model for every E15/E16
  ticket; accessibility and token rules fail CI instead of relying on review; the API client cannot drift from the
  spec; the SPA is ready for the BFF without holding tokens, and the web image no longer needs `'unsafe-inline'`
  styles.
- **Negative / costs:** we maintain our own visual components (kept thin over native elements, CDK and
  @angular/aria); `@angular/aria` is young and its API may still move; the showcase is less rich than Storybook; the
  ng-openapi-gen template output is ours to review on upgrades.
- **Follow-up work:** `E15-T02` shell, session-expiry modal, No access page, workspace route scoping; `E15-T03`
  command registry and server-side preferences (pane layouts); `E15-T04` Playwright + axe + performance budgets in CI;
  `E15-T05` locale/time-zone formatting; E16 grid, viewer, coding pane on these components; #46 confirms the BFF
  contract in §6.2.
- **Verification:** `tokens.spec.ts` (contrast, density, target size), `design-lint.spec.ts` (no hard-coded colours or
  inline styles), per-component axe specs, `npm run api:check` in CI, the production build's `fileReplacements`
  (no dev routes shipped), license and SBOM job (no MPL in shipped components).

## Alternatives considered

| Option | Why not chosen |
|---|---|
| NgRx (Store or SignalStore) | Extra concepts and dependency for state that route-scoped signal services handle; revisit only if cross-feature state becomes complex |
| Angular Material | Material Design density and visual language would need overriding everywhere; conflicts with our own look (Q-47) |
| PrimeNG / other full suites | Large bundles, own theming and update cadence; we need few widgets and full control of keyboard behaviour |
| openapi-typescript (+ fetch wrapper) | Peer-depends on TypeScript 5 (we use 6), types only, bypasses `HttpClient` interceptors |
| @hey-api/openapi-ts | Pre-1.0 with frequent breaking changes |
| OpenAPI Generator (typescript-angular) | Needs a JVM in the web toolchain; heavy, verbose output |
| Hand-written client | Drifts from the spec; no compile-time check of API changes |
| Storybook | Second build toolchain and hundreds of dev dependencies for evidence the showcase and unit axe tests already give |
| Stylelint for the colour lint | Transitive High advisory (`braces`) blocks the dependency audit |
| Tokens in the SPA (silent renewal) | Rejected by ADR-015 D4: XSS could steal tokens |

## Baseline amendments

- *Proposed* (A-19): add a "Frontend architecture & UX baseline" section after §4 that summarises §1–§14 of this ADR:
  WCAG 2.2 AA, design system on Angular CDK/@angular/aria with tokens, signal-based state, generated API client, BFF
  session, supported browsers and viewport, paging/push model and UI performance targets. The baseline is edited only
  after lead-architect and product-owner sign-off.

## Links

- Baseline: §3, §4, §28
- Review findings: [review-findings.md](../plan/review-findings.md) A-02, A-11, A-19; [UI/UX review](../plan/reviews/ui-ux.md)
  findings 1, 5, 6, 7, 12, 14, 16
- Decisions: [decisions.md](../plan/decisions.md) Q-10, Q-28, Q-32, Q-33, Q-34, Q-35, Q-37, Q-47
- UX guide: [review-platform-familiarity-guide.md](../ux/review-platform-familiarity-guide.md)
- Code: `src/Opportunity.Web/src/styles/_tokens.scss`, `src/Opportunity.Web/src/app/ui/`,
  `src/Opportunity.Web/src/app/core/`, `deploy/docker/web/`
