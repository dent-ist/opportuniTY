import { Chord, Platform, chordParts } from './chord';

// Display of chords for the cheat sheet and the rebinding dialog (loaded with them, not with the registry).

const KEY_LABELS: Record<string, string> = {
  Minus: '-',
  Equal: '=',
  BracketLeft: '[',
  BracketRight: ']',
  Backslash: '\\',
  Semicolon: ';',
  Quote: "'",
  Backquote: '`',
  Comma: ',',
  Period: '.',
  Slash: '/',
  Space: 'Space',
  Escape: 'Esc',
  PageUp: 'Page Up',
  PageDown: 'Page Down',
  ArrowUp: '↑',
  ArrowDown: '↓',
  ArrowLeft: '←',
  ArrowRight: '→',
  NumpadAdd: 'Num +',
  NumpadSubtract: 'Num -',
};
/** US-layout characters of Shift + key, used for the familiar labels `?` and `+`. */
const SHIFTED: Record<string, string> = { Slash: '?', Equal: '+', Comma: '<', Period: '>' };
const MAC_SYMBOLS: Record<string, string> = { Mod: '⌘', Ctrl: '⌃', Alt: '⌥', Shift: '⇧' };
const SPOKEN: Record<string, string> = {
  '-': 'minus',
  '=': 'equals',
  '+': 'plus',
  '[': 'left bracket',
  ']': 'right bracket',
  '/': 'slash',
  '?': 'question mark',
  ',': 'comma',
  '.': 'period',
  '<': 'less than',
  '>': 'greater than',
  '↑': 'Up Arrow',
  '↓': 'Down Arrow',
  '←': 'Left Arrow',
  '→': 'Right Arrow',
  '↩': 'Return',
};

export function keyLabel(code: string, platform: Platform = 'pc'): string {
  if (code === 'Enter') return platform === 'mac' ? '↩' : 'Enter';
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);
  if (/^Digit\d$/.test(code)) return code.slice(5);
  if (/^Numpad\d$/.test(code)) return 'Num ' + code.slice(6);
  return KEY_LABELS[code] ?? code;
}

/**
 * How a chord is shown: `Ctrl+Shift+Enter` / `⌘⇧↩`. A Shift-only character key shows the character it types
 * on a US layout (`?`, `+`, `N`), the way reviewers say it.
 */
export function formatChord(chord: Chord, platform: Platform): string {
  const { modifiers, code } = chordParts(chord);
  if (modifiers.length === 1 && modifiers[0] === 'Shift' && SHIFTED[code]) return SHIFTED[code];
  if (modifiers.length === 0 && /^Key[A-Z]$/.test(code)) return code.slice(3).toLowerCase();
  const key = keyLabel(code, platform);
  if (platform === 'mac') return modifiers.map((m) => MAC_SYMBOLS[m] ?? m).join('') + key;
  return [...modifiers.map((m) => (m === 'Mod' ? 'Ctrl' : m)), key].join('+');
}

/** A screen-reader friendly reading of `formatChord` (`⌥⇧K` → "Option Shift K"). */
export function speakChord(chord: Chord, platform: Platform): string {
  const { modifiers, code } = chordParts(chord);
  const names: Record<string, string> =
    platform === 'mac'
      ? { Mod: 'Command', Ctrl: 'Control', Alt: 'Option', Shift: 'Shift' }
      : { Mod: 'Ctrl', Ctrl: 'Ctrl', Alt: 'Alt', Shift: 'Shift' };
  const shown = formatChord(chord, platform);
  if (
    modifiers.length === 0 ||
    (modifiers.length === 1 && modifiers[0] === 'Shift' && SHIFTED[code])
  ) {
    return SPOKEN[shown] ?? shown;
  }
  const key = keyLabel(code, platform);
  return [...modifiers.map((m) => names[m] ?? m), SPOKEN[key] ?? key].join(' ');
}
