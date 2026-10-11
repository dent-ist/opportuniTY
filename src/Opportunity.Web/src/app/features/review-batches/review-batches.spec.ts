import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../app.config';
import { FakeApi, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import { PERMISSIONS } from '../../core/workspace/sections';
import { ToastService } from '../../ui';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';

const WS = '/api/v1/workspaces/ws-1';
const ME = { userId: 'u-1', displayName: 'Riley Reviewer' };
const SAM = { userId: 'u-2', displayName: 'Sam Senior' };

function batch(
  ordinal: number,
  status: 'available' | 'checkedOut' | 'completed',
  assignee: typeof ME | null = null,
  version = 1,
) {
  return {
    batchId: `b-${ordinal}`,
    batchSetId: 'set-1',
    batchSetName: 'First pass',
    ordinal,
    name: `FP_000${ordinal}`,
    reviewPass: 'firstPass',
    status,
    documentCount: 50,
    assignee,
    statusChangedAt: '2026-10-10T09:00:00Z',
    statusChangedBy: assignee?.userId ?? null,
    version,
  };
}

const SET = {
  batchSetId: 'set-1',
  name: 'First pass',
  batchPrefix: 'FP',
  maxBatchSize: 50,
  keepFamiliesTogether: true,
  keepThreadsTogether: false,
  reviewPass: 'firstPass',
  qcOfBatchSetId: null,
  reviewerGroup: null,
  snapshotId: 'snap-1',
  batchCount: 3,
  documentCount: 150,
  statusCounts: { available: 1, checkedOut: 1, completed: 1 },
  createdBy: SAM,
  createdAt: '2026-10-09T08:00:00Z',
  sort: 'familyDate',
  sortDescending: false,
};

const page = <T>(items: T[]) => ({
  items,
  nextCursor: null,
  total: { value: items.length, relation: 'eq' },
});

describe('Review Batches (#178)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;

  async function setup(
    permissions: string[] = [
      PERMISSIONS.documentView,
      PERMISSIONS.codingWrite,
      PERMISSIONS.reviewBatchManage,
    ],
    configure: (api: FakeApi) => void = () => undefined,
  ) {
    api = new FakeApi()
      .on('GET', '/api/v1/me', { body: { ...ME, email: 'r@example.test', groups: [] } })
      .on('GET', WS, {
        body: { workspaceId: 'ws-1', name: 'Acme v. Widget', displayTimeZone: 'UTC', permissions },
      })
      .on('GET', `${WS}/review-batch-sets`, { body: page([SET]) })
      .on('GET', `${WS}/review-batch-sets/set-1`, { body: SET })
      .on('GET', `${WS}/review-batches`, (req) =>
        req.params.get('assigneeId') === 'me'
          ? { body: page([batch(2, 'checkedOut', ME, 2)]) }
          : {
              body: page([
                batch(1, 'completed', SAM, 3),
                batch(2, 'checkedOut', ME, 2),
                batch(3, 'available'),
              ]),
            },
      );
    configure(api);
    TestBed.configureTestingModule({
      providers: [...provideAppRouting(), ...provideOpportunityHttp(), ...provideFakeApi(api)],
    });
    vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce').mockResolvedValue();
    localStorage.clear();
    harness = await RouterTestingHarness.create();
  }

  async function go(url: string): Promise<void> {
    await TestBed.inject(Router).navigateByUrl(url);
    await settle();
  }

  async function settle(rounds = 6): Promise<void> {
    for (let i = 0; i < rounds; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      await harness.fixture.whenStable();
    }
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const text = (el: Element = root()) => el.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const button = (label: string) =>
    [...root().querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => b.getAttribute('aria-label') === label || text(b).startsWith(label),
    )!;
  const overlayButton = (name: string) =>
    [...document.querySelectorAll<HTMLButtonElement>('.cdk-overlay-container button')].find(
      (b) => text(b) === name,
    )!;

  it('lists my batches and the Batch Sets with Pending / In progress / Completed counts', async () => {
    await setup();
    await go('/w/ws-1/review-batches');
    expect(text(root().querySelector('h1')!)).toBe('Review Batches');
    const mine = root().querySelector('[aria-label="My batches"] tbody tr')!;
    expect(text(mine)).toContain('FP_0002');
    expect(text(mine)).toContain('In progress');
    const set = root().querySelector('[aria-label="Batch Sets"] tbody tr')!;
    expect(text(set)).toContain('First pass');
    expect(text(set)).toContain('FP_0001…');
    expect(text(set)).toContain('150');
    const headers = [...root().querySelectorAll('[aria-label="Batch Sets"] thead th')].map((th) =>
      text(th),
    );
    expect(headers).toEqual(expect.arrayContaining(['Pending', 'In progress', 'Completed']));
    expect(root().querySelector('a[href="/w/ws-1/review-batches/new"]')).not.toBeNull();
    await expectNoAxeViolations(root());
  });

  it('checks out the next batch and opens it in Documents bound to the batch', async () => {
    await setup(undefined, (a) =>
      a.on('POST', `${WS}/review-batch-sets/set-1/check-out-next`, {
        body: batch(3, 'checkedOut', ME, 2),
      }),
    );
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await go('/w/ws-1/review-batches');
    button('Check out next batch of First pass').click();
    await settle();
    expect(api.urls('POST')).toEqual([`${WS}/review-batch-sets/set-1/check-out-next`]);
    expect(navigate).toHaveBeenCalledWith(['/w', 'ws-1', 'documents'], {
      queryParams: { batch: 'b-3' },
    });
  });

  it('says when no batch is pending', async () => {
    await setup(undefined, (a) =>
      a.on('POST', `${WS}/review-batch-sets/set-1/check-out-next`, {
        status: 409,
        body: { status: 409, code: 'conflict', detail: 'No batch of this Batch Set is available.' },
      }),
    );
    const toast = vi.spyOn(TestBed.inject(ToastService), 'show');
    await go('/w/ws-1/review-batches');
    button('Check out next batch of First pass').click();
    await settle();
    expect(toast.mock.calls.at(-1)![0]).toContain('No batch of this Batch Set is pending');
  });

  it('checks a batch in as completed with If-Match', async () => {
    let checkIn: HttpRequest<unknown> | null = null;
    await setup(undefined, (a) =>
      a.on('POST', `${WS}/review-batches/b-2/check-in`, (req) => {
        checkIn = req;
        return { body: batch(2, 'completed', ME, 3) };
      }),
    );
    await go('/w/ws-1/review-batches');
    button('Check in FP_0002').click();
    await settle();
    overlayButton('Mark completed').click();
    await settle();
    expect(checkIn!.body).toEqual({ completed: true });
    expect(checkIn!.headers.get('If-Match')).toBe('"2"');
  });

  it('lets a manager assign and release batches of a set', async () => {
    const writes: HttpRequest<unknown>[] = [];
    await setup(undefined, (a) =>
      a
        .on('GET', `${WS}/review-batch-sets/set-1/assignee-candidates`, {
          body: { items: [ME, SAM] },
        })
        .on('PUT', `${WS}/review-batches/b-1/assignment`, (req) => {
          writes.push(req);
          return { body: batch(1, 'checkedOut', SAM, 4) };
        })
        .on('PUT', `${WS}/review-batches/b-2/assignment`, (req) => {
          writes.push(req);
          return { body: batch(2, 'available', null, 3) };
        }),
    );
    await go('/w/ws-1/review-batches/set-1');
    expect(text(root().querySelector('h1')!)).toBe('First pass');
    expect(text()).toContain('Family Date, earliest first');
    const rows = [...root().querySelectorAll('[aria-label="Batches"] tbody tr')].map((r) =>
      text(r),
    );
    expect(rows[0]).toContain('Completed');
    expect(rows[1]).toContain('In progress');
    expect(rows[1]).toContain('You');
    expect(rows[2]).toContain('Pending');
    await expectNoAxeViolations(root());

    button('Release FP_0002').click();
    await settle();
    overlayButton('Release batch').click();
    await settle();
    expect(writes[0].body).toEqual({ assigneeId: null });
    expect(writes[0].headers.get('If-Match')).toBe('"2"');

    button('Assign FP_0001').click();
    await settle();
    const select = document.querySelector<HTMLSelectElement>('.cdk-overlay-container select')!;
    expect([...select.options].map((o) => o.text.trim())).toContain('Sam Senior');
    select.value = 'u-2';
    select.dispatchEvent(new Event('change'));
    await settle();
    overlayButton('Assign').click();
    await settle();
    expect(writes[1].body).toEqual({ assigneeId: 'u-2' });
    expect(writes[1].headers.get('If-Match')).toBe('"3"');
  });

  it('shows reviewers no manager actions', async () => {
    await setup([PERMISSIONS.documentView, PERMISSIONS.codingWrite]);
    await go('/w/ws-1/review-batches/set-1');
    expect(button('Assign FP_0001')).toBeUndefined();
    expect(button('Release FP_0002')).toBeUndefined();
    expect(button('Check out FP_0003')).toBeDefined();
    expect(button('Review FP_0002')).toBeDefined();
  });

  it('creates a Batch Set from a saved search with size, prefix, families and order', async () => {
    let created: HttpRequest<unknown> | null = null;
    await setup(undefined, (a) =>
      a
        .on('GET', `${WS}/saved-searches`, {
          body: page([{ savedSearchId: 'ss-1', name: 'Smith — not reviewed', lastHitCount: 150 }]),
        })
        .on('POST', `${WS}/snapshots`, {
          status: 201,
          body: {
            snapshotId: 'snap-9',
            name: 'Smith',
            purpose: 'reviewBatch',
            status: 'ready',
            documentCount: 150,
            inclusionCounts: {},
            createdAt: '2026-10-10T09:00:00Z',
          },
        })
        .on('POST', `${WS}/review-batch-sets`, (req) => {
          created = req;
          return { status: 201, body: { ...SET, batchSetId: 'set-9' } };
        }),
    );
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await go('/w/ws-1/review-batches/new');
    expect(text(root().querySelector('legend')!)).toBe('Documents to review');

    button('Create Batch Set').click();
    await settle();
    expect(text(root().querySelector('[role="alert"]')!)).toContain(
      'Freeze the documents to review first.',
    );

    button('Freeze set').click();
    await settle();
    const inputs = [...root().querySelectorAll<HTMLInputElement>('input[type="text"]')];
    const field = (label: string) =>
      inputs.find((i) => text(root().querySelector(`label[for="${i.id}"]`)!).startsWith(label))!;
    for (const [label, value] of [
      ['Batch Set name', 'Smith first pass'],
      ['Batch name prefix', 'SMITH'],
      ['Maximum batch size', '25'],
    ]) {
      const input = field(label);
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    const order = [...root().querySelectorAll<HTMLSelectElement>('select')].find((s) =>
      [...s.options].some((o) => o.value === 'familyDate'),
    )!;
    order.value = 'familyDate';
    order.dispatchEvent(new Event('change'));
    await settle();
    button('Create Batch Set').click();
    await settle();

    expect(created!.body).toEqual({
      name: 'Smith first pass',
      snapshotId: 'snap-9',
      batchPrefix: 'SMITH',
      maxBatchSize: 25,
      keepFamiliesTogether: true,
      keepThreadsTogether: false,
      sort: 'familyDate',
      sortDescending: false,
      reviewerGroup: null,
    });
    expect(created!.headers.get('Idempotency-Key')).toBeTruthy();
    expect(navigate.mock.calls.at(-1)![0]).toEqual(['..', 'set-9']);
  });
});
