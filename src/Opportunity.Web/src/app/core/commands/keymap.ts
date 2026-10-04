import { Injectable, computed, effect, inject, linkedSignal, untracked } from '@angular/core';
import { PreferenceStorage } from '../preferences/preference-storage';
import { Chord, PLATFORM, isCharacterKey, normalizeChord } from './chord';
import {
  COMMANDS,
  CommandDefinition,
  commandById,
  defaultKeys,
  scopesOverlap,
} from './command-catalog';

/** Stored under the `shortcuts` preference: only what differs from the defaults. */
export interface StoredShortcuts {
  /** `false` turns every character-key shortcut off (WCAG 2.1.4). */
  singleKey?: boolean;
  /** Replaced bindings by command id; an empty list means "no keys". */
  bindings?: Record<string, string[]>;
}

export const SHORTCUTS_PREFERENCE = 'shortcuts';
const MAX_KEYS_PER_COMMAND = 4;

export interface BindingConflict {
  readonly chord: Chord;
  readonly commands: readonly CommandDefinition[];
}

/**
 * The user's key map: the defaults (`COMMANDS`), their own rebindings and the single-key switch, saved to the
 * user profile (`PreferenceStorage`, E15-T03) so the same keys work after a reload and on another machine.
 */
@Injectable({ providedIn: 'root' })
export class Keymap {
  private readonly storage = inject(PreferenceStorage);
  readonly platform = inject(PLATFORM);
  private readonly stored = computed(() =>
    sanitize(this.storage.read<StoredShortcuts>(SHORTCUTS_PREFERENCE)),
  );

  /** Character-key shortcuts on (default) or off. */
  readonly singleKeyEnabled = linkedSignal(() => this.stored().singleKey !== false);
  private readonly overrides = linkedSignal(() => this.stored().bindings ?? {});

  /** Every command's keys, before the single-key switch. */
  readonly keys = computed(() => {
    const overrides = this.overrides();
    return new Map(
      COMMANDS.map((c) => [c.id, overrides[c.id] ?? defaultKeys(c, this.platform)] as const),
    );
  });

  /** Chord → commands that answer it now (character keys dropped while single-key shortcuts are off). */
  readonly active = computed(() => {
    const single = this.singleKeyEnabled();
    const byChord = new Map<Chord, CommandDefinition[]>();
    for (const command of COMMANDS) {
      for (const chord of this.keys().get(command.id) ?? []) {
        if (!single && isCharacterKey(chord)) continue;
        const list = byChord.get(chord) ?? [];
        list.push(command);
        byChord.set(chord, list);
      }
    }
    return byChord;
  });

  /** Chords bound to more than one command in scopes that can be active together. Empty for the defaults. */
  readonly conflicts = computed<BindingConflict[]>(() => {
    const found: BindingConflict[] = [];
    const byChord = new Map<Chord, CommandDefinition[]>();
    for (const command of COMMANDS) {
      for (const chord of this.keys().get(command.id) ?? []) {
        byChord.set(chord, [...(byChord.get(chord) ?? []), command]);
      }
    }
    for (const [chord, commands] of byChord) {
      const clashing = commands.filter((a) =>
        commands.some((b) => a !== b && scopesOverlap(a.scope, b.scope)),
      );
      if (clashing.length > 1) found.push({ chord, commands: clashing });
    }
    return found;
  });

  constructor() {
    effect(() => {
      const value = this.toStored(this.singleKeyEnabled(), this.overrides());
      const current = untracked(this.stored);
      if (JSON.stringify(value) === JSON.stringify(current)) return;
      if (Object.keys(value).length === 0) this.storage.remove(SHORTCUTS_PREFERENCE);
      else this.storage.write(SHORTCUTS_PREFERENCE, value);
    });
  }

  keysFor(commandId: string): readonly Chord[] {
    return this.keys().get(commandId) ?? [];
  }

  /** The keys shown for a command: without character keys while those are off. */
  activeKeysFor(commandId: string): readonly Chord[] {
    const keys = this.keysFor(commandId);
    return this.singleKeyEnabled() ? keys : keys.filter((k) => !isCharacterKey(k));
  }

  isCustomised(commandId: string): boolean {
    return commandId in this.overrides();
  }

  /** Other commands that already use `chord` in a scope that can be active together with `commandId`'s. */
  conflictsWith(commandId: string, chord: Chord): CommandDefinition[] {
    const command = commandById(commandId);
    if (!command) return [];
    return COMMANDS.filter(
      (other) =>
        other.id !== commandId &&
        scopesOverlap(other.scope, command.scope) &&
        this.keysFor(other.id).includes(chord),
    );
  }

  /**
   * Adds `chord` to a command. With `replace`, it is first taken away from the commands it conflicts with;
   * otherwise a conflicting chord is not added. Check reserved keys first (`bindingProblem`). Returns false when
   * nothing changed.
   */
  addKey(commandId: string, chord: Chord, { replace = false } = {}): boolean {
    chord = normalizeChord(chord);
    if (!commandById(commandId)) return false;
    const conflicts = this.conflictsWith(commandId, chord);
    if (conflicts.length > 0 && !replace) return false;
    const next = { ...this.overrides() };
    for (const other of conflicts) {
      next[other.id] = this.keysFor(other.id).filter((k) => k !== chord);
    }
    const keys = this.keysFor(commandId).filter((k) => k !== chord);
    next[commandId] = [...keys, chord].slice(-MAX_KEYS_PER_COMMAND);
    this.overrides.set(this.withoutDefaults(next));
    return true;
  }

  removeKey(commandId: string, chord: Chord): void {
    const next = {
      ...this.overrides(),
      [commandId]: this.keysFor(commandId).filter((k) => k !== chord),
    };
    this.overrides.set(this.withoutDefaults(next));
  }

  reset(commandId: string): void {
    const next = { ...this.overrides() };
    delete next[commandId];
    this.overrides.set(next);
  }

  resetAll(): void {
    this.overrides.set({});
    this.singleKeyEnabled.set(true);
  }

  private withoutDefaults(bindings: Record<string, string[]>): Record<string, string[]> {
    const result: Record<string, string[]> = {};
    for (const [id, keys] of Object.entries(bindings)) {
      const command = commandById(id);
      if (!command) continue;
      const defaults = defaultKeys(command, this.platform);
      if (keys.length !== defaults.length || keys.some((k, i) => k !== defaults[i]))
        result[id] = keys;
    }
    return result;
  }

  private toStored(singleKey: boolean, bindings: Record<string, string[]>): StoredShortcuts {
    const value: StoredShortcuts = {};
    if (!singleKey) value.singleKey = false;
    if (Object.keys(bindings).length > 0) value.bindings = bindings;
    return value;
  }
}

/** Drops unknown commands and malformed chords from a stored map (it comes from the server or another version). */
function sanitize(stored: StoredShortcuts | undefined): StoredShortcuts {
  if (!stored || typeof stored !== 'object') return {};
  const value: StoredShortcuts = {};
  if (stored.singleKey === false) value.singleKey = false;
  if (stored.bindings && typeof stored.bindings === 'object') {
    const bindings: Record<string, string[]> = {};
    for (const [id, keys] of Object.entries(stored.bindings)) {
      if (!commandById(id) || !Array.isArray(keys)) continue;
      bindings[id] = keys
        .filter((k): k is string => typeof k === 'string' && /^[A-Za-z0-9+]+$/.test(k))
        .map(normalizeChord)
        .slice(0, MAX_KEYS_PER_COMMAND);
    }
    if (Object.keys(bindings).length > 0) value.bindings = bindings;
  }
  return value;
}
