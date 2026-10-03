import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { describeError, toApiError } from '../core/api/problem-details';
import {
  DENSITIES,
  Density,
  THEMES,
  Theme,
  UiPreferences,
} from '../core/preferences/ui-preferences';
import { SessionService } from '../core/session/session';
import { Icon, MENU, ToastService } from '../ui';
import { PRODUCT_NAME, SHELL_PATHS } from './navigation';

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

/** The signed-in user's menu: display preferences, About, Sign out (familiarity guide §2.2). */
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
        <div class="opp-menu__separator" role="separator"></div>
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

  protected readonly productName = PRODUCT_NAME;
  protected readonly about = SHELL_PATHS.about;
  protected readonly themes = THEMES.map((value) => ({ value, label: THEME_LABELS[value] }));
  protected readonly densities = DENSITIES.map((value) => ({
    value,
    label: DENSITY_LABELS[value],
  }));
  protected readonly signingOut = signal(false);

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
