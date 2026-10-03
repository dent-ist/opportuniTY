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
  'grip-vertical': 'M10 6v.5M14 6v.5M10 12v.5M14 12v.5M10 18v.5M14 18v.5',
  'grip-horizontal': 'M6 10h.5M12 10h.5M18 10h.5M6 14h.5M12 14h.5M18 14h.5',
  inbox: 'M3.5 13.5l3-8h11l3 8M3.5 13.5V19h17v-5.5M3.5 13.5H9l1 2h4l1-2h5.5',
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
