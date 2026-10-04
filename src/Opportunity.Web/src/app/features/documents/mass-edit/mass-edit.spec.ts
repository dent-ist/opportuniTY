import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../../app.config';
import { FakeApi, FakeResponse, provideFakeApi } from '../../../core/api/fake-api.testing';
import type { SearchRequest } from '../../../core/api/generated/models';
import { provideOpportunityHttp } from '../../../core/api/http';
import { CommandRegistry } from '../../../core/commands';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { ToastService } from '../../../ui';
import { expectNoAxeViolations } from '../../../ui/testing/axe.testing';
import { DocumentsPage } from '../documents-page';
import { fakePage } from '../grid/grid-fixtures.testing';
import { MASS_EDIT_POLL_MS } from './mass-edit-jobs';

const WS = '/api/v1/workspaces/ws-1';
const ALL = [
  PERMISSIONS.documentView,
  PERMISSIONS.searchExecute,
  'Coding.Write',
  'Coding.Bulk',
  'Coding.WritePrivilege',
  'Job.ViewAll',
];

function codingField(id: number, displayName: string, type: string, choices: string[], extra = {}) {
  return {
    fieldId: id,
    queryName: displayName.toLowerCase(),
    displayName,
    type,
    storage: 'coding',
    multiValue: type === 'multiChoice',
    isSystem: false,
    isHidden: false,
    isSecurityAffecting: false,
    datePrecision: null,
    reducedCapabilities: false,
    capabilities: { sortable: false, filterable: true, leadingWildcard: false },
    choices: choices.map((name, i) => ({ choiceId: id + i + 1, name, isActive: true })),
    ...extra,
  };
}

const FIELDS = [
  codingField(100, 'Responsiveness', 'singleChoice', ['Responsive', 'Not Responsive']),
  codingField(200, 'Issues', 'multiChoice', ['Pricing', 'Termination']),
  codingField(300, 'Privilege', 'singleChoice', ['Privileged'], { isSecurityAffecting: true }),
];

function snapshot(id: string, count: number | null, status = 'ready', whileIndexing = false) {
  return {
    snapshotId: id,
    status,
    statusReason: null,
    documentCount: count,
    searchGeneration: status === 'ready' ? 18432 : null,
    selectedWhileIndexing: whileIndexing,
    selectedAt: '2026-10-04T10:42:00Z',
    materializedAt: null,
    createdAt: '2026-10-04T10:42:00Z',
  };
}

function job(status: string, applied: number, skipped: number, searchable: boolean) {
  return {
    jobId: 'job-1',
    status,
    statusReason: null,
    committed: {
      itemsApplied: applied,
      itemsUnchanged: 0,
      itemsSkippedConcurrentEdit: skipped,
      itemsFailed: 0,
      itemsExcludedNoAccess: 0,
    },
    indexed: {
      indexTasksApplied: searchable ? 1 : 0,
      indexTasksTotal: 1,
      state: searchable ? 'current' : 'indexing',
    },
  };
}

describe('Mass Actions and Mass Edit (E16-T06)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let announced: string[];
  let jobReads: number;

  async function setup(
    options: {
      permissions?: string[];
      snapshot?: (req: HttpRequest<unknown>) => FakeResponse;
      jobs?: () => FakeResponse;
    } = {},
  ): Promise<void> {
    jobReads = 0;
    const result = { total: 250, pageSize: 100 };
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { userId: 'u-1', displayName: 'Alex Reviewer', email: 'a@example.test', groups: [] },
      })
      .on('GET', WS, {
        body: {
          workspaceId: 'ws-1',
          name: 'Acme v. Widget',
          displayTimeZone: 'UTC',
          permissions: options.permissions ?? ALL,
        },
      })
      .on('GET', `${WS}/fields`, {
        body: { items: FIELDS, nextCursor: null, total: { value: 3, relation: 'eq' } },
      })
      .on('POST', `${WS}/searches`, (req) => {
        const query = (req.body as SearchRequest).query ?? '';
        return { body: fakePage(query ? { total: 12, pageSize: 100 } : result, 1) };
      })
      .on('GET', `${WS}/searches/search-1/pages`, (req) => ({
        body: fakePage(result, Number(req.params.get('cursor')?.slice(1) ?? 1)),
      }))
      .on(
        'POST',
        `${WS}/snapshots`,
        options.snapshot ??
          ((req) => {
            const ids = (req.body as { documentIds?: string[] }).documentIds;
            return { status: 201, body: snapshot('snap-1', ids ? ids.length : 250) };
          }),
      )
      .on('GET', `${WS}/snapshots/snap-1`, { body: snapshot('snap-1', 250) })
      .on('POST', `${WS}/bulk-coding`, { status: 202, body: job('created', 0, 0, false) })
      .on(
        'GET',
        `${WS}/jobs/job-1`,
        options.jobs ??
          (() => {
            jobReads++;
            return {
              body:
                jobReads === 1
                  ? job('running', 100, 0, false)
                  : job('completed', 248, 2, jobReads > 2),
            };
          }),
      );
    TestBed.configureTestingModule({
      providers: [
        ...provideAppRouting(),
        ...provideOpportunityHttp(),
        ...provideFakeApi(api),
        { provide: MASS_EDIT_POLL_MS, useValue: 1 },
      ],
    });
    announced = [];
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockImplementation(async (m) => {
      announced.push(String(m));
    });
    localStorage.clear();
    harness = await RouterTestingHarness.create();
    await TestBed.inject(Router).navigateByUrl('/w/ws-1/documents');
    await TestBed.inject(CommandRegistry).ready();
    await settle();
  }

  async function settle(ms = 0): Promise<void> {
    for (let i = 0; i < 4; i++) {
      await new Promise((resolve) => setTimeout(resolve, ms));
      await harness.fixture.whenStable();
    }
  }

  /** Mass Edit's code is loaded on first use: waits until the dialog is open. */
  async function openMassEdit(): Promise<void> {
    press('KeyE', { altKey: true, shiftKey: true, key: 'E' });
    for (let i = 0; i < 200 && !dialog(); i++) await settle(10);
    await settle();
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const grid = () => root().querySelector<HTMLElement>('[role="grid"]')!;
  const dialog = () => document.querySelector<HTMLElement>('[role="dialog"]');
  const text = (el: Element | null = root()) => el?.textContent?.replace(/\s+/g, ' ') ?? '';
  const buttonIn = (el: Element, name: string) =>
    [...el.querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => b.textContent?.replace(/\s+/g, ' ').trim() === name,
    )!;
  const press = (code: string, init: KeyboardEventInit = {}) =>
    grid().dispatchEvent(
      new KeyboardEvent('keydown', {
        code,
        key: code === 'Space' ? ' ' : code,
        bubbles: true,
        cancelable: true,
        ...init,
      }),
    );
  const change = (label: string) =>
    dialog()!.querySelector<HTMLInputElement>(`input[aria-label="Change ${label}"]`)!;
  function choose(label: string, value: string): void {
    const owner = [...dialog()!.querySelectorAll('label')].find((l) =>
      l.textContent?.trim().startsWith(label),
    )!;
    // A choice row's label wraps its select; a field's label is the select's sibling.
    const select = owner.querySelector('select') ?? owner.parentElement!.querySelector('select')!;
    select.value = value;
    select.dispatchEvent(new Event('change'));
  }
  const requests = (method: string, url: string) =>
    api.requests.filter((r) => r.method === method && r.url === url);

  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  it('selects all results as query + generation, freezes them, confirms the frozen count and runs the job', async () => {
    await setup();
    grid().focus();
    press('KeyA', { ctrlKey: true, key: 'a' });
    await settle();
    expect(text()).toContain('All 100 documents on this page are selected.');
    expect(announced).toContain('100 documents selected.');
    buttonIn(root(), 'Select all 250 results').click();
    await settle(200); // selection announcements are throttled to one per burst
    expect(text()).toContain('Selected: all 250 results');
    expect(text()).toContain('All 250 results are selected: the whole search');
    expect(announced).toContain('All 250 results selected.');

    await openMassEdit();
    expect(dialog()).not.toBeNull();
    expect(text(dialog())).toContain('Selected: all 250 results');
    await expectNoAxeViolations(dialog()!);

    // Continue without a change is refused and explained.
    buttonIn(dialog()!, 'Continue').click();
    await settle();
    expect(text(dialog())).toContain('Tick Change for at least one field.');
    expect(requests('POST', `${WS}/snapshots`)).toHaveLength(0);

    change('Responsiveness').click();
    await settle();
    buttonIn(dialog()!, 'Continue').click();
    await settle();
    expect(text(dialog())).toContain('Choose a value.');
    choose('Responsiveness value', '101');
    change('Issues').click();
    await settle();
    choose('Pricing', 'add');
    choose('Termination', 'remove');
    await settle();
    buttonIn(dialog()!, 'Continue').click();
    await settle();

    const [freeze] = requests('POST', `${WS}/snapshots`);
    // "All results" is the query, never an id list.
    expect(freeze.body).toEqual({ purpose: 'bulkCoding', query: '' });
    expect(freeze.headers.get('Idempotency-Key')).toBeTruthy();
    const confirm = text(dialog());
    expect(confirm).toContain('Frozen set');
    expect(confirm).toContain('250 documents');
    expect(confirm).toMatch(/Frozen at 10:42.* · generation 18,432/);
    expect(confirm).toContain('The list showed 250 documents.');
    expect(confirm).toContain(
      'Documents whose changed fields are edited by someone else after this job starts will be skipped and listed.',
    );
    expect(confirm).toContain('Responsiveness: set to Responsive');
    expect(confirm).toContain('Issues: add Pricing; remove Termination');
    expect(dialog()!.querySelector('[data-typed]')).toBeNull(); // 250 documents, no security field
    expect(document.activeElement?.textContent?.trim()).toBe('Frozen set');
    await expectNoAxeViolations(dialog()!);

    buttonIn(dialog()!, 'Apply to 250 documents').click();
    await settle(5);
    const [submit] = requests('POST', `${WS}/bulk-coding`);
    expect(submit.body).toEqual({
      snapshotId: 'snap-1',
      changes: [
        { fieldId: '100', operation: 'set', value: 101 },
        { fieldId: '200', operation: 'addChoices', value: [201] },
        { fieldId: '200', operation: 'removeChoices', value: [202] },
      ],
    });
    expect(submit.headers.get('Idempotency-Key')).toBeTruthy();
    await settle(5);
    const progress = text(dialog());
    expect(progress).toContain('Mass Edit finished');
    expect(progress).toMatch(/Updated\s*248/);
    expect(progress).toMatch(/Skipped\s*2/);
    expect(progress).toMatch(/Failed\s*0/);
    expect(progress).toContain('Searchable');
    expect(announced).toContain('Mass Edit finished. Updated 248 · Skipped 2 · Failed 0');
    await expectNoAxeViolations(dialog()!);

    buttonIn(dialog()!, 'Close').click();
    await settle();
    expect(dialog()).toBeNull();
    expect(document.activeElement).toBe(grid());
  }, 20_000);

  it('needs the count typed for a security-affecting field, sends checked rows as ids and waits for a large set', async () => {
    let reads = 0;
    await setup({
      permissions: ALL.filter((p) => p !== 'Job.ViewAll'),
      snapshot: (req) => {
        const ids = (req.body as { documentIds: string[] }).documentIds;
        expect(ids).toEqual(['doc-1', 'doc-2']);
        return { status: 202, body: snapshot('snap-1', null, 'materializing') };
      },
    });
    api.on('GET', `${WS}/snapshots/snap-1`, () => {
      reads++;
      return { body: snapshot('snap-1', 2, reads > 1 ? 'ready' : 'materializing', true) };
    });
    grid().focus();
    press('Space');
    press('ArrowDown', { shiftKey: true, key: 'ArrowDown' });
    await settle();
    expect(text()).toContain('Selected: 2');
    await openMassEdit();
    change('Privilege').click();
    await settle();
    choose('Privilege value', '301');
    buttonIn(dialog()!, 'Continue').click();
    await settle(5);
    expect(reads).toBe(2);
    const confirm = text(dialog());
    expect(confirm).toContain('All 2 checked documents are in the frozen set.');
    expect(confirm).not.toContain('generation'); // Q-10: reviewers never see generations
    expect(confirm).toContain(
      'were selected while recent changes to them were still being indexed',
    );
    const apply = buttonIn(dialog()!, 'Apply to 2 documents');
    expect(apply.disabled).toBe(true);
    const typed = dialog()!.querySelector<HTMLInputElement>('[data-typed] input')!;
    expect(document.activeElement).toBe(typed);
    expect(text(dialog())).toContain('Type 2 to confirm');
    expect(text(dialog())).toContain('Required because Privilege affects who may see documents.');
    typed.value = '3';
    typed.dispatchEvent(new Event('input'));
    await settle();
    expect(apply.disabled).toBe(true);
    typed.value = '2';
    typed.dispatchEvent(new Event('input'));
    await settle();
    expect(apply.disabled).toBe(false);
    await expectNoAxeViolations(dialog()!);
  }, 20_000);

  it('keeps following a job after its dialog closes and reports the outcome in a toast', async () => {
    let finish = false;
    await setup({
      jobs: () => ({ body: finish ? job('completed', 1, 0, true) : job('running', 0, 0, false) }),
    });
    const toasts = TestBed.inject(ToastService);
    grid().focus();
    press('Space');
    await settle();
    await openMassEdit();
    change('Responsiveness').click();
    await settle();
    dialog()!
      .querySelectorAll<HTMLInputElement>('input[type="radio"]')
      .forEach((r) => r.value === 'clear' && r.click());
    await settle();
    buttonIn(dialog()!, 'Continue').click();
    await settle();
    expect(text(dialog())).toContain('Responsiveness: clear the value');
    buttonIn(dialog()!, 'Apply to 1 document').click();
    await settle(5);
    expect(text(dialog())).toContain('Mass Edit is running');
    buttonIn(dialog()!, 'Close').click();
    await settle();
    finish = true;
    await settle(10);
    expect(toasts.toasts().map((t) => t.message)).toContain(
      'Mass Edit finished. Updated 1 · Skipped 0 · Failed 0',
    );
  }, 20_000);

  it('drops "all results" when the search changes, asks for a selection first, and hides Mass Actions without Coding.Bulk', async () => {
    await setup();
    const toasts = TestBed.inject(ToastService);
    grid().focus();
    press('KeyE', { altKey: true, shiftKey: true, key: 'E' });
    await settle();
    expect(dialog()).toBeNull();
    expect(toasts.toasts().at(-1)?.message).toMatch(/Select documents first/);

    press('KeyA', { altKey: true, shiftKey: true, key: 'A' });
    await settle();
    expect(text()).toContain('Selected: all 250 results');
    const box = root().querySelector<HTMLInputElement>('input[aria-label="Select ACM0000002"]')!;
    expect(box.checked).toBe(true);
    // Unchecking a row leaves "all results": the loaded rows stay selected except that one.
    box.click();
    await settle();
    expect(text()).toContain('Selected: 99');
    press('KeyA', { altKey: true, shiftKey: true, key: 'A' });
    await settle();
    (
      harness
        .routeDebugElement!.query((d) => d.name === 'opp-review-grid')
        .injector.get(DocumentsPage) as unknown as { search: { set(v: unknown): void } }
    ).search.set({ query: 'responsiveness:Responsive' });
    await settle(5);
    expect(text()).toContain('Selected: 0');
    expect(announced).toContain('Selection of all results cleared: the search changed.');

    TestBed.resetTestingModule();
    await setup({ permissions: [PERMISSIONS.documentView, PERMISSIONS.searchExecute] });
    expect(text()).not.toContain('Mass Actions');
  }, 20_000);

  it('checks a Shift+click range of rows', async () => {
    await setup();
    const box = (n: number) =>
      root().querySelector<HTMLInputElement>(`input[aria-label="Select ACM000000${n}"]`)!;
    box(2).click();
    await settle();
    box(5).dispatchEvent(
      new MouseEvent('click', { shiftKey: true, bubbles: true, cancelable: true }),
    );
    await settle();
    expect(text()).toContain('Selected: 4');
    expect([2, 3, 4, 5].every((n) => box(n).checked)).toBe(true);
  });
});
