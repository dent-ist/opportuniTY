import { LiveAnnouncer } from '@angular/cdk/a11y';
import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAppRouting } from '../../app.config';
import { FakeApi, provideFakeApi } from '../../core/api/fake-api.testing';
import { provideOpportunityHttp } from '../../core/api/http';
import { PERMISSIONS } from '../../core/workspace/sections';
import { DialogService } from '../../ui';
import { expectNoAxeViolations } from '../../ui/testing/axe.testing';
import { csvCell, csvFileName, toCsv } from './csv';
import {
  defaultExportForm,
  examplePath,
  rerunRequest,
  splitVolume,
  stepProblems,
  toCreateRequest,
  withPathColumns,
} from './export-model';

const WS = '/api/v1/workspaces/ws-1';
const ME = { userId: 'u-1', displayName: 'Lee Support', email: 'l@example.test', groups: [] };
const SHA = 'f'.repeat(64);

const FIELDS = {
  items: [
    { fieldId: 1, displayName: 'Control Number', queryName: 'controlnumber', isSystem: true },
    { fieldId: 12, displayName: 'Custodian', queryName: 'custodian', isSystem: false },
    {
      fieldId: 13,
      displayName: 'Secret Notes',
      queryName: 'secret',
      isSystem: false,
      isHidden: true,
    },
  ].map((f) => ({
    isHidden: false,
    isSecurityAffecting: false,
    multiValue: false,
    reducedCapabilities: false,
    storage: 'column',
    type: 'text',
    datePrecision: null,
    capabilities: {},
    ...f,
  })),
  nextCursor: null,
};

function snapshot(id: string, count: number, purpose = 'export') {
  return {
    snapshotId: id,
    name: 'Frozen',
    purpose,
    status: 'ready',
    statusReason: null,
    documentCount: count,
    pageCount: count * 3,
    inclusionCounts: { Family: 2 },
    createdAt: '2026-10-10T09:00:00Z',
    selectedAt: '2026-10-10T09:00:00Z',
    source: { kind: 'query' },
  };
}

function exportResource(id: string, status = 'completed') {
  return {
    exportId: id,
    workspaceId: 'ws-1',
    name: 'Responsive set',
    snapshotId: 'snap-9',
    status,
    statusReason: null,
    settings: {
      volumeName: 'VOL001',
      maxFilesPerFolder: 1000,
      includeNatives: true,
      includeText: true,
      includeImages: false,
      textEncoding: 'utf-8',
      pathSeparator: '\\',
      delimiters: 'concordance',
      encoding: 'utf-8',
      fields: [
        { fieldId: 1, column: null, header: 'BEGDOC' },
        { fieldId: null, column: 'familyId', header: 'FamilyID' },
      ],
    },
    report:
      status === 'completed'
        ? {
            documentsExported: 40,
            documentsExcluded: 2,
            natives: 40,
            texts: 40,
            images: 0,
            pages: 0,
            files: 83,
            totalBytes: 2048000,
            manifestSha256: SHA,
          }
        : null,
    job: {
      jobId: 'job-exp-1',
      status: status === 'completed' ? 'completed' : 'running',
      committed: { chunksTotal: 4, chunksCommitted: 2 },
      indexed: { state: 'current' },
    },
    createdBy: 'u-1',
    createdAt: '2026-10-10T10:00:00Z',
    completedAt: status === 'completed' ? '2026-10-10T10:05:00Z' : null,
  };
}

describe('Exports (E12-T08)', () => {
  let api: FakeApi;
  let harness: RouterTestingHarness;

  async function setup(configure: (api: FakeApi) => void = () => undefined) {
    api = new FakeApi()
      .on('GET', '/api/v1/me', { body: ME })
      .on('GET', WS, {
        body: {
          workspaceId: 'ws-1',
          name: 'Acme v. Widget',
          displayTimeZone: 'UTC',
          permissions: [
            PERMISSIONS.documentView,
            PERMISSIONS.searchExecute,
            PERMISSIONS.exportCreate,
            'Export.Download',
          ],
        },
      })
      .on('GET', `${WS}/fields`, { body: FIELDS })
      .on('GET', `${WS}/saved-searches`, {
        body: {
          items: [
            { savedSearchId: 'ss-1', name: 'Responsive', lastHitCount: 40 },
            { savedSearchId: 'ss-2', name: 'Hot docs', lastHitCount: null },
          ],
          nextCursor: null,
        },
      })
      .on('GET', `${WS}/exports`, {
        body: {
          items: [exportResource('exp-1'), exportResource('exp-2', 'running')],
          nextCursor: null,
        },
      })
      .on('GET', `${WS}/exports/exp-1`, { body: exportResource('exp-1') })
      .on('GET', `${WS}/snapshots/snap-9`, { body: snapshot('snap-9', 42) })
      .on('GET', `${WS}/exports/exp-1/exclusions`, {
        body: {
          items: [
            { ordinal: 1, documentId: 'd-7', controlNumber: 'ABC0007', reason: 'AccessChanged' },
          ],
          nextCursor: null,
        },
      })
      .on('GET', `${WS}/exports/exp-1/files`, {
        body: {
          items: [
            { fileId: 'f-1', path: 'MANIFEST.csv', kind: 'manifest', sizeBytes: 900, sha256: SHA },
            { fileId: 'f-2', path: 'EXCLUSIONS.csv', kind: 'report', sizeBytes: 90, sha256: SHA },
            {
              fileId: 'f-3',
              path: 'VOL001/DATA/VOL001.dat',
              kind: 'dat',
              sizeBytes: 5000,
              sha256: SHA,
            },
          ],
          nextCursor: null,
        },
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

  async function settle(rounds = 8): Promise<void> {
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

  it('lists exports with their frozen set ID and document count', async () => {
    await setup();
    await go('/w/ws-1/exports');
    expect(text(root().querySelector('h1')!)).toBe('Exports');
    const rows = root().querySelectorAll('tbody tr');
    expect(rows).toHaveLength(2);
    expect(text(rows[0])).toContain('snap-9');
    expect(text(rows[0])).toContain('42');
    expect(text(rows[1])).toContain('After the run');
    expect(rows[0].querySelector('a')!.getAttribute('href')).toBe('/w/ws-1/exports/exp-1');
    await expectNoAxeViolations(root());
  });

  it('freezes a saved search, walks the steps and starts the export', async () => {
    let posted: HttpRequest<unknown> | null = null;
    await setup((a) =>
      a
        .on('POST', `${WS}/snapshots`, { status: 201, body: snapshot('snap-new', 40) })
        .on('POST', `${WS}/exports`, (req) => {
          posted = req;
          return { status: 202, body: exportResource('exp-new', 'running') };
        })
        .on('GET', `${WS}/exports/exp-new`, { body: exportResource('exp-new', 'running') })
        .on('GET', `${WS}/snapshots/snap-new`, { body: snapshot('snap-new', 40) }),
    );
    await go('/w/ws-1/exports/new?savedSearch=ss-2');
    expect(text(root().querySelector('h1')!)).toBe('New Export');
    const select = root().querySelector<HTMLSelectElement>('select')!;
    expect(select.value).toBe('ss-2');

    // Continue is blocked until the set is frozen.
    button('Continue').click();
    await settle();
    expect(text()).toContain('Freeze the documents to export first.');

    button('Freeze set').click();
    await settle();
    const snapshotBody = api.requests.find((r) => r.url === `${WS}/snapshots`)!;
    expect(snapshotBody.body).toEqual({
      purpose: 'export',
      savedSearchId: 'ss-2',
      expand: { family: true, duplicates: false },
    });
    expect(snapshotBody.headers.get('Idempotency-Key')).toBeTruthy();
    const frozen = root().querySelector('[aria-label="Frozen set"]')!;
    expect(text(frozen)).toContain('snap-new');
    expect(text(frozen)).toContain('40');
    expect(text(frozen)).toContain('2 added by Include');

    button('Continue').click();
    await settle();
    expect(text(root().querySelector('h2')!)).toContain('Volume & folders');
    expect(text(root().querySelector('[aria-label="Example path"]')!)).toContain(
      'VOL001\\NATIVES\\NATIVE0001\\ABC0000001.docx',
    );
    button('Continue').click();
    await settle();
    button('Continue').click();
    await settle();
    // Load file: Control Number first (header ControlNumber), the computed columns, then the path columns.
    const headers = [...root().querySelectorAll<HTMLInputElement>('.dl__columns input')].map(
      (i) => i.value,
    );
    expect(headers).toEqual(['ControlNumber', 'FamilyID', 'ParentID', 'NativePath', 'TextPath']);
    button('Continue').click();
    await settle();
    expect(text()).toContain('snap-new');
    button('Start export').click();
    await settle();

    expect(posted!.body).toEqual({
      snapshotId: 'snap-new',
      includeNatives: true,
      includeText: true,
      includeImages: true,
      textEncoding: 'utf-8',
      pathSeparator: '\\',
      delimiters: 'concordance',
      encoding: 'utf-8',
      volume: { prefix: 'VOL', start: 1, padding: 3, maxFilesPerFolder: 1000 },
      fields: [
        { fieldId: 1, header: 'ControlNumber' },
        { column: 'familyId', header: 'FamilyID' },
        { column: 'parentId', header: 'ParentID' },
        { column: 'nativePath', header: 'NativePath' },
        { column: 'textPath', header: 'TextPath' },
      ],
    });
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/exports/exp-new');
  });

  it('blocks the run on validation errors and offers them as a CSV list', async () => {
    await setup((a) =>
      a
        .on('POST', `${WS}/snapshots`, { status: 201, body: snapshot('snap-new', 3) })
        .on('POST', `${WS}/exports`, {
          status: 400,
          body: {
            title: 'Validation failed',
            status: 400,
            code: 'validation',
            errors: { 'volume.prefix': ['Use 1 to 20 letters.'], 'fields[1]': ['Unknown column.'] },
          },
        }),
    );
    const created = vi.fn(() => 'blob:errors');
    URL.createObjectURL = created;
    URL.revokeObjectURL = vi.fn();
    await go('/w/ws-1/exports/new');
    button('Freeze set').click();
    await settle();
    for (let i = 0; i < 4; i++) {
      button('Continue').click();
      await settle();
    }
    button('Start export').click();
    await settle();
    const alert = root().querySelector('.dl__errors')!;
    expect(text(alert)).toContain('Volume prefix: Use 1 to 20 letters.');
    expect(text(alert)).toContain('DAT column 2: Unknown column.');
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/exports/new');
    button('Download error list (CSV)').click();
    expect(created).toHaveBeenCalledTimes(1);
  });

  it('shows the frozen set, report, exclusions and gateway downloads of one export, and re-runs it', async () => {
    let posted: HttpRequest<unknown> | null = null;
    await setup((a) =>
      a.on('POST', `${WS}/exports`, (req) => {
        posted = req;
        return { status: 202, body: exportResource('exp-3', 'running') };
      }),
    );
    vi.spyOn(TestBed.inject(DialogService), 'confirm').mockResolvedValue(true);
    await go('/w/ws-1/exports/exp-1');
    expect(text(root().querySelector('h1')!)).toBe('Responsive set');
    expect(text()).toContain('snap-9');
    expect(text()).toContain('42 (2 added by Include)');
    expect(text()).toContain(SHA);
    expect(text()).toContain('ABC0007');
    // Every download is a link to the protected-content gateway: each click is authorized and audited again.
    const links = [...root().querySelectorAll<HTMLAnchorElement>('a[download]')].map((a) =>
      a.getAttribute('href'),
    );
    expect(links).toEqual([
      `${WS}/exports/exp-1/package`,
      `${WS}/exports/exp-1/files/f-2/content`,
      `${WS}/exports/exp-1/files/f-1/content`,
      `${WS}/exports/exp-1/files/f-1/content`,
      `${WS}/exports/exp-1/files/f-2/content`,
      `${WS}/exports/exp-1/files/f-3/content`,
    ]);
    // Columns are named by the field catalogue.
    const column = [...root().querySelectorAll('[aria-label="DAT columns"] tbody tr')][0];
    expect([...column.querySelectorAll('td')].map((td) => text(td))).toEqual([
      '1',
      'BEGDOC',
      'Control Number',
    ]);
    await expectNoAxeViolations(root());

    button('Re-run with same settings').click();
    await settle();
    expect(posted!.body).toMatchObject({
      snapshotId: 'snap-9',
      includeImages: false,
      volume: { prefix: 'VOL', start: 1, padding: 3, maxFilesPerFolder: 1000 },
      fields: [
        { fieldId: 1, header: 'BEGDOC' },
        { column: 'familyId', header: 'FamilyID' },
      ],
    });
    expect(TestBed.inject(Router).url).toBe('/w/ws-1/exports/exp-3');
  });

  it('tells someone who did not run the export that only its creator receives the files', async () => {
    await setup((a) =>
      a.on('GET', `${WS}/exports/exp-1/files`, {
        status: 404,
        body: { title: 'Not found', status: 404 },
      }),
    );
    await go('/w/ws-1/exports/exp-1');
    expect(text()).toContain('Only the person who ran this export');
    expect(root().querySelector(`a[href="${WS}/exports/exp-1/package"]`)).toBeNull();
  });
});

describe('export model', () => {
  const fields = [
    { fieldId: 1, name: 'Control Number', queryName: 'controlnumber', isSystem: true },
  ];

  it('keeps the path columns in step with the included files', () => {
    const form = defaultExportForm(fields);
    const noNatives = withPathColumns({ ...form, includeNatives: false });
    expect(noNatives.columns.map((c) => c.key)).not.toContain('column:nativePath');
    expect(withPathColumns({ ...noNatives, includeNatives: true }).columns.at(-1)!.key).toBe(
      'column:nativePath',
    );
  });

  it('shows an example path with the chosen separator and volume', () => {
    const form = { ...defaultExportForm(fields), volumePrefix: 'PROD', volumeStart: '7' };
    expect(examplePath({ ...form, pathSeparator: '/' })).toBe(
      'PROD007/NATIVES/NATIVE0001/ABC0000001.docx',
    );
    expect(
      examplePath({ ...form, includeNatives: false, includeImages: false, includeText: false }),
    ).toBe('PROD007\\DATA\\PROD007.dat');
  });

  it('checks each step before it continues', () => {
    const form = defaultExportForm(fields);
    expect(stepProblems('source', form, false)).toHaveLength(1);
    expect(stepProblems('volume', { ...form, volumePadding: '9' }, true)).toEqual([
      'Volume padding: a whole number from 1 to 6.',
    ]);
    expect(
      stepProblems(
        'loadFile',
        { ...form, columns: form.columns.map((c) => ({ ...c, header: 'X' })) },
        true,
      ),
    ).toEqual(['The header "x" is used twice.']);
  });

  it('re-runs an export with the same frozen set and settings', () => {
    const request = rerunRequest(exportResource('exp-1') as never);
    expect(request.snapshotId).toBe('snap-9');
    expect(request.volume).toEqual({
      prefix: 'VOL',
      start: 1,
      padding: 3,
      maxFilesPerFolder: 1000,
    });
    expect(splitVolume('ACME_VOL0012')).toEqual({ prefix: 'ACME_VOL', start: 12, padding: 4 });
    expect(toCreateRequest('s', defaultExportForm(fields)).fields[0]).toEqual({
      fieldId: 1,
      header: 'ControlNumber',
    });
  });

  it('writes CSV with quoting and neutralised formulas', () => {
    expect(csvCell('=SUM(A1)')).toBe("'=SUM(A1)");
    expect(csvCell('a,"b"')).toBe('"a,""b"""');
    expect(
      toCsv([
        ['a', 1],
        [null, '-2'],
      ]),
    ).toBe("a,1\r\n,'-2\r\n");
    expect(csvFileName(['Acme v. Widget', 'Volume 1'], new Date('2026-10-03T10:42:00Z'))).toBe(
      'Acme-v.-Widget_Volume-1_2026-10-03T1042.csv',
    );
  });
});
