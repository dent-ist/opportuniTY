import { Injectable } from '@angular/core';

/**
 * Per-user UI preference storage (theme, density, pane sizes). This default keeps them in the browser;
 * E15-T03 replaces it via DI with the user-preferences API so layouts follow the reviewer across machines.
 * Never store workspace data or anything protected here.
 */
@Injectable({ providedIn: 'root' })
export class PreferenceStorage {
  private static readonly PREFIX = 'opp.pref.';

  read<T>(key: string): T | undefined {
    try {
      const raw = globalThis.localStorage?.getItem(PreferenceStorage.PREFIX + key);
      return raw == null ? undefined : (JSON.parse(raw) as T);
    } catch {
      return undefined;
    }
  }

  write(key: string, value: unknown): void {
    try {
      globalThis.localStorage?.setItem(PreferenceStorage.PREFIX + key, JSON.stringify(value));
    } catch {
      // Storage full, blocked or unavailable: preferences are a convenience, not state.
    }
  }
}
