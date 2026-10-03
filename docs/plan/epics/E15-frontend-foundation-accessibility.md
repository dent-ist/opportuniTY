# E15 — Frontend Foundation & Accessibility

**Labels:** `epic`, `role:ui`, `role:qa`, `P0`  
**Starts in:** M1 - First Vertical Slice  
**Tickets:** 5

## Goal
Set up the Angular foundation every screen builds on: a frontend architecture ADR, design tokens and components, the application shell on a BFF session, a keyboard command framework, WCAG 2.2 AA and UI-performance gates in CI, and localisation.

## Baseline sections
§3, §4, §15, §16, §18 (Opportunity.Web), §20/§32

## Scope / out of scope
**In scope**
- ADR-018 frontend architecture (state, paging, push vs poll, i18n, browser support, UI perf targets)
- Design tokens and core components
- Shell and workspace context
- Keyboard command framework
- a11y/perf gates
- Localisation and time-zone display

**Out of scope**
- Feature screens (E16 and feature epics)

## Contributing roles
- **Roles:** UI/UX, QA
- **Source reviews:** UI/UX, Security & Compliance, QA & Performance
- **Milestones spanned:** M1 - First Vertical Slice, M3 - MVP Feature Complete

## Exit criteria
- [ ] CI fails on new serious/critical axe violations
- [ ] The code → save → next loop works keyboard-only
- [ ] Switching workspace clears all workspace-scoped client state

## Tickets

| Key | Title | Milestone | Size | Depends on |
|---|---|---|---|---|
| [E15-T01](#e15-t01) | Write frontend architecture ADR and build design tokens and core components | M1 | L | E01-T01, E02-T01 |
| [E15-T02](#e15-t02) | Build application shell, session handling and workspace context | M1 | M | E15-T01, E05-T01 |
| [E15-T03](#e15-t03) | Build keyboard command framework and user preferences | M1 | M | E15-T02 |
| [E15-T04](#e15-t04) | Add accessibility and UI-performance gates to CI | M1 | M | E15-T01, E01-T03 |
| [E15-T05](#e15-t05) | Localise UI with date/time-zone display rules | M3 | M | E15-T02, E04-T05 |

---

### E15-T01

**Write frontend architecture ADR and build design tokens and core components**  
Labels: `role:ui`, `P0`, `size:L`, `adr` · Milestone: M1 - First Vertical Slice

#### Context
§3/§4 mention only 'Angular — review grid, viewer, coding, admin'. UI finding 1: no a11y target, design system, state management, i18n, browser support; finding 16: no UI performance targets.

#### Description
ADR-018: WCAG 2.2 AA target, design system on Angular CDK, state management, i18n approach, supported browsers, minimum viewport, cursor paging model, push (SSE) vs polling, UI performance targets (next doc visible ≤ 500 ms p95 with prefetch, coding ack ≤ 200 ms p95). Design tokens (colour, type, spacing, density, elevation, focus ring) with light/dark/high-contrast themes and compact/comfortable density; core components (button, input, select, multi-choice, date, dialog, toast, tabs, split pane, badge, status pill, progress) in Storybook.

#### Acceptance criteria
- [ ] ADR-018 is Accepted and listed in the ADR index
- [ ] Tokens are CSS custom properties; lint fails on hard-coded colours
- [ ] All colour pairs meet 4.5:1 text and 3:1 non-text contrast in every theme (automated check)
- [ ] Every component has a Storybook story with passing axe checks and documented keyboard interaction; targets ≥ 24×24 px
- [ ] Compact density shows ≥ 30 grid rows at 1080p

#### Dependencies
- `E01-T01` — Scaffold repository layout, .NET solution and Angular workspace
- `E02-T01` — Establish ADR process, resolve numbering and record layering/API conventions

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / L

#### Notes
Proposed amendment — frontend architecture section for the baseline. Q-37.

---

### E15-T02

**Build application shell, session handling and workspace context**  
Labels: `role:ui`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
§2.3 WorkspaceId as a hard boundary; §15 RBAC-aware navigation; BFF session from `E05-T01`.

#### Description
Login/logout via the BFF (no tokens in Angular), session-expiry handling, workspace switcher, primary navigation (Review, Search, Jobs, Admin), user menu; WorkspaceId held in route and state; navigation driven by permissions from the API.

#### Acceptance criteria
- [ ] Every API call carries the active WorkspaceId from the route; switching workspace clears all workspace-scoped client state (e2e test)
- [ ] A deep link to an inaccessible workspace/document shows a 'No access' page
- [ ] Skip-to-content link and landmark regions; shell passes axe

#### Dependencies
- `E15-T01` — Write frontend architecture ADR and build design tokens and core components
- `E05-T01` — Implement OIDC authentication with a BFF session

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX, Security & Compliance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E15-T03

**Build keyboard command framework and user preferences**  
Labels: `role:ui`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
UI finding 14: professional reviewers code thousands of documents a day via keyboard; WCAG 2.1.4.

#### Description
Central command registry with scopes (grid, viewer, coding, redaction); default bindings following reviewer conventions; '?' cheat sheet; per-user rebinding with conflict detection; single-key shortcuts can be disabled/remapped and are inactive in text inputs; user-preferences API for bindings, pane sizes and grid views.

#### Acceptance criteria
- [ ] Commands exist for next/prev document, next/prev hit, focus-region cycle, save, save-and-next, viewer mode toggle, bulk-select toggle
- [ ] Rebinding persists to the user profile across reloads
- [ ] The code → save → next loop works with no mouse (e2e)

#### Dependencies
- `E15-T02` — Build application shell, session handling and workspace context

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E15-T04

**Add accessibility and UI-performance gates to CI**  
Labels: `role:ui`, `role:qa`, `P0`, `size:M` · Milestone: M1 - First Vertical Slice

#### Context
UI finding 14 and 16; QA finding 14 (UI latency as smoke check only).

#### Description
axe-core in component and e2e tests; keyboard-only e2e through the vertical slice; Lighthouse/performance budgets; grid scroll frame-budget test; living WCAG 2.2 AA conformance checklist.

#### Acceptance criteria
- [ ] CI fails on new serious/critical axe violations
- [ ] Initial JS bundle ≤ agreed budget (e.g. 500 KB gzip); viewer and admin routes are lazy-loaded
- [ ] Grid scroll with 10k loaded rows keeps ≥ 50 fps on the reference machine

#### Dependencies
- `E15-T01` — Write frontend architecture ADR and build design tokens and core components
- `E01-T03` — Set up PR CI pipeline with real-dependency integration tests

#### Roles
- **Owner:** UI/UX
- **Contributing:** QA
- **Source reviews:** UI/UX, QA & Performance
- **Milestone / phase / size:** M1 - First Vertical Slice / P0 / M

---

### E15-T05

**Localise UI with date/time-zone display rules**  
Labels: `role:ui`, `P1`, `size:M` · Milestone: M3 - MVP Feature Complete

#### Context
UI finding 1 (i18n, international date formats); eDiscovery: matter display time zone, UTC storage with raw strings.

#### Description
i18n framework, locale-aware date/number formatting, matter display time zone with per-user override, raw-value tooltip for imported dates, MVP locales per Q-28/Q-37.

#### Acceptance criteria
- [ ] All dates render in the selected display time zone with zone indicator; raw imported string is available
- [ ] Locale switch changes date/number formats without reload
- [ ] No hard-coded user-visible strings (lint)

#### Dependencies
- `E15-T02` — Build application shell, session handling and workspace context
- `E04-T05` — Expose workspace management API

#### Roles
- **Owner:** UI/UX
- **Contributing:** —
- **Source reviews:** UI/UX, eDiscovery Practitioner
- **Milestone / phase / size:** M3 - MVP Feature Complete / P1 / M

#### Notes
Q-28, Q-37.
