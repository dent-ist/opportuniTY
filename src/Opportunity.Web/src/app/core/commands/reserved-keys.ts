import { Chord, Platform, chordParts } from './chord';
import { CommandDefinition, commandById } from './command-catalog';
import type { Keymap } from './keymap';

/**
 * Chords a user may not bind: the browser, the operating system or assistive technology owns them, and a page
 * either cannot intercept them or must not (docs/accessibility/wcag-2.2-aa-checklist.md, shortcut conflict
 * matrix). Some defaults deliberately take over a browser key inside one region (Ctrl+F, F3 and Ctrl+= in the
 * viewer, Ctrl+S in review); those are listed in the matrix as scoped overrides, not here.
 */
const ALWAYS: Record<string, string> = {
  Tab: 'Tab moves focus',
  'Shift+Tab': 'Shift+Tab moves focus',
  F5: 'the browser reloads the page',
  'Mod+KeyR': 'the browser reloads the page',
  'Mod+Shift+KeyR': 'the browser reloads the page',
  'Mod+KeyT': 'the browser opens a tab',
  'Mod+Shift+KeyT': 'the browser reopens a tab',
  'Mod+KeyN': 'the browser opens a window',
  'Mod+Shift+KeyN': 'the browser opens a private window',
  'Mod+KeyW': 'the browser closes the tab',
  'Mod+Shift+KeyW': 'the browser closes the window',
  'Mod+KeyL': 'the browser focuses the address bar',
  'Mod+KeyP': 'the browser prints',
  'Mod+Tab': 'the browser switches tabs',
  'Mod+Shift+Tab': 'the browser switches tabs',
  'Mod+PageUp': 'the browser switches tabs',
  'Mod+PageDown': 'the browser switches tabs',
  'Mod+KeyC': 'copy',
  'Mod+KeyV': 'paste',
  'Mod+KeyX': 'cut',
  'Mod+KeyZ': 'undo',
  'Mod+Shift+KeyZ': 'redo',
  'Mod+KeyY': 'redo',
  'Mod+Shift+KeyI': 'the browser opens developer tools',
  'Mod+Shift+KeyJ': 'the browser opens developer tools',
  'Mod+Shift+KeyC': 'the browser opens developer tools',
  'Mod+Shift+Delete': 'the browser clears browsing data',
  F6: 'the browser moves focus between the page and its toolbars',
  'Shift+F6': 'the browser moves focus between the page and its toolbars',
  F7: 'the browser turns on caret browsing',
  F11: 'the browser enters full screen',
  F12: 'the browser opens developer tools',
};

const PC: Record<string, string> = {
  'Alt+ArrowLeft': 'the browser goes back',
  'Alt+ArrowRight': 'the browser goes forward',
  'Alt+Home': 'the browser opens the home page',
  F10: 'the browser focuses its menu',
  'Alt+F4': 'Windows closes the window',
};

const MAC: Record<string, string> = {
  'Mod+KeyQ': 'macOS quits the browser',
  'Mod+KeyH': 'macOS hides the browser',
  'Mod+KeyM': 'macOS minimises the window',
  'Mod+Backquote': 'macOS switches windows',
  'Mod+Comma': 'the browser opens its settings',
  'Mod+BracketLeft': 'the browser goes back',
  'Mod+BracketRight': 'the browser goes forward',
  'Mod+Shift+Digit3': 'macOS takes a screenshot',
  'Mod+Shift+Digit4': 'macOS takes a screenshot',
  'Mod+Shift+Digit5': 'macOS takes a screenshot',
  F3: 'macOS opens Mission Control',
};

/** Why `chord` cannot be bound, or null when it can. */
export function reservedReason(chord: Chord, platform: Platform): string | null {
  const exact = ALWAYS[chord] ?? (platform === 'mac' ? MAC[chord] : PC[chord]);
  if (exact) return `Reserved: ${exact}.`;
  const { modifiers, code } = chordParts(chord);
  const has = (m: string) => modifiers.includes(m);
  if (has('Meta')) return 'Reserved: the Windows key belongs to the operating system.';
  if (has('Mod') && /^Digit[1-9]$/.test(code)) return 'Reserved: the browser switches tabs.';
  if (platform === 'mac' && has('Ctrl') && has('Alt')) {
    return 'Reserved: Control+Option is the VoiceOver key.';
  }
  if (platform === 'pc' && has('Mod') && has('Alt')) {
    return 'Reserved: Ctrl+Alt is AltGr on many keyboard layouts and rotates the screen on some drivers.';
  }
  if (platform === 'pc' && has('Alt') && !has('Shift') && !has('Mod') && /^Key[A-Z]$/.test(code)) {
    return 'Reserved: Alt+letter opens browser menus. Use Alt+Shift+letter instead.';
  }
  if (code === 'Insert' || code === 'CapsLock' || code === 'NumLock') {
    return 'Reserved: screen readers use this key as their modifier.';
  }
  if (/^(Arrow(Up|Down|Left|Right)|Home|End)$/.test(code) && modifiers.length === 0) {
    return 'Reserved: arrow, Home and End keys move within lists, text and menus.';
  }
  return null;
}

/**
 * Why `chord` cannot be added to `commandId`: reserved by the browser, OS or a screen reader, or already used by
 * another command that can be active at the same time (`conflicts`, which "Replace" can take it from). `null`
 * when it can be added.
 */
export function bindingProblem(
  keymap: Keymap,
  commandId: string,
  chord: Chord,
): { reason: string; conflicts: CommandDefinition[] } | null {
  if (!commandById(commandId)) return { reason: 'Unknown command.', conflicts: [] };
  const reserved = reservedReason(chord, keymap.platform);
  if (reserved) return { reason: reserved, conflicts: [] };
  const conflicts = keymap.conflictsWith(commandId, chord);
  if (conflicts.length === 0) return null;
  return {
    reason: `Already used by ${conflicts.map((c) => `"${c.label}"`).join(' and ')}.`,
    conflicts,
  };
}
