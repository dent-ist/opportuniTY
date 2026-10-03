import { InjectionToken } from '@angular/core';

/**
 * Key chords (familiarity guide §4, rule 4): modifiers plus a `KeyboardEvent.code`, written
 * `Mod+Shift+Enter`, `Alt+Shift+KeyK`, `BracketRight`. `Mod` is Ctrl on Windows/Linux and ⌘ on macOS, so one
 * stored binding works on both; `Ctrl` is the macOS Control key. Codes name physical keys, so bindings keep
 * working with dead keys, AltGr layouts and macOS Option characters.
 */
export type Chord = string;
export type Platform = 'pc' | 'mac';

export const PLATFORM = new InjectionToken<Platform>('PLATFORM', {
  factory: () => detectPlatform(globalThis.navigator),
});

export function detectPlatform(nav: Navigator | undefined): Platform {
  const ua = nav as (Navigator & { userAgentData?: { platform?: string } }) | undefined;
  const name = ua?.userAgentData?.platform || ua?.platform || ua?.userAgent || '';
  return /mac|iphone|ipad/i.test(name) ? 'mac' : 'pc';
}

const MODIFIER_ORDER = ['Mod', 'Ctrl', 'Alt', 'Shift'] as const;
type Modifier = (typeof MODIFIER_ORDER)[number];
const MODIFIER_CODES = /^(Control|Alt|Shift|Meta|OS|CapsLock|Fn)(Left|Right)?$/;

/** The chord of a key press, or null for a bare modifier press or a key without a code. */
export function chordFromEvent(event: KeyboardEvent, platform: Platform): Chord | null {
  if (!event.code || MODIFIER_CODES.test(event.code)) return null;
  const mods = new Set<Modifier | 'Meta'>();
  if (platform === 'mac') {
    if (event.metaKey) mods.add('Mod');
    if (event.ctrlKey) mods.add('Ctrl');
  } else {
    if (event.ctrlKey) mods.add('Mod');
    // The Windows key is the OS's; a chord with it never matches a binding.
    if (event.metaKey) mods.add('Meta');
  }
  if (event.altKey) mods.add('Alt');
  if (event.shiftKey) mods.add('Shift');
  return [
    ...MODIFIER_ORDER.filter((m) => mods.has(m)),
    ...(mods.has('Meta') ? ['Meta'] : []),
    event.code,
  ].join('+');
}

/** Normalises a written chord (modifier order, aliases) so it compares equal to `chordFromEvent`. */
export function normalizeChord(chord: string): Chord {
  const parts = chord.split('+').filter((p) => p.length > 0);
  const code = parts.pop() ?? '';
  const mods = new Set(parts.map((p) => (p === 'Control' ? 'Ctrl' : p === 'Option' ? 'Alt' : p)));
  return [
    ...MODIFIER_ORDER.filter((m) => mods.has(m)),
    ...(mods.has('Meta') ? ['Meta'] : []),
    code,
  ].join('+');
}

export function chordParts(chord: Chord): { modifiers: string[]; code: string } {
  const parts = chord.split('+');
  return { code: parts[parts.length - 1], modifiers: parts.slice(0, -1) };
}

/** No Mod, Ctrl, Alt or Meta: the key acts on its own (Shift may be held). */
export function isUnmodified(chord: Chord): boolean {
  return chordParts(chord).modifiers.every((m) => m === 'Shift');
}

const CHARACTER_CODE =
  /^(Key[A-Z]|Digit\d|Numpad(\d|Add|Subtract|Multiply|Divide|Decimal)|Minus|Equal|BracketLeft|BracketRight|Backslash|Semicolon|Quote|Backquote|Comma|Period|Slash|IntlBackslash)$/;

/**
 * A character-key shortcut in the sense of WCAG 2.1.4: a letter, digit, punctuation or symbol key with no
 * modifier other than Shift. These are the bindings the "single-key shortcuts" preference turns off.
 */
export function isCharacterKey(chord: Chord): boolean {
  return isUnmodified(chord) && CHARACTER_CODE.test(chordParts(chord).code);
}

/** True when keys typed at `target` are text input: single keys there must type, not run commands. */
export function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target.isContentEditable) return true;
  if (target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) return true;
  if (target instanceof HTMLInputElement) {
    return !/^(checkbox|radio|button|submit|reset|range|color|file|image)$/.test(target.type);
  }
  const role = target.getAttribute('role');
  return role === 'textbox' || role === 'searchbox' || role === 'combobox' || role === 'spinbutton';
}
