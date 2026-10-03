import { DOCUMENT, Injectable, effect, inject, signal } from '@angular/core';
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

/** Theme, density and display locale as signals, applied to `<html>` as `data-theme` / `data-density`. */
@Injectable({ providedIn: 'root' })
export class UiPreferences {
  private readonly storage = inject(PreferenceStorage);
  private readonly root = inject(DOCUMENT).documentElement;
  private readonly stored = this.storage.read<Stored>('ui') ?? {};

  readonly theme = signal<Theme>(oneOf(this.stored.theme, THEMES, 'system'));
  readonly density = signal<Density>(oneOf(this.stored.density, DENSITIES, 'comfortable'));
  readonly locale = signal<DisplayLocale>(oneOf(this.stored.locale, DISPLAY_LOCALES, 'en-US'));

  constructor() {
    effect(() => {
      const theme = this.theme();
      if (theme === 'system') this.root.removeAttribute('data-theme');
      else this.root.setAttribute('data-theme', theme);
      this.root.setAttribute('data-density', this.density());
      this.root.setAttribute('lang', this.locale());
      this.storage.write('ui', { theme, density: this.density(), locale: this.locale() });
    });
  }
}

function oneOf<T>(value: T | undefined, allowed: readonly T[], fallback: T): T {
  return value !== undefined && allowed.includes(value) ? value : fallback;
}
