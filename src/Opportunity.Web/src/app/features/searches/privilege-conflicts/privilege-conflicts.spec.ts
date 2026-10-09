import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../../app.config';
import { FakeApi, provideFakeApi } from '../../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../../core/api/http';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { DialogService, ToastService } from '../../../ui';
import { expectNoAxeViolations } from '../../../ui/testing/axe.testing';
import type { ConflictGroup, ConflictMember } from './privilege-conflicts-api';
import {
  defaultSource,
  documentsParams,
  groupTitle,
  propagationMessage,
  summaryText,
} from './privilege-conflicts-model';

const WS = '/api/v1/workspaces/ws-1';
const ALEX = { userId: 'u-1', displayName: 'Alex Privilege' };
const SAM = { userId: 'u-2', displayName: 'Sam Second' };

function value(values: string[], by: typeof ALEX | null = ALEX) {
  return {
    values,
    choiceIds: values.map((_, i) => i + 1),
    changedBy: values.length ? by : null,
    changedAt: values.length ? '2026-10-08T09:30:00Z' : null,
  };
}

function member(id: string, cn: string, status: string[], extra: object = {}) {
  return {
    documentId: id,
    controlNumber: cn,
    familySequence: 0,
    isPrimary: false,
    inProduction: false,
    privilegeStatus: value(status),
    privilegeBasis: value(status.includes('Withhold') ? ['Attorney-Client'] : []),
    responsiveness: null,
    ...extra,
  };
}

const REPORT = {
  generatedAt: '2026-10-08T10:00:00Z',
  responsivenessFieldId: null,
  productionId: null,
  familyConflictCount: '1',
  duplicateConflictCount: '1',
  truncated: false,
  groups: [
    {
      kind: 'family',
      groupId: 'fam-1',
      reasons: ['withheldMember'],
      members: [
        member('d-1', 'ACM0001', ['Withhold']),
        member('d-2', 'ACM0002', ['Not Privileged'], {
          familySequence: 1,
          privilegeStatus: value(['Not Privileged'], SAM),
        }),
      ],
    },
    {
      kind: 'duplicates',
      groupId: 'dup-1',
      reasons: ['privilegeCallsDiffer'],
      members: [
        member('d-5', 'ACM0005', ['Not Privileged'], { isPrimary: true }),
        member('d-6', 'ACM0006', ['Withhold']),
        member('d-7', 'ACM0007', []),
      ],
    },
  ],
};

const FIELDS = [
  { fieldId: 1000, displayName: 'Responsiveness', type: 'singleChoice', storage: 'coding' },
  { fieldId: 37, displayName: 'Privilege Status', type: 'singleChoice', storage: 'coding' },
  { fieldId: 1001, displayName: 'Issues', type: 'multiChoice', storage: 'coding' },
].map((f) => ({
  ...f,
  queryName: f.displayName.toLowerCase(),
  capabilities: {},
  datePrecision: null,
  isHidden: false,
  isSecurityAffecting: false,
  isSystem: false,
  multiValue: false,
  reducedCapabilities: false,
}));

describe('Searches › Privilege Conflicts (E13-T02)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;

  async function setup(
    permissions: string[] = [
      PERMISSIONS.documentView,
      PERMISSIONS.searchExecute,
      PERMISSIONS.privilegeLogGenerate,
      PERMISSIONS.codingBulk,
      PERMISSIONS.codingWritePrivilege,
    ],
    configure: (api: FakeApi) => void = () => undefined,
  ) {
    api = new FakeApi()
      .on('GET', '/api/v1/me', { body: { ...ALEX, email: 'a@example.test', groups: [] } })
      .on('GET', WS, {
        body: { workspaceId: 'ws-1', name: 'Acme v. Widget', displayTimeZone: 'UTC', permissions },
      })
      .on('GET', `${WS}/fields`, { body: { items: FIELDS, nextCursor: null } })
      .on('GET', `${WS}/privilege-conflicts`, { body: REPORT });
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
  const groups = () => [...root().querySelectorAll<HTMLElement>('article.pc__group')];

  it('lists each conflict with values, reviewers and an Open in Documents link', async () => {
    await setup();
    await go('/w/ws-1/searches/privilege-conflicts');
    expect(text(root().querySelector('h1')!)).toBe('Privilege Conflicts');
    expect(text()).toContain('1 family conflict · 1 duplicate conflict');
    expect(text()).toContain('Only documents you can see are checked.');
    // The responsiveness field of the default template is preselected; privilege fields are never offered.
    const select = root().querySelector<HTMLSelectElement>('select')!;
    expect(select.value).toBe('1000');
    expect([...select.options].map((o) => o.text.trim())).toEqual([
      'Do not check responsiveness',
      'Responsiveness',
    ]);
    expect(api.urls()).toContain(`${WS}/privilege-conflicts?responsivenessField=1000`);

    const [family, duplicates] = groups();
    expect(text(family.querySelector('h3')!)).toBe('Family of ACM0001');
    expect(text(family)).toContain('Withheld member');
    expect(text(family)).toContain('Withhold');
    expect(text(family)).toContain('by Alex Privilege, Oct 8, 2026');
    expect(text(family)).toContain('by Sam Second');
    expect(text(family)).toContain('Attachment 1');
    expect(family.querySelector('a.pc__open')?.getAttribute('href')).toBe(
      '/w/ws-1/documents?related=family&relatedId=fam-1&relatedOf=ACM0001',
    );
    expect(text(duplicates.querySelector('h3')!)).toBe('Duplicates of ACM0005');
    expect(text(duplicates)).toContain('Not coded');
    expect(root().querySelector('a[download]')?.getAttribute('href')).toBe(
      `${WS}/privilege-conflicts/export?responsivenessField=1000`,
    );
    await expectNoAxeViolations(root());
  });

  it('propagates the chosen call to the selected duplicate groups as one job', async () => {
    let posted: HttpRequest<unknown> | null = null;
    await setup(undefined, (a) =>
      a.on('POST', `${WS}/privilege-conflicts/propagations`, (req) => {
        posted = req;
        return { status: 202, body: { jobId: 'job-9', jobType: 'bulkCoding' } };
      }),
    );
    const confirm = vi.spyOn(TestBed.inject(DialogService), 'confirm').mockResolvedValue(true);
    const toast = vi.spyOn(TestBed.inject(ToastService), 'show');
    await go('/w/ws-1/searches/privilege-conflicts');

    const duplicates = groups()[1];
    const radios = [...duplicates.querySelectorAll<HTMLInputElement>('input[type="radio"]')];
    // The withheld duplicate is preselected as the call to copy (the primary is Not Privileged).
    expect(radios.map((r) => r.checked)).toEqual([false, true, false]);
    const propagate = [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      text(b).startsWith('Propagate privilege call'),
    )!;
    expect(propagate.disabled).toBe(true);

    radios[0].click();
    duplicates.querySelector<HTMLInputElement>('input[type="checkbox"]')!.click();
    await settle();
    expect(text()).toContain('1 group selected');
    expect(propagate.disabled).toBe(false);
    propagate.click();
    await settle();

    expect(confirm).toHaveBeenCalledOnce();
    expect(posted!.body).toEqual({
      groups: [{ duplicateGroupId: 'dup-1', sourceDocumentId: 'd-5' }],
    });
    expect(posted!.headers.get('Idempotency-Key')).toBeTruthy();
    expect(toast.mock.calls[0][0]).toContain('Propagation started');
    expect(root().querySelector('a[href="/w/ws-1/jobs/job-9"]')).not.toBeNull();
  });

  it('offers no propagation without Coding.WritePrivilege', async () => {
    await setup([
      PERMISSIONS.documentView,
      PERMISSIONS.privilegeLogGenerate,
      PERMISSIONS.codingBulk,
    ]);
    await go('/w/ws-1/searches/privilege-conflicts');
    expect(groups()).toHaveLength(2);
    expect(root().querySelector('input[type="radio"]')).toBeNull();
    expect(text()).not.toContain('Propagate privilege call');
  });

  it('says plainly when there is nothing to fix', async () => {
    await setup(undefined, (a) =>
      a.on('GET', `${WS}/privilege-conflicts`, {
        body: { ...REPORT, familyConflictCount: 0, duplicateConflictCount: 0, groups: [] },
      }),
    );
    await go('/w/ws-1/searches/privilege-conflicts');
    expect(text()).toContain('No privilege conflicts');
    expect(groups()).toHaveLength(0);
  });
});

describe('privilege conflict helpers', () => {
  const m = (
    id: string,
    status: string[],
    extra: Partial<ConflictMember> = {},
  ): ConflictMember => ({
    documentId: id,
    controlNumber: id.toUpperCase(),
    familySequence: 1,
    isPrimary: false,
    inProduction: false,
    status: { values: status, changedBy: null, changedAt: null },
    basis: { values: [], changedBy: null, changedAt: null },
    responsiveness: null,
    ...extra,
  });
  const group = (kind: ConflictGroup['kind'], members: ConflictMember[]): ConflictGroup => ({
    kind,
    groupId: 'g-1',
    reasons: [],
    members,
  });

  it('names a group after its parent or primary, or after its first visible member', () => {
    expect(groupTitle(group('family', [m('a', []), m('b', [], { familySequence: 0 })]))).toBe(
      'Family of B',
    );
    expect(groupTitle(group('family', [m('a', []), m('c', [])]))).toBe('Family with A');
    expect(groupTitle(group('duplicates', [m('a', []), m('b', [], { isPrimary: true })]))).toBe(
      'Duplicates of B',
    );
    expect(documentsParams(group('duplicates', [m('a', [])]))).toEqual({
      related: 'duplicates',
      relatedId: 'g-1',
      relatedOf: 'A',
    });
  });

  it('preselects the primary call when it is a privilege call, else the first one', () => {
    expect(
      defaultSource(
        group('duplicates', [m('a', ['Redact'], { isPrimary: true }), m('b', ['Withhold'])]),
      ),
    ).toBe('a');
    expect(
      defaultSource(
        group('duplicates', [
          m('a', ['Not Privileged'], { isPrimary: true }),
          m('b', ['Withhold']),
        ]),
      ),
    ).toBe('b');
    expect(defaultSource(group('duplicates', [m('a', []), m('b', ['Not Privileged'])]))).toBe('b');
    expect(defaultSource(group('duplicates', [m('a', []), m('b', [])]))).toBeNull();
  });

  it('counts in words', () => {
    expect(summaryText(1, 2, 'en-US')).toBe('1 family conflict · 2 duplicate conflicts');
    expect(propagationMessage(1, 2)).toContain('1 duplicate group (2 other listed documents');
  });
});
