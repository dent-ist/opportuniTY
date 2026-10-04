import { Chord, Platform } from './chord';

/**
 * Where a command applies. `global` is everywhere in the signed-in app; the others follow the focused region
 * (`[oppCommandScope]`): `grid` the document list, `review` the review-mode page, which contains the
 * `viewer`, `coding` and `related` regions; `redaction` lives inside the viewer (M3).
 */
export type CommandScope =
  'global' | 'grid' | 'review' | 'viewer' | 'coding' | 'related' | 'redaction';

export const SCOPE_LABELS: Record<CommandScope, string> = {
  global: 'Everywhere',
  grid: 'Document list',
  review: 'Review',
  viewer: 'Viewer',
  coding: 'Coding pane',
  related: 'Related items',
  redaction: 'Redaction',
};

/** Scopes that sit inside another one, so both can be active at once (see `scopesOverlap`). */
const SCOPE_PARENTS: Partial<Record<CommandScope, readonly CommandScope[]>> = {
  viewer: ['review'],
  coding: ['review'],
  related: ['review'],
  grid: ['review'], // the list strip in review mode
  redaction: ['viewer', 'review'],
};

/** Two commands may share a chord only when their scopes can never be active together. */
export function scopesOverlap(a: CommandScope, b: CommandScope): boolean {
  if (a === b || a === 'global' || b === 'global') return true;
  return !!SCOPE_PARENTS[a]?.includes(b) || !!SCOPE_PARENTS[b]?.includes(a);
}

export type CommandGroup = 'Review flow' | 'Viewer' | 'Focus' | 'Selection' | 'Actions' | 'Help';
export const COMMAND_GROUPS: readonly CommandGroup[] = [
  'Review flow',
  'Viewer',
  'Focus',
  'Selection',
  'Actions',
  'Help',
];

export interface CommandDefinition {
  readonly id: string;
  readonly label: string;
  readonly group: CommandGroup;
  readonly scope: CommandScope;
  /** Default chords on Windows/Linux. The first is the primary binding. */
  readonly keys: readonly Chord[];
  /** macOS defaults when they differ from `keys` (with `Mod` = ⌘ the same chords usually work). */
  readonly macKeys?: readonly Chord[];
  /** Holding the keys repeats the command (hit and page navigation). */
  readonly repeatable?: boolean;
  /** After the chord, a digit 1–9 within a moment invokes the command again with that number. */
  readonly takesDigit?: string;
}

/**
 * The default key map (familiarity guide §4; conflict matrix in docs/accessibility/wcag-2.2-aa-checklist.md).
 * Our own map, not a copy of any product's: `Alt+Shift` is the command chord, `Ctrl/⌘+Enter` and `Ctrl/⌘+S`
 * save from anywhere, and character keys are optional single-key alternatives. Changing a default here means
 * changing the guide table and the conflict matrix in the same change.
 */
export const COMMANDS: readonly CommandDefinition[] = [
  // Review flow
  {
    id: 'review.saveAndNext',
    label: 'Save & Next',
    group: 'Review flow',
    scope: 'review',
    keys: ['Mod+Enter'],
  },
  {
    id: 'review.saveAndPrevious',
    label: 'Save & Previous',
    group: 'Review flow',
    scope: 'review',
    keys: ['Mod+Shift+Enter'],
  },
  { id: 'review.save', label: 'Save', group: 'Review flow', scope: 'review', keys: ['Mod+KeyS'] },
  {
    id: 'review.cancelEdits',
    label: 'Cancel unsaved edits',
    group: 'Review flow',
    scope: 'review',
    keys: ['Alt+Shift+KeyZ'],
  },
  {
    id: 'document.next',
    label: 'Next document',
    group: 'Review flow',
    scope: 'review',
    keys: ['Alt+Shift+Period', 'BracketRight'],
  },
  {
    id: 'document.previous',
    label: 'Previous document',
    group: 'Review flow',
    scope: 'review',
    keys: ['Alt+Shift+Comma', 'BracketLeft'],
  },
  {
    id: 'review.backToList',
    label: 'Back to list',
    group: 'Review flow',
    scope: 'review',
    keys: ['Alt+Shift+KeyL', 'Escape'],
  },
  {
    id: 'grid.openDocument',
    label: 'Open the focused document in the viewer',
    group: 'Review flow',
    scope: 'grid',
    keys: ['Enter'],
  },
  // Viewer
  {
    id: 'viewer.mode.text',
    label: 'Extracted Text',
    group: 'Viewer',
    scope: 'review',
    keys: ['Alt+Shift+Digit1'],
  },
  {
    id: 'viewer.mode.image',
    label: 'Image',
    group: 'Viewer',
    scope: 'review',
    keys: ['Alt+Shift+Digit2'],
  },
  {
    id: 'viewer.mode.native',
    label: 'Native',
    group: 'Viewer',
    scope: 'review',
    keys: ['Alt+Shift+Digit3'],
  },
  {
    id: 'viewer.mode.production',
    label: 'Production',
    group: 'Viewer',
    scope: 'review',
    keys: ['Alt+Shift+Digit4'],
  },
  {
    id: 'viewer.mode.metadata',
    label: 'Metadata',
    group: 'Viewer',
    scope: 'review',
    keys: ['Alt+Shift+Digit5'],
  },
  {
    id: 'viewer.nextHit',
    label: 'Next hit',
    group: 'Viewer',
    scope: 'review',
    keys: ['F3', 'KeyN'],
    macKeys: ['Mod+KeyG', 'KeyN'],
    repeatable: true,
  },
  {
    id: 'viewer.previousHit',
    label: 'Previous hit',
    group: 'Viewer',
    scope: 'review',
    keys: ['Shift+F3', 'Shift+KeyN'],
    macKeys: ['Mod+Shift+KeyG', 'Shift+KeyN'],
    repeatable: true,
  },
  {
    id: 'viewer.toggleHighlights',
    label: 'Toggle all highlighting',
    group: 'Viewer',
    scope: 'review',
    keys: ['Alt+Shift+KeyH'],
  },
  {
    id: 'viewer.find',
    label: 'Find in document',
    group: 'Viewer',
    scope: 'viewer',
    keys: ['Mod+KeyF'],
  },
  {
    id: 'viewer.nextPage',
    label: 'Next page',
    group: 'Viewer',
    scope: 'viewer',
    keys: ['PageDown'],
    repeatable: true,
  },
  {
    id: 'viewer.previousPage',
    label: 'Previous page',
    group: 'Viewer',
    scope: 'viewer',
    keys: ['PageUp'],
    repeatable: true,
  },
  {
    id: 'viewer.zoomIn',
    label: 'Zoom in',
    group: 'Viewer',
    scope: 'viewer',
    keys: ['Mod+Equal', 'Shift+Equal'],
    repeatable: true,
  },
  {
    id: 'viewer.zoomOut',
    label: 'Zoom out',
    group: 'Viewer',
    scope: 'viewer',
    keys: ['Mod+Minus', 'Minus'],
    repeatable: true,
  },
  {
    id: 'viewer.zoomFit',
    label: 'Fit to width',
    group: 'Viewer',
    scope: 'viewer',
    keys: ['Mod+Digit0', 'Digit0'],
  },
  {
    id: 'viewer.rotate',
    label: 'Rotate page',
    group: 'Viewer',
    scope: 'viewer',
    keys: ['Alt+Shift+KeyR'],
  },
  // Focus regions
  {
    id: 'region.next',
    label: 'Next region',
    group: 'Focus',
    scope: 'global',
    keys: ['Alt+Shift+KeyG'],
  },
  {
    id: 'region.previous',
    label: 'Previous region',
    group: 'Focus',
    scope: 'global',
    keys: ['Alt+Shift+KeyB'],
  },
  {
    id: 'search.focus',
    label: 'Focus keyword search',
    group: 'Focus',
    scope: 'global',
    keys: ['Alt+Shift+KeyK', 'Slash'],
  },
  {
    id: 'grid.toggleFilters',
    label: 'Show or hide the filter row',
    group: 'Focus',
    scope: 'global',
    keys: ['Alt+Shift+KeyU'],
  },
  {
    id: 'coding.focus',
    label: 'Focus coding pane',
    group: 'Focus',
    scope: 'review',
    keys: ['Alt+Shift+KeyC'],
    takesDigit: 'then 1–9 jumps to that coding field',
  },
  {
    id: 'related.focus',
    label: 'Focus related items',
    group: 'Focus',
    scope: 'review',
    keys: ['Alt+Shift+KeyI'],
  },
  // Selection
  {
    id: 'selection.toggleRow',
    label: 'Select or clear the focused row',
    group: 'Selection',
    scope: 'grid',
    keys: ['Space'],
  },
  {
    id: 'selection.allOnPage',
    label: 'Select all on this page',
    group: 'Selection',
    scope: 'grid',
    keys: ['Mod+KeyA'],
  },
  {
    id: 'selection.allResults',
    label: 'Select all results',
    group: 'Selection',
    scope: 'grid',
    keys: ['Alt+Shift+KeyA'],
  },
  {
    id: 'selection.clear',
    label: 'Clear selection',
    group: 'Selection',
    scope: 'grid',
    keys: ['Alt+Shift+Digit0'],
  },
  // Actions
  {
    id: 'actions.massEdit',
    label: 'Mass Edit selected',
    group: 'Actions',
    scope: 'grid',
    keys: ['Alt+Shift+KeyE'],
  },
  {
    id: 'actions.applyToFamily',
    label: 'Apply to Family…',
    group: 'Actions',
    scope: 'review',
    keys: ['Alt+Shift+KeyF'],
  },
  // Help
  {
    id: 'help.shortcuts',
    label: 'Keyboard shortcuts',
    group: 'Help',
    scope: 'global',
    keys: ['Alt+Shift+Slash', 'Shift+Slash'],
  },
];

/** Keys that widgets handle themselves; shown in the cheat sheet, not rebindable. */
export interface FixedKey {
  readonly label: string;
  readonly keys: string;
  readonly scope: CommandScope;
}

export const FIXED_KEYS: readonly FixedKey[] = [
  { label: 'Toggle choice n in the focused choice field', keys: '1–9', scope: 'coding' },
  { label: 'Move between rows and cells', keys: '↑ ↓ ← →', scope: 'grid' },
  {
    label: 'Move a screen up or down, to the first or last loaded row',
    keys: 'PageUp / PageDown / Home / End',
    scope: 'grid',
  },
  { label: 'Sort by the focused column header', keys: 'Enter', scope: 'grid' },
  { label: 'Extend the selection', keys: 'Shift+↑ / Shift+↓', scope: 'grid' },
  { label: 'Move between the filters of the filter row', keys: '← / →', scope: 'grid' },
  { label: 'Clear the focused filter', keys: 'Esc', scope: 'grid' },
  { label: 'Close a dialog or menu', keys: 'Esc', scope: 'global' },
];

const BY_ID = new Map(COMMANDS.map((c) => [c.id, c]));

export function commandById(id: string): CommandDefinition | undefined {
  return BY_ID.get(id);
}

export function defaultKeys(command: CommandDefinition, platform: Platform): readonly Chord[] {
  return platform === 'mac' ? (command.macKeys ?? command.keys) : command.keys;
}
