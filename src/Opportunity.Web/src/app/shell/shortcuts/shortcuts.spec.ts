import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../app.config';
import { FakeApi, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import { CommandRegistry, PLATFORM } from '../../core/commands';
import { PREFERENCES_URL, PreferenceStorage } from '../../core/preferences/preference-storage';
import { PERMISSIONS } from '../../core/workspace/sections';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';

describe('Keyboard shortcut cheat sheet and rebinding', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let saved: Record<string, unknown>;

  async function setup(url: string, profile: Record<string, unknown> = {}): Promise<void> {
    saved = {};
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { userId: 'u-1', displayName: 'Alex Reviewer', email: null, groups: [] },
      })
      .on('GET', '/api/v1/workspaces/ws-1', {
        body: {
          workspaceId: 'ws-1',
          name: 'Acme v. Widget',
          permissions: [PERMISSIONS.documentView],
        },
      })
      .on('GET', PREFERENCES_URL, { body: { values: profile } })
      .on('PUT', `${PREFERENCES_URL}/shortcuts`, (req) => {
        saved['shortcuts'] = req.body;
        return { status: 204 };
      });
    TestBed.configureTestingModule({
      providers: [
        ...provideAppRouting(),
        ...provideOpportunityHttp(),
        ...provideFakeApi(api),
        { provide: PLATFORM, useValue: 'pc' },
      ],
    });
    harness = await RouterTestingHarness.create();
    await TestBed.inject(Router).navigateByUrl(url);
    await TestBed.inject(CommandRegistry).ready();
    await settle();
  }

  async function settle(ms = 0): Promise<void> {
    for (let i = 0; i < 3; i++) {
      await new Promise((resolve) => setTimeout(resolve, ms));
      await harness.fixture.whenStable();
    }
  }

  /** The dialogs are loaded on first use: wait for one to render. */
  async function waitForDialog(title: string): Promise<HTMLElement> {
    for (let i = 0; i < 100; i++) {
      const heading = dialog()?.querySelector('h2')?.textContent;
      if (heading === title) return dialog()!;
      await settle(10);
    }
    throw new Error(`Dialog "${title}" did not open`);
  }

  function press(code: string, init: KeyboardEventInit = {}, target?: Element | null) {
    const event = new KeyboardEvent('keydown', { code, bubbles: true, cancelable: true, ...init });
    (target ?? document.activeElement ?? document.body).dispatchEvent(event);
    return event;
  }

  const dialog = () => document.querySelector<HTMLElement>('[role="dialog"]');
  const rows = () =>
    [...dialog()!.querySelectorAll('.help__row')].map((r) =>
      [
        r.querySelector('dt')!.firstChild!.textContent!.trim(),
        r.querySelector('dd')!.textContent!.trim(),
      ].join(': '),
    );

  // The dialogs are lazy chunks; load them once up front so the first test is not timed on the transform.
  beforeAll(async () => {
    await Promise.all([import('./shortcut-help'), import('./shortcut-settings')]);
  }, 30_000);

  beforeEach(() => localStorage.clear());
  afterEach(() => dialog()?.closest('.cdk-overlay-container')?.replaceChildren());

  it('opens with ? and shows the shortcuts that work where focus is, in words for screen readers', async () => {
    await setup('/w/ws-1/documents');
    document.querySelector<HTMLElement>('main h1')!.focus();
    press('Slash', { key: '?', shiftKey: true });
    await waitForDialog('Keyboard shortcuts');
    expect(rows()).toEqual([
      'Next region: Alt+Shift+GAlt Shift G',
      'Previous region: Alt+Shift+BAlt Shift B',
      'Focus keyword search: Alt+Shift+KAlt Shift K/slash',
      'Keyboard shortcuts: Alt+Shift+/Alt Shift slash?question mark',
      'Close a dialog or menu: Esc',
    ]);
    await expectNoAxeViolations(dialog()!);

    dialog()!.querySelector<HTMLButtonElement>('.dialog__actions button[data-autofocus]')!.click();
    await settle();
    expect(dialog()).toBeNull();
  });

  it('lists every command on request', async () => {
    await setup('/workspaces');
    press('Slash', { altKey: true, shiftKey: true }, document.body);
    await waitForDialog('Keyboard shortcuts');
    dialog()!.querySelector<HTMLInputElement>('input[type="checkbox"]')!.click();
    await settle();
    expect(rows().length).toBeGreaterThan(30);
    expect(rows()).toContain('Save & Next: Ctrl+EnterCtrl Enter');
    await expectNoAxeViolations(dialog()!);
  });

  it('rebinds a command with conflict detection and saves the key map to the user profile', async () => {
    await setup('/w/ws-1/documents');
    document.querySelector<HTMLButtonElement>('opp-user-menu button')!.click();
    await settle();
    [...document.querySelectorAll<HTMLElement>('[role="menuitem"]')]
      .find((i) => i.textContent?.includes('Keyboard shortcuts'))!
      .click();
    await waitForDialog('Customise keyboard shortcuts');
    await expectNoAxeViolations(dialog()!);

    const add = (label: string) =>
      dialog()!.querySelector<HTMLButtonElement>(`button[aria-label="Add key for ${label}"]`)!;

    // A free chord is added at once.
    add('Focus keyword search').click();
    await settle();
    const box = document.activeElement as HTMLElement;
    expect(box.getAttribute('aria-label')).toBe('New key for Focus keyword search');
    press('KeyJ', { altKey: true, shiftKey: true });
    await settle();
    expect(document.activeElement).toBe(add('Focus keyword search'));

    // A browser key is refused with the reason.
    add('Next document').click();
    await settle();
    press('KeyT', { ctrlKey: true });
    await settle();
    expect(dialog()!.querySelector('.settings__error')?.textContent).toContain(
      'Reserved: the browser opens a tab.',
    );

    // A taken chord asks first, then moves.
    press('KeyJ', { altKey: true, shiftKey: true });
    await settle();
    expect(dialog()!.querySelector('[role="alert"]')?.textContent).toContain(
      'Alt+Shift+J: Already used by "Focus keyword search".',
    );
    expect(document.activeElement?.textContent?.trim()).toBe('Use for Next document');
    (document.activeElement as HTMLButtonElement).click();
    await settle();

    // Single-key shortcuts off.
    dialog()!.querySelector<HTMLInputElement>('input[type="checkbox"]')!.click();
    await settle();
    await TestBed.inject(PreferenceStorage).flush();
    expect(saved['shortcuts']).toEqual({
      singleKey: false,
      // Focus keyword search is back to its defaults, so only Next document is stored.
      bindings: { 'document.next': ['Alt+Shift+Period', 'BracketRight', 'Alt+Shift+KeyJ'] },
    });
    await expectNoAxeViolations(dialog()!);
  }, 20_000); // three axe runs over the full key list are slow on a loaded CI machine

  it('uses the key map from the user profile after a reload', async () => {
    await setup('/w/ws-1/documents', {
      shortcuts: { singleKey: false, bindings: { 'search.focus': ['Alt+Shift+KeyJ'] } },
    });
    const textbox = () => document.querySelector('opp-query-bar textarea');
    document.querySelector<HTMLElement>('main h1')!.focus();
    expect(press('Slash', { key: '/' }).defaultPrevented).toBe(false);
    expect(press('KeyK', { altKey: true, shiftKey: true }).defaultPrevented).toBe(false);
    expect(document.activeElement).not.toBe(textbox());
    press('KeyJ', { altKey: true, shiftKey: true });
    expect(document.activeElement).toBe(textbox());
  });
});
