import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { describeError, toApiError } from '../core/api/problem-details';
import {
  DATE_FORMATS,
  DENSITIES,
  DateFormat,
  Density,
  THEMES,
  Theme,
  UiPreferences,
  ZoneChoice,
} from '../core/preferences/ui-preferences';
import { ActiveWorkspace } from '../core/workspace/workspace-context';
import { SessionService } from '../core/session/session';
import { Icon, MENU, ToastService } from '../ui';
import { PRODUCT_NAME, SHELL_PATHS } from './navigation';
import { ShortcutDialogs } from './shortcuts/shortcut-dialogs';
import { DisplayFormat } from '../core/i18n';

const THEME_LABELS: Record<Theme, string> = {
  system: 'Match system',
  light: 'Light',
  dark: 'Dark',
  'high-contrast': 'High contrast',
};
const DENSITY_LABELS: Record<Density, string> = {
  comfortable: 'Comfortable',
  compact: 'Compact',
};

/**
 * The signed-in user's menu: display preferences (theme, density, date format, display time zone), keyboard
 * shortcuts, About, Sign out (familiarity guide §2.2). Date format and time zone apply at once, without a reload.
 */
@Component({
  selector: 'opp-user-menu',
  imports: [Icon, RouterLink, ...MENU],
  template: `<button type="button" class="user__trigger" [cdkMenuTriggerFor]="menu">
      <span class="user__initials" aria-hidden="true">{{ initials() }}</span>
      <span class="opp-visually-hidden">User menu: </span>
      <span class="user__name">{{ name() }}</span>
      <opp-icon name="chevron-down" />
    </button>
    <ng-template #menu>
      <div cdkMenu class="opp-menu" aria-label="User menu">
        @if (email(); as email) {
          <div class="opp-menu__heading user__email" aria-hidden="true">{{ email }}</div>
        }
        <div cdkMenuGroup aria-label="Theme">
          <div class="opp-menu__heading" aria-hidden="true">Theme</div>
          @for (theme of themes; track theme.value) {
            <button
              type="button"
              cdkMenuItemRadio
              class="opp-menu__item"
              [cdkMenuItemChecked]="prefs.theme() === theme.value"
              (cdkMenuItemTriggered)="prefs.theme.set(theme.value)"
            >
              <opp-icon class="opp-menu__check" name="check" />{{ theme.label }}
            </button>
          }
        </div>
        <div cdkMenuGroup aria-label="Density">
          <div class="opp-menu__heading" aria-hidden="true">Density</div>
          @for (density of densities; track density.value) {
            <button
              type="button"
              cdkMenuItemRadio
              class="opp-menu__item"
              [cdkMenuItemChecked]="prefs.density() === density.value"
              (cdkMenuItemTriggered)="prefs.density.set(density.value)"
            >
              <opp-icon class="opp-menu__check" name="check" />{{ density.label }}
            </button>
          }
        </div>
        <div cdkMenuGroup aria-label="Date format">
          <div class="opp-menu__heading" aria-hidden="true">Date format</div>
          @for (option of dateFormats; track option.value) {
            <button
              type="button"
              cdkMenuItemRadio
              class="opp-menu__item"
              [cdkMenuItemChecked]="dateFormat() === option.value"
              (cdkMenuItemTriggered)="setDateFormat(option.value)"
            >
              <opp-icon class="opp-menu__check" name="check" />{{ option.label }}
            </button>
          }
        </div>
        <div cdkMenuGroup aria-label="Time zone">
          <div class="opp-menu__heading" aria-hidden="true">Time zone</div>
          @for (option of zones(); track option.value) {
            <button
              type="button"
              cdkMenuItemRadio
              class="opp-menu__item"
              [cdkMenuItemChecked]="prefs.timeZone() === option.value"
              (cdkMenuItemTriggered)="prefs.timeZone.set(option.value)"
            >
              <opp-icon class="opp-menu__check" name="check" />{{ option.label }}
            </button>
          }
        </div>
        <div class="opp-menu__separator" role="separator"></div>
        <button
          type="button"
          cdkMenuItem
          class="opp-menu__item"
          (cdkMenuItemTriggered)="openShortcuts()"
        >
          Keyboard shortcuts…
        </button>
        <a cdkMenuItem class="opp-menu__item" [routerLink]="about">About {{ productName }}</a>
        <button
          type="button"
          cdkMenuItem
          class="opp-menu__item"
          [attr.aria-busy]="signingOut() || null"
          (cdkMenuItemTriggered)="signOut()"
        >
          Sign out
        </button>
      </div>
    </ng-template>`,
  styleUrl: './user-menu.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserMenu {
  protected readonly prefs = inject(UiPreferences);
  private readonly session = inject(SessionService);
  private readonly toasts = inject(ToastService);
  private readonly shortcuts = inject(ShortcutDialogs);
  private readonly format = inject(DisplayFormat);
  private readonly active = inject(ActiveWorkspace);

  protected readonly productName = PRODUCT_NAME;
  protected readonly about = SHELL_PATHS.about;
  protected readonly themes = THEMES.map((value) => ({ value, label: THEME_LABELS[value] }));
  protected readonly densities = DENSITIES.map((value) => ({
    value,
    label: DENSITY_LABELS[value],
  }));
  protected readonly signingOut = signal(false);
  protected readonly dateFormats = DATE_FORMATS;
  protected readonly dateFormat = computed<DateFormat>(() =>
    this.prefs.dateStyle() === 'iso' ? 'iso' : this.prefs.locale(),
  );
  protected readonly zones = computed<{ value: ZoneChoice; label: string }[]>(() => {
    const workspace = this.active.current();
    return [
      {
        value: 'workspace',
        label: workspace ? `Workspace (${this.format.workspaceTimeZone()})` : 'Workspace time zone',
      },
      { value: 'local', label: `This computer (${this.format.browserTimeZone})` },
      { value: 'utc', label: 'UTC' },
    ];
  });

  protected readonly name = computed(() => this.session.principal()?.displayName || 'Signed in');
  protected readonly email = computed(() => this.session.principal()?.email ?? null);
  protected readonly initials = computed(() =>
    this.name()
      .split(/\s+/)
      .filter((part) => part.length > 0)
      .slice(0, 2)
      .map((part) => part[0].toUpperCase())
      .join(''),
  );

  protected setDateFormat(value: DateFormat): void {
    if (value === 'iso') {
      this.prefs.dateStyle.set('iso');
    } else {
      this.prefs.locale.set(value);
      this.prefs.dateStyle.set('locale');
    }
  }

  protected openShortcuts(): void {
    void this.shortcuts.settings();
  }

  protected async signOut(): Promise<void> {
    if (this.signingOut()) return;
    this.signingOut.set(true);
    try {
      await this.session.logout();
    } catch (e) {
      const { title, detail } = describeError(toApiError(e));
      this.toasts.show(`Sign out failed. ${title}. ${detail}`, { tone: 'error' });
      this.signingOut.set(false);
    }
  }
}
