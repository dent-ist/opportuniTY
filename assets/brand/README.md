# opportuniTY App Logo Assets

These files were generated from the approved opportuniTY logo without redesigning it.

## Branding
- `branding/opportuniTY-logo-transparent.png` — full logo + tagline, transparent background
- `branding/opportuniTY-logo-white-bg.png` — full logo + tagline on white
- `branding/opportuniTY-mark-transparent-1024.png` — icon/mark only, transparent
- `branding/opportuniTY-mark-white-bg-1024.png` — icon/mark only, white background

## App icons
`icons/png/` contains 16, 24, 32, 48, 64, 128, 256, 512 and 1024 px PNG icons.

## Windows / Electron
- `icons/windows/opportuniTY.ico` — multi-resolution Windows icon

Example Electron Builder configuration:

```json
{
  "build": {
    "win": {
      "icon": "build/icons/opportuniTY.ico"
    }
  }
}
```

For BrowserWindow/taskbar use, a 256px or 512px PNG from `icons/png/` is also suitable.

## Web / PWA
`icons/web/` contains:
- favicon.ico
- favicon-16x16.png
- favicon-32x32.png
- apple-touch-icon.png
- android-chrome-192x192.png
- android-chrome-512x512.png
- maskable-192x192.png
- maskable-512x512.png

Suggested HTML:

```html
<link rel="icon" href="/favicon.ico">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
```

## Note
These are raster production assets derived from the selected generated logo.
For a true editable vector master (SVG/AI), the logo should be rebuilt as vector paths rather than auto-traced.

## Where these are used
- Web app: `src/Opportunity.Web/public/` holds copies of `icons/web/*` (favicon, Apple touch icon, PWA/manifest icons, wired in `src/index.html` and `public/site.webmanifest`) and the header mark (`public/brand/opportunity-mark-32.png` / `-64.png` from `icons/png/`).
- Brand colours and theme guidance: [docs/ux/brand-and-angular-theme-guide.md](../../docs/ux/brand-and-angular-theme-guide.md). The app's design tokens live in `src/Opportunity.Web/src/styles/_tokens.scss` (ADR-018); adopting the brand palette there is tracked separately because some brand colours need darker accessible variants for text (WCAG 2.2 AA).
