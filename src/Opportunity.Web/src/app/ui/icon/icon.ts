import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

// Original, minimal 24×24 stroke icons drawn for opportuniTY (no third-party or proprietary icon sets).
const PATHS = {
  close: 'M6 6l12 12M18 6L6 18',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  minus: 'M6 12h12',
  'chevron-left': 'M14.5 6l-6 6 6 6',
  'chevron-right': 'M9.5 6l6 6-6 6',
  'chevron-down': 'M6 9.5l6 6 6-6',
  info: 'M12 21a9 9 0 100-18 9 9 0 000 18zM12 11v6M12 7.5v.5',
  warning: 'M12 3.5L2.5 20h19L12 3.5zM12 10v4.5M12 17v.5',
  error: 'M12 21a9 9 0 100-18 9 9 0 000 18zM12 7.5V13M12 16v.5',
  success: 'M12 21a9 9 0 100-18 9 9 0 000 18zM8 12.5l3 3 5-6',
  lock: 'M6.5 11h11v9h-11zM8.5 11V8a3.5 3.5 0 017 0v3',
  'eye-off': 'M3 12s3.5-6 9-6c1.6 0 3 .5 4.2 1.2M21 12s-3.5 6-9 6c-1.6 0-3-.5-4.2-1.2M4 4l16 16',
  refresh: 'M19 8a8 8 0 10.9 6M19 3.5V8h-4.5',
  clock: 'M12 21a9 9 0 100-18 9 9 0 000 18zM12 7v5l3.5 2',
  search: 'M10.5 17.5a7 7 0 100-14 7 7 0 000 14zM15.5 15.5L21 21',
  filter: 'M4 5h16l-6 7.5v5.5l-4 2v-7.5z',
  'grip-vertical': 'M10 6v.5M14 6v.5M10 12v.5M14 12v.5M10 18v.5M14 18v.5',
  'grip-horizontal': 'M6 10h.5M12 10h.5M18 10h.5M6 14h.5M12 14h.5M18 14h.5',
  inbox: 'M3.5 13.5l3-8h11l3 8M3.5 13.5V19h17v-5.5M3.5 13.5H9l1 2h4l1-2h5.5',
  help: 'M12 21a9 9 0 100-18 9 9 0 000 18zM9.5 9.5a2.5 2.5 0 114 2c-1 .6-1.5 1.2-1.5 2.5M12 17v.5',
  unfold: 'M8 9l4-4 4 4M8 15l4 4 4-4',
  // Review mode: back to the list; panes.
  'back-to-list': 'M10 7H20M10 12H20M10 17H20M7 9l-3 3 3 3',
  'pane-right': 'M4 4.5h16v15H4zM14.5 4.5v15',
  'pane-bottom': 'M4 4.5h16v15H4zM4 14.5h16',
  fold: 'M8 4.5l4 4 4-4M8 19.5l4-4 4 4',
  // Viewer: find, zoom, fit, rotate, pages, native file.
  'chevron-up': 'M6 14.5l6-6 6 6',
  'zoom-in': 'M10.5 17.5a7 7 0 100-14 7 7 0 000 14zM15.5 15.5L21 21M7.5 10.5h6M10.5 7.5v6',
  'zoom-out': 'M10.5 17.5a7 7 0 100-14 7 7 0 000 14zM15.5 15.5L21 21M7.5 10.5h6',
  'fit-width': 'M3.5 5v14M20.5 5v14M7 12h10M9.5 9.5L7 12l2.5 2.5M14.5 9.5L17 12l-2.5 2.5',
  'fit-page': 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5',
  rotate: 'M20 12a8 8 0 11-2.3-5.6M20 4v4h-4',
  thumbnails: 'M4 4.5h6v6H4zM14 4.5h6v6h-6zM4 13.5h6v6H4zM14 13.5h6v6h-6z',
  file: 'M7 3.5h7l4 4v13H7zM14 3.5v4h4',
  download: 'M12 3.5v11M7.5 10l4.5 4.5 4.5-4.5M4 16.5V20h16v-3.5',
  copy: 'M9 8.5h10.5v12H9zM15 8.5V3.5H4.5v12H9',
  // Workspace navigation (sidebar).
  documents: 'M7 3.5h7l4 4v13H7zM14 3.5v4h4M9.5 12h6M9.5 15.5h6',
  report: 'M5 20V11M10 20V5M15 20v-6M20 20V8',
  production: 'M4 8l8-4.5L20 8v8l-8 4.5L4 16zM4 8l8 4.5L20 8M12 12.5v8',
  import: 'M12 3.5v11M7.5 10l4.5 4.5 4.5-4.5M4 16.5V20h16v-3.5',
  export: 'M12 14.5v-11M7.5 8L12 3.5 16.5 8M4 16.5V20h16v-3.5',
  jobs: 'M8.5 6H20M8.5 12H20M8.5 18H20M4 6h.5M4 12h.5M4 18h.5',
  settings: 'M4 7h9M17 7h3M4 17h3M11 17h9M15 4.5v5M9 14.5v5',
  'sidebar-collapse': 'M4 4.5h16v15H4zM9 4.5v15M15.5 9.5L13 12l2.5 2.5',
  'sidebar-expand': 'M4 4.5h16v15H4zM9 4.5v15M13 9.5l2.5 2.5-2.5 2.5',
} as const;

export type IconName = keyof typeof PATHS;
export const ICON_NAMES = Object.keys(PATHS) as IconName[];

/** Decorative icon. Always paired with visible text or an accessible name on the parent control. */
@Component({
  selector: 'opp-icon',
  template: `<svg viewBox="0 0 24 24" focusable="false" aria-hidden="true">
    <path [attr.d]="path()" />
  </svg>`,
  styleUrl: './icon.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'aria-hidden': 'true' },
})
export class Icon {
  readonly name = input.required<IconName>();
  protected readonly path = computed(() => PATHS[this.name()]);
}
