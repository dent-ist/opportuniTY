import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ApiError } from '../../core/api/problem-details';
import {
  DENSITIES,
  DISPLAY_LOCALES,
  Density,
  DisplayLocale,
  THEMES,
  Theme,
  UiPreferences,
} from '../../core/preferences/ui-preferences';
import {
  Badge,
  Button,
  Checkbox,
  Count,
  DialogService,
  EmptyState,
  ErrorState,
  FreshnessIndicator,
  Icon,
  IconButton,
  LoadingState,
  Progress,
  ProtectionBadge,
  Select,
  SplitPane,
  StatusPill,
  TABS,
  TextField,
  ToastService,
} from '../../ui';
import { ShowcaseDialog } from './showcase-dialog';

interface KeyboardDoc {
  component: string;
  keys: [key: string, action: string][];
}

/** Keyboard interaction of every core component (ADR-018 §7; E15-T01 acceptance criterion). */
export const KEYBOARD_DOCS: KeyboardDoc[] = [
  { component: 'Button / icon button', keys: [['Enter, Space', 'Activate']] },
  {
    component: 'Text field',
    keys: [
      ['Tab', 'Move in / out'],
      ['Typing', 'Edit (native)'],
    ],
  },
  {
    component: 'Select',
    keys: [
      ['Arrow keys, type-ahead', 'Change choice (native)'],
      ['Alt+Down, Space', 'Open the list'],
    ],
  },
  { component: 'Checkbox', keys: [['Space', 'Toggle']] },
  {
    component: 'Dialog',
    keys: [
      ['Tab, Shift+Tab', 'Cycle focus inside the dialog (trapped)'],
      ['Escape', 'Close; focus returns to the trigger'],
      ['Enter', 'Submit the confirmation form'],
    ],
  },
  {
    component: 'Tabs',
    keys: [
      ['Left / Right', 'Previous / next tab (selection follows focus)'],
      ['Home / End', 'First / last tab'],
      ['Tab', 'Move into the active panel'],
    ],
  },
  {
    component: 'Split pane splitter',
    keys: [
      ['Arrow keys', 'Move the splitter 2 % (Shift: 10 %)'],
      ['Home / End', 'Minimum / maximum size'],
      ['Enter', 'Collapse / restore (collapsible panes)'],
      ['Double-click', 'Restore the default size'],
    ],
  },
  {
    component: 'Toast',
    keys: [
      ['Tab', 'Reach the action and dismiss buttons; focus pauses auto-dismiss'],
      ['Enter, Space', 'Run the action / dismiss'],
    ],
  },
  { component: 'Badges, count, progress, states', keys: [['—', 'Not interactive']] },
];

const SAMPLE_ROWS = Array.from({ length: 40 }, (_, i) => ({
  control: `DEMO-${String(i + 1).padStart(6, '0')}`,
  date: new Date(Date.UTC(2024, 0, 2 + i, 9, 30)).toISOString(),
  from: ['a.lee@example.test', 'j.ortiz@example.test', 'm.chen@example.test'][i % 3],
  subject: ['Quarterly forecast', 'Re: draft agreement', 'Board pack', 'Fwd: pricing'][i % 4],
  designation: (['privileged', 'aeo', null, null, null] as const)[i % 5],
}));

/**
 * Development-only gallery of the design system: every component in every state, theme and density,
 * plus the review-workspace layout (Q-47) built from nested split panes.
 */
@Component({
  selector: 'opp-showcase',
  imports: [
    Badge,
    Button,
    Checkbox,
    Count,
    EmptyState,
    ErrorState,
    FreshnessIndicator,
    Icon,
    IconButton,
    LoadingState,
    Progress,
    ProtectionBadge,
    Select,
    SplitPane,
    StatusPill,
    TextField,
    ...TABS,
  ],
  templateUrl: './showcase.html',
  styleUrl: './showcase.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Showcase {
  protected readonly prefs = inject(UiPreferences);
  private readonly dialogs = inject(DialogService);
  private readonly toasts = inject(ToastService);

  protected readonly keyboardDocs = KEYBOARD_DOCS;
  protected readonly rows = SAMPLE_ROWS;
  protected readonly themeOptions = THEMES.map((t) => ({ value: t, label: label(t) }));
  protected readonly densityOptions = DENSITIES.map((d) => ({ value: d, label: label(d) }));
  protected readonly localeOptions = DISPLAY_LOCALES.map((l) => ({ value: l, label: l }));
  protected readonly responsiveOptions = [
    { value: 'responsive', label: 'Responsive' },
    { value: 'not-responsive', label: 'Not Responsive' },
    { value: 'further-review', label: 'Needs Further Review' },
  ];

  protected readonly name = signal('');
  protected readonly choice = signal('');
  protected readonly privileged = signal(false);
  protected readonly selectAll = signal(false);
  protected readonly viewerMode = signal<string | undefined>('text');
  protected readonly lastConfirm = signal<string>('—');
  protected readonly serverError = new ApiError(503, {
    title: 'Service Unavailable',
    status: 503,
    traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
  });

  protected setTheme(value: string): void {
    this.prefs.theme.set(value as Theme);
  }

  protected setDensity(value: string): void {
    this.prefs.density.set(value as Density);
  }

  protected setLocale(value: string): void {
    this.prefs.locale.set(value as DisplayLocale);
  }

  protected openDialog(): void {
    this.dialogs.open(ShowcaseDialog);
  }

  protected async confirm(typed: boolean): Promise<void> {
    const ok = await this.dialogs.confirm(
      typed
        ? {
            title: 'Apply coding to 12,400 documents?',
            message: 'This bulk edit cannot be undone. Family members are included.',
            confirmLabel: 'Apply coding',
            typedConfirmation: '12400',
          }
        : {
            title: 'Remove Privileged designation?',
            message: 'Reviewers in other groups will be able to see this document.',
            confirmLabel: 'Remove designation',
            tone: 'danger',
          },
    );
    this.lastConfirm.set(ok ? 'Confirmed' : 'Cancelled');
  }

  protected toast(tone: 'info' | 'success' | 'error'): void {
    const messages = {
      info: 'Bulk coding job queued.',
      success: 'Coding saved · search index updating.',
      error: 'Export failed: 3 chunks could not be written.',
    };
    this.toasts.show(messages[tone], {
      tone,
      action: tone === 'error' ? { label: 'View job', run: () => undefined } : undefined,
    });
  }
}

function label(value: string): string {
  return value
    .split('-')
    .map((w) => w[0].toUpperCase() + w.slice(1))
    .join(' ');
}
