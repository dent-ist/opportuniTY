import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../app.config';
import { FakeApi, provideFakeApi } from '../../core/api/fake-api.testing';
import type {
  AuditEventResource,
  AuditExportResource,
  CodingHistoryEntryResource,
  FieldResource,
} from '../../core/api/generated/models';
import { provideOpportunityHttp } from '../../core/api/http';
import { PERMISSIONS } from '../../core/workspace/sections';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { actionLabel, toQuery, EMPTY_FILTER } from './audit-log-model';
import { DocumentHistory } from './document-history';
import { DocumentHistoryApi, valueText } from './document-history-api';

const WS = '/api/v1/workspaces/ws-1';

function event(n: number, overrides: Partial<AuditEventResource> = {}): AuditEventResource {
  return {
    eventId: `ev-${n}`,
    occurredAt: `2026-10-10T09:00:0${n}Z`,
    recordedAt: `2026-10-10T09:00:0${n}Z`,
    category: 'Document',
    action: 'Viewed',
    outcome: 'success',
    reasonCode: null,
    actor: { type: 'user', id: 'u-2', displayName: 'Sam Reviewer' },
    onBehalfOf: null,
    accessPath: 'normal',
    resource: { type: 'Document', id: `doc-${n}`, controlNumber: `ABC-00000${n}` },
    jobId: null,
    chunkSequence: null,
    snapshotId: null,
    searchGeneration: null,
    correlationId: `corr-${n}`,
    clientIp: '203.0.113.7',
    details: { Purpose: 'Display' },
    searchText: null,
    sequence: n,
    ...overrides,
  };
}

const completedExport: AuditExportResource = {
  exportId: 'exp-1',
  jobId: 'job-9',
  status: 'completed',
  statusReason: null,
  filters: {
    from: null,
    to: null,
    category: 'Document',
    action: null,
    actorId: null,
    documentId: null,
    jobId: null,
    outcome: null,
  },
  includeSearchText: false,
  occurredBefore: '2026-10-10T10:00:00Z',
  createdAt: '2026-10-10T10:00:00Z',
  completedAt: '2026-10-10T10:00:05Z',
  rowCount: 2,
  byteCount: 900,
  contentSha256: 'ab'.repeat(32),
};

describe('Audit log viewer (E14-T04)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;

  async function setup(permissions = [PERMISSIONS.documentView, PERMISSIONS.auditRead]) {
    api = new FakeApi()
      .on('GET', '/api/v1/me', {
        body: { userId: 'u-1', displayName: 'Alex', email: 'a@example.test', groups: [] },
      })
      .on('GET', WS, {
        body: { workspaceId: 'ws-1', name: 'Acme v. Widget', displayTimeZone: 'UTC', permissions },
      })
      .on('GET', `${WS}/audit-actions`, {
        body: [
          { category: 'Document', action: 'Viewed' },
          { category: 'Document', action: 'NativeDownloaded' },
          { category: 'Coding', action: 'Changed' },
          { category: 'AuthZ', action: 'Denied' },
        ],
      })
      .on('GET', `${WS}/audit-events`, (req) => {
        if (req.params.get('cursor') === 'c-2') {
          return {
            body: { items: [event(1)], nextCursor: null, total: { value: 1, relation: 'gte' } },
          };
        }
        const items = [
          event(3, {
            category: 'Coding',
            action: 'BulkChunkApplied',
            resource: { type: 'Job', id: 'job-7', controlNumber: null },
            jobId: 'job-7',
            chunkSequence: 2,
          }),
          event(2, {
            category: 'AuthZ',
            action: 'Denied',
            outcome: 'denied',
            reasonCode: 'PermissionNotGranted',
            actor: { type: 'user', id: 'u-3', displayName: 'Pat Outsider' },
          }),
        ];
        return { body: { items, nextCursor: 'c-2', total: { value: 2, relation: 'gte' } } };
      })
      .on('GET', `${WS}/audit-exports`, {
        body: { items: [completedExport], nextCursor: null, total: { value: 1, relation: 'eq' } },
      })
      .on('POST', `${WS}/audit-exports`, {
        status: 202,
        body: { ...completedExport, exportId: 'exp-2', status: 'queued', rowCount: null },
      });
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

  async function settle(rounds = 8): Promise<void> {
    for (let i = 0; i < rounds; i++) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      await harness.fixture.whenStable();
    }
  }

  const root = () => harness.fixture.nativeElement as HTMLElement;
  const text = (el: Element = root()) => el.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const button = (label: string) =>
    [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      text(b).startsWith(label),
    )!;
  const eventRequests = () =>
    api.requests.filter((r) => r.method === 'GET' && r.url === `${WS}/audit-events`);
  const lastEvents = () => eventRequests().at(-1)!;

  it('lists the trail newest first one page at a time, and its filters combine in the URL', async () => {
    await setup();
    await go('/w/ws-1/admin/audit');

    expect(text(root().querySelector('h1')!)).toBe('Audit');
    expect(lastEvents().params.get('limit')).toBe('100');
    const rows = [...root().querySelectorAll('[aria-label="Audit events"] tbody tr')];
    expect(rows.map((r) => text(r.querySelector('th')!))).toEqual([
      'Coding › Bulk chunk applied',
      'Access denied › Denied',
    ]);
    expect(text(rows[1])).toContain('Pat Outsider');
    expect(text(rows[1])).toContain('Denied');
    expect(rows[0].querySelector('a')?.getAttribute('href')).toBe('/w/ws-1/jobs/job-7');
    await expectNoAxeViolations(root());

    // More events come by cursor, never all at once.
    button('Load more events').click();
    await settle();
    expect(lastEvents().params.get('cursor')).toBe('c-2');
    expect(root().querySelectorAll('[aria-label="Audit events"] tbody tr').length).toBe(3);
    expect(text()).toContain('Showing 3 events.');
    expect(
      root().querySelector('[aria-label="Audit events"] tbody tr:last-child')?.textContent,
    ).toContain('ABC-000001');

    // Category, then an action of it, then outcome: each in the URL and in the request, combined.
    const category = root().querySelectorAll<HTMLSelectElement>('select')[0];
    const action = root().querySelectorAll<HTMLSelectElement>('select')[1];
    expect(action.disabled).toBe(true);
    category.value = 'Document';
    category.dispatchEvent(new Event('change'));
    await settle();
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/admin/audit?category=Document');
    expect(action.disabled).toBe(false);
    action.value = 'NativeDownloaded';
    action.dispatchEvent(new Event('change'));
    await settle();
    const outcome = root().querySelectorAll<HTMLSelectElement>('select')[2];
    outcome.value = 'denied';
    outcome.dispatchEvent(new Event('change'));
    await settle();
    const params = lastEvents().params;
    expect([params.get('category'), params.get('action'), params.get('outcome')]).toEqual([
      'Document',
      'NativeDownloaded',
      'denied',
    ]);

    // Text filters apply with Apply.
    const documentField = [...root().querySelectorAll<HTMLInputElement>('input')].find(
      (i) => i.placeholder === 'Control Number',
    )!;
    documentField.value = 'ABC-000002';
    documentField.dispatchEvent(new Event('input'));
    button('Apply').click();
    await settle();
    expect(lastEvents().params.get('controlNumber')).toBe('ABC-000002');
    expect(lastEvents().params.get('category')).toBe('Document');

    button('Clear filters').click();
    await settle();
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/admin/audit');
  }, 30_000);

  it('opens a row’s details and narrows the list to its user or job', async () => {
    await setup();
    await go('/w/ws-1/admin/audit');

    const toggle = button('Details');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    await settle();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    const detail = root().querySelector(`#${toggle.getAttribute('aria-controls')}`)!;
    const facts = [...detail.querySelectorAll('dt')].map((dt) => [
      text(dt),
      text(dt.nextElementSibling!),
    ]);
    expect(facts).toContainEqual(['Correlation ID', 'corr-3']);
    expect(facts).toContainEqual(['Purpose', 'Display']);
    expect(text(detail)).toContain('sealed in the hash chain (#3)');
    await expectNoAxeViolations(root());

    button('Only this job').click();
    await settle();
    expect(lastEvents().params.get('jobId')).toBe('job-7');

    button('Details').click();
    await settle();
    button('Only this user').click();
    await settle();
    expect(lastEvents().params.get('actorId')).toBe('u-2');
    expect(text(root().querySelector('.audit__chip')!)).toContain('User: Sam Reviewer');
    button('Remove').click();
    await settle();
    expect(lastEvents().params.get('actorId')).toBeNull();
  }, 30_000);

  it('exports the filtered trail as a job and offers completed exports for download', async () => {
    await setup();
    await go('/w/ws-1/admin/audit?category=Coding&outcome=denied');

    const download = [...root().querySelectorAll<HTMLAnchorElement>('a')].find((a) =>
      text(a).startsWith('Download CSV'),
    )!;
    expect(download.getAttribute('href')).toBe(`${WS}/audit-exports/exp-1/content`);
    expect(download.hasAttribute('download')).toBe(true);

    button('Export to CSV').click();
    await settle();
    const post = api.requests.find((r) => r.method === 'POST') as HttpRequest<{
      category: string;
      outcome: string;
    }>;
    expect(post.url).toBe(`${WS}/audit-exports`);
    expect(post.headers.get('Idempotency-Key')).toBeTruthy();
    expect(post.body).toEqual(expect.objectContaining({ category: 'Coding', outcome: 'denied' }));
    const exports = root().querySelector('[aria-label="Your audit exports"]')!;
    expect(text(exports)).toContain('Queued');
    expect(text(exports)).toContain('Document');
    await expectNoAxeViolations(root());
  }, 30_000);

  it('is not reachable without Audit.Read', async () => {
    await setup([PERMISSIONS.documentView]);
    await go('/w/ws-1/admin/audit');
    expect(eventRequests()).toEqual([]);
  });
});

describe('Audit filter model', () => {
  it('turns local days into an instant range with an exclusive end and drops an orphan action', () => {
    const q = toQuery({ ...EMPTY_FILTER, from: '2026-10-01', to: '2026-10-01', action: 'Viewed' });
    expect(new Date(q.to!).getTime() - new Date(q.from!).getTime()).toBe(24 * 3600 * 1000);
    expect(q.action).toBeUndefined();
  });

  it('spells actions as words', () => {
    expect(actionLabel('ResultsPageServed')).toBe('Results page served');
    expect(actionLabel('SavedSearch.Created')).toBe('Saved search created');
  });
});

@Component({
  imports: [DocumentHistory],
  template: `<opp-document-history [documentId]="documentId()" controlNumber="ABC-000001" />`,
})
class HistoryHost {
  readonly documentId = signal('doc-1');
}

describe('Document History (E14-T04)', () => {
  const field: FieldResource = {
    fieldId: 1001,
    displayName: 'Responsiveness',
    queryName: 'Responsiveness',
    type: 'singleChoice',
    storage: 'coding',
    isHidden: false,
    isSystem: false,
    isSecurityAffecting: false,
    multiValue: false,
    reducedCapabilities: false,
    datePrecision: null,
    capabilities: {} as FieldResource['capabilities'],
    choices: [
      { choiceId: 1, name: 'Responsive' } as never,
      { choiceId: 2, name: 'Not Responsive' } as never,
    ],
  };

  function entry(n: number, overrides: Partial<CodingHistoryEntryResource> = {}) {
    return {
      eventId: `ce-${n}`,
      occurredAt: '2026-10-10T09:00:00Z',
      fieldId: 1001,
      fieldName: 'Responsiveness',
      kind: 'changed',
      oldValue: null,
      newValue: 1,
      documentVersion: n,
      actor: { userId: 'u-2', displayName: 'Sam Reviewer' },
      actorType: 'human',
      source: 'interactive',
      jobId: null,
      originEventId: null,
      ...overrides,
    } as CodingHistoryEntryResource;
  }

  it('lists who changed which field from what to what, how, and pages by cursor', async () => {
    const calls: { fieldId: string | null; cursor?: string | null }[] = [];
    const fake: DocumentHistoryApi = {
      fields: async () => [field],
      history: async (_doc: string, fieldId: string | null, cursor?: string | null) => {
        calls.push({ fieldId, cursor });
        return cursor
          ? { items: [entry(1)], nextCursor: null }
          : {
              items: [
                entry(3, {
                  oldValue: 1,
                  newValue: 2,
                  source: 'job',
                  jobId: 'job-5',
                  actorType: 'bulkHuman',
                }),
                entry(2, {
                  kind: 'skippedConcurrentEdit',
                  oldValue: 2,
                  newValue: 1,
                  jobId: 'job-6',
                  source: 'job',
                }),
              ],
              nextCursor: 'next',
            };
      },
    } as DocumentHistoryApi;
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    TestBed.overrideComponent(DocumentHistory, {
      set: { providers: [{ provide: DocumentHistoryApi, useValue: fake }] },
    });
    const fixture = TestBed.createComponent(HistoryHost);
    for (let i = 0; i < 6; i++) {
      await new Promise((r) => setTimeout(r, 0));
      await fixture.whenStable();
    }
    const el = fixture.nativeElement as HTMLElement;
    const rowText = [...el.querySelectorAll('tbody tr')].map((r) =>
      r.textContent!.replace(/\s+/g, ' ').trim(),
    );
    expect(rowText[0]).toMatch(/Responsive\s*→\s*changed to\s*Not Responsive/);
    expect(rowText[0]).toContain('Job (Mass Edit or import)');
    expect(rowText[1]).toContain('Not changed');
    expect(el.querySelector('tbody a')?.getAttribute('href')).toBe('/w//jobs/job-5');
    await expectNoAxeViolations(el);

    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Load more'))!.click();
    for (let i = 0; i < 6; i++) {
      await new Promise((r) => setTimeout(r, 0));
      await fixture.whenStable();
    }
    expect(calls.at(-1)).toEqual({ fieldId: null, cursor: 'next' });
    expect(el.querySelectorAll('tbody tr').length).toBe(3);
    expect(el.textContent).toContain('Empty');
  });

  it('formats canonical values in the reviewer’s words', () => {
    expect(valueText([1, 2], field)).toBe('Responsive, Not Responsive');
    expect(valueText(true, undefined)).toBe('Yes');
    expect(valueText(null, field)).toBeNull();
    expect(valueText([], field)).toBeNull();
  });
});
