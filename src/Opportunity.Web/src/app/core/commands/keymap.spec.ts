import { TestBed } from '@angular/core/testing';
import { PreferenceStorage } from '../preferences/preference-storage';
import {
  PLATFORM,
  Platform,
  chordFromEvent,
  detectPlatform,
  isCharacterKey,
  normalizeChord,
} from './chord';
import { formatChord, speakChord } from './chord-format';
import { COMMANDS, commandById, defaultKeys, scopesOverlap } from './command-catalog';
import { Keymap, SHORTCUTS_PREFERENCE } from './keymap';
import { bindingProblem, reservedReason } from './reserved-keys';

function press(code: string, init: KeyboardEventInit = {}): KeyboardEvent {
  return new KeyboardEvent('keydown', { code, ...init });
}

describe('Key chords', () => {
  it('reads chords from KeyboardEvent.code, with Mod = Ctrl on Windows/Linux and ⌘ on macOS', () => {
    expect(chordFromEvent(press('KeyK', { altKey: true, shiftKey: true }), 'pc')).toBe(
      'Alt+Shift+KeyK',
    );
    expect(chordFromEvent(press('Enter', { ctrlKey: true }), 'pc')).toBe('Mod+Enter');
    expect(chordFromEvent(press('Enter', { metaKey: true }), 'mac')).toBe('Mod+Enter');
    expect(chordFromEvent(press('Enter', { ctrlKey: true }), 'mac')).toBe('Ctrl+Enter');
    expect(chordFromEvent(press('Enter', { metaKey: true }), 'pc')).toBe('Meta+Enter');
    expect(chordFromEvent(press('ShiftLeft', { shiftKey: true }), 'pc')).toBeNull();
    // macOS Option produces "˚" for Alt+K: the code still says KeyK.
    expect(
      chordFromEvent(new KeyboardEvent('keydown', { code: 'KeyK', key: '˚', altKey: true }), 'mac'),
    ).toBe('Alt+KeyK');
    expect(normalizeChord('Shift+Option+KeyK')).toBe('Alt+Shift+KeyK');
  });

  it('knows which chords are character keys (WCAG 2.1.4)', () => {
    expect(
      ['Slash', 'Shift+Slash', 'BracketRight', 'KeyN', 'Shift+KeyN', 'Digit0'].every(
        isCharacterKey,
      ),
    ).toBe(true);
    expect(
      ['F3', 'Escape', 'Enter', 'Space', 'PageDown', 'Alt+Shift+KeyK', 'Mod+KeyS'].some(
        isCharacterKey,
      ),
    ).toBe(false);
  });

  it('shows and speaks chords the platform way', () => {
    expect(formatChord('Alt+Shift+Period', 'pc')).toBe('Alt+Shift+.');
    expect(formatChord('Mod+Shift+Enter', 'pc')).toBe('Ctrl+Shift+Enter');
    expect(formatChord('Mod+Shift+Enter', 'mac')).toBe('⌘⇧↩');
    expect(formatChord('Alt+Shift+KeyK', 'mac')).toBe('⌥⇧K');
    expect(formatChord('Shift+Slash', 'pc')).toBe('?');
    expect(formatChord('KeyN', 'pc')).toBe('n');
    expect(speakChord('Alt+Shift+KeyK', 'mac')).toBe('Option Shift K');
    expect(speakChord('Shift+Slash', 'pc')).toBe('question mark');
    expect(speakChord('BracketRight', 'pc')).toBe('right bracket');
  });

  it('detects macOS from the navigator', () => {
    expect(detectPlatform({ platform: 'MacIntel' } as Navigator)).toBe('mac');
    expect(detectPlatform({ platform: 'Win32' } as Navigator)).toBe('pc');
    expect(detectPlatform(undefined)).toBe('pc');
  });
});

describe('Default key map (familiarity guide §4)', () => {
  it('has the commands the review loop needs', () => {
    const ids = [
      'document.next',
      'document.previous',
      'viewer.nextHit',
      'viewer.previousHit',
      'region.next',
      'region.previous',
      'review.save',
      'review.saveAndNext',
      'review.saveAndPrevious',
      'review.cancelEdits',
      'viewer.mode.text',
      'viewer.mode.image',
      'viewer.mode.native',
      'viewer.mode.production',
      'viewer.mode.metadata',
      'selection.toggleRow',
      'selection.allResults',
      'selection.clear',
      'coding.focus',
      'actions.massEdit',
      'actions.applyToFamily',
      'viewer.toggleHighlights',
      'search.focus',
      'help.shortcuts',
    ];
    expect(ids.filter((id) => !commandById(id))).toEqual([]);
    expect(new Set(COMMANDS.map((c) => c.id)).size).toBe(COMMANDS.length);
  });

  it('matches the guide table for the review flow', () => {
    const pc = (id: string) => defaultKeys(commandById(id)!, 'pc');
    expect(pc('review.saveAndNext')).toEqual(['Mod+Enter']);
    expect(pc('review.saveAndPrevious')).toEqual(['Mod+Shift+Enter']);
    expect(pc('review.save')).toEqual(['Mod+KeyS']);
    expect(pc('document.next')).toEqual(['Alt+Shift+Period', 'BracketRight']);
    expect(pc('search.focus')).toEqual(['Alt+Shift+KeyK', 'Slash']);
    expect(pc('help.shortcuts')).toEqual(['Alt+Shift+Slash', 'Shift+Slash']);
    expect(defaultKeys(commandById('viewer.nextHit')!, 'mac')).toEqual(['Mod+KeyG', 'KeyN']);
  });

  for (const platform of ['pc', 'mac'] as Platform[]) {
    it(`has no conflicts and no reserved chords on ${platform}`, () => {
      TestBed.configureTestingModule({ providers: [{ provide: PLATFORM, useValue: platform }] });
      const keymap = TestBed.inject(Keymap);
      expect(keymap.conflicts()).toEqual([]);
      const reserved = COMMANDS.flatMap((c) =>
        defaultKeys(c, platform)
          .filter((k) => reservedReason(k, platform))
          .map((k) => `${c.id}: ${k}`),
      );
      expect(reserved).toEqual([]);
    });
  }

  it('lets sibling regions share a chord but not nested ones', () => {
    expect(scopesOverlap('viewer', 'coding')).toBe(false);
    expect(scopesOverlap('grid', 'viewer')).toBe(false);
    expect(scopesOverlap('viewer', 'review')).toBe(true);
    expect(scopesOverlap('redaction', 'viewer')).toBe(true);
    expect(scopesOverlap('coding', 'global')).toBe(true);
  });
});

describe('Keymap (rebinding)', () => {
  let storage: PreferenceStorage;
  let keymap: Keymap;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [{ provide: PLATFORM, useValue: 'pc' }] });
    storage = TestBed.inject(PreferenceStorage);
    keymap = TestBed.inject(Keymap);
  });

  it('adds a free chord and saves only the difference from the defaults', () => {
    expect(bindingProblem(keymap, 'document.next', 'Alt+Shift+KeyN')).toBeNull();
    expect(keymap.addKey('document.next', 'Alt+Shift+KeyN')).toBe(true);
    TestBed.tick();
    expect(keymap.keysFor('document.next')).toEqual([
      'Alt+Shift+Period',
      'BracketRight',
      'Alt+Shift+KeyN',
    ]);
    expect(storage.read(SHORTCUTS_PREFERENCE)).toEqual({
      bindings: { 'document.next': ['Alt+Shift+Period', 'BracketRight', 'Alt+Shift+KeyN'] },
    });

    keymap.reset('document.next');
    TestBed.tick();
    expect(keymap.isCustomised('document.next')).toBe(false);
    expect(storage.read(SHORTCUTS_PREFERENCE)).toBeUndefined();
  });

  it('refuses a chord used by a command that can be active at the same time, unless replaced', () => {
    const problem = bindingProblem(keymap, 'document.next', 'Alt+Shift+KeyK');
    expect(problem?.reason).toBe('Already used by "Focus keyword search".');
    expect(keymap.addKey('document.next', 'Alt+Shift+KeyK')).toBe(false);

    expect(keymap.addKey('document.next', 'Alt+Shift+KeyK', { replace: true })).toBe(true);
    expect(keymap.keysFor('search.focus')).toEqual(['Slash']);
    expect(
      keymap
        .active()
        .get('Alt+Shift+KeyK')
        ?.map((c) => c.id),
    ).toEqual(['document.next']);
    expect(keymap.conflicts()).toEqual([]);
  });

  it('allows the same chord in regions that are never active together', () => {
    // Find in document lives in the viewer; the grid's Mass Edit can share its chord.
    expect(bindingProblem(keymap, 'actions.massEdit', 'Mod+KeyF')).toBeNull();
  });

  it('refuses browser, OS and screen-reader keys', () => {
    expect(bindingProblem(keymap, 'document.next', 'Mod+KeyT')?.reason).toContain('opens a tab');
    expect(bindingProblem(keymap, 'document.next', 'Alt+KeyF')?.reason).toContain('browser menus');
    expect(bindingProblem(keymap, 'document.next', 'Mod+Digit2')?.reason).toContain(
      'switches tabs',
    );
    expect(bindingProblem(keymap, 'document.next', 'Tab')?.reason).toContain('moves focus');
    expect(reservedReason('Ctrl+Alt+KeyN', 'mac')).toContain('VoiceOver');
  });

  it('turns character-key shortcuts off with one switch, keeping modified ones', () => {
    keymap.singleKeyEnabled.set(false);
    TestBed.tick();
    expect(keymap.active().has('Slash')).toBe(false);
    expect(keymap.active().has('BracketRight')).toBe(false);
    expect(keymap.active().has('Alt+Shift+KeyK')).toBe(true);
    expect(keymap.active().has('F3')).toBe(true);
    expect(keymap.activeKeysFor('search.focus')).toEqual(['Alt+Shift+KeyK']);
    expect(storage.read(SHORTCUTS_PREFERENCE)).toEqual({ singleKey: false });
  });

  it('follows the profile loaded from the server and ignores unknown or malformed entries', () => {
    storage.write(SHORTCUTS_PREFERENCE, {
      singleKey: false,
      bindings: {
        'search.focus': ['Alt+Shift+KeyJ'],
        'no.such.command': ['KeyX'],
        'document.next': [42, 'Bad Key!'],
      },
    });
    TestBed.tick();
    expect(keymap.singleKeyEnabled()).toBe(false);
    expect(keymap.keysFor('search.focus')).toEqual(['Alt+Shift+KeyJ']);
    expect(keymap.keysFor('document.next')).toEqual([]);
    expect(keymap.active().has('Alt+Shift+KeyK')).toBe(false);
  });
});
