# opportuniTY Brand & Angular Theme Guide

## 1. Brand Direction

**opportuniTY**는 무료 오픈소스 eDiscovery 플랫폼을 지향합니다.

브랜드 컬러는 기존 eDiscovery 시장의 대표적인 Orange 계열과 시각적으로 차별화되도록 **Navy + Teal + Cyan** 조합을 사용합니다.

브랜드 컬러의 의미는 다음과 같습니다.

- **Navy** — Legal, Trust, Enterprise, Stability
- **Teal** — Free, Open Source, Accessibility, Openness
- **Cyan** — Technology, Interaction, Focus, Innovation
- **White / Light Gray** — Clean, Transparent, Modern

Orange 계열은 핵심 브랜드 컬러로 사용하지 않고, 필요할 경우 **Warning / Caution** 상태에만 제한적으로 사용합니다.

---

# 2. Core Brand Colors

| Role | Color | Hex | Purpose |
|---|---|---:|---|
| Primary | Navy | `#123B63` | Main navigation, header, primary buttons, legal/enterprise identity |
| Primary Hover | Dark Navy | `#0F3152` | Primary hover state |
| Primary Pressed | Deep Navy | `#0B2742` | Primary pressed state |
| Secondary | Teal | `#009BBF` | Links, secondary buttons, interactive elements |
| Secondary Hover | Dark Teal | `#0087A7` | Secondary hover state |
| Secondary Pressed | Deep Teal | `#006F8A` | Secondary pressed state |
| Accent | Cyan | `#13B5D1` | Focus, selections, highlights, active indicators |
| Accent Soft | Light Cyan | `#D9F5FA` | Highlight backgrounds, FREE badge |
| Dark Surface | Deep Navy | `#071F38` | Sidebar, footer, dark UI areas |
| Background | Light Gray | `#F8FAFC` | Main app background |
| Surface | White | `#FFFFFF` | Cards, dialogs, forms |
| Text Primary | Near Black | `#0F172A` | Primary text |
| Text Secondary | Slate | `#64748B` | Secondary/supporting text |
| Border | Light Slate | `#E2E8F0` | Borders and dividers |

---

# 3. Semantic Colors

| Role | Hex | Usage |
|---|---:|---|
| Success | `#10B981` | Saved, completed, valid, successful operation |
| Warning | `#F59E0B` | Caution, pending issue, attention required |
| Error | `#EF4444` | Error, destructive action, failure |
| Info | `#0EA5E9` | Informational messages |
| Focus Ring | `#13B5D1` | Keyboard/input focus indication |

---

# 4. Brand Color Usage Rules

## Primary — Navy

Use Navy for:

- Main navigation
- Application header
- Primary CTA buttons
- Major headings
- Active navigation structures
- Enterprise/legal branding areas

Example:

```css
background: #123B63;
color: #FFFFFF;
```

---

## Secondary — Teal

Use Teal for:

- Secondary buttons
- Links
- Interactive icons
- Active controls
- Progress indicators
- Open-source / FREE related visual elements

Example:

```css
color: #009BBF;
```

---

## Accent — Cyan

Use Cyan sparingly for:

- Focus rings
- Selected rows
- Active filters
- Current review item
- Selected tags
- Keyboard focus state
- Important highlights

Example:

```css
border-color: #13B5D1;
```

---

# 5. Angular SCSS Tokens

Create:

```text
src/styles/_opportuniTY-tokens.scss
```

```scss
$op-primary: #123B63;
$op-primary-hover: #0F3152;
$op-primary-pressed: #0B2742;

$op-secondary: #009BBF;
$op-secondary-hover: #0087A7;
$op-secondary-pressed: #006F8A;

$op-accent: #13B5D1;
$op-accent-soft: #D9F5FA;

$op-background: #F8FAFC;
$op-surface: #FFFFFF;
$op-surface-dark: #071F38;

$op-text-primary: #0F172A;
$op-text-secondary: #64748B;
$op-border: #E2E8F0;

$op-success: #10B981;
$op-warning: #F59E0B;
$op-error: #EF4444;
$op-info: #0EA5E9;

$op-focus-ring: #13B5D1;
```

---

# 6. CSS Variables

For runtime theming and reusable component styling:

```scss
:root {
  --op-primary: #123B63;
  --op-primary-hover: #0F3152;
  --op-primary-pressed: #0B2742;

  --op-secondary: #009BBF;
  --op-secondary-hover: #0087A7;
  --op-secondary-pressed: #006F8A;

  --op-accent: #13B5D1;
  --op-accent-soft: #D9F5FA;

  --op-background: #F8FAFC;
  --op-surface: #FFFFFF;
  --op-surface-dark: #071F38;

  --op-text-primary: #0F172A;
  --op-text-secondary: #64748B;
  --op-border: #E2E8F0;

  --op-success: #10B981;
  --op-warning: #F59E0B;
  --op-error: #EF4444;
  --op-info: #0EA5E9;

  --op-focus-ring: #13B5D1;
}
```

---

# 7. Angular Global Theme

Create:

```text
src/styles/_opportuniTY-theme.scss
```

```scss
@use './opportuniTY-tokens' as *;

html,
body {
  background: $op-background;
  color: $op-text-primary;
}

body {
  margin: 0;
  font-family:
    Inter,
    ui-sans-serif,
    system-ui,
    -apple-system,
    BlinkMacSystemFont,
    "Segoe UI",
    sans-serif;
}

a {
  color: $op-secondary;

  &:hover {
    color: $op-secondary-hover;
  }
}
```

Import in:

```text
src/styles.scss
```

```scss
@use './styles/opportuniTY-tokens';
@use './styles/opportuniTY-theme';
```

---

# 8. Buttons

## Primary Button

```scss
.op-btn-primary {
  background: var(--op-primary);
  color: white;

  &:hover {
    background: var(--op-primary-hover);
  }

  &:active {
    background: var(--op-primary-pressed);
  }
}
```

Use for:

- Run Search
- Save
- Export
- Submit
- Confirm

---

## Secondary Button

```scss
.op-btn-secondary {
  background: var(--op-secondary);
  color: white;

  &:hover {
    background: var(--op-secondary-hover);
  }
}
```

Use for:

- Filter
- Tag
- Share
- Secondary workflow actions

---

## Outline Button

```scss
.op-btn-outline {
  background: transparent;
  border: 1px solid var(--op-secondary);
  color: var(--op-secondary);
}
```

---

# 9. Form Controls

```scss
.op-input {
  background: var(--op-surface);
  color: var(--op-text-primary);
  border: 1px solid var(--op-border);

  &:focus {
    outline: none;
    border-color: var(--op-accent);
    box-shadow: 0 0 0 3px rgba(19, 181, 209, 0.18);
  }
}
```

Cyan should be the primary focus color throughout the application.

This helps create a consistent keyboard-navigation experience.

---

# 10. Cards

```scss
.op-card {
  background: var(--op-surface);
  border: 1px solid var(--op-border);
  border-radius: 12px;

  box-shadow:
    0 1px 2px rgba(7, 31, 56, 0.04);
}
```

Use for:

- Document panels
- Review cards
- Search panels
- Dashboard widgets
- Settings groups

---

# 11. Navigation

Recommended sidebar:

```scss
.op-sidebar {
  background: var(--op-surface-dark);
  color: white;
}
```

Navigation items:

```scss
.op-nav-link {
  color: rgba(255, 255, 255, 0.78);

  &:hover,
  &.active {
    color: white;
    background: rgba(19, 181, 209, 0.14);
  }

  &.active {
    border-left: 3px solid var(--op-accent);
  }
}
```

Recommended visual structure:

```text
Deep Navy Sidebar
        |
        +-- Cyan active indicator
        |
        +-- White active text
        |
        +-- Muted white inactive text
```

---

# 12. FREE / Open Source Badge

The FREE concept should be associated primarily with **Teal + Cyan**, not green.

Example:

```scss
.op-badge-free {
  display: inline-flex;
  align-items: center;

  background: var(--op-accent-soft);
  color: var(--op-primary);

  border-radius: 999px;
  padding: 4px 10px;

  font-size: 12px;
  font-weight: 700;
  text-transform: uppercase;
}
```

Example text:

```html
<span class="op-badge-free">
  Free & Open Source
</span>
```

This reinforces that FREE is part of the product identity rather than a temporary promotion.

---

# 13. Status Colors

## Success

```text
#10B981
```

Use for:

- Export complete
- Save successful
- Processing completed

---

## Warning

```text
#F59E0B
```

Use for:

- Partial result
- Potential issue
- Missing metadata
- Review warning

Orange should primarily remain a **warning state**, not a brand identity color.

---

## Error

```text
#EF4444
```

Use for:

- Failed operation
- Delete confirmation
- Security error
- Export failure

---

## Info

```text
#0EA5E9
```

Use for:

- Informational banners
- System messages
- Neutral notices

---

# 14. eDiscovery-Specific UI Mapping

Recommended color usage inside opportuniTY:

| Feature | Color |
|---|---|
| Search Button | Navy |
| Search Result Highlight | Cyan |
| Active Filter | Cyan |
| Tag Button | Teal |
| Selected Tag | Teal |
| Review Queue | Navy |
| Current Document | Cyan outline |
| Coding Panel Header | Navy |
| Responsive / Produced | Green |
| Privileged | Red |
| Needs Review | Orange |
| Informational System Tag | Blue |
| FREE Badge | Teal / Light Cyan |
| Export | Navy |
| Bulk Action | Navy |
| Link / Related Docs | Teal |

---

# 15. Bootstrap / ng-bootstrap

For applications using Bootstrap or ng-bootstrap:

```scss
@use './opportuniTY-tokens' as *;

$primary: $op-primary;
$secondary: $op-secondary;

$success: $op-success;
$warning: $op-warning;
$danger: $op-error;
$info: $op-info;

$body-bg: $op-background;
$body-color: $op-text-primary;

$border-color: $op-border;

$link-color: $op-secondary;
$link-hover-color: $op-secondary-hover;

$focus-ring-color:
  rgba(19, 181, 209, 0.25);
```

Import these overrides **before Bootstrap SCSS**.

---

# 16. TypeScript Theme Object

Create:

```text
src/app/theme/opportuniTY-theme.ts
```

```typescript
export const OPPORTUNITY_THEME = {

  brand: {
    primary: '#123B63',
    secondary: '#009BBF',
    accent: '#13B5D1',
    dark: '#071F38'
  },

  surface: {
    background: '#F8FAFC',
    surface: '#FFFFFF',
    border: '#E2E8F0'
  },

  text: {
    primary: '#0F172A',
    secondary: '#64748B'
  },

  state: {
    success: '#10B981',
    warning: '#F59E0B',
    error: '#EF4444',
    info: '#0EA5E9'
  }

} as const;
```

Useful for:

- Charts
- Dynamic components
- Canvas rendering
- Runtime UI generation
- JavaScript libraries
- Third-party components

---

# 17. Application Icon Guidelines

The opportuniTY application icon should follow the same brand system.

Recommended icon colors:

```text
Background / Main Shape
Navy
#123B63

Interactive / Open element
Teal
#009BBF

Highlight
Cyan
#13B5D1

Negative space
White
#FFFFFF
```

Avoid:

- Orange-dominant icons
- Multiple unrelated colors
- Heavy gradients
- Excessive shadows
- Highly detailed icons that fail at 16×16

---

# 18. Recommended Icon Sizes

Generate application icons in:

```text
16 × 16
24 × 24
32 × 32
48 × 48
64 × 64
128 × 128
192 × 192
256 × 256
512 × 512
1024 × 1024
```

Additional formats:

```text
favicon.ico
opportuniTY.ico
apple-touch-icon.png
android-chrome-192x192.png
android-chrome-512x512.png
maskable-192x192.png
maskable-512x512.png
```

---

# 19. PWA Theme

Recommended Angular PWA manifest:

```json
{
  "name": "opportuniTY",
  "short_name": "opportuniTY",
  "start_url": "/",
  "display": "standalone",
  "background_color": "#F8FAFC",
  "theme_color": "#123B63"
}
```

Browser theme:

```html
<meta
  name="theme-color"
  content="#123B63">
```

---

# 20. Accessibility

Color should never be the only indication of state.

For example:

Bad:

```text
Red row = Privileged
```

Better:

```text
🔒 Privileged
Red status indicator
```

Similarly:

```text
⚠ Needs Review
Orange indicator
```

Focus states should always remain visible:

```text
Cyan outline
+ sufficient contrast
+ keyboard navigation
```

---

# 21. Brand Identity Summary

The visual identity should consistently communicate:

```text
Navy
Trust
Legal
Enterprise
Stability

       +

Teal
Open
Free
Accessible

       +

Cyan
Technology
Innovation
Interaction
```

Result:

> **opportuniTY = Legal technology that feels open, modern, trustworthy, and free.**

---

# 22. Recommended Design Rule

A practical UI ratio:

```text
70% Neutral
20% Navy
8% Teal
2% Cyan / Semantic Highlights
```

This prevents the application from becoming overly colorful while preserving a recognizable brand identity.

For dense eDiscovery review screens, neutral backgrounds should dominate so that coding, search highlights, tags, and warnings remain visually meaningful.

---

# 23. Final Palette

```text
PRIMARY
#123B63

PRIMARY HOVER
#0F3152

PRIMARY PRESSED
#0B2742

SECONDARY
#009BBF

SECONDARY HOVER
#0087A7

SECONDARY PRESSED
#006F8A

ACCENT
#13B5D1

ACCENT SOFT
#D9F5FA

DARK SURFACE
#071F38

BACKGROUND
#F8FAFC

SURFACE
#FFFFFF

TEXT PRIMARY
#0F172A

TEXT SECONDARY
#64748B

BORDER
#E2E8F0

SUCCESS
#10B981

WARNING
#F59E0B

ERROR
#EF4444

INFO
#0EA5E9
```

---

# 24. Product Principle

The opportuniTY UI should feel:

**Professional enough for enterprise legal work, but open and approachable enough to communicate that powerful eDiscovery software does not need to be expensive or closed.**

The visual distinction should be clear:

```text
Traditional Enterprise eDiscovery
        ↓
Heavy / Closed / Expensive

opportuniTY
        ↓
Open / Modern / Free
```

---

**Project:** opportuniTY  
**Category:** Open Source eDiscovery Platform  
**Brand Theme:** Navy + Teal + Cyan  
**Core Concept:** Free & Open Source
