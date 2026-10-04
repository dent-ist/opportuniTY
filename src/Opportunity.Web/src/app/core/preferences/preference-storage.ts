import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type { UserPreferencesResource } from '../api/generated/models';

/** `GET /api/v1/me/preferences`; `PUT` / `DELETE` `…/{key}` (E15-T03). */
export const PREFERENCES_URL = '/api/v1/me/preferences';

const PREFIX = 'opp.pref.';

/**
 * Keys that belong to the user profile and follow the reviewer across browsers and machines. Anything else
 * (for example the recent-workspaces list) stays in this browser only.
 */
const PROFILE_KEY =
  /^(ui|shortcuts|pane\.[A-Za-z0-9_.-]+|grid\.[A-Za-z0-9_.-]+|viewer\.[A-Za-z0-9_.-]+)$/;

/** Wait this long after the last change of a key before saving it, so a burst of changes is one request. */
export const PREFERENCE_SAVE_DELAY_MS = 400;

/**
 * Per-user UI preferences (theme, density, keyboard bindings, pane sizes). Reads are synchronous and come from
 * a browser cache, so the first paint already uses the last known values; after sign-in `load()` replaces the
 * profile keys with the server's copy (the user-preferences API, E15-T03) and every later write is saved there.
 * Reads are signal-tracked: consumers that read inside `computed`/`linkedSignal` follow the server copy.
 * Never store workspace data or anything protected here.
 */
@Injectable({ providedIn: 'root' })
export class PreferenceStorage {
  private readonly http = inject(HttpClient);
  private readonly values = signal<ReadonlyMap<string, unknown>>(readCache());
  private readonly pending = new Map<string, ReturnType<typeof setTimeout>>();
  private loading?: Promise<void>;

  read<T>(key: string): T | undefined {
    return this.values().get(key) as T | undefined;
  }

  write(key: string, value: unknown): void {
    const current = this.values().get(key);
    if (current !== undefined && JSON.stringify(current) === JSON.stringify(value)) return;
    this.set(key, value);
    if (isProfileKey(key)) this.schedule(key);
  }

  /** Back to the default: removes the value here and from the profile. */
  remove(key: string): void {
    if (!this.values().has(key)) return;
    this.set(key, undefined);
    if (isProfileKey(key)) this.schedule(key);
  }

  /**
   * Loads the signed-in user's profile once per page load. The server copy wins for every profile key except
   * those changed here since the page loaded (their save is on its way). A profile key the server does not have
   * yet is kept and saved, which moves preferences made before the profile existed into it. Failure keeps the
   * cached values.
   */
  load(): Promise<void> {
    this.loading ??= firstValueFrom(this.http.get<UserPreferencesResource>(PREFERENCES_URL))
      .then((resource) => {
        const server = (resource?.values ?? {}) as Record<string, unknown>;
        const next = new Map(this.values());
        for (const key of next.keys()) {
          if (isProfileKey(key) && !this.pending.has(key) && !(key in server)) this.schedule(key);
        }
        for (const [key, value] of Object.entries(server)) {
          if (isProfileKey(key) && !this.pending.has(key)) next.set(key, value);
        }
        this.values.set(next);
        writeCache(next);
      })
      .catch(() => {
        // Offline or the API is unavailable: keep working with the cached preferences.
      });
    return this.loading;
  }

  /** Sends pending changes now (tests, and before the page is left). */
  async flush(): Promise<void> {
    const keys = [...this.pending.keys()];
    for (const key of keys) clearTimeout(this.pending.get(key));
    this.pending.clear();
    await Promise.all(keys.map((key) => this.save(key)));
  }

  private set(key: string, value: unknown): void {
    const next = new Map(this.values());
    if (value === undefined) next.delete(key);
    else next.set(key, value);
    this.values.set(next);
    writeCache(next);
  }

  private schedule(key: string): void {
    clearTimeout(this.pending.get(key));
    this.pending.set(
      key,
      setTimeout(() => {
        this.pending.delete(key);
        void this.save(key);
      }, PREFERENCE_SAVE_DELAY_MS),
    );
  }

  private async save(key: string): Promise<void> {
    const url = `${PREFERENCES_URL}/${encodeURIComponent(key)}`;
    const value = this.values().get(key);
    try {
      await firstValueFrom(value === undefined ? this.http.delete(url) : this.http.put(url, value));
    } catch {
      // A preference that did not reach the server stays in this browser; the next change retries.
    }
  }
}

function isProfileKey(key: string): boolean {
  return PROFILE_KEY.test(key);
}

function readCache(): Map<string, unknown> {
  const values = new Map<string, unknown>();
  try {
    const storage = globalThis.localStorage;
    for (let i = 0; storage && i < storage.length; i++) {
      const name = storage.key(i);
      if (!name?.startsWith(PREFIX)) continue;
      try {
        values.set(name.slice(PREFIX.length), JSON.parse(storage.getItem(name) ?? 'null'));
      } catch {
        // A corrupt entry is ignored; the default applies.
      }
    }
  } catch {
    // Storage blocked or unavailable.
  }
  return values;
}

function writeCache(values: ReadonlyMap<string, unknown>): void {
  try {
    const storage = globalThis.localStorage;
    if (!storage) return;
    for (let i = storage.length - 1; i >= 0; i--) {
      const name = storage.key(i);
      if (name?.startsWith(PREFIX) && !values.has(name.slice(PREFIX.length))) {
        storage.removeItem(name);
      }
    }
    for (const [key, value] of values) storage.setItem(PREFIX + key, JSON.stringify(value));
  } catch {
    // Storage full, blocked or unavailable: preferences are a convenience, not state.
  }
}
