import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../../../app.config';
import { FakeApi, FakeResponse, provideFakeApi } from '../../../../core/api/fake-api.testing';
import type { SearchRequest } from '../../../../core/api/generated/models';
import { provideOpportunityHttp } from '../../../../core/api/http';
import { CommandRegistry } from '../../../../core/commands';
import { PERMISSIONS } from '../../../../core/workspace/sections';
import { ToastService } from '../../../../ui';
import { expectNoAxeViolations } from '../../../../ui/testing/axe.testing';
import { fakePage, hit } from '../../grid/grid-fixtures.testing';
import { documentResource, textChunkResource } from '../viewer/viewer-fixtures.testing';

const WS = '/api/v1/workspaces/ws-1';

const RESPONSIVENESS = {
  fieldId: 1000,
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
  capabilities: { sortable: false, filterable: true },
  choices: [
    { choiceId: 1, name: 'Responsive', isActive: true },
    { choiceId: 2, name: 'Not Responsive', isActive: true },
  ],
};

/** ACM1 is the parent of ACM2 and ACM3; ACM1 and ACM4 are duplicates. */
const HITS = [
  hit(1, { familyId: 'fam-1', isFamilyParent: true }),
  hit(2, { familyId: 'fam-1', parentDocumentId: 'doc-1', familySequence: 1 }),
  hit(3, { familyId: 'fam-1', parentDocumentId: 'doc-1', familySequence: 2 }),
  { ...hit(4), duplicateGroupId: 'dup-1' },
];

function member(n: number, extra: Record<string, unknown> = {}) {
  return {
    documentId: `doc-${n}`,
    controlNumber: `ACM${String(n).padStart(7, '0')}`,
    fileName: `Message ${n}.msg`,
    documentDate: '2024-03-01T14:30:00Z',
    familySequence: n === 1 ? 0 : n - 1,
    isParent: n === 1,
    isPrimary: n === 1,
    isSelf: false,
    coding: { responsiveness: n === 2 ? ['Not Responsive'] : n === 1 ? ['Responsive'] : [] },
    ...extra,
  };
}

function relationships(self: number) {
  const mark = (n: number) => member(n, { isSelf: n === self });
  return {
    documentId: `doc-${self}`,
    family: {
      familyId: 'fam-1',
      parent: mark(1),
      members: [1, 2, 3].map(mark),
      restrictedCount: 1,
    },
    duplicates: {
      duplicateGroupId: 'dup-1',
      primaryDocumentId: 'doc-1',
      members: [1, 4].map(mark),
      restrictedCount: 0,
    },
    thread: { emailThreadId: null, members: [], total: 0, restrictedCount: 0 },
  };
}

describe('Related Items and Apply to Family / Duplicates (E16-T10)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;
  let applyResponse: () => FakeResponse;
  let previewMode: 'interactive' | 'job';

  async function setup(): Promise<void> {
    previewMode = 'interactive';
    applyResponse = () => ({ body: { mode: 'interactive', applied: 2, skipped: 0 } });
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
        body: { items: [RESPONSIVENESS], nextCursor: null, total: { value: 1, relation: 'eq' } },
      })
      .on('POST', `${WS}/searches`, () => ({
        body: { ...fakePage({ total: 4, pageSize: 100 }, 1), items: HITS },
      }))
      .on('POST', `${WS}/coding-propagations/preview`, () => ({
        body: {
          previewId: 'preview-1',
          targetCount: previewMode === 'job' ? 1500 : 2,
          conflictCount: 1,
          conflicts: [
            {
              documentId: 'doc-2',
              controlNumber: 'ACM0000002',
              fieldId: 1000,
              currentValues: ['2'],
              newValues: ['1'],
            },
          ],
          restrictedCount: 1,
          skippedCount: 0,
          mode: previewMode,
          threshold: 1000,
        },
      }))
      .on('POST', `${WS}/coding-propagations`, () => applyResponse());
    for (let n = 1; n <= 4; n++) {
      api
        .on('GET', `${WS}/documents/doc-${n}`, { body: documentResource(n) })
        .on('GET', `${WS}/documents/doc-${n}/text/chunks/0`, {
          body: textChunkResource(`Text of document ${n}`),
        })
        .on('POST', `${WS}/documents/doc-${n}/views`, { status: 204 })
        .on('GET', `${WS}/documents/doc-${n}/relationships`, { body: relationships(n) })
        .on('GET', `${WS}/documents/doc-${n}/coding`, {
          body: {
            documentId: `doc-${n}`,
            documentVersion: '7',
            projectedVersion: '7',
            indexingState: 'indexed',
            layoutId: null,
            lastEditor: null,
            fields: n === 1 ? [{ fieldId: 1000, value: 1, editable: true }] : [],
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
    // Open ACM1 (the first row) in Review mode.
    grid().focus();
    grid().dispatchEvent(
      new KeyboardEvent('keydown', { code: 'Enter', key: 'Enter', bubbles: true }),
    );
    await settle();
  }

  async function settle(ms = 0): Promise<void> {
    for (let i = 0; i < 4; i++) {
      await new Promise((resolve) => setTimeout(resolve, ms));
      await harness.fixture.whenStable();
    }
  }

  async function waitFor(check: () => unknown): Promise<void> {
    for (let i = 0; i < 200 && !check(); i++) await settle(10);
  }

  const root = () => harness.routeNativeElement as HTMLElement;
  const grid = () => root().querySelector<HTMLElement>('[role="grid"]')!;
  const related = () => root().querySelector<HTMLElement>('opp-related-items')!;
  const tableRows = () =>
    [...related().querySelectorAll('tbody tr')].map((r) =>
      [...r.querySelectorAll('td')].map((td) => td.textContent?.replace(/\s+/g, ' ').trim()),
    );
  const tab = (name: string) =>
    [...related().querySelectorAll<HTMLElement>('[role="tab"]')].find((t) =>
      t.textContent?.trim().startsWith(name),
    )!;
  const buttonIn = (el: ParentNode, name: string) =>
    [...el.querySelectorAll<HTMLElement>('button, a')].find(
      (b) => (b.getAttribute('aria-label') ?? b.textContent?.replace(/\s+/g, ' ').trim()) === name,
    )!;
  const dialog = () => document.querySelector<HTMLElement>('[role="dialog"]');
  const bar = () =>
    root().querySelector('.review__bar')!.textContent!.replace(/\s+/g, ' ') +
    root().querySelector('.review__notices')!.textContent!.replace(/\s+/g, ' ');
  const requests = (url: string) =>
    api.requests.filter((r) => r.method === 'POST' && r.url === url) as HttpRequest<
      Record<string, unknown>
    >[];

  it('lists the family with relation, coding status and a restricted count without metadata', async () => {
    await setup();
    const asked = api.requests.find((r) => r.url.endsWith('/doc-1/relationships'))!;
    expect(asked.params.get('fields')).toBe('responsiveness');
    expect(tab('Family').getAttribute('aria-selected')).toBe('true');
    expect(tab('Family').textContent).toContain('3');
    expect(tableRows()).toEqual([
      ['Parent (this document)', 'ACM0000001', 'Message 1.msg', '03/01/2024, 02:30 PM UTC', 'Responsive'],
      ['Attachment 1', 'ACM0000002', 'Message 2.msg', '03/01/2024, 02:30 PM UTC', 'Not Responsive'],
      ['Attachment 2', 'ACM0000003', 'Message 3.msg', '03/01/2024, 02:30 PM UTC', 'Not set'],
    ]);
    expect(related().textContent).toContain('1 restricted item');

    tab('Duplicates').click();
    await settle();
    expect(tableRows().map((r) => r.slice(0, 2))).toEqual([
      ['Primary (this document)', 'ACM0000001'],
      ['Duplicate', 'ACM0000004'],
    ]);
    tab('Email Thread').click();
    await settle();
    expect(related().textContent).toContain('This document is not part of an email thread.');
    await expectNoAxeViolations(root());
  }, 30_000);

  it('opens a member without moving the review cursor', async () => {
    await setup();
    buttonIn(related(), 'ACM0000003').click();
    await settle();
    expect(bar()).toContain('Doc 1 of 4');
    expect(bar()).toContain('Viewing related item ACM0000003');
    expect(root().querySelector('[data-viewer-document]')?.getAttribute('data-viewer-document')).toBe(
      'doc-3',
    );
  }, 30_000);

  it('shows the duplicates in the list from Related Items', async () => {
    await setup();
    tab('Duplicates').click();
    await settle();
    buttonIn(related(), 'Show duplicates in the list').click();
    await settle();
    expect(root().querySelector('opp-review-workspace')).toBeNull();
    const searches = requests(`${WS}/searches`).map((r) => r.body as unknown as SearchRequest);
    expect(searches.at(-1)?.query).toBe('duplicategroup:"dup-1"');
  }, 30_000);

  it('previews the count and conflicts of Apply to Family and applies them on confirmation', async () => {
    await setup();
    buttonIn(root(), 'Apply to Family…').click();
    await waitFor(dialog);
    expect(dialog()?.textContent).toContain('Apply to Family');
    const field = [...dialog()!.querySelectorAll('label')].find((l) =>
      l.textContent?.includes('Responsiveness'),
    )!;
    expect(field.textContent).toContain('Responsive');
    field.querySelector('input')!.click();
    buttonIn(dialog()!, 'Preview').click();
    await settle();
    expect(requests(`${WS}/coding-propagations/preview`)[0].body).toEqual({
      sourceDocumentId: 'doc-1',
      scope: 'family',
      fields: [1000],
    });
    const text = dialog()!.textContent!.replace(/\s+/g, ' ');
    expect(text).toContain('Applies to 2 documents. 1 is already coded differently');
    const conflicts = [...dialog()!.querySelectorAll('tbody tr')].map((r) =>
      [...r.querySelectorAll('td')].map((td) => td.textContent?.trim()),
    );
    expect(conflicts).toEqual([['ACM0000002', 'Responsiveness', 'Not Responsive', 'Responsive']]);
    expect(text).toContain('1 restricted item is not changed.');
    await expectNoAxeViolations(dialog()!);

    buttonIn(dialog()!, 'Apply to 2 documents').click();
    await settle();
    const apply = requests(`${WS}/coding-propagations`)[0];
    expect(apply.body).toEqual({ previewId: 'preview-1' });
    expect(apply.headers.get('Idempotency-Key')).toBeTruthy();
    expect(dialog()).toBeNull();
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toBe(
      'Coding applied to 2 documents.',
    );
    // Related Items reads the new coding.
    expect(api.urls('GET').filter((u) => u.includes('/doc-1/relationships')).length).toBe(2);
  }, 30_000);

  it('saves unsaved edits first, never propagates on save, and offers the saved field ticked', async () => {
    await setup();
    api.on('PUT', `${WS}/documents/doc-1/coding`, {
      body: {
        documentId: 'doc-1',
        documentVersion: '8',
        projectedVersion: '7',
        indexingState: 'pending',
        layoutId: null,
        lastEditor: null,
        fields: [{ fieldId: 1000, value: 2, editable: true }],
      },
      headers: { ETag: '"8"' },
    });
    const notResponsive = [...root().querySelectorAll<HTMLInputElement>('input[type="radio"]')]
      .find((i) => i.closest('label')?.textContent?.includes('Not Responsive'))!;
    notResponsive.click();
    await settle();
    buttonIn(root(), 'Apply to Family…').click();
    await waitFor(dialog);
    expect(api.requests.filter((r) => r.method === 'PUT')).toHaveLength(1);
    expect(requests(`${WS}/coding-propagations/preview`)).toHaveLength(0);
    expect(requests(`${WS}/coding-propagations`)).toHaveLength(0);
    const field = [...dialog()!.querySelectorAll('label')].find((l) =>
      l.textContent?.includes('Responsiveness'),
    )!;
    expect(field.querySelector('input')!.checked).toBe(true);
    expect(field.textContent).toContain('Not Responsive');
  }, 30_000);

  it('runs above the threshold as a job linked to the job monitor, and asks for a new preview when stale', async () => {
    await setup();
    previewMode = 'job';
    TestBed.inject(CommandRegistry).invoke('actions.applyToFamily');
    await waitFor(dialog);
    [...dialog()!.querySelectorAll('label')]
      .find((l) => l.textContent?.includes('Responsiveness'))!
      .querySelector('input')!
      .click();
    buttonIn(dialog()!, 'Preview').click();
    await settle();
    expect(dialog()!.textContent).toContain('this runs as a Mass Edit job');

    applyResponse = () => ({
      status: 409,
      body: { title: 'Conflict', status: 409, code: 'PREVIEW_STALE' },
    });
    buttonIn(dialog()!, 'Start job for 1,500 documents').click();
    await settle();
    expect(dialog()!.textContent).toContain('Preview again before applying.');
    buttonIn(dialog()!, 'Preview again').click();
    await settle();

    applyResponse = () => ({
      status: 202,
      body: {
        mode: 'job',
        job: { jobId: 'job-9', jobType: 'bulkCoding', status: 'running', createdAt: '' },
      },
    });
    buttonIn(dialog()!, 'Start job for 1,500 documents').click();
    await settle();
    expect(dialog()!.textContent).toContain('running as a Mass Edit job');
    expect(buttonIn(dialog()!, 'Open job').getAttribute('href')).toBe('/w/ws-1/jobs/job-9');
  }, 30_000);
});
