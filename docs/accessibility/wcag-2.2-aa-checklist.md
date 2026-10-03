# WCAG 2.2 AA conformance checklist

This is a living checklist for the reviewer UI (`src/Opportunity.Web`). The target is WCAG 2.2 AA (Q-37, ADR-018 §10), and an ACR (VPAT-style) is published for 1.0, built from this file. Update the row in the same PR whenever a change affects a criterion. Every milestone also adds a dated line under [Manual passes](#manual-passes).

**How each criterion is checked**

- **axe-e2e:** axe-core in Chromium over every app-shell route and open menu, in the light, dark and high-contrast themes. Serious or critical violations fail CI (`e2e/accessibility.spec.ts`, [CI](../ci.md#accessibility-and-ui-performance-gates)).
- **axe-unit:** axe-core in every component spec (jsdom, contrast excluded) (`ui/testing/axe.testing.ts`).
- **tokens:** contrast of every colour pair in every theme (`ui/tokens/tokens.spec.ts`).
- **keyboard-e2e:** keyboard-only Playwright path with a visible-focus assertion on every Tab stop (`e2e/keyboard.spec.ts`).
- **unit:** a behavioural unit spec.
- **manual:** the milestone pass (keyboard only, NVDA + Firefox, JAWS + Chrome, VoiceOver + Safari, 200 % zoom, forced colours).

**Status:** **Supports**, **Partial** (works where built; gaps listed), **Open** (not yet addressed), **Not applicable** (with a reason). These follow the ACR terms. "Partial" here means *partially supports*.

| SC | Level | Criterion | How we meet it | Checked by | Status |
|---|---|---|---|---|---|
| 1.1.1 | A | Non-text content | Icons are decorative (`aria-hidden`) next to text labels. Icon-only controls carry an accessible name | axe-e2e, axe-unit | Supports |
| 1.2.1–1.2.5 | A/AA | Time-based media | The product UI has no media of its own. Audio and video documents under review are third-party evidence, displayed as they are | — | Not applicable (UI); ACR note for evidence |
| 1.3.1 | A | Info and relationships | Native elements, landmarks, `th scope`, labelled groups, CDK/APG patterns | axe-e2e, axe-unit | Supports |
| 1.3.2 | A | Meaningful sequence | DOM order matches visual order. No CSS reordering of content | manual | Supports |
| 1.3.3 | A | Sensory characteristics | Instructions name controls by label, never by position or colour | manual | Supports |
| 1.3.4 | AA | Orientation | No orientation lock | manual | Supports |
| 1.3.5 | AA | Identify input purpose | No personal-data inputs yet. Sign-in happens at the IdP | — | Not applicable (yet) |
| 1.4.1 | A | Use of colour | States are carried by text plus an icon, never by colour alone (ADR-018 §9) | manual | Supports |
| 1.4.2 | A | Audio control | No auto-playing audio | — | Not applicable |
| 1.4.3 | AA | Contrast (minimum) | Token pairs ≥ 4.5:1 (text) in every theme | tokens, axe-e2e | Supports |
| 1.4.4 | AA | Resize text | rem-based type scale. Layout checked at 200 % | manual | Partial (review workspace not built yet) |
| 1.4.5 | AA | Images of text | None | — | Supports |
| 1.4.10 | AA | Reflow | Non-grid pages reflow at 320 px equivalent. Grids scroll in two dimensions (ADR-018 §12) | manual | Partial (grid in E16) |
| 1.4.11 | AA | Non-text contrast | Control borders and the focus ring ≥ 3:1 in every theme | tokens, axe-e2e | Supports |
| 1.4.12 | AA | Text spacing | No fixed-height text containers | manual | Partial (to verify on review pages) |
| 1.4.13 | AA | Content on hover or focus | Menus are dismissible (Esc), hoverable and persistent. No hover-only tooltips | keyboard-e2e, manual | Supports |
| 2.1.1 | A | Keyboard | Every shell function is reachable and operable by keyboard | keyboard-e2e | Partial (vertical slice in E16) |
| 2.1.2 | A | No keyboard trap | Dialogs trap focus deliberately and close with Esc. Focus is restored | unit, keyboard-e2e | Supports |
| 2.1.4 | A | Character key shortcuts | Character-key shortcuts (`/`, `?`, `[`, `]`, `n`, `+`, `-`, `0`) never act while typing in a text field. Each can be removed or remapped per user, and one preference turns them all off (user menu → Keyboard shortcuts…). Saved to the user profile (E15-T03) | unit, keyboard-e2e | Supports |
| 2.2.1 | A | Timing adjustable | Session timeouts are security controls (Q-45). The session-ended dialog keeps the place to resume after sign-in. A warning before expiry is still to be decided | manual | Partial |
| 2.2.2 | A | Pause, stop, hide | No auto-updating content yet. Freshness and job progress (E16-T07) must stay quiet or be throttled | manual | Open (E16-T07) |
| 2.3.1 | A | Three flashes | No flashing content. `prefers-reduced-motion` is honoured | manual | Supports |
| 2.4.1 | A | Bypass blocks | A skip link is the first Tab stop and moves focus to `main` | keyboard-e2e | Supports |
| 2.4.2 | A | Page titled | Every route has a title: "Page · Workspace · opportuniTY" | unit, keyboard-e2e | Supports |
| 2.4.3 | A | Focus order | Focus moves to the main heading on navigation. Menus return focus to their trigger | keyboard-e2e, unit | Supports |
| 2.4.4 | A | Link purpose (in context) | Link text names the destination | axe-e2e, manual | Supports |
| 2.4.5 | AA | Multiple ways | Section navigation, workspace switcher, Workspaces list with filter | manual | Supports |
| 2.4.6 | AA | Headings and labels | One `h1` per page. Every field has a visible label | axe-e2e | Supports |
| 2.4.7 | AA | Focus visible | One product focus-ring token. Asserted on every Tab stop | keyboard-e2e | Supports |
| 2.4.11 | AA | Focus not obscured (minimum) | Scroll padding below sticky bars and toasts | manual | Partial (to verify with toasts on review pages) |
| 2.5.1 | A | Pointer gestures | No path-based or multipoint gestures | — | Supports |
| 2.5.2 | A | Pointer cancellation | Native buttons activate on up-event | — | Supports |
| 2.5.3 | A | Label in name | Accessible names start with the visible label | axe-e2e | Supports |
| 2.5.4 | A | Motion actuation | None | — | Not applicable |
| 2.5.7 | AA | Dragging movements | The split-pane handle works by keyboard and has buttons. Redaction drawing needs an alternative (E11/E16) | unit | Partial |
| 2.5.8 | AA | Target size (minimum) | Controls ≥ 24 × 24 px, including the compact density | axe-e2e, manual | Supports |
| 3.1.1 | A | Language of page | `<html lang>` follows the display locale | axe-e2e | Supports |
| 3.1.2 | AA | Language of parts | UI is English only. Document text keeps its own language where the source declares it | — | Not applicable (UI) |
| 3.2.1 | A | On focus | Focus never changes context | manual | Supports |
| 3.2.2 | A | On input | Changing a setting never navigates without an explicit action | manual | Supports |
| 3.2.3 | AA | Consistent navigation | One shell, fixed section order (guide §2) | manual | Supports |
| 3.2.4 | AA | Consistent identification | One component library (`opp-*`) and shared labels | manual | Supports |
| 3.2.6 | A | Consistent help | About and help sit in the user menu on every page | manual | Supports |
| 3.3.1 | A | Error identification | Problem details are shown as text next to the field or in an error state | unit | Partial (forms arrive with features) |
| 3.3.2 | A | Labels or instructions | Visible labels and hints on every field | axe-unit | Supports |
| 3.3.3 | AA | Error suggestion | Validation messages say how to fix the input | unit | Partial (forms arrive with features) |
| 3.3.4 | AA | Error prevention (legal, financial, data) | Bulk coding, productions and exports have a confirmation step with frozen counts (E16-T06) | unit, manual | Open (E16-T06) |
| 3.3.7 | A | Redundant entry | Coding and saved views keep earlier input | manual | Open (E16) |
| 3.3.8 | AA | Accessible authentication (minimum) | Handled by the organisation's OIDC IdP. The app has no password fields | — | Not applicable (IdP) |
| 4.1.2 | A | Name, role, value | Native elements first, otherwise CDK/`@angular/aria` APG patterns | axe-e2e, axe-unit | Supports |
| 4.1.3 | AA | Status messages | One throttled live-region announcer. Counts and results use `role="status"` | unit | Supports |

## Keyboard shortcut conflict matrix

Every default binding of the command registry (`src/app/core/commands/command-catalog.ts`, familiarity guide §4) against the browsers on Windows and macOS (Chrome, Edge, Firefox, Safari) and the screen readers (NVDA, JAWS, VoiceOver). The registry also refuses to let a user bind a reserved key: tab, window and history keys, copy and paste, developer tools, F5–F7 and F10–F12, Ctrl/⌘+digit, Alt+letter without Shift (browser menus), Ctrl+Alt (AltGr) on Windows, Control+Option (the VoiceOver key) on macOS, Insert and Caps Lock (screen-reader modifiers), and bare arrow, Home and End keys (`reserved-keys.ts`). Two commands may share a chord only when their regions are never active together (for example the viewer and the document list). A unit test (`keymap.spec.ts`) fails when a default is reserved or two defaults conflict.

This matrix is an analysis of the documented browser, OS and screen-reader keys. It has not yet been run on every browser and screen-reader combination; that run is part of the M1 manual pass below. A default that conflicts changes in the guide and in the registry together.

**Legend.** **Free**: no browser, OS or screen-reader function. **Scoped override**: the default replaces a browser function only while focus is in the named region, on purpose (guide §4). Everywhere else the browser function still works, so browser zoom (WCAG 1.4.4) and find remain available. **Page first**: a browser uses the chord for its own UI, but the page receives the key first, so the command wins while it is available and the browser acts otherwise. These still need confirmation in the manual pass. **Browse mode**: the screen reader keeps unmodified character keys for quick navigation in browse/virtual mode (NVDA, JAWS, VoiceOver with Quick Nav). The page receives them only in focus/forms mode, so screen-reader users use the modified chord or turn single keys off.

| Command | Windows / Linux | macOS | Region | Browsers | Screen readers | Result |
|---|---|---|---|---|---|---|
| Save & Next / Save & Previous | `Ctrl+Enter` / `Ctrl+Shift+Enter` | `⌘↩` / `⌘⇧↩` | Review | Free | Passed through | Free |
| Save | `Ctrl+S` | `⌘S` | Review | Save page (all) | Passed through | Scoped override |
| Cancel unsaved edits | `Alt+Shift+Z` | `⌥⇧Z` | Review | Free | Passed through | Free |
| Next / previous document | `Alt+Shift+.` / `Alt+Shift+,`; `]` / `[` | `⌥⇧.` / `⌥⇧,`; `]` / `[` | Review | Free | Single keys: browse mode | Free; single keys optional |
| Back to list | `Alt+Shift+L`; `Esc` | `⌥⇧L`; `Esc` | Review | Free (`Esc` is never taken inside dialogs, menus or text fields) | Passed through | Free |
| Open focused row | `Enter` | `↩` | Document list | Free | Passed through (focus mode in the grid) | Free |
| Viewer modes | `Alt+Shift+1` … `5` | `⌥⇧1` … `5` | Review | Free. On Windows, Alt+Shift pressed and released on its own switches the input language; with a digit in between it does not | Passed through | Free |
| Next / previous hit | `F3` / `Shift+F3`; `n` / `Shift+N` | `⌘G` / `⌘⇧G`; `n` / `Shift+N` | Review | Find next / previous (all) | `n`: browse mode (NVDA and JAWS: next non-link text) | Scoped override; single keys optional |
| Toggle highlighting | `Alt+Shift+H` | `⌥⇧H` | Review | Free | Passed through | Free |
| Find in document | `Ctrl+F` | `⌘F` | Viewer | Find in page (all) | Passed through | Scoped override |
| Next / previous page | `Page Down` / `Page Up` | same | Viewer | Scroll (all) | Passed through | Scoped override |
| Zoom in / out / fit | `Ctrl+=` / `Ctrl+-` / `Ctrl+0`; `+` / `-` / `0` | `⌘=` / `⌘-` / `⌘0`; `+` / `-` / `0` | Viewer | Browser zoom (all) | Single keys: browse mode | Scoped override; single keys optional |
| Rotate page | `Alt+Shift+R` | `⌥⇧R` | Viewer | Free | Passed through | Free |
| Next / previous region | `Alt+Shift+G` / `Alt+Shift+B` | `⌥⇧G` / `⌥⇧B` | Everywhere | `Alt+Shift+B`: Chrome and Edge focus the bookmarks bar | Passed through | Page first (`B`) |
| Focus coding pane (then 1–9 = field n) | `Alt+Shift+C` | `⌥⇧C` | Review | Free | Passed through | Free |
| Focus keyword search | `Alt+Shift+K`; `/` | `⌥⇧K`; `/` | Everywhere (handled on Documents) | `/`: Firefox Quick Find (not in text fields) | `/`: browse mode | Free; `/` is page first in Firefox, optional |
| Focus related items | `Alt+Shift+I` | `⌥⇧I` | Review | `Alt+Shift+I`: Chrome and Edge open the feedback form | Passed through | Page first |
| Select / clear focused row | `Space` | `Space` | Document list | Scroll (all) | Focus mode in the grid | Scoped override |
| Select all on page | `Ctrl+A` | `⌘A` | Document list | Select all text (all) | Passed through | Scoped override |
| Select all results / clear selection | `Alt+Shift+A` / `Alt+Shift+0` | `⌥⇧A` / `⌥⇧0` | Document list | `Alt+Shift+A`: Chrome focuses an inactive browser dialog, if one is showing | Passed through | Page first (`A`) |
| Mass Edit / Apply to Family | `Alt+Shift+E` / `Alt+Shift+F` | `⌥⇧E` / `⌥⇧F` | Document list / Review | Free | Passed through | Free |
| Shortcut cheat sheet | `Alt+Shift+/`; `?` | `⌥⇧/`; `?` | Everywhere | Free | `?`: browse mode | Free; single key optional |

Notes:

- **Screen readers.** No default uses the NVDA or JAWS modifier (Insert or Caps Lock) or the VoiceOver key (Control+Option). NVDA and JAWS pass modified chords they do not use through to the page in both browse and focus mode.
- **macOS.** Option+Shift+letter types a symbol in text fields. Bindings use `KeyboardEvent.code`, so the command still fires, and the registry prevents the symbol from being typed when it runs. `F3` is Mission Control on macOS, so next/previous hit use `⌘G` / `⌘⇧G` there.
- **Page first.** `Alt+Shift+B`, `Alt+Shift+I` and `Alt+Shift+A` overlap Chromium browser-UI keys. The registry calls `preventDefault()` only while the command has a handler, so the browser key keeps working everywhere else. If the manual pass shows that Chrome or Edge acts before the page, these three defaults change in the guide and in `command-catalog.ts` together.
- **Single keys** (`/`, `?`, `[`, `]`, `n`, `Shift+N`, `+`, `-`, `0`) are alternatives, never the only binding. They are inactive in text fields and inside dialogs and menus, and one preference turns them all off (WCAG 2.1.4).

## Manual passes

| Date | Milestone | Scope | Result |
|---|---|---|---|
| — | M1 | Due before M1 closes: shell, then the vertical slice | — |
