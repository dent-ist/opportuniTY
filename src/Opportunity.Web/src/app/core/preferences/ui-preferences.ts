import {
  DOCUMENT,
  Injectable,
  computed,
  effect,
  inject,
  linkedSignal,
  untracked,
} from '@angular/core';
import { PreferenceStorage } from './preference-storage';

export type Theme = 'system' | 'light' | 'dark' | 'high-contrast';
export type Density = 'comfortable' | 'compact';
/** MVP display locales (Q-28). UI text is English in both; only formats differ. */
export type DisplayLocale = 'en-US' | 'en-GB';

export const THEMES: readonly Theme[] = ['system', 'light', 'dark', 'high-contrast'];
export const DENSITIES: readonly Density[] = ['comfortable', 'compact'];
export const DISPLAY_LOCALES: readonly DisplayLocale[] = ['en-US', 'en-GB'];

interface Stored {
  theme?: Theme;
  density?: Density;
  locale?: DisplayLocale;
}

/**
 * Theme, density and display locale as signals, applied to `<html>` as `data-theme` / `data-density`. They
 * follow the user profile (`PreferenceStorage`): a value loaded from the server after sign-in replaces the
 * cached one, and a change made here is saved to the profile.
 */
@Injectable({ providedIn: 'root' })
export class UiPreferences {
  private readonly storage = inject(PreferenceStorage);
  private readonly root = inject(DOCUMENT).documentElement;
  private readonly stored = computed(() => this.storage.read<Stored>('ui') ?? {});

  readonly theme = linkedSignal<Theme>(() => oneOf(this.stored().theme, THEMES, 'system'));
  readonly density = linkedSignal<Density>(() =>
    oneOf(this.stored().density, DENSITIES, 'comfortable'),
  );
  readonly locale = linkedSignal<DisplayLocale>(() =>
    oneOf(this.stored().locale, DISPLAY_LOCALES, 'en-US'),
  );

  constructor() {
    effect(() => {
      const theme = this.theme();
      if (theme === 'system') this.root.removeAttribute('data-theme');
      else this.root.setAttribute('data-theme', theme);
      this.root.setAttribute('data-density', this.density());
      this.root.setAttribute('lang', this.locale());
      const value = { theme, density: this.density(), locale: this.locale() };
      // Only a change made here is saved; the stored value (or the defaults) must not be written back, or it
      // would mask the profile that `PreferenceStorage.load()` is about to bring.
      if (untracked(() => !sameAsStored(this.stored(), value))) this.storage.write('ui', value);
    });
  }
}

function sameAsStored(stored: Stored, value: Required<Stored>): boolean {
  return (
    oneOf(stored.theme, THEMES, 'system') === value.theme &&
    oneOf(stored.density, DENSITIES, 'comfortable') === value.density &&
    oneOf(stored.locale, DISPLAY_LOCALES, 'en-US') === value.locale
  );
}

function oneOf<T>(value: T | undefined, allowed: readonly T[], fallback: T): T {
  return value !== undefined && allowed.includes(value) ? value : fallback;
}
