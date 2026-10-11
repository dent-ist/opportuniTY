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

const COLUMNS = ['Priv ID', 'Privilege Basis', 'Description', 'Withheld/Redacted'];

const ENTRIES = {
  items: [
    {
      ordinal: 1,
      documentId: 'd-1',
      treatment: 'withheld',
      cells: ['PRIV0001', 'Attorney-Client', 'Advice', 'Withheld'],
    },
    {
      ordinal: 2,
      documentId: 'd-2',
      treatment: 'redacted',
      cells: ['ACM0000007', 'Work Product', '', 'Redacted'],
    },
  ],
  nextCursor: null,
  total: { value: 2, relation: 'eq' },
};

const FIRST_VERSION = {
  logId: 'log-1',
  version: 1,
  previousLogId: null,
  previousVersion: null,
  added: 0,
  removed: 0,
  changed: 0,
  unchanged: 0,
  columnsAdded: [],
  columnsRemoved: [],
  items: [],
  nextCursor: null,
};

function log(version: number, logId = `log-${version}`) {
  return {
    logId,
    version,
    descriptionColumn: 2,
    savedSearchId: null,
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
      columns: COLUMNS,
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

describe('Searches › Privilege Logs (E13-T03, #182)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;

  async function setup(
    permissions: string[] = [
      PERMISSIONS.documentView,
      PERMISSIONS.searchExecute,
      PERMISSIONS.privilegeLogGenerate,
      PERMISSIONS.productionCreate,
      PERMISSIONS.codingWrite,
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
      })
      .on('GET', `${WS}/saved-searches`, {
        body: {
          items: [{ savedSearchId: 'ss-1', name: 'Privileged emails' }],
          nextCursor: null,
        },
      })
      .on('GET', `${WS}/privilege-logs/log-1/entries`, { body: ENTRIES })
      .on('GET', `${WS}/privilege-logs/log-1/changes`, { body: FIRST_VERSION });
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
  const labelled = (label: string) =>
    root().querySelector<HTMLButtonElement>(`button[aria-label="${label}"]`)!;
  const cells = (row: Element) => [...row.querySelectorAll('td')].map((td) => text(td));
  const select = (label: string) => {
    const l = [...root().querySelectorAll<HTMLLabelElement>('label')].find(
      (x) => text(x) === label,
    );
    return l ? root().querySelector<HTMLSelectElement>(`#${l.htmlFor}`) : null;
  };
  const choose = async (label: string, value: string) => {
    const el = select(label)!;
    el.value = value;
    el.dispatchEvent(new Event('change'));
    await settle();
  };

  it('lists versions with SHA-256, exclusion rules and gateway downloads', async () => {
    await setup();
    await go('/w/ws-1/searches/privilege-logs');
    expect(text(root().querySelector('h1')!)).toBe('Privilege Logs');
    expect([...select('Source')!.options].map((o) => o.text.trim())).toEqual([
      'Finalized production',
      'Saved search',
    ]);
    // Only finalized productions can be logged.
    expect([...select('Production')!.options].map((o) => o.text.trim())).toEqual([
      'Volume 1 · ACM0000001 – ACM0000120',
    ]);
    expect([...select('Template')!.options].map((o) => o.text.trim())).toEqual([
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
    await choose('Template', 'template:t-1');
    button('Generate log').click();
    await settle();

    expect(posted!.body).toEqual({ productionId: 'p-1', templateId: 't-1' });
    expect(toast.mock.calls[0][0]).toBe('Version 2 generated: 12 documents listed.');
    expect(root().querySelectorAll('table.pl__table tbody tr')).toHaveLength(2);
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

  it('lists versions without offering production logs to a role that cannot create productions', async () => {
    await setup([PERMISSIONS.documentView, PERMISSIONS.privilegeLogGenerate]);
    await go('/w/ws-1/searches/privilege-logs');
    expect(root().querySelector('select')).toBeNull();
    expect(text()).toContain('needs permission to create productions');
    expect(root().querySelectorAll('table.pl__table tbody tr')).toHaveLength(1);
    expect(api.urls().some((u) => u.startsWith(`${WS}/productions`))).toBe(false);
    // Without Coding.Write the entries are read-only.
    expect(root().querySelector('button[aria-label^="Edit description"]')).toBeNull();
  });

  it('generates a log from a saved search', async () => {
    await setup(undefined, (a) =>
      a.on('POST', `${WS}/privilege-logs`, { status: 200, body: { log: log(1), unchanged: true } }),
    );
    await go('/w/ws-1/searches/privilege-logs');
    await choose('Source', 'savedSearch');
    expect(select('Production')).toBeNull();
    expect([...select('Saved search')!.options].map((o) => o.text.trim())).toEqual([
      'Privileged emails',
    ]);
    button('Generate log').click();
    await settle();
    expect(api.requests.filter((r) => r.method === 'POST')[0].body).toEqual({
      savedSearchId: 'ss-1',
      preset: 'documentByDocument',
    });
  });

  it('lists the entries of the selected version with metadata columns and privilege basis', async () => {
    await setup();
    await go('/w/ws-1/searches/privilege-logs');
    const table = root().querySelector('opp-privilege-log-entries table')!;
    expect([...table.querySelectorAll('thead th')].map((th) => text(th))).toEqual([
      ...COLUMNS,
      'Description actions',
    ]);
    const rows = [...table.querySelectorAll('tbody tr')];
    expect(cells(rows[0])).toEqual([
      'PRIV0001',
      'Attorney-Client',
      'Advice',
      'Withheld',
      'Edit History',
    ]);
    expect(cells(rows[1]).slice(0, 2)).toEqual(['ACM0000007', 'Work Product']);
    expect(text(root().querySelector('opp-privilege-log-changes')!)).toContain(
      'This is the first version of this log',
    );
    await expectNoAxeViolations(root());
  });

  it('edits a description through coding with If-Match, shows its history and offers the next version', async () => {
    let saved: HttpRequest<unknown> | null = null;
    await setup(undefined, (a) =>
      a
        .on('GET', `${WS}/documents/d-1/coding`, {
          body: {
            documentId: 'd-1',
            documentVersion: 7,
            fields: [{ fieldId: 39, value: 'Advice', editable: true }],
          },
        })
        .on('PUT', `${WS}/documents/d-1/coding`, (req) => {
          saved = req;
          return {
            body: {
              documentId: 'd-1',
              documentVersion: 8,
              fields: [
                { fieldId: 39, value: 'Legal advice on the supply agreement', editable: true },
              ],
            },
          };
        })
        .on('GET', `${WS}/documents/d-1/coding-history`, {
          body: {
            items: [
              {
                kind: 'changed',
                occurredAt: '2026-10-10T09:00:00Z',
                actor: { userId: 'u-1', displayName: 'Pat Privilege' },
                oldValue: 'Advice',
                newValue: 'Legal advice on the supply agreement',
              },
            ],
            nextCursor: null,
          },
        }),
    );
    const toast = vi.spyOn(TestBed.inject(ToastService), 'show');
    await go('/w/ws-1/searches/privilege-logs');
    labelled('Edit description of PRIV0001').click();
    await settle();
    const area = root().querySelector<HTMLTextAreaElement>('textarea')!;
    expect(area.getAttribute('aria-label')).toBe('Description of PRIV0001');
    expect(area.value).toBe('Advice');
    area.value = 'Legal advice on the supply agreement';
    area.dispatchEvent(new Event('input'));
    await settle();
    button('Save').click();
    await settle();

    expect(saved!.headers.get('If-Match')).toBe('"7"');
    expect(saved!.headers.get('Idempotency-Key')).toBeTruthy();
    expect(saved!.body).toEqual({
      changes: [{ fieldId: 39, operation: 'set', value: 'Legal advice on the supply agreement' }],
    });
    expect(toast.mock.calls[0][0]).toBe(
      'Description of PRIV0001 saved. It is listed in the next version you generate.',
    );
    const entries = root().querySelector('opp-privilege-log-entries')!;
    expect(cells(entries.querySelector('tbody tr')!)[2]).toBe(
      'Legal advice on the supply agreement Edited · next version',
    );
    expect(text(entries)).toContain('1 description was edited since this version.');

    labelled('Description history of PRIV0001').click();
    await settle();
    const history = entries.querySelector('ol.plt__history')!;
    expect(text(history)).toContain('Pat Privilege');
    expect(text(history)).toContain('Advice → changed to Legal advice on the supply agreement');
    await expectNoAxeViolations(root());
  });

  it('refuses to open the editor for a description the user may not change', async () => {
    await setup(undefined, (a) =>
      a.on('GET', `${WS}/documents/d-2/coding`, {
        body: {
          documentId: 'd-2',
          documentVersion: 3,
          fields: [{ fieldId: 39, value: '', editable: false }],
        },
      }),
    );
    const toast = vi.spyOn(TestBed.inject(ToastService), 'show');
    await go('/w/ws-1/searches/privilege-logs');
    labelled('Edit description of ACM0000007').click();
    await settle();
    expect(root().querySelector('textarea')).toBeNull();
    expect(toast.mock.calls[0][0]).toBe('You cannot edit the description of ACM0000007.');
  });

  it('regenerates the selected log and shows what changed since the previous version', async () => {
    await setup(undefined, (a) =>
      a.on('POST', `${WS}/privilege-logs`, () => {
        a.on('GET', `${WS}/privilege-logs`, {
          body: { items: [log(2), log(1)], nextCursor: null, total: { value: 2, relation: 'eq' } },
        })
          .on('GET', `${WS}/privilege-logs/log-2/entries`, { body: ENTRIES })
          .on('GET', `${WS}/privilege-logs/log-2/changes`, {
            body: {
              ...FIRST_VERSION,
              logId: 'log-2',
              version: 2,
              previousLogId: 'log-1',
              previousVersion: 1,
              added: 1,
              removed: 1,
              changed: 1,
              unchanged: 9,
              items: [
                {
                  kind: 'changed',
                  documentId: 'd-1',
                  ordinal: 1,
                  previousOrdinal: 1,
                  treatment: 'withheld',
                  cells: ['PRIV0001', 'Attorney-Client', 'Legal advice', 'Withheld'],
                  changedCells: [
                    { column: 'Description', before: 'Advice', after: 'Legal advice' },
                  ],
                },
                {
                  kind: 'added',
                  documentId: 'd-3',
                  ordinal: 3,
                  previousOrdinal: null,
                  treatment: 'withheld',
                  cells: ['PRIV0002', 'Work Product', '', 'Withheld'],
                  changedCells: [],
                },
                {
                  kind: 'removed',
                  documentId: 'd-4',
                  ordinal: null,
                  previousOrdinal: 2,
                  treatment: 'redacted',
                  cells: ['ACM0000009', 'Attorney-Client', '', 'Redacted'],
                  changedCells: [],
                },
              ],
            },
          });
        return { status: 201, body: { log: log(2), unchanged: false } };
      }),
    );
    await go('/w/ws-1/searches/privilege-logs');
    button('Generate new version').click();
    await settle();

    expect(api.requests.filter((r) => r.method === 'POST')[0].body).toEqual({
      productionId: 'p-1',
      templateId: 't-1',
    });
    const changes = root().querySelector('opp-privilege-log-changes')!;
    expect(text(changes.querySelector('h4')!)).toBe('Changes since version 1');
    expect(text(changes)).toContain('1 added · 1 removed · 1 changed · 9 unchanged');
    const rows = [...changes.querySelectorAll('tbody tr')].map(cells);
    expect(rows[0]).toEqual([
      'Changed',
      'PRIV0001',
      'Description: Advice → changed to Legal advice',
    ]);
    expect(rows[1]).toEqual(['Added', 'PRIV0002', 'Now listed as Withheld.']);
    expect(rows[2]).toEqual(['Removed', 'ACM0000009', 'No longer listed (was Redacted).']);
    await expectNoAxeViolations(root());
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
