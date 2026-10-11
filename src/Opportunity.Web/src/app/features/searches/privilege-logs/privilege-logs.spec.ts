import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../../app.config';
import { FakeApi, provideFakeApi } from '../../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../../core/api/http';
import { PERMISSIONS } from '../../../core/workspace/sections';
import { ToastService } from '../../../ui';
import { expectNoAxeViolations } from '../../../ui/testing/axe.testing';
import { ruleConditions } from './privilege-logs-api';

const WS = '/api/v1/workspaces/ws-1';
const PAT = { userId: 'u-1', displayName: 'Pat Privilege' };
const SHA = 'a'.repeat(64);

const PRODUCTIONS = {
  items: [
    {
      productionId: 'p-1',
      name: 'Volume 1',
      version: 1,
      status: 'finalized',
      bates: { first: 'ACM0000001', last: 'ACM0000120' },
    },
    {
      productionId: 'p-2',
      name: 'Draft 2',
      version: 1,
      status: 'draft',
      bates: { first: null, last: null },
    },
  ],
  nextCursor: null,
};

const TEMPLATES = {
  items: [
    {
      templateId: 't-1',
      name: 'Supply case log',
      definition: {
        columns: [
          { kind: 'privId', header: 'Priv ID' },
          { kind: 'treatment', header: 'Withheld/Redacted' },
        ],
      },
      modifiedBy: PAT,
      modifiedAt: '2026-10-09T08:00:00Z',
      version: 1,
    },
  ],
  presets: [
    {
      preset: 'documentByDocument',
      name: 'Document-by-document',
      definition: {
        columns: [
          { kind: 'privId', header: 'Priv ID' },
          { kind: 'basis', header: 'Privilege Basis' },
        ],
      },
    },
    {
      preset: 'metadataOnly',
      name: 'Metadata only',
      definition: { columns: [{ kind: 'privId', header: 'Priv ID' }] },
    },
  ],
};

function log(version: number, logId = `log-${version}`) {
  return {
    logId,
    version,
    source: 'production',
    productionId: 'p-1',
    snapshotId: 's-1',
    reviewSetSnapshotId: null,
    templateId: 't-1',
    templateName: 'Supply case log',
    contentSha256: SHA,
    files: [
      { format: 'csv', sha256: 'b'.repeat(64), bytes: 812 },
      { format: 'xlsx', sha256: 'c'.repeat(64), bytes: 4096 },
    ],
    metadata: {
      formatVersion: 1,
      source: 'production',
      productionId: 'p-1',
      productionName: 'Volume 1',
      productionVersion: 1,
      snapshotId: 's-1',
      reviewSetSnapshotId: null,
      templateId: 't-1',
      templateName: 'Supply case log',
      columns: ['Priv ID', 'Withheld/Redacted'],
      privacyRedactionsIncluded: false,
      exclusionRules: [
        {
          label: 'Post-complaint outside counsel',
          dateField: 'Document Date',
          onOrAfter: '2026-01-15',
          before: null,
          logCategories: [],
          attorneysInvolved: [],
          excludedDocuments: 3,
        },
      ],
      entries: 12,
      withheld: 9,
      redacted: 3,
      redactedPrivacy: 0,
      excludedByRules: 3,
    },
    generatedBy: PAT,
    generatedAt: '2026-10-09T10:00:00Z',
  };
}

describe('Searches › Privilege Logs (E13-T03)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;

  async function setup(
    permissions: string[] = [
      PERMISSIONS.documentView,
      PERMISSIONS.searchExecute,
      PERMISSIONS.privilegeLogGenerate,
      PERMISSIONS.productionCreate,
    ],
    configure: (api: FakeApi) => void = () => undefined,
  ) {
    api = new FakeApi()
      .on('GET', '/api/v1/me', { body: { ...PAT, email: 'p@example.test', groups: [] } })
      .on('GET', WS, {
        body: { workspaceId: 'ws-1', name: 'Acme v. Widget', displayTimeZone: 'UTC', permissions },
      })
      .on('GET', `${WS}/productions`, { body: PRODUCTIONS })
      .on('GET', `${WS}/privilege-log-templates`, { body: TEMPLATES })
      .on('GET', `${WS}/privilege-logs`, {
        body: { items: [log(1)], nextCursor: null, total: { value: 1, relation: 'eq' } },
      });
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
    [...root().querySelectorAll<HTMLButtonElement>('button')].find((b) =>
      text(b).startsWith(label),
    )!;

  it('lists versions with SHA-256, exclusion rules and gateway downloads', async () => {
    await setup();
    await go('/w/ws-1/searches/privilege-logs');
    expect(text(root().querySelector('h1')!)).toBe('Privilege Logs');
    const selects = [...root().querySelectorAll<HTMLSelectElement>('select')];
    // Only finalized productions can be logged.
    expect([...selects[0].options].map((o) => o.text.trim())).toEqual([
      'Volume 1 · ACM0000001 – ACM0000120',
    ]);
    expect([...selects[1].options].map((o) => o.text.trim())).toEqual([
      'Document-by-document',
      'Metadata only',
      'Supply case log',
    ]);
    expect(text()).toContain('Columns: Priv ID, Privilege Basis');
    expect(text()).toContain('Only documents you can see are listed or counted.');

    const row = root().querySelector('tbody tr')!;
    expect(text(row)).toContain('Volume 1');
    expect(text(row)).toContain('12 documents');
    expect(text(row)).toContain('9 withheld · 3 redacted');
    expect(text(row)).toContain('a'.repeat(12));
    const detail = root().querySelector('article.pl__detail')!;
    expect(text(detail)).toContain('Post-complaint outside counsel');
    expect(text(detail)).toContain('Document Date on or after 2026-01-15');
    expect(text(detail)).toContain('3 documents excluded');
    expect(text(detail)).toContain(SHA);
    const links = [...detail.querySelectorAll<HTMLAnchorElement>('a[download]')].map((a) =>
      a.getAttribute('href'),
    );
    expect(links).toEqual([
      `${WS}/privilege-logs/log-1/content?format=csv`,
      `${WS}/privilege-logs/log-1/content?format=xlsx`,
    ]);
    await expectNoAxeViolations(root());
  });

  it('generates a version from the chosen production and template', async () => {
    let posted: HttpRequest<unknown> | null = null;
    await setup(undefined, (a) =>
      a.on('POST', `${WS}/privilege-logs`, (req) => {
        posted = req;
        a.on('GET', `${WS}/privilege-logs`, {
          body: { items: [log(2), log(1)], nextCursor: null, total: { value: 2, relation: 'eq' } },
        });
        return { status: 201, body: { log: log(2), unchanged: false } };
      }),
    );
    const toast = vi.spyOn(TestBed.inject(ToastService), 'show');
    await go('/w/ws-1/searches/privilege-logs');
    const template = root().querySelectorAll<HTMLSelectElement>('select')[1];
    template.value = 'template:t-1';
    template.dispatchEvent(new Event('change'));
    await settle();
    button('Generate log').click();
    await settle();

    expect(posted!.body).toEqual({ productionId: 'p-1', templateId: 't-1' });
    expect(toast.mock.calls[0][0]).toBe('Version 2 generated: 12 documents listed.');
    expect(root().querySelectorAll('tbody tr')).toHaveLength(2);
    expect(text(root().querySelector('article.pl__detail h3')!)).toBe('Volume 1 · version 2');
  });

  it('says when nothing changed and no new version was made', async () => {
    await setup(undefined, (a) =>
      a.on('POST', `${WS}/privilege-logs`, { status: 200, body: { log: log(1), unchanged: true } }),
    );
    const toast = vi.spyOn(TestBed.inject(ToastService), 'show');
    await go('/w/ws-1/searches/privilege-logs');
    button('Generate log').click();
    await settle();
    expect(toast.mock.calls[0][0]).toContain('Nothing changed since version 1');
    expect(api.requests.filter((r) => r.method === 'POST')[0].body).toEqual({
      productionId: 'p-1',
      preset: 'documentByDocument',
    });
  });

  it('lists versions without offering generation to a role that cannot create productions', async () => {
    await setup([
      PERMISSIONS.documentView,
      PERMISSIONS.searchExecute,
      PERMISSIONS.privilegeLogGenerate,
    ]);
    await go('/w/ws-1/searches/privilege-logs');
    expect(root().querySelector('select')).toBeNull();
    expect(text()).toContain('needs permission to create productions');
    expect(root().querySelectorAll('tbody tr')).toHaveLength(1);
    expect(api.urls().some((u) => u.startsWith(`${WS}/productions`))).toBe(false);
  });

  it('describes exclusion rule conditions in words', () => {
    expect(
      ruleConditions({
        dateField: 'Document Date',
        onOrAfter: '2026-01-15',
        before: '2026-06-01',
        logCategories: ['Outside counsel'],
        attorneysInvolved: ['Riley Counsel'],
      }),
    ).toBe(
      'Document Date on or after 2026-01-15 before 2026-06-01; Log Category: Outside counsel; Attorneys Involved: Riley Counsel',
    );
    expect(ruleConditions({ logCategories: [], attorneysInvolved: ['A', 'B'] })).toBe(
      'Attorneys Involved: A, B',
    );
  });
});
