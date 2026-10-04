import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../../app.config';
import { FakeApi, FakeResponse, provideFakeApi } from '../../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../../core/api/http';
import { CommandRegistry } from '../../../core/commands';
import { PreferenceStorage } from '../../../core/preferences/preference-storage';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { expectNoAxeViolations } from '../../../ui/testing/axe.testing';
import { FakeResultOptions, fakePage, hit } from '../grid/grid-fixtures.testing';
import { ReviewCoding } from './review-regions';

const WS = '/api/v1/workspaces/ws-1';
const SEARCHES = `${WS}/searches`;
const PAGES = `${WS}/searches/search-1/pages`;

const CODING_FIELD = {
  fieldId: 'f-resp',
  queryName: 'responsiveness',
  displayName: 'Responsiveness',
  type: 'singleChoice',
  storage: 'coding',
  multiValue: false,
  isSystem: false,
  isHidden: false,
  isSecurityAffecting: false,
  datePrecision: null,
  reducedCapabilities: false,
  capabilities: { sortable: true, filterable: true },
  choices: [
    { choiceId: 1, name: 'Responsive', isActive: true },
    { choiceId: 2, name: 'Not Responsive', isActive: true },
  ],
};

describe('Review mode (E16-T03)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let result: FakeResultOptions;
  let expired: boolean;
  let removed: Set<number>;
  let retrievals: number;

  async function setup(options: { total?: number } = {}): Promise<void> {
    result = { total: options.total ?? 250, pageSize: 100 };
    expired = false;
    removed = new Set();
    retrievals = 0;
    // Pages over the documents still in the set (a recoded document can leave it).
    const page = (n: number) => {
      const docs = Array.from({ length: result.total }, (_, i) => i + 1).filter(
        (d) => !removed.has(d),
      );
      const meta = fakePage({ ...result, total: docs.length }, n);
      return { ...meta, items: docs.slice((n - 1) * 100, n * 100).map((d) => hit(d)) };
    };
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { userId: 'u-1', displayName: 'Alex Reviewer', email: 'a@example.test', groups: [] },
      })
      .on('GET', WS, {
        body: {
          workspaceId: 'ws-1',
          name: 'Acme v. Widget',
          displayTimeZone: 'UTC',
          permissions: [
            PERMISSIONS.documentView,
            PERMISSIONS.searchExecute,
            PERMISSIONS.codingWrite,
          ],
        },
      })
      .on('GET', `${WS}/fields`, {
        body: { items: [CODING_FIELD], nextCursor: null, total: { value: 1, relation: 'eq' } },
      })
      .on('POST', SEARCHES, () => {
        expired = false;
        return { body: page(1) };
      })
      .on('GET', PAGES, (req) =>
        expired
          ? { status: 404, body: { title: 'Not found', status: 404 } }
          : { body: page(Number(req.params.get('cursor')?.slice(1) ?? req.params.get('page'))) },
      );
    for (let n = 1; n <= 260; n++) {
      api
        .on('GET', `${WS}/documents/doc-${n}/text`, (req) => text(req, n))
        .on('POST', `${WS}/documents/doc-${n}/views`, { status: 204 })
        .on('GET', `${WS}/documents/doc-${n}/coding`, {
          body: {
            documentId: `doc-${n}`,
            documentVersion: '7',
            projectedVersion: '7',
            indexingState: 'indexed',
            layoutId: null,
            lastEditor: null,
            fields: n === 3 ? [{ fieldId: 'f-resp', value: 1, editable: true }] : [],
          },
          headers: { ETag: '"7"' },
        });
    }
    TestBed.configureTestingModule({
      providers: [...provideAppRouting(), ...provideOpportunityHttp(), ...provideFakeApi(api)],
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    localStorage.clear();
    harness = await RouterTestingHarness.create();
    await TestBed.inject(Router).navigateByUrl('/w/ws-1/documents');
    await TestBed.inject(CommandRegistry).ready();
    await settle();
  }

  function text(req: HttpRequest<unknown>, n: number): FakeResponse {
    const body = `Extracted text of document ${n}`;
    return {
      status: 206,
      body: new Blob([body]),
      headers: {
        'Content-Range': `bytes 0-${body.length - 1}/${body.length}`,
        'X-Opportunity-Retrieval-Id': `r-${++retrievals}-${req.params.get('purpose')}`,
      },
    };
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      await harness.fixture.whenStable();
    }
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const grid = () => root().querySelector<HTMLElement>('[role="grid"]')!;
  const review = () => root().querySelector<HTMLElement>('opp-review-workspace');
  const region = (name: string) =>
    [...root().querySelectorAll<HTMLElement>('section[data-command-region]')].find(
      (s) => root().querySelector(`#${s.getAttribute('aria-labelledby')}`)?.textContent === name,
    )!;
  const bar = () => review()!.querySelector('.review__bar')!.textContent!.replace(/\s+/g, ' ');
  const viewerText = () => review()!.querySelector('.viewer__text')?.textContent;
  const textRequests = () =>
    api.requests
      .filter((r) => r.method === 'GET' && r.url.endsWith('/text'))
      .map((r) => `${r.url.split('/').at(-2)}:${r.params.get('purpose')}`);
  const views = () =>
    api.requests
      .filter((r) => r.method === 'POST' && r.url.endsWith('/views'))
      .map((r) => `${r.url.split('/').at(-2)}:${(r.body as { retrievalId: string }).retrievalId}`);
  const button = (name: string) =>
    [...root().querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => (b.getAttribute('aria-label') ?? b.textContent?.trim()) === name,
    )!;

  function press(code: string, init: KeyboardEventInit = {}): void {
    (document.activeElement ?? document.body).dispatchEvent(
      new KeyboardEvent('keydown', { code, key: code, bubbles: true, cancelable: true, ...init }),
    );
  }

  async function openRow(index: number): Promise<void> {
    grid().focus();
    for (let i = 0; i < index; i++) press('ArrowDown', { key: 'ArrowDown' });
    press('Enter', { key: 'Enter' });
    await settle();
  }

  it('opens the focused row in Review mode with the viewer, coding and related regions', async () => {
    await setup();
    await openRow(1);
    expect(root().querySelector('.documents__list')?.hasAttribute('hidden')).toBe(true);
    expect(bar()).toContain('Doc 2 of 250');
    expect(bar()).toContain('ACM0000002');
    for (const name of ['Viewer', 'Coding', 'Related Items']) {
      expect(region(name).tagName).toBe('SECTION');
    }
    expect(document.activeElement).toBe(region('Viewer'));
    expect(viewerText()).toBe('Extracted text of document 2');
    expect(region('Coding').textContent).toContain('Responsiveness');
    expect(region('Coding').textContent).toContain('Not set');
    await expectNoAxeViolations(review()!);
  }, 30_000); // axe over the review layout is slow in jsdom on a loaded machine

  it('prefetches the next document as prefetch, records views only for displayed documents, and shows it at once', async () => {
    await setup();
    await openRow(1);
    expect(textRequests()).toEqual(['doc-2:display', 'doc-3:prefetch']);
    expect(views()).toEqual(['doc-2:r-1-display']);

    press('Period', { altKey: true, shiftKey: true, key: '>' });
    await Promise.resolve();
    harness.fixture.detectChanges();
    // Shown from the prefetch in the same frame: no loading state, no display request.
    expect(review()!.querySelector('[data-viewer-document]')?.getAttribute('data-state')).toBe(
      'ready',
    );
    expect(viewerText()).toBe('Extracted text of document 3');
    await settle();
    expect(bar()).toContain('Doc 3 of 250');
    expect(region('Coding').textContent).toContain('Responsive');
    expect(textRequests()).toEqual(['doc-2:display', 'doc-3:prefetch', 'doc-4:prefetch']);
    // The view of doc-3 refers to the prefetch delivery; doc-4 (prefetched only) has no view.
    expect(views()).toEqual(['doc-2:r-1-display', 'doc-3:r-2-prefetch']);

    press('Comma', { altKey: true, shiftKey: true, key: '<' });
    await settle();
    expect(bar()).toContain('Doc 2 of 250');
    expect(textRequests()).toHaveLength(3); // doc-2 was kept as the previous document
  });

  it('follows the list across cursor pages and stops at the end without wrapping', async () => {
    await setup({ total: 102 });
    grid().focus();
    press('End', { key: 'End' });
    press('Enter', { key: 'Enter' });
    await settle();
    expect(bar()).toContain('Doc 100 of 102');
    button('Next document').click();
    await settle();
    expect(api.urls('GET').filter((u) => u.startsWith(PAGES))).toEqual([`${PAGES}?cursor=p2`]);
    expect(bar()).toContain('Doc 101 of 102');
    press('Enter', { ctrlKey: true, key: 'Enter' }); // Save & Next
    await settle();
    expect(bar()).toContain('Doc 102 of 102');
    expect(button('Next document').disabled).toBe(true);
    press('BracketRight', { key: ']' });
    await settle();
    expect(bar()).toContain('Doc 102 of 102');
    expect(review()!.textContent).toContain('End of list.');
  });

  it('keeps working when the search expired and continues from the next document after its own left the set (Q-33)', async () => {
    await setup();
    await openRow(0);
    for (let i = 0; i < 98; i++) {
      press('BracketRight', { key: ']' });
      await harness.fixture.whenStable();
    }
    await settle();
    expect(bar()).toContain('Doc 99 of 250');
    // The search expires, and the next document is recoded out of the set by someone else.
    expired = true;
    removed.add(100);
    press('BracketRight', { key: ']' });
    await settle();
    // On the last loaded row the cursor fetched the next page: the search ran again without doc-100.
    expect(review()!.textContent).toContain('ACM0000100 is no longer in the results.');
    expect(review()!.textContent).toContain('Results refreshed: the search had expired');
    button('Continue from next').click();
    await settle();
    expect(bar()).toContain('Doc 100 of 249');
    expect(bar()).toContain('ACM0000101');
  }, 30_000); // a hundred moves

  it('asks Save / Discard / Cancel before leaving unsaved coding; Save & Next saves first', async () => {
    await setup();
    await openRow(0);
    const coding = harness.fixture.debugElement.query(By.directive(ReviewCoding))
      .componentInstance as ReviewCoding;
    const dirty = signal(true);
    const save = vi.fn(async () => (dirty.set(false), true));
    Object.assign(coding, { dirty, save, discard: () => dirty.set(false) });

    button('Next document').click();
    await settle();
    const dialog = () => document.querySelector<HTMLElement>('[role="alertdialog"]');
    expect(dialog()?.textContent).toContain('Unsaved coding');
    expect(document.activeElement?.textContent?.trim()).toBe('Save');
    [...dialog()!.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Cancel')!
      .click();
    await settle();
    expect(bar()).toContain('Doc 1 of 250');

    button('Next document').click();
    await settle();
    [...dialog()!.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Discard')!
      .click();
    await settle();
    expect(bar()).toContain('Doc 2 of 250');
    expect(save).not.toHaveBeenCalled();

    dirty.set(true);
    button('Save & Next').click();
    await settle();
    expect(save).toHaveBeenCalledTimes(1);
    expect(dialog()).toBeNull();
    expect(bar()).toContain('Doc 3 of 250');
  });

  it('cycles the panes as regions, collapses them and keeps their sizes as user preferences', async () => {
    await setup();
    await openRow(0);
    press('KeyG', { altKey: true, shiftKey: true });
    expect(document.activeElement).toBe(region('Coding'));
    press('KeyG', { altKey: true, shiftKey: true });
    expect(document.activeElement).toBe(region('Related Items'));
    press('KeyG', { altKey: true, shiftKey: true });
    expect(document.activeElement).toBe(region('Viewer'));

    button('Hide coding pane').click();
    await settle();
    const storage = TestBed.inject(PreferenceStorage);
    expect(storage.read('pane.review.coding')).toEqual({ size: 28, collapsed: true });
    expect(button('Coding').getAttribute('aria-expanded')).toBe('false');
    // Alt+Shift+C opens the pane again and focuses it.
    press('KeyC', { altKey: true, shiftKey: true });
    await settle();
    expect(document.activeElement).toBe(region('Coding'));
    expect(storage.read('pane.review.coding')).toEqual({ size: 28, collapsed: false });

    const splitter = root().querySelector<HTMLElement>(
      '[role="separator"][aria-label="Resize coding pane"]',
    )!;
    splitter.focus();
    splitter.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true, cancelable: true }),
    );
    await settle();
    expect(storage.read('pane.review.coding')).toEqual({ size: 30, collapsed: false });
  });

  it('returns to the list with Escape, on the reviewed document, with the selection kept', async () => {
    await setup();
    grid().focus();
    press('Space', { key: ' ' });
    await openRow(0);
    press('BracketRight', { key: ']' });
    press('BracketRight', { key: ']' });
    await settle();
    expect(bar()).toContain('Doc 3 of 250');
    press('Escape', { key: 'Escape' });
    await settle();
    expect(review()).toBeNull();
    expect(root().querySelector('.documents__list')?.hasAttribute('hidden')).toBe(false);
    expect(document.activeElement).toBe(grid());
    const active = grid().getAttribute('aria-activedescendant');
    expect(root().querySelector(`#${active}`)?.textContent?.trim()).toBe('ACM0000003');
    expect(root().textContent).toContain('Selected: 1');
  });
});
